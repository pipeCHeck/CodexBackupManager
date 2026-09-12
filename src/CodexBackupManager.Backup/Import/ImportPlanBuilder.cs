using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <see cref="ImportPreview"/>를 <see cref="ImportPlan"/>으로 freeze한다(Phase 06_01 요구사항 4,
/// Phase 06_02에서 backup identity/precondition 강화). 여기서 UI 트리를 다시 해석하거나 계획을
/// 다시 정하지 않는다 — Preview가 이미 확정한 <see cref="ImportConversationPreview.PlannedAction"/>과
/// 실제로 비교에 쓰인 <see cref="ImportConversationPreview.LocalRevision"/>/
/// <see cref="ImportConversationPreview.IncomingRevision"/>을 그대로 옮겨 담을 뿐이다. Codex에는
/// 아무것도 쓰지 않는다.
/// </summary>
public static class ImportPlanBuilder
{
    /// <summary>
    /// Plan을 만든다. <paramref name="preview"/>가 검증에 실패했으면(<see cref="ImportPreview.Success"/>가
    /// <c>false</c>) freeze할 대상이 없으므로 <c>null</c>을 돌려준다.
    /// </summary>
    /// <remarks>
    /// backup 파일 전체의 SHA-256을 스트리밍으로 계산해 <see cref="ImportBackupIdentity"/>에 담는다
    /// (Phase 06_02 — 요구사항 1). 파일 전체를 메모리에 올리지 않는다.
    /// </remarks>
    public static ImportPlan? Build(
        ImportPreview preview, string backupFilePath, CancellationToken cancellationToken = default)
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

        ImportBackupIdentity identity = BuildBackupIdentity(preview, backupFilePath, cancellationToken);

        return new ImportPlan(identity, projects, conversations, hasBlockingIssues, hasUnresolvedDivergence);
    }

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

    /// <summary>
    /// backup 파일 전체를 스트리밍으로 다시 읽어 길이+SHA-256을 계산한다(요구사항 1 — 파일 전체를
    /// 메모리에 올리지 않는다). 이게 Phase 7 preflight가 "Preview했던 그 파일인지" 증명하는 유일한
    /// source of truth다.
    /// </summary>
    private static ImportBackupIdentity BuildBackupIdentity(
        ImportPreview preview, string backupFilePath, CancellationToken cancellationToken)
    {
        using FileStream stream = new(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        StreamingHashCopy.Result hashed = StreamingHashCopy.HashOnly(stream, cancellationToken);

        return new ImportBackupIdentity(
            backupFilePath,
            hashed.ByteLength,
            hashed.Sha256Hex,
            preview.Manifest!.BackupFormatVersion,
            preview.Manifest.CreatedAtUtc,
            preview.Manifest.AppVersion,
            preview.Manifest.ConversationCount);
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
