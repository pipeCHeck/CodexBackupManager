using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CodexBackupManager.Backup.Checksums;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Reading;
using ZstdSharp;

namespace CodexBackupManager.Backup.Validation;

/// <summary>Self-validation 결과. 스펙: <c>docs/codexbackup-format-v1.md</c> §3.7, 요구사항 13.</summary>
/// <param name="Success">전부 통과했는지.</param>
/// <param name="Errors">실패 사유 목록(성공이면 빈 목록).</param>
public sealed record BackupValidationResult(bool Success, IReadOnlyList<string> Errors)
{
    /// <summary>성공 결과를 만든다.</summary>
    public static BackupValidationResult Pass() => new(true, []);

    /// <summary>실패 결과를 만든다.</summary>
    public static BackupValidationResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// <c>.codexbackup</c> 파일이 실제로 온전한지 검증한다. <see cref="Writing.BackupWriter"/>가 temp
/// 파일을 최종 파일로 옮기기 전에 반드시 이 검증을 통과해야 한다(자기 자신을 다시 열어 검증 —
/// "Writer가 파일을 만들었다고 성공으로 끝내지 않는다").
/// </summary>
public static class BackupValidator
{
    /// <summary>파일 경로를 검증한다.</summary>
    public static BackupValidationResult Validate(string path, CancellationToken cancellationToken = default)
    {
        BackupReader reader;
        try
        {
            reader = BackupReader.Open(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return BackupValidationResult.Fail($"ZIP 파일을 열 수 없습니다: {ex.GetType().Name}");
        }

        using (reader)
        {
            return Validate(reader, cancellationToken);
        }
    }

    /// <summary>이미 열린 리더로 검증한다.</summary>
    public static BackupValidationResult Validate(BackupReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var errors = new List<string>();

        // ── 1) entry 이름 자체의 안전성(중복, path traversal) ──────────────────────────
        List<string> entryNames = reader.EntryNames.ToList();
        var duplicates = entryNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            errors.Add($"중복된 ZIP entry가 있습니다: {duplicates.Count}건.");
        }

        List<string> unsafeEntries = entryNames.Where(n => !ZipEntryPathSafety.IsSafe(n)).ToList();
        if (unsafeEntries.Count > 0)
        {
            errors.Add($"안전하지 않은(path traversal 위험) entry가 있습니다: {unsafeEntries.Count}건.");
        }

        if (errors.Count > 0)
        {
            return new BackupValidationResult(false, errors); // 이후 검사는 entry 이름을 신뢰해야 하므로 여기서 멈춘다.
        }

        // ── 2) manifest.json ─────────────────────────────────────────────────────────
        if (!reader.HasEntry("manifest.json"))
        {
            return BackupValidationResult.Fail("manifest.json이 없습니다.");
        }

        BackupManifest manifest;
        try
        {
            manifest = reader.ReadManifest();
        }
        catch (JsonException ex)
        {
            return BackupValidationResult.Fail($"manifest.json을 파싱할 수 없습니다: {ex.Message}");
        }

        if (manifest.BackupFormatVersion != BackupManifest.CurrentFormatVersion)
        {
            return BackupValidationResult.Fail(
                $"지원하지 않는 backupFormatVersion입니다: {manifest.BackupFormatVersion} (지원: {BackupManifest.CurrentFormatVersion}).");
        }

        // ── 3) checksums.json ────────────────────────────────────────────────────────
        if (!reader.HasEntry("checksums.json"))
        {
            errors.Add("checksums.json이 없습니다.");
            return new BackupValidationResult(false, errors);
        }

        ChecksumManifest checksums;
        try
        {
            checksums = reader.ReadChecksums();
        }
        catch (JsonException ex)
        {
            return BackupValidationResult.Fail($"checksums.json을 파싱할 수 없습니다: {ex.Message}");
        }

        Dictionary<string, ChecksumEntry> checksumByPath = checksums.Entries
            .ToDictionary(e => e.Path, StringComparer.Ordinal);

        // ── 4) conversation → payload 참조 유효성(선택/dependency 구분 없이 전부) ─────────
        foreach (Backup.Manifest.BackupConversationMetadata conversation in manifest.Conversations)
        {
            foreach (string entryPath in conversation.PayloadRolloutEntries.Concat(conversation.PayloadAttachmentEntries))
            {
                if (!checksumByPath.ContainsKey(entryPath))
                {
                    errors.Add($"thread {Domain.Diagnostics.Redact.ShortHash(conversation.ThreadId)}의 payload 참조가 checksums.json에 없습니다: {entryPath}");
                }

                if (!reader.HasEntry(entryPath))
                {
                    errors.Add($"thread {Domain.Diagnostics.Redact.ShortHash(conversation.ThreadId)}의 payload entry가 ZIP에 없습니다: {entryPath}");
                }
            }
        }

        // ── 5) 모든 checksum entry가 실제로 존재하고, 길이/해시가 일치하는지(스트리밍 재계산) ──
        foreach (ChecksumEntry entry in checksums.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using Stream? stream = reader.OpenEntry(entry.Path);
            if (stream is null)
            {
                errors.Add($"checksums.json이 참조하는 entry가 ZIP에 없습니다: {entry.Path}");
                continue;
            }

            StreamingHashCopy.Result actual = StreamingHashCopy.HashOnly(stream, cancellationToken);
            if (actual.ByteLength != entry.ByteLength)
            {
                errors.Add($"{entry.Path}의 바이트 길이가 일치하지 않습니다(기록: {entry.ByteLength}, 실제: {actual.ByteLength}).");
            }

            if (!string.Equals(actual.Sha256Hex, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{entry.Path}의 SHA-256이 일치하지 않습니다(변조 의심).");
            }

            if (entry.Path.StartsWith("payload/rollouts/", StringComparison.Ordinal))
            {
                string? parseError = TryMinimalJsonlParse(reader, entry.Path);
                if (parseError is not null)
                {
                    errors.Add($"{entry.Path}: {parseError}");
                }
            }
        }

        return errors.Count == 0 ? BackupValidationResult.Pass() : new BackupValidationResult(false, errors);
    }

    /// <summary>
    /// rollout payload의 첫 줄이 JSON으로 파싱되는지만 확인한다("최소 parse 가능", 요구사항 13).
    /// 전체 파일을 파싱하지 않는다 — 대형 rollout(수백 MB)에서도 검증이 느려지지 않게 하기 위함이다.
    /// <c>.jsonl.zst</c>는 <see cref="DecompressionStream"/>으로 먼저 풀어서 확인한다.
    /// </summary>
    private static string? TryMinimalJsonlParse(BackupReader reader, string entryPath)
    {
        using Stream? raw = reader.OpenEntry(entryPath);
        if (raw is null)
        {
            return "entry를 열 수 없습니다.";
        }

        Stream content = entryPath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
            ? new DecompressionStream(raw)
            : raw;
        try
        {
            using var textReader = new StreamReader(content);
            string? firstLine = textReader.ReadLine();
            if (firstLine is null)
            {
                return null; // 빈 파일은 파싱 실패로 취급하지 않는다(빈 rollout이 이론상 가능).
            }

            using JsonDocument _ = JsonDocument.Parse(firstLine);
            return null;
        }
        catch (JsonException)
        {
            return "첫 줄이 유효한 JSON이 아닙니다.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return $".zst 압축 해제에 실패했습니다: {ex.Message}";
        }
        finally
        {
            if (!ReferenceEquals(content, raw))
            {
                content.Dispose();
            }
        }
    }
}
