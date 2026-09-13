using System;
using System.IO;
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
}

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
    DateTimeOffset UpdatedAtUtc);

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
            return journal is null
                ? new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Corrupt, null)
                : new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Valid, journal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new RestoreTransactionJournalReadResult(RestoreTransactionJournalReadStatus.Corrupt, null);
        }
    }
}
