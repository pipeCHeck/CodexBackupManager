using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Domain.Codex.Selection;

namespace CodexBackupManager.App.ViewModels;

/// <summary>왼쪽 트리에서 프로젝트 그룹 하나를 나타내는 모델("기타 대화" 포함).</summary>
/// <remarks>
/// <para>
/// <see cref="IsSelected"/>(3상태)는 자체 필드로 저장하지 않는다 — 매번 자식들의 실제 선택 상태
/// (<see cref="ConversationSelectionState"/>)를 다시 읽어 계산한다. 그래서 자식 하나가 다른 경로로
/// 바뀌어도(개별 체크박스, 전체 선택 버튼 등) 이 값은 항상 최신이다.
/// </para>
/// <para>
/// <b>체크박스 클릭 의미</b>: WPF <c>IsThreeState</c> 체크박스가 클릭 시 전달하는 원시값(참/거짓/불확정)은
/// 무시한다. 대신 "클릭 직전에 계산된 상태"만으로 목표를 정한다 — 이미 전체 선택된 상태가 아니면
/// 전체 선택으로, 이미 전체 선택된 상태면 전체 해제로. 그래서 setter는 <c>value</c> 매개변수를 읽지 않는다.
/// </para>
/// <para>
/// <b>대량 선택 시 알림은 한 번만.</b> 프로젝트 전체 선택/해제는 <see cref="ConversationSelectionState"/>를
/// 한 번만 호출하고(자식 수만큼 이벤트가 연쇄되지 않는다), 그 다음 자식마다 정확히 한 번씩만
/// <c>PropertyChanged</c>를 알린다 — 재귀나 반복 재계산이 없다(요구사항: 수천 개 항목에서도 안정적).
/// </para>
/// </remarks>
public sealed class ProjectNodeViewModel : ObservableObject
{
    private readonly ConversationSelectionState _selection;

    /// <summary>생성자.</summary>
    public ProjectNodeViewModel(
        string displayName,
        bool isUncategorized,
        IReadOnlyList<ConversationNodeViewModel> conversations,
        ConversationSelectionState selection)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(selection);

        DisplayName = displayName;
        IsUncategorized = isUncategorized;
        Conversations = conversations;
        _selection = selection;

        foreach (ConversationNodeViewModel conversation in conversations)
        {
            conversation.Parent = this;
        }
    }

    /// <summary>표시 이름.</summary>
    public string DisplayName { get; }

    /// <summary>이 그룹에 속한 대화들.</summary>
    public IReadOnlyList<ConversationNodeViewModel> Conversations { get; }

    /// <summary>"기타 대화"(프로젝트 미연결) 그룹인지. 선택 동작은 일반 프로젝트와 완전히 동일하다.</summary>
    public bool IsUncategorized { get; }

    /// <summary>헤더에 보여줄 "이름 (개수)" 텍스트.</summary>
    public string HeaderText => $"{DisplayName} ({Conversations.Count})";

    /// <summary>
    /// 3상태 선택 상태. <c>true</c> = 하위 대화 전체 선택, <c>false</c> = 전체 미선택,
    /// <c>null</c> = 일부만 선택(indeterminate). 자식이 하나도 없으면 <c>false</c>로 본다.
    /// </summary>
    public bool? IsSelected
    {
        get
        {
            if (Conversations.Count == 0)
            {
                return false;
            }

            bool anySelected = false;
            bool anyUnselected = false;
            foreach (ConversationNodeViewModel conversation in Conversations)
            {
                if (conversation.IsSelected)
                {
                    anySelected = true;
                }
                else
                {
                    anyUnselected = true;
                }

                if (anySelected && anyUnselected)
                {
                    return null;
                }
            }

            return anySelected;
        }
        set => SetAllSelected(IsSelected != true);
    }

    /// <summary>
    /// 모든 자식을 한 번에 선택/해제한다. 중앙 상태는 한 번만 호출하고, 자식마다 정확히 한 번씩만
    /// 바인딩을 갱신한다(클래스 remarks 참고).
    /// </summary>
    public void SetAllSelected(bool selected)
    {
        if (Conversations.Count == 0)
        {
            return;
        }

        IEnumerable<string> threadIds = Conversations.Select(c => c.ThreadId);
        if (selected)
        {
            _selection.SelectMany(threadIds);
        }
        else
        {
            _selection.DeselectMany(threadIds);
        }

        foreach (ConversationNodeViewModel conversation in Conversations)
        {
            conversation.NotifySelectionChanged();
        }

        NotifySelectionChanged();
    }

    /// <summary>자식 하나의 선택이 개별적으로(체크박스 클릭 등) 바뀌었을 때, 이 프로젝트의 tri-state를 다시 읽으라고 알린다.</summary>
    internal void NotifyChildSelectionChanged() => NotifySelectionChanged();

    /// <summary>이 프로젝트의 선택 상태가 바뀌었을 수 있다고 바인딩에 알린다. 값 자체는 저장하지 않는다.</summary>
    internal void NotifySelectionChanged() => OnPropertyChanged(nameof(IsSelected));
}
