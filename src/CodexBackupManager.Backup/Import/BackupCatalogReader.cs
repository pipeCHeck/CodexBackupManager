using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Sessions;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// backup(<c>.codexbackup</c>)의 <c>payload/rollouts/</c> entry들만으로 로컬
/// <see cref="Codex.Catalog.CodexCatalogBuilder"/>와 같은 lineage(<see cref="ThreadChain"/>)를
/// 재구성한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>manifest의 <c>payloadRolloutEntries</c>에 의존하지 않는다.</b> Export는 파일을 파싱/재작성하지
/// 않고 원본 바이트를 그대로 담으므로, 각 rollout entry 안에는 로컬 파일과 완전히 동일한
/// <c>session_meta</c>(history_base/forked_from_id 등)가 그대로 들어 있다. 그래서 로컬 카탈로그를
/// 만들 때와 똑같이 (1) 파일명 파싱 → (2) <c>session_meta</c> 스캔 → (3)
/// <see cref="ThreadChainResolver.Resolve"/>만 반복하면, manifest에 별도 lineage 필드를 추가하지
/// 않고도 Export/Viewer와 100% 같은 코드로 backup의 lineage를 얻을 수 있다(drift 없음).
/// </para>
/// <para>
/// backup은 하나의 대화가 요구하는 전체 dependency closure(선택 + 조상)를 이미 담고 있으므로
/// (Phase 05_01 "부분 성공 금지" 정책), <c>payload/rollouts/</c> 전체로 만든 체인 맵은 manifest에
/// 나열된 어떤 conversation의 ancestry도 해석할 수 있을 만큼 충분하다.
/// </para>
/// </remarks>
public static class BackupCatalogReader
{
    private const string RolloutEntryPrefix = "payload/rollouts/";

    /// <summary>결과.</summary>
    /// <param name="Files">backup에서 찾은 rollout 파일(=entry) 참조 목록.</param>
    /// <param name="Chains">thread ID → <see cref="ThreadChain"/> 맵(로컬 카탈로그와 같은 구조).</param>
    /// <param name="Warnings">session_meta를 찾지 못한 entry 등(사용자 원문 없음).</param>
    public sealed record Result(
        IReadOnlyList<RolloutFileReference> Files,
        IReadOnlyDictionary<string, ThreadChain> Chains,
        IReadOnlyList<string> Warnings);

    /// <summary>backup에서 lineage를 재구성한다.</summary>
    public static Result Build(BackupReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var files = new List<RolloutFileReference>();
        foreach (string entryName in reader.EntryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entryName.StartsWith(RolloutEntryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string fileName = entryName[RolloutEntryPrefix.Length..];
            RolloutFileNamePattern.ParsedRolloutFileName? parsed = RolloutFileNamePattern.TryParse(fileName);
            if (parsed is null)
            {
                continue; // 이름 규칙에 맞지 않는 entry는 조용히 무시한다(로컬 탐색과 동일한 정책).
            }

            // FullPath는 여기서 "실제 파일 경로"가 아니라 ZIP entry 경로다 — 이 값으로 이 파일을
            // 다시 열 수 있는 유일한 키이므로(BackupRolloutSliceReader가 그대로 쓴다), 그 의미로
            // 재사용한다(로컬/backup 양쪽에서 같은 RolloutFileReference 타입을 공유하기 위함).
            files.Add(new RolloutFileReference(
                FullPath: entryName,
                FileName: fileName,
                ThreadId: parsed.ThreadId,
                SegmentId: parsed.SegmentId,
                TimestampFromFileName: parsed.Timestamp,
                IsArchived: false,
                Kind: parsed.Kind));
        }

        var warnings = new List<string>();
        var metadataByFile = new Dictionary<RolloutFileReference, SessionMetadata?>();
        foreach (RolloutFileReference file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using Stream? raw = reader.OpenEntry(file.FullPath);
            if (raw is null)
            {
                warnings.Add($"{file.FileName}: backup에서 entry를 열 수 없습니다.");
                metadataByFile[file] = null;
                continue;
            }

            CodexSessionParser.ParseResult parsed = CodexSessionParser.ParseSessionMetadata(
                raw, file.Kind, file.FileName, file.FullPath, isArchived: false);
            metadataByFile[file] = parsed.Metadata;
            if (parsed.Warning is not null)
            {
                warnings.Add(parsed.Warning);
            }
        }

        IReadOnlyDictionary<string, ThreadChain> chains = ThreadChainResolver.Resolve(files, metadataByFile);
        return new Result(files, chains, warnings);
    }
}
