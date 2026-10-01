using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore.Tests.TestSupport;
using CodexBackupManager.Restore.Undo;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_4-T1 ~ T7 — 성공한 가져오기를 그 가져오기가 쓴 것만 역으로 처리해 되돌린다(합성 Codex Home).
/// </summary>
public sealed partial class ProjectCreateApplyTests
{
    private sealed record MixedImport(string SnapshotDirectory, string New1, string New2, string Updated, string Linked, string Other, string Before);

    /// <summary>
    /// 가져오기 직전 상태: 기타 대화에 Updated·Linked·Other(무관)가 있다. 백업: "pa"(원본 없음) = Updated(이어받기)·Linked, "pb"(미등록 폴더) = New1·New2.
    /// pa도 같은 폴더로 지정해 새 프로젝트 하나로 합치고, Linked를 그 프로젝트로 옮긴다.
    /// </summary>
    private MixedImport ImportMixed()
    {
        string updated = NewId(), linked = NewId(), other = NewId(), new1 = NewId(), new2 = NewId();
        ImportOnceAsUncategorized(updated);
        ImportOnceAsUncategorized(linked);
        ImportOnceAsUncategorized(other);
        _sourceExtraLines[updated] = EventLine(2, "원본 PC에서 이어 씀");
        string folder = NewFolder("mixed");
        string backupPath = Export(
            new SourceConversation(updated, "pa", "A", MissingFolder("orig")),
            new SourceConversation(linked, "pa", "A", MissingFolder("orig")),
            new SourceConversation(new1, "pb", "B", folder),
            new SourceConversation(new2, "pb", "B", folder));
        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = Choices(preview, new Dictionary<string, ProjectTargetDecision> { [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) }) with
        {
            RelinkThreadIds = new HashSet<string>([linked], StringComparer.OrdinalIgnoreCase),
        };
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(2, summary.ImportCount);
        Assert.Equal(1, summary.UpdateCount);
        Assert.Equal(1, summary.RelinkCount);
        Assert.Single(summary.NewProjects);

        string before = Dump();
        RestoreResult result = Apply(Plan(preview, backupPath, choices));
        AssertSucceeded(result);
        return new MixedImport(Path.Combine(_snapshotRoot, result.SnapshotId!), new1, new2, updated, linked, other, before);
    }

    /// <summary>되돌리기가 다루는 모든 것(대화·프로젝트 행 전부, global-state 바이트, rollout 파일 트리)의 정확한 상태.</summary>
    private string Dump()
    {
        var builder = new StringBuilder();
        foreach (string table in new[] { "threads", "projects", "project_roots", "project_idempotency_keys" })
        {
            foreach (object?[] row in Rows($"SELECT * FROM {table} ORDER BY 1, 2"))
            {
                builder.Append(table).Append(':').AppendJoin('|', row.Select(v => v?.ToString() ?? "NULL")).Append('\n');
            }
        }

        builder.Append("gs:").Append(FileHash(GlobalStatePath)).Append('\n');
        foreach (string file in Directory.EnumerateFiles(_pcBHome, "rollout-*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(_pcBHome, file)).Append('=').Append(FileHash(file)).Append('\n');
        }

        return builder.ToString();
    }

    private UndoResult Undo(string importDir, IRestoreFaultInjectionHook? hook = null)
        => ImportUndoService.Undo(_pcBHome, importDir, _snapshotRoot, () => [], hook);

    private int SnapshotCount() => Directory.EnumerateDirectories(_snapshotRoot).Count();

    private string? ThreadRolloutOf(string threadId) => TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path") as string;

    private void MutateGlobalState(Func<string, string> change)
    {
        string text = File.ReadAllText(GlobalStatePath, Encoding.UTF8);
        File.WriteAllBytes(GlobalStatePath, GlobalStateJson.StrictUtf8.GetBytes(change(text)));
        Assert.True(GlobalStateProjectGate.Check(GlobalStatePath, CanonicalPath.Create(_pcBHome)).IsSupported);
    }

    private string CreatedProjectId(MixedImport mixed) => (string)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, mixed.New1, "project_id")!;

    private string LegacyIdOf(string dbProjectId)
    {
        var root = (GlobalStateJsonObject)GlobalStateJson.Parse(File.ReadAllText(GlobalStatePath));
        var mapping = (GlobalStateJsonObject)((GlobalStateJsonObject)root.Get(GlobalStateReader.LegacyProjectIdMappingKey)!).Members[0].Value;
        return mapping.Members.Single(m => ((GlobalStateJsonString)m.Value).Value == dbProjectId).Key;
    }

    // ── 9_4-T1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void U1_변경_없이_되돌리면_가져오기_직전과_같고_다른_대화와_global_state_키는_바이트가_같다()
    {
        MixedImport mixed = ImportMixed();
        RestoreTransactionJournal journal = RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!;
        Assert.Equal(RestoreTransactionState.Completed, journal.State);
        Assert.NotNull(journal.UndoRecordSha256);
        ImportRecordSummary summary = journal.Summary!;
        Assert.Equal((2, 1, 1, 1), (summary.ImportedCount, summary.UpdatedCount, summary.RelinkedCount, summary.CreatedProjectCount));
        Assert.DoesNotContain(_root, File.ReadAllText(Path.Combine(mixed.SnapshotDirectory, "restore-transaction.json")), StringComparison.OrdinalIgnoreCase); // journal에 경로 원문 없음

        UndoAssessment assessment = ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot);
        Assert.True(assessment.CanUndo, string.Join(",", assessment.Blockers));
        Assert.True(Assert.Single(assessment.Projects).Delete);

        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Equal(4, result.UndoneConversationCount);
        Assert.Equal(1, result.DeletedProjectCount);
        Assert.Empty(result.KeptProjects);
        Assert.Equal(mixed.Before, Dump());
        Assert.Equal(RestoreTransactionState.Undone, RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!.State);
    }

    // ── 9_4-T2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void U2_가져온_대화를_이어_쓰면_기록_전체를_거부하고_쓰지_않는다()
    {
        MixedImport mixed = ImportMixed();
        File.AppendAllText(ThreadRolloutOf(mixed.New1)!, EventLine(9, "Codex에서 이어 씀"));
        string afterChange = Dump();
        int snapshots = SnapshotCount();

        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Contains(result.Assessment!.Blockers, b => b.ThreadId == mixed.New1 && b.Reason == UndoBlockReason.RolloutChanged);
        Assert.Equal(afterChange, Dump());
        Assert.Equal(snapshots, SnapshotCount());
    }

    [Fact]
    public void U2_Desktop처럼_updated_at_ms와_경로_표기만_바뀌면_되돌릴_수_있다()
    {
        MixedImport mixed = ImportMixed();
        foreach (string id in new[] { mixed.New1, mixed.Updated, mixed.Linked })
        {
            TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, id, "updated_at_ms", 1_999_999_999_000L);
            TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, id, "cwd", @"\\?\" + (string)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, id, "cwd")!);
            TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, id, "rollout_path", @"\\?\" + ThreadRolloutOf(id));
        }

        Assert.True(ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).CanUndo);
        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Null(TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, mixed.Linked, "project_id") as string);
        Assert.DoesNotContain(mixed.New1, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    // ── 9_4-T3 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RestoreFaultInjectionPoint.AfterUndoDbCommit)]
    [InlineData(RestoreFaultInjectionPoint.AfterUndoFileDelete)]
    [InlineData(RestoreFaultInjectionPoint.AfterUndoTruncate)]
    [InlineData(RestoreFaultInjectionPoint.AfterUndoGlobalStateReplace)]
    [InlineData(RestoreFaultInjectionPoint.BeforeUndoValidation)]
    public void U3_되돌리기_도중_실패하면_가져오기_직후_상태로_돌아간다(RestoreFaultInjectionPoint point)
    {
        MixedImport mixed = ImportMixed();
        string afterImport = Dump();

        UndoResult result = Undo(mixed.SnapshotDirectory, new ThrowAt(point));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(ImportUndoService.RolledBackMessage, result.Message);
        Assert.Equal(afterImport, Dump());
        Assert.Equal(RestoreTransactionState.Completed, RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!.State);
        Assert.True(ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).CanUndo); // 다시 되돌릴 수 있다
    }

    [Fact]
    public void U3_크래시_시뮬레이션_되돌리기_DB_커밋_직후_강제_종료되면_다음_실행에서_가져오기_직후로_복구한다()
    {
        MixedImport mixed = ImportMixed();
        string afterImport = Dump();

        CrashRecoveryIntegrationTests.RunUndoAndKillAtCrashPoint(_pcBHome, mixed.SnapshotDirectory, RestoreFaultInjectionPoint.AfterUndoDbCommit, _snapshotRoot);
        Assert.NotEqual(afterImport, Dump());

        IncompleteApply stuck = Assert.Single(IncompleteApplyRecoveryService.FindIncompleteForHome(_snapshotRoot, _pcBHome));
        Assert.Equal(RestoreTransactionState.Undoing, stuck.Journal!.State);
        Assert.Equal(RestoreOutcome.NotReady, Undo(mixed.SnapshotDirectory).Outcome); // 미완료가 있으면 시작하지 않는다
        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(stuck.SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(afterImport, Dump());
        Assert.True(Undo(mixed.SnapshotDirectory).Outcome == RestoreOutcome.Succeeded);
        Assert.Equal(mixed.Before, Dump());
    }

    // ── 9_4-T5 ────────────────────────────────────────────────────────────────

    public static TheoryData<string> TraceVariants() => new() { "history", "atom", "session-index", "unknown-schema" };

    [Theory]
    [MemberData(nameof(TraceVariants))]
    public void U5_Codex에서_열어_본_흔적이_있거나_확인할_수_없으면_거부하고_쓰지_않는다(string variant)
    {
        MixedImport mixed = ImportMixed();
        string historyPath = Path.Combine(_pcBHome, "thread_history_1.sqlite");
        switch (variant)
        {
            case "history":
            case "unknown-schema":
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = historyPath, Pooling = false }.ConnectionString))
                {
                    connection.Open();
                    using SqliteCommand cmd = connection.CreateCommand();
                    cmd.CommandText = variant == "history"
                        ? $"CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT); INSERT INTO thread_turns VALUES ('{mixed.New2}', 't1');"
                        : "CREATE TABLE something_else (id TEXT);";
                    cmd.ExecuteNonQuery();
                }

                break;
            case "atom":
                MutateGlobalState(t => "{\"electron-persisted-atom-state\":{\"recent\":\"" + mixed.New2 + "\"}," + t[1..]);
                break;
            case "session-index":
                File.AppendAllText(Path.Combine(_pcBHome, "session_index.jsonl"), "{\"id\":\"" + mixed.New2 + "\",\"thread_name\":\"x\"}\n");
                break;
        }

        string afterChange = Dump();
        int snapshots = SnapshotCount();
        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        UndoBlockReason expected = variant == "unknown-schema" ? UndoBlockReason.TraceUnknown : UndoBlockReason.OpenedInCodex;
        Assert.Contains(result.Assessment!.Blockers, b => b.Reason == expected && (variant == "unknown-schema" || b.ThreadId == mixed.New2));
        Assert.Equal(afterChange, Dump());
        Assert.Equal(snapshots, SnapshotCount());
    }

    // ── 9_4-T6 ────────────────────────────────────────────────────────────────

    public static TheoryData<string> KeepVariants() => new() { "thread-uses", "desktop-assigned", "pinned", "entry-renamed" };

    [Theory]
    [MemberData(nameof(KeepVariants))]
    public void U6_새_프로젝트가_쓰이고_있으면_남겨_두고_대화만_되돌린다(string variant)
    {
        MixedImport mixed = ImportMixed();
        string projectId = CreatedProjectId(mixed);
        string legacyId = LegacyIdOf(projectId);
        string extraThread = NewId();
        switch (variant)
        {
            case "thread-uses":
                TestCodexHomeBuilder.InsertExistingThread(_pcBHome, extraThread, Path.Combine(_pcADir, "x.jsonl"), @"C:\X", projectId: projectId, threadSource: "user");
                break;
            case "desktop-assigned":
                MutateGlobalState(t => t.Replace("\"thread-project-assignments\":{}",
                    "\"thread-project-assignments\":{\"" + extraThread + "\":{\"projectKind\":\"local\",\"projectId\":\"" + legacyId + "\"}}", StringComparison.Ordinal));
                break;
            case "pinned":
                MutateGlobalState(t => t[..^1] + ",\"pinned-project-ids\":[\"" + legacyId + "\"]}");
                break;
            case "entry-renamed":
                MutateGlobalState(t => t.Replace("\"id\":\"" + legacyId + "\",\"name\":\"", "\"id\":\"" + legacyId + "\",\"name\":\"바뀐 ", StringComparison.Ordinal));
                break;
        }

        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        UndoProjectDecision kept = Assert.Single(result.KeptProjects);
        ProjectKeepReason expected = variant switch
        {
            "thread-uses" => ProjectKeepReason.ThreadsStillUseIt,
            "desktop-assigned" => ProjectKeepReason.DesktopAssigned,
            "pinned" => ProjectKeepReason.Pinned,
            _ => ProjectKeepReason.SidebarEntryChanged,
        };
        Assert.Equal(expected, kept.KeepReason);
        Assert.Equal(0, result.DeletedProjectCount);
        Assert.Single(Rows($"SELECT id FROM projects WHERE id = '{projectId}'"));
        Assert.Contains(legacyId, File.ReadAllText(GlobalStatePath), StringComparison.Ordinal); // 사이드바 항목도 남는다
        Assert.NotNull(FreshCatalog().ProjectDirectory.FindById(projectId));                 // 9_4-10: 프로젝트 목록에도 남는다
        Assert.DoesNotContain(mixed.New1, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));    // 대화는 되돌렸다
        Assert.Null(TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, mixed.Linked, "project_id") as string);
    }

    // ── 9_4-T7 ────────────────────────────────────────────────────────────────

    [Fact]
    public void U7_연결_변경만_있는_기록은_Codex가_열어_본_흔적이_있어도_되돌릴_수_있고_두_번은_안_된다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("relink-only");
        ImportPreview preview = Preview(backupPath);
        RestoreResult applied = Apply(Plan(preview, backupPath, RelinkChoices(preview, folder, threadId)));
        AssertSucceeded(applied);
        string importDir = Path.Combine(_snapshotRoot, applied.SnapshotId!);
        File.AppendAllText(Path.Combine(_pcBHome, "session_index.jsonl"), "{\"id\":\"" + threadId + "\",\"thread_name\":\"opened\"}\n");

        UndoResult first = Undo(importDir);
        Assert.True(first.Outcome == RestoreOutcome.Succeeded, $"{first.Outcome}: {first.Message}");
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(OriginalCwd, Column(threadId, "cwd"));

        Assert.Equal(UndoUnavailableReason.AlreadyUndone, ImportUndoService.Assess(_pcBHome, importDir, _snapshotRoot).Unavailable);
        Assert.Equal(RestoreOutcome.NotReady, Undo(importDir).Outcome);
    }

    [Fact]
    public void U7_옛_기록과_사이드바_보정_기록은_되돌릴_수_없다()
    {
        MixedImport mixed = ImportMixed();
        RestoreTransactionJournal journal = RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!;
        RestoreTransactionJournalStore.Write(mixed.SnapshotDirectory, journal with { UndoRecordSha256 = null, Summary = null }); // 9_4 이전 형식
        Assert.Equal(UndoUnavailableReason.OldFormat, ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).Unavailable);

        SnapshotCreateResult repair = SnapshotService.Create(_snapshotRoot, _pcBHome, SidebarRepairService.SnapshotMarker, [("global-state", GlobalStatePath)]);
        RestoreTransactionJournalStore.Write(repair.SnapshotDirectory!, new RestoreTransactionJournal(repair.Manifest!.SnapshotId, _pcBHome, RestoreTransactionState.Completed, DateTimeOffset.UtcNow));
        Assert.Equal(UndoUnavailableReason.SidebarRepair, ImportUndoService.Assess(_pcBHome, repair.SnapshotDirectory!, _snapshotRoot).Unavailable);

        IReadOnlyList<ImportHistoryEntry> history = ImportHistoryService.List(_snapshotRoot, _pcBHome);
        Assert.Contains(history, e => e.SnapshotId == repair.Manifest.SnapshotId && e.Kind == ImportHistoryKind.SidebarRepair && e.Availability == UndoUnavailableReason.SidebarRepair);
        Assert.Contains(history, e => e.SnapshotDirectory == mixed.SnapshotDirectory && e.Availability == UndoUnavailableReason.OldFormat);
        Assert.Empty(ImportHistoryService.List(_snapshotRoot, Path.Combine(_root, "other-home"))); // 다른 Home 기록은 없다
    }

    [Fact]
    public void U7_journal_구조_검증은_새_필드가_깨져_있으면_손상으로_본다()
    {
        MixedImport mixed = ImportMixed();
        string path = Path.Combine(mixed.SnapshotDirectory, "restore-transaction.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"UndoRecordSha256\": \"", "\"UndoRecordSha256\": \"zz", StringComparison.Ordinal));
        Assert.Equal(RestoreTransactionJournalReadStatus.Corrupt, RestoreTransactionJournalStore.TryReadDetailed(mixed.SnapshotDirectory).Status);
        Assert.Equal(UndoUnavailableReason.RecordCorrupt, ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).Unavailable);
    }

    [Fact]
    public void U7_되돌리기_기록_파일이_바뀌면_손상으로_보고_되돌리지_않는다()
    {
        MixedImport mixed = ImportMixed();
        File.AppendAllText(Path.Combine(mixed.SnapshotDirectory, UndoRecordStore.FileName), " ");
        Assert.Equal(UndoUnavailableReason.RecordCorrupt, ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).Unavailable);
    }

    // ── 9_4-10 ────────────────────────────────────────────────────────────────

    private CodexCatalog FreshCatalog()
        => CodexCatalogBuilder.Build(new CodexDetectionService().DetectFromUserSelection(_pcBHome).Installation!);

    private static string? GroupKeyIn(CodexCatalog catalog, string threadId)
        => UndoRecordBuilder.GroupKeyOf(catalog, catalog.AllConversations.Single(e => string.Equals(e.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void U8_되돌린_뒤_fresh_카탈로그에서_새_대화는_없고_이어받기와_옮기기_대화는_가져오기_직전_그룹에_있다()
    {
        MixedImport mixed = ImportMixed();
        RestoreTransactionJournal journal = RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!;
        UndoRecord record = UndoRecordStore.TryRead(mixed.SnapshotDirectory, journal.UndoRecordSha256!)!;
        // 가져오기 직전에는 둘 다 기타 대화였다(null). 새로 가져온 대화는 기록하지 않는다.
        Assert.Equal(
            new[] { mixed.Linked, mixed.Updated }.Order(StringComparer.OrdinalIgnoreCase),
            record.BeforeGroups!.Select(g => g.ThreadId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(record.BeforeGroups!, g => Assert.Null(g.GroupKey));

        string projectId = CreatedProjectId(mixed);
        CodexCatalog afterImport = FreshCatalog();
        Assert.NotNull(GroupKeyIn(afterImport, mixed.Linked)); // 가져온 뒤에는 새 프로젝트 그룹 아래
        Assert.NotNull(afterImport.ProjectDirectory.FindById(projectId));

        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        CodexCatalog afterUndo = FreshCatalog();
        Assert.DoesNotContain(afterUndo.AllConversations, e => e.ThreadId == mixed.New1 || e.ThreadId == mixed.New2);
        Assert.Null(GroupKeyIn(afterUndo, mixed.Linked));
        Assert.Null(GroupKeyIn(afterUndo, mixed.Updated));
        Assert.Null(afterUndo.ProjectDirectory.FindById(projectId));
    }

    [Fact]
    public void U8_사후_카탈로그_확인에_실패하면_되돌리기_Snapshot으로_가져오기_직후_상태로_돌아간다()
    {
        MixedImport mixed = ImportMixed();
        string projectId = CreatedProjectId(mixed);
        RestoreTransactionJournal journal = RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!;
        UndoRecord record = UndoRecordStore.TryRead(mixed.SnapshotDirectory, journal.UndoRecordSha256!)!;
        // 옮긴 대화의 "가져오기 직전 그룹"을 일부러 틀리게 적는다 — 행 값은 제대로 돌아가도 카탈로그 확인에서 걸려야 한다.
        UndoRecord wrong = record with
        {
            BeforeGroups = record.BeforeGroups!.Select(g => g.ThreadId == mixed.Linked ? g with { GroupKey = "not-the-original-group" } : g).ToList(),
        };
        string sha = UndoRecordStore.Write(mixed.SnapshotDirectory, wrong);
        RestoreTransactionJournalStore.Write(mixed.SnapshotDirectory, journal with { UndoRecordSha256 = sha });
        Assert.True(ImportUndoService.Assess(_pcBHome, mixed.SnapshotDirectory, _snapshotRoot).CanUndo);
        string afterImport = Dump();

        UndoResult result = Undo(mixed.SnapshotDirectory);

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(ImportUndoService.RolledBackMessage, result.Message);
        Assert.Equal(afterImport, Dump());
        Assert.Equal(projectId, Column(mixed.Linked, "project_id"));
        Assert.Equal(RestoreTransactionState.Completed, RestoreTransactionJournalStore.TryRead(mixed.SnapshotDirectory)!.State);
        Assert.Equal(RestoreTransactionState.RolledBack, RestoreTransactionJournalStore.TryRead(Path.Combine(_snapshotRoot, result.UndoSnapshotId!))!.State);
        Assert.Empty(IncompleteApplyRecoveryService.FindIncompleteForHome(_snapshotRoot, _pcBHome));
    }

    // ── 9_4-T4 ────────────────────────────────────────────────────────────────

    private string MakeRecord(RestoreTransactionState? state, int ageDays, bool undoable, string? home = null)
    {
        SnapshotCreateResult created = SnapshotService.Create(_snapshotRoot, home ?? _pcBHome, "backup-sha", []);
        string dir = created.SnapshotDirectory!;
        string manifestPath = Path.Combine(dir, "manifest.json");
        string manifest = File.ReadAllText(manifestPath);
        DateTimeOffset when = DateTimeOffset.UtcNow.AddDays(-ageDays);
        File.WriteAllText(manifestPath, System.Text.RegularExpressions.Regex.Replace(
            manifest, "\"CreatedAtUtc\": \"[^\"]+\"", "\"CreatedAtUtc\": \"" + when.ToString("O") + "\""));
        if (state is { } s)
        {
            var journal = new RestoreTransactionJournal(created.Manifest!.SnapshotId, home ?? _pcBHome, s, DateTimeOffset.UtcNow);
            if (undoable)
            {
                string sha = UndoRecordStore.Write(dir, new UndoRecord(UndoRecord.CurrentVersion, created.Manifest.SnapshotId, [], [], [], [], [], [], [], []));
                journal = journal with { UndoRecordSha256 = sha };
            }

            RestoreTransactionJournalStore.Write(dir, journal);
        }

        return created.Manifest!.SnapshotId;
    }

    [Fact]
    public void U4_정리는_진행_중_기록과_다른_Home을_지우지_않고_되돌리기_가능_기록은_확인이_필요하다()
    {
        string prepared = MakeRecord(RestoreTransactionState.Prepared, 60, false);
        string applying = MakeRecord(RestoreTransactionState.Applying, 60, false);
        string undoing = MakeRecord(RestoreTransactionState.Undoing, 60, false);
        string otherHome = MakeRecord(RestoreTransactionState.Completed, 60, false, Path.Combine(_root, "other-home"));
        string oldPlain = MakeRecord(RestoreTransactionState.Completed, 60, false);
        string newPlain = MakeRecord(RestoreTransactionState.Completed, 1, false);
        string undoable = MakeRecord(RestoreTransactionState.Completed, 60, true);

        // 30일 규칙: 오래됐고 되돌리기 불가인 종료 기록만 후보
        Assert.Equal([oldPlain], SnapshotRetentionService.FindCleanupCandidates(_snapshotRoot, _pcBHome, DateTimeOffset.UtcNow).Select(e => e.SnapshotId));

        RetentionResult refused = SnapshotRetentionService.Delete(_snapshotRoot, _pcBHome, [prepared, applying, undoing, otherHome], allowUndoable: true);
        Assert.Equal(0, refused.DeletedCount);
        Assert.Equal(
            [RetentionRefusal.InProgress, RetentionRefusal.InProgress, RetentionRefusal.InProgress, RetentionRefusal.NotInHistory],
            refused.Refused.Select(r => r.Reason));

        RetentionResult needsConfirm = SnapshotRetentionService.Delete(_snapshotRoot, _pcBHome, [undoable, newPlain], allowUndoable: false);
        Assert.True(needsConfirm.NeedsUndoableConfirmation);
        Assert.Equal(0, needsConfirm.DeletedCount); // 확인 전에는 하나도 지우지 않는다
        Assert.True(Directory.Exists(Path.Combine(_snapshotRoot, newPlain)));

        RetentionResult confirmed = SnapshotRetentionService.Delete(_snapshotRoot, _pcBHome, [undoable, newPlain], allowUndoable: true);
        Assert.Equal(2, confirmed.DeletedCount);
        Assert.False(Directory.Exists(Path.Combine(_snapshotRoot, undoable)));
        foreach (string kept in new[] { prepared, applying, undoing, otherHome, oldPlain })
        {
            Assert.True(Directory.Exists(Path.Combine(_snapshotRoot, kept)));
        }
    }

    [Fact]
    public void U4_정리는_루트_밖_경로와_정션을_거부한다()
    {
        string id = MakeRecord(RestoreTransactionState.Completed, 60, false);
        string dir = Path.Combine(_snapshotRoot, id);
        string outside = NewFolder("outside");
        Assert.False(SnapshotRetentionService.IsSafeSnapshotDirectory(_snapshotRoot, outside));
        Assert.False(SnapshotRetentionService.IsSafeSnapshotDirectory(_snapshotRoot, Path.Combine(dir, "..", "..", "x")));
        Assert.True(SnapshotRetentionService.IsSafeSnapshotDirectory(_snapshotRoot, dir));

        File.WriteAllText(Path.Combine(outside, "keep.txt"), "must survive");
        string junction = Path.Combine(dir, "link");
        using (Process? mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true }))
        {
            mklink!.WaitForExit();
        }

        Assert.True(Directory.Exists(junction));
        Assert.False(SnapshotRetentionService.IsSafeSnapshotDirectory(_snapshotRoot, dir));
        RetentionResult result = SnapshotRetentionService.Delete(_snapshotRoot, _pcBHome, [id], allowUndoable: true);
        Assert.Equal(RetentionRefusal.UnsafePath, Assert.Single(result.Refused).Reason);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Directory.Delete(junction); // 정션만 지운다(대상은 그대로)
    }
}
