using System.Windows;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.Views;
using CodexBackupManager.Codex;

namespace CodexBackupManager.App;

/// <summary>애플리케이션 진입점. 의존성을 조립하고 메인 창을 띄운다.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();

        var logger = new FileLogger();
        logger.Info("CodexBackupManager 시작 (Phase 1)");

        var viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(),
            logger,
            FolderPicker.PickCodexHome);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        _ = viewModel.InitializeAsync();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        new FileLogger().Info("CodexBackupManager 종료");
        base.OnExit(e);
    }
}
