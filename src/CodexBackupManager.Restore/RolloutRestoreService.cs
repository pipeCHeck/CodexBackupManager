using System;
using System.IO;
using System.Security.Cryptography;
using CodexBackupManager.Backup.Reading;

namespace CodexBackupManager.Restore;

/// <summary>
/// backup entry로부터 로컬 rollout 파일을 실제로 만들거나 이어붙인다(Phase 7 요구사항 9/12/15).
/// </summary>
/// <remarks>
/// 새 파일은 항상 <b>temp에 쓰고 검증한 뒤 atomic move</b>한다(요구사항 15). 기존 파일에 이어붙일
/// 때는 append 직전 원본 길이/해시를 한 번 더 확인하고, append 직후 전체 길이/해시를 즉시 다시
/// 확인한다 — 기대와 다르면 예외를 던져 <see cref="RestoreExecutor"/>가 Rollback하게 한다.
/// </remarks>
public static class RolloutRestoreService
{
    /// <summary>backup entry 바이트를 그대로 복사해 새 rollout 파일을 만든다(New/새 segment).</summary>
    public static void CreateNewFile(BackupReader reader, PlannedNewRolloutFile planned)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(planned);

        string? targetDir = Path.GetDirectoryName(planned.TargetAbsolutePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempPath = planned.TargetAbsolutePath + ".cbm-restore-tmp";
        using (Stream source = reader.OpenEntry(planned.SourceEntryPath)
            ?? throw new InvalidDataException($"backup entry를 열 수 없습니다: {planned.OwningThreadId}"))
        using (FileStream dest = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(dest);
        }

        (long length, string sha256) = HashFile(tempPath);
        if (length != planned.ExpectedLength || !string.Equals(sha256, planned.ExpectedSha256Hex, StringComparison.Ordinal))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException($"새 rollout 파일 검증 실패(thread={Redact(planned.OwningThreadId)}).");
        }

        File.Move(tempPath, planned.TargetAbsolutePath, overwrite: false);
    }

    /// <summary>기존 파일 끝에 backup entry의 뒷부분을 이어붙인다(plain jsonl fast-forward만).</summary>
    public static void AppendToFile(BackupReader reader, PlannedRolloutAppend planned)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(planned);

        (long beforeLength, string beforeSha256) = HashFile(planned.TargetAbsolutePath);
        if (beforeLength != planned.ExpectedBeforeLength ||
            !string.Equals(beforeSha256, planned.ExpectedBeforeSha256Hex, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"append 직전 재확인 실패 — 파일이 예상과 다릅니다(thread={Redact(planned.ThreadId)}).");
        }

        using (Stream source = reader.OpenEntry(planned.AppendSourceEntryPath)
            ?? throw new InvalidDataException($"backup entry를 열 수 없습니다: {planned.ThreadId}"))
        {
            SkipBytes(source, planned.AppendSourceSkipBytes);

            using FileStream dest = new(planned.TargetAbsolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            dest.Seek(0, SeekOrigin.End);
            source.CopyTo(dest);
        }

        (long afterLength, string afterSha256) = HashFile(planned.TargetAbsolutePath);
        if (afterLength != planned.ExpectedAfterLength ||
            !string.Equals(afterSha256, planned.ExpectedAfterSha256Hex, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"append 직후 검증 실패 — 결과가 예상과 다릅니다(thread={Redact(planned.ThreadId)}).");
        }
    }

    private static void SkipBytes(Stream stream, long count)
    {
        byte[] buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new InvalidOperationException("backup entry가 예상보다 짧습니다.");
            }

            remaining -= read;
        }
    }

    private static (long Length, string Sha256Hex) HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string Redact(string threadId)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(threadId)))[..12];
}
