using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Codex.Attachments;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Diagnostics;

namespace CodexBackupManager.Backup.Planning;

/// <summary>
/// 선택된 ThreadId 집합(<c>MainViewModel.GetSelectedThreadIdsSnapshot()</c>)과 카탈로그로부터
/// <see cref="ExportPlan"/>을 계산한다. 파일 I/O는 하지 않는다(첨부 실존 확인을 위한 가벼운
/// <see cref="File.Exists"/> 확인과, 첨부 참조를 찾기 위한 rollout 파일 스트리밍 스캔은 예외).
/// </summary>
/// <remarks>
/// <para>
/// <b>lineage 규칙을 새로 만들지 않는다.</b> 어느 rollout 파일이 필요한지는 전부
/// <see cref="ThreadDependencyResolver"/>(=Viewer의 <c>ConversationTranscriptBuilder</c>가 쓰는
/// 것과 같은 코드)가 결정한다. 이 클래스는 그 결과를 여러 선택 대화에 걸쳐 모으고 dedupe할 뿐이다.
/// </para>
/// <para>
/// <b>선택 vs dependency 구분</b>: <paramref name="selectedThreadIds"/>에 있는 thread만
/// <see cref="PlannedConversation.IsSelected"/>가 <c>true</c>다. 어떤 선택 대화의 조상이라서
/// 파일이 필요해 포함된 thread는 <c>false</c>다 — 그 thread가 우연히 다른 선택에도 포함돼 있다면
/// 당연히 <c>true</c>가 된다(선택이 우선).
/// </para>
/// </remarks>
public static class ExportPlanBuilder
{
    /// <summary>Export 계획을 만든다.</summary>
    /// <param name="catalog">현재 카탈로그(<see cref="CodexCatalog"/>).</param>
    /// <param name="selectedThreadIds">사용자가 실제로 선택한 ThreadId 집합.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    public static ExportPlan Build(
        CodexCatalog catalog,
        IReadOnlySet<string> selectedThreadIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedThreadIds);

        var warnings = new List<string>();
        var fatalErrors = new List<string>();
        Dictionary<string, ConversationEntry> entryByThreadId = catalog.AllConversations
            .ToDictionary(e => e.ThreadId, StringComparer.OrdinalIgnoreCase);

        // thread ID -> 필요한 파일(여러 선택 대화의 조상으로 겹치면 합집합), 절대경로 기준.
        var filesByThread = new Dictionary<string, Dictionary<string, RolloutFileReference>>(StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string threadId in selectedThreadIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected.Add(threadId);
            string threadHash = Redact.ShortHash(threadId);

            // 선택한 대화 하나라도 완전하게 백업할 수 없으면(체인 없음/조상 없음/순환 참조/metadata
            // 없음) 이 Export 전체를 FAIL시킨다 — "부분 성공"을 성공으로 위장하지 않는다
            // (Phase 05_01). optional한 것(첨부 누락 등)만 warnings로 허용한다.
            if (!catalog.Chains.ContainsKey(threadId))
            {
                fatalErrors.Add($"선택한 대화(thread={threadHash})의 rollout 파일 체인을 찾을 수 없습니다.");
                continue;
            }

            if (!entryByThreadId.ContainsKey(threadId))
            {
                fatalErrors.Add($"선택한 대화(thread={threadHash})의 state DB metadata를 찾을 수 없어 복원에 필요한 정보가 부족합니다.");
                continue;
            }

            var chainWarnings = new List<string>();
            IReadOnlyList<ThreadDependencyResolver.ChainLink> links =
                ThreadDependencyResolver.ResolveChainLinks(threadId, catalog.Chains, chainWarnings, out bool hasCycle);

            if (hasCycle)
            {
                fatalErrors.Add($"선택한 대화(thread={threadHash})의 조상 체인에서 순환 참조가 발견되어 완전한 복원을 보장할 수 없습니다.");
                continue;
            }

            if (chainWarnings.Count > 0)
            {
                fatalErrors.Add($"선택한 대화(thread={threadHash})의 조상 rollout 파일 일부를 찾을 수 없어 완전한 dependency closure를 만들 수 없습니다.");
                continue;
            }

            foreach (ThreadDependencyResolver.ChainLink link in links)
            {
                Dictionary<string, RolloutFileReference> files = filesByThread.TryGetValue(link.ThreadId, out var existing)
                    ? existing
                    : filesByThread[link.ThreadId] = new Dictionary<string, RolloutFileReference>(StringComparer.OrdinalIgnoreCase);

                foreach (RolloutFileReference file in link.Files)
                {
                    files[file.FullPath] = file; // 같은 조상을 여러 선택이 공유하면 합집합이 된다.
                }
            }
        }

        // ── rollout payload(전역, dedupe) ──────────────────────────────────────────
        var rolloutByPath = new Dictionary<string, PlannedPayloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, RolloutFileReference> files in filesByThread.Values)
        {
            foreach (RolloutFileReference file in files.Values)
            {
                if (!rolloutByPath.ContainsKey(file.FullPath))
                {
                    rolloutByPath[file.FullPath] = new PlannedPayloadFile(file.FullPath, $"payload/rollouts/{file.FileName}");
                }
            }
        }

        // ── 첨부(local_image) 스캔 ──────────────────────────────────────────────────
        var attachmentPathsByThread = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scannedFileCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string threadId, Dictionary<string, RolloutFileReference> files) in filesByThread)
        {
            var refs = new List<string>();
            foreach (RolloutFileReference file in files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scannedFileCache.TryGetValue(file.FullPath, out IReadOnlyList<string>? paths))
                {
                    paths = LocalImageAttachmentScanner.ScanFile(file, cancellationToken);
                    scannedFileCache[file.FullPath] = paths;
                }

                refs.AddRange(paths);
            }

            attachmentPathsByThread[threadId] = refs;
        }

        var attachmentByAbsolutePath = new Dictionary<string, PlannedAttachment>(StringComparer.OrdinalIgnoreCase);
        int missingAttachments = 0;
        int attachmentIndex = 0;
        foreach (string path in attachmentPathsByThread.Values.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (attachmentByAbsolutePath.ContainsKey(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                missingAttachments++;
                continue; // 존재하지 않는 첨부는 포함할 수 없다 — 개수만 경고로 남긴다(원본 경로는 남기지 않는다).
            }

            string entryName = $"payload/attachments/{attachmentIndex}/{Path.GetFileName(path)}";
            attachmentIndex++;
            attachmentByAbsolutePath[path] = new PlannedAttachment(path, entryName);
        }

        if (missingAttachments > 0)
        {
            warnings.Add($"참조된 첨부 이미지 파일 {missingAttachments}개를 찾을 수 없어 Export에서 제외했습니다.");
        }

        // ── 대화 목록 ────────────────────────────────────────────────────────────────
        var conversations = new List<PlannedConversation>();
        foreach ((string threadId, Dictionary<string, RolloutFileReference> filesDict) in filesByThread)
        {
            List<RolloutFileReference> orderedFiles = filesDict.Values
                .OrderBy(f => f.TimestampFromFileName ?? DateTimeOffset.MinValue)
                .ThenBy(f => f.SegmentId ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            List<string> attachmentPaths = attachmentPathsByThread.TryGetValue(threadId, out List<string>? paths)
                ? paths.Where(p => attachmentByAbsolutePath.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

            entryByThreadId.TryGetValue(threadId, out ConversationEntry? entry);

            conversations.Add(new PlannedConversation(
                threadId,
                IsSelected: selected.Contains(threadId),
                Entry: entry,
                RolloutFiles: orderedFiles,
                AttachmentAbsolutePaths: attachmentPaths));
        }

        // ── 프로젝트(선택된 대화 기준) ─────────────────────────────────────────────────
        var projects = new List<PlannedProject>();
        foreach (ProjectEntry project in catalog.Projects)
        {
            List<string> selectedIds = project.Conversations
                .Select(c => c.ThreadId)
                .Where(selected.Contains)
                .ToList();

            if (selectedIds.Count == 0)
            {
                continue;
            }

            projects.Add(new PlannedProject(project.ProjectId, project.DisplayName, project.RootPaths, selectedIds));
        }

        return new ExportPlan(conversations, rolloutByPath.Values.ToList(), attachmentByAbsolutePath.Values.ToList(), projects, warnings, fatalErrors);
    }
}
