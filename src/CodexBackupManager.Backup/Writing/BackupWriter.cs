using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CodexBackupManager.Backup.Checksums;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Backup.Validation;

namespace CodexBackupManager.Backup.Writing;

/// <summary>
/// <see cref="ExportPlan"/>을 실제 <c>.codexbackup</c> 파일로 쓴다. 스펙:
/// <c>docs/codexbackup-format-v1.md</c> §3.6~3.7(원본 변경 감지, atomic publish, self validation).
/// </summary>
/// <remarks>
/// <para>
/// <b>스트리밍</b>: 어떤 payload 파일도 <see cref="File.ReadAllBytes(string)"/> 등으로 전체를
/// 메모리에 올리지 않는다(<see cref="StreamingHashCopy"/>가 고정 크기 버퍼로 복사+해시를 겸한다).
/// </para>
/// <para>
/// <b>원본 변경 감지</b>: 각 payload를 열기 직전 <c>(Length, LastWriteTimeUtc)</c>를 스냅샷하고,
/// 복사가 끝난 직후 다시 읽어 비교한다. 다르면(Codex가 실행 중이어서 append/rotate됐을 가능성)
/// 그 자리에서 Export 전체를 실패시키고 temp를 지운다 — "부분적으로 성공"을 성공으로 위장하지 않는다.
/// </para>
/// <para>
/// <b>Atomic publish + self validation</b>: 최종 목적지에 바로 쓰지 않는다. 같은 디렉터리에 temp
/// 파일을 만들고, 다 쓴 뒤 <see cref="BackupReader"/>/<see cref="BackupValidator"/>로 다시 열어
/// 전부 검증한 뒤에만 최종 파일로 옮긴다(<see cref="File.Move(string, string, bool)"/> — 같은
/// 볼륨이라 원자적). 실패/취소/예외 시 temp만 지우고 기존 목적지 파일은 절대 건드리지 않는다.
/// </para>
/// </remarks>
public static class BackupWriter
{
    /// <summary>Export 결과.</summary>
    /// <param name="Success">성공 여부.</param>
    /// <param name="FailureReason">실패 사유(사용자 원문/개인 경로 없이). 성공 시 <c>null</c>.</param>
    /// <param name="Warnings">Manifest에 기록된 경고(성공 시에도 있을 수 있다).</param>
    /// <param name="Validation">self-validation 결과(성공 시 PASS).</param>
    public sealed record WriteResult(
        bool Success,
        string? FailureReason,
        IReadOnlyList<string> Warnings,
        BackupValidationResult? Validation);

    /// <summary>
    /// Export를 실행한다.
    /// </summary>
    /// <param name="plan">Export 계획.</param>
    /// <param name="manifestWithoutChecksums">
    /// <see cref="ManifestBuilder.Build"/>로 만든 manifest(payload entry 경로까지는 확정, 체크섬은
    /// 아직 없음 — 체크섬은 이 메서드가 실제로 복사하면서 계산한다).
    /// </param>
    /// <param name="destinationPath">최종 <c>.codexbackup</c> 경로.</param>
    /// <param name="overwrite">목적지가 이미 있을 때 덮어쓸지. 기본 <c>false</c> — 있으면 시작 전 실패.</param>
    /// <param name="cancellationToken">취소 토큰. 취소되면 temp를 지우고 <see cref="OperationCanceledException"/>을 던진다.</param>
    public static WriteResult Write(
        ExportPlan plan,
        BackupManifest manifestWithoutChecksums,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifestWithoutChecksums);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // 선택한 대화 중 하나라도 완전하게 백업할 수 없으면(§ExportPlan.FatalErrors) 아예 temp 파일도
        // 만들지 않고 즉시 실패시킨다 — "부분 성공"을 성공으로 보여주지 않는다(Phase 05_01).
        if (plan.FatalErrors.Count > 0)
        {
            return new WriteResult(
                false,
                $"선택한 대화 중 일부를 완전하게 백업할 수 없어 Export를 중단했습니다 ({plan.FatalErrors.Count}건).",
                plan.Warnings,
                null);
        }

        if (!overwrite && File.Exists(destinationPath))
        {
            return new WriteResult(false, "대상 파일이 이미 있습니다.", plan.Warnings, null);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (directory is null)
        {
            return new WriteResult(false, "대상 경로가 올바르지 않습니다.", plan.Warnings, null);
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            List<ChecksumEntry> checksumEntries;
            try
            {
                checksumEntries = WriteTempArchive(plan, manifestWithoutChecksums, tempPath, cancellationToken);
            }
            catch (SourceChangedDuringExportException ex)
            {
                return new WriteResult(false, ex.Message, plan.Warnings, null);
            }

            BackupValidationResult validation = BackupValidator.Validate(tempPath, cancellationToken);
            if (!validation.Success)
            {
                return new WriteResult(false, "생성한 백업이 자체 검증을 통과하지 못했습니다.", plan.Warnings, validation);
            }

            File.Move(tempPath, destinationPath, overwrite);
            return new WriteResult(true, null, plan.Warnings, validation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                TryDelete(tempPath);
            }
        }
    }

    private static List<ChecksumEntry> WriteTempArchive(
        ExportPlan plan,
        BackupManifest manifestWithoutChecksums,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var checksumEntries = new List<ChecksumEntry>();

        using (FileStream zipStream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (PlannedPayloadFile file in plan.RolloutFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checksumEntries.Add(CopyPayload(archive, file.SourceFullPath, file.EntryPath, cancellationToken));
            }

            foreach (PlannedAttachment attachment in plan.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checksumEntries.Add(CopyPayload(archive, attachment.SourceFullPath, attachment.EntryPath, cancellationToken));
            }

            // Phase 05_01: manifest.json도 accidental corruption을 탐지할 수 있게 체크섬을 남긴다.
            // 순환은 생기지 않는다 — manifest.json의 바이트는 이 시점에 이미 확정돼 있고(payload
            // entry 경로는 계획 단계에서 정해졌다) checksums.json 쪽만 manifest의 해시를 담을 뿐,
            // manifest.json 자신은 checksums.json 내용을 전혀 참조하지 않는다(단방향).
            // checksums.json 자기 자신은 여전히 해시하지 않는다(그건 진짜 순환이라 불가능하다).
            byte[] manifestBytes = Encoding.UTF8.GetBytes(ManifestJson.Serialize(manifestWithoutChecksums));
            string manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            checksumEntries.Add(new ChecksumEntry("manifest.json", manifestBytes.LongLength, manifestSha256));

            var checksumManifest = new ChecksumManifest { Entries = checksumEntries };
            WriteTextEntry(archive, "checksums.json", ChecksumJson.Serialize(checksumManifest));
            WriteBytesEntry(archive, "manifest.json", manifestBytes);
        }

        return checksumEntries;
    }

    private static ChecksumEntry CopyPayload(
        ZipArchive archive,
        string sourceFullPath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (!ZipEntryPathSafety.IsSafe(entryPath))
        {
            throw new InvalidOperationException($"안전하지 않은 entry 이름을 생성하려 했습니다: {entryPath}");
        }

        var before = new FileInfo(sourceFullPath);
        if (!before.Exists)
        {
            throw new SourceChangedDuringExportException("Export 대상 파일을 열 수 없습니다(삭제되었거나 접근할 수 없음).");
        }

        long lengthBefore = before.Length;
        DateTime writeTimeBefore = before.LastWriteTimeUtc;

        ZipArchiveEntry entry = archive.CreateEntry(entryPath, ChooseCompressionLevel(entryPath));
        StreamingHashCopy.Result result;
        using (FileStream source = new(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (Stream destination = entry.Open())
        {
            result = StreamingHashCopy.CopyWithHash(source, destination, cancellationToken);
        }

        var after = new FileInfo(sourceFullPath);
        if (!after.Exists || after.Length != lengthBefore || after.LastWriteTimeUtc != writeTimeBefore)
        {
            throw new SourceChangedDuringExportException(
                "Export 도중 원본 파일이 변경되어 일관성을 보장할 수 없습니다. Codex가 이 파일을 쓰고 있을 수 있습니다.");
        }

        return new ChecksumEntry(entryPath, result.ByteLength, result.Sha256Hex);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryPath, string content)
        => WriteBytesEntry(archive, entryPath, Encoding.UTF8.GetBytes(content));

    private static void WriteBytesEntry(ZipArchive archive, string entryPath, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using Stream destination = entry.Open();
        destination.Write(bytes, 0, bytes.Length);
    }

    private static CompressionLevel ChooseCompressionLevel(string entryPath)
    {
        string ext = Path.GetExtension(entryPath);
        return ext is ".zst" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Export 도중 원본 payload 파일이 변경된 것을 감지했을 때.</summary>
public sealed class SourceChangedDuringExportException(string message) : Exception(message);
