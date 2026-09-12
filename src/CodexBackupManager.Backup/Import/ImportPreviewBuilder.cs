using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Backup.Validation;
using CodexBackupManager.Codex.Revisions;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// Phase 6 Import Preview의 진입점. <c>.codexbackup</c> 파일 + 현재 로컬 <see cref="CodexCatalog"/>로
/// <see cref="ImportPreview"/>를 만든다. Codex에는 어떤 것도 쓰지 않는다(요구사항 전체의 대전제).
/// </summary>
/// <remarks>
/// ViewModel은 이 클래스만 부른다 — ZIP/rollout 비교 로직을 직접 만들지 않는다(요구사항 9).
/// </remarks>
public static class ImportPreviewBuilder
{
    /// <summary>파일 경로로 Preview를 만든다. 검증 실패면 <see cref="ImportPreview.Failed"/>를 돌려준다.</summary>
    public static ImportPreview Build(string backupFilePath, CodexCatalog localCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        ArgumentNullException.ThrowIfNull(localCatalog);

        BackupValidationResult validation = BackupValidator.Validate(backupFilePath, cancellationToken);
        if (!validation.Success)
        {
            return ImportPreview.Failed(validation.Errors);
        }

        using BackupReader reader = BackupReader.Open(backupFilePath);
        return Build(reader, localCatalog, cancellationToken);
    }

    /// <summary>이미 연 <see cref="BackupReader"/>로 Preview를 만든다.</summary>
    public static ImportPreview Build(BackupReader reader, CodexCatalog localCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(localCatalog);

        // 호출자가 검증을 건너뛰고 바로 이 오버로드를 불렀을 수도 있으므로 여기서도 반드시 다시
        // 확인한다 — malformed backup으로는 절대 Preview를 만들지 않는다(요구사항 17).
        BackupValidationResult validation = BackupValidator.Validate(reader, cancellationToken);
        if (!validation.Success)
        {
            return ImportPreview.Failed(validation.Errors);
        }

        BackupManifest manifest = reader.ReadManifest();
        BackupCatalogReader.Result backupCatalog = BackupCatalogReader.Build(reader, cancellationToken);

        var backupSliceReader = new BackupRolloutSliceReader(reader);
        IRolloutSliceReader localSliceReader = LocalFileRolloutSliceReader.Instance;

        Dictionary<string, ConversationEntry> localByThreadId = localCatalog.AllConversations
            .ToDictionary(e => e.ThreadId, StringComparer.OrdinalIgnoreCase);

        var conversationPreviews = new Dictionary<string, ImportConversationPreview>(StringComparer.OrdinalIgnoreCase);
        foreach (BackupConversationMetadata conversation in manifest.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            conversationPreviews[conversation.ThreadId] = BuildConversationPreview(
                conversation, localCatalog, localByThreadId, backupCatalog, backupSliceReader, localSliceReader, cancellationToken);
        }

        var projectPreviews = new List<ImportProjectPreview>();
        var coveredSelectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BackupProjectMetadata project in manifest.Projects)
        {
            ProjectPathMapping mapping = ProjectPathMapper.Resolve(project, localCatalog.Projects);
            List<ImportConversationPreview> conversations = project.ConversationThreadIds
                .Where(conversationPreviews.ContainsKey)
                .Select(id => conversationPreviews[id])
                .ToList();
            foreach (string id in project.ConversationThreadIds)
            {
                coveredSelectedIds.Add(id);
            }

            projectPreviews.Add(new ImportProjectPreview(project.ProjectId, project.DisplayName, mapping, conversations));
        }

        // manifest.projects[].conversationThreadIds에 포함되지 않은 선택 대화가 있으면(이론상
        // 발생하면 안 되지만 — 실제 UI 선택은 항상 "기타 대화" 그룹을 거치므로) 조용히 화면에서
        // 사라지게 두지 않는다. 이 프로젝트의 기존 원칙과 같다: 확신 없으면 안전한 쪽(숨기지 않는다).
        List<ImportConversationPreview> uncoveredSelected = manifest.Conversations
            .Where(c => c.IsSelected && !coveredSelectedIds.Contains(c.ThreadId))
            .Select(c => conversationPreviews[c.ThreadId])
            .ToList();
        if (uncoveredSelected.Count > 0)
        {
            var fallbackMapping = new ProjectPathMapping(null, "기타 대화", [], ProjectPathMappingStatus.NotApplicable, null, null);
            projectPreviews.Add(new ImportProjectPreview(null, "기타 대화", fallbackMapping, uncoveredSelected));
        }

        List<ImportConversationPreview> dependencyOnly = manifest.Conversations
            .Where(c => !c.IsSelected)
            .Select(c => conversationPreviews[c.ThreadId])
            .ToList();

        List<string> warnings = [.. manifest.Warnings, .. backupCatalog.Warnings];

        return new ImportPreview(true, [], manifest, projectPreviews, dependencyOnly, warnings);
    }

    private static ImportConversationPreview BuildConversationPreview(
        BackupConversationMetadata conversation,
        CodexCatalog localCatalog,
        Dictionary<string, ConversationEntry> localByThreadId,
        BackupCatalogReader.Result backupCatalog,
        IRolloutSliceReader backupSliceReader,
        IRolloutSliceReader localSliceReader,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        localByThreadId.TryGetValue(conversation.ThreadId, out ConversationEntry? localEntry);

        RevisionRelation relation = DetermineRelation(
            conversation.ThreadId, localCatalog.Chains, localSliceReader,
            backupCatalog.Chains, backupSliceReader, warnings, cancellationToken);

        MetadataDifferences metadataDiff = MetadataDifferenceAnalyzer.Compare(localEntry, conversation);
        ImportPlannedAction plannedAction = ImportConflictAnalyzer.Decide(relation);

        return new ImportConversationPreview(
            conversation.ThreadId,
            conversation.IsSelected,
            conversation.ResolvedTitle,
            relation,
            plannedAction,
            metadataDiff,
            warnings);
    }

    private static RevisionRelation DetermineRelation(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> localChains,
        IRolloutSliceReader localSliceReader,
        IReadOnlyDictionary<string, ThreadChain> backupChains,
        IRolloutSliceReader backupSliceReader,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!localChains.ContainsKey(threadId))
        {
            return RevisionRelation.New;
        }

        ConversationRevisionBuildResult localResult =
            ConversationRevisionBuilder.Build(threadId, localChains, localSliceReader, cancellationToken);
        if (localResult.Revision is null)
        {
            warnings.Add(localResult.UnverifiableReason ?? "로컬 대화의 revision을 계산할 수 없습니다.");
            return RevisionRelation.Unverifiable;
        }

        ConversationRevisionBuildResult incomingResult =
            ConversationRevisionBuilder.Build(threadId, backupChains, backupSliceReader, cancellationToken);
        if (incomingResult.Revision is null)
        {
            warnings.Add(incomingResult.UnverifiableReason ?? "backup 대화의 revision을 계산할 수 없습니다.");
            return RevisionRelation.Unverifiable;
        }

        return ConversationRevisionComparer.Compare(
            localResult.Revision, localSliceReader, incomingResult.Revision, backupSliceReader, cancellationToken);
    }
}
