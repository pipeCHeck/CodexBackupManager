using System;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Container;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore;

/// <summary>
/// snapshot manifest를 기준으로 원상복구한다(Phase 7 요구사항 18). 존재했던 파일은 원래 바이트로
/// 되돌리고, 존재하지 않았던 파일(이번 Restore가 새로 만든 것)은 삭제한다. 복원 후 각 파일을
/// 다시 해시해 manifest와 비교한다 — 여기서도 실패하면 "복구 완료"라고 말하지 않는다.
/// </summary>
public static class RollbackService
{
    /// <summary>Rollback 결과.</summary>
    /// <param name="Success">모든 파일이 manifest와 정확히 일치하는 상태로 복구됐는지.</param>
    /// <param name="FailureReason">실패했으면 이유(<c>CRITICAL</c> — 수동 개입 필요).</param>
    public sealed record Result(bool Success, string? FailureReason);

    public static Result Rollback(SnapshotManifest manifest, string snapshotDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);

        try
        {
            foreach (SnapshotFileEntry entry in manifest.Files)
            {
                if (entry.ExistedBefore)
                {
                    RestoreOriginal(snapshotDirectory, entry);
                }
                else if (File.Exists(entry.OriginalAbsolutePath))
                {
                    File.Delete(entry.OriginalAbsolutePath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, $"Rollback 파일 복원 중 오류: {ex.GetType().Name}");
        }

        // 복구 후 재검증 — snapshot manifest와 정확히 일치해야 "복구 완료"다.
        foreach (SnapshotFileEntry entry in manifest.Files)
        {
            string? mismatch = VerifyEntryMatchesSnapshot(entry);
            if (mismatch is not null)
            {
                return new Result(false, $"CRITICAL: {mismatch}");
            }
        }

        // 요구사항 5(Phase 07_02) — state DB를 복구했으면(=이번 Restore가 SQL write를 했으면), 복구
        // 후 기본적인 integrity/threads 상태까지 확인한다. 단, 실제 target 파일을 다시 열어서
        // 확인하지 않는다 — 이 프로젝트가 실제 `.codex`에서 ReadOnly SQLite 연결도 WAL index
        // 재구성으로 `-shm`을 다시 건드릴 수 있음을 실측했다(Rollback 검증 자체가 방금 만든 rollback
        // 결과를 또 바꿔버리면 안 된다). 그래서 target을 별도 임시 검증 폴더로 복사한 뒤 그 복사본만
        // 열어 확인하고, 마지막에 target의 DB/WAL/SHM 해시/존재 상태를 한 번 더 재확인한다(integrity
        // 확인이 어떤 경로로든 target에 영향을 줬다면 여기서 잡힌다).
        SnapshotFileEntry? stateDbEntry = manifest.Files.FirstOrDefault(f => f.RelativeLabel == "state-db" && f.ExistedBefore);
        if (stateDbEntry is not null)
        {
            SnapshotFileEntry? walEntry = manifest.Files.FirstOrDefault(f => f.RelativeLabel == "state-db-wal");
            SnapshotFileEntry? shmEntry = manifest.Files.FirstOrDefault(f => f.RelativeLabel == "state-db-shm");

            string? integrityError = CheckStateDbIntegrityViaDisposableCopy(stateDbEntry, walEntry, shmEntry);
            if (integrityError is not null)
            {
                return new Result(false, $"CRITICAL: 복구 후 state DB integrity 확인 실패: {integrityError}");
            }

            foreach (SnapshotFileEntry? entry in new SnapshotFileEntry?[] { stateDbEntry, walEntry, shmEntry })
            {
                if (entry is null)
                {
                    continue;
                }

                string? mismatch = VerifyEntryMatchesSnapshot(entry);
                if (mismatch is not null)
                {
                    return new Result(false, $"CRITICAL: integrity 확인 이후 target 상태가 바뀌었습니다 — {mismatch}");
                }
            }
        }

        return new Result(true, null);
    }

    /// <summary>
    /// <paramref name="entry"/>가 지금 원본 위치에서 snapshot과 정확히 일치하는지 확인한다(존재
    /// 해야 할 파일이 없거나 해시가 다르면, 또는 없어야 할 파일이 여전히 있으면 실패 사유 문자열).
    /// 일치하면 <c>null</c>.
    /// </summary>
    private static string? VerifyEntryMatchesSnapshot(SnapshotFileEntry entry)
    {
        if (entry.ExistedBefore)
        {
            if (!File.Exists(entry.OriginalAbsolutePath))
            {
                return $"복구했어야 할 파일이 없습니다({entry.RelativeLabel}).";
            }

            (long length, string sha256) = HashFile(entry.OriginalAbsolutePath);
            if (length != entry.ByteLength || !string.Equals(sha256, entry.Sha256Hex, StringComparison.Ordinal))
            {
                return $"복구 후 해시가 snapshot과 다릅니다({entry.RelativeLabel}).";
            }
        }
        else if (File.Exists(entry.OriginalAbsolutePath))
        {
            return $"새로 생겼던 파일이 삭제되지 않았습니다({entry.RelativeLabel}).";
        }

        return null;
    }

    /// <summary>
    /// target의 DB(+있으면 WAL/SHM)를 임시 검증 폴더로 복사해 그 복사본만 열어
    /// <c>PRAGMA quick_check</c>와 <c>threads</c> 테이블 조회를 확인한다 — target 자체는 절대 다시
    /// 열지 않는다. 문제가 없으면 <c>null</c>.
    /// </summary>
    private static string? CheckStateDbIntegrityViaDisposableCopy(
        SnapshotFileEntry dbEntry, SnapshotFileEntry? walEntry, SnapshotFileEntry? shmEntry)
    {
        string verifyDir = Path.Combine(Path.GetTempPath(), "CodexBackupManager-RollbackVerify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verifyDir);
        try
        {
            string verifyDbPath = Path.Combine(verifyDir, "verify.sqlite");
            File.Copy(dbEntry.OriginalAbsolutePath, verifyDbPath, overwrite: true);

            if (walEntry is { ExistedBefore: true } && File.Exists(walEntry.OriginalAbsolutePath))
            {
                File.Copy(walEntry.OriginalAbsolutePath, verifyDbPath + "-wal", overwrite: true);
            }

            if (shmEntry is { ExistedBefore: true } && File.Exists(shmEntry.OriginalAbsolutePath))
            {
                File.Copy(shmEntry.OriginalAbsolutePath, verifyDbPath + "-shm", overwrite: true);
            }

            return CheckStateDbIntegrity(verifyDbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"integrity 검증용 복사본 준비 실패: {ex.GetType().Name}";
        }
        finally
        {
            try
            {
                Directory.Delete(verifyDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="stateDbPath"/>를 read-only로 열어 <c>PRAGMA quick_check</c>와 <c>threads</c>
    /// 테이블을 실제로 조회할 수 있는지 확인한다. 문제가 없으면 <c>null</c>. 항상 임시 복사본
    /// 경로로만 호출된다 — 실제 Codex Home의 파일을 직접 열지 않는다.
    /// </summary>
    private static string? CheckStateDbIntegrity(string stateDbPath)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = stateDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                Cache = SqliteCacheMode.Private,
            };

            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();

            using (SqliteCommand quickCheck = connection.CreateCommand())
            {
                quickCheck.CommandText = "PRAGMA quick_check";
                object? result = quickCheck.ExecuteScalar();
                if (result is string text && !string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return $"quick_check 결과: {text}";
                }
            }

            using SqliteCommand countThreads = connection.CreateCommand();
            countThreads.CommandText = "SELECT COUNT(*) FROM threads";
            countThreads.ExecuteScalar();

            return null;
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    private static void RestoreOriginal(string snapshotDirectory, SnapshotFileEntry entry)
    {
        string snapshotFilePath = Path.Combine(snapshotDirectory, entry.SnapshotFileName);

        string? targetDir = Path.GetDirectoryName(entry.OriginalAbsolutePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempPath = entry.OriginalAbsolutePath + ".cbm-rollback-tmp";
        using (FileStream source = new(snapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream dest = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(dest);
        }

        File.Move(tempPath, entry.OriginalAbsolutePath, overwrite: true);
    }

    private static (long Length, string Sha256Hex) HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        StreamingHashCopy.Result result = StreamingHashCopy.HashOnly(stream);
        return (result.ByteLength, result.Sha256Hex);
    }
}
