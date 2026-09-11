using System;

namespace CodexBackupManager.Domain.Codex.Conversation;

/// <summary>
/// Viewer에 표시할 메시지 하나(User 또는 Assistant).
/// </summary>
/// <remarks>
/// Tool Call / Reasoning / Developer 주입 메시지 등은 이 타입으로 만들지 않는다 — 파서가 애초에
/// 걸러낸다. <c>Reasoning.encrypted_content</c>는 복호화할 수 없고 시도하지도 않는다
/// (docs/codex-storage-format.md §3).
/// </remarks>
public sealed record ConversationMessage
{
    /// <summary>원본 item/message ID. 없으면 <c>null</c>(추측해서 만들어내지 않는다).</summary>
    public string? ItemId { get; init; }

    /// <summary>발화자.</summary>
    public required ConversationRole Role { get; init; }

    /// <summary>메시지 본문. 원본 줄바꿈을 그대로 보존한다.</summary>
    public required string Text { get; init; }

    /// <summary>Assistant 메시지의 phase. User 메시지이거나 확인할 수 없으면 <c>null</c>.</summary>
    public AssistantPhase? Phase { get; init; }

    /// <summary>메시지 시각. rollout 줄의 <c>timestamp</c>. 파싱 실패 시 <c>null</c>.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// 이 메시지가 나온 rollout 줄의 top-level <c>ordinal</c>(파일 내 0부터 시작하는 줄 번호).
    /// 정렬과 <c>history_base.end_ordinal_exclusive</c> 경계 판정에 쓴다.
    /// </summary>
    public long? Ordinal { get; init; }
}
