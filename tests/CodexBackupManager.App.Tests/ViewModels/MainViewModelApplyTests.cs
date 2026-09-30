using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Restore;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// 메인 화면에 남은 적용 관련 동작: 완료되지 못한 이전 적용 배너와 [이전 상태로 복구](Phase 07_02/07_03), 내보내기 전
/// Codex 실행 안내(9_2-20). 가져오기 화면(분석·선택·적용·결과)은 <c>ImportWorkspaceViewModelTests</c>가 검증한다(9_2-08에서 이관).
/// 실제 사용자 <c>.codex</c>는 전혀 건드리지 않는다 — 항상 커밋된 fixture를 임시 폴더로 복사해서 쓴다.
/// </summary>
public sealed class MainViewModelApplyTests : IAsyncLifetime
{
    private string? _sourceHome;
    private string? _targetHome;
    private string? _testDir;

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

    /// <summary>호스트에서 실제로 실행 중인 프로세스와 무관한 "Codex 없음" 목록(Phase 9_1-01).</summary>
    private static readonly CodexProcessGuard.RunningProcessLister NoCodexRunning = () => [];

    /// <summary>가짜 "Codex 실행 중" 목록.</summary>
    private static readonly CodexProcessGuard.RunningProcessLister FakeCodexRunning =
        () => [new RunningProcessInfo("Codex", null)];

    /// <summary>
    /// Phase 9_1-01/9_2-2 — ViewModel이 쓰는 프로세스 목록(복구, 내보내기 안내, 가져오기 화면)을 주입한다.
    /// </summary>
    private static void UseProcessLister(MainViewModel viewModel, CodexProcessGuard.RunningProcessLister lister)
    {
        viewModel.ProcessLister = lister;
        viewModel.ImportWorkspace.ProcessLister = lister;
    }

    private MainViewModel CreateViewModel(
        string home,
        Func<string, string?>? exportFilePicker = null,
        Func<string?>? importFilePicker = null,
        Func<string?>? projectPathPicker = null,
        Func<string, string, bool>? confirmDialog = null,
        Func<string>? snapshotRootProvider = null,
        CodexProcessGuard.RunningProcessLister? processLister = null)
    {
        var viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir!, $"settings-{Guid.NewGuid():N}.json")),
            new FileLogger(Path.Combine(_testDir!, "logs")),
            folderPicker: () => home,
            exportFilePicker: exportFilePicker,
            importFilePicker: importFilePicker,
            projectPathPicker: projectPathPicker,
            confirmDialog: confirmDialog,
            snapshotRootProvider: snapshotRootProvider ?? (() => Path.Combine(_testDir!, "snapshots")));
        UseProcessLister(viewModel, processLister ?? NoCodexRunning);
        return viewModel;
    }

    [Fact]
    public async Task 내보내기_전에_Codex가_실행_중이면_안내만_하고_내보내기는_계속한다()
    {
        // Phase 9_2-20 — 차단하지 않는다.
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        string backupPath = Path.Combine(_testDir, "notice.codexbackup");
        MainViewModel viewModel = CreateViewModel(_sourceHome, exportFilePicker: _ => backupPath, processLister: FakeCodexRunning);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        await WaitUntilFalse(() => viewModel.IsCatalogLoading, "카탈로그 로딩");
        viewModel.SelectAllConversationsCommand.Execute(null);

        viewModel.ExportCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsExporting, "Export");

        Assert.Contains("Codex에서 사용 중인 대화는 내보내기 도중 바뀌면 실패할 수 있습니다", viewModel.ExportNoticeText);
        Assert.True(File.Exists(backupPath));

        UseProcessLister(viewModel, NoCodexRunning);
        viewModel.ExportCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsExporting, "Export");
        Assert.Null(viewModel.ExportNoticeText);
    }

    [Fact]
    public async Task 완료되지_못한_이전_Apply가_있으면_가져오기_화면에서_가져오기가_막힌다()
    {
        // Phase 07_02 요구사항 7 → 9_2-08: 배너와 [이전 상태로 복구]는 메인에 남고, 가져오기 버튼은 화면에서 막힌다.
        using var harness = new ImportWorkspaceHarness();
        harness.RemoveFromTarget(ImportWorkspaceHarness.Thread2);
        string backup = await harness.ExportAsync();
        string staleSnapshotDir = Path.Combine(harness.SnapshotRoot, "stale");
        Directory.CreateDirectory(staleSnapshotDir);
        RestoreTransactionJournalStore.Write(
            staleSnapshotDir, new RestoreTransactionJournal("stale", harness.TargetHome, RestoreTransactionState.Applying, DateTimeOffset.UtcNow));

        MainViewModel main = harness.CreateMainViewModel(harness.TargetHome, importFilePicker: () => backup);
        await ImportWorkspaceHarness.ConnectAsync(main);
        Assert.True(main.HasIncompleteApply);
        Assert.True(main.RecoverIncompleteApplyCommand.CanExecute(null));

        main.OpenImportWorkspaceCommand.Execute(null);
        await ImportWorkspaceHarness.WaitUntil(() => main.ImportWorkspace.State == ImportWorkspaceState.Editing, "가져오기 화면");

        Assert.Equal(1, main.ImportWorkspace.Summary!.ImportCount);
        Assert.False(main.ImportWorkspace.CanImport);
        Assert.False(main.ImportWorkspace.ImportCommand.CanExecute(null));
        Assert.Contains("이전 상태로 복구", main.ImportWorkspace.SummaryHint);
    }

    [Fact]
    public async Task 이전_상태로_복구를_승인하면_완료되지_못한_Apply가_해소된다()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();

        string snapshotRoot = Path.Combine(_testDir, "snapshots");
        string staleSnapshotDir = Path.Combine(snapshotRoot, "stale");
        Directory.CreateDirectory(staleSnapshotDir);

        // 실제로 존재하는 파일 하나를 snapshot 대상으로 등록해야 Recover가 (아무리 사소해도) 실제
        // Rollback 절차를 완주할 수 있다 — 존재하지 않는 파일은 Rollback이 아예 할 일이 없다.
        string dummyTarget = Path.Combine(_testDir, "dummy.txt");
        File.WriteAllText(dummyTarget, "original");
        SnapshotCreateResult snapshot = SnapshotService.Create(snapshotRoot, _sourceHome, "deadbeef", [("dummy", dummyTarget)]);
        Assert.True(snapshot.Success, snapshot.FailureReason);
        RestoreTransactionJournalStore.Write(
            snapshot.SnapshotDirectory!,
            new RestoreTransactionJournal(snapshot.Manifest!.SnapshotId, _sourceHome, RestoreTransactionState.Applying, DateTimeOffset.UtcNow));
        File.WriteAllText(dummyTarget, "mutated-by-interrupted-apply");

        bool confirmShown = false;
        MainViewModel viewModel = CreateViewModel(
            _sourceHome,
            confirmDialog: (_, _) => { confirmShown = true; return true; },
            snapshotRootProvider: () => snapshotRoot);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");

        Assert.True(viewModel.HasIncompleteApply);

        viewModel.RecoverIncompleteApplyCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (viewModel.HasIncompleteApply)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("복구가 제한 시간 안에 끝나지 않았습니다.");
            }

            await Task.Delay(10);
        }

        Assert.True(confirmShown);
        Assert.False(viewModel.HasIncompleteApply);
        Assert.Equal("original", File.ReadAllText(dummyTarget));
    }

    [Fact]
    public async Task 다른_Home의_미완료_Apply는_현재_Home에_나타나지_않고_그_Home을_다시_선택하면_나타난다()
    {
        // Phase 07_03 요구사항 2 — 수동 Codex Home 선택을 지원하므로, 완료되지 못한 이전 Apply는
        // 그 Apply가 실제로 겨냥했던 Home에서만 배너로 나타나야 한다.
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _targetHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        string homeA = _sourceHome;
        string homeB = _targetHome;

        string snapshotRoot = Path.Combine(_testDir, "snapshots");
        string staleSnapshotDir = Path.Combine(snapshotRoot, "stale-home-a");
        Directory.CreateDirectory(staleSnapshotDir);
        RestoreTransactionJournalStore.Write(
            staleSnapshotDir, new RestoreTransactionJournal("stale-home-a", homeA, RestoreTransactionState.Applying, DateTimeOffset.UtcNow));

        string currentFolder = homeB;
        var viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir, $"settings-{Guid.NewGuid():N}.json")),
            new FileLogger(Path.Combine(_testDir, "logs")),
            folderPicker: () => currentFolder,
            snapshotRootProvider: () => snapshotRoot);
        UseProcessLister(viewModel, NoCodexRunning);

        // Home B를 먼저 선택한다 — Home A에 대한 미완료 Apply가 여기 나타나면 안 된다.
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        Assert.False(viewModel.HasIncompleteApply);

        // Home A로 바꾸면 그제야 나타나야 한다.
        currentFolder = homeA;
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        Assert.True(viewModel.HasIncompleteApply);

        // 다시 Home B로 바꾸면 사라져야 한다 — Home A의 snapshot을 지우거나 건드리지 않고, 단지
        // 지금 화면에는 보이지 않는 것뿐이다.
        currentFolder = homeB;
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        Assert.False(viewModel.HasIncompleteApply);

        // Home A의 snapshot/journal 자체는 그대로 남아 있다.
        Assert.True(Directory.Exists(staleSnapshotDir));
        RestoreTransactionJournal? journal = RestoreTransactionJournalStore.TryRead(staleSnapshotDir);
        Assert.Equal(RestoreTransactionState.Applying, journal!.State);
    }
}
