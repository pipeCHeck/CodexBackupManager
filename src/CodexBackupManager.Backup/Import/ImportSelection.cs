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
    /// <summary>쓸 것(Import + Update)이 0개인지.</summary>
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

        // 5) 대화별 최종 동작.
        var results = new List<ImportSelectionConversation>(ordered.Count);
        foreach ((ImportConversationPreview conversation, ImportSelectionProject? project) in ordered)
        {
            bool isIncluded = included.Contains(conversation.ThreadId);
            bool isAuto = !isIncluded && autoAncestors.Contains(conversation.ThreadId);
            (ImportPlannedAction action, ImportSkipReason skipReason) = (isIncluded || isAuto)
                ? (conversation.PlannedAction, conversation.Relation == RevisionRelation.LocalAhead ? ImportSkipReason.LocalAhead : ImportSkipReason.None)
                : (ImportPlannedAction.Skip, ExcludedSkipReason(conversation));

            results.Add(new ImportSelectionConversation(
                conversation, project?.ProjectKey, project?.Target, action, skipReason,
                GetUnselectableReason(conversation), isIncluded, isAuto));
        }

        // 6) 요약.
        int importCount = results.Count(r => r.FinalAction == ImportPlannedAction.Import);
        int updateCount = results.Count(r => r.FinalAction == ImportPlannedAction.Update);
        int noOpCount = results.Count(r => r.FinalAction == ImportPlannedAction.NoOp);
        int skipCount = results.Count(r => r.FinalAction == ImportPlannedAction.Skip);
        int uncategorizedImports = results.Count(r =>
            r.FinalAction == ImportPlannedAction.Import && (r.Target is null || r.Target.Kind == ProjectTargetKind.Uncategorized));

        ImportNothingToWriteReason nothingReason = importCount + updateCount > 0
            ? ImportNothingToWriteReason.None
            : DescribeNothingToWrite(results, included.Count);

        return new ImportSelectionSummary(
            projects, results, importCount, updateCount, noOpCount, skipCount, autoAncestors.Count, uncategorizedImports,
            blocking, warnings, nothingReason, hasBlockedInClosure, hasDivergedInClosure);
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

        if (decision.NewProjectName is not null)
        {
            return Suggestion("새 프로젝트 만들기는 아직 지원하지 않습니다.");
        }

        if (decision.UseSuggestion)
        {
            return Suggestion(null);
        }

        if (!project.PathMapping.CanManuallyOverride)
        {
            return Suggestion("기타 대화 그룹은 폴더를 지정할 수 없습니다.");
        }

        if (string.IsNullOrWhiteSpace(decision.FolderPath))
        {
            return Suggestion("폴더가 지정되지 않았습니다.");
        }

        if (!Directory.Exists(decision.FolderPath))
        {
            return Suggestion("지정한 폴더가 없습니다.");
        }

        return new ProjectTargetResolution(
            ProjectTargetResolver.ResolveFolder(decision.FolderPath, userSelected: true, directory),
            ProjectPathMappingStatus.ManuallyLinked,
            null);
    }

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
