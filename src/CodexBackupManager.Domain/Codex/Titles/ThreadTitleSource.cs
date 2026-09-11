namespace CodexBackupManager.Domain.Codex.Titles;

/// <summary>
/// 제목을 어디에서 가져왔는지. 우선순위 순서(docs/codex-storage-format.md §5 "제목의 출처").
/// </summary>
public enum ThreadTitleSource
{
    /// <summary><c>state_*.sqlite threads.name</c> — LLM이 생성한 짧은 제목.</summary>
    StateName = 1,

    /// <summary><c>session_index.jsonl</c>의 <c>thread_name</c> (같은 id 중 마지막 값).</summary>
    SessionIndex = 2,

    /// <summary><c>threads.title</c>.</summary>
    StateTitle = 3,

    /// <summary><c>threads.first_user_message</c>.</summary>
    FirstUserMessage = 4,

    /// <summary><c>threads.preview</c>.</summary>
    Preview = 5,

    /// <summary>위 어디에도 값이 없어 thread ID 일부로 만든 안전한 기본 표시명.</summary>
    ThreadIdFallback = 6,
}

/// <summary>제목 결정 결과.</summary>
/// <param name="Text">화면에 표시할 제목.</param>
/// <param name="Source">이 제목을 가져온 출처.</param>
public sealed record ThreadTitle(string Text, ThreadTitleSource Source);
