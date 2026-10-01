using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_2-5 — 9_2-33(프로젝트 이름 검색), 9_2-34(종료 대기 화면의 우측 상단 [다시 분석]).
/// </summary>
public sealed class ImportWorkspaceSearchAndReanalyzeTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportConversationNodeViewModel Node(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == threadId);

    private static ImportProjectNodeViewModel ProjectOf(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == threadId));

    private static ImportProjectNodeViewModel UncategorizedGroup(ImportWorkspaceViewModel ws)
        => ws.Projects.Single(p => p.ProjectKey == ImportUserChoices.UncategorizedProjectKey);

    // ── 9_2-33 프로젝트 이름 검색 ──────────────────────────────────────────────

    [Fact]
    public async Task 프로젝트_이름이_맞으면_그_프로젝트와_대화가_모두_보이고_대소문자와_앞뒤_공백을_무시한다()
    {
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync());
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        ImportProjectNodeViewModel other = UncategorizedGroup(ws);
        Assert.DoesNotContain(alpha.Conversations, c => c.Title.Contains(alpha.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        ImportUserChoices before = ws.Choices!;

        ws.SearchText = "  " + alpha.DisplayName.ToUpperInvariant() + "  ";

        Assert.True(alpha.IsVisible);
        Assert.All(alpha.Conversations, c => Assert.True(c.IsVisible));
        Assert.False(other.IsVisible);
        Assert.All(other.Conversations, c => Assert.False(c.IsVisible));
        Assert.Same(before, ws.Choices); // 검색은 선택을 바꾸지 않는다

        // 부분 일치(이름 일부, 소문자)
        ws.SearchText = alpha.DisplayName[1..].ToLowerInvariant();
        Assert.True(alpha.IsVisible);
        Assert.All(alpha.Conversations, c => Assert.True(c.IsVisible));
        Assert.Same(before, ws.Choices);
    }

    [Fact]
    public async Task 대화_제목만_맞으면_그_대화와_부모_프로젝트만_보인다()
    {
        _h.SetSourceThreadName(Thread2, "검색용 고유 제목"); // fixture 대화 제목은 모두 같으므로 하나만 바꾼다
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync());
        ImportProjectNodeViewModel other = UncategorizedGroup(ws);
        ImportConversationNodeViewModel target = Node(ws, Thread2);
        Assert.Equal("검색용 고유 제목", target.Title);
        ImportUserChoices before = ws.Choices!;

        ws.SearchText = "  고유  ";

        Assert.True(other.IsVisible);
        Assert.True(target.IsVisible);
        Assert.False(Node(ws, Thread3).IsVisible);          // 같은 그룹의 다른 대화는 숨김
        Assert.False(ProjectOf(ws, Thread1).IsVisible);
        Assert.Same(before, ws.Choices);
    }

    [Fact]
    public async Task 이름도_제목도_아니면_모두_숨기고_지우면_모두_보인다()
    {
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync());

        ws.SearchText = "어디에도-없는-검색어";
        Assert.All(ws.Projects, p => Assert.False(p.IsVisible));
        Assert.All(ws.Projects.SelectMany(p => p.Conversations), c => Assert.False(c.IsVisible));

        ws.SearchText = "   ";
        Assert.All(ws.Projects, p => Assert.True(p.IsVisible));
        Assert.All(ws.Projects.SelectMany(p => p.Conversations), c => Assert.True(c.IsVisible));
    }

    [Theory]
    // (검색어, 그룹 이름, 원본 그룹?, 제목, 자동 포함?, 보임)
    [InlineData("", "Alpha", false, "제목", false, true)]
    [InlineData("alp", "Alpha", false, "제목", false, true)]       // 프로젝트 이름 → 대화 전부
    [InlineData("제목", "Alpha", false, "제목", false, true)]       // 대화 제목
    [InlineData("zzz", "Alpha", false, "제목", false, false)]
    [InlineData("", "필요한 원본 대화(자동 포함)", true, "부모", true, true)]
    [InlineData("", "필요한 원본 대화(자동 포함)", true, "부모", false, false)]  // 실제로 자동 포함된 것만
    [InlineData("원본", "필요한 원본 대화(자동 포함)", true, "부모", true, false)] // 그룹 이름은 숨은 대화를 드러내지 않는다
    [InlineData("원본", "필요한 원본 대화(자동 포함)", true, "부모", false, false)]
    [InlineData("부모", "필요한 원본 대화(자동 포함)", true, "부모", true, true)]
    [InlineData("부모", "필요한 원본 대화(자동 포함)", true, "부모", false, false)]
    public void 검색_표시_규칙(string query, string groupName, bool isDependencyGroup, string title, bool isAutoIncluded, bool expected)
        => Assert.Equal(expected, ImportWorkspaceViewModel.IsShownBySearch(query, groupName, isDependencyGroup, title, isAutoIncluded));

    // ── 9_2-34 종료 대기 화면의 [다시 분석] ────────────────────────────────────────

    [Fact]
    public async Task 첫_열기_대기_화면에서_다시_분석은_켜져_있고_실행_중이면_대기를_유지하고_종료_후에는_기본_선택으로_분석한다()
    {
        string backup = await _h.ExportAsync();
        _h.CodexRunning = true;
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        await ws.OpenAsync();
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);

        Assert.True(ws.ReanalyzeCommand.CanExecute(null));
        ws.ReanalyzeCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
        Assert.Equal(0, ws.AnalysisStartCount);
        Assert.Equal(ImportTexts.CodexRunningAtStart, ws.Message);

        _h.CodexRunning = false;
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "분석");
        Assert.Equal(1, ws.AnalysisStartCount);
        ImportUserChoices defaults = ImportUserChoices.CreateDefault(ws.CurrentPreview!);
        Assert.Equal(defaults.IncludedThreadIds.OrderBy(x => x), ws.Choices!.IncludedThreadIds.OrderBy(x => x));
        Assert.Equal(defaults.ProjectDecisions.OrderBy(p => p.Key), ws.Choices.ProjectDecisions.OrderBy(p => p.Key));
        Assert.All(ws.Choices.ProjectDecisions.Values, d => Assert.True(d.UseSuggestion)); // 기본 선택(제안 그대로)
    }

    public enum WaitButton
    {
        TopReanalyze,
        CenterRecheck,
    }

    [Theory]
    [InlineData(WaitButton.TopReanalyze)]
    [InlineData(WaitButton.CenterRecheck)]
    public async Task 편집에서_온_대기_화면에서_다시_분석하면_대화_체크_폴더_새_프로젝트_이름_만들지_않기가_유지된다(WaitButton button)
    {
        _h.RemoveFromTarget(Thread1, Thread2, Thread3);
        string alphaFolder = _h.NewFolder("alpha-folder");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: () => alphaFolder);

        // 9_5-11 — 백업 "기타 대화" 그룹에는 폴더를 지정하지 않으므로 폴더·이름·만들지 않기는 모두 Alpha에서 고른다.
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.ChooseFolderCommand.Execute(null);
        alpha.NewProjectName = "유지할 이름";
        alpha.DeclineCreation = true;
        Node(ws, Thread3).IsChecked = false;
        ImportUserChoices before = ws.Choices!;

        // Codex를 켠 채 우측 상단 [다시 분석] → 대기 화면
        _h.CodexRunning = true;
        ws.ReanalyzeCommand.Execute(null);
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
        Assert.True(ws.ReanalyzeCommand.CanExecute(null));
        Assert.True(ws.RecheckCodexCommand.CanExecute(null));

        RelayCommand press = button == WaitButton.TopReanalyze ? ws.ReanalyzeCommand : ws.RecheckCodexCommand;
        press.Execute(null);
        await Task.Delay(50);
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State); // 아직 실행 중
        Assert.Equal(1, ws.AnalysisStartCount);
        Assert.Equal(ImportTexts.CodexRunningAtStart, ws.Message);

        _h.CodexRunning = false;
        press.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "다시 분석");
        Assert.Equal(2, ws.AnalysisStartCount);

        Assert.Same(before, ws.Choices);
        Assert.False(Node(ws, Thread3).IsChecked);
        Assert.True(Node(ws, Thread2).IsChecked);
        alpha = ProjectOf(ws, Thread1);
        Assert.True(alpha.IsFolderUserSelected);
        Assert.Equal(alphaFolder, alpha.Target!.FolderPath);
        Assert.Equal("유지할 이름", alpha.NewProjectName);   // 이름 결정 유지
        Assert.True(alpha.DeclineCreation);                  // 만들지 않기 유지
        Assert.False(alpha.IsCreatingProject);
        Assert.True(ws.CanImport);
        Assert.Equal(0, ws.PlanBuildCount);
    }
}
