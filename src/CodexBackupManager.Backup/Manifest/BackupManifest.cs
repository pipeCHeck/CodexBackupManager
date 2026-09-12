using System;
using System.Collections.Generic;

namespace CodexBackupManager.Backup.Manifest;

/// <summary>
/// <c>.codexbackup</c> 파일의 <c>manifest.json</c>. 스펙은 <c>docs/codexbackup-format-v1.md</c> §3.2.
/// </summary>
/// <remarks>
/// <c>checksums.json</c>과 달리 이 파일 자신은 체크섬 목록에 없다 — 파일 내용이 확정되기 전에는
/// 자기 자신의 해시를 계산할 수 없어 순환이 생기기 때문이다(§3.3). 무결성은 "정상 JSON 파싱 +
/// <see cref="BackupFormatVersion"/> 존재"로 판정한다.
/// </remarks>
public sealed record BackupManifest
{
    /// <summary>이 프로젝트가 지원하는 유일한 현재 버전.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>백업 포맷 버전. V1은 항상 1.</summary>
    public required int BackupFormatVersion { get; init; }

    /// <summary>이 백업을 만든 앱 버전(실행 어셈블리 버전).</summary>
    public required string AppVersion { get; init; }

    /// <summary>Export를 수행한 시각(UTC).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Export를 수행한 OS.</summary>
    public required string SourceOS { get; init; }

    /// <summary>Export 시점의 Codex Desktop 버전. 확인 불가면 <c>null</c>.</summary>
    public string? SourceCodexDesktopVersion { get; init; }

    /// <summary>Export 시점의 Codex CLI 버전. 확인 불가면 <c>null</c>.</summary>
    public string? SourceCodexCliVersion { get; init; }

    /// <summary>사용자가 실제로 선택한 대화 수(조상 전용 dependency는 제외).</summary>
    public required int ConversationCount { get; init; }

    /// <summary>선택되진 않았지만 체인 복원을 위해 추가로 포함된 조상 thread 수.</summary>
    public required int DependencyConversationCount { get; init; }

    /// <summary>선택된 대화가 속한 프로젝트 수.</summary>
    public required int ProjectCount { get; init; }

    /// <summary>payload(rollout + attachment) 총 개수.</summary>
    public required int PayloadCount { get; init; }

    /// <summary>선택된 대화가 속한 프로젝트 목록.</summary>
    public required IReadOnlyList<BackupProjectMetadata> Projects { get; init; }

    /// <summary>선택된 대화 + dependency-only 조상 대화 전체 목록.</summary>
    public required IReadOnlyList<BackupConversationMetadata> Conversations { get; init; }

    /// <summary>
    /// payload로 포함된 첨부(로컬 이미지) 전체의 원본 경로 ↔ entry 경로 명시적 매핑(Phase 05_01).
    /// 개수는 <see cref="PayloadCount"/>의 일부다(rollout 파일 수와 합쳐서 payload 총 개수가 된다).
    /// </summary>
    public required IReadOnlyList<BackupAttachmentMetadata> Attachments { get; init; }

    /// <summary>
    /// Export 중 발견한 문제(누락된 attachment 개수 등). 사용자 대화 원문이나 개인 절대경로는
    /// 담지 않는다(CLAUDE.md §29).
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Manifest 안의 프로젝트 그룹 메타데이터.</summary>
public sealed record BackupProjectMetadata
{
    /// <summary>원본 프로젝트 ID. "기타 대화" 그룹이면 <c>null</c>.</summary>
    public string? ProjectId { get; init; }

    /// <summary>표시 이름.</summary>
    public required string DisplayName { get; init; }

    /// <summary>원본 PC 기준 알려진 루트 경로(정보용 — 다른 PC에서 그대로 못 쓸 수 있다).</summary>
    public required IReadOnlyList<string> OriginalRootPaths { get; init; }

    /// <summary>이 프로젝트에 속한, 실제로 선택된 대화의 thread ID 목록.</summary>
    public required IReadOnlyList<string> ConversationThreadIds { get; init; }

    /// <summary>"기타 대화"(미분류) 그룹인지.</summary>
    public bool IsUncategorized => ProjectId is null;
}

/// <summary>Manifest 안의 대화(thread) 메타데이터. Restore Sufficiency Audit(§1)로 확정한 필드 전체.</summary>
public sealed record BackupConversationMetadata
{
    /// <summary>thread ID.</summary>
    public required string ThreadId { get; init; }

    /// <summary>
    /// 사용자가 실제로 선택했는지. <c>false</c>면 어떤 선택 대화의 체인 복원을 위해서만
    /// 포함된 조상(dependency-only) thread다 — <see cref="BackupManifest.ConversationCount"/>에서 제외된다.
    /// </summary>
    public required bool IsSelected { get; init; }

    /// <summary>
    /// 결정된 표시 제목(<c>ThreadTitleResolver</c>의 우선순위 결과). <b>이건 가공값이다</b> —
    /// 아래 <see cref="Title"/>/<see cref="Name"/>/<see cref="FirstUserMessage"/>/<see cref="Preview"/>
    /// 원본 컬럼을 대체하지 않는다(Phase 05_01: 가공값을 원본의 대체물로 쓰지 말라는 지시).
    /// </summary>
    public string? ResolvedTitle { get; init; }

    /// <summary>제목의 출처(<c>ThreadTitleSource</c> 이름).</summary>
    public string? TitleSource { get; init; }

    /// <summary>
    /// <c>threads.project_id</c> 원본 값. 마이그레이션 미완료 상태에서는 거의 항상 <c>null</c>이다
    /// (docs/codex-storage-format.md §4~5) — 실제 프로젝트 연결을 보려면
    /// <see cref="ResolvedProjectId"/>를 쓴다. 이 필드는 원본 컬럼값 보존용이다.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// <c>CodexProjectResolver</c>가 3중 경로(state_5.project_id → global-state → cwd fallback)로
    /// 실제 해결한 프로젝트 ID. <see cref="BackupManifest.Projects"/> 그룹핑에 쓰인 값과 같다.
    /// </summary>
    public string? ResolvedProjectId { get; init; }

    /// <summary>원본 작업 디렉터리(<c>threads.cwd</c>).</summary>
    public string? OriginalCwd { get; init; }

    /// <summary>
    /// <c>threads.rollout_path</c> 원본 값(마지막 세그먼트 경로, 원본 PC 기준 절대경로).
    /// <b>Restore가 그대로 재사용할 경로가 아니다</b> — 다른 PC에서는 rollout 파일이 다른 디렉터리에
    /// 새로 놓인다(<see cref="PayloadRolloutEntries"/>가 실제 Restore 대상이다). 이 값은 원본
    /// metadata를 잃지 않기 위한 참고 정보로만 보존한다(Phase 05_01 정책).
    /// </summary>
    public string? OriginalRolloutPath { get; init; }

    /// <summary>
    /// <c>threads.source</c> 원본 값(예: <c>"vscode"</c> 또는 subagent 정보를 담은 JSON 문자열).
    /// <see cref="ThreadSource"/>(<c>thread_source</c> 컬럼)와는 다른 컬럼이다 — 혼동 금지.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>첫 user 메시지를 절단한 원본 값(<c>threads.title</c>). 원문이므로 로그에 남기지 않는다.</summary>
    public string? Title { get; init; }

    /// <summary>LLM이 생성한 짧은 제목 원본 값(<c>threads.name</c>). 원문이므로 로그에 남기지 않는다.</summary>
    public string? Name { get; init; }

    /// <summary>첫 user 메시지 절단 원본 값(<c>threads.first_user_message</c>). 원문이므로 로그에 남기지 않는다.</summary>
    public string? FirstUserMessage { get; init; }

    /// <summary>첫 user 메시지 절단(미리보기용) 원본 값(<c>threads.preview</c>). 원문이므로 로그에 남기지 않는다.</summary>
    public string? Preview { get; init; }

    /// <summary>생성 시각(epoch seconds, <c>threads.created_at</c> 원본 값).</summary>
    public long? CreatedAtSeconds { get; init; }

    /// <summary>생성 시각(epoch milliseconds, <c>threads.created_at_ms</c> 원본 값).</summary>
    public long? CreatedAtMs { get; init; }

    /// <summary>생성 시각(UTC, <see cref="CreatedAtMs"/>에서 파생된 편의 값).</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>갱신 시각(epoch seconds, <c>threads.updated_at</c> 원본 값).</summary>
    public long? UpdatedAtSeconds { get; init; }

    /// <summary>갱신 시각(epoch milliseconds, <c>threads.updated_at_ms</c> 원본 값).</summary>
    public long? UpdatedAtMs { get; init; }

    /// <summary>갱신 시각(UTC, <see cref="UpdatedAtMs"/>에서 파생된 편의 값).</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>최근 사용 시각(epoch seconds, <c>threads.recency_at</c> 원본 값).</summary>
    public long? RecencyAtSeconds { get; init; }

    /// <summary>최근 사용 시각(epoch milliseconds, <c>threads.recency_at_ms</c> 원본 값).</summary>
    public long? RecencyAtMs { get; init; }

    /// <summary>아카이브 여부.</summary>
    public bool Archived { get; init; }

    /// <summary>아카이브 시각(epoch seconds, <c>threads.archived_at</c> 원본 값).</summary>
    public long? ArchivedAtSeconds { get; init; }

    /// <summary>아카이브 시각(UTC, <see cref="ArchivedAtSeconds"/>에서 파생된 편의 값).</summary>
    public DateTimeOffset? ArchivedAtUtc { get; init; }

    /// <summary><c>"paginated"</c> | <c>"legacy"</c>.</summary>
    public string? HistoryMode { get; init; }

    /// <summary><c>"user"</c> | <c>"subagent"</c> | <c>"guardian_review"</c> 등.</summary>
    public string? ThreadSource { get; init; }

    /// <summary>Codex CLI 버전.</summary>
    public string? CliVersion { get; init; }

    /// <summary>모델 제공자.</summary>
    public string? ModelProvider { get; init; }

    /// <summary>모델 이름.</summary>
    public string? Model { get; init; }

    /// <summary>추론 강도.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>메모리 모드.</summary>
    public string? MemoryMode { get; init; }

    /// <summary>Sandbox 정책. opaque 문자열(해석하지 않는다).</summary>
    public string? SandboxPolicyRaw { get; init; }

    /// <summary>승인 모드. opaque 문자열(해석하지 않는다).</summary>
    public string? ApprovalMode { get; init; }

    /// <summary>사용된 토큰 수.</summary>
    public long? TokensUsed { get; init; }

    /// <summary>사용자 이벤트 존재 여부(§1.4 — 의미는 확정하지 못했으나 값은 보존).</summary>
    public bool? HasUserEvent { get; init; }

    /// <summary>Git commit SHA.</summary>
    public string? GitSha { get; init; }

    /// <summary>Git 브랜치.</summary>
    public string? GitBranch { get; init; }

    /// <summary>Git origin URL.</summary>
    public string? GitOriginUrl { get; init; }

    /// <summary>subagent 표시 이름.</summary>
    public string? AgentNickname { get; init; }

    /// <summary>subagent 역할.</summary>
    public string? AgentRole { get; init; }

    /// <summary>subagent 경로.</summary>
    public string? AgentPath { get; init; }

    /// <summary>사이드바에 고정되어 있는지.</summary>
    public bool? IsPinned { get; init; }

    /// <summary>사이드바 섹션 ID.</summary>
    public string? ThreadSectionId { get; init; }

    /// <summary>섹션 안 정렬 순서.</summary>
    public long? SectionPosition { get; init; }

    /// <summary>섹션에 들어간 시각(epoch ms, <c>threads.section_entered_at_ms</c> 원본 값).</summary>
    public long? SectionEnteredAtMs { get; init; }

    /// <summary>
    /// 이 thread(체인)를 재구성하는 데 필요한 rollout payload entry 경로 목록(시간순,
    /// <c>payload/rollouts/…</c> 상대경로). <see cref="Planning.ExportPlanBuilder"/>가 계산한다.
    /// </summary>
    public required IReadOnlyList<string> PayloadRolloutEntries { get; init; }

    /// <summary>
    /// 이 대화가 참조하는 첨부(로컬 이미지) payload entry 경로 목록. 없으면 빈 목록. 원본 경로
    /// ↔ entry 경로의 명시적 역매핑은 <see cref="BackupManifest.Attachments"/>에 있다(Phase 05_01).
    /// </summary>
    public required IReadOnlyList<string> PayloadAttachmentEntries { get; init; }
}

/// <summary>
/// 첨부 하나의 원본 경로 ↔ backup entry 경로 명시적 매핑(Phase 05_01 hardening).
/// </summary>
/// <remarks>
/// <see cref="BackupConversationMetadata.PayloadAttachmentEntries"/>만으로는 "이 entry가 원래
/// 어떤 로컬 파일이었는지"를 역으로 알 수 없다(같은 basename이 서로 다른 디렉터리에 있었을 수 있고,
/// entry 이름은 dedupe를 위해 인덱스 폴더로 분리돼 있다). 이 표가 유일한 신뢰 가능한 역매핑이다.
/// <see cref="OriginalAbsolutePath"/>는 원본 rollout content(<c>local_image.path</c> 등)에 이미
/// 있던 값이라 backup metadata에 두는 것 자체는 정상이다 — 단, 로그에는 출력하지 않는다.
/// </remarks>
public sealed record BackupAttachmentMetadata
{
    /// <summary>ZIP 안의 상대경로(<c>payload/attachments/…</c>).</summary>
    public required string EntryPath { get; init; }

    /// <summary>Export 시점의 원본 PC 기준 절대경로. 다른 PC에서는 유효하지 않을 수 있다.</summary>
    public required string OriginalAbsolutePath { get; init; }
}
