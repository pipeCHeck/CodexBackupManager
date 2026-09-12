using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 07_01 — <see cref="MainViewModel.ApplyCommand"/>가 frozen <see cref="Backup.Import.ImportPlan"/>을
/// 실제 production 진입점(<see cref="Restore.RestoreExecutor.Apply(Backup.Import.ImportPlan,string,string?,Restore.IRestoreFaultInjectionHook?,Action{string}?,System.Threading.CancellationToken)"/>)에
/// 그대로 넘기는지 확인한다. 여기서는 안전성 판단(Preflight/backup pin/Operation Plan 재검증)을
/// 다시 테스트하지 않는다 — 그건 Restore.Tests의 책임이다. 이 테스트는 오직 ViewModel 배선(확인
/// 대화상자 → RestoreExecutor 호출 → 상태 문구/커맨드 활성화 반영)만 확인한다. 실제 사용자
/// <c>.codex</c>는 전혀 건드리지 않는다 — 항상 커밋된 fixture를 임시 폴더로 복사해서 쓴다.
/// </summary>
public sealed class MainViewModelApplyTests : IAsyncLifetime
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
        Func<string?>? projectPathPicker = null,
        Func<string, string, bool>? confirmDialog = null)
        => new(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_testDir!, $"settings-{Guid.NewGuid():N}.json")),
            new FileLogger(Path.Combine(_testDir!, "logs")),
            folderPicker: () => home,
            exportFilePicker: exportFilePicker,
            importFilePicker: importFilePicker,
            projectPathPicker: projectPathPicker,
            confirmDialog: confirmDialog);

    /// <summary>
    /// 같은 fixture를 복사한 source/target Codex Home 사이에서 Export → Import Preview까지 실행해
    /// frozen <see cref="MainViewModel.CurrentImportPlan"/>이 준비된 importer를 돌려준다. Export/target
    /// 쪽 backup 경로/폴더는 인스턴스 필드(<see cref="_sourceHome"/> 등)에 캐시해 두므로, 같은 테스트
    /// 안에서 confirmDialog만 다른 두 번째 importer가 필요하면 <see cref="CreateImporterForFrozenBackup"/>을
    /// 다시 부르면 된다(백업 파일을 다시 만들지 않는다).
    /// </summary>
    private async Task<MainViewModel> BuildImporterWithFrozenPlanAsync(Func<string, string, bool>? confirmDialog = null)
    {
        _sourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _targetHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _backupPath = Path.Combine(_testDir, "apply.codexbackup");

        MainViewModel exporter = CreateViewModel(_sourceHome, exportFilePicker: _ => _backupPath);
        exporter.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsBusy, "탐지");
        await WaitUntilFalse(() => exporter.IsCatalogLoading, "카탈로그 로딩");
        exporter.SelectAllConversationsCommand.Execute(null);
        exporter.ExportCommand.Execute(null);
        await WaitUntilFalse(() => exporter.IsExporting, "Export");
        Assert.True(File.Exists(_backupPath));

        return await CreateImporterForFrozenBackupAsync(confirmDialog);
    }

    /// <summary>
    /// 이미 만들어진 <see cref="_backupPath"/>를 대상으로 새 importer의 Preview를 만든다. fixture의
    /// 프로젝트 경로(<c>C:\Fixture\Projects\...</c>류의 고정 문자열)는 실제로는 이 테스트 머신에
    /// 존재하지 않으므로, Apply의 fresh preflight(<see cref="ImportPlanPreflightValidator"/>)가
    /// <c>TargetPathUnavailable</c>로 정확히 막는다(이건 버그가 아니라 Phase 06_02에서 이미 확인한
    /// 실제 데이터 특성이다) — 그래서 여기서 실제로 존재하는 임시 폴더로 수동 재지정까지 마친 뒤
    /// 돌려준다.
    /// </summary>
    private async Task<MainViewModel> CreateImporterForFrozenBackupAsync(Func<string, string, bool>? confirmDialog = null)
    {
        string overrideFolder = Path.Combine(_testDir!, $"project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(overrideFolder);

        MainViewModel importer = CreateViewModel(
            _targetHome!, importFilePicker: () => _backupPath, projectPathPicker: () => overrideFolder,
            confirmDialog: confirmDialog);
        importer.ChangeFolderCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsBusy, "탐지");
        await WaitUntilFalse(() => importer.IsCatalogLoading, "카탈로그 로딩");

        importer.ImportPreviewCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsImportPreviewLoading, "Import Preview");
        Assert.NotNull(importer.CurrentImportPreview);

        foreach (ImportProjectRowViewModel project in importer.CurrentImportPreview!.Projects.Where(p => p.CanOverridePath))
        {
            project.OverridePathCommand!.Execute(null);
        }

        Assert.NotNull(importer.CurrentImportPlan);
        Assert.True(importer.CurrentImportPlan!.IsApplyReady);

        return importer;
    }

    [Fact]
    public async Task Preview_직후에는_ApplyCommand를_실행할_수_있고_Plan이_없으면_실행할_수_없다()
    {
        MainViewModel importer = await BuildImporterWithFrozenPlanAsync();

        // Preview/override 전(생성 직후)에는 frozen Plan이 없으므로 CanExecute도 항상 false였다 —
        // 이 시점엔 이미 Preview가 끝나 있으니 대신 CloseImportPreview 이후 다시 false로 돌아오는지로
        // "Plan이 없으면 실행할 수 없다"를 확인한다.
        Assert.True(importer.ApplyCommand.CanExecute(null));

        importer.CloseImportPreviewCommand.Execute(null);
        Assert.False(importer.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task 확인_대화상자에서_취소하면_아무것도_적용하지_않는다()
    {
        string? shownMessage = null;
        string? shownTitle = null;
        MainViewModel importer = await BuildImporterWithFrozenPlanAsync((message, title) =>
        {
            shownMessage = message;
            shownTitle = title;
            return false;
        });

        importer.ApplyCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsApplying, "Apply");

        Assert.NotNull(shownMessage);
        Assert.Contains("Snapshot을 생성합니다", shownMessage);
        Assert.Contains("Codex가 완전히 종료되어 있어야 합니다", shownMessage);
        Assert.Equal("적용 확인", shownTitle);
        Assert.False(importer.IsApplying);
        Assert.Null(importer.ApplyStatusText);
        // 취소는 Apply를 아예 시작하지 않는다 — Plan은 여전히 살아있다.
        Assert.NotNull(importer.CurrentImportPlan);
    }

    [Fact]
    public async Task 완전히_동일한_대상에_Apply하면_NothingToDo로_끝나고_Plan을_무효화한다()
    {
        // 같은 fixture를 그대로 복사한 두 Codex Home 사이의 Apply이므로 opPlan은 항상 비어 있다
        // (전부 Identical/NoOp) — Snapshot조차 만들지 않는 가장 안전한 경로다. RestoreExecutor는
        // 여기서 production 진입점(CodexProcessGuard.SystemRunningProcessLister 포함)을 실제로
        // 그대로 탄다 — 테스트 프로세스 이름이 "Codex"가 아니므로 통과한다.
        MainViewModel importer = await BuildImporterWithFrozenPlanAsync((_, _) => true);

        Assert.True(importer.ApplyCommand.CanExecute(null));
        importer.ApplyCommand.Execute(null);
        await WaitUntilFalse(() => importer.IsApplying, "Apply");

        Assert.Contains("변경 사항이 없", importer.ApplyStatusText);
        Assert.Null(importer.CurrentImportPlan);
        Assert.False(importer.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void KnownLimitationsText는_항상_비어있지_않다()
    {
        Assert.False(string.IsNullOrWhiteSpace(MainViewModel.KnownLimitationsText));
    }
}
