using System.Windows;
using System.Windows.Controls;
using CodexBackupManager.App.ViewModels.Import;

namespace CodexBackupManager.App.Views;

/// <summary>가져오기 작업 공간 화면. 로직은 <see cref="ImportWorkspaceViewModel"/>에 있다.</summary>
public partial class ImportWorkspaceView : UserControl
{
    /// <summary>생성자.</summary>
    public ImportWorkspaceView() => InitializeComponent();

    /// <summary>TreeView의 SelectedItem은 바인딩할 수 없어 여기서 ViewModel로 넘긴다(상태는 두지 않는다).</summary>
    private void ImportTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ImportWorkspaceViewModel viewModel)
        {
            viewModel.SelectNode(e.NewValue);
        }
    }
}
