using System;
using System.Collections.Generic;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 어떤 <c>.codexbackup</c>에서 만들어진 <see cref="ImportPreview"/>를 식별하기 위한 최소 정보.
/// Phase 7이 "이 Plan이 어떤 backup에서 나왔는지" 확인할 수 있는 정도로만 담는다 — manifest 전체를
/// 다시 들고 다니지 않는다.
/// </summary>
/// <param name="BackupFilePath">Plan을 만들 때 사용한 <c>.codexbackup</c> 경로.</param>
/// <param name="CreatedAtUtc">backup manifest의 Export 시각.</param>
/// <param name="AppVersion">backup을 만든 앱 버전.</param>
/// <param name="TotalConversationCount">manifest의 선택된 대화 수(<c>ConversationCount</c>).</param>
public sealed record ImportBackupIdentity(
    string BackupFilePath,
    DateTimeOffset CreatedAtUtc,
    string AppVersion,
    int TotalConversationCount);

/// <summary>Import Plan에 고정된 대화(thread) 하나 — Phase 7이 이 값을 그대로 받아 적용한다.</summary>
/// <param name="ThreadId">thread ID.</param>
/// <param name="IsSelected">사용자가 실제로 선택했던 대화인지(<c>false</c>면 dependency-only 조상).</param>
/// <param name="Relation">확정된 conversation 내용 관계.</param>
/// <param name="PlannedAction">확정된 계획(New→Import, Identical→NoOp, IncomingAhead→Update, LocalAhead→Skip, Diverged→RequiresDecision, Unverifiable→Blocked).</param>
/// <param name="TargetProjectPath">
/// 이 대화가 속한 프로젝트의 현재 PC 기준 목표 경로(자동 연결 또는 수동 재지정 결과). 프로젝트가
/// 없거나(dependency-only/미분류) 아직 경로를 알 수 없으면 <c>null</c>.
/// </param>
public sealed record ImportPlanConversation(
    string ThreadId,
    bool IsSelected,
    Domain.Codex.Import.RevisionRelation Relation,
    ImportPlannedAction PlannedAction,
    string? TargetProjectPath);

/// <summary>Import Plan에 고정된 프로젝트 하나.</summary>
/// <param name="ProjectId">backup 쪽 원본 프로젝트 ID. 미분류면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름.</param>
/// <param name="PathStatus">경로 매핑 상태(자동 연결/수동 재지정/미해결).</param>
/// <param name="TargetProjectPath">해결된 현재 PC 기준 경로. 미해결이면 <c>null</c>.</param>
public sealed record ImportPlanProject(
    string? ProjectId,
    string DisplayName,
    Domain.Codex.Import.ProjectPathMappingStatus PathStatus,
    string? TargetProjectPath);

/// <summary>
/// <see cref="ImportPreview"/>를 실제 Apply(Phase 7)가 받아 쓸 수 있는 형태로 <b>freeze</b>한 결과.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 7은 이 값을 그대로 받아 적용해야 한다 — UI 트리(ProjectNodes/ImportPreviewViewModel 등)를
/// 다시 해석하지 않는다.</b> Preview에서 이미 확정한 <see cref="ImportConversationPreview.PlannedAction"/>이
/// 이 레코드의 <see cref="ImportPlanConversation.PlannedAction"/>으로 그대로 옮겨진다 — Phase 7이
/// 관계를 다시 판정하거나 계획을 다시 정하지 않는다.
/// </para>
/// <para>
/// <b>이 타입 자체는 아무것도 적용하지 않는다.</b> <see cref="ImportPlanBuilder.Build"/>는 순수
/// 데이터 변환일 뿐이고, Codex에는 어떤 것도 쓰지 않는다(Phase 06_01 범위 — 실제 Apply는 Phase 7).
/// </para>
/// </remarks>
/// <param name="Backup">이 Plan이 어떤 backup에서 나왔는지.</param>
/// <param name="Projects">확정된 프로젝트 목록(경로 매핑 포함).</param>
/// <param name="Conversations">확정된 대화 목록(선택 + dependency-only 전부, 경로 매핑 포함).</param>
/// <param name="HasBlockingIssues">
/// <see cref="ImportPlannedAction.Blocked"/>(Unverifiable)가 하나라도 있는지. 이게 있으면 항상
/// Apply-ready가 아니다 — Unverifiable은 항상 blocking이다(요구사항).
/// </param>
/// <param name="HasUnresolvedDivergence">
/// <see cref="ImportPlannedAction.RequiresDecision"/>(Diverged)이 하나라도 있는지. Phase 06_01은
/// 분기 충돌에 대한 사용자 결정 메커니즘을 아직 만들지 않았으므로, 이게 있으면 Apply-ready가 아니다.
/// </param>
public sealed record ImportPlan(
    ImportBackupIdentity Backup,
    IReadOnlyList<ImportPlanProject> Projects,
    IReadOnlyList<ImportPlanConversation> Conversations,
    bool HasBlockingIssues,
    bool HasUnresolvedDivergence)
{
    /// <summary>
    /// Phase 7이 지금 바로 적용해도 안전한 상태인지. <see cref="HasBlockingIssues"/>와
    /// <see cref="HasUnresolvedDivergence"/> 둘 다 없어야 한다 — Diverged가 하나라도 있으면 Preview
    /// 자체는 가능해도(이 Plan은 만들어질 수 있다) Apply-ready는 아니다.
    /// </summary>
    public bool IsApplyReady => !HasBlockingIssues && !HasUnresolvedDivergence;
}
