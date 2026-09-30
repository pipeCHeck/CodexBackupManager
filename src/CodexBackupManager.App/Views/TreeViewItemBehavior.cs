using System.Windows;
using System.Windows.Controls;

namespace CodexBackupManager.App.Views;

/// <summary>
/// TreeViewItem이 (바인딩으로) 선택되면 보이는 위치로 스크롤한다(Phase 9_2-17 — 가져온 대화를 목록에서 보여줄 때).
/// 상태를 두지 않는 순수 View 동작이다.
/// </summary>
public static class TreeViewItemBehavior
{
    /// <summary>선택되면 <see cref="FrameworkElement.BringIntoView()"/>를 부를지.</summary>
    public static readonly DependencyProperty BringIntoViewWhenSelectedProperty = DependencyProperty.RegisterAttached(
        "BringIntoViewWhenSelected", typeof(bool), typeof(TreeViewItemBehavior),
        new PropertyMetadata(false, OnChanged));

    /// <summary>getter.</summary>
    public static bool GetBringIntoViewWhenSelected(DependencyObject element) => (bool)element.GetValue(BringIntoViewWhenSelectedProperty);

    /// <summary>setter.</summary>
    public static void SetBringIntoViewWhenSelected(DependencyObject element, bool value) => element.SetValue(BringIntoViewWhenSelectedProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item)
        {
            return;
        }

        item.Selected -= OnSelected;
        if (e.NewValue is true)
        {
            item.Selected += OnSelected;
        }
    }

    private static void OnSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item)
        {
            item.BringIntoView();
        }
    }
}
