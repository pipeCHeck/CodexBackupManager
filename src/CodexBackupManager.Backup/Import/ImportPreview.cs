using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// Phase 6이 Phase 7에서 실제로 어떤 동작을 할지에 대한 <b>계획</b>. 이 값 자체는 아직 아무것도
/// 적용하지 않는다 — Preview일 뿐이다.
/// </summary>
public enum ImportPlannedAction
{
    /// <summary>새로 Import(backup에만 있음).</summary>
    Import = 0,

    /// <summary>아무것도 하지 않는다(로컬과 완전히 동일).</summary>
    NoOp = 1,

    /// <summary>Fast-forward 갱신(backup이 로컬보다 진행됨).</summary>
    Update = 2,

    /// <summary>건너뛴다(로컬이 backup보다 진행됨 — 로컬을 뒤로 되돌리지 않는다).</summary>
    Skip = 3,

    /// <summary>분기 충돌 — 자동 적용 금지, 사용자 결정이 필요하다.</summary>
    RequiresDecision = 4,

    /// <summary>안전하게 판정할 수 없다 — 적용 금지.</summary>
    Blocked = 5,
}

/// <summary>대화(thread) 하나의 Import Preview 결과.</summary>
/// <param name="ThreadId">thread ID.</param>
/// <param name="IsSelected">
/// backup manifest 기준 사용자가 실제로 선택했던 대화인지. <c>false</c>면 dependency-only(조상)라
/// 일반 목록이 아니라 상세 정보에서만 보여줘야 한다(요구사항 11).
/// </param>
/// <param name="ResolvedTitle">backup의 표시용 제목(가공값, UI 표시 전용).</param>
/// <param name="Relation">conversation 내용 관계.</param>
/// <param name="PlannedAction">Phase 7이 참고할 기본 계획(Preview일 뿐, 아직 적용하지 않는다).</param>
/// <param name="Metadata">metadata 차이(내용 관계와 별개).</param>
/// <param name="Warnings">이 대화를 판정하며 발견한 개별 문제(Unverifiable 사유 등). 원문 없음.</param>
/// <param name="LocalRevision">
/// (Phase 06_02) 이 판정에 실제로 쓰인 로컬 <see cref="ConversationRevision"/>. 로컬 chain이 없거나
/// (New) revision을 만들 수 없었으면(Unverifiable) <c>null</c>. <see cref="ImportPlanBuilder"/>가
/// 이 값을 그대로 <see cref="ImportConversationPrecondition"/>으로 freeze한다 — Phase 7이 나중에
/// "로컬이 그때와 같은지"를 다시 판정 없이 그대로 비교할 수 있게 하기 위함이다(재계산 금지 원칙).
/// </param>
/// <param name="IncomingRevision">
/// (Phase 06_02) 이 판정에 실제로 쓰인 backup 쪽 <see cref="ConversationRevision"/>. 계산하지
/// 못했으면(New/Unverifiable) <c>null</c>.
/// </param>
public sealed record ImportConversationPreview(
    string ThreadId,
    bool IsSelected,
    string? ResolvedTitle,
    RevisionRelation Relation,
    ImportPlannedAction PlannedAction,
    MetadataDifferences Metadata,
    IReadOnlyList<string> Warnings,
    ConversationRevision? LocalRevision,
    ConversationRevision? IncomingRevision)
{
    /// <summary>
    /// (Phase 9_1-07) 이 대화가 대상 PC에 지금 어디에 있는지. Preview를 만들 때 채운다(없으면 <c>null</c> — 이전 방식으로
    /// 직접 만든 레코드). 백업 쪽과의 원시 프로젝트 ID 비교(<see cref="MetadataDifferences.ProjectAssignmentDiffers"/>)는
    /// PC마다 ID가 달라 화면에 쓰지 않고, 이 값으로 "이 PC 위치"를 보여준다.
    /// </summary>
    public ConversationLocalLocation? LocalLocation { get; init; }

    /// <summary>(Phase 9_1-07) 백업 체인(조상 포함)에 <c>.jsonl.zst</c> 압축 rollout이 있는지.</summary>
    public bool IsCompressedRollout { get; init; }

    /// <summary>(Phase 9_1-07) 백업 체인에서 이 대화의 분기 조상 thread ID(뿌리 → 가까운 순). 자기 자신은 포함하지 않는다.</summary>
    public IReadOnlyList<string> RequiredAncestorThreadIds { get; init; } = [];

    /// <summary>(Phase 9_3-00) 이 PC의 <c>threads.project_id</c> 원문(DB 값). 이 PC에 없거나 비어 있으면 <c>null</c>.</summary>
    public string? LocalDbProjectId { get; init; }

    /// <summary>(Phase 9_3-00) 이 PC의 <c>threads.cwd</c> 원문. 이 PC에 없으면 <c>null</c>.</summary>
    public string? LocalCwd { get; init; }

    /// <summary>(Phase 9_3-00) Codex Desktop이 global-state에 이 대화의 위치를 따로 기록했는지(읽기 전용 판정).</summary>
    public DesktopPlacementStatus DesktopPlacement { get; init; } = DesktopPlacementStatus.Unavailable;
}

/// <summary>
/// 대화 하나의 "이 PC에서의 현재 위치"(Phase 9_1-07, 설계 §5.2).
/// </summary>
/// <param name="ExistsLocally">이 PC의 Codex에 이 thread가 이미 있는지.</param>
/// <param name="KnownProjectKey">
/// 이 PC에서 속한 프로젝트의 <see cref="Domain.Codex.Projects.KnownProject.Key"/>(원시 배정 ID를 ProjectDirectory로 정규화).
/// 등록 목록에 없는 원시 ID면 그 ID 그대로. "기타 대화"거나 이 PC에 없으면 <c>null</c>.
/// </param>
/// <param name="ProjectDisplayName">그 프로젝트의 표시 이름. "기타 대화"거나 이 PC에 없으면 <c>null</c>.</param>
/// <param name="Archived">이 PC에서 보관(archive) 상태인지. 없으면 <c>false</c>.</param>
/// <summary>(Phase 9_3-00) Codex Desktop이 global-state에 대화 위치를 따로 기록했는지.</summary>
public enum DesktopPlacementStatus
{
    /// <summary>global-state를 읽지 못했거나 9_5a 게이트에 실패했다 — 옮기지 않는다.</summary>
    Unavailable = 0,

    /// <summary>Desktop이 따로 기록하지 않았다(DB <c>project_id</c>만 바꿔도 된다, 9_0-A).</summary>
    NotRecorded = 1,

    /// <summary><c>thread-project-assignments</c>에 있다 — 옮기지 않는다.</summary>
    Assigned = 2,

    /// <summary><c>projectless-thread-ids</c>에 있다 — 옮기지 않는다.</summary>
    Projectless = 3,
}

public sealed record ConversationLocalLocation(
    bool ExistsLocally,
    string? KnownProjectKey,
    string? ProjectDisplayName,
    bool Archived)
{
    /// <summary>이 PC에 없는 대화.</summary>
    public static ConversationLocalLocation NotPresent { get; } = new(false, null, null, false);

    /// <summary>이 PC에 있지만 "기타 대화"에 있는지.</summary>
    public bool IsUncategorized => ExistsLocally && KnownProjectKey is null;
}

/// <summary>프로젝트 하나의 Import Preview 결과.</summary>
/// <param name="ProjectId">backup 쪽 원본 프로젝트 ID. 미분류면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름.</param>
/// <param name="PathMapping">경로 재매핑 Preview(요구사항 12).</param>
/// <param name="Conversations">
/// 이 프로젝트에 속한 <b>선택된</b> 대화만(dependency-only는 포함하지 않는다 — 요구사항 11).
/// </param>
public sealed record ImportProjectPreview(
    string? ProjectId,
    string DisplayName,
    ProjectPathMapping PathMapping,
    IReadOnlyList<ImportConversationPreview> Conversations)
{
    /// <summary>
    /// (Phase 9_1-07) 이 프로젝트의 대상 PC 목적지 판정(<see cref="ProjectTargetResolver"/>). <see cref="PathMapping"/>은
    /// Phase 6 호환용으로 그대로 두고, 실제로 연결되는지는 이 값이 말한다. Preview를 만들 때와 수동 재지정 때 채운다.
    /// 이전 방식으로 직접 만든 레코드면 <c>null</c>.
    /// </summary>
    public ProjectTarget? SuggestedTarget { get; init; }

    /// <summary>이 프로젝트에서 특정 관계에 해당하는 대화 수(UI 요약용).</summary>
    public int CountOf(RevisionRelation relation) => Conversations.Count(c => c.Relation == relation);
}

/// <summary>Import Preview 전체 결과.</summary>
/// <param name="Success">
/// backup을 <see cref="Validation.BackupValidator"/>로 검증 통과했는지. <c>false</c>면 Preview
/// 자체를 만들지 않는다(요구사항 17 "malformed backup → Preview 생성 금지") — 다른 필드는 비어 있다.
/// </param>
/// <param name="ValidationErrors">검증 실패 사유(<see cref="Success"/>가 <c>false</c>일 때만).</param>
/// <param name="Manifest">읽은 manifest(<see cref="Success"/>가 <c>true</c>일 때만).</param>
/// <param name="Projects">프로젝트별 Preview.</param>
/// <param name="DependencyOnlyConversations">
/// 선택되지 않은(조상 전용) 대화의 Preview. 일반 목록에는 넣지 않고 상세 정보용으로만 별도 보관한다.
/// </param>
/// <param name="Warnings">manifest/backup lineage 재구성 중 발견한 경고. 원문 없음.</param>
/// <param name="SourceBackupIdentity">
/// (Phase 06_03) 이 Preview가 실제로 검증·분석한 <c>.codexbackup</c> 파일의 identity(길이+전체
/// streaming SHA-256). <see cref="ImportPreviewBuilder.Build(string,CodexBackupManager.Domain.Codex.Catalog.CodexCatalog,System.Threading.CancellationToken)"/>
/// 가 분석을 마친 직후 같은 경로를 다시 읽어 고정한다. <see cref="ImportPlanBuilder"/>는 나중에
/// (임의로 시간이 지난 뒤) 같은 경로의 파일을 다시 hash해서 이 값과 정확히 같을 때만 Plan을
/// 만든다 — 그 사이 파일이 다른 것으로 바뀌었으면 "새 source로 다시 freeze"하지 않고 Plan 생성
/// 자체를 거부한다(Preview↔Plan TOCTOU 방지). 검증에 실패한 Preview(<see cref="Success"/>가
/// <c>false</c>)이거나 경로를 알 수 없는 <see cref="ImportPreviewBuilder.Build(CodexBackupManager.Backup.Reading.BackupReader,CodexBackupManager.Domain.Codex.Catalog.CodexCatalog,System.Threading.CancellationToken)"/>
/// 오버로드로 직접 만들었으면 <c>null</c>.
/// </param>
public sealed record ImportPreview(
    bool Success,
    IReadOnlyList<string> ValidationErrors,
    BackupManifest? Manifest,
    IReadOnlyList<ImportProjectPreview> Projects,
    IReadOnlyList<ImportConversationPreview> DependencyOnlyConversations,
    IReadOnlyList<string> Warnings,
    ImportBackupIdentity? SourceBackupIdentity)
{
    /// <summary>
    /// (Phase 9_1-07) 이 Preview를 만들 때 쓴 대상 PC의 프로젝트 목록. 수동 재지정(<see cref="ImportPreviewBuilder.ApplyManualProjectPathOverride"/>)이
    /// 같은 기준으로 목적지를 다시 판정하고, 화면이 연결 대상 이름을 보여줄 때 쓴다. Apply는 이 값을 쓰지 않고 fresh
    /// 카탈로그로 다시 판정한다.
    /// </summary>
    public ProjectDirectory LocalProjectDirectory { get; init; } = ProjectDirectory.Empty;

    /// <summary>검증 실패 결과를 만든다.</summary>
    public static ImportPreview Failed(IReadOnlyList<string> validationErrors)
        => new(false, validationErrors, null, [], [], [], null);
}
