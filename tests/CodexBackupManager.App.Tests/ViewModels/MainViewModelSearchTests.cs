using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_U-01 — 메인 목록 검색(가져오기 화면과 같은 규칙). 표시만 바꾸고 체크·내보내기 대상은 그대로다.
/// 실제 .codex가 아니라 harness의 대상 PC 사본에 연결한다.
/// </summary>
public sealed class MainViewModelSearchTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<MainViewModel> ConnectedAsync(Func<string, string?>? exportFilePicker = null)
    {
        _h.SetTargetThreadName(Thread1, "Alpha 기능 설계");
        _h.SetTargetThreadName(Thread2, "버그 수정 회의");
        _h.SetTargetThreadName(Thread3, "README 정리");
        MainViewModel main = _h.CreateMainViewModel(_h.TargetHome, exportFilePicker);
        await ConnectAsync(main);
        Assert.True(main.TotalConversationCount >= 2, "fixture 대화가 목록에 있어야 한다");
        return main;
    }

    private static ConversationNodeViewModel Node(MainViewModel main, string threadId)
        => main.ProjectNodes.SelectMany(p => p.Conversations).Single(c => c.ThreadId == threadId);

    private static string[] VisibleThreadIds(MainViewModel main)
        => main.ProjectNodes.SelectMany(p => p.Conversations).Where(c => c.IsVisible).Select(c => c.ThreadId).Order().ToArray();

    [Fact]
    public async Task 대화_제목이_맞으면_그_대화와_부모_프로젝트만_보이고_검색_중_표시가_나온다()
    {
        MainViewModel main = await ConnectedAsync();

        main.SearchText = "버그";

        Assert.Equal([Thread2], VisibleThreadIds(main));
        Assert.True(Node(main, Thread2).Parent!.IsVisible);
        Assert.All(main.ProjectNodes.Where(p => !p.Conversations.Any(c => c.IsVisible)), p => Assert.False(p.IsVisible));
        Assert.Equal($"검색 중: 대화 1개 표시 (전체 {main.TotalConversationCount}개)", main.SearchStatusText);
        Assert.False(main.HasNoSearchResults);
        Assert.True(main.ClearSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task 프로젝트_이름이_맞으면_그_프로젝트의_대화가_모두_보인다()
    {
        MainViewModel main = await ConnectedAsync();
        ProjectNodeViewModel project = Node(main, Thread1).Parent!;

        main.SearchText = "  " + project.DisplayName.ToUpperInvariant() + " ";

        Assert.True(project.IsVisible);
        Assert.All(project.Conversations, c => Assert.True(c.IsVisible));
    }

    [Fact]
    public async Task 앞뒤_공백과_대소문자를_무시한다()
    {
        MainViewModel main = await ConnectedAsync();

        main.SearchText = "  alpha 기능 ";

        Assert.Contains(Thread1, VisibleThreadIds(main));
        Assert.DoesNotContain(Thread2, VisibleThreadIds(main));
    }

    [Fact]
    public async Task 아무것도_맞지_않으면_결과_없음을_알리고_지우면_원래대로_돌아온다()
    {
        MainViewModel main = await ConnectedAsync();

        main.SearchText = "없는검색어xyz";

        Assert.Empty(VisibleThreadIds(main));
        Assert.All(main.ProjectNodes, p => Assert.False(p.IsVisible));
        Assert.True(main.HasNoSearchResults);

        main.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, main.SearchText);
        Assert.All(main.ProjectNodes, p => Assert.True(p.IsVisible));
        Assert.Equal(main.TotalConversationCount, VisibleThreadIds(main).Length);
        Assert.Null(main.SearchStatusText);
        Assert.False(main.HasNoSearchResults);
        Assert.False(main.ClearSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task 검색은_체크를_바꾸지_않고_숨은_대화도_내보내기에_들어간다()
    {
        string backupPath = Path.Combine(_h.TestDir, "search-export.codexbackup");
        MainViewModel main = await ConnectedAsync(_ => backupPath);
        main.SelectAllConversationsCommand.Execute(null);
        int selected = main.SelectedConversationCount;
        string summary = main.SelectionSummaryText;

        main.SearchText = "버그";

        Assert.Equal(selected, main.SelectedConversationCount);
        Assert.Equal(summary, main.SelectionSummaryText);
        Assert.True(Node(main, Thread1).IsSelected); // 숨은 대화도 선택 그대로
        Assert.False(Node(main, Thread1).IsVisible);
        Assert.EndsWith($"목록에 안 보이는 선택 {selected - 1}개도 내보내기에 들어갑니다", main.SearchStatusText, StringComparison.Ordinal);

        main.ExportCommand.Execute(null);
        await WaitUntil(() => !main.IsExporting && main.ExportStatusText is not null && !main.ExportStatusText.StartsWith("내보내는 중", StringComparison.Ordinal), "내보내기");

        Assert.StartsWith("내보내기 완료", main.ExportStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("ms", main.ExportStatusText, StringComparison.Ordinal); // 9_U-06: 처리 시간은 화면에 없다
        using ZipArchive zip = ZipFile.OpenRead(backupPath);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        string manifest = await reader.ReadToEndAsync();
        Assert.Contains(Thread1, manifest, StringComparison.OrdinalIgnoreCase); // 검색으로 숨은 대화
        Assert.Contains(Thread2, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 같은_폴더를_다시_읽어도_검색어를_유지하고_새_목록에_다시_적용한다()
    {
        // [새로고침]은 자동 탐지(실제 %USERPROFILE%\.codex)부터 다시 하므로 테스트에서는 누르지 않는다.
        // 같은 harness 폴더를 다시 연결해 목록을 새로 만드는 경로(카탈로그 재구성 → 검색 재적용)를 확인한다.
        MainViewModel main = await ConnectedAsync();
        main.SearchText = "버그";
        ProjectNodeViewModel before = main.ProjectNodes[0];

        await ConnectAsync(main);

        Assert.NotSame(before, main.ProjectNodes[0]);
        Assert.Equal(_h.TargetHome, main.HomePath);
        Assert.Equal("버그", main.SearchText);
        Assert.Equal([Thread2], VisibleThreadIds(main));
        Assert.NotNull(main.SearchStatusText);
    }

    [Fact]
    public async Task 목록에서_보기는_검색으로_숨은_대화를_보이게_검색을_지운다()
    {
        MainViewModel main = await ConnectedAsync();
        main.SearchText = "버그";
        Assert.False(Node(main, Thread1).IsVisible);

        main.HighlightConversations([Thread1]);

        Assert.Equal(string.Empty, main.SearchText);
        Assert.True(Node(main, Thread1).IsVisible);
        Assert.True(Node(main, Thread1).IsTreeSelected);
    }

    [Fact]
    public async Task 버튼_툴팁은_하는_일과_꺼진_이유를_알려준다()
    {
        MainViewModel main = await ConnectedAsync();

        Assert.Equal("내보낼 대화나 프로젝트를 먼저 체크하세요.", main.ExportButtonToolTip);
        Assert.StartsWith("백업 파일을 열어 가져올 대화를 고릅니다.", main.ImportButtonToolTip, StringComparison.Ordinal);
        Assert.StartsWith("이 Codex 데이터 폴더의 가져오기 기록을 봅니다.", main.HistoryButtonToolTip, StringComparison.Ordinal);

        var raised = new System.Collections.Generic.List<string?>();
        main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Node(main, Thread2).IsSelected = true;

        Assert.Contains(nameof(MainViewModel.ExportButtonToolTip), raised);
        Assert.Equal("체크한 대화 1개를 백업 파일(.codexbackup) 하나로 저장합니다. Codex 데이터는 바꾸지 않습니다.", main.ExportButtonToolTip);
    }

    [Theory]
    [InlineData("26.928.21956", "0.146.0", "Codex 버전 26.928")]
    [InlineData(null, "0.146.0-alpha.9.2", "Codex CLI 0.146.0-alpha.9.2")]
    [InlineData(null, null, "Codex 버전 확인 불가")]
    [InlineData("27", null, "Codex 버전 27")]
    public void 기본_화면의_Codex_버전은_앞_두_자리만_쉬운_말로(string? desktop, string? cli, string expected)
        => Assert.Equal(expected, MainViewModel.DescribeCodexVersion(desktop, cli));

    [Theory]
    [InlineData("", "프로젝트", "제목", true)]
    [InlineData("제목", "프로젝트", "긴 제목입니다", true)]
    [InlineData("프로젝", "프로젝트", "제목", true)]
    [InlineData("ALPHA", "x", "alpha 설계", true)]
    [InlineData("없음", "프로젝트", "제목", false)]
    public void 공용_검색_규칙(string query, string projectName, string title, bool expected)
        => Assert.Equal(expected, TreeSearch.IsShown(TreeSearch.Normalize(query), projectName, title));
}
