using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;

namespace CodexBackupManager.Backup.Import;

/// <summary>대화를 사용자가 체크할 수 없는 이유(설계 §7.4).</summary>
public enum ImportUnselectableReason
{
    /// <summary>이미 이 PC에 같은 내용이 있다(Identical). 연결 변경은 Phase 9_3.</summary>
    AlreadyPresent = 0,

    /// <summary>이 PC 쪽이 더 최신이다(LocalAhead).</summary>
    LocalAhead = 1,

    /// <summary>양쪽이 다르게 이어졌다(Diverged).</summary>
    Diverged = 2,

    /// <summary>이 PC 데이터가 불완전해 비교할 수 없다(Unverifiable).</summary>
    Unverifiable = 3,

    /// <summary>이어받을 대상이 <c>.jsonl.zst</c> 압축 rollout이다 — Restore가 압축 파일 append를 막는다.</summary>
    CompressedUpdate = 4,

    /// <summary>백업에 조상으로만 들어 있는 대화다. 필요하면 자동 포함되고, 직접 체크할 수 없다.</summary>
    DependencyOnly = 5,
}

/// <summary>
/// (Phase 9_3-00) 이 PC에 이미 있는 대화를 그 백업 프로젝트의 목적지로 옮길 수 있는지("📁 이 프로젝트로 옮기기").
/// <see cref="Available"/>만 고를 수 있다. Desktop* 세 가지는 흐린 체크와 이유를 보여주고, 나머지는 체크를 숨긴다.
/// </summary>
public enum RelinkStatus
{
    /// <summary>옮길 수 있다.</summary>
    Available = 0,

    /// <summary>해당 없음(이 PC에 없음·새 대화·백업에 원본으로만 있음) — 숨김.</summary>
    NotApplicable = 1,

    /// <summary>이 PC에서 보관된 대화 — 숨김.</summary>
    Archived = 2,

    /// <summary>양쪽이 다르게 이어졌거나 확인할 수 없는 대화(Diverged/Unverifiable) — 숨김.</summary>
    RelationNotAllowed = 3,

    /// <summary>목적지가 기타 대화이거나 결정 오류가 있다(9_5-11로 백업 "기타 대화" 그룹은 항상 여기) — 숨김.</summary>
    TargetNotProject = 4,

    /// <summary>이미 그 프로젝트에 있다 — 숨김.</summary>
    AlreadyThere = 5,

    /// <summary>Codex Desktop이 <c>thread-project-assignments</c>에 위치를 따로 기록했다 — 흐림 + 이유.</summary>
    DesktopAssigned = 6,

    /// <summary>Codex Desktop이 <c>projectless-thread-ids</c>에 기록했다 — 흐림 + 이유.</summary>
    DesktopProjectless = 7,

    /// <summary>Codex Desktop 상태 파일을 읽지 못했거나 9_5a 게이트에 실패했다 — 흐림 + 이유.</summary>
    DesktopStateUnavailable = 8,
}

/// <summary>쓸 것이 0개인 이유.</summary>
public enum ImportNothingToWriteReason
{
    /// <summary>쓸 것이 있다.</summary>
    None = 0,

    /// <summary>선택 가능한 대화가 있지만 하나도 체크하지 않았다.</summary>
    NothingSelected = 1,

    /// <summary>선택한 대화가 모두 이미 이 PC에 있다(같거나 이 PC가 더 최신).</summary>
    AllAlreadyPresent = 2,

    /// <summary>선택할 수 있는 대화가 없다(충돌/확인 불가 등).</summary>
    NoneSelectable = 3,
}

/// <summary>백업 프로젝트 하나의 목적지 판정 결과(사용자 결정 반영).</summary>
/// <param name="Target">최종 목적지. 결정에 오류가 있으면 제안 목적지.</param>
/// <param name="PathStatus">Phase 6 호환 경로 상태(폴더를 골랐으면 ManuallyLinked, 아니면 Preview의 자동 판정 상태).</param>
/// <param name="Error">결정 오류(적용 불가 사유). 없으면 <c>null</c>.</param>
public sealed record ProjectTargetResolution(ProjectTarget Target, ProjectPathMappingStatus PathStatus, string? Error);

/// <summary>백업 프로젝트 하나의 선택 결과.</summary>
public sealed record ImportSelectionProject(
    ImportProjectPreview Preview,
    string ProjectKey,
    ProjectTargetResolution Resolution)
{
    /// <summary>최종 목적지.</summary>
    public ProjectTarget Target => Resolution.Target;
}

/// <summary>대화 하나의 선택 결과.</summary>
/// <param name="Preview">Preview의 대화.</param>
/// <param name="ProjectKey">소속 백업 프로젝트 키. 조상 전용 대화는 <c>null</c>.</param>
/// <param name="Target">소속 프로젝트의 목적지. 조상 전용 대화는 <c>null</c>(기타 대화로 들어간다).</param>
/// <param name="FinalAction">이번 가져오기에서의 최종 동작(Import/Update/NoOp/Skip, closure 안의 충돌 조상은 Preview 값 그대로).</param>
/// <param name="SkipReason"><see cref="ImportPlannedAction.Skip"/>인 이유.</param>
/// <param name="UnselectableReason">사용자가 체크할 수 없는 이유. 체크할 수 있으면 <c>null</c>.</param>
/// <param name="IsIncludedByUser">사용자가 체크했고 선택 가능한 대화인지.</param>
/// <param name="IsAutoIncludedAncestor">포함된 대화의 조상이라 자동으로 들어갔는지(사용자가 끌 수 없다).</param>
public sealed record ImportSelectionConversation(
    ImportConversationPreview Preview,
    string? ProjectKey,
    ProjectTarget? Target,
    ImportPlannedAction FinalAction,
    ImportSkipReason SkipReason,
    ImportUnselectableReason? UnselectableReason,
    bool IsIncludedByUser,
    bool IsAutoIncludedAncestor)
{
    /// <summary>thread ID.</summary>
    public string ThreadId => Preview.ThreadId;

    /// <summary>사용자가 체크할 수 있는지.</summary>
    public bool IsSelectable => UnselectableReason is null;

    /// <summary>이번 가져오기에서 실제로 다루는 대화인지(포함 + 자동 포함 조상).</summary>
    public bool IsInClosure => IsIncludedByUser || IsAutoIncludedAncestor;

    /// <summary>(Phase 9_3-00) 목적지로 옮길 수 있는지.</summary>
    public RelinkStatus Relink { get; init; } = RelinkStatus.NotApplicable;

    /// <summary>(Phase 9_3-01) 사용자가 "📁 이 프로젝트로 옮기기"를 골랐고 옮길 수 있는지.</summary>
    public bool IsRelinkSelected { get; init; }
}

/// <summary><see cref="ImportSelection.Compute"/> 결과(요약).</summary>
/// <param name="Projects">백업 프로젝트별 목적지(Preview 순서).</param>
/// <param name="Conversations">대화별 결과(프로젝트 대화 → 조상 전용 대화 순서, Plan과 같은 순서).</param>
/// <param name="ImportCount">새로 가져올 대화 수(조상 포함).</param>
/// <param name="UpdateCount">뒤에 이어붙일 대화 수(조상 포함).</param>
/// <param name="NoOpCount">closure 안에서 이미 같은 조상 수.</param>
/// <param name="SkipCount">이번에 건너뛰는 대화 수.</param>
/// <param name="AutoIncludedAncestorCount">자동 포함된 조상 수.</param>
/// <param name="UncategorizedImportCount">"기타 대화"로 들어갈 새 대화 수(목적지가 Uncategorized이거나 조상 전용).</param>
/// <param name="BlockingReasons">적용할 수 없는 이유(closure 안의 충돌 조상, 결정 오류). 없으면 빈 목록.</param>
/// <param name="Warnings">무시한 선택 등 경고.</param>
/// <param name="NothingToWriteReason">쓸 것이 0개인 이유.</param>
/// <param name="HasBlockingIssues">
/// closure 안에 적용할 수 없는 대화가 있는지(Plan 플래그와 같은 값): Blocked(Unverifiable) 조상, 백업에 없는 조상,
/// 압축 rollout을 이어받아야 하는 조상. 결정 오류는 여기에 넣지 않는다(그때는 Plan을 만들지 않는다).
/// </param>
/// <param name="HasUnresolvedDivergence">closure 안에 RequiresDecision(Diverged) 대화가 있는지(Plan 플래그와 같은 값).</param>
public sealed record ImportSelectionSummary(
    IReadOnlyList<ImportSelectionProject> Projects,
    IReadOnlyList<ImportSelectionConversation> Conversations,
    int ImportCount,
    int UpdateCount,
    int NoOpCount,
    int SkipCount,
    int AutoIncludedAncestorCount,
    int UncategorizedImportCount,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings,
    ImportNothingToWriteReason NothingToWriteReason,
    bool HasBlockingIssues,
    bool HasUnresolvedDivergence)
{
    /// <summary>
    /// (Phase 9_5-01) 이번 가져오기로 새로 만들 프로젝트(같은 canonical 루트는 하나). 새로 가져올 대화가 들어가는 목적지만 센다.
    /// </summary>
    public IReadOnlyList<NewProjectGroup> NewProjects { get; init; } = [];

    /// <summary>(Phase 9_3-01) 이번에 프로젝트를 옮길 대화 수.</summary>
    public int RelinkCount { get; init; }

    /// <summary>쓸 것(Import + Update + 연결 변경)이 0개인지.</summary>
    public bool HasNothingToWrite => NothingToWriteReason != ImportNothingToWriteReason.None;

    /// <summary>
    /// [가져오기]를 눌러도 되는지. 차단 사유가 없고 쓸 것이 있어야 한다. 쓸 것이 0개면 <see cref="NothingToWriteReason"/>이 이유다.
    /// </summary>
    public bool CanApply => BlockingReasons.Count == 0 && !HasNothingToWrite;
}

/// <summary>
/// 사용자 선택(<see cref="ImportUserChoices"/>)으로 이번 가져오기의 대화별 최종 동작과 요약을 계산한다(Phase 9_2-02, 설계 §6 "9_2").
/// </summary>
/// <remarks>
/// <para>
/// 순수 로직이다. 파일 I/O는 폴더 결정의 <see cref="Directory.Exists(string?)"/>뿐이고 backup을 다시 읽거나 해시하지 않는다 —
/// 체크를 바꿀 때마다 불러도 된다(설계 §3.2). <see cref="ImportPlanBuilder"/>의 선택 오버로드도 이 메서드의 결과로 Plan을 만든다
/// (요약과 Plan이 서로 다른 판단을 하지 않도록 같은 코드 경로를 쓴다).
/// </para>
/// <list type="bullet">
///   <item>선택 가능: New, IncomingAhead(<c>.jsonl.zst</c>가 아닐 때). 불가: Identical(9_3 전까지), LocalAhead, Diverged, Unverifiable, 조상 전용.</item>
///   <item>포함된 대화의 <see cref="ImportConversationPreview.RequiredAncestorThreadIds"/>는 자동 포함한다. 조상의 동작은 Preview 값이다.</item>
///   <item>closure 안에 Diverged/Unverifiable(또는 압축 rollout 이어받기) 조상이 있으면 적용할 수 없다. closure 밖 충돌은 막지 않는다.</item>
///   <item>closure 밖: LocalAhead는 Skip(LocalAhead, 기존 동작), 조상 전용은 Skip(NotSelectedDependencyNotNeeded), 그 밖은 Skip(UserExcluded).</item>
/// </list>
/// </remarks>
public static class ImportSelection
{
    /// <summary>선택 결과를 계산한다.</summary>
    public static ImportSelectionSummary Compute(ImportPreview preview, ImportUserChoices choices)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(choices);

        var warnings = new List<string>();
        var blocking = new List<string>();

        // 1) 프로젝트별 목적지.
        var projects = new List<ImportSelectionProject>(preview.Projects.Count);
        foreach (ImportProjectPreview project in preview.Projects)
        {
            string key = ImportUserChoices.ProjectKeyOf(project);
            ProjectTargetResolution resolution = ResolveTarget(project, choices.DecisionFor(key), preview.LocalProjectDirectory);
            if (resolution.Error is not null)
            {
                blocking.Add($"프로젝트 '{project.DisplayName}': {resolution.Error}");
            }

            projects.Add(new ImportSelectionProject(project, key, resolution));
        }

        // 2) 대화 목록(Plan과 같은 순서: 프로젝트 대화 → 조상 전용 대화).
        var ordered = new List<(ImportConversationPreview Conversation, ImportSelectionProject? Project)>();
        var byThreadId = new Dictionary<string, (ImportConversationPreview Conversation, ImportSelectionProject? Project)>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportSelectionProject project in projects)
        {
            foreach (ImportConversationPreview conversation in project.Preview.Conversations)
            {
                ordered.Add((conversation, project));
                byThreadId.TryAdd(conversation.ThreadId, (conversation, project));
            }
        }

        foreach (ImportConversationPreview dependency in preview.DependencyOnlyConversations)
        {
            ordered.Add((dependency, null));
            byThreadId.TryAdd(dependency.ThreadId, (dependency, null));
        }

        // 3) 사용자 포함 집합(선택 가능한 대화만).
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string threadId in choices.IncludedThreadIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            if (!byThreadId.TryGetValue(threadId, out var found))
            {
                warnings.Add($"백업에 없는 대화({threadId})는 선택에서 무시했습니다.");
                continue;
            }

            ImportUnselectableReason? reason = GetUnselectableReason(found.Conversation);
            if (reason is not null)
            {
                warnings.Add($"선택할 수 없는 대화({threadId}, {reason})는 선택에서 무시했습니다.");
                continue;
            }

            included.Add(found.Conversation.ThreadId);
        }

        // 4) 조상 closure + 차단 판정.
        var autoAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasBlockedInClosure = false;
        bool hasDivergedInClosure = false;
        foreach ((ImportConversationPreview conversation, _) in ordered)
        {
            if (!included.Contains(conversation.ThreadId))
            {
                continue;
            }

            foreach (string ancestorId in conversation.RequiredAncestorThreadIds)
            {
                if (!byThreadId.TryGetValue(ancestorId, out var ancestor))
                {
                    hasBlockedInClosure = true;
                    blocking.Add($"대화 {conversation.ThreadId}에 필요한 원본 대화 {ancestorId}가 백업에 없습니다.");
                    continue;
                }

                if (!included.Contains(ancestor.Conversation.ThreadId))
                {
                    autoAncestors.Add(ancestor.Conversation.ThreadId);
                }

                switch (ancestor.Conversation.PlannedAction)
                {
                    case ImportPlannedAction.RequiresDecision:
                        hasDivergedInClosure = true;
                        blocking.Add($"대화 {conversation.ThreadId}에 필요한 원본 대화 {ancestorId}가 양쪽에서 다르게 이어져(충돌) 함께 가져올 수 없습니다.");
                        break;
                    case ImportPlannedAction.Blocked:
                        hasBlockedInClosure = true;
                        blocking.Add($"대화 {conversation.ThreadId}에 필요한 원본 대화 {ancestorId}를 이 PC에서 확인할 수 없어(확인 불가) 함께 가져올 수 없습니다.");
                        break;
                    case ImportPlannedAction.Update when ancestor.Conversation.IsCompressedRollout:
                        hasBlockedInClosure = true;
                        blocking.Add($"대화 {conversation.ThreadId}에 필요한 원본 대화 {ancestorId}가 압축된 기록이라 이어받을 수 없습니다.");
                        break;
                }
            }
        }

        // 5) 대화별 최종 동작 + (Phase 9_3) 연결 변경.
        var relinkRequested = new HashSet<string>(choices.RelinkThreadIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<ImportSelectionConversation>(ordered.Count);
        foreach ((ImportConversationPreview conversation, ImportSelectionProject? project) in ordered)
        {
            bool isIncluded = included.Contains(conversation.ThreadId);
            bool isAuto = !isIncluded && autoAncestors.Contains(conversation.ThreadId);
            (ImportPlannedAction action, ImportSkipReason skipReason) = (isIncluded || isAuto)
                ? (conversation.PlannedAction, conversation.Relation == RevisionRelation.LocalAhead ? ImportSkipReason.LocalAhead : ImportSkipReason.None)
                : (ImportPlannedAction.Skip, ExcludedSkipReason(conversation));

            RelinkStatus relink = project is null
                ? RelinkStatus.NotApplicable
                : GetRelinkStatus(conversation, project.Resolution, preview.LocalProjectDirectory);
            bool relinkRequestedHere = relinkRequested.Remove(conversation.ThreadId);
            bool relinkSelected = relinkRequestedHere && relink == RelinkStatus.Available;
            if (relinkRequestedHere && !relinkSelected)
            {
                warnings.Add($"옮길 수 없는 대화({conversation.ThreadId}, {relink})는 옮기기 선택에서 무시했습니다.");
            }

            results.Add(new ImportSelectionConversation(
                conversation, project?.ProjectKey, project?.Target, action, skipReason,
                GetUnselectableReason(conversation), isIncluded, isAuto)
            {
                Relink = relink,
                IsRelinkSelected = relinkSelected,
            });
        }

        foreach (string unknown in relinkRequested.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"백업에 없는 대화({unknown})는 옮기기 선택에서 무시했습니다.");
        }

        // 6) 요약.
        int importCount = results.Count(r => r.FinalAction == ImportPlannedAction.Import);
        int updateCount = results.Count(r => r.FinalAction == ImportPlannedAction.Update);
        int noOpCount = results.Count(r => r.FinalAction == ImportPlannedAction.NoOp);
        int skipCount = results.Count(r => r.FinalAction == ImportPlannedAction.Skip);
        int uncategorizedImports = results.Count(r =>
            r.FinalAction == ImportPlannedAction.Import && (r.Target is null || r.Target.Kind == ProjectTargetKind.Uncategorized));

        int relinkCount = results.Count(r => r.IsRelinkSelected);
        ImportNothingToWriteReason nothingReason = importCount + updateCount + relinkCount > 0
            ? ImportNothingToWriteReason.None
            : DescribeNothingToWrite(results, included.Count);

        // 7) (Phase 9_5-01) 새로 만들 프로젝트: 새로 가져올 대화가 실제로 들어가는 CreateNew 목적지만, 같은 루트는 하나로.
        //    (Phase 9_3-05) 옮길 대화가 가는 CreateNew 목적지도 만든다. 이어받기만 가는 목적지로는 여전히 만들지 않는다.
        var importingKeys = new HashSet<string>(
            results.Where(r => (r.FinalAction == ImportPlannedAction.Import || r.IsRelinkSelected) && r.ProjectKey is not null).Select(r => r.ProjectKey!),
            StringComparer.Ordinal);
        IReadOnlyList<NewProjectGroup> newProjects = NewProjectGrouping.Group(
            projects.Where(p => importingKeys.Contains(p.ProjectKey) && p.Resolution.Error is null).Select(p => (p.ProjectKey, p.Target)));
        // Phase 9_5-T4 — 스키마 게이트 실패로 새 프로젝트를 만들 수 없는 목적지가 이번 가져오기에 쓰이면 알린다(Apply는 막지 않는다).
        int unsupported = projects.Count(p => importingKeys.Contains(p.ProjectKey) && p.Target.Reason == ProjectTargetReason.CreationUnsupported);
        if (unsupported > 0)
        {
            warnings.Add($"이 PC Codex의 프로젝트 저장 형식이 확인한 형태와 달라 새 프로젝트를 만들지 않습니다. 프로젝트 {unsupported}개의 대화는 기타 대화로 들어갑니다.");
        }

        foreach (NewProjectGroup group in newProjects.Where(g => g.HasConflictingNames))
        {
            warnings.Add($"같은 폴더를 쓰는 프로젝트 {group.ProjectKeys.Count}개를 새 프로젝트 하나('{group.Name}')로 합칩니다.");
        }

        return new ImportSelectionSummary(
            projects, results, importCount, updateCount, noOpCount, skipCount, autoAncestors.Count, uncategorizedImports,
            blocking, warnings, nothingReason, hasBlockedInClosure, hasDivergedInClosure)
        {
            NewProjects = newProjects,
            RelinkCount = relinkCount,
        };
    }

    /// <summary>
    /// (Phase 9_3-00) 이 대화를 그 백업 프로젝트의 목적지로 옮길 수 있는지(순수 규칙, 설계 §6 "9_3 V1 확정 규칙").
    /// </summary>
    /// <param name="conversation">백업 대화(이 PC 값과 Desktop 기록은 Preview에 있다).</param>
    /// <param name="resolution">그 백업 프로젝트의 목적지 판정(사용자 결정 반영).</param>
    /// <param name="directory">
    /// (Phase 9_3-07) 이 PC 프로젝트 목록. 주면 카탈로그 기준 현재 그룹(DB project_id → global-state → cwd 대체, 9_1 규칙)이 목적지 프로젝트와
    /// 같을 때도 AlreadyThere다(Desktop이 이미 그 프로젝트 아래에 보여 주는 대화).
    /// </param>
    public static RelinkStatus GetRelinkStatus(ImportConversationPreview conversation, ProjectTargetResolution resolution, ProjectDirectory? directory = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(resolution);

        if (!conversation.IsSelected || conversation.LocalLocation is not { ExistsLocally: true } location)
        {
            return RelinkStatus.NotApplicable;
        }

        if (location.Archived)
        {
            return RelinkStatus.Archived;
        }

        if (conversation.Relation is not (RevisionRelation.Identical or RevisionRelation.IncomingAhead or RevisionRelation.LocalAhead))
        {
            return conversation.Relation == RevisionRelation.New ? RelinkStatus.NotApplicable : RelinkStatus.RelationNotAllowed;
        }

        ProjectTarget target = resolution.Target;
        bool isProject = resolution.Error is null &&
                         (target.Kind == ProjectTargetKind.CreateNew ||
                          (target.Kind == ProjectTargetKind.LinkExisting && !string.IsNullOrWhiteSpace(target.LinkDbProjectId)));
        if (!isProject)
        {
            return RelinkStatus.TargetNotProject;
        }

        if (target.Kind == ProjectTargetKind.LinkExisting &&
            (string.Equals(conversation.LocalDbProjectId, target.LinkDbProjectId, StringComparison.Ordinal) ||
             (location.KnownProjectKey is { } currentKey && directory?.FindById(target.LinkDbProjectId)?.Key is { } targetKey &&
              string.Equals(currentKey, targetKey, StringComparison.Ordinal))))
        {
            return RelinkStatus.AlreadyThere;
        }

        return conversation.DesktopPlacement switch
        {
            DesktopPlacementStatus.NotRecorded => RelinkStatus.Available,
            DesktopPlacementStatus.Assigned => RelinkStatus.DesktopAssigned,
            DesktopPlacementStatus.Projectless => RelinkStatus.DesktopProjectless,
            _ => RelinkStatus.DesktopStateUnavailable,
        };
    }

    /// <summary>
    /// 사용자가 이 대화를 체크할 수 없는 이유(Phase 9_2-02, 설계 §7.4). 체크할 수 있으면 <c>null</c>.
    /// </summary>
    public static ImportUnselectableReason? GetUnselectableReason(ImportConversationPreview conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!conversation.IsSelected)
        {
            return ImportUnselectableReason.DependencyOnly;
        }

        return conversation.Relation switch
        {
            RevisionRelation.New => null,
            RevisionRelation.IncomingAhead => conversation.IsCompressedRollout ? ImportUnselectableReason.CompressedUpdate : null,
            RevisionRelation.Identical => ImportUnselectableReason.AlreadyPresent,
            RevisionRelation.LocalAhead => ImportUnselectableReason.LocalAhead,
            RevisionRelation.Diverged => ImportUnselectableReason.Diverged,
            _ => ImportUnselectableReason.Unverifiable,
        };
    }

    /// <summary>
    /// 백업 프로젝트 하나의 목적지를 사용자 결정으로 다시 판정한다(Phase 9_2-06). UI가 폴더를 바꿀 때 부르는 순수 함수다 —
    /// Plan을 만들지 않고 메모리와 <see cref="Directory.Exists(string?)"/>만 쓴다.
    /// </summary>
    /// <param name="project">백업 프로젝트 Preview.</param>
    /// <param name="decision">사용자 결정.</param>
    /// <param name="directory">이 PC의 프로젝트 목록(보통 <see cref="ImportPreview.LocalProjectDirectory"/>).</param>
    public static ProjectTargetResolution ResolveTarget(ImportProjectPreview project, ProjectTargetDecision decision, ProjectDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(directory);

        ProjectTarget suggested = project.SuggestedTarget ?? SuggestionFromMapping(project, directory);
        ProjectTargetResolution Suggestion(string? error) => new(suggested, project.PathMapping.Status, error);

        // Phase 9_5-11 — 백업의 "기타 대화" 그룹은 서로 관계없는 채팅 모음이라 폴더(=한 프로젝트)를 지정하지 않는다. 목적지는 항상
        // Uncategorized(NotApplicable)이고, 폴더·새 프로젝트 이름 결정은 결정 오류로 거부한다(화면은 그런 결정을 만들지 않는다).
        if (project.ProjectId is null)
        {
            ProjectTarget none = ProjectTarget.Uncategorized(ProjectTargetReason.NotApplicable, null);
            return !decision.UseSuggestion || decision.NewProjectName is not null
                ? new ProjectTargetResolution(none, ProjectPathMappingStatus.NotApplicable, UncategorizedGroupDecisionError)
                : new ProjectTargetResolution(none, ProjectPathMappingStatus.NotApplicable, null);
        }

        if (decision.UseSuggestion)
        {
            return ApplyCreationDecision(suggested, project.PathMapping.Status, decision);
        }

        if (string.IsNullOrWhiteSpace(decision.FolderPath))
        {
            return Suggestion("폴더가 지정되지 않았습니다.");
        }

        if (!Directory.Exists(decision.FolderPath))
        {
            return Suggestion("지정한 폴더가 없습니다.");
        }

        return ApplyCreationDecision(
            ProjectTargetResolver.ResolveFolder(decision.FolderPath, userSelected: true, directory),
            ProjectPathMappingStatus.ManuallyLinked,
            decision);
    }

    /// <summary>
    /// (Phase 9_5-01) 새 프로젝트 목적지에 사용자 결정(이름, 만들지 않기)을 적용한다. 새 프로젝트가 아니면 그대로다.
    /// </summary>
    private static ProjectTargetResolution ApplyCreationDecision(ProjectTarget target, ProjectPathMappingStatus status, ProjectTargetDecision decision)
    {
        if (target.Kind != ProjectTargetKind.CreateNew)
        {
            return new ProjectTargetResolution(target, status, null);
        }

        if (!decision.CreateProject)
        {
            // "새 프로젝트를 만들지 않고 기타 대화로" — 폴더와 사유는 남겨 화면이 무엇을 끈 것인지 보여줄 수 있게 한다.
            return new ProjectTargetResolution(ProjectTarget.Uncategorized(target.Reason, target.FolderPath), status, null);
        }

        if (decision.NewProjectName is null)
        {
            return new ProjectTargetResolution(target, status, null);
        }

        string name = decision.NewProjectName.Trim();
        return name.Length == 0
            ? new ProjectTargetResolution(target, status, EmptyProjectNameError)
            : new ProjectTargetResolution(target with { NewProjectName = name }, status, null);
    }

    /// <summary>(Phase 9_5-11) 백업 "기타 대화" 그룹에 폴더·이름 결정이 들어왔을 때의 결정 오류.</summary>
    public const string UncategorizedGroupDecisionError = "백업의 기타 대화에는 작업 폴더를 지정할 수 없습니다.";

    /// <summary>새 프로젝트 이름이 비었을 때의 결정 오류.</summary>
    public const string EmptyProjectNameError = "새 프로젝트 이름을 입력해 주세요.";

    private static ProjectTarget SuggestionFromMapping(ImportProjectPreview project, ProjectDirectory directory)
        => project.PathMapping.Status == ProjectPathMappingStatus.NotApplicable
            ? ProjectTarget.Uncategorized(ProjectTargetReason.NotApplicable, null)
            : ProjectTargetResolver.Resolve(project.PathMapping.OriginalRootPaths, null, directory);

    private static ImportSkipReason ExcludedSkipReason(ImportConversationPreview conversation)
    {
        if (!conversation.IsSelected)
        {
            return ImportSkipReason.NotSelectedDependencyNotNeeded;
        }

        return conversation.Relation == RevisionRelation.LocalAhead ? ImportSkipReason.LocalAhead : ImportSkipReason.UserExcluded;
    }

    private static ImportNothingToWriteReason DescribeNothingToWrite(List<ImportSelectionConversation> results, int includedCount)
    {
        List<ImportSelectionConversation> selected = results.Where(r => r.Preview.IsSelected).ToList();
        if (selected.Any(r => r.IsSelectable))
        {
            return includedCount == 0 ? ImportNothingToWriteReason.NothingSelected : ImportNothingToWriteReason.NoneSelectable;
        }

        return selected.Count > 0 && selected.All(r => r.Preview.Relation is RevisionRelation.Identical or RevisionRelation.LocalAhead)
            ? ImportNothingToWriteReason.AllAlreadyPresent
            : ImportNothingToWriteReason.NoneSelectable;
    }
}
