using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 어떤 <c>.codexbackup</c>에서 만들어진 <see cref="ImportPreview"/>를 식별하기 위한 정보(Phase 06_02
/// 강화). Phase 7이 실제 write를 시작하기 전에 "지금 이 파일이 정말 Preview했던 그 파일인지"를
/// 증명할 수 있어야 한다 — 파일 경로만으로는 그 사이 다른 파일로 교체됐는지 구분할 수 없다.
/// </summary>
/// <remarks>
/// <b>source of truth는 <see cref="BackupFileSha256"/>(+ <see cref="BackupFileLength"/>) 하나다.</b>
/// <see cref="CreatedAtUtc"/>/<see cref="AppVersion"/>/<see cref="TotalConversationCount"/>는 참고용
/// 진단 정보일 뿐이고(우연히 전부 같아도 hash가 다르면 다른 파일이다 — RED→GREEN 테스트로 확인),
/// <see cref="BackupFormatVersion"/>은 preflight가 재검증할 때 쓰는 <see cref="Validation.BackupValidator"/>가
/// 이미 다시 확인하므로 identity 비교에는 직접 쓰지 않는다(정보용으로만 보존).
/// </remarks>
/// <param name="BackupFilePath">Plan을 만들 때 사용한 <c>.codexbackup</c> 경로.</param>
/// <param name="BackupFileLength">Export 파일 전체의 바이트 길이(빠른 1차 비교용).</param>
/// <param name="BackupFileSha256">
/// Export 파일 전체(ZIP 컨테이너 자체)의 SHA-256(소문자 hex). 스트리밍으로 계산했다 — 파일 전체를
/// 메모리에 올리지 않는다(<see cref="Container.StreamingHashCopy.HashOnly"/>).
/// </param>
/// <param name="BackupFormatVersion">manifest의 <c>backupFormatVersion</c>(정보용).</param>
/// <param name="CreatedAtUtc">backup manifest의 Export 시각(정보용).</param>
/// <param name="AppVersion">backup을 만든 앱 버전(정보용).</param>
/// <param name="TotalConversationCount">manifest의 선택된 대화 수(정보용, <c>ConversationCount</c>).</param>
public sealed record ImportBackupIdentity(
    string BackupFilePath,
    long BackupFileLength,
    string BackupFileSha256,
    int BackupFormatVersion,
    DateTimeOffset CreatedAtUtc,
    string AppVersion,
    int TotalConversationCount);

/// <summary>
/// Preview 시점에 이 대화가 로컬에 어떤 상태였어야 하는지에 대한 기대값(Phase 06_02). Phase 7은
/// relation을 다시 판정하지 않고, <b>지금의 로컬 상태가 이 기대값과 정확히 같은지만 확인</b>한다.
/// </summary>
/// <param name="ExpectedPresence">
/// <see cref="ExpectedLocalPresence.MustNotExist"/>(New)면 지금도 로컬에 이 ThreadId가 전혀 없어야
/// 한다. <see cref="ExpectedLocalPresence.MustExist"/>면 <see cref="ExpectedLocalRevision"/>과
/// 정확히 같은 로컬 revision을 다시 계산할 수 있어야 한다.
/// </param>
/// <param name="ExpectedLocalRevision">
/// Preview 당시 실제로 비교에 쓰인 로컬 <see cref="ConversationRevision"/>. 계산할 수 없었으면
/// (New/Unverifiable) <c>null</c>. Phase 7은 이 값을 다시 판정 근거로 쓰지 않고, 지금 다시 만든
/// 로컬 revision과 <b>완전히 같은지</b>(RolloutId/Boundary/길이/해시 시퀀스) 비교만 한다.
/// </param>
/// <param name="ExpectedIncomingRevision">
/// Preview 당시 실제로 비교에 쓰인 backup 쪽 <see cref="ConversationRevision"/>(요구사항 4 — 특히
/// IncomingAhead의 fast-forward에 필요). 계산할 수 없었으면 <c>null</c>.
/// </param>
public sealed record ImportConversationPrecondition(
    ExpectedLocalPresence ExpectedPresence,
    ConversationRevision? ExpectedLocalRevision,
    ConversationRevision? ExpectedIncomingRevision);

/// <summary>Preview 시점에 로컬에 이 ThreadId가 존재했어야 하는지.</summary>
public enum ExpectedLocalPresence
{
    /// <summary>New — Preview 당시 로컬에 이 ThreadId가 전혀(metadata도 chain도) 없었다.</summary>
    MustNotExist = 0,

    /// <summary>
    /// New가 아닌 모든 관계 — Preview 당시 로컬에 이 ThreadId가 어떤 형태로든 존재했다(chain 유무와
    /// 무관하게, Unverifiable도 포함 — "존재는 하는데 안전하게 확인할 수 없었다"는 뜻이었으므로).
    /// </summary>
    MustExist = 1,
}

/// <summary>Import Plan에 고정된 대화(thread) 하나 — Phase 7이 이 값을 그대로 받아 적용한다.</summary>
/// <param name="ThreadId">thread ID.</param>
/// <param name="IsSelected">사용자가 실제로 선택했던 대화인지(<c>false</c>면 dependency-only 조상).</param>
/// <param name="Relation">확정된 conversation 내용 관계.</param>
/// <param name="PlannedAction">확정된 계획(New→Import, Identical→NoOp, IncomingAhead→Update, LocalAhead→Skip, Diverged→RequiresDecision, Unverifiable→Blocked).</param>
/// <param name="TargetProjectPath">
/// 이 대화가 속한 프로젝트의 현재 PC 기준 목표 경로(자동 연결 또는 수동 재지정 결과). 프로젝트가
/// 없거나(dependency-only/미분류) 아직 경로를 알 수 없으면 <c>null</c>.
/// </param>
/// <param name="Precondition">
/// Preview 당시 로컬 상태의 frozen 기대값(Phase 06_02) — Phase 7의 stale-plan 방지 근거.
/// </param>
public sealed record ImportPlanConversation(
    string ThreadId,
    bool IsSelected,
    RevisionRelation Relation,
    ImportPlannedAction PlannedAction,
    string? TargetProjectPath,
    ImportConversationPrecondition Precondition);

/// <summary>Import Plan에 고정된 프로젝트 하나.</summary>
/// <param name="ProjectId">backup 쪽 원본 프로젝트 ID. 미분류면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름.</param>
/// <param name="PathStatus">경로 매핑 상태(자동 연결/수동 재지정/미해결).</param>
/// <param name="TargetProjectPath">해결된 현재 PC 기준 경로. 미해결이면 <c>null</c>.</param>
public sealed record ImportPlanProject(
    string? ProjectId,
    string DisplayName,
    ProjectPathMappingStatus PathStatus,
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
