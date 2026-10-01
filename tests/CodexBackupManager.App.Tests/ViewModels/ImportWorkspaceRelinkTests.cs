using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Restore;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_3-01/04 — 가져오기 화면의 "📁 이 프로젝트로 옮기기". 원본 PC에서 Thread2를 Alpha 프로젝트로 옮겨(cwd 폴백) 내보내면,
/// 대상 PC(기타 대화에 같은 Thread2가 있음)에서 그 대화를 Alpha로 옮길 수 있다.
/// </summary>
public sealed class ImportWorkspaceRelinkTests : IDisposable
{
    private const string TargetDbId = "019a0000-0000-7000-8000-0000000000e1";
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportConversationNodeViewModel Node(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == threadId);

    /// <summary>대상 PC에 등록된 실제 폴더 프로젝트 "작업 프로젝트"를 Alpha의 작업 폴더로 고른 편집 화면.</summary>
    private async Task<ImportWorkspaceViewModel> OpenWithThread2InAlphaAsync(Func<string, string, bool>? confirm = null)
    {
        _h.AssignSourceThreadToAlpha(Thread2); // 원본 PC: Thread2를 Alpha 프로젝트로(대상 PC에서는 기타 대화)
        string folder = _h.NewFolder("work");
        _h.RegisterTargetProject(TargetDbId, "작업 프로젝트", folder);
        string backup = await _h.ExportAsync(Thread2, Thread3);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => folder, confirm: confirm);
        ImportProjectNodeViewModel owner = ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == Thread2));
        owner.ChooseFolderCommand.Execute(null);
        return ws;
    }

    [Fact]
    public async Task 옮길_수_있는_대화에_기본_해제_체크가_보이고_옮기기만으로_가져오기가_켜진다()
    {
        string? confirmText = null;
        ImportWorkspaceViewModel ws = await OpenWithThread2InAlphaAsync((message, _) =>
        {
            confirmText = message;
            return true;
        });
        ImportConversationNodeViewModel node = Node(ws, Thread2);
        Assert.Equal(RevisionRelation.Identical, node.Result.Preview.Relation);
        Assert.Equal(ProjectTargetKind.LinkExisting, ws.Projects.First(p => p.Conversations.Contains(node)).Target!.Kind);
        Assert.True(node.IsRelinkVisible);
        Assert.True(node.IsRelinkEnabled);
        Assert.False(node.IsRelinkChecked); // 기본 해제
        Assert.Null(node.RelinkReasonText);
        Assert.Equal("📁 이 프로젝트로 옮기기", node.RelinkLabel);
        Assert.False(ws.CanImport);

        node.IsRelinkChecked = true;

        Assert.True(node.IsRelinkChecked);
        Assert.False(node.IsChecked);                       // 가져오기 체크와는 따로다
        Assert.Equal(1, ws.Summary!.RelinkCount);
        Assert.Contains("연결 변경 1", ws.SummaryText, StringComparison.Ordinal);
        Assert.Equal("대화 1개 옮기기", ws.ImportButtonText);
        Assert.True(ws.CanImport);
        Assert.Equal(0, ws.PlanBuildCount);

        await ws.ImportAsync();

        Assert.Contains("  프로젝트 옮기기 1개:", confirmText, StringComparison.Ordinal);
        Assert.Contains($"'{node.Title}' 기타 대화 → '작업 프로젝트'", confirmText, StringComparison.Ordinal);
        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        ImportResultGroup moved = ws.Result.Groups.Single(g => g.Header == "프로젝트 옮기기");
        ImportResultItem item = Assert.Single(moved.Items);
        Assert.Equal(node.Title, item.Title);
        Assert.Equal("옮김: 기타 대화 → '작업 프로젝트'", item.Text);
        Assert.Contains(Thread2, ws.ImportedThreadIds);
        Assert.Equal(TargetDbId, ReadThreadColumn(_h.TargetHome, Thread2, "project_id"));
    }

    [Fact]
    public async Task 가져오기와_옮기기를_함께_고르면_버튼_문구에_둘_다_나온다()
    {
        _h.RemoveFromTarget(Thread3);
        ImportWorkspaceViewModel ws = await OpenWithThread2InAlphaAsync();
        Node(ws, Thread2).IsRelinkChecked = true;

        Assert.Equal(1, ws.Summary!.ImportCount);
        Assert.Equal("대화 1개 가져오기 · 1개 옮기기", ws.ImportButtonText);
    }

    [Theory]
    [InlineData("assigned")]
    [InlineData("projectless")]
    [InlineData("unavailable")]
    public async Task Desktop이_위치를_따로_기록했거나_상태를_모르면_흐린_체크와_이유를_보여준다(string variant)
    {
        string home = _h.TargetHome;
        string hostKey = HostKeyJson(home);
        string json = variant switch
        {
            "assigned" => "{\"local-projects\":{},\"project-order\":[],\"thread-project-assignments\":{\"" + Thread2 + "\":{\"projectKind\":\"local\",\"projectId\":\"x\"}}," +
                          "\"app-server-project-id-by-legacy-project-id-by-host\":{" + hostKey + ":{}},\"app-server-projects-migration-by-host\":{" + hostKey +
                          ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}}}",
            "projectless" => "{\"local-projects\":{},\"project-order\":[],\"projectless-thread-ids\":[\"" + Thread2 + "\"]," +
                             "\"app-server-project-id-by-legacy-project-id-by-host\":{" + hostKey + ":{}},\"app-server-projects-migration-by-host\":{" + hostKey +
                             ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}}}",
            _ => "{ \"broken\": true }",
        };
        File.WriteAllText(GlobalStatePath(home), json);

        ImportWorkspaceViewModel ws = await OpenWithThread2InAlphaAsync();
        ImportConversationNodeViewModel node = Node(ws, Thread2);

        Assert.True(node.IsRelinkVisible);
        Assert.False(node.IsRelinkEnabled);
        Assert.Equal(variant == "unavailable" ? ImportTexts.RelinkDesktopStateUnavailable : ImportTexts.RelinkDesktopRecorded, node.RelinkReasonText);
        node.IsRelinkChecked = true;
        Assert.False(node.IsRelinkChecked);
        Assert.Equal(0, ws.Summary!.RelinkCount);
    }

    [Fact]
    public async Task 백업_기타_대화_그룹의_대화에는_옮기기_체크가_없다()
    {
        ImportWorkspaceViewModel ws = await OpenWithThread2InAlphaAsync();
        ImportConversationNodeViewModel uncategorized = Node(ws, Thread3); // 9_5-11: 목적지가 항상 기타 대화
        Assert.False(uncategorized.IsRelinkVisible);
        Assert.Null(uncategorized.RelinkReasonText);
    }
}
