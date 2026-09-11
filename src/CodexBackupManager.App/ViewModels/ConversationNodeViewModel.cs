namespace CodexBackupManager.App.ViewModels;

/// <summary>왼쪽 트리에서 대화 하나를 나타내는 표시 전용 모델. Phase 2는 클릭해도 본문을 열지 않는다.</summary>
public sealed class ConversationNodeViewModel
{
    /// <summary>표시할 제목(이미 우선순위로 결정된 값).</summary>
    public required string Title { get; init; }

    /// <summary>thread ID. 진단/향후 기능(뷰어, 선택)에서 쓴다.</summary>
    public required string ThreadId { get; init; }
}
