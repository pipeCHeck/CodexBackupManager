using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 07_02 요구사항 5 — <see cref="RollbackService"/>의 post-rollback integrity 확인이 실제
/// target DB/WAL/SHM을 다시 열지 않고, target의 DB/WAL/SHM 상태가 Rollback 직후 snapshot과
/// byte-for-byte 정확히 같이 남아 있는지를 직접 검증한다. 실제 사용자 <c>.codex</c>는 이 테스트
/// 전체에서 전혀 관여하지 않는다 — 전부 temp 디렉터리의 합성 SQLite 파일이다.
/// </summary>
public sealed class RollbackServiceShmSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-rollback-shm-tests", Guid.NewGuid().ToString("N"));

    public RollbackServiceShmSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// WAL 모드로 실제 row 하나를 커밋하되 checkpoint가 일어나지 않게 한다 — 쓰기를 한 그 연결을
    /// <b>닫지 않고</b> 반환한다(그 연결이 닫히는 순간 SQLite가 "마지막 연결"로 보고 자동
    /// checkpoint해 WAL을 비울 수 있다). 호출자는 이 연결이 아직 열려 있는 동안 원본
    /// <c>-wal</c>/<c>-shm</c> 바이트를 전부 읽은 뒤에만 닫아야 한다.
    /// </summary>
    private static SqliteConnection CreateDbWithPendingWal(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, note TEXT);";
            create.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO threads (id, note) VALUES ('t1', 'hello');";
            insert.ExecuteNonQuery();
        }

        return connection;
    }

    private static void MutateTarget(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO threads (id, note) VALUES ('t2', 'mutated-by-apply');";
        insert.ExecuteNonQuery();
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// <c>File.ReadAllBytes</c>는 아직 열려 있는 SQLite 연결(WAL 모드)과 공유 모드가 맞지 않아
    /// <see cref="IOException"/>이 날 수 있다 — 이 테스트는 원본 바이트를 <b>연결이 열려 있는
    /// 동안</b> 캡처해야 하므로, SQLite의 Win32 VFS와 같은 관대한 공유 모드로 직접 연다.
    /// </summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Theory]
    [InlineData(true, true)]   // WAL 있음 + SHM 있음
    [InlineData(true, false)]  // WAL 있음 + SHM 없음
    [InlineData(false, false)] // 둘 다 없음
    public void Rollback_이후_target_DB_WAL_SHM은_snapshot과_byte_for_byte_동일하다(bool walExisted, bool shmExisted)
    {
        string targetDir = Path.Combine(_root, $"target-{walExisted}-{shmExisted}");
        Directory.CreateDirectory(targetDir);
        string targetDb = Path.Combine(targetDir, "state_5.sqlite");
        string targetWal = targetDb + "-wal";
        string targetShm = targetDb + "-shm";

        SqliteConnection? keepAlive = walExisted ? CreateDbWithPendingWal(targetDb) : null;
        if (keepAlive is null)
        {
            // walExisted=false 케이스는 non-trivial WAL을 남길 필요가 없다 — 평범하게 연결 하나로
            // db를 만들고 닫으면(마지막 연결이 닫히며) SQLite가 자동으로 checkpoint해 WAL/SHM이
            // 남지 않는 "이미 checkpoint된 DB" 상태가 자연스럽게 재현된다.
            var builder = new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, note TEXT);";
            create.ExecuteNonQuery();
        }

        if (walExisted)
        {
            Assert.True(
                File.Exists(targetWal),
                "이 SQLite 버전이 기대와 달리 커밋 즉시 checkpoint한 것 같습니다 — 테스트 전제가 깨졌습니다. " +
                "실제 디렉터리 내용: " + string.Join(", ", Directory.GetFiles(targetDir)));
        }
        else
        {
            Assert.False(File.Exists(targetWal));
        }

        // "snapshot"으로 쓸 원본 바이트를 Rollback 호출보다 먼저 기록해 둔다. keepAlive 연결은 이
        // 시점까지만 필요하다(WAL이 checkpoint되어 비워지기 전에 실제 내용을 캡처하기 위해서) —
        // 캡처가 끝나면 곧바로 닫는다.
        byte[] originalDbBytes = ReadAllBytesShared(targetDb);
        byte[]? originalWalBytes = walExisted ? ReadAllBytesShared(targetWal) : null;
        byte[]? originalShmBytes = shmExisted ? ReadAllBytesShared(targetShm) : null;
        keepAlive?.Dispose();

        if (!shmExisted && File.Exists(targetShm))
        {
            File.Delete(targetShm);
        }

        string snapshotDir = Path.Combine(_root, $"snapshot-{walExisted}-{shmExisted}");
        Directory.CreateDirectory(snapshotDir);
        File.WriteAllBytes(Path.Combine(snapshotDir, "0001.bin"), originalDbBytes);

        var entries = new List<SnapshotFileEntry>
        {
            new("state-db", targetDb, "0001.bin", true, originalDbBytes.LongLength, Sha256Of(originalDbBytes)),
        };

        if (originalWalBytes is not null)
        {
            File.WriteAllBytes(Path.Combine(snapshotDir, "0002.bin"), originalWalBytes);
            entries.Add(new("state-db-wal", targetWal, "0002.bin", true, originalWalBytes.LongLength, Sha256Of(originalWalBytes)));
        }
        else
        {
            entries.Add(new("state-db-wal", targetWal, "0002.bin", false, 0, null));
        }

        if (originalShmBytes is not null)
        {
            File.WriteAllBytes(Path.Combine(snapshotDir, "0003.bin"), originalShmBytes);
            entries.Add(new("state-db-shm", targetShm, "0003.bin", true, originalShmBytes.LongLength, Sha256Of(originalShmBytes)));
        }
        else
        {
            entries.Add(new("state-db-shm", targetShm, "0003.bin", false, 0, null));
        }

        var manifest = new SnapshotManifest("test-snapshot", DateTimeOffset.UtcNow, targetDir, "deadbeef", entries);

        // Apply가 이미 target을 mutate한 상태를 재현한다(DB에 한 줄 더 써서 WAL/SHM도 다시 갱신됨).
        MutateTarget(targetDb);

        RollbackService.Result result = RollbackService.Rollback(manifest, snapshotDir);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(originalDbBytes, File.ReadAllBytes(targetDb));
        Assert.Equal(walExisted, File.Exists(targetWal));
        if (walExisted)
        {
            Assert.Equal(originalWalBytes, File.ReadAllBytes(targetWal));
        }

        Assert.Equal(shmExisted, File.Exists(targetShm));
        if (shmExisted)
        {
            Assert.Equal(originalShmBytes, File.ReadAllBytes(targetShm));
        }
    }
}
