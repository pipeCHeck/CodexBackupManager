using System;
using System.IO;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 4 요구사항 7(카탈로그 refresh 시 선택 유지/정리, Codex Home 변경 시 선택 초기화)을 실제
/// 탐지 + 카탈로그 빌드 파이프라인으로 검증한다. <c>tests/Fixtures/CodexHome</c>(합성 픽스처, 실제
/// 사용자 데이터 아님)을 임시 폴더로 복사해 사용하며, 원본 픽스처와 사용자의 실제 <c>.codex</c>는
/// 절대 건드리지 않는다.
/// </summary>
public sealed class MainViewModelCatalogRefreshTests : IAsyncLifetime
{
    private string? _homeA;
    private string? _homeB;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TryDelete(_homeA);
        TryDelete(_homeB);
        return Task.CompletedTask;
    }

    private static void TryDelete(string? path)
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

    private static MainViewModel CreateViewModel(string directory, Func<string?> folderPicker) => new(
        new CodexDetectionService(),
        new SettingsStore(Path.Combine(directory, "settings.json")),
        new FileLogger(Path.Combine(directory, "logs")),
        folderPicker);

    /// <summary>
    /// <c>ChangeFolderCommand</c>/<c>RefreshCommand</c>는 fire-and-forget이라(<c>_ = ...Async()</c>),
    /// 커맨드 실행 직후에는 아직 탐지조차 시작 전일 수 있다. 두 단계로 나눠 기다려야 경쟁 조건이 없다:
    /// 1) <c>IsBusy</c>는 탐지 메서드의 첫 줄에서 동기적으로 true가 되므로, 이게 다시 false가 될 때까지
    ///    기다리면 그 시점엔 이미 카탈로그 빌드(fire-and-forget)가 "시작"된 상태임이 보장된다.
    /// 2) 그 다음 <c>IsCatalogLoading</c>이 false가 될 때까지 기다리면 카탈로그 빌드까지 끝난 것이다.
    /// </summary>
    private static async Task WaitUntilIdle(MainViewModel viewModel)
    {
        await WaitUntilFalse(() => viewModel.IsBusy, "탐지");
        await WaitUntilFalse(() => viewModel.IsCatalogLoading, "카탈로그 로딩");
    }

    private static async Task WaitUntilFalse(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{label}이(가) 제한 시간 안에 끝나지 않았습니다.");
            }

            await Task.Delay(10);
        }
    }

    private static void DeleteThreadFromStateDb(string codexHome, string threadId)
    {
        string dbPath = Path.Combine(codexHome, "state_4.sqlite");
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", threadId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task 같은_Codex_Home을_다시_확인하면_유효한_선택은_유지된다()
    {
        _homeA = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        var testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        MainViewModel viewModel = CreateViewModel(testDir, () => _homeA);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        Assert.True(viewModel.TotalConversationCount >= 2); // 픽스처: thread 001, 002 — 실측 전제 확인.
        viewModel.SelectAllConversationsCommand.Execute(null);
        var beforeRefresh = viewModel.GetSelectedThreadIdsSnapshot();
        Assert.Equal(viewModel.TotalConversationCount, beforeRefresh.Count);

        // 같은 Home을 "다시 확인"한다(파일은 그대로 — 삭제/변경 없음). RefreshCommand는 CODEX_HOME/
        // %USERPROFILE%\.codex를 이 픽스처보다 우선 탐색하므로(CLAUDE.md §4), 이 테스트를 실행하는
        // 실제 PC에 진짜 .codex가 있으면 RefreshCommand가 그쪽으로 가버려 "같은 Home"이 아니게 된다.
        // 그래서 같은 경로를 다시 지정하는 ChangeFolderCommand로 "같은 Home 재확인"을 재현한다.
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        var afterRefresh = viewModel.GetSelectedThreadIdsSnapshot();
        Assert.Equal(beforeRefresh, afterRefresh); // 선택이 그대로 유지된다.
    }

    [Fact]
    public async Task 사라진_ThreadId는_다시_확인하면_선택에서_제거된다()
    {
        _homeA = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        var testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        MainViewModel viewModel = CreateViewModel(testDir, () => _homeA);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        viewModel.SelectAllConversationsCommand.Execute(null);
        int selectedBefore = viewModel.SelectedConversationCount;
        Assert.True(selectedBefore >= 2);

        const string disappearingThreadId = "01a00000-0000-7000-8000-000000000002";
        Assert.Contains(disappearingThreadId, viewModel.GetSelectedThreadIdsSnapshot());

        // state db에서 thread 하나를 지워 "카탈로그에서 더 이상 보이지 않게 됨"을 재현한다
        // (CodexCatalogBuilder는 state db의 threads 행을 기준으로 카탈로그를 만든다).
        DeleteThreadFromStateDb(_homeA, disappearingThreadId);

        // 같은 이유로(위 테스트 주석 참고) ChangeFolderCommand로 같은 경로를 다시 확인한다.
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        var afterRefresh = viewModel.GetSelectedThreadIdsSnapshot();
        Assert.DoesNotContain(disappearingThreadId, afterRefresh); // 사라진 thread는 선택에서도 빠졌다.
        Assert.Equal(selectedBefore - 1, afterRefresh.Count); // 나머지는 그대로 유지된다.
    }

    [Fact]
    public async Task 다른_Codex_Home으로_바꾸면_이전_선택이_전부_초기화된다()
    {
        _homeA = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        _homeB = RepositoryFixtures.CopyCodexHomeFixtureToTemp(); // 내용은 같지만 "다른 Home"(다른 경로)이다.
        var testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        string? pickerResult = _homeA;
        MainViewModel viewModel = CreateViewModel(testDir, () => pickerResult);

        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);
        viewModel.SelectAllConversationsCommand.Execute(null);
        Assert.True(viewModel.HasSelection);

        pickerResult = _homeB;
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        Assert.False(viewModel.HasSelection);
        Assert.Equal(0, viewModel.SelectedConversationCount);
    }
}
