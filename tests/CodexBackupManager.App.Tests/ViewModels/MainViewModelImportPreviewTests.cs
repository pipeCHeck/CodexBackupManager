using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 6 — <see cref="MainViewModel"/>이 <see cref="MainViewModel.ImportPreviewCommand"/>를
/// core(<see cref="Backup.Import.ImportPreviewBuilder"/>)에 그대로 위임하는지, 실제 fixture Codex
/// Home(합성 데이터)으로 Export → Import Preview 왕복까지 확인한다.
/// </summary>
public sealed class MainViewModelImportPreviewTests : IAsyncLifetime
{
    private string? _sourceHome;
    private string? _targetHome;
    private string? _testDir;
    private string? _backupPath;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_sourceHome);
        TryDeleteDirectory(_targetHome);
        TryDeleteDirectory(_testDir);
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

    private MainViewModel CreateViewModel(
        string home,
        Func<string, string?>? exportFilePicker = null,
        Func<string?>? importFilePicker = null,
        Func<string?>? projectPathPicker = null)
        => new(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir!, $"settings-{Guid.NewGuid():N}.json")),
            new FileLogger(Path.Combine(_testDir!, "logs")),
            folderPicker: () => home,
            exportFilePicker: exportFilePicker,
            importFilePicker: importFilePicker,
            projectPathPicker: projectPathPicker);

    [Fact]
    public async Task 같은_fixture로_Export한_뒤_같은_fixture에_다시_Import_Preview하면_전부_Identical이다()
    {
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _targetHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _backupPath = Path.Combine(_testDir, "roundtrip.codexbackup");

        MainViewModel exporter = CreateViewModel(_sourceHome, exportFilePicker: _ => _backupPath);
        exporter.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsBusy, "탐지");
        await WaitUntilFalse(() => exporter.IsCatalogLoading, "카탈로그 로딩");
        exporter.SelectAllConversationsCommand.Execute(null);
        exporter.ExportCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsExporting, "Export");
        Assert.True(File.Exists(_backupPath));

        MainViewModel importer = CreateViewModel(_targetHome, importFilePicker: () => _backupPath);
        importer.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsBusy, "탐지");
        await WaitUntilFalse(() => importer.IsCatalogLoading, "카탈로그 로딩");

        Assert.True(importer.ImportPreviewCommand.CanExecute(null));
        importer.ImportPreviewCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsImportPreviewLoading, "Import Preview");

        Assert.True(importer.HasImportPreview);
        Assert.True(importer.CurrentImportPreview!.Success);
        Assert.Contains("완료", importer.ImportStatusText);

        var allConversations = importer.CurrentImportPreview.Projects.SelectMany(p => p.Conversations).ToList();
        Assert.NotEmpty(allConversations);
        Assert.All(allConversations, c => Assert.Equal("동일", c.StatusLabel));

        importer.CloseImportPreviewCommand.Execute(null);
        Assert.False(importer.HasImportPreview);
    }

    [Fact]
    public async Task 프로젝트_경로를_폴더_선택으로_수동_재지정하면_ManuallyLinked로_바뀐다()
    {
        // fixture의 프로젝트 루트 경로는 global-state.json에 고정 문자열(C:\Fixture\Projects\Alpha)로
        // 박혀 있어 두 fixture 복사본 모두 같은 값을 보고하므로 자동으로 AutoLinked된다 — 이 테스트는
        // "AutoLinked 프로젝트도 사용자가 원하면 수동으로 덮어쓸 수 있다"는 요구사항을 확인한다.
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _targetHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _backupPath = Path.Combine(_testDir, "override-roundtrip.codexbackup");

        MainViewModel exporter = CreateViewModel(_sourceHome, exportFilePicker: _ => _backupPath);
        exporter.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsBusy, "탐지");
        await WaitUntilFalse(() => exporter.IsCatalogLoading, "카탈로그 로딩");
        exporter.SelectAllConversationsCommand.Execute(null);
        exporter.ExportCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsExporting, "Export");
        Assert.True(File.Exists(_backupPath));

        string overrideFolder = Path.Combine(_testDir, "manually-chosen-project");
        Directory.CreateDirectory(overrideFolder);

        MainViewModel importer = CreateViewModel(
            _targetHome, importFilePicker: () => _backupPath, projectPathPicker: () => overrideFolder);
        importer.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsBusy, "탐지");
        await WaitUntilFalse(() => importer.IsCatalogLoading, "카탈로그 로딩");

        importer.ImportPreviewCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsImportPreviewLoading, "Import Preview");
        Assert.True(importer.HasImportPreview);

        ImportProjectRowViewModel? overridableProject = importer.CurrentImportPreview!.Projects
            .FirstOrDefault(p => p.CanOverridePath);
        Assert.NotNull(overridableProject);
        Assert.NotNull(overridableProject!.OverridePathCommand);

        overridableProject.OverridePathCommand!.Execute(null);

        ImportProjectRowViewModel updatedProject = importer.CurrentImportPreview!.Projects
            .Single(p => p.DisplayName == overridableProject.DisplayName);
        Assert.Contains("사용자가 지정함", updatedProject.PathMappingStatusText);
        Assert.Contains("재지정", importer.ImportStatusText);
    }

    [Fact]
    public async Task malformed_backup_파일은_실패_상태로_Preview를_표시한다()
    {
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        string brokenPath = Path.Combine(_testDir, "broken.codexbackup");
        File.WriteAllBytes(brokenPath, [0x00, 0x01, 0x02]);

        MainViewModel viewModel = CreateViewModel(_sourceHome, importFilePicker: () => brokenPath);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        await WaitUntilFalse(() => viewModel.IsCatalogLoading, "카탈로그 로딩");

        viewModel.ImportPreviewCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsImportPreviewLoading, "Import Preview");

        Assert.True(viewModel.HasImportPreview);
        Assert.False(viewModel.CurrentImportPreview!.Success);
        Assert.Contains("사용할 수 없", viewModel.ImportStatusText);
    }
}
