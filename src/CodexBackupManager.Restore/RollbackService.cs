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
            if (entry.ExistedBefore)
            {
                if (!File.Exists(entry.OriginalAbsolutePath))
                {
                    return new Result(false, $"CRITICAL: 복구했어야 할 파일이 없습니다({entry.RelativeLabel}).");
                }

                (long length, string sha256) = HashFile(entry.OriginalAbsolutePath);
                if (length != entry.ByteLength || !string.Equals(sha256, entry.Sha256Hex, StringComparison.Ordinal))
                {
                    return new Result(false, $"CRITICAL: 복구 후 해시가 snapshot과 다릅니다({entry.RelativeLabel}).");
                }
            }
            else if (File.Exists(entry.OriginalAbsolutePath))
            {
                return new Result(false, $"CRITICAL: 새로 생겼던 파일이 삭제되지 않았습니다({entry.RelativeLabel}).");
            }
        }

        // 요구사항 6 — state DB를 복구했으면(=이번 Restore가 SQL write를 했으면), 복구 후 다시
        // read-only로 열어 기본적인 integrity/threads 상태까지 확인한다. byte-level hash가 이미
        // snapshot과 정확히 같음을 위에서 확인했으므로 이 결과는 "복구가 됐다"의 재확인일 뿐이지만,
        // 파일은 정확한데 어떤 이유로든 SQLite 자체가 손상된 상태로 열리는 경우까지 방어한다.
        SnapshotFileEntry? stateDbEntry = manifest.Files.FirstOrDefault(f => f.RelativeLabel == "state-db" && f.ExistedBefore);
        if (stateDbEntry is not null)
        {
            string? integrityError = CheckStateDbIntegrity(stateDbEntry.OriginalAbsolutePath);
            if (integrityError is not null)
            {
                return new Result(false, $"CRITICAL: 복구 후 state DB integrity 확인 실패: {integrityError}");
            }
        }

        return new Result(true, null);
    }

    /// <summary>
    /// 복구된 state DB를 read-only로 열어 <c>PRAGMA quick_check</c>와 <c>threads</c> 테이블을 실제로
    /// 조회할 수 있는지 확인한다. 문제가 없으면 <c>null</c>.
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
