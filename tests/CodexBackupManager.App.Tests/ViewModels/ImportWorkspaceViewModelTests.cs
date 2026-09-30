using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Restore;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_2-2 — 가져오기 작업 공간(<see cref="ImportWorkspaceViewModel"/>). 커밋된 fixture Codex Home 복사본 두 개로
/// 실제 Export → 분석 → 선택 → 적용 → 결과를 돌린다(9_2-T3~T6). 실제 사용자 <c>.codex</c>는 쓰지 않는다.
/// </summary>
public sealed class ImportWorkspaceViewModelTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportConversationNodeViewModel Node(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == threadId);

    private static ImportProjectNodeViewModel ProjectOf(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == threadId));

    // ── 상태 전이 / Codex 실행 정책 ────────────────────────────────────────────

    [Fact]
    public async Task 파일_선택을_취소하면_Opening에서_Closed로_돌아간다()
    {
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => null);
        var closed = new List<ImportWorkspaceClosedEventArgs>();
        ws.Closed += (_, e) => closed.Add(e);

        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Closed, ws.State);
        Assert.Equal(0, ws.AnalysisStartCount);
        Assert.False(Assert.Single(closed).CodexDataChanged);
    }

    [Fact]
    public async Task Codex가_실행_중이면_분석을_시작하지_않고_종료를_기다린다()
    {
        string backup = await _h.ExportAsync();
        _h.CodexRunning = true;
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);

        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
        Assert.Equal(0, ws.AnalysisStartCount); // Analyzing 진입 0회
        Assert.Contains("Codex를 종료한 상태에서만", ws.Message);
        Assert.True(ws.RecheckCodexCommand.CanExecute(null));

        // 여전히 실행 중이면 [다시 확인]해도 분석하지 않는다.
        ws.RecheckCodexCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
        Assert.Equal(0, ws.AnalysisStartCount);

        _h.CodexRunning = false;
        ws.RecheckCodexCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "분석");
        Assert.Equal(1, ws.AnalysisStartCount);
    }

    [Fact]
    public async Task 분석을_취소하면_닫힌다()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        var started = new ManualResetEventSlim();
        ws.CatalogBuilder = (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            throw new TimeoutException();
        };

        Task open = ws.OpenAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(ImportWorkspaceState.Analyzing, ws.State);
        ws.CancelAnalysisCommand.Execute(null);
        await open;

        Assert.Equal(ImportWorkspaceState.Closed, ws.State);
    }

    [Fact]
    public async Task 손상된_백업은_Failed와_해결_방법을_보여준다()
    {
        string broken = Path.Combine(_h.TestDir, "broken.codexbackup");
        File.WriteAllText(broken, "not a zip");
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => broken);

        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Failed, ws.State);
        Assert.Contains("손상", ws.Message);
        Assert.Contains("다시 내보내", ws.MessageHint);
        Assert.True(ws.ChooseOtherFileCommand.CanExecute(null));
        Assert.True(ws.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task 편집_중_Codex가_켜지면_배너와_비활성이고_다시_분석하면_선택과_폴더가_유지된다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await _h.ExportAsync();
        string folder = _h.NewFolder("chosen");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => folder);
        Assert.True(ws.CanImport);

        Node(ws, Thread3).IsChecked = false;
        ProjectOf(ws, Thread1).ChooseFolderCommand.Execute(null);
        Assert.NotNull(_h.PollCallback); // 편집 중에는 5초 감시가 돈다

        _h.CodexRunning = true;
        _h.PollCallback!();
        Assert.True(ws.HasCodexBanner);
        Assert.True(ws.IsCodexRunning);
        Assert.False(ws.CanImport);
        Assert.False(ws.ImportCommand.CanExecute(null));

        // Codex를 꺼도 분석이 낡았으므로 [다시 분석] 전까지는 막는다.
        _h.CodexRunning = false;
        ws.CheckCodexRunning(); // 창 활성화 경로
        Assert.False(ws.IsCodexRunning);
        Assert.True(ws.IsAnalysisStale);
        Assert.False(ws.CanImport);

        ImportUserChoices before = ws.Choices!;
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing && !ws.IsAnalysisStale, "다시 분석");

        Assert.Equal(2, ws.AnalysisStartCount);
        Assert.False(ws.HasCodexBanner);
        Assert.Same(before, ws.Choices); // 선택·폴더 결정 그대로
        Assert.False(Node(ws, Thread3).IsChecked);
        Assert.True(ProjectOf(ws, Thread1).IsFolderUserSelected);
        Assert.True(ws.CanImport);
    }

    // ── 트리 / 선택 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 프로젝트_3상태_체크와_검색_중_선택_유지()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ImportProjectNodeViewModel group = ProjectOf(ws, Thread2);
        Assert.Contains(group.Conversations, c => c.ThreadId == Thread3); // 둘 다 기타 대화 그룹

        Assert.True(group.IsChecked);
        Node(ws, Thread3).IsChecked = false;
        Assert.Null(group.IsChecked);             // 일부
        Assert.Equal("2개 중 1", group.CountText);

        group.IsChecked = null;                   // WPF가 클릭 때 넘기는 값 → 전체 선택으로 정규화
        Assert.True(group.IsChecked);
        group.IsChecked = false;                  // 전체 해제
        Assert.False(group.IsChecked);
        Assert.False(Node(ws, Thread2).IsChecked);

        group.IsChecked = true;
        ws.SearchText = "존재하지 않는 제목";
        Assert.All(ws.Projects.SelectMany(p => p.Conversations), c => Assert.False(c.IsVisible));
        Assert.Contains(Thread2, ws.Choices!.IncludedThreadIds); // 표시만 바뀌고 선택은 그대로
        ws.SearchText = string.Empty;
        Assert.True(Node(ws, Thread2).IsVisible);
        Assert.True(Node(ws, Thread2).IsChecked);
    }

    [Fact]
    public async Task 이미_있는_대화는_체크할_수_없고_사유_툴팁이_있다()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        ImportConversationNodeViewModel node = Node(ws, Thread2);
        Assert.False(node.IsCheckable);
        Assert.Equal("이미 있음", node.BadgeText);
        Assert.Contains("이미 이 PC에 있어", node.DisabledReason);
        node.IsChecked = true;
        Assert.False(node.IsChecked);
    }

    [Fact]
    public async Task 빠른_선택_버튼과_0개일_때_비활성_사유()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        ws.ClearAllCommand.Execute(null);
        Assert.False(ws.CanImport);
        Assert.False(ws.ImportCommand.CanExecute(null));
        Assert.Equal("가져올 대화를 선택해 주세요.", ws.SummaryHint);
        Assert.True(ws.IsSummaryHintWarning);
        Assert.Equal("대화 0개 가져오기", ws.ImportButtonText);

        ws.SelectNewOnlyCommand.Execute(null);
        Assert.Equal(2, ws.Summary!.ImportCount);
        Assert.Equal("대화 2개 가져오기", ws.ImportButtonText);
        ws.ClearAllCommand.Execute(null);
        ws.SelectAllCommand.Execute(null);
        Assert.Equal(2, ws.Summary!.ImportCount);
        Assert.True(ws.CanImport);
        Assert.Contains("새 대화 2", ws.SummaryText);
        Assert.Contains("기타 대화로 2", ws.SummaryText);
        Assert.Contains("\"기타 대화\"로 들어가는 것이 2개", ws.SummaryHint);
        Assert.Equal(0, ws.PlanBuildCount);
    }

    // ── 작업 폴더 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 폴더를_바꾸면_Plan_없이_즉시_다시_판정하고_요약이_바뀐다()
    {
        _h.RemoveFromTarget(Thread1);
        string registered = _h.NewFolder("registered");
        _h.RegisterTargetProject("db-registered", "등록 프로젝트", registered);
        string unregistered = _h.NewFolder("unregistered");
        string backup = await _h.ExportAsync();
        string pick = unregistered;
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => pick);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);

        Assert.Equal(ProjectTargetReason.OriginalRootMissing, alpha.Target!.Reason);
        Assert.Contains("원본 폴더가 이 PC에 없습니다", alpha.TargetStatusText);
        Assert.Equal("폴더 선택…", alpha.ChooseFolderText);
        Assert.Equal(1, ws.Summary!.UncategorizedImportCount);

        alpha.ChooseFolderCommand.Execute(null);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, alpha.Target!.Reason);
        Assert.Contains("Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다", alpha.TargetStatusText);
        Assert.True(alpha.ShowRefreshHint);
        Assert.True(alpha.IsFolderUserSelected);
        Assert.Equal(1, ws.Summary!.UncategorizedImportCount);

        pick = registered;
        alpha.ChooseFolderCommand.Execute(null);
        Assert.Equal(ProjectTargetKind.LinkExisting, alpha.Target!.Kind);
        Assert.Contains("'등록 프로젝트' 프로젝트에 연결됩니다(직접 지정)", alpha.TargetStatusText);
        Assert.True(alpha.IsLinked);
        Assert.Equal(registered, alpha.LocalPathText);
        Assert.Equal(0, ws.Summary!.UncategorizedImportCount); // 요약 즉시 갱신

        alpha.ResetFolderCommand.Execute(null);
        Assert.Equal(ProjectTargetReason.OriginalRootMissing, alpha.Target!.Reason);
        Assert.False(alpha.IsFolderUserSelected);

        Assert.Equal(0, ws.PlanBuildCount); // 폴더 변경은 Plan을 만들지 않는다
    }

    [Fact]
    public async Task 등록됐지만_폴더가_없는_프로젝트는_보충_문구를_붙인다()
    {
        _h.RemoveFromTarget(Thread1);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        // fixture의 Alpha 프로젝트(C:\Fixture\Projects\Alpha)는 대상 PC에 등록돼 있지만 폴더가 없다.
        Assert.Contains("같은 경로의 프로젝트가 이 PC Codex에 있지만 폴더가 없습니다", ProjectOf(ws, Thread1).TargetStatusText);
    }

    // ── 스크린샷 시나리오(9_2-T4) ──────────────────────────────────────────────

    [Fact]
    public async Task T4_이미_기타_대화에_있는_대화_1개짜리_백업은_버튼이_꺼지고_위치를_보여준다()
    {
        string backup = await _h.ExportAsync(Thread2);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        Assert.False(ws.CanImport);
        Assert.Equal("선택한 대화는 모두 이미 이 PC에 있습니다. 가져올 것이 없습니다.", ws.SummaryHint);
        ImportConversationNodeViewModel node = Node(ws, Thread2);
        Assert.Equal("이 PC 위치: 기타 대화", node.PresenceText); // 9_2-30: 상태 설명은 StatusSentence만 한다
        Assert.Contains("이미 이 PC에 있습니다", node.StatusSentence);

        // 강제로 적용 경로를 태운다(쓸 것이 없는 선택) → NothingToDo 결과 화면과 현재 위치.
        await ws.ImportAsync(bypassCanImport: true);

        Assert.Equal(ImportWorkspaceState.Result, ws.State);
        Assert.Equal(RestoreOutcome.NothingToDo, ws.Result!.Outcome);
        Assert.Equal("적용할 변경이 없었습니다. 선택한 대화는 모두 이미 이 PC에 있습니다.", ws.Result!.Title);
        ImportResultItem item = Assert.Single(Assert.Single(ws.Result!.Groups).Items);
        Assert.Equal("이 PC 위치: 기타 대화", item.Text);
        Assert.True(ws.ShowInListCommand.CanExecute(null));
    }

    // ── 확인 / 적용 / 결과 ────────────────────────────────────────────────────

    [Fact]
    public async Task 확인에서_취소하면_아무것도_만들지_않고_편집으로_돌아간다()
    {
        _h.RemoveFromTarget(Thread2);
        string backup = await _h.ExportAsync();
        string? confirmMessage = null;
        ImportWorkspaceState? stateDuringConfirm = null;
        ImportWorkspaceViewModel? wsRef = null;
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, confirm: (m, _) =>
        {
            confirmMessage = m;
            stateDuringConfirm = wsRef!.State;
            return false;
        });
        wsRef = ws;
        Dictionary<string, string> before = HashTree(_h.TargetHome);

        await ws.ImportAsync();

        Assert.Equal(ImportWorkspaceState.Confirming, stateDuringConfirm);
        Assert.Equal(ImportWorkspaceState.Editing, ws.State);
        Assert.Equal(0, ws.PlanBuildCount);
        Assert.Contains("새로 가져올 대화   1개", confirmMessage);
        Assert.Contains("기타 대화로 들어갈 대화 1개", confirmMessage);
        Assert.Contains("복구 지점(Snapshot)", confirmMessage);
        Assert.Equal(before, HashTree(_h.TargetHome));
    }

    [Fact]
    public async Task 적용_시점에_Codex가_실행_중이면_NotReady_결과와_다시_시도를_보여준다()
    {
        _h.RemoveFromTarget(Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        _h.RestoreSeesCodex = true; // 화면은 못 봤지만 Restore의 이중 확인이 잡는 경우

        await ws.ImportAsync();

        Assert.Equal(ImportWorkspaceState.Result, ws.State);
        Assert.Equal(RestoreOutcome.NotReady, ws.Result!.Outcome);
        Assert.Contains("실행 중", ws.Result!.Title);
        Assert.True(ws.CanRetry);
        Assert.False(ThreadExists(_h.TargetHome, Thread2));
        Assert.Equal(1, ws.PlanBuildCount);

        _h.RestoreSeesCodex = false;
        ws.RetryCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "다시 시도");
        Assert.True(ws.CanImport);
    }

    [Fact]
    public async Task 분석_이후_백업이_바뀌면_Plan을_만들_수_없다는_사유를_보여준다()
    {
        _h.RemoveFromTarget(Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        File.AppendAllText(backup, "x");

        await ws.ImportAsync();

        Assert.Equal(ImportWorkspaceState.Editing, ws.State);
        Assert.Equal(ImportTexts.PlanUnavailable, ws.EditingError);
        Assert.Null(ws.LastAppliedPlan);
    }

    [Fact]
    public async Task 완료되지_못한_이전_적용이_있으면_가져오기를_막는다()
    {
        _h.RemoveFromTarget(Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup, hasIncompleteApply: () => true);
        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Editing, ws.State);
        Assert.False(ws.CanImport);
        Assert.Contains("이전 상태로 복구", ws.SummaryHint);
    }

    [Fact]
    public async Task 폴더를_바꾼_뒤에도_Plan의_backup_identity는_분석한_그_파일이다()
    {
        _h.RemoveFromTarget(Thread1);
        string registered = _h.NewFolder("registered");
        _h.RegisterTargetProject("db-id-check", "등록", registered);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => registered);
        ImportBackupIdentity analyzed = ws.CurrentPreview!.SourceBackupIdentity!;

        ProjectOf(ws, Thread1).ChooseFolderCommand.Execute(null);
        await ws.ImportAsync();

        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        Assert.Equal(analyzed, ws.LastAppliedPlan!.Backup);
        Assert.Same(ws.LastAppliedPlan.UserChoices, ws.Choices);
        Assert.Equal("db-id-check", ReadThreadColumn(_h.TargetHome, Thread1, "project_id"));
        Assert.Equal(registered, ReadThreadColumn(_h.TargetHome, Thread1, "cwd"));
        ImportResultGroup group = Assert.Single(ws.Result!.Groups);
        Assert.Contains("'등록' 프로젝트", group.Destination);
    }

    // ── 9_2-T5 분석·선택·폴더·요약 동안 쓰기 0건 ─────────────────────────────────

    [Fact]
    public async Task T5_분석과_선택과_폴더_변경과_요약_동안_Codex_Home에_쓰지_않는다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await _h.ExportAsync();
        string folder = _h.NewFolder("pick");
        Dictionary<string, string> before = HashTree(_h.TargetHome);

        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => folder);
        Node(ws, Thread3).IsChecked = false;
        ws.SelectAllCommand.Execute(null);
        ws.ClearAllCommand.Execute(null);
        ws.SelectNewOnlyCommand.Execute(null);
        ws.SearchText = "fixture";
        ProjectOf(ws, Thread1).ChooseFolderCommand.Execute(null);
        ProjectOf(ws, Thread1).ResetFolderCommand.Execute(null);
        _ = ws.SummaryText;
        _ = ws.SummaryHint;
        ws.SelectNode(Node(ws, Thread2));
        _ = ws.SelectedConversation!.StatusSentence;
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing && ws.AnalysisStartCount == 2, "다시 분석");

        Assert.Equal(before, HashTree(_h.TargetHome));
        Assert.Equal(0, ws.PlanBuildCount);
    }

    // ── 9_2-T6 E2E: Export → 새 화면으로 일부 제외 → Apply → [목록에서 보기] ────────────

    [Fact]
    public async Task T6_일부를_제외하고_가져오면_고른_것만_적용되고_목록에서_보기가_그_대화를_강조한다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await _h.ExportAsync();
        MainViewModel main = _h.CreateMainViewModel(_h.TargetHome, importFilePicker: () => backup);
        await ConnectAsync(main);
        Assert.DoesNotContain(main.ProjectNodes.SelectMany(p => p.Conversations), c => c.ThreadId == Thread2);

        ImportWorkspaceViewModel ws = main.ImportWorkspace;
        Assert.True(main.OpenImportWorkspaceCommand.CanExecute(null));
        main.OpenImportWorkspaceCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "가져오기 화면 열기");
        Assert.True(main.IsImportWorkspaceOpen);
        Assert.False(main.ExportCommand.CanExecute(null)); // 열려 있는 동안 메인 명령은 막힌다

        Node(ws, Thread3).IsChecked = false;
        Assert.Equal(1, ws.Summary!.ImportCount);
        await ws.ImportAsync();

        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        Assert.True(ThreadExists(_h.TargetHome, Thread2));
        Assert.False(ThreadExists(_h.TargetHome, Thread3));
        Assert.Contains("새로 가져옴 1", ws.Result!.CountsText);
        Assert.Contains("복구 지점", ws.Result!.SnapshotText);
        Assert.Equal([Thread2], ws.ImportedThreadIds);
        ImportResultGroup group = Assert.Single(ws.Result!.Groups);
        Assert.Equal("→ 기타 대화", group.Destination);

        main.HighlightDuration = TimeSpan.FromMinutes(1); // 인스턴스 값(9_2-29c) — 전역 상태를 바꾸지 않는다
        ws.ShowInListCommand.Execute(null);
        await WaitUntil(() => !main.IsImportWorkspaceOpen && !main.IsCatalogLoading &&
                              main.ProjectNodes.SelectMany(p => p.Conversations).Any(c => c.ThreadId == Thread2 && c.IsHighlighted), "목록에서 보기");

        ConversationNodeViewModel shown = main.ProjectNodes.SelectMany(p => p.Conversations).Single(c => c.ThreadId == Thread2);
        Assert.True(shown.IsHighlighted);
        Assert.True(shown.IsTreeSelected);
        Assert.True(main.ProjectNodes.Single(p => p.Conversations.Contains(shown)).IsExpanded);
        Assert.False(main.Selection.IsSelected(Thread2)); // 백업 선택은 바꾸지 않는다
    }
}
