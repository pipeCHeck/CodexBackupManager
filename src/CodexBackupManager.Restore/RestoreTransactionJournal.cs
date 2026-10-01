using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexBackupManager.Restore;

/// <summary>
/// Apply 진행 상태(Phase 07_02 요구사항 7). Snapshot만으로는 프로세스가 강제 종료됐을 때 "복원
/// 도중 죽었다"는 사실 자체를 다음 실행이 알 방법이 없다 — 이 journal이 그 사실을 남긴다.
/// </summary>
public enum RestoreTransactionState
{
    /// <summary>Snapshot 생성/검증이 끝났다. 아직 mutation을 하나도 하지 않았다.</summary>
    Prepared,

    /// <summary>첫 mutation을 시작했다. 이 상태로 남아 있으면(재시작 후에도) 이전 Apply가 완료되지
    /// 못하고 중단됐다는 뜻이다 — 복구가 필요하다.</summary>
    Applying,

    /// <summary>post-apply validation까지 전부 통과해 정상적으로 끝났다.</summary>
    Completed,

    /// <summary>실패/취소로 Snapshot 기반 Rollback까지 정상적으로 끝났다.</summary>
    RolledBack,

    /// <summary>
    /// (Phase 9_4-03) 되돌리기 Snapshot의 journal: 첫 역연산을 시작했다. 이 상태로 남아 있으면 되돌리기가 중단됐다는 뜻이다 —
    /// <see cref="RestoreTransactionState.Applying"/>과 같이 미완료로 보고, 복구는 이 되돌리기 Snapshot 기준으로 한다.
    /// </summary>
    Undoing,

    /// <summary>(Phase 9_4-03) 가져오기 journal: 그 가져오기를 되돌렸다(종료 상태 — 다시 되돌릴 수 없다).</summary>
    Undone,
}

/// <summary>
/// (Phase 9_4-01) 기록 화면용 가져오기 요약. <b>대화 제목·경로 원문은 넣지 않는다</b>(경로는 Snapshot 안의 되돌리기 기록 파일에만 있다).
/// </summary>
/// <param name="BackupFileName">백업 파일 이름(경로 없음).</param>
/// <param name="AppliedAtUtc">적용 시각.</param>
/// <param name="ImportedCount">새로 가져온 대화 수.</param>
/// <param name="UpdatedCount">이어받은 대화 수.</param>
/// <param name="RelinkedCount">옮긴 대화 수.</param>
/// <param name="CreatedProjectCount">새로 만든 프로젝트 수.</param>
/// <param name="ThreadIds">이 가져오기가 쓴 대화(thread ID, 화면은 현재 카탈로그에서 제목을 찾는다).</param>
/// <param name="CreatedProjectIds">새로 만든 프로젝트 DB ID.</param>
public sealed record ImportRecordSummary(
    string BackupFileName,
    DateTimeOffset AppliedAtUtc,
    int ImportedCount,
    int UpdatedCount,
    int RelinkedCount,
    int CreatedProjectCount,
    IReadOnlyList<string> ThreadIds,
    IReadOnlyList<string> CreatedProjectIds);

/// <summary>
/// 하나의 Apply 시도에 대한 durable transaction marker. Snapshot 디렉터리 안에
/// <c>restore-transaction.json</c>으로 저장된다(<see cref="RestoreTransactionJournalStore"/>).
/// </summary>
/// <param name="SnapshotId">이 journal이 속한 Snapshot의 ID(디렉터리 이름과 같다).</param>
/// <param name="CodexHomePath">대상 Codex Home 경로(진단용 — 원문 그대로, 로그에는 별도 redact 적용).</param>
/// <param name="State">현재 상태.</param>
/// <param name="UpdatedAtUtc">이 상태로 마지막으로 바뀐 시각.</param>
public sealed record RestoreTransactionJournal(
    string SnapshotId,
    string CodexHomePath,
    RestoreTransactionState State,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>(Phase 9_4-01) 성공한 가져오기의 요약(선택 필드 — 없으면 9_4 이전 기록).</summary>
    public ImportRecordSummary? Summary { get; init; }

    /// <summary>
    /// (Phase 9_4-01) 같은 Snapshot 디렉터리의 되돌리기 기록 파일(<c>undo-record.json</c>)의 SHA-256(선택 필드). 없으면 되돌리기를 지원하지 않는다.
    /// 읽을 때 파일 해시가 이 값과 다르면 손상으로 본다.
    /// </summary>
    public string? UndoRecordSha256 { get; init; }

    /// <summary>(Phase 9_4-03) 되돌리기 Snapshot의 journal이면 되돌린 가져오기의 Snapshot ID(선택 필드).</summary>
    public string? UndoOfSnapshotId { get; init; }
}

/// <summary>journal 파일을 읽어본 결과의 분류(Phase 07_03 요구사항 3).</summary>
public enum RestoreTransactionJournalReadStatus
{
    /// <summary>journal 파일 자체가 없다 — journal 개념이 생기기 전의 오래된 정상 Snapshot일 수 있다.</summary>
    Missing,

    /// <summary>정상적으로 읽고 파싱했다.</summary>
    Valid,

    /// <summary>파일은 있지만 읽거나 파싱할 수 없다(손상) — 상태를 알 수 없으므로 보수적으로 취급해야 한다.</summary>
    Corrupt,
}

/// <summary>journal 읽기 결과. <see cref="Status"/>가 <see cref="RestoreTransactionJournalReadStatus.Valid"/>일 때만 <see cref="Journal"/>이 non-null이다.</summary>
public sealed record RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus Status, RestoreTransactionJournal? Journal);

/// <summary>
/// <see cref="RestoreTransactionJournal"/>을 Snapshot 디렉터리 안에 원자적으로 읽고 쓴다(temp
/// write + flush + atomic move — <see cref="SnapshotService"/>의 manifest.json과 같은 패턴).
/// </summary>
public static class RestoreTransactionJournalStore
{
    private const string FileName = "restore-transaction.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>journal을 (덮어)쓴다. temp에 쓰고 flush한 뒤 atomic move한다.</summary>
    public static void Write(string snapshotDirectory, RestoreTransactionJournal journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(journal);

        string path = Path.Combine(snapshotDirectory, FileName);
        string tempPath = path + ".tmp";
        string json = JsonSerializer.Serialize(journal, JsonOptions);

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// journal을 읽는다. 없거나 손상됐으면 <c>null</c> — 두 경우를 구분해야 하면(요구사항 3)
    /// <see cref="TryReadDetailed"/>를 쓴다.
    /// </summary>
    public static RestoreTransactionJournal? TryRead(string snapshotDirectory) => TryReadDetailed(snapshotDirectory).Journal;

    /// <summary>
    /// journal을 읽되, "파일이 없다"와 "파일은 있는데 손상됐다"를 구분해서 돌려준다(요구사항 3 —
    /// 손상된 journal을 마치 없는 것처럼 조용히 무시하면 위험한 상태를 놓칠 수 있다).
    /// </summary>
    public static RestoreTransactionJournalReadResult TryReadDetailed(string snapshotDirectory)
    {
        string path = Path.Combine(snapshotDirectory, FileName);
        if (!File.Exists(path))
        {
            return new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Missing, null);
        }

        try
        {
            string json = File.ReadAllText(path);
            RestoreTransactionJournal? journal = JsonSerializer.Deserialize<RestoreTransactionJournal>(json, JsonOptions);
            return journal is null || !IsStructurallyValid(journal)
                ? new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Corrupt, null)
                : new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Valid, journal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Corrupt, null);
        }
    }

    /// <summary>
    /// Phase 8 release-blocker(#4) — JSON 파싱만 성공했다고 "정상 journal"로 보지 않는다. 예를 들어
    /// <c>{}</c>는 <c>System.Text.Json</c>이 각 필드를 기본값(<c>null</c>/빈 문자열/enum 0/
    /// <c>default(DateTimeOffset)</c>)으로 채워 파싱 자체는 성공하지만, 이걸 <see cref="RestoreTransactionState.Prepared"/>
    /// 상태의 정상 journal로 오판하면 안 된다 — 최소한의 구조적 정합성을 확인한다.
    /// </summary>
    private static bool IsStructurallyValid(RestoreTransactionJournal journal)
        => !string.IsNullOrWhiteSpace(journal.SnapshotId)
        && !string.IsNullOrWhiteSpace(journal.CodexHomePath)
        && Enum.IsDefined(journal.State)
        && journal.UpdatedAtUtc != default
        // Phase 9_4-01 — 새 필드는 선택이다. 있으면 그 구조도 맞아야 한다(있는데 깨졌으면 손상으로 본다).
        && (journal.Summary is null || IsSummaryValid(journal.Summary))
        && (journal.UndoRecordSha256 is null || IsSha256Hex(journal.UndoRecordSha256))
        && (journal.UndoOfSnapshotId is null || !string.IsNullOrWhiteSpace(journal.UndoOfSnapshotId));

    private static bool IsSummaryValid(ImportRecordSummary summary)
        => !string.IsNullOrWhiteSpace(summary.BackupFileName)
        && summary.AppliedAtUtc != default
        && summary.ImportedCount >= 0 && summary.UpdatedCount >= 0 && summary.RelinkedCount >= 0 && summary.CreatedProjectCount >= 0
        && summary.ThreadIds is not null && summary.CreatedProjectIds is not null;

    private static bool IsSha256Hex(string value)
        => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
