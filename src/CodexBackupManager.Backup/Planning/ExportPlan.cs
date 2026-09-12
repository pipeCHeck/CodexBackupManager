using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Backup.Planning;

/// <summary>
/// <see cref="ExportPlanBuilder"/>가 계산한, 실제 I/O를 하기 전의 "무엇을 Export할지" 계획.
/// </summary>
/// <param name="Conversations">선택된 대화 + dependency-only 조상 대화 전체(뿌리 순서 아님, 중복 없음).</param>
/// <param name="RolloutFiles">복사해야 할 rollout 파일 전체(여러 대화가 공유해도 한 번만).</param>
/// <param name="Attachments">복사해야 할 첨부(local_image) 파일 전체.</param>
/// <param name="Projects">선택된 대화가 속한 프로젝트 그룹.</param>
/// <param name="Warnings">
/// 계획 단계에서 발견한 <b>선택적(optional)</b> 문제 — 누락된 attachment 파일 등. 이런 문제가
/// 있어도 Export는 성공 처리한다(요구사항: "optional attachment 누락은 warning으로 허용 가능").
/// 사용자 원문 없음.
/// </param>
/// <param name="FatalErrors">
/// 선택한 대화를 <b>완전하게</b> 백업할 수 없게 만드는 문제(체인 없음/조상 rollout 없음/순환 참조
/// 등). 하나라도 있으면 <see cref="Writing.BackupWriter"/>는 아예 temp 파일도 만들지 않고 즉시
/// 실패 처리한다 — "부분 성공"을 성공으로 위장하지 않는다(Phase 05_01).
/// </param>
public sealed record ExportPlan(
    IReadOnlyList<PlannedConversation> Conversations,
    IReadOnlyList<PlannedPayloadFile> RolloutFiles,
    IReadOnlyList<PlannedAttachment> Attachments,
    IReadOnlyList<PlannedProject> Projects,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> FatalErrors)
{
    /// <summary>사용자가 실제로 선택한 대화 수.</summary>
    public int SelectedConversationCount => Count(true);

    /// <summary>선택되지 않았지만 체인 복원을 위해 포함된 조상 대화 수.</summary>
    public int DependencyConversationCount => Count(false);

    private int Count(bool selected)
    {
        int count = 0;
        foreach (PlannedConversation c in Conversations)
        {
            if (c.IsSelected == selected)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>계획에 포함된 대화(thread) 하나.</summary>
/// <param name="ThreadId">thread ID.</param>
/// <param name="IsSelected">사용자가 실제로 선택했는지(false=dependency-only 조상).</param>
/// <param name="Entry">
/// 카탈로그에서 찾은 메타데이터. state DB에 행이 없는 고아 thread(원본 손상 등)라면 <c>null</c> —
/// 그래도 파일은 포함하되 metadata는 채우지 않는다(추측하지 않는다).
/// </param>
/// <param name="RolloutFiles">이 thread(체인)를 재구성하는 데 필요한 rollout 파일(시간순).</param>
/// <param name="AttachmentAbsolutePaths">이 thread가 참조하는 첨부(local_image)의 원본 절대경로.</param>
public sealed record PlannedConversation(
    string ThreadId,
    bool IsSelected,
    Domain.Codex.Catalog.ConversationEntry? Entry,
    IReadOnlyList<RolloutFileReference> RolloutFiles,
    IReadOnlyList<string> AttachmentAbsolutePaths);

/// <summary>계획에 포함된 rollout payload 파일 하나(dedupe 완료).</summary>
/// <param name="SourceFullPath">원본 절대경로.</param>
/// <param name="EntryPath">ZIP 안의 상대경로(<c>payload/rollouts/…</c>).</param>
public sealed record PlannedPayloadFile(string SourceFullPath, string EntryPath);

/// <summary>계획에 포함된 첨부(local_image) 파일 하나(dedupe 완료, 실존 확인됨).</summary>
/// <param name="SourceFullPath">원본 절대경로.</param>
/// <param name="EntryPath">ZIP 안의 상대경로(<c>payload/attachments/…</c>).</param>
public sealed record PlannedAttachment(string SourceFullPath, string EntryPath);

/// <summary>계획에 포함된 프로젝트 그룹(선택된 대화 기준).</summary>
/// <param name="ProjectId">프로젝트 ID. 미분류면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름.</param>
/// <param name="OriginalRootPaths">알려진 원본 루트 경로.</param>
/// <param name="SelectedThreadIds">이 프로젝트에 속한, 실제로 선택된 thread ID.</param>
public sealed record PlannedProject(
    string? ProjectId,
    string DisplayName,
    IReadOnlyList<string> OriginalRootPaths,
    IReadOnlyList<string> SelectedThreadIds);
