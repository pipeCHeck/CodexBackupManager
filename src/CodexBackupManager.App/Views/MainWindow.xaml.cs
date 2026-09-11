using System.Windows;
using System.Windows.Controls;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Views;

/// <summary>메인 창. 로직은 <see cref="ViewModels.MainViewModel"/>에 있다.</summary>
public partial class MainWindow : Window
{
    /// <summary>생성자.</summary>
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// TreeView는 <c>SelectedItem</c>을 바인딩할 수 없어(읽기 전용) 코드 비하인드에서 이어준다.
    /// 프로젝트 노드를 선택하면 오른쪽 뷰어를 비운다.
    /// </summary>
    private void ConversationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectConversation(e.NewValue as ConversationNodeViewModel);
        }
    }
}
