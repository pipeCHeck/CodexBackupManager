using System;
using System.Windows;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Views;

/// <summary>(Phase 9_4-02) "가져오기 기록" 창. 로직은 <see cref="ImportHistoryViewModel"/>에 있다.</summary>
public partial class ImportHistoryWindow : Window
{
    /// <summary>생성자.</summary>
    public ImportHistoryWindow(ImportHistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>메인 창 소유의 모달 창으로 띄운다. 이 앱이 실행 중이 아니면(단위 테스트) 아무것도 하지 않는다.</summary>
    public static void ShowFor(ImportHistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Application.Current is not CodexBackupManager.App.App)
        {
            return;
        }

        var window = new ImportHistoryWindow(viewModel);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }
}
