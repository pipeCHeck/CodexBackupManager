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
}
