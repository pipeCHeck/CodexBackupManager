using System;
using System.Collections.Generic;
using System.IO;
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
using CodexBackupManager.Domain.Paths;

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

        RelationComputation computation = DetermineRelation(
            conversation.ThreadId, localCatalog.Chains, localByThreadId.ContainsKey(conversation.ThreadId), localSliceReader,
            backupCatalog.Chains, backupSliceReader, warnings, cancellationToken);

        MetadataDifferences metadataDiff = MetadataDifferenceAnalyzer.Compare(localEntry, conversation);
        ImportPlannedAction plannedAction = ImportConflictAnalyzer.Decide(computation.Relation);

        return new ImportConversationPreview(
            conversation.ThreadId,
            conversation.IsSelected,
            conversation.ResolvedTitle,
            computation.Relation,
            plannedAction,
            metadataDiff,
            warnings,
            computation.LocalRevision,
            computation.IncomingRevision);
    }

    /// <summary>
    /// <see cref="DetermineRelation"/>의 결과. 관계 enum뿐 아니라 실제로 계산에 쓰인
    /// <see cref="ConversationRevision"/> 두 개도 함께 돌려준다 — Phase 06_02의
    /// <see cref="ImportPlanBuilder"/>가 이 값을 그대로 freeze해서 Phase 7이 다시 계산하지 않고도
    /// "그때의 revision"을 정확히 재현/비교할 수 있게 하기 위함이다(재계산 금지 원칙, 요구사항 8).
    /// </summary>
    private sealed record RelationComputation(
        RevisionRelation Relation, ConversationRevision? LocalRevision, ConversationRevision? IncomingRevision);

    /// <summary>
    /// Phase 06_01 정정: "New"는 현재 Codex에 이 ThreadId가 정말로 전혀 없을 때만이다 — chain이
    /// 없다는 사실 하나만으로 New로 단정하지 않는다. state DB/카탈로그(dependency-only/internal
    /// thread를 포함해 CodexCatalog.AllConversations 전체)에 같은 ThreadId metadata가 있는데
    /// rollout 파일 삭제/경로 손상/파싱 실패 등으로 chain만 없는 상태라면, 이건 "새 대화"가 아니라
    /// "로컬 상태가 손상돼 안전하게 판정할 수 없는" 경우다 — New로 잘못 분류하면 Phase 7이 이미
    /// 존재하는 대화를 "새 Import"로 취급해 위험한 동작을 할 수 있다.
    /// </summary>
    private static RelationComputation DetermineRelation(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> localChains,
        bool existsInLocalCatalog,
        IRolloutSliceReader localSliceReader,
        IReadOnlyDictionary<string, ThreadChain> backupChains,
        IRolloutSliceReader backupSliceReader,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        bool hasLocalChain = localChains.ContainsKey(threadId);

        if (!hasLocalChain && !existsInLocalCatalog)
        {
            return new RelationComputation(RevisionRelation.New, null, null);
        }

        if (!hasLocalChain)
        {
            warnings.Add("로컬에 이 대화의 metadata는 있지만 rollout 파일 체인을 찾을 수 없습니다.");
            return new RelationComputation(RevisionRelation.Unverifiable, null, null);
        }

        ConversationRevisionBuildResult localResult =
            ConversationRevisionBuilder.Build(threadId, localChains, localSliceReader, cancellationToken);
        if (localResult.Revision is null)
        {
            warnings.Add(localResult.UnverifiableReason ?? "로컬 대화의 revision을 계산할 수 없습니다.");
            return new RelationComputation(RevisionRelation.Unverifiable, null, null);
        }

        ConversationRevisionBuildResult incomingResult =
            ConversationRevisionBuilder.Build(threadId, backupChains, backupSliceReader, cancellationToken);
        if (incomingResult.Revision is null)
        {
            warnings.Add(incomingResult.UnverifiableReason ?? "backup 대화의 revision을 계산할 수 없습니다.");
            return new RelationComputation(RevisionRelation.Unverifiable, localResult.Revision, null);
        }

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            localResult.Revision, localSliceReader, incomingResult.Revision, backupSliceReader, cancellationToken);

        return new RelationComputation(relation, localResult.Revision, incomingResult.Revision);
    }

    /// <summary>
    /// 사용자가 특정 프로젝트의 경로를 직접 재지정한 결과를 <paramref name="preview"/>에 반영한
    /// 새 <see cref="ImportPreview"/>를 만든다(Phase 06_01 — 요구사항 3). Codex에는 아무것도 쓰지
    /// 않는다 — 이 메서드는 <see cref="ImportPreview"/>(메모리 상의 Preview 결과)만 갱신한다.
    /// </summary>
    /// <remarks>
    /// revision 비교 결과(<see cref="ImportConversationPreview.Relation"/>/<see cref="ImportConversationPreview.PlannedAction"/>)는
    /// 경로 재매핑과 무관하므로 다시 계산하지 않는다 — 해당 프로젝트의
    /// <see cref="ImportProjectPreview.PathMapping"/>만 교체한다. View code-behind가 아니라 이
    /// 메서드(그리고 <see cref="ProjectPathMapping.WithManualOverride"/>)가 override 상태의 유일한
    /// 저장 장소다 — Phase 7은 이 결과를 그대로 받아 쓰면 된다.
    /// </remarks>
    /// <param name="preview">기존 Preview(<see cref="ImportPreview.Success"/>가 <c>true</c>여야 한다).</param>
    /// <param name="projectId">재지정할 프로젝트의 backup 쪽 원본 ID. "기타 대화"는 <c>null</c>이며 재지정할 수 없다.</param>
    /// <param name="userSelectedPath">사용자가 고른 폴더 경로. 실제로 존재하는 디렉터리여야 한다.</param>
    /// <exception cref="InvalidOperationException"><paramref name="preview"/>가 실패했거나, 대상 프로젝트가 "기타 대화"일 때.</exception>
    /// <exception cref="ArgumentException">해당 <paramref name="projectId"/>를 가진 프로젝트가 없거나 경로가 비었을 때.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="userSelectedPath"/>가 실제 디렉터리로 존재하지 않을 때.</exception>
    public static ImportPreview ApplyManualProjectPathOverride(ImportPreview preview, string? projectId, string userSelectedPath)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSelectedPath);

        if (!preview.Success)
        {
            throw new InvalidOperationException("검증에 실패한 Preview에는 경로를 재지정할 수 없습니다.");
        }

        // Codex에는 쓰지 않지만, 사용자가 고른 경로 자체는 실제로 존재하는 폴더인지 확인한다 —
        // 존재하지 않는 경로를 Import Plan에 그대로 흘려보내지 않는다.
        if (!Directory.Exists(userSelectedPath))
        {
            throw new DirectoryNotFoundException("지정한 폴더가 존재하지 않습니다.");
        }

        CanonicalPath canonical = CanonicalPath.Create(userSelectedPath);

        int matchIndex = -1;
        for (int i = 0; i < preview.Projects.Count; i++)
        {
            if (string.Equals(preview.Projects[i].ProjectId, projectId, StringComparison.Ordinal))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            throw new ArgumentException($"프로젝트를 찾을 수 없습니다: {projectId ?? "(null)"}", nameof(projectId));
        }

        ImportProjectPreview target = preview.Projects[matchIndex];
        ProjectPathMapping updatedMapping = target.PathMapping.WithManualOverride(canonical.Display);

        List<ImportProjectPreview> updatedProjects = preview.Projects.ToList();
        updatedProjects[matchIndex] = target with { PathMapping = updatedMapping };

        return preview with { Projects = updatedProjects };
    }
}
