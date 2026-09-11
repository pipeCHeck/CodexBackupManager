namespace CodexBackupManager.Domain.Codex.Conversation;

/// <summary>
/// Viewer에 표시하는 메시지의 발화자.
/// </summary>
/// <remarks>
/// Codex의 실제 role은 이보다 많다(<c>developer</c> 등, docs/codex-storage-format.md §3).
/// Phase 3 UI는 <c>User</c>/<c>Assistant</c>만 보여주므로 Domain도 이 둘만 표현한다.
/// 그 외 role/item type은 파서가 <see cref="ConversationMessage"/>로 만들지 않고 건너뛴다.
/// </remarks>
public enum ConversationRole
{
    /// <summary>사용자가 입력한 메시지.</summary>
    User = 1,

    /// <summary>Codex(Assistant)가 보낸 메시지.</summary>
    Assistant = 2,
}
