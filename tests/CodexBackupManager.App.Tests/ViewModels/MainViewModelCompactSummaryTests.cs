using System;
using System.IO;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 04_07 — 상단 진단 영역을 Compact/Expand 구조로 바꾸면서 도입한
/// <see cref="MainViewModel.CompactSummaryText"/>를 검증한다. 접힌 기본 상태에서도 사용자가
/// "Desktop / CLI / Sessions / Threads" 핵심 지표만은 한눈에 볼 수 있어야 한다는 요구사항을
/// 실제 탐지 + 카탈로그 빌드 파이프라인(합성 픽스처 사용, 실제 사용자 데이터 아님)으로 확인한다.
/// </summary>
public sealed class MainViewModelCompactSummaryTests : IAsyncLifetime
{
    private string? _home;
    private string? _invalidFolder;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TryDelete(_home);
        TryDelete(_invalidFolder);
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

    [Fact]
    public async Task 연결_성공하면_쉬운_버전_한_줄이_보이고_진단_값은_상세_정보에_있다()
    {
        _home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        var testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        MainViewModel viewModel = CreateViewModel(testDir, () => _home);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        Assert.True(viewModel.IsConnected);
        // Phase 9_U-04 — 기본 화면은 "Codex 버전 99.123"(fixture Desktop 99.123.45678)만, 진단 값은 [상세 정보] 표에 그대로 남는다.
        Assert.Equal("Codex 버전 99.123", viewModel.CompactSummaryText);
        Assert.Contains(viewModel.Rows, r => r.Label == "Codex Desktop" && r.Value == "99.123.45678");
        Assert.Contains(viewModel.Rows, r => r.Label == "Codex CLI");
        Assert.Contains(viewModel.Rows, r => r.Label == "Sessions");
        Assert.Contains(viewModel.Rows, r => r.Label == "Threads (state DB)");
        Assert.Contains(viewModel.Rows, r => r.Label == "목록 통계" && r.Value.Contains("rollout 파일", StringComparison.Ordinal) && r.Value.Contains("ms", StringComparison.Ordinal));
        Assert.Matches(@"^프로젝트 \d+개 · 대화 \d+개$", viewModel.CatalogSummaryText);
    }

    [Fact]
    public async Task 탐지_실패하면_요약이_비워진다()
    {
        _invalidFolder = Path.Combine(Path.GetTempPath(), "cbm-app-tests", "not-a-codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_invalidFolder);
        var testDir = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        MainViewModel viewModel = CreateViewModel(testDir, () => _invalidFolder);
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntilIdle(viewModel);

        Assert.False(viewModel.IsConnected);
        Assert.Null(viewModel.CompactSummaryText);
    }
}
