using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Views;

/// <summary>
/// (Phase 9_U-09) 트리에서 키보드 포커스가 있는 줄(<see cref="TreeViewItem"/>)에서 Space를 누르면 그 줄의 체크를 바꾼다.
/// 체크박스는 대화 보기와 분리하려고 포커스를 받지 않으므로(메인 트리), 키보드 사용자는 이 키로 체크한다.
/// </summary>
/// <remarks>
/// 줄의 모델(<see cref="ICheckToggle"/>)이 마우스로 체크박스를 누를 때와 같은 setter를 부른다. 트리 선택(오른쪽 대화 보기)은
/// 바꾸지 않는다. Space가 아닌 키, 수정 키(Ctrl·Alt·Shift)가 붙은 Space, 트리 줄이 아닌 곳(예: 줄 안의 입력 칸)에서 온 키는 건드리지 않는다.
/// </remarks>
public static class TreeSpaceToggleBehavior
{
    /// <summary>Space로 체크를 바꿀지.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TreeSpaceToggleBehavior), new PropertyMetadata(false, OnChanged));

    /// <summary>getter.</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>setter.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree)
        {
            return;
        }

        tree.KeyDown -= OnKeyDown;
        if (e.NewValue is true)
        {
            tree.KeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.OriginalSource is TreeViewItem { DataContext: ICheckToggle node })
        {
            node.ToggleCheck();
            e.Handled = true;
        }
    }
}
