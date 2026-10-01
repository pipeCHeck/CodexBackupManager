using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <see cref="ImportPreview"/>를 <see cref="ImportPlan"/>으로 freeze한다(Phase 06_01 요구사항 4,
/// Phase 06_02에서 backup identity/precondition 강화, Phase 06_03에서 Preview↔Plan TOCTOU 방지).
/// 여기서 UI 트리를 다시 해석하거나 계획을 다시 정하지 않는다 — Preview가 이미 확정한
/// <see cref="ImportConversationPreview.PlannedAction"/>과 실제로 비교에 쓰인
/// <see cref="ImportConversationPreview.LocalRevision"/>/<see cref="ImportConversationPreview.IncomingRevision"/>을
/// 그대로 옮겨 담을 뿐이다. Codex에는 아무것도 쓰지 않는다.
/// </summary>
public static class ImportPlanBuilder
{
    /// <summary>
    /// Plan을 만든다. <paramref name="preview"/>가 검증에 실패했으면(<see cref="ImportPreview.Success"/>가
    /// <c>false</c>) freeze할 대상이 없으므로 <c>null</c>을 돌려준다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// (Phase 06_03) <b>backup identity를 여기서 새로 "채택"하지 않는다.</b> Preview가 분석을 마친
    /// 직후 고정해 둔 <see cref="ImportPreview.SourceBackupIdentity"/>가 있어야 하고,
    /// <paramref name="backupFilePath"/>를 지금 다시 streaming hash해서 그 값과 길이+SHA-256이
    /// 정확히 같을 때만 Plan을 만든다. Preview와 이 호출 사이에 같은 경로의 파일이 다른 내용(같은
    /// 길이/CreatedAt/AppVersion이어도 hash가 다르면 다른 파일)으로 바뀌었으면, "그 새 파일을
    /// 현재 source로 다시 freeze"하지 않고 Plan 생성 자체를 거부(<c>null</c>)한다 — 사용자가 다시
    /// <c>[백업 불러오기]</c>부터 해야 한다.
    /// </para>
    /// </remarks>
    public static ImportPlan? Build(
        ImportPreview preview, string backupFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);

        if (!preview.Success || preview.Manifest is null || preview.SourceBackupIdentity is not { } sourceIdentity)
        {
            return null;
        }

        // Preview가 실제로 검증/분석했던 그 바이트가 지금도 그대로인지 확인한다 — 다르면 이 Plan은
        // 서로 다른 source(Preview는 A, 지금 파일은 B)를 섞은 것이 되므로 만들지 않는다.
        if (!BackupIdentityHasher.Matches(sourceIdentity, backupFilePath, cancellationToken))
        {
            return null;
        }

        var projects = new List<ImportPlanProject>();
        var conversations = new List<ImportPlanConversation>();
        bool hasBlockingIssues = false;
        bool hasUnresolvedDivergence = false;

        foreach (ImportProjectPreview project in preview.Projects)
        {
            string? targetPath = project.PathMapping.ResolvedLocalPath;
            projects.Add(new ImportPlanProject(project.ProjectId, project.DisplayName, project.PathMapping.Status, targetPath)
            {
                ResolvedTarget = WithoutCreation(project.SuggestedTarget),
            });

            foreach (ImportConversationPreview conversation in project.Conversations)
            {
                conversations.Add(BuildPlanConversation(conversation, targetPath));
                UpdateBlockingFlags(conversation.PlannedAction, ref hasBlockingIssues, ref hasUnresolvedDivergence);
            }
        }

        // dependency-only(조상) 대화는 어떤 project 그룹에도 들어있지 않지만, Phase 7이 실제로
        // 파일을 써야 할 대상이므로 Plan에서 빠지면 안 된다 — 경로는 없다(프로젝트에 속하지 않는다).
        foreach (ImportConversationPreview dependency in preview.DependencyOnlyConversations)
        {
            conversations.Add(BuildPlanConversation(dependency, targetProjectPath: null));
            UpdateBlockingFlags(dependency.PlannedAction, ref hasBlockingIssues, ref hasUnresolvedDivergence);
        }

        // Plan의 identity는 방금 재확인한 sourceIdentity 그 자체다 — 다시 계산하지 않는다(같은
        // 값임이 이미 확인됐다).
        return new ImportPlan(sourceIdentity, projects, conversations, hasBlockingIssues, hasUnresolvedDivergence);
    }

    /// <summary>
    /// (Phase 9_2-04) 사용자 선택을 반영한 Plan을 만든다. <see cref="ImportSelection.Compute"/>의 결과를 그대로 옮기므로 화면 요약과
    /// Plan이 서로 다른 판단을 하지 않는다. backup identity pinning(06_03)은 선택 없는 오버로드와 같다.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>대화 순서와 사전조건(<see cref="ImportConversationPrecondition"/>)은 선택 없는 오버로드와 같다. 제외된 대화의
    ///     <see cref="ImportPlanConversation.PlannedAction"/>은 <see cref="ImportPlannedAction.Skip"/>이고 이유는 <see cref="ImportPlanConversation.SkipReason"/>이다.</item>
    ///   <item>프로젝트 <see cref="ImportPlanProject.ResolvedTarget"/>은 사용자 결정 기준이다. <see cref="ImportPlanProject.PathStatus"/>는 폴더를
    ///     골랐으면 ManuallyLinked, 아니면 Preview의 자동 판정 상태다. <see cref="ImportPlanProject.TargetProjectPath"/>와 대화의
    ///     <see cref="ImportPlanConversation.TargetProjectPath"/>는 LinkExisting이면 연결 루트, 아니면 <c>null</c>이다.</item>
    ///   <item><see cref="ImportPlan.HasBlockingIssues"/>/<see cref="ImportPlan.HasUnresolvedDivergence"/>는 closure 기준이다.</item>
    ///   <item>결정 오류(예: 빈 새 프로젝트 이름, 없는 폴더)가 있으면 Plan을 만들지 않는다(<c>null</c>).</item>
    ///   <item>(Phase 9_5) 새 프로젝트 목적지(<see cref="ProjectTargetKind.CreateNew"/>)는 <see cref="ImportPlanProject.ResolvedTarget"/>에 이름과 함께
    ///     freeze된다. <see cref="ImportPlanProject.TargetProjectPath"/>는 연결 루트 전용이라 <c>null</c>이다. 실제 생성 계획은 Restore Planner가 만든다.</item>
    /// </list>
    /// </remarks>
    public static ImportPlan? Build(
        ImportPreview preview, string backupFilePath, ImportUserChoices choices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        ArgumentNullException.ThrowIfNull(choices);

        if (!preview.Success || preview.Manifest is null || preview.SourceBackupIdentity is not { } sourceIdentity)
        {
            return null;
        }

        ImportSelectionSummary selection = ImportSelection.Compute(preview, choices);
        if (selection.Projects.Any(p => p.Resolution.Error is not null))
        {
            return null;
        }

        if (!BackupIdentityHasher.Matches(sourceIdentity, backupFilePath, cancellationToken))
        {
            return null;
        }

        var projects = new List<ImportPlanProject>(selection.Projects.Count);
        var targetPathByKey = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ImportSelectionProject project in selection.Projects)
        {
            string? targetPath = project.Target.Kind == ProjectTargetKind.LinkExisting ? project.Target.FolderPath : null;
            targetPathByKey.TryAdd(project.ProjectKey, targetPath);
            projects.Add(new ImportPlanProject(
                project.Preview.ProjectId, project.Preview.DisplayName, project.Resolution.PathStatus, targetPath)
            {
                ResolvedTarget = project.Target,
            });
        }

        var conversations = new List<ImportPlanConversation>(selection.Conversations.Count);
        foreach (ImportSelectionConversation conversation in selection.Conversations)
        {
            string? targetPath = conversation.ProjectKey is { } key && targetPathByKey.TryGetValue(key, out string? path) ? path : null;
            conversations.Add(BuildPlanConversation(conversation.Preview, targetPath) with
            {
                PlannedAction = conversation.FinalAction,
                SkipReason = conversation.SkipReason,
                TargetProjectKey = conversation.ProjectKey,
            });
        }

        // Phase 9_3-02 — 연결 변경(계획 시점의 이 PC 값을 고정한다. Apply 때 다르면 거부한다).
        List<ImportPlanRelink> relinks = selection.Conversations
            .Where(c => c.IsRelinkSelected && c.ProjectKey is not null)
            .Select(c => new ImportPlanRelink(c.ThreadId, c.Preview.LocalDbProjectId, c.Preview.LocalCwd, c.ProjectKey!))
            .ToList();

        return new ImportPlan(
            sourceIdentity, projects, conversations, selection.HasBlockingIssues, selection.HasUnresolvedDivergence)
        {
            UserChoices = choices,
            Relinks = relinks,
        };
    }

    /// <summary>
    /// (Phase 9_5, 설계 §8-9) 선택 없는 이전 방식 Plan은 0.1.3과 같은 결과여야 한다 — 새 프로젝트 제안은 만들지 않고
    /// 기타 대화(같은 사유와 폴더)로 둔다. 새 프로젝트는 사용자 선택 Plan에서만 만든다.
    /// </summary>
    private static ProjectTarget? WithoutCreation(ProjectTarget? target)
        => target is { Kind: ProjectTargetKind.CreateNew }
            ? ProjectTarget.Uncategorized(target.Reason, target.FolderPath)
            : target;

    private static ImportPlanConversation BuildPlanConversation(ImportConversationPreview conversation, string? targetProjectPath)
    {
        ExpectedLocalPresence expectedPresence = conversation.Relation == RevisionRelation.New
            ? ExpectedLocalPresence.MustNotExist
            : ExpectedLocalPresence.MustExist;

        var precondition = new ImportConversationPrecondition(
            expectedPresence, conversation.LocalRevision, conversation.IncomingRevision);

        return new ImportPlanConversation(
            conversation.ThreadId, conversation.IsSelected, conversation.Relation, conversation.PlannedAction,
            targetProjectPath, precondition);
    }

    private static void UpdateBlockingFlags(ImportPlannedAction action, ref bool hasBlockingIssues, ref bool hasUnresolvedDivergence)
    {
        // Unverifiable(Blocked)은 항상 blocking이다 — 예외 없음(요구사항).
        if (action == ImportPlannedAction.Blocked)
        {
            hasBlockingIssues = true;
        }

        // Diverged(RequiresDecision)는 사용자 결정 메커니즘이 아직 없으므로 Apply-ready를 막는다.
        if (action == ImportPlannedAction.RequiresDecision)
        {
            hasUnresolvedDivergence = true;
        }
    }
}
