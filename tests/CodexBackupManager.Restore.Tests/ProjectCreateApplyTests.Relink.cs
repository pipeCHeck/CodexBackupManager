using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_3-T1/T2 — 이 PC에 이미 있는 대화(기타 대화)를 가져오기 화면에서 고른 작업 폴더의 프로젝트로 옮긴다(연결 변경).
/// 먼저 그 대화를 원본 폴더가 없는 백업 프로젝트로 한 번 가져와(기타 대화) "이미 있음" 상태를 만든 뒤 같은 백업을 다시 연다.
/// </summary>
public sealed partial class ProjectCreateApplyTests
{
    private const string RegisteredProjectId = "019a0000-0000-7000-8000-0000000000b1";

    /// <summary>원본 폴더가 이 PC에 없는 백업 프로젝트 "pa"로 대화를 내보내 한 번 가져온다 → 이 PC의 기타 대화.</summary>
    private string ImportOnceAsUncategorized(string threadId)
    {
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", MissingFolder("orig")));
        ImportPreview first = Preview(backupPath);
        AssertSucceeded(Apply(Plan(first, backupPath, ImportUserChoices.CreateDefault(first))));
        Assert.Null(Column(threadId, "project_id"));
        return backupPath;
    }

    private ImportUserChoices RelinkChoices(ImportPreview preview, string folder, params string[] relink)
        => Choices(preview, new Dictionary<string, ProjectTargetDecision> { [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) }) with
        {
            RelinkThreadIds = relink.ToHashSet(StringComparer.OrdinalIgnoreCase),
        };

    private string RolloutPathOf(string threadId) => (string)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path")!;

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private ImportSelectionConversation SelectionOf(ImportPreview preview, ImportUserChoices choices, string threadId)
        => ImportSelection.Compute(preview, choices).Conversations.Single(c => c.ThreadId == threadId);

    private string RegisterFolder(string label)
    {
        string folder = NewFolder(label);
        TestCodexHomeBuilder.InsertProject(_pcBHome, RegisteredProjectId, "등록 프로젝트", CanonicalPath.Create(folder).Display);
        return folder;
    }

    private static string EventLine(long ordinal, string text)
        => "{\"timestamp\":\"2026-03-05T05:06:07.000Z\",\"ordinal\":" + ordinal + ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\"," +
           "\"item\":{\"type\":\"UserMessage\",\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}\n";

    // ── 9_3-T1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void R1_기타_대화의_같은_대화를_등록_프로젝트로_옮기면_project_id와_cwd만_바뀐다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("registered");
        object? updatedBefore = Column(threadId, "updated_at_ms");
        string rollout = RolloutPathOf(threadId);
        string rolloutHash = FileHash(rollout);

        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = RelinkChoices(preview, folder, threadId);
        ImportSelectionConversation selected = SelectionOf(preview, choices, threadId);
        Assert.Equal(RevisionRelation.Identical, selected.Preview.Relation);
        Assert.Equal(RelinkStatus.Available, selected.Relink);
        Assert.True(selected.IsRelinkSelected);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(1, summary.RelinkCount);
        Assert.Equal(0, summary.ImportCount);
        Assert.True(summary.CanApply);

        ImportPlan plan = Plan(preview, backupPath, choices);
        ImportPlanRelink relink = Assert.Single(plan.Relinks);
        Assert.Null(relink.ExpectedProjectId);
        AssertSucceeded(Apply(plan));

        Assert.Equal(RegisteredProjectId, Column(threadId, "project_id"));
        Assert.Equal(plan.Projects.Single().ResolvedTarget!.FolderPath, Column(threadId, "cwd"));
        Assert.Equal(updatedBefore, Column(threadId, "updated_at_ms")); // 사이드바 정렬이 바뀌지 않게
        Assert.Equal(rolloutHash, FileHash(rollout));                   // rollout은 건드리지 않는다
        CodexCatalog fresh = RestoreExecutor.BuildFreshCatalog(_pcBHome);
        Assert.Contains(fresh.Projects.Single(p => p.ProjectId == RegisteredProjectId).Conversations, c => c.ThreadId == threadId);
    }

    [Fact]
    public void R2_새_대화_없이_옮기기만_해도_CreateNew_목적지면_프로젝트를_만들고_사이드바에도_기록한다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = NewFolder("unregistered");

        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = RelinkChoices(preview, folder, threadId);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(ProjectTargetKind.CreateNew, summary.Projects.Single().Target.Kind);
        Assert.Single(summary.NewProjects); // 9_3-05: 옮기기만 가는 목적지도 만든다
        Assert.Equal(0, summary.ImportCount);

        AssertSucceeded(Apply(Plan(preview, backupPath, choices)));

        string newId = (string)Assert.Single(Rows("SELECT id FROM projects"))[0]!;
        Assert.Equal(newId, Column(threadId, "project_id"));
        Assert.Equal(CanonicalPath.Create(folder).Display, Column(threadId, "cwd"));
        var state = (GlobalStateJsonObject)GlobalStateJson.Parse(File.ReadAllText(GlobalStatePath));
        Assert.Single(((GlobalStateJsonObject)state.Get("local-projects")!).Members); // 9_5a 레거시 항목
        Assert.Empty(((GlobalStateJsonObject)state.Get("thread-project-assignments")!).Members); // 대화 소속 키는 쓰지 않는다
    }

    [Fact]
    public void R3_이_PC가_더_최신인_대화도_옮기고_rollout은_그대로다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string rollout = RolloutPathOf(threadId);
        File.AppendAllText(rollout, EventLine(2, "이 PC에서 이어 씀"));
        string rolloutHash = FileHash(rollout);
        string folder = RegisterFolder("local-ahead");

        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = RelinkChoices(preview, folder, threadId);
        Assert.Equal(RevisionRelation.LocalAhead, SelectionOf(preview, choices, threadId).Preview.Relation);
        AssertSucceeded(Apply(Plan(preview, backupPath, choices)));

        Assert.Equal(RegisteredProjectId, Column(threadId, "project_id"));
        Assert.Equal(rolloutHash, FileHash(rollout));
    }

    [Fact]
    public void R4_이어받기와_옮기기를_함께_고르면_둘_다_한다()
    {
        string threadId = NewId();
        ImportOnceAsUncategorized(threadId);
        string rollout = RolloutPathOf(threadId);
        long lengthBefore = new FileInfo(rollout).Length;
        _sourceExtraLines[threadId] = EventLine(2, "원본 PC에서 이어 씀");
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", MissingFolder("orig")));
        string folder = RegisterFolder("incoming");

        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = RelinkChoices(preview, folder, threadId);
        ImportSelectionConversation selected = SelectionOf(preview, choices, threadId);
        Assert.Equal(RevisionRelation.IncomingAhead, selected.Preview.Relation);
        Assert.True(selected.IsIncludedByUser);   // 이어받기(기본 체크)
        Assert.True(selected.IsRelinkSelected);   // 옮기기(따로 고름)
        AssertSucceeded(Apply(Plan(preview, backupPath, choices)));

        Assert.Equal(RegisteredProjectId, Column(threadId, "project_id"));
        Assert.True(new FileInfo(rollout).Length > lengthBefore);
    }

    public static TheoryData<string> RelinkExclusions() => new() { "assigned", "projectless", "gate-failed", "uncategorized-target" };

    [Theory]
    [MemberData(nameof(RelinkExclusions))]
    public void R5_Desktop이_따로_기록했거나_상태를_모르거나_목적지가_기타_대화면_옮기지_않는다(string variant)
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("excluded");
        switch (variant)
        {
            case "assigned":
                TestCodexHomeBuilder.WriteGlobalState(_pcBHome, [], threadAssignments: new Dictionary<string, string> { [threadId] = "legacy-x" });
                break;
            case "projectless":
                TestCodexHomeBuilder.WriteGlobalState(_pcBHome, [], projectlessThreadIds: [threadId]);
                break;
            case "gate-failed":
                File.WriteAllText(GlobalStatePath, File.ReadAllText(GlobalStatePath).Replace(",", ", ", StringComparison.Ordinal));
                break;
        }

        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = variant == "uncategorized-target"
            ? Choices(preview) with { RelinkThreadIds = new HashSet<string>([threadId]) } // 목적지: 원본 폴더 없음 → 기타 대화
            : RelinkChoices(preview, folder, threadId);
        ImportSelectionConversation selected = SelectionOf(preview, choices, threadId);
        RelinkStatus expected = variant switch
        {
            "assigned" => RelinkStatus.DesktopAssigned,
            "projectless" => RelinkStatus.DesktopProjectless,
            "gate-failed" => RelinkStatus.DesktopStateUnavailable,
            _ => RelinkStatus.TargetNotProject,
        };
        Assert.Equal(expected, selected.Relink);
        Assert.False(selected.IsRelinkSelected);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(0, summary.RelinkCount);
        Assert.Contains(summary.Warnings, w => w.Contains("옮기기 선택에서 무시", StringComparison.Ordinal));
        Assert.False(summary.CanApply); // 쓸 것이 없다
    }

    [Fact]
    public void R6_옮기기를_고르지_않으면_9_3_이전과_같다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("regression");
        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = Choices(preview, new Dictionary<string, ProjectTargetDecision> { [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) });

        Assert.Empty(choices.RelinkThreadIds);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(RelinkStatus.Available, summary.Conversations.Single().Relink); // 고를 수는 있지만
        Assert.Equal(0, summary.RelinkCount);                                      // 고르지 않았다
        Assert.Equal(ImportNothingToWriteReason.AllAlreadyPresent, summary.NothingToWriteReason);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath, choices);
        Assert.NotNull(plan);
        Assert.Empty(plan!.Relinks);
        Assert.False(plan.UsesProjectTarget(plan.Projects.Single()));
    }

    // ── 9_3-T2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void T2_계획_뒤_위치가_바뀌면_Planner가_거부하고_아무것도_쓰지_않는다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("moved");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId));
        TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, threadId, "cwd", @"C:\Elsewhere");
        string dbBefore = FileHash(TestCodexHomeBuilder.FindStateDbPath(_pcBHome));

        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(dbBefore, FileHash(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateDirectories(_snapshotRoot).Count() > 1); // 첫 가져오기 Snapshot만
    }

    [Fact]
    public void T2_트랜잭션_직전에_project_id가_바뀌면_영향_행_0으로_Rollback하고_새_프로젝트와_global_state를_되돌린다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = NewFolder("toctou");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId)); // CreateNew + 옮기기
        byte[] globalBefore = File.ReadAllBytes(GlobalStatePath);
        TestCodexHomeBuilder.InsertProject(_pcBHome, "019a0000-0000-7000-8000-0000000000c9", "다른", NewFolder("other"));

        var hook = new ActionAt(RestoreFaultInjectionPoint.BeforeSqliteTransaction, () =>
            TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, threadId, "project_id", "019a0000-0000-7000-8000-0000000000c9"));
        RestoreResult result = Apply(plan, hook);

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, Count("projects"));                    // 새 프로젝트 행은 되돌려졌다(미리 있던 "다른"만)
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Null(Column(threadId, "project_id"));           // Snapshot 시점 값으로 복구
        Assert.Equal(globalBefore, File.ReadAllBytes(GlobalStatePath));
    }

    [Fact]
    public void T2_계획_뒤_Desktop_배정에_들어가면_거부하고_쓰지_않는다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("assigned-later");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId));
        TestCodexHomeBuilder.WriteGlobalState(_pcBHome, [], threadAssignments: new Dictionary<string, string> { [threadId] = "legacy-x" });
        byte[] globalAfterChange = File.ReadAllBytes(GlobalStatePath);
        string dbBefore = FileHash(TestCodexHomeBuilder.FindStateDbPath(_pcBHome));

        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(dbBefore, FileHash(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)));
        Assert.Equal(globalAfterChange, File.ReadAllBytes(GlobalStatePath));
    }

    [Fact]
    public void T2_Snapshot_뒤_global_state가_바뀌면_트랜잭션_안에서_거부하고_되돌린다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("hash-later");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId)); // 옮기기만(프로젝트 생성 없음)
        byte[] globalBefore = File.ReadAllBytes(GlobalStatePath);

        var hook = new ActionAt(RestoreFaultInjectionPoint.AfterSnapshot, () =>
            TestCodexHomeBuilder.WriteGlobalState(_pcBHome, [], threadAssignments: new Dictionary<string, string> { [threadId] = "legacy-x" }));
        RestoreResult result = Apply(plan, hook);

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(globalBefore, File.ReadAllBytes(GlobalStatePath)); // 옮기기 때문에 global-state에 쓰지 않았고, Snapshot 바이트로 복구
    }

    [Fact]
    public void T2_Snapshot_뒤_global_state가_무관하게_바뀌어도_해시가_달라_트랜잭션_안에서_거부한다()
    {
        // 9_3-06 — 위치와 관계없는 변경(형식은 그대로)이라 사후 검증으로는 잡히지 않는다. 계획 시점 해시 비교만이 거부한다.
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("hash-unrelated");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId));
        byte[] globalBefore = File.ReadAllBytes(GlobalStatePath);

        var hook = new ActionAt(RestoreFaultInjectionPoint.AfterSnapshot, () =>
            TestCodexHomeBuilder.WriteGlobalState(_pcBHome, [new TestCodexHomeBuilder.LegacyProject("unrelated-legacy", "무관", @"C:\Unrelated")]));
        RestoreResult result = Apply(plan, hook);

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(globalBefore, File.ReadAllBytes(GlobalStatePath));
    }

    [Fact]
    public void T2_연결_변경_직후_실패하면_Rollback한다()
    {
        string threadId = NewId();
        string backupPath = ImportOnceAsUncategorized(threadId);
        string folder = RegisterFolder("fault");
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, RelinkChoices(preview, folder, threadId));

        RestoreResult result = Apply(plan, new ThrowAt(RestoreFaultInjectionPoint.AfterThreadProjectLink));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Null(Column(threadId, "project_id"));
    }
}
