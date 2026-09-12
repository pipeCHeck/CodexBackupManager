using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <see cref="ImportPreview"/>를 <see cref="ImportPlan"/>으로 freeze한다(Phase 06_01 요구사항 4).
/// 여기서 UI 트리를 다시 해석하거나 계획을 다시 정하지 않는다 — Preview가 이미 확정한
/// <see cref="ImportConversationPreview.PlannedAction"/>을 그대로 옮겨 담을 뿐이다. Codex에는
/// 아무것도 쓰지 않는다.
/// </summary>
public static class ImportPlanBuilder
{
    /// <summary>
    /// Plan을 만든다. <paramref name="preview"/>가 검증에 실패했으면(<see cref="ImportPreview.Success"/>가
    /// <c>false</c>) freeze할 대상이 없으므로 <c>null</c>을 돌려준다.
    /// </summary>
    public static ImportPlan? Build(ImportPreview preview, string backupFilePath)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);

        if (!preview.Success || preview.Manifest is null)
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
            projects.Add(new ImportPlanProject(project.ProjectId, project.DisplayName, project.PathMapping.Status, targetPath));

            foreach (ImportConversationPreview conversation in project.Conversations)
            {
                conversations.Add(new ImportPlanConversation(
                    conversation.ThreadId, conversation.IsSelected, conversation.Relation, conversation.PlannedAction, targetPath));

                UpdateBlockingFlags(conversation.PlannedAction, ref hasBlockingIssues, ref hasUnresolvedDivergence);
            }
        }

        // dependency-only(조상) 대화는 어떤 project 그룹에도 들어있지 않지만, Phase 7이 실제로
        // 파일을 써야 할 대상이므로 Plan에서 빠지면 안 된다 — 경로는 없다(프로젝트에 속하지 않는다).
        foreach (ImportConversationPreview dependency in preview.DependencyOnlyConversations)
        {
            conversations.Add(new ImportPlanConversation(
                dependency.ThreadId, dependency.IsSelected, dependency.Relation, dependency.PlannedAction, TargetProjectPath: null));

            UpdateBlockingFlags(dependency.PlannedAction, ref hasBlockingIssues, ref hasUnresolvedDivergence);
        }

        var identity = new ImportBackupIdentity(
            backupFilePath, preview.Manifest.CreatedAtUtc, preview.Manifest.AppVersion, preview.Manifest.ConversationCount);

        return new ImportPlan(identity, projects, conversations, hasBlockingIssues, hasUnresolvedDivergence);
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
