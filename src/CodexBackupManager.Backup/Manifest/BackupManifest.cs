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

    /// <summary>결정된 표시 제목.</summary>
    public string? ResolvedTitle { get; init; }

    /// <summary>제목의 출처(<c>ThreadTitleSource</c> 이름).</summary>
    public string? TitleSource { get; init; }

    /// <summary>연결된 프로젝트 ID. 없으면 <c>null</c>.</summary>
    public string? ProjectId { get; init; }

    /// <summary>원본 작업 디렉터리.</summary>
    public string? OriginalCwd { get; init; }

    /// <summary>생성 시각(UTC).</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>갱신 시각(UTC).</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>아카이브 여부.</summary>
    public bool Archived { get; init; }

    /// <summary>아카이브 시각(UTC).</summary>
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

    /// <summary>
    /// 이 thread(체인)를 재구성하는 데 필요한 rollout payload entry 경로 목록(시간순,
    /// <c>payload/rollouts/…</c> 상대경로). <see cref="ThreadDependencyClosure"/>가 계산한다.
    /// </summary>
    public required IReadOnlyList<string> PayloadRolloutEntries { get; init; }

    /// <summary>이 대화가 참조하는 첨부(로컬 이미지) payload entry 경로 목록. 없으면 빈 목록.</summary>
    public required IReadOnlyList<string> PayloadAttachmentEntries { get; init; }
}
