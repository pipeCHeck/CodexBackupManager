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

    /// <summary>journal을 읽는다. 없거나 손상됐으면 <c>null</c>(존재하지 않는 것과 같이 취급한다).</summary>
    public static RestoreTransactionJournal? TryRead(string snapshotDirectory)
    {
        string path = Path.Combine(snapshotDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RestoreTransactionJournal>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
