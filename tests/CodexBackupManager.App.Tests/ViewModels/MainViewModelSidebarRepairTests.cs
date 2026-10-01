using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Restore;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_5a-04 — 메인 화면의 "이 앱으로 만든 프로젝트 N개가 Codex 사이드바에 보이지 않습니다. [사이드바에 표시]" 줄.
/// 대상 PC는 fixture 복사본(temp)이다. 실제 <c>.codex</c>는 쓰지 않는다.
/// </summary>
public sealed class MainViewModelSidebarRepairTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string GlobalStatePath => ImportWorkspaceHarness.GlobalStatePath(_h.TargetHome);

    private async Task<MainViewModel> ConnectAsync()
    {
        MainViewModel main = _h.CreateMainViewModel(_h.TargetHome);
        await ImportWorkspaceHarness.ConnectAsync(main);
        return main;
    }

    [Fact]
    public async Task 대상이_없으면_줄을_숨긴다()
    {
        MainViewModel main = await ConnectAsync();

        Assert.False(main.HasSidebarRepairNotice);
        Assert.Null(main.SidebarRepairText);
        Assert.False(main.RepairSidebarCommand.CanExecute(null));
    }

    [Fact]
    public async Task 이_앱_프로젝트가_사이드바에_없으면_줄이_보이고_실행하면_추가하고_다시_감지하면_0개다()
    {
        _h.RegisterAppProjectInTarget("019a0000-0000-7000-8000-00000000a001", "삼각형 3개", _h.NewFolder("tri"));
        MainViewModel main = await ConnectAsync();

        Assert.True(main.HasSidebarRepairNotice);
        Assert.Equal("이 앱으로 만든 프로젝트 1개가 Codex 사이드바에 보이지 않습니다.", main.SidebarRepairText);
        Assert.True(main.RepairSidebarCommand.CanExecute(null));

        await main.RepairSidebarAsync();

        Assert.Contains("1개를 추가했습니다", main.SidebarRepairText, StringComparison.Ordinal);
        Assert.Equal(0, main.SidebarRepairCandidateCount);
        Assert.False(main.RepairSidebarCommand.CanExecute(null));
        var state = (GlobalStateJsonObject)GlobalStateJson.Parse(File.ReadAllText(GlobalStatePath, Encoding.UTF8));
        Assert.Single(((GlobalStateJsonObject)state.Get("local-projects")!).Members);

        await ImportWorkspaceHarness.ConnectAsync(main); // 같은 대상 복사본으로 다시 연결(자동 탐지로 실제 Home을 보지 않게)
        Assert.Equal(_h.TargetHome, main.HomePath);
        Assert.False(main.HasSidebarRepairNotice); // 멱등: 다시 감지하면 0개
    }

    [Fact]
    public async Task Codex가_실행_중이면_시작하지_않고_안내한다()
    {
        _h.RegisterAppProjectInTarget("019a0000-0000-7000-8000-00000000a001", "삼각형 3개", _h.NewFolder("tri"));
        MainViewModel main = await ConnectAsync();
        byte[] before = File.ReadAllBytes(GlobalStatePath);
        main.ProcessLister = () => [new RunningProcessInfo("Codex", null)];

        await main.RepairSidebarAsync();

        Assert.Contains("Codex를 종료한 상태에서만", main.SidebarRepairText, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(GlobalStatePath));
        Assert.Equal(1, main.SidebarRepairCandidateCount);
    }

    [Fact]
    public async Task 형식이_다른_상태_파일이면_안내만_하고_버튼은_꺼진다()
    {
        _h.RegisterAppProjectInTarget("019a0000-0000-7000-8000-00000000a001", "삼각형 3개", _h.NewFolder("tri"));
        File.WriteAllText(GlobalStatePath, File.ReadAllText(GlobalStatePath).Replace(",", ", ", StringComparison.Ordinal));
        MainViewModel main = await ConnectAsync();

        Assert.True(main.HasSidebarRepairNotice);
        Assert.Contains("지금은 표시할 수 없습니다", main.SidebarRepairText, StringComparison.Ordinal);
        Assert.False(main.RepairSidebarCommand.CanExecute(null));
    }
}
