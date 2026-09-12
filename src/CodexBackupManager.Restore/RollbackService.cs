using System;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Container;

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

        return new Result(true, null);
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
