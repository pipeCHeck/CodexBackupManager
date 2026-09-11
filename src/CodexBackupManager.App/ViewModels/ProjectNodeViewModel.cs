using System.Collections.Generic;

namespace CodexBackupManager.App.ViewModels;

/// <summary>왼쪽 트리에서 프로젝트 그룹 하나를 나타내는 표시 전용 모델("기타 대화" 포함).</summary>
public sealed class ProjectNodeViewModel
{
    /// <summary>표시 이름.</summary>
    public required string DisplayName { get; init; }

    /// <summary>이 그룹에 속한 대화들.</summary>
    public required IReadOnlyList<ConversationNodeViewModel> Conversations { get; init; }

    /// <summary>"기타 대화"(프로젝트 미연결) 그룹인지. 트리에서 다르게 표시할 때 쓴다.</summary>
    public bool IsUncategorized { get; init; }

    /// <summary>헤더에 보여줄 "이름 (개수)" 텍스트.</summary>
    public string HeaderText => $"{DisplayName} ({Conversations.Count})";
}
