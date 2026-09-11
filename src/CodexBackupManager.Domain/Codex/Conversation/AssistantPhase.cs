namespace CodexBackupManager.Domain.Codex.Conversation;

/// <summary>
/// Assistant 메시지의 채널. docs/codex-storage-format.md §3: <c>AgentMessage.phase</c>.
/// </summary>
public enum AssistantPhase
{
    /// <summary>작업 중 중간 진행 상황 공유(<c>commentary</c> 채널).</summary>
    Commentary = 1,

    /// <summary>사용자에게 돌려주는 최종 응답(<c>final</c> 채널).</summary>
    Final = 2,
}
