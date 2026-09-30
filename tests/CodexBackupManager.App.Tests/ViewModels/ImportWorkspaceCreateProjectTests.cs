using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_5-05 — 가져오기 화면의 새 프로젝트 만들기(CreateNew 문구, 이름 칸, 만들지 않기, 요약·확인·결과 문구, 백업 "기타 대화" 그룹의 폴더 지정).
/// harness는 대상 PC 복사본의 프로젝트 스키마를 실측 형태로 맞춘다(공유 fixture는 그대로).
/// </summary>
public sealed class ImportWorkspaceCreateProjectTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportProjectNodeViewModel ProjectOf(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == threadId));

    private static ImportProjectNodeViewModel UncategorizedGroup(ImportWorkspaceViewModel ws)
        => ws.Projects.Single(p => p.ProjectKey == ImportUserChoices.UncategorizedProjectKey);

    private static string Display(string folder) => CanonicalPath.Create(folder).Display;

    [Fact]
    public async Task 미등록_폴더를_고르면_새_프로젝트_문구와_이름_칸이_보이고_요약과_확인에_들어간다()
    {
        _h.RemoveFromTarget(Thread1);
        string folder = _h.NewFolder("새 작업 폴더");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        Assert.False(alpha.IsCreatingProject);

        alpha.ChooseFolderCommand.Execute(null);

        string name = Path.GetFileName(folder);
        Assert.True(alpha.IsCreatingProject);
        Assert.True(alpha.IsCreationOffered);
        Assert.False(alpha.DeclineCreation);
        Assert.False(alpha.ShowRefreshHint);
        Assert.Equal(name, alpha.NewProjectName);
        Assert.Equal($"✨ 이 폴더로 새 프로젝트 '{name}'을(를) 만들어 연결합니다.", alpha.TargetStatusText);
        Assert.Equal(Display(folder), alpha.LocalPathText);

        Assert.Contains("새 프로젝트 1", ws.SummaryText);
        Assert.Contains($"새로 만들 프로젝트 1개: '{name}'({Display(folder)})", ws.SummaryHint);
        Assert.Contains($"새로 만들 프로젝트 1개: '{name}'({Display(folder)})", ImportTexts.ConfirmMessage(ws.Summary!));
        Assert.True(ws.CanImport);
    }

    [Fact]
    public async Task 이름을_바꾸면_반영되고_빈_이름이면_오류와_함께_가져오기가_꺼진다()
    {
        _h.RemoveFromTarget(Thread1);
        string folder = _h.NewFolder("rename");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.ChooseFolderCommand.Execute(null);

        alpha.NewProjectName = "내 프로젝트";
        Assert.Equal("내 프로젝트", alpha.Target!.NewProjectName);
        Assert.Contains("'내 프로젝트'", ws.SummaryHint);
        Assert.Null(alpha.DecisionError);

        alpha.NewProjectName = "  ";
        Assert.Equal("  ", alpha.NewProjectName); // 입력 중인 값을 그대로 보여준다
        Assert.Equal(ImportSelection.EmptyProjectNameError, alpha.DecisionError);
        Assert.False(ws.CanImport);
        Assert.Contains(ImportSelection.EmptyProjectNameError, ws.SummaryHint);

        alpha.NewProjectName = "다시";
        Assert.True(ws.CanImport);
        Assert.Equal(0, ws.PlanBuildCount); // 이름 편집은 Plan을 만들지 않는다
    }

    [Fact]
    public async Task 만들지_않기를_고르면_기타_대화_문구가_되고_새로고침_안내도_없다()
    {
        _h.RemoveFromTarget(Thread1);
        string folder = _h.NewFolder("decline");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.ChooseFolderCommand.Execute(null);

        alpha.DeclineCreation = true;

        Assert.False(alpha.IsCreatingProject);
        Assert.True(alpha.IsCreationOffered); // 다시 켤 수 있게 선택은 계속 보인다
        Assert.True(alpha.DeclineCreation);
        Assert.Equal("새 프로젝트를 만들지 않고 기타 대화로 가져옵니다.", alpha.TargetStatusText);
        Assert.False(alpha.ShowRefreshHint);
        Assert.Empty(ws.Summary!.NewProjects);
        Assert.Equal(1, ws.Summary.UncategorizedImportCount);
        Assert.DoesNotContain("새 프로젝트", ws.SummaryText);

        alpha.DeclineCreation = false;
        Assert.True(alpha.IsCreatingProject);
        Assert.Single(ws.Summary!.NewProjects);
    }

    [Fact]
    public async Task 적용하면_새_프로젝트가_생기고_결과_화면에_새로_만든_프로젝트로_보인다()
    {
        _h.RemoveFromTarget(Thread1);
        string folder = _h.NewFolder("apply");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread1), folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ProjectOf(ws, Thread1);
        alpha.ChooseFolderCommand.Execute(null);
        alpha.NewProjectName = "결과 프로젝트";

        await ws.ImportAsync();

        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        Assert.Contains("새 프로젝트 1", ws.Result.CountsText);
        ImportResultGroup group = Assert.Single(ws.Result.Groups);
        Assert.Equal($"→ 새로 만든 프로젝트 '결과 프로젝트'({Display(folder)})", group.Destination);

        object? projectId = ReadThreadColumn(_h.TargetHome, Thread1, "project_id");
        Assert.IsType<string>(projectId);
        Assert.Equal(Display(folder), ReadThreadColumn(_h.TargetHome, Thread1, "cwd"));
    }

    [Fact]
    public async Task 백업의_기타_대화_그룹에도_폴더를_지정해_새_프로젝트로_가져온다()
    {
        _h.RemoveFromTarget(Thread2);
        string folder = _h.NewFolder("uncategorized-target");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread2), folderPicker: () => folder);
        ImportProjectNodeViewModel group = UncategorizedGroup(ws);
        Assert.True(group.IsFolderEditable);
        Assert.Equal("폴더 선택…", group.ChooseFolderText);
        Assert.True(group.ChooseFolderCommand.CanExecute(null));

        group.ChooseFolderCommand.Execute(null);
        Assert.True(group.IsCreatingProject);

        await ws.ImportAsync();
        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        Assert.IsType<string>(ReadThreadColumn(_h.TargetHome, Thread2, "project_id"));
    }
}

/// <summary>Phase 9_5-T4 — 대상 PC 스키마가 확인한 형태와 다르면(공유 fixture 그대로) 새 프로젝트를 만들지 않는다는 문구.</summary>
public sealed class ImportWorkspaceCreationUnsupportedTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new(realProjectSchema: false);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task 스키마가_다르면_지원하지_않는다는_문구와_경고가_보이고_기타_대화로_들어간다()
    {
        _h.RemoveFromTarget(Thread1);
        string folder = _h.NewFolder("unsupported");
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(), folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == Thread1));

        alpha.ChooseFolderCommand.Execute(null);

        Assert.Equal(ProjectTargetReason.CreationUnsupported, alpha.Target!.Reason);
        Assert.False(alpha.IsCreatingProject);
        Assert.False(alpha.IsCreationOffered);
        Assert.False(alpha.ShowRefreshHint);
        Assert.Contains("프로젝트 자동 생성을 지원하지 않습니다", alpha.TargetStatusText);
        Assert.Contains(ws.Summary!.Warnings, w => w.Contains("새 프로젝트를 만들지 않습니다", StringComparison.Ordinal));
        Assert.Equal(1, ws.Summary.UncategorizedImportCount);
        Assert.True(ws.CanImport);
    }
}
