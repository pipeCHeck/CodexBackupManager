using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_5a-T1 — 사이드바 보정(<see cref="SidebarRepairService"/>): 감지 정확도, 실행·멱등, Codex 실행 중 거부, 실패 → Rollback,
/// 크래시 → 다음 실행 복구. 합성 Codex Home만 쓴다.
/// </summary>
public sealed class SidebarRepairServiceTests : IDisposable
{
    private const string AppKeyPrefix = "codex-backup-manager:import:v1:";
    private const string DesktopLegacyId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-sidebar-repair-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _snapshotRoot;

    public SidebarRepairServiceTests()
    {
        _home = Path.Combine(_root, "home");
        _snapshotRoot = Path.Combine(_root, "snapshots");
        TestCodexHomeBuilder.CreateEmpty(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string GlobalStatePath => TestCodexHomeBuilder.GlobalStatePath(_home);

    private string NewFolder(string label)
    {
        string path = Path.Combine(_root, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private void InsertKey(string key, string projectId)
        => TestCodexHomeBuilder.Execute(_home, $"INSERT INTO project_idempotency_keys (key, project_id, created_at_ms) VALUES ('{key}', '{projectId}', 1)");

    private void SetCreatedAt(string projectId, long ms)
        => TestCodexHomeBuilder.Execute(_home, $"UPDATE projects SET created_at_ms = {ms} WHERE id = '{projectId}'");

    private string DbHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(TestCodexHomeBuilder.FindStateDbPath(_home))));

    private sealed record Scenario(string App1, string App2, string Desktop, string AppMapped, byte[] GlobalState);

    /// <summary>
    /// position 0: 이 앱 프로젝트(대상) · 1: Desktop 프로젝트(키 = 레거시 ID, 매핑 있음) · 2: 이 앱 프로젝트(대상, 한글 이름) ·
    /// 3: 이 앱 프로젝트지만 이미 매핑에 있음 · 고아 키(이 앱 접두사, 프로젝트 행 없음) · 지운 프로젝트의 고아 매핑.
    /// </summary>
    private Scenario Arrange()
    {
        const string app1 = "019a0000-0000-7000-8000-00000000a001";
        const string desktop = "019a0000-0000-7000-8000-00000000d001";
        const string app2 = "019a0000-0000-7000-8000-00000000a002";
        const string appMapped = "019a0000-0000-7000-8000-00000000a003";
        string desktopRoot = NewFolder("desktop");
        string mappedRoot = NewFolder("mapped");
        TestCodexHomeBuilder.InsertProject(_home, app1, "삼각형 3개", NewFolder("tri"));
        TestCodexHomeBuilder.InsertProject(_home, desktop, "Desktop", desktopRoot);
        TestCodexHomeBuilder.InsertProject(_home, app2, "과제 수행", NewFolder("task"), NewFolder("task-2"));
        TestCodexHomeBuilder.InsertProject(_home, appMapped, "Mapped", mappedRoot);
        SetCreatedAt(app1, 1_759_300_000_001);
        SetCreatedAt(app2, 1_759_300_000_002);
        InsertKey(AppKeyPrefix + "aa:01", app1);
        InsertKey(DesktopLegacyId, desktop);
        InsertKey(AppKeyPrefix + "aa:02", app2);
        InsertKey(AppKeyPrefix + "aa:03", appMapped);
        InsertKey(AppKeyPrefix + "aa:04", "019a0000-0000-7000-8000-0000000000ff"); // 고아 키

        TestCodexHomeBuilder.WriteGlobalState(
            _home,
            [
                new TestCodexHomeBuilder.LegacyProject(DesktopLegacyId, "Desktop", CanonicalPath.Create(desktopRoot).Display),
                new TestCodexHomeBuilder.LegacyProject("cccccccc-cccc-4ccc-8ccc-cccccccccccc", "Mapped", CanonicalPath.Create(mappedRoot).Display),
            ],
            legacyToDbProjectIds: new Dictionary<string, string>
            {
                [DesktopLegacyId] = desktop,
                ["cccccccc-cccc-4ccc-8ccc-cccccccccccc"] = appMapped,
                ["dddddddd-dddd-4ddd-8ddd-dddddddddddd"] = "019a0000-0000-7000-8000-0000000000ee", // 지운 프로젝트
            });
        return new Scenario(app1, app2, desktop, appMapped, File.ReadAllBytes(GlobalStatePath));
    }

    private SidebarRepairResult Repair(IRestoreFaultInjectionHook? hook = null, bool codexRunning = false)
        => SidebarRepairService.Repair(
            _home, codexRunning ? () => [new RunningProcessInfo("Codex", null)] : () => [], _snapshotRoot, hook, catalogBuilder: null);

    private sealed class ThrowAt(RestoreFaultInjectionPoint point) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint current)
        {
            if (current == point)
            {
                throw new InvalidOperationException("fault injection");
            }
        }
    }

    [Fact]
    public void 감지는_이_앱_키_접두사이고_매핑에_없는_실존_프로젝트만_DB_position_순으로_찾는다()
    {
        Scenario s = Arrange();
        string dbBefore = DbHash();

        SidebarRepairDetection detection = SidebarRepairService.Detect(_home);

        Assert.Equal(GlobalStateGateFailure.None, detection.GateFailure);
        Assert.Equal([s.App1, s.App2], detection.Candidates.Select(c => c.DbProjectId));
        SidebarRepairCandidate second = detection.Candidates[1];
        Assert.Equal("과제 수행", second.Name);
        Assert.Equal(2, second.RootPaths.Count);
        Assert.Equal(1_759_300_000_002, second.CreatedAtMs);
        Assert.Equal(s.GlobalState, File.ReadAllBytes(GlobalStatePath)); // 읽기 전용
        Assert.Equal(dbBefore, DbHash());
    }

    [Fact]
    public void 이_앱_프로젝트가_없으면_대상_0개()
    {
        TestCodexHomeBuilder.InsertProject(_home, "019a0000-0000-7000-8000-00000000d001", "Desktop", NewFolder("d"));
        InsertKey(DesktopLegacyId, "019a0000-0000-7000-8000-00000000d001");
        Assert.Empty(SidebarRepairService.Detect(_home).Candidates);
    }

    [Fact]
    public void 보정하면_DB_값으로_사이드바_끝에_추가하고_DB는_바꾸지_않고_다시_실행하면_할_일이_없다()
    {
        Scenario s = Arrange();
        string dbBefore = DbHash();

        SidebarRepairResult result = Repair();

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(dbBefore, DbHash());

        string text = File.ReadAllText(GlobalStatePath, Encoding.UTF8);
        var state = (GlobalStateJsonObject)GlobalStateJson.Parse(text);
        var order = ((GlobalStateJsonArray)state.Get("project-order")!).Items.Select(i => ((GlobalStateJsonString)i).Value).ToList();
        var hostMap = (GlobalStateJsonObject)((GlobalStateJsonObject)state.Get(GlobalStateReader.LegacyProjectIdMappingKey)!).Members[0].Value;
        Dictionary<string, string> legacyToDb = hostMap.Members.ToDictionary(m => m.Key, m => ((GlobalStateJsonString)m.Value).Value);
        Assert.Equal(4, order.Count);
        Assert.Equal([s.App1, s.App2], order.Skip(2).Select(id => legacyToDb[id]));

        var entry = (GlobalStateJsonObject)((GlobalStateJsonObject)state.Get("local-projects")!).Get(order[3])!;
        Assert.Equal("과제 수행", ((GlobalStateJsonString)entry.Get("name")!).Value);
        Assert.Equal(2, ((GlobalStateJsonArray)entry.Get("rootPaths")!).Items.Count);
        Assert.Equal("1759300000002", ((GlobalStateJsonNumber)entry.Get("createdAt")!).Raw);
        Assert.Equal("1759300000002", ((GlobalStateJsonNumber)entry.Get("updatedAt")!).Raw);

        CodexCatalog fresh = RestoreExecutor.BuildFreshCatalog(_home);
        KnownProject? merged = fresh.ProjectDirectory.FindById(order[2]);
        Assert.Same(merged, fresh.ProjectDirectory.FindById(s.App1));

        Assert.Empty(SidebarRepairService.Detect(_home).Candidates);
        byte[] afterFirst = File.ReadAllBytes(GlobalStatePath);
        SidebarRepairResult again = Repair();
        Assert.Equal(RestoreOutcome.NothingToDo, again.Outcome);
        Assert.Equal(afterFirst, File.ReadAllBytes(GlobalStatePath));
    }

    [Fact]
    public void Codex가_실행_중이면_시작하지_않는다()
    {
        Scenario s = Arrange();

        SidebarRepairResult result = Repair(codexRunning: true);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Contains("Codex", result.Message, StringComparison.Ordinal);
        Assert.Equal(s.GlobalState, File.ReadAllBytes(GlobalStatePath));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateDirectories(_snapshotRoot).Any());
    }

    [Fact]
    public void 게이트를_통과하지_못하면_쓰지_않는다()
    {
        Arrange();
        string indented = File.ReadAllText(GlobalStatePath).Replace(",\"", ",\n \"", StringComparison.Ordinal);
        File.WriteAllText(GlobalStatePath, indented);
        byte[] before = File.ReadAllBytes(GlobalStatePath);

        SidebarRepairDetection detection = SidebarRepairService.Detect(_home);
        Assert.Equal(GlobalStateGateFailure.RoundTripMismatch, detection.GateFailure);
        Assert.Equal(2, detection.Candidates.Count);

        SidebarRepairResult result = Repair();
        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Contains(nameof(GlobalStateGateFailure.RoundTripMismatch), result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(GlobalStatePath));
    }

    [Theory]
    [InlineData(RestoreFaultInjectionPoint.DuringGlobalStateTempWrite)]
    [InlineData(RestoreFaultInjectionPoint.AfterGlobalStateReplace)]
    [InlineData(RestoreFaultInjectionPoint.BeforeGlobalStateValidation)]
    public void 실패하면_Snapshot으로_되돌린다(RestoreFaultInjectionPoint point)
    {
        Scenario s = Arrange();

        SidebarRepairResult result = Repair(new ThrowAt(point));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(s.GlobalState, File.ReadAllBytes(GlobalStatePath));
        Assert.False(File.Exists(GlobalStatePath + GlobalStateWriter.TempSuffix));
        Assert.Equal(2, SidebarRepairService.Detect(_home).Candidates.Count);
        Assert.Empty(IncompleteApplyRecoveryService.FindIncompleteForHome(_snapshotRoot, _home));
    }

    [Fact]
    public void 크래시_시뮬레이션_교체_직후_강제_종료되면_미완료로_남고_복구하면_원래_바이트다()
    {
        Scenario s = Arrange();

        CrashRecoveryIntegrationTests.RunRepairAndKillAtCrashPoint(_home, RestoreFaultInjectionPoint.AfterGlobalStateReplace, _snapshotRoot);
        Assert.NotEqual(s.GlobalState, File.ReadAllBytes(GlobalStatePath));

        IncompleteApply stuck = Assert.Single(IncompleteApplyRecoveryService.FindIncompleteForHome(_snapshotRoot, _home));
        SidebarRepairResult blocked = Repair();
        Assert.Equal(RestoreOutcome.NotReady, blocked.Outcome); // 미완료 작업이 있으면 새 보정도 시작하지 않는다

        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(stuck.SnapshotDirectory, () => []);
        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(s.GlobalState, File.ReadAllBytes(GlobalStatePath));
    }
}
