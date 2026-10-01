using System;
using System.Windows;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Views;

/// <summary>(Phase 9_6-01) "내보내기 완료" 대화상자. 로직은 <see cref="ExportCompletedViewModel"/>에 있다.</summary>
public partial class ExportCompletedWindow : Window
{
    /// <summary>생성자.</summary>
    public ExportCompletedWindow(ExportCompletedViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>
    /// 메인 창을 소유자로 모달로 띄운다(MainViewModel의 기본 표시 방법). 이 앱(<see cref="App"/>)이 실행 중이 아니면(예: 단위 테스트)
    /// 아무것도 하지 않는다 — 모달 창이 테스트를 멈추지 않게.
    /// </summary>
    public static void ShowFor(ExportCompletedViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Application.Current is not CodexBackupManager.App.App)
        {
            return;
        }

        var window = new ExportCompletedWindow(viewModel);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
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
