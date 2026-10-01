using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Restore;
using CodexBackupManager.Restore.Undo;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>Phase 9_4-02/07/09 — 결과 화면 [이 가져오기 되돌리기], 가져오기 기록 화면, 되돌리기 문구. temp Snapshot 루트만 쓴다.</summary>
public sealed class ImportUndoUiTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<ImportWorkspaceViewModel> ImportThread2Async()
    {
        _h.RemoveFromTarget(Thread2);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread2));
        await ws.ImportAsync();
        Assert.Equal(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        await ws.UndoAssessTask!;
        return ws;
    }

    [Fact]
    public async Task 결과_화면에서_되돌리면_가져온_대화가_사라지고_버튼이_꺼진다()
    {
        ImportWorkspaceViewModel ws = await ImportThread2Async();
        Assert.True(ws.IsUndoImportVisible);
        Assert.True(ws.CanUndoImport);
        Assert.StartsWith("되돌릴 수 있습니다.", ws.UndoText, StringComparison.Ordinal);
        Assert.True(ThreadExists(_h.TargetHome, Thread2));

        await ws.UndoImportAsync();

        Assert.StartsWith("가져오기를 되돌렸습니다. 대화 1개", ws.UndoText, StringComparison.Ordinal);
        Assert.False(ws.CanUndoImport);
        Assert.False(ws.UndoImportCommand.CanExecute(null));
        Assert.False(ThreadExists(_h.TargetHome, Thread2));
    }

    [Fact]
    public async Task 가져온_대화를_이어_썼으면_되돌리지_않고_이유를_보여준다()
    {
        ImportWorkspaceViewModel ws = await ImportThread2Async();
        File.AppendAllText(RolloutFileOf(_h.TargetHome, Thread2), "{\"timestamp\":\"2026-01-03T00:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"x\"}}\n");

        await ws.UndoImportAsync();

        Assert.Contains("되돌릴 수 없습니다", ws.UndoText, StringComparison.Ordinal);
        Assert.Contains("가져온 뒤 이어서 쓴 대화라 되돌릴 수 없습니다.", ws.UndoText, StringComparison.Ordinal);
        Assert.True(ThreadExists(_h.TargetHome, Thread2));
    }

    [Fact]
    public async Task 실패한_가져오기에는_되돌리기_줄이_없다()
    {
        _h.RemoveFromTarget(Thread2);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(await _h.ExportAsync(Thread2));
        _h.RestoreSeesCodex = true; // 적용 시점에 Codex 실행 → NotReady
        await ws.ImportAsync();

        Assert.NotEqual(RestoreOutcome.Succeeded, ws.Result!.Outcome);
        Assert.False(ws.IsUndoImportVisible);
        Assert.Null(ws.UndoText);
    }

    [Fact]
    public async Task 기록_화면은_현재_Home_기록만_종류와_가능_여부를_보여주고_되돌린_뒤에는_되돌릴_수_없다()
    {
        await ImportThread2Async();
        // 같은 Home의 보정 기록, 다른 Home의 기록(목록에 없어야 한다).
        SnapshotCreateResult repair = SnapshotService.Create(_h.SnapshotRoot, _h.TargetHome, SidebarRepairService.SnapshotMarker, []);
        RestoreTransactionJournalStore.Write(repair.SnapshotDirectory!, new RestoreTransactionJournal(repair.Manifest!.SnapshotId, _h.TargetHome, RestoreTransactionState.Completed, DateTimeOffset.UtcNow));
        SnapshotCreateResult other = SnapshotService.Create(_h.SnapshotRoot, Path.Combine(_h.TestDir, "other-home"), "x", []);
        RestoreTransactionJournalStore.Write(other.SnapshotDirectory!, new RestoreTransactionJournal(other.Manifest!.SnapshotId, Path.Combine(_h.TestDir, "other-home"), RestoreTransactionState.Completed, DateTimeOffset.UtcNow));

        MainViewModel main = _h.CreateMainViewModel(_h.TargetHome);
        await ConnectAsync(main);
        ImportHistoryViewModel? history = null;
        main.ImportHistoryPresenter = vm => history = vm;
        Assert.True(main.OpenImportHistoryCommand.CanExecute(null));
        main.OpenImportHistoryCommand.Execute(null);

        Assert.NotNull(history);
        Assert.DoesNotContain(history!.Entries, e => e.Entry.SnapshotId == other.Manifest.SnapshotId);
        ImportHistoryRowViewModel repairRow = history.Entries.Single(e => e.Entry.SnapshotId == repair.Manifest.SnapshotId);
        Assert.Equal("사이드바 보정", repairRow.KindText);
        Assert.Equal("사이드바 보정 기록은 되돌리기를 지원하지 않습니다.", repairRow.AvailabilityText);
        ImportHistoryRowViewModel importRow = history.Entries.Single(e => e.Entry.Kind == ImportHistoryKind.Import);
        Assert.Equal("가져오기", importRow.KindText);
        Assert.Equal("완료", importRow.ResultText);
        Assert.StartsWith("새로 1 · 이어받음 0 · 옮김 0 · 새 프로젝트 0", importRow.CountsText, StringComparison.Ordinal);

        history.SelectedEntry = importRow;
        await history.SelectionTask!;
        Assert.True(history.CanUndo);
        Assert.Single(history.DetailConversations);

        await history.UndoSelectedAsync();

        Assert.StartsWith("가져오기를 되돌렸습니다.", history.StatusText, StringComparison.Ordinal);
        Assert.False(ThreadExists(_h.TargetHome, Thread2));
        ImportHistoryRowViewModel undone = history.Entries.Single(e => e.Entry.SnapshotId == importRow.Entry.SnapshotId);
        Assert.Equal("되돌림", undone.ResultText);
        Assert.Equal("이미 되돌린 기록입니다.", undone.AvailabilityText);
        Assert.Contains(history.Entries, e => e.KindText == "되돌리기");
        history.SelectedEntry = undone;
        await (history.SelectionTask ?? Task.CompletedTask);
        Assert.False(history.CanUndo);
        Assert.False(history.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task 옛_기록은_이전_버전_문구로_보인다()
    {
        ImportWorkspaceViewModel ws = await ImportThread2Async();
        string dir = Path.Combine(_h.SnapshotRoot, ws.Result!.SnapshotId!);
        RestoreTransactionJournal journal = RestoreTransactionJournalStore.TryRead(dir)!;
        RestoreTransactionJournalStore.Write(dir, journal with { UndoRecordSha256 = null, Summary = null });

        var history = new ImportHistoryViewModel(
            _h.TargetHome, _h.SnapshotRoot, new CodexBackupManager.App.Services.FileLogger(Path.Combine(_h.TestDir, "logs")),
            (_, _) => true, _ => null, () => [], () => { });

        ImportHistoryRowViewModel row = Assert.Single(history.Entries);
        Assert.Equal("—", row.BackupText);
        Assert.Equal("이 기록은 이전 버전에서 만들어져 되돌리기를 지원하지 않습니다.", row.AvailabilityText);
    }

    [Fact]
    public void 남겨_둔_프로젝트와_막는_대화_문구()
    {
        var kept = new UndoProjectDecision("p1", "삼각형 3개", false, ProjectKeepReason.Pinned, null);
        var result = new UndoResult(RestoreOutcome.Succeeded, "x", "u", 2, 0, [kept], null);
        Assert.Equal(
            "가져오기를 되돌렸습니다. 대화 2개 · 지운 프로젝트 0개" + Environment.NewLine + "'삼각형 3개': Codex에서 이 프로젝트를 사용 중이라 프로젝트는 남겨 두었습니다.",
            ImportTexts.UndoResultText(result, new Dictionary<string, string>()));

        var blocked = new UndoAssessment(UndoUnavailableReason.None, [new UndoBlocker("t1", UndoBlockReason.OpenedInCodex)], [], null, null);
        Assert.Equal(
            "되돌릴 수 없습니다(부분 되돌리기는 지원하지 않습니다): '제목' — Codex에서 이미 열어 본 대화라 되돌릴 수 없습니다.",
            ImportTexts.UndoAssessmentText(blocked, new Dictionary<string, string> { ["t1"] = "제목" }));
    }
}
