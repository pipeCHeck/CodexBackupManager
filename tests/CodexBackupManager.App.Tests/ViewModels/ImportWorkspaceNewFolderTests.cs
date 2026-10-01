using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Restore;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_5-T6 — 가져오기 화면의 [새 폴더 만들기]: 폴더 생성 → CreateNew, 원래 이름, 실패 안내, 기준 폴더 거부·저장,
/// 이 화면에서 만든 빈 폴더 정리(지워도 되는 경우와 절대 지우면 안 되는 경우). 새 폴더 위치는 harness의 temp 폴더다.
/// </summary>
public sealed class ImportWorkspaceNewFolderTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportProjectNodeViewModel ProjectOf(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == threadId));

    private static ImportProjectNodeViewModel UncategorizedGroup(ImportWorkspaceViewModel ws)
        => ws.Projects.Single(p => p.ProjectKey == ImportUserChoices.UncategorizedProjectKey);

    private async Task<ImportWorkspaceViewModel> OpenWithNewThread1Async(Func<string?>? picker = null)
    {
        _h.RemoveFromTarget(Thread1);
        return await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: picker);
    }

    // ── 9_5-07 만들기 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 새_폴더_만들기는_기준_폴더_아래_폴더를_만들고_CreateNew로_지정하며_이름_칸은_원래_이름이다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        Assert.True(alpha.ShowCreateFolder);
        Assert.True(alpha.CreateFolderCommand.CanExecute(null));
        Assert.False(Directory.Exists(_h.DefaultNewFolderBase));

        alpha.CreateFolderCommand.Execute(null);

        string expected = Path.Combine(_h.DefaultNewFolderBase, NewProjectFolderService.SanitizeFolderName(alpha.DisplayName));
        Assert.True(Directory.Exists(expected));
        Assert.Empty(Directory.EnumerateFileSystemEntries(expected));
        Assert.Equal(ProjectTargetKind.CreateNew, alpha.Target!.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, alpha.Target.Reason);
        Assert.Equal(alpha.DisplayName.Trim(), alpha.NewProjectName);
        Assert.True(alpha.IsFolderUserSelected);
        Assert.Null(alpha.FolderCreateError);
        Assert.Equal([expected], ws.CreatedFolders); // 기준 폴더는 목록에 없다(지우지 않는다)
        Assert.Equal(0, ws.PlanBuildCount);
    }

    [Fact]
    public async Task 같은_이름_폴더가_있으면_번호를_붙이지만_프로젝트_이름에는_번호가_들어가지_않는다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        string existing = Path.Combine(_h.DefaultNewFolderBase, NewProjectFolderService.SanitizeFolderName(alpha.DisplayName));
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "user file");

        alpha.CreateFolderCommand.Execute(null);

        Assert.Equal(existing + " (2)", alpha.Target!.FolderPath);
        Assert.Equal(alpha.DisplayName.Trim(), alpha.NewProjectName);
        Assert.True(File.Exists(Path.Combine(existing, "keep.txt"))); // 기존 폴더를 재사용하지 않는다
    }

    [Fact]
    public async Task 기타_대화_그룹은_체크된_대화가_1개면_그_제목_여러_개면_가져온_대화_날짜로_만든다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread2, Thread3));
        ws.Now = () => new DateTime(2026, 10, 2, 9, 0, 0);
        ImportProjectNodeViewModel group = UncategorizedGroup(ws);
        Assert.True(group.ShowCreateFolder);

        ws.SetIncluded([Thread3], include: false);
        group.CreateFolderCommand.Execute(null);
        string title = group.Conversations.Single(c => c.ThreadId == Thread2).Title.Trim();
        Assert.Equal(title, group.NewProjectName);
        Assert.Equal(Path.Combine(_h.DefaultNewFolderBase, NewProjectFolderService.SanitizeFolderName(title)), group.Target!.FolderPath);

        ws.SetIncluded([Thread3], include: true);
        group.CreateFolderCommand.Execute(null);
        Assert.Equal("가져온 대화 2026-10-02", group.NewProjectName);
        Assert.Equal(Path.Combine(_h.DefaultNewFolderBase, "가져온 대화 2026-10-02"), group.Target!.FolderPath);
    }

    [Fact]
    public async Task 만들기에_실패하면_목적지를_바꾸지_않고_그_행에_사유를_보여준다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        ProjectTarget before = alpha.Target!;
        _h.CreateFolderOverride = (_, _) => NewProjectFolderResult.Failed(nameof(UnauthorizedAccessException));

        alpha.CreateFolderCommand.Execute(null);

        Assert.Equal(before, alpha.Target);
        Assert.False(alpha.IsFolderUserSelected);
        Assert.Equal("폴더를 만들지 못했습니다(UnauthorizedAccessException). [폴더 선택…]으로 직접 골라 주세요.", alpha.FolderCreateError);
        Assert.Empty(ws.CreatedFolders);

        // 기준 폴더 자리가 파일인 실제 경우도 같다(사유에 경로가 없다).
        _h.CreateFolderOverride = null;
        File.WriteAllText(_h.DefaultNewFolderBase, "not a folder");
        alpha.CreateFolderCommand.Execute(null);
        Assert.Equal(before, alpha.Target);
        Assert.StartsWith("폴더를 만들지 못했습니다(", alpha.FolderCreateError);
        Assert.DoesNotContain(_h.TestDir, alpha.FolderCreateError!, StringComparison.OrdinalIgnoreCase);
    }

    // ── 9_5-08 기준 폴더 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 기준_폴더를_바꾸면_설정에_저장되고_다시_열어도_반영된다()
    {
        string picked = _h.NewFolder("my-base");
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async(() => picked);
        Assert.Equal(_h.DefaultNewFolderBase, ws.NewFolderBase);
        Assert.Equal("새 폴더 위치: " + ImportTexts.DisplayPath(_h.DefaultNewFolderBase), ws.NewFolderBaseText);

        ws.ChangeNewFolderBaseCommand.Execute(null);

        Assert.Equal(picked, _h.SavedNewFolderBase);
        Assert.Equal(picked, ws.NewFolderBase);
        Assert.Null(ws.NewFolderBaseError);
        ProjectOf(ws, Thread1).CreateFolderCommand.Execute(null);
        Assert.StartsWith(picked, ProjectOf(ws, Thread1).Target!.FolderPath);

        ImportWorkspaceViewModel again = await _h.OpenEditingAsync(await _h.ExportAsync());
        Assert.Equal(picked, again.NewFolderBase);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("codex-home")]
    [InlineData("app-data")]
    [InlineData("snapshot-root")]
    public async Task 쓸_수_없는_기준_폴더는_저장하지_않고_안내한다(string kind)
    {
        string candidate = kind switch
        {
            "relative" => @"relative\dir",
            "codex-home" => Path.Combine(_h.TargetHome, "sessions"),
            "app-data" => Path.Combine(_h.AppDataRoot, "x"),
            _ => _h.SnapshotRoot,
        };
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async(() => candidate);

        ws.ChangeNewFolderBaseCommand.Execute(null);

        Assert.Null(_h.SavedNewFolderBase);
        Assert.Equal(_h.DefaultNewFolderBase, ws.NewFolderBase);
        Assert.NotNull(ws.NewFolderBaseError);
    }

    [Fact]
    public async Task 저장된_기준_폴더가_Codex_Home_안이면_만들지_않고_안내한다()
    {
        _h.SavedNewFolderBase = Path.Combine(_h.TargetHome, "inside");
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        ProjectTarget before = alpha.Target!;

        Assert.NotNull(ws.NewFolderBaseError);
        alpha.CreateFolderCommand.Execute(null);

        Assert.Equal(before, alpha.Target);
        Assert.NotNull(alpha.FolderCreateError);
        Assert.False(Directory.Exists(_h.SavedNewFolderBase));
    }

    // ── 9_5-09 정리 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task 원래대로를_누르면_만든_빈_폴더를_지운다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.CreateFolderCommand.Execute(null);
        string created = alpha.Target!.FolderPath!;

        alpha.ResetFolderCommand.Execute(null);

        Assert.False(Directory.Exists(created));
        Assert.True(Directory.Exists(_h.DefaultNewFolderBase)); // 기준 폴더는 남긴다
        Assert.Empty(ws.CreatedFolders);
        Assert.Equal(ProjectTargetReason.OriginalRootMissing, alpha.Target!.Reason);
    }

    [Fact]
    public async Task 다른_폴더로_바꾸면_만든_빈_폴더를_지우고_파일이_생긴_폴더는_남긴다()
    {
        string other = _h.NewFolder("other");
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async(() => other);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);

        alpha.CreateFolderCommand.Execute(null);
        string empty = alpha.Target!.FolderPath!;
        alpha.ChooseFolderCommand.Execute(null);
        Assert.False(Directory.Exists(empty));

        alpha.CreateFolderCommand.Execute(null);
        string used = alpha.Target!.FolderPath!;
        File.WriteAllText(Path.Combine(used, "user.txt"), "사용자가 넣은 파일");
        alpha.ResetFolderCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(used, "user.txt")));
    }

    [Fact]
    public async Task 가져오기_없이_닫으면_만든_빈_폴더를_지운다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.CreateFolderCommand.Execute(null);
        string created = alpha.Target!.FolderPath!;

        ws.CloseCommand.Execute(null);

        Assert.False(Directory.Exists(created));
        Assert.True(Directory.Exists(_h.DefaultNewFolderBase));
    }

    [Fact]
    public async Task 적용에_성공하면_쓰인_폴더는_남기고_쓰이지_않은_만든_폴더만_지운다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        UncategorizedGroup(ws).CreateFolderCommand.Execute(null); // 기타 대화 그룹은 모두 "이미 있음" → 이 폴더는 적용에 쓰이지 않는다
        string unused = UncategorizedGroup(ws).Target!.FolderPath!;
        ProjectOf(ws, Thread1).CreateFolderCommand.Execute(null);
        string used = ProjectOf(ws, Thread1).Target!.FolderPath!;

        await ws.ImportAsync();
        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        ws.CloseCommand.Execute(null);

        Assert.True(Directory.Exists(used)); // 새 프로젝트 루트
        Assert.False(Directory.Exists(unused));
        Assert.IsType<string>(ReadThreadColumn(_h.TargetHome, Thread1, "project_id"));
    }

    [Fact]
    public async Task 가져오기가_실패한_뒤_닫으면_만든_빈_폴더를_지운다()
    {
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async();
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.CreateFolderCommand.Execute(null);
        string created = alpha.Target!.FolderPath!;
        _h.RestoreSeesCodex = true; // 적용 시점에 Codex가 켜져 있음 → 적용하지 않음

        await ws.ImportAsync();
        Assert.NotEqual(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        ws.CloseCommand.Execute(null);

        Assert.False(Directory.Exists(created));
    }

    [Fact]
    public async Task 다른_백업_파일로_다시_시작하면_이전_화면에서_만든_빈_폴더를_지운다()
    {
        _h.RemoveFromTarget(Thread1);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        await ws.OpenAsync();
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "편집");
        ProjectOf(ws, Thread1).CreateFolderCommand.Execute(null);
        string created = ProjectOf(ws, Thread1).Target!.FolderPath!;

        await ws.OpenAsync();
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "다시 편집");

        Assert.False(Directory.Exists(created));
        Assert.Empty(ws.CreatedFolders);
    }

    [Fact]
    public async Task 사용자가_고른_기존_빈_폴더는_원래대로나_닫기에도_절대_지우지_않는다()
    {
        string userFolder = _h.NewFolder("user-empty"); // 앱이 만들지 않은 빈 폴더
        ImportWorkspaceViewModel ws = await OpenWithNewThread1Async(() => userFolder);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);

        alpha.ChooseFolderCommand.Execute(null);
        alpha.ResetFolderCommand.Execute(null);
        Assert.True(Directory.Exists(userFolder));

        alpha.ChooseFolderCommand.Execute(null);
        ws.CloseCommand.Execute(null);
        Assert.True(Directory.Exists(userFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(userFolder));
    }

    // ── 9_5-10 결과 화면: 새 프로젝트 그룹에 섞인 이어받기 대화 ─────────────────────

    [Fact]
    public async Task 새_프로젝트_그룹에_이어받기가_섞이면_이어받은_대화는_기존_위치로_보인다()
    {
        _h.RemoveFromTarget(Thread2);                       // 기타 대화 그룹: 새 대화
        _h.AppendToSourceRollout(Thread3, 2, "이어받을-줄"); // 기타 대화 그룹(보관됨): 이어받기
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread2, Thread3));
        ImportProjectNodeViewModel group = UncategorizedGroup(ws);
        Assert.Equal(RevisionRelation.IncomingAhead, group.Conversations.Single(c => c.ThreadId == Thread3).Result.Preview.Relation);
        group.CreateFolderCommand.Execute(null);
        Assert.True(group.IsCreatingProject);

        // 확인·요약은 새 대화가 들어가는 프로젝트만 센다(이어받기는 그 수에 영향이 없다).
        Assert.Single(ws.Summary!.NewProjects);
        Assert.Equal(1, ws.Summary.ImportCount);
        Assert.Equal(1, ws.Summary.UpdateCount);

        await ws.ImportAsync();
        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);

        ImportResultGroup created = ws.Result.Groups.Single(g => g.Destination!.StartsWith("→ 새로 만든 프로젝트", StringComparison.Ordinal));
        Assert.Equal(["새로 가져옴"], created.Items.Select(i => i.Text));
        ImportResultGroup kept = ws.Result.Groups.Single(g => g.Destination == "→ 기존 위치 유지");
        ImportResultItem update = Assert.Single(kept.Items);
        Assert.StartsWith("이어받음 · 이 PC 위치: 기타 대화", update.Text);
        Assert.EndsWith("(기존 위치 유지)", update.Text);

        Assert.IsType<string>(ReadThreadColumn(_h.TargetHome, Thread2, "project_id"));
        Assert.IsType<DBNull>(ReadThreadColumn(_h.TargetHome, Thread3, "project_id")); // 이어받은 대화는 옮기지 않는다
    }
}
