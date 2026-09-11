using System;

namespace CodexBackupManager.Domain.Codex.Sessions;

/// <summary>
/// rollout 파일의 <c>ordinal:0, type:"session_meta"</c> 한 줄에서 읽은 값.
/// </summary>
/// <remarks>
/// <para>
/// docs/codex-storage-format.md §3 기준. <b>확인되지 않은 필드는 추측하지 않고 <c>null</c>로 남긴다.</b>
/// </para>
/// <para><b>제목은 여기 없다.</b> <c>session_meta</c>에는 대화 제목 필드가 없다. 제목은
/// <see cref="Titles.ThreadTitleResolver"/>가 state DB / session_index에서 따로 읽는다.</para>
/// </remarks>
public sealed record SessionMetadata
{
    /// <summary>thread ID (<c>session_id</c> / <c>id</c> 필드. 둘은 실측상 같은 값이었다).</summary>
    public required string ThreadId { get; init; }

    /// <summary>세션 생성 시각. 파싱하지 못하면 <c>null</c>.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>작업 디렉터리 원문(<c>\\?\</c> prefix 없는 형태로 기록되는 경우가 많다).</summary>
    public string? Cwd { get; init; }

    /// <summary><c>"Codex Desktop"</c> | <c>"codex_work_desktop"</c> 등.</summary>
    public string? Originator { get; init; }

    /// <summary>Codex CLI(core) 버전.</summary>
    public string? CliVersion { get; init; }

    /// <summary><c>"vscode"</c> 등.</summary>
    public string? Source { get; init; }

    /// <summary><c>"user"</c> | <c>"subagent"</c> | <c>"guardian_review"</c> 등.</summary>
    public string? ThreadSource { get; init; }

    /// <summary>모델 제공자. 비어 있으면 Codex에서 resume이 실패한다(Issue #29083).</summary>
    public string? ModelProvider { get; init; }

    /// <summary>모델 이름.</summary>
    public string? Model { get; init; }

    /// <summary><c>"paginated"</c> | <c>"legacy"</c>.</summary>
    public string? HistoryMode { get; init; }

    /// <summary>Git 메타데이터. 없으면 <c>null</c>.</summary>
    public GitInfo? Git { get; init; }

    /// <summary>분기 부모 thread ID. 분기된 대화가 아니면 <c>null</c>.</summary>
    public string? ForkedFromId { get; init; }

    /// <summary>부모 thread에서 분기한 ordinal(미포함). <see cref="ForkedFromId"/>가 있을 때만 의미 있다.</summary>
    public long? ForkedFromOrdinalExclusive { get; init; }

    /// <summary>이어붙이기 참조. 없으면 <c>null</c>.</summary>
    public HistoryBaseReference? HistoryBase { get; init; }

    /// <summary>이 메타데이터를 읽어온 rollout 파일 경로.</summary>
    public required string SourceFilePath { get; init; }

    /// <summary><c>archived_sessions\</c> 아래에서 읽었는지.</summary>
    public bool IsArchived { get; init; }
}
