using System;
using System.IO;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Backup.Validation;
using CodexBackupManager.Codex;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 5 — <see cref="MainViewModel"/>이 <c>GetSelectedThreadIdsSnapshot()</c>을 그대로 Export
/// core 파이프라인(ExportPlanBuilder → ManifestBuilder → BackupWriter)에 넘기는지 실제 fixture
/// Codex Home(합성 데이터, 실제 사용자 데이터 아님)으로 확인한다.
/// </summary>
public sealed class MainViewModelExportTests : IAsyncLifetime
{
    private string? _home;
    private string? _testDir;
    private string? _exportPath;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_home);
        TryDeleteDirectory(_testDir);
        TryDeleteFile(_exportPath);
        return Task.CompletedTask;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitUntilFalse(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{label}이(가) 제한 시간 안에 끝나지 않았습니다.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 선택_0개면_ExportCommand가_비활성화된다()
    {
        _home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        MainViewModel viewModel = new(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir, "settings.json")),
            new FileLogger(Path.Combine(_testDir, "logs")),
            folderPicker: () => _home);

        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        await WaitUntilFalse(() => viewModel.IsCatalogLoading, "카탈로그 로딩");

        Assert.False(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task 선택한_대화를_실제로_codexbackup으로_내보낸다()
    {
        _home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _exportPath = Path.Combine(_testDir, "export-test.codexbackup");

        MainViewModel viewModel = new(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir, "settings.json")),
            new FileLogger(Path.Combine(_testDir, "logs")),
            folderPicker: () => _home,
            exportFilePicker: _ => _exportPath);

        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        await WaitUntilFalse(() => viewModel.IsCatalogLoading, "카탈로그 로딩");

        viewModel.SelectAllConversationsCommand.Execute(null);
        Assert.True(viewModel.HasSelection);

        Assert.True(viewModel.ExportCommand.CanExecute(null));
        viewModel.ExportCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsExporting, "Export");

        Assert.True(File.Exists(_exportPath));
        Assert.Contains("완료", viewModel.ExportStatusText);

        BackupValidationResult validation = BackupValidator.Validate(_exportPath);
        Assert.True(validation.Success, string.Join("; ", validation.Errors));

        using BackupReader reader = BackupReader.Open(_exportPath);
        Backup.Manifest.BackupManifest manifest = reader.ReadManifest();
        Assert.Equal(viewModel.SelectedConversationCount, manifest.ConversationCount);
    }
}
