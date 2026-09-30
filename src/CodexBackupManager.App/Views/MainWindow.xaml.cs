using System.Windows;
using System.Windows.Controls;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Views;

/// <summary>메인 창. 로직은 <see cref="ViewModels.MainViewModel"/>에 있다.</summary>
public partial class MainWindow : Window
{
    /// <summary>생성자.</summary>
    public MainWindow()
    {
        InitializeComponent();
        // Phase 8 — 창 제목에 배포 버전을 표시한다(XAML에 버전 문자열을 따로 하드코딩하지 않는다).
        Title = AppVersionInfo.DisplayName;
    }

    /// <summary>
    /// TreeView는 <c>SelectedItem</c>을 바인딩할 수 없어(읽기 전용) 코드 비하인드에서 이어준다.
    /// 프로젝트 노드를 선택하면 오른쪽 뷰어를 비운다.
    /// </summary>
    /// <summary>
    /// 창이 활성화될 때 가져오기 화면이 Codex 실행 여부를 다시 확인하게 한다(Phase 9_2-14, 설계 §7.1).
    /// </summary>
    private void Window_Activated(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ImportWorkspace.CheckCodexRunning();
        }
    }

    private void ConversationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectConversation(e.NewValue as ConversationNodeViewModel);
        }
    }
}
