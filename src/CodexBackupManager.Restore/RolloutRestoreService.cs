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

    /// <summary>
    /// 기존 파일 끝에 backup entry의 뒷부분을 이어붙인다(plain jsonl fast-forward만).
    /// </summary>
    /// <remarks>
    /// <b>기존 파일에 직접 Seek+CopyTo로 append하지 않는다(Phase 07_02 요구사항 6).</b> 정상 예외라면
    /// <see cref="RestoreExecutor"/>의 Snapshot 기반 Rollback이 처리하지만, 프로세스 강제 종료/정전
    /// 중에는 그 catch/Rollback 자체가 실행되지 않아 원본 rollout이 반쯤 쓰인 상태로 남을 위험이
    /// 있다. 대신 같은 디렉터리의 temp 파일에 (1) 원본을 streaming copy (2) incoming delta를 이어
    /// 붙이고 (3) 전체 길이/해시를 검증하고 (4) <c>Flush(true)</c>로 디스크에 내린 뒤, (5) atomic
    /// move로 원본을 교체한다 — temp 작성 중 크래시가 나도 원본은 전혀 건드리지 않았으므로 안전하다.
    /// </remarks>
    public static void AppendToFile(BackupReader reader, PlannedRolloutAppend planned, IRestoreFaultInjectionHook? faultInjection = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(planned);
        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;

        string targetPath = planned.TargetAbsolutePath;
        string tempPath = targetPath + ".cbm-restore-tmp";

        (long beforeLength, string beforeSha256) = HashFile(targetPath);
        if (beforeLength != planned.ExpectedBeforeLength ||
            !string.Equals(beforeSha256, planned.ExpectedBeforeSha256Hex, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"append 직전 재확인 실패 — 파일이 예상과 다릅니다(thread={Redact(planned.ThreadId)}).");
        }

        try
        {
            using (FileStream original = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            // FileMode.Create(덮어쓰기 허용): 이전 시도가 크래시로 temp를 남겼을 수 있는데, 그 잔재
            // 때문에 재시도 자체가 막히면 안 된다 — temp는 순전히 작업용이라 남아 있어도 버려도 되는
            // 내용이다.
            using (FileStream temp = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                original.CopyTo(temp);

                faultInjection.Check(RestoreFaultInjectionPoint.DuringAppendTempWrite);

                using Stream source = reader.OpenEntry(planned.AppendSourceEntryPath)
                    ?? throw new InvalidDataException($"backup entry를 열 수 없습니다: {planned.ThreadId}");
                SkipBytes(source, planned.AppendSourceSkipBytes);
                source.CopyTo(temp);

                temp.Flush(flushToDisk: true);
            }

            (long tempLength, string tempSha256) = HashFile(tempPath);
            if (tempLength != planned.ExpectedAfterLength ||
                !string.Equals(tempSha256, planned.ExpectedAfterSha256Hex, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"append 결과 검증 실패 — temp 파일이 예상과 다릅니다(thread={Redact(planned.ThreadId)}).");
            }

            faultInjection.Check(RestoreFaultInjectionPoint.BeforeAtomicReplace);
            File.Move(tempPath, targetPath, overwrite: true);
            faultInjection.Check(RestoreFaultInjectionPoint.AfterAtomicReplace);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        (long afterLength, string afterSha256) = HashFile(targetPath);
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
