using System;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;

namespace CodexBackupManager.Domain.Codex.Catalog;

/// <summary>
/// 카탈로그에 표시할 대화 하나. <see cref="Threads.ThreadRow"/> + 제목/프로젝트 해결 결과 + 파일 체인을 합친 것.
/// </summary>
public sealed record ConversationEntry
{
    /// <summary>thread ID.</summary>
    public required string ThreadId { get; init; }

    /// <summary>원본 행. 제목/경로 원문이 들어 있으므로 로그에 그대로 남기지 않는다.</summary>
    public required ThreadRow Row { get; init; }

    /// <summary>결정된 표시 제목.</summary>
    public required ThreadTitle Title { get; init; }

    /// <summary>프로젝트 연결 해결 결과.</summary>
    public required ProjectAssignment Project { get; init; }

    /// <summary>이 thread를 구성하는 rollout 파일 체인. 찾지 못하면 <c>null</c>.</summary>
    public ThreadChain? Chain { get; init; }

    /// <summary><c>threads.thread_source == "user"</c>인지. 기본 UI는 이것만 보여준다.</summary>
    public bool IsUserConversation => string.Equals(Row.ThreadSource, "user", StringComparison.Ordinal);

    /// <summary>아카이브 여부(<see cref="Threads.ThreadRow.Archived"/> 그대로).</summary>
    public bool Archived => Row.Archived;

    /// <summary>생성 시각(UTC). <c>created_at_ms</c>를 읽지 못하면 <c>null</c>.</summary>
    public DateTimeOffset? CreatedAtUtc => FromEpochMs(Row.CreatedAtMs);

    /// <summary>갱신 시각(UTC). <c>updated_at_ms</c>를 읽지 못하면 <c>null</c>.</summary>
    public DateTimeOffset? UpdatedAtUtc => FromEpochMs(Row.UpdatedAtMs);

    private static DateTimeOffset? FromEpochMs(long? epochMs)
        => epochMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
