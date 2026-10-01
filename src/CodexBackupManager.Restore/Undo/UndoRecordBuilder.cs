using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex.Locating;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore.Undo;

/// <summary>
/// (Phase 9_4-01) 성공한 Apply가 쓴 것을 역연산 항목 목록(<see cref="UndoRecord"/>)과 요약(<see cref="ImportRecordSummary"/>)으로 만든다.
/// Apply 사후 검증을 통과한 직후(Codex 종료 상태, 프로세스 락 안) <see cref="RestoreExecutor"/>가 부른다.
/// </summary>
/// <remarks>
/// UPDATE한 컬럼의 이전 값은 그 Apply의 Snapshot DB 사본(임시 폴더로 복사한 것)에서 읽는다 — live DB의 Snapshot 파일을 직접 열지 않는다.
/// 이후 값과 행 fingerprint는 live DB를 읽기 전용으로 연다. 이 클래스는 Codex Home에 쓰지 않는다(Snapshot 디렉터리에 기록 파일만 쓴다).
/// </remarks>
internal static class UndoRecordBuilder
{
    /// <summary>Desktop이 켜지기만 해도 바꿔 되돌리기 전 확인에서 빼는 컬럼.</summary>
    internal static readonly IReadOnlySet<string> DesktopVolatileColumns = new HashSet<string>(StringComparer.Ordinal) { "updated_at", "updated_at_ms" };

    /// <summary>기록을 만들어 Snapshot 디렉터리에 쓰고 (요약, 기록 파일 해시)를 돌려준다.</summary>
    public static (ImportRecordSummary Summary, string RecordSha256) BuildAndWrite(
        string codexHomePath,
        ImportPlan plan,
        RestoreOperationPlan executedPlan,
        IReadOnlySet<string> reusedProjectIds,
        GlobalStateWriteResult? globalState,
        SnapshotManifest snapshot,
        string snapshotDirectory)
    {
        string stateDbPath = Path.Combine(codexHomePath, CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath)[0]);

        var inserted = new List<UndoInsertedThread>();
        var updated = new List<UndoUpdatedThread>();
        using (SqliteConnection live = OpenReadOnly(stateDbPath))
        {
            IReadOnlyList<string> fingerprintColumns = ThreadRowFingerprint.AvailableColumns(live);
            foreach (PlannedThreadInsert insert in executedPlan.ThreadInserts)
            {
                IReadOnlyList<UndoColumnValue> values = ThreadRowFingerprint.ReadValues(live, insert.Source.ThreadId, fingerprintColumns)
                    ?? throw new InvalidOperationException("가져온 행을 다시 읽을 수 없습니다.");
                inserted.Add(new UndoInsertedThread(insert.Source.ThreadId, fingerprintColumns, ThreadRowFingerprint.Compute(values)));
            }

            Dictionary<string, List<string>> updatedColumns = UpdatedColumns(executedPlan);
            if (updatedColumns.Count > 0)
            {
                using SnapshotDatabaseCopy copy = SnapshotDatabaseCopy.Open(snapshot, snapshotDirectory);
                foreach ((string threadId, List<string> columns) in updatedColumns)
                {
                    IReadOnlyList<UndoColumnValue> before = ThreadRowFingerprint.ReadValues(copy.Connection, threadId, columns)
                        ?? throw new InvalidOperationException("이어받은 행의 이전 값을 Snapshot에서 읽을 수 없습니다.");
                    List<string> checkedColumns = columns.Where(c => !DesktopVolatileColumns.Contains(c)).ToList();
                    IReadOnlyList<UndoColumnValue> after = ThreadRowFingerprint.ReadValues(live, threadId, checkedColumns)
                        ?? throw new InvalidOperationException("이어받은 행을 다시 읽을 수 없습니다.");
                    updated.Add(new UndoUpdatedThread(threadId, before, checkedColumns, ThreadRowFingerprint.Compute(after)));
                }
            }
        }

        var newFiles = executedPlan.NewRolloutFiles
            .Select(f => new UndoNewRolloutFile(f.OwningThreadId, f.TargetAbsolutePath, f.ExpectedLength, f.ExpectedSha256Hex))
            .ToList();
        var appended = executedPlan.RolloutAppends
            .Select(a => new UndoAppendedRollout(a.ThreadId, a.TargetAbsolutePath, a.ExpectedBeforeLength, a.ExpectedBeforeSha256Hex, a.ExpectedAfterLength, a.ExpectedAfterSha256Hex))
            .ToList();
        var links = executedPlan.ThreadProjectLinks
            .Select(l => new UndoThreadLink(l.ThreadId, l.ExpectedProjectId, l.ExpectedCwd, l.NewProjectId, l.NewCwd))
            .ToList();
        var projects = executedPlan.ProjectCreates
            .Where(c => !reusedProjectIds.Contains(c.NewProjectId))
            .Select(c => new UndoCreatedProject(c.NewProjectId, c.Name, [c.RootPathDisplay], c.IdempotencyKey))
            .ToList();
        var entries = (globalState?.Added ?? [])
            .Select(a => new UndoGlobalStateEntry(a.LegacyProjectId, a.DbProjectId, a.Addition.Name, a.Addition.RootPaths, a.Addition.TimestampMs))
            .ToList();

        List<string> tracedThreads = inserted.Select(i => i.ThreadId).Concat(updated.Select(u => u.ThreadId))
            .Concat(appended.Select(a => a.ThreadId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<UndoTraceBaseline> baselines = CodexTraceInspector.Count(codexHomePath, tracedThreads).Values.ToList();

        var record = new UndoRecord(
            UndoRecord.CurrentVersion, snapshot.SnapshotId, newFiles, appended, inserted, updated, links, projects, entries, baselines);
        string sha = UndoRecordStore.Write(snapshotDirectory, record);

        List<string> threadIds = inserted.Select(i => i.ThreadId).Concat(updated.Select(u => u.ThreadId)).Concat(links.Select(l => l.ThreadId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = new ImportRecordSummary(
            Path.GetFileName(plan.Backup.BackupFilePath),
            DateTimeOffset.UtcNow,
            executedPlan.ThreadInserts.Count(i => i.Source.IsSelected),
            updated.Select(u => u.ThreadId).Concat(appended.Select(a => a.ThreadId)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            links.Count,
            projects.Count,
            threadIds,
            projects.Select(p => p.DbProjectId).ToList());
        return (summary, sha);
    }

    /// <summary>이 Apply가 UPDATE한 기존 행의 컬럼(rollout_path, 값이 있던 메타데이터 컬럼).</summary>
    private static Dictionary<string, List<string>> UpdatedColumns(RestoreOperationPlan executedPlan)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string> For(string id) => map.TryGetValue(id, out List<string>? list) ? list : map[id] = [];

        foreach (PlannedThreadRolloutPathUpdate update in executedPlan.ThreadRolloutPathUpdates)
        {
            For(update.ThreadId).Add("rollout_path");
        }

        foreach (PlannedThreadMetadataUpdate update in executedPlan.ThreadMetadataUpdates)
        {
            List<string> columns = For(update.ThreadId);
            void Add(string column, object? value)
            {
                if (value is not null && !columns.Contains(column))
                {
                    columns.Add(column);
                }
            }

            Add("updated_at", update.UpdatedAtSeconds);
            Add("updated_at_ms", update.UpdatedAtMs);
            Add("tokens_used", update.TokensUsed);
            Add("has_user_event", update.HasUserEvent);
            Add("name", update.NameIfLocalMissing);
            Add("model", update.ModelIfLocalMissing);
            Add("cli_version", update.CliVersionIfLocalMissing);
        }

        return map.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    internal static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>Snapshot의 state DB(+WAL/SHM) 사본을 임시 폴더에 복사해 읽기 전용으로 연다(Snapshot 파일 자체는 열지 않는다).</summary>
internal sealed class SnapshotDatabaseCopy : IDisposable
{
    private readonly string _directory;

    private SnapshotDatabaseCopy(string directory, SqliteConnection connection)
    {
        _directory = directory;
        Connection = connection;
    }

    public SqliteConnection Connection { get; }

    public static SnapshotDatabaseCopy Open(SnapshotManifest manifest, string snapshotDirectory)
    {
        SnapshotFileEntry db = manifest.Files.FirstOrDefault(f => f.RelativeLabel == "state-db" && f.ExistedBefore)
            ?? throw new InvalidOperationException("Snapshot에 state DB가 없습니다.");
        string directory = Path.Combine(Path.GetTempPath(), "CodexBackupManager-UndoRead", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "snapshot.sqlite");
        File.Copy(Path.Combine(snapshotDirectory, db.SnapshotFileName), path);
        foreach ((string label, string suffix) in new[] { ("state-db-wal", "-wal"), ("state-db-shm", "-shm") })
        {
            if (manifest.Files.FirstOrDefault(f => f.RelativeLabel == label && f.ExistedBefore) is { } sidecar)
            {
                File.Copy(Path.Combine(snapshotDirectory, sidecar.SnapshotFileName), path + suffix);
            }
        }

        return new SnapshotDatabaseCopy(directory, UndoRecordBuilder.OpenReadOnly(path));
    }

    public void Dispose()
    {
        Connection.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
