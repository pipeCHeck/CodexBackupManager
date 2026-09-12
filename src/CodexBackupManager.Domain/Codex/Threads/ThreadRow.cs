namespace CodexBackupManager.Domain.Codex.Threads;

/// <summary>
/// <c>state_*.sqlite</c>의 <c>threads</c> 테이블 한 행을 그대로 옮긴 값.
/// </summary>
/// <remarks>
/// 컬럼은 Codex 버전에 따라 있을 수도 없을 수도 있다(docs/codex-storage-format.md §4).
/// 없는 컬럼은 <c>null</c>로 남기고 추측하지 않는다.
/// </remarks>
public sealed record ThreadRow
{
    /// <summary>thread ID.</summary>
    public required string Id { get; init; }

    /// <summary>마지막(최신) 세그먼트 rollout 파일 경로.</summary>
    public string? RolloutPath { get; init; }

    /// <summary>작업 디렉터리(<c>\\?\</c> prefix가 붙어 있는 경우가 많다).</summary>
    public string? Cwd { get; init; }

    /// <summary>첫 user 메시지를 절단한 값. 원문이므로 로그에 남기지 않는다.</summary>
    public string? Title { get; init; }

    /// <summary>LLM이 생성한 짧은 제목. 원문이므로 로그에 남기지 않는다.</summary>
    public string? Name { get; init; }

    /// <summary>첫 user 메시지 절단. 원문이므로 로그에 남기지 않는다.</summary>
    public string? FirstUserMessage { get; init; }

    /// <summary>첫 user 메시지 절단(미리보기용). 원문이므로 로그에 남기지 않는다.</summary>
    public string? Preview { get; init; }

    /// <summary><c>"user"</c> | <c>"subagent"</c> | <c>"guardian_review"</c> 등.</summary>
    public string? ThreadSource { get; init; }

    /// <summary>
    /// <c>threads.source</c>. 실측 결과 <c>"vscode"</c> 같은 단순 문자열이거나 subagent 정보를 담은
    /// JSON 문자열(<c>{"subagent":{"thread_spawn":{...}}}</c>)이다 — 파싱하지 않고 그대로 보존한다.
    /// <see cref="ThreadSource"/>(<c>thread_source</c> 컬럼, "user"/"subagent"/"guardian_review")와는
    /// 이름이 비슷하지만 다른 컬럼이다 — 혼동하지 말 것(Phase 05_01 Restore Sufficiency Audit에서
    /// 처음 누락이 발견됨).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>신규(마이그레이션 완료 후) 프로젝트 연결. 마이그레이션 중이면 전부 <c>null</c>일 수 있다.</summary>
    public string? ProjectId { get; init; }

    /// <summary>아카이브 여부.</summary>
    public bool Archived { get; init; }

    /// <summary>아카이브 시각(epoch seconds). 없으면 <c>null</c>.</summary>
    public long? ArchivedAtSeconds { get; init; }

    /// <summary>생성 시각(epoch milliseconds).</summary>
    public long? CreatedAtMs { get; init; }

    /// <summary>갱신 시각(epoch milliseconds).</summary>
    public long? UpdatedAtMs { get; init; }

    /// <summary><c>"paginated"</c> | <c>"legacy"</c>.</summary>
    public string? HistoryMode { get; init; }

    /// <summary>이 thread를 마지막으로 갱신한 Codex CLI 버전.</summary>
    public string? CliVersion { get; init; }

    /// <summary>모델 제공자.</summary>
    public string? ModelProvider { get; init; }

    /// <summary>모델 이름.</summary>
    public string? Model { get; init; }

    /// <summary>Git commit SHA.</summary>
    public string? GitSha { get; init; }

    /// <summary>Git 브랜치.</summary>
    public string? GitBranch { get; init; }

    /// <summary>Git origin URL.</summary>
    public string? GitOriginUrl { get; init; }

    // ── Phase 5 Restore Sufficiency Audit(docs/codexbackup-format-v1.md §1)에서 추가된 컬럼.
    // 실제 state_5.sqlite(38컬럼) 전수 실측 + 공식 Codex 소스(ThreadMetadata/threads 마이그레이션)
    // 대조로 확정했다. sandbox_policy/approval_mode는 공식 소스가 opaque JSON/enum 직렬화 문자열로
    // 쓰는 것으로 확인됐다 — 해석하지 않고 그대로 보존한다(추측 금지).

    /// <summary>생성 시각(epoch seconds). <c>created_at_ms</c>와 별개로 원본 컬럼 값 그대로 보존한다.</summary>
    public long? CreatedAtSeconds { get; init; }

    /// <summary>갱신 시각(epoch seconds).</summary>
    public long? UpdatedAtSeconds { get; init; }

    /// <summary>Sandbox 정책. 공식 소스 확인 결과 opaque JSON 문자열(예: <c>{"type":"workspace-write",...}</c>)이거나 단순 문자열(<c>"danger-full-access"</c>)이다 — 파싱하지 않고 그대로 보존한다.</summary>
    public string? SandboxPolicy { get; init; }

    /// <summary>승인 모드. 공식 <c>AskForApproval</c> enum의 직렬화 문자열(예: <c>"on-request"</c>, <c>"never"</c>) 또는 <c>{"granular":{...}}</c> 형태.</summary>
    public string? ApprovalMode { get; init; }

    /// <summary>이 thread에서 사용한 토큰 수.</summary>
    public long? TokensUsed { get; init; }

    /// <summary>사용자 이벤트가 있었는지. 이 PC의 실제 스키마엔 있으나 공개 Codex 소스 트리에서는 컬럼 정의를 찾지 못했다(docs/codexbackup-format-v1.md §1.4) — 값은 보존하되 의미를 추측하지 않는다.</summary>
    public bool? HasUserEvent { get; init; }

    /// <summary>subagent 표시 이름. subagent가 아닌 thread는 대부분 <c>null</c>.</summary>
    public string? AgentNickname { get; init; }

    /// <summary>subagent 역할.</summary>
    public string? AgentRole { get; init; }

    /// <summary>subagent 경로.</summary>
    public string? AgentPath { get; init; }

    /// <summary>메모리 모드(예: <c>"enabled"</c>).</summary>
    public string? MemoryMode { get; init; }

    /// <summary>추론 강도(예: <c>"high"</c>).</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>사이드바에 고정(pin)되어 있는지.</summary>
    public bool? IsPinned { get; init; }

    /// <summary>사이드바 섹션 ID(예: "Pinned"). UI 정렬 전용 — 대부분 <c>null</c>.</summary>
    public string? ThreadSectionId { get; init; }

    /// <summary>섹션 안에서의 정렬 순서.</summary>
    public long? SectionPosition { get; init; }

    /// <summary>섹션에 들어간 시각(epoch ms).</summary>
    public long? SectionEnteredAtMs { get; init; }

    /// <summary>최근 사용 시각(epoch seconds). Codex의 정렬 기준.</summary>
    public long? RecencyAtSeconds { get; init; }

    /// <summary>최근 사용 시각(epoch ms).</summary>
    public long? RecencyAtMs { get; init; }
}
