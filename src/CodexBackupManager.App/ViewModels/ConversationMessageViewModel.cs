using System.Windows.Documents;
using CodexBackupManager.App.Rendering;
using CodexBackupManager.Domain.Codex.Conversation;

namespace CodexBackupManager.App.ViewModels;

/// <summary>Conversation Viewer에 표시할 메시지 한 줄.</summary>
public sealed class ConversationMessageViewModel
{
    /// <summary>"User" 또는 "Assistant".</summary>
    public required string RoleLabel { get; init; }

    /// <summary>메시지 본문. 원래 줄바꿈을 그대로 보존한다.</summary>
    public required string Text { get; init; }

    /// <summary>Assistant의 phase 표시("commentary"/"final"). 없으면 <c>null</c>.</summary>
    public string? PhaseLabel { get; init; }

    /// <summary>User 메시지인지(정렬/색상 구분용).</summary>
    public bool IsUser { get; init; }

    /// <summary>
    /// <see cref="Text"/>를 Markdown-lite로 파싱해 그린 <see cref="FlowDocument"/>(제목/문단/목록/
    /// 코드블록 서식 포함). Viewer는 이 문서를 읽기 전용 <c>RichTextBox</c>에 붙여서 보여준다 —
    /// 그래야 서식이 섞여도 텍스트 선택/복사가 그대로 유지된다.
    /// </summary>
    public required FlowDocument Body { get; init; }

    /// <summary>생성.</summary>
    public static ConversationMessageViewModel FromDomain(ConversationMessage message)
        => new()
        {
            RoleLabel = message.Role == ConversationRole.User ? "User" : "Assistant",
            Text = message.Text,
            Body = MarkdownLiteFlowDocumentRenderer.Render(MarkdownLiteParser.Parse(message.Text)),
            PhaseLabel = message.Phase switch
            {
                AssistantPhase.Commentary => "commentary",
                AssistantPhase.Final => "final",
                _ => null,
            },
            IsUser = message.Role == ConversationRole.User,
        };
}
