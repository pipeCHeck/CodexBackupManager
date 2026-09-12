using System;
using CodexBackupManager.Domain.Codex.Selection;

namespace CodexBackupManager.App.ViewModels;

/// <summary>왼쪽 트리에서 대화 하나를 나타내는 모델.</summary>
/// <remarks>
/// <para>
/// <b>선택(백업 대상)과 Viewer 포커스는 완전히 다른 개념이다.</b> 이 클래스의 <see cref="IsSelected"/>는
/// Phase 4의 체크박스 상태(Phase 5에서 백업할 대상)이고, "지금 오른쪽에서 열람 중인 대화"는 TreeView의
/// <c>SelectedItem</c>(<see cref="MainViewModel.SelectConversation"/>)이 따로 관리한다. 이 클래스는
/// TreeView 선택/포커스를 전혀 참조하지 않는다.
/// </para>
/// <para>
/// <see cref="IsSelected"/>는 자체 필드를 두지 않는다 — 항상 <see cref="ConversationSelectionState"/>를
/// 그대로 읽고 쓰는 얇은 뷰다. 상태를 이 클래스와 저장소 양쪽에 나눠 두면 서로 어긋날 수 있기 때문이다.
/// </para>
/// </remarks>
public sealed class ConversationNodeViewModel : ObservableObject
{
    private readonly ConversationSelectionState _selection;

    /// <summary>생성자.</summary>
    /// <param name="title">표시할 제목(이미 우선순위로 결정된 값).</param>
    /// <param name="threadId">thread ID. 진단/뷰어/선택 전반에서 이 값을 키로 쓴다.</param>
    /// <param name="selection">선택 상태의 단일 source of truth. 같은 카탈로그의 모든 노드가 공유한다.</param>
    public ConversationNodeViewModel(string title, string threadId, ConversationSelectionState selection)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(selection);

        Title = title;
        ThreadId = threadId;
        _selection = selection;
    }

    /// <summary>표시할 제목(이미 우선순위로 결정된 값).</summary>
    public string Title { get; }

    /// <summary>thread ID. 진단/뷰어/선택 전반에서 이 값을 키로 쓴다.</summary>
    public string ThreadId { get; }

    /// <summary>이 대화가 속한 프로젝트 노드. 부모의 tri-state를 갱신할 때만 쓴다.</summary>
    internal ProjectNodeViewModel? Parent { get; set; }

    /// <summary>
    /// 백업 대상으로 선택되었는지. Source of truth는 언제나 <see cref="ConversationSelectionState"/>다 —
    /// 이 프로퍼티는 그것을 읽고 쓰는 통로일 뿐이다.
    /// </summary>
    public bool IsSelected
    {
        get => _selection.IsSelected(ThreadId);
        set
        {
            if (_selection.IsSelected(ThreadId) == value)
            {
                return;
            }

            _selection.SetSelected(ThreadId, value);
            OnPropertyChanged();
            Parent?.NotifyChildSelectionChanged();
        }
    }

    /// <summary>
    /// 선택 상태가 이 노드가 아닌 다른 경로(형제 일괄 선택, refresh 등)에서 바뀌었을 때 바인딩을
    /// 갱신하기 위해 호출한다. 값 자체는 저장하지 않고 다시 읽으라고 알리기만 한다.
    /// </summary>
    internal void NotifySelectionChanged() => OnPropertyChanged(nameof(IsSelected));
}
