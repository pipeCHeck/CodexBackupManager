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
/// 파일을 최종 파일로 옮기기 전에 반드시 이 검증을 통과해야 한다("Writer가 파일을 만들었다고
/// 성공으로 끝내지 않는다"). Phase 6부터는 <c>.codexbackup</c>이 외부 입력(사용자가 다른 PC에서
/// 가져온 파일)이 되므로, <b>malformed/조작된 ZIP이 들어와도 예외를 밖으로 흘리지 않고 항상
/// <see cref="BackupValidationResult.Fail"/>로 돌려준다</b>(Phase 05_01 hardening).
/// </summary>
public static class BackupValidator
{
    /// <summary>파일 경로를 검증한다. 어떤 이유로도 예외를 던지지 않는다.</summary>
    public static BackupValidationResult Validate(string path, CancellationToken cancellationToken = default)
    {
        BackupReader reader;
        try
        {
            reader = BackupReader.Open(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BackupValidationResult.Fail($"ZIP 파일을 열 수 없습니다: {ex.GetType().Name}");
        }

        using (reader)
        {
            return Validate(reader, cancellationToken);
        }
    }

    /// <summary>이미 열린 리더로 검증한다. 어떤 이유로도 예외를 던지지 않는다(취소 제외).</summary>
    public static BackupValidationResult Validate(BackupReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            return ValidateCore(reader, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // malformed 입력(예: 손상된 entry, 예상 못한 타입 변환)이 여기까지 예외로 새어나오면
            // Phase 6에서 외부 .codexbackup 하나가 앱 전체를 죽일 수 있다 — 반드시 Fail로 바꾼다.
            return BackupValidationResult.Fail($"예상치 못한 오류로 검증에 실패했습니다: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static BackupValidationResult ValidateCore(BackupReader reader, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // ── 1) entry 이름 자체의 안전성(중복 — ordinal/case-insensitive 둘 다 — path traversal, 허용된 위치인지) ──
        List<string> entryNames = reader.EntryNames.ToList();

        var ordinalDuplicates = entryNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (ordinalDuplicates.Count > 0)
        {
            errors.Add($"중복된 ZIP entry가 있습니다: {ordinalDuplicates.Count}건.");
        }

        // Windows는 파일 시스템이 대소문자를 구분하지 않는다 — "payload/A"와 "payload/a"는 서로 다른
        // ordinal 문자열이라 위 검사를 통과하지만, 실제로 압축을 풀면 서로 덮어써 데이터가 사라진다.
        var caseInsensitiveDuplicates = entryNames
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (caseInsensitiveDuplicates.Count > 0)
        {
            errors.Add($"Windows 기준(대소문자 무시) 충돌하는 ZIP entry가 있습니다: {caseInsensitiveDuplicates.Count}건.");
        }

        List<string> unsafeEntries = entryNames.Where(n => !ZipEntryPathSafety.IsSafe(n)).ToList();
        if (unsafeEntries.Count > 0)
        {
            errors.Add($"안전하지 않은(path traversal 위험) entry가 있습니다: {unsafeEntries.Count}건.");
        }

        // 허용된 위치: manifest.json, checksums.json, payload/ 하위. 그 외 "예상하지 못한" entry는
        // 우리가 만들지 않는 것이므로(조작된 파일 의심) 명시적으로 거부한다(요구사항 4 "예상하지
        // 못한/추가 payload 정책").
        List<string> unexpectedEntries = entryNames
            .Where(n => n is not ("manifest.json" or "checksums.json") && !n.StartsWith("payload/", StringComparison.Ordinal))
            .ToList();
        if (unexpectedEntries.Count > 0)
        {
            errors.Add($"허용되지 않은 위치의 entry가 있습니다: {unexpectedEntries.Count}건.");
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
            return BackupValidationResult.Fail("checksums.json이 없습니다.");
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

        // checksums.json 자신의 무결성(중복 경로, 안전한 상대경로)부터 확인한다 — 이 값들을
        // 그대로 신뢰해서 딕셔너리를 만들면(예: 중복 키) 예외가 나므로 먼저 걸러낸다.
        var checksumPathDuplicates = checksums.Entries
            .GroupBy(e => e.Path, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (checksumPathDuplicates.Count > 0)
        {
            errors.Add($"checksums.json에 중복된 경로가 있습니다: {checksumPathDuplicates.Count}건.");
        }

        List<string> unsafeChecksumPaths = checksums.Entries
            .Where(e => !ZipEntryPathSafety.IsSafe(e.Path))
            .Select(e => e.Path)
            .ToList();
        if (unsafeChecksumPaths.Count > 0)
        {
            errors.Add($"checksums.json에 안전하지 않은 경로가 있습니다: {unsafeChecksumPaths.Count}건.");
        }

        if (errors.Count > 0)
        {
            return new BackupValidationResult(false, errors); // 아래부터는 경로를 키로 쓰는 사전을 만들어야 하므로 여기서 멈춘다.
        }

        Dictionary<string, ChecksumEntry> checksumByPath = checksums.Entries
            .ToDictionary(e => e.Path, StringComparer.Ordinal);

        // ── 4) manifest 자체의 내부 일관성(개수 필드, thread ID 중복, project 참조) ─────────
        var threadIdDuplicates = manifest.Conversations
            .GroupBy(c => c.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (threadIdDuplicates.Count > 0)
        {
            errors.Add($"manifest에 중복된 threadId가 있습니다: {threadIdDuplicates.Count}건.");
        }

        int actualSelectedCount = manifest.Conversations.Count(c => c.IsSelected);
        if (manifest.ConversationCount != actualSelectedCount)
        {
            errors.Add($"conversationCount({manifest.ConversationCount})가 실제 선택된 대화 수({actualSelectedCount})와 다릅니다.");
        }

        int actualDependencyCount = manifest.Conversations.Count(c => !c.IsSelected);
        if (manifest.DependencyConversationCount != actualDependencyCount)
        {
            errors.Add($"dependencyConversationCount({manifest.DependencyConversationCount})가 실제 dependency 대화 수({actualDependencyCount})와 다릅니다.");
        }

        if (manifest.ProjectCount != manifest.Projects.Count)
        {
            errors.Add($"projectCount({manifest.ProjectCount})가 실제 projects 개수({manifest.Projects.Count})와 다릅니다.");
        }

        int actualPayloadCount = checksums.Entries.Count(e => e.Path.StartsWith("payload/", StringComparison.Ordinal));
        if (manifest.PayloadCount != actualPayloadCount)
        {
            errors.Add($"payloadCount({manifest.PayloadCount})가 실제 payload entry 수({actualPayloadCount})와 다릅니다.");
        }

        var selectedThreadIds = new HashSet<string>(
            manifest.Conversations.Where(c => c.IsSelected).Select(c => c.ThreadId),
            StringComparer.OrdinalIgnoreCase);
        foreach (BackupProjectMetadata project in manifest.Projects)
        {
            foreach (string threadId in project.ConversationThreadIds)
            {
                if (!selectedThreadIds.Contains(threadId))
                {
                    errors.Add($"project '{project.DisplayName}'가 선택되지 않은/존재하지 않는 thread를 참조합니다.");
                }
            }
        }

        // ── 5) conversation → payload 참조 유효성(선택/dependency 구분 없이 전부) ─────────
        foreach (BackupConversationMetadata conversation in manifest.Conversations)
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

        // ── 6) 모든 payload/ ZIP entry가 checksum으로 보호되는지(반대 방향) ────────────────
        List<string> unprotectedPayloadEntries = entryNames
            .Where(n => n.StartsWith("payload/", StringComparison.Ordinal) && !checksumByPath.ContainsKey(n))
            .ToList();
        if (unprotectedPayloadEntries.Count > 0)
        {
            errors.Add($"checksums.json에 없는 payload entry가 있습니다(체크섬으로 보호되지 않음): {unprotectedPayloadEntries.Count}건.");
        }

        // ── 7) 모든 checksum entry가 실제로 존재하고, 길이/해시가 일치하는지(스트리밍 재계산) ──
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
