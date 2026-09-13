using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
        logger.Info($"{AppVersionInfo.DisplayName} 시작");

        var viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(),
            logger,
            FolderPicker.PickCodexHome);

        // ── 임시 진단(Phase 4 UI 크래시 조사) ────────────────────────────────────────
        // 실제 스크롤 중 반복 종료가 보고됐는데 지금까지 미처리 예외를 잡는 장치가 없어 스택을
        // 못 잡았다. 세 경로 모두 증거만 남기고 Handled를 건드리지 않는다 — 앱은 원래처럼 죽는다.
        DispatcherUnhandledException += (_, args) =>
            CrashDiagnostics.LogCrash(logger, "Dispatcher", args.Exception, viewModel);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                CrashDiagnostics.LogCrash(logger, "AppDomain", exception, viewModel);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashDiagnostics.LogCrash(logger, "UnobservedTask", args.Exception, viewModel);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        _ = viewModel.InitializeAsync();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        new FileLogger().Info($"{AppVersionInfo.DisplayName} 종료");
        base.OnExit(e);
    }
}
