using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_5a-T1 — 새 프로젝트를 만들면 Codex Desktop 레거시 저장소(<c>local-projects</c>·<c>project-order</c>·매핑)에도 기록한다.
/// 게이트 실패 → 만들지 않고 기타 대화(Apply 성공 + 경고), fault injection·크래시 → DB와 global-state 모두 원래대로,
/// 계획 뒤 global-state 변경 → 거부.
/// </summary>
public sealed partial class ProjectCreateApplyTests
{
    private const string OtherDesktopLegacyId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private string GlobalStatePath => TestCodexHomeBuilder.GlobalStatePath(_pcBHome);

    private static string Js(string value)
    {
        var builder = new StringBuilder();
        GlobalStateJson.WriteString(builder, value);
        return builder.ToString();
    }

    /// <summary>
    /// 실측처럼 다른 키(비ASCII, 소수, 다른 host 매핑, 대화 배정 등)가 섞인 Desktop 상태를 손으로 쓴 JS stringify 형식으로 만든다.
    /// Desktop이 만든 프로젝트 하나가 이미 있다(DB에도 있다).
    /// </summary>
    private byte[] WriteRichDesktopState()
    {
        string otherRoot = NewFolder("desktop-made");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "019a0000-0000-7000-8000-0000000000d1", "데스크톱 프로젝트", otherRoot);
        string host = Js("local:" + _pcBHome);
        string json =
            "{\"electron-persisted-atom-state\":{\"ratio\":0.35,\"big\":1e+21,\"label\":\"한글 \\\"따옴표\\\" \\\\/ \\u0001\"}," +
            "\"project-order\":[" + Js(OtherDesktopLegacyId) + "]," +
            "\"thread-project-assignments\":{\"t1\":{\"projectKind\":\"local\",\"projectId\":" + Js(OtherDesktopLegacyId) + "}}," +
            "\"local-projects\":{" + Js(OtherDesktopLegacyId) + ":{\"id\":" + Js(OtherDesktopLegacyId) + ",\"name\":\"데스크톱 프로젝트\"," +
            "\"rootPaths\":[" + Js(CanonicalPath.Create(otherRoot).Display) + "],\"createdAt\":1759300000000,\"updatedAt\":1759300000123}}," +
            "\"pinned-project-ids\":[]," +
            "\"app-server-project-id-by-legacy-project-id-by-host\":{\"local:D:\\\\Other\\\\.codex\":{\"x\":\"y\"}," + host +
            ":{" + Js(OtherDesktopLegacyId) + ":\"019a0000-0000-7000-8000-0000000000d1\",\"deleted-legacy\":\"019a0000-0000-7000-8000-0000000000ff\"}}," +
            "\"app-server-projects-migration-by-host\":{" + host + ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false,\"pendingThreadAssignmentIds\":[\"p1\"]}}," +
            "\"emoji\":\"😀\\ud800\"}";
        byte[] bytes = GlobalStateJson.StrictUtf8.GetBytes(json);
        File.WriteAllBytes(GlobalStatePath, bytes);
        Assert.True(GlobalStateProjectGate.CheckBytes(bytes, CanonicalPath.Create(_pcBHome)).IsSupported); // 손으로 쓴 바이트가 stringify 형식
        return bytes;
    }

    private GlobalStateJsonObject ReadState(out string text)
    {
        text = File.ReadAllText(GlobalStatePath, Encoding.UTF8);
        return (GlobalStateJsonObject)GlobalStateJson.Parse(text);
    }

    private static string RawOf(string text, GlobalStateJsonNode node) => text[node.SourceStart..node.SourceEnd];

    [Fact]
    public void A1_새_프로젝트를_만들면_DB와_global_state_세_곳에_기록하고_다른_키는_바이트까지_그대로다()
    {
        byte[] original = WriteRichDesktopState();
        string folder = NewFolder("새 작업 폴더");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", MissingFolder("orig")));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, Choices(preview, new Dictionary<string, ProjectTargetDecision> { [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) }));

        AssertSucceeded(Apply(plan));

        object?[] project = Assert.Single(Rows("SELECT id, name, created_at_ms, position FROM projects WHERE id <> '019a0000-0000-7000-8000-0000000000d1'"));
        string dbId = (string)project[0]!;

        // 세 곳: local-projects 끝, project-order 끝, 현재 host 매핑 끝.
        GlobalStateJsonObject after = ReadState(out string afterText);
        var before = (GlobalStateJsonObject)GlobalStateJson.Parse(Encoding.UTF8.GetString(original));
        string beforeText = Encoding.UTF8.GetString(original);
        var localProjects = (GlobalStateJsonObject)after.Get("local-projects")!;
        Assert.Equal(2, localProjects.Members.Count);
        (string legacyId, GlobalStateJsonNode entryNode) = (localProjects.Members[1].Key, localProjects.Members[1].Value);
        Assert.True(Guid.TryParse(legacyId, out Guid legacyGuid));
        Assert.Equal(4, legacyGuid.Version);
        Assert.Equal(legacyGuid.ToString("D"), legacyId); // 소문자 하이픈
        string ms = ((long)project[2]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            "{\"id\":" + Js(legacyId) + ",\"name\":" + Js(Path.GetFileName(folder)) + ",\"rootPaths\":[" + Js(CanonicalPath.Create(folder).Display) +
            "],\"createdAt\":" + ms + ",\"updatedAt\":" + ms + "}",
            RawOf(afterText, entryNode));

        var order = (GlobalStateJsonArray)after.Get("project-order")!;
        Assert.Equal([OtherDesktopLegacyId, legacyId], order.Items.Select(i => ((GlobalStateJsonString)i).Value));
        var hostMap = (GlobalStateJsonObject)((GlobalStateJsonObject)after.Get(GlobalStateReader.LegacyProjectIdMappingKey)!).Members[1].Value;
        Assert.Equal(legacyId, hostMap.Members[^1].Key);
        Assert.Equal(dbId, ((GlobalStateJsonString)hostMap.Members[^1].Value).Value);
        Assert.Equal(3, hostMap.Members.Count); // 지운 프로젝트의 고아 매핑도 그대로

        // 바꾼 세 키를 뺀 최상위 키는 원문 조각까지 같고, 키 순서도 같다. 다른 host 매핑도 그대로다.
        Assert.Equal(before.Members.Select(m => m.Key), after.Members.Select(m => m.Key));
        foreach ((string key, GlobalStateJsonNode value) in before.Members)
        {
            if (key is not ("local-projects" or "project-order" or GlobalStateReader.LegacyProjectIdMappingKey))
            {
                Assert.Equal(RawOf(beforeText, value), RawOf(afterText, after.Get(key)!));
            }
        }

        var mapAfter = (GlobalStateJsonObject)after.Get(GlobalStateReader.LegacyProjectIdMappingKey)!;
        var mapBefore = (GlobalStateJsonObject)before.Get(GlobalStateReader.LegacyProjectIdMappingKey)!;
        Assert.Equal(RawOf(beforeText, mapBefore.Members[0].Value), RawOf(afterText, mapAfter.Members[0].Value));

        // 결과 바이트는 다시 게이트를 통과한다(Desktop이 읽을 수 있는 stringify 형식).
        Assert.True(GlobalStateProjectGate.Check(GlobalStatePath, CanonicalPath.Create(_pcBHome)).IsSupported);
        Assert.False(File.Exists(GlobalStatePath + GlobalStateWriter.TempSuffix));

        // fresh 카탈로그: 레거시 ID와 DB ID가 같은 프로젝트로 합쳐진다.
        CodexCatalog fresh = RestoreExecutor.BuildFreshCatalog(_pcBHome);
        KnownProject? known = fresh.ProjectDirectory.FindById(legacyId);
        Assert.NotNull(known);
        Assert.Same(known, fresh.ProjectDirectory.FindById(dbId));
        Assert.Equal(dbId, known!.DbProjectId);
        Assert.Contains(fresh.Projects.Single(p => p.ProjectId == dbId).Conversations, c => c.ThreadId == threadId);
    }

    [Fact]
    public void A2_두_개를_만들면_DB_position_순서대로_project_order_끝에_붙는다()
    {
        WriteRichDesktopState();
        string folderA = NewFolder("first");
        string folderB = NewFolder("second");
        string a = NewId();
        string b = NewId();
        string backupPath = Export(new SourceConversation(a, "pa", "A", MissingFolder("oa")), new SourceConversation(b, "pb", "B", MissingFolder("ob")));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, Choices(preview, new Dictionary<string, ProjectTargetDecision>
        {
            [KeyOf("pa")] = ProjectTargetDecision.Folder(folderA),
            [KeyOf("pb")] = ProjectTargetDecision.Folder(folderB),
        }));

        AssertSucceeded(Apply(plan));

        List<string> dbIdsByPosition = Rows("SELECT id FROM projects ORDER BY position").Select(r => (string)r[0]!).ToList();
        Assert.Equal(3, dbIdsByPosition.Count);
        GlobalStateJsonObject after = ReadState(out _);
        var hostMap = (GlobalStateJsonObject)((GlobalStateJsonObject)after.Get(GlobalStateReader.LegacyProjectIdMappingKey)!).Members[1].Value;
        Dictionary<string, string> legacyToDb = hostMap.Members.ToDictionary(m => m.Key, m => ((GlobalStateJsonString)m.Value).Value);
        List<string> orderedDbIds = ((GlobalStateJsonArray)after.Get("project-order")!).Items
            .Select(i => legacyToDb[((GlobalStateJsonString)i).Value]).ToList();

        Assert.Equal(dbIdsByPosition, orderedDbIds); // 사이드바 순서 = DB position(Desktop 실측 규칙)
    }

    public static TheoryData<string> GateFailureVariants() => new()
    {
        "bom", "indented", "field-added", "field-missing", "host-0", "host-2", "not-migrated", "missing",
    };

    private void BreakGlobalState(string variant)
    {
        string host = Js("local:" + _pcBHome);
        string entry = "{\"id\":\"l1\",\"name\":\"n\",\"rootPaths\":[\"C:\\\\r\"],\"createdAt\":1,\"updatedAt\":1}";
        string Doc(string lpEntry, string mapping, string migrated)
            => "{\"local-projects\":{\"l1\":" + lpEntry + "},\"project-order\":[\"l1\"],\"app-server-project-id-by-legacy-project-id-by-host\":" + mapping +
               ",\"app-server-projects-migration-by-host\":{" + host + ":{\"version\":1,\"projectsMigrated\":" + migrated + ",\"threadAssignmentsMigrated\":false}}}";
        string okMapping = "{" + host + ":{}}";

        switch (variant)
        {
            case "bom":
                File.WriteAllBytes(GlobalStatePath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Doc(entry, okMapping, "true"))]);
                break;
            case "indented":
                File.WriteAllText(GlobalStatePath, Doc(entry, okMapping, "true").Replace(",", ",\n  ", StringComparison.Ordinal));
                break;
            case "field-added":
                File.WriteAllText(GlobalStatePath, Doc(entry[..^1] + ",\"color\":\"red\"}", okMapping, "true"));
                break;
            case "field-missing":
                File.WriteAllText(GlobalStatePath, Doc(entry.Replace(",\"updatedAt\":1", string.Empty, StringComparison.Ordinal), okMapping, "true"));
                break;
            case "host-0":
                File.WriteAllText(GlobalStatePath, Doc(entry, "{\"local:D:\\\\Other\":{}}", "true"));
                break;
            case "host-2":
                File.WriteAllText(GlobalStatePath, Doc(entry, "{" + host + ":{}," + Js("local:" + _pcBHome.ToUpperInvariant() + "\\") + ":{}}", "true"));
                break;
            case "not-migrated":
                File.WriteAllText(GlobalStatePath, Doc(entry, okMapping, "false"));
                break;
            case "missing":
                File.Delete(GlobalStatePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    [Theory]
    [MemberData(nameof(GateFailureVariants))]
    public void A3_global_state_게이트가_실패하면_만들지_않고_기타_대화로_Apply가_성공하고_경고한다(string variant)
    {
        BreakGlobalState(variant);
        byte[]? before = File.Exists(GlobalStatePath) ? File.ReadAllBytes(GlobalStatePath) : null;
        string root = NewFolder("gate-" + variant);
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);

        Assert.Equal(ProjectCreationSupport.DesktopStateUnsupported, preview.LocalProjectDirectory.ProjectCreation);
        Assert.Equal(ProjectTargetReason.CreationUnsupported, TargetOf(preview, "pa").Reason);
        Assert.Equal(ProjectTargetKind.Uncategorized, TargetOf(preview, "pa").Kind);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, ImportUserChoices.CreateDefault(preview));
        Assert.Empty(summary.NewProjects);
        Assert.Contains(summary.Warnings, w => w.Contains("새 프로젝트를 만들지 않습니다", StringComparison.Ordinal));
        Assert.Contains(RestoreExecutor.BuildFreshCatalog(_pcBHome).Warnings, w => w.Contains("데스크톱 앱 상태 파일", StringComparison.Ordinal));

        AssertSucceeded(Apply(Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview))));

        Assert.Equal(0, Count("projects"));
        Assert.Null(Column(threadId, "project_id")); // 기타 대화
        Assert.Equal(before, File.Exists(GlobalStatePath) ? File.ReadAllBytes(GlobalStatePath) : null); // global-state는 손대지 않는다
    }

    [Theory]
    [InlineData(RestoreFaultInjectionPoint.DuringGlobalStateTempWrite)]
    [InlineData(RestoreFaultInjectionPoint.AfterGlobalStateReplace)]
    [InlineData(RestoreFaultInjectionPoint.BeforeGlobalStateValidation)]
    public void A4_global_state_쓰기_중_실패하면_DB와_global_state를_함께_되돌린다(RestoreFaultInjectionPoint point)
    {
        byte[] original = WriteRichDesktopState();
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", NewFolder("fault")));
        ImportPreview preview = Preview(backupPath);

        RestoreResult result = Apply(Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview)), new ThrowAt(point));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, Count("projects")); // 미리 있던 Desktop 프로젝트만
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(original, File.ReadAllBytes(GlobalStatePath));
        Assert.False(File.Exists(GlobalStatePath + GlobalStateWriter.TempSuffix));
    }

    [Theory]
    [InlineData(RestoreFaultInjectionPoint.AfterGlobalStateReplace)]
    [InlineData(RestoreFaultInjectionPoint.DuringGlobalStateTempWrite)]
    public void A5_크래시_시뮬레이션_global_state_교체_직후_강제_종료되면_다음_실행에서_DB와_global_state를_되돌린다(RestoreFaultInjectionPoint point)
    {
        byte[] original = WriteRichDesktopState();
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", NewFolder("crash-gs")));

        CrashRecoveryIntegrationTests.RunAndKillAtCrashPoint(_pcBHome, backupPath, point, _snapshotRoot, withChoices: true);
        Assert.Equal(2, Count("projects")); // 커밋은 끝났다
        if (point == RestoreFaultInjectionPoint.AfterGlobalStateReplace)
        {
            Assert.NotEqual(original, File.ReadAllBytes(GlobalStatePath)); // 교체도 끝났다
        }

        IncompleteApply stuck = Assert.Single(IncompleteApplyRecoveryService.FindIncompleteForHome(_snapshotRoot, _pcBHome));
        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(stuck.SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(1, Count("projects"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(original, File.ReadAllBytes(GlobalStatePath));
        Assert.False(File.Exists(GlobalStatePath + GlobalStateWriter.TempSuffix)); // 크래시가 남긴 temp도 정리
    }

    [Fact]
    public void A6_계획_후_global_state가_확인한_형태가_아니게_바뀌면_적용을_거부하고_아무것도_쓰지_않는다()
    {
        WriteRichDesktopState();
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", NewFolder("late-gs")));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));

        // 계획 뒤 다른 프로그램이 들여쓰기 형식으로 다시 썼다.
        string indented = File.ReadAllText(GlobalStatePath).Replace("{\"", "{\n  \"", StringComparison.Ordinal);
        File.WriteAllText(GlobalStatePath, indented);
        byte[] changed = File.ReadAllBytes(GlobalStatePath);
        string dbBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome))));

        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal(1, Count("projects"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(changed, File.ReadAllBytes(GlobalStatePath));
        Assert.Equal(dbBefore, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)))));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateDirectories(_snapshotRoot).Any()); // Snapshot도 만들지 않았다
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A6_Snapshot_뒤_global_state가_바뀌면_트랜잭션_안에서_거부하고_DB를_쓰지_않고_되돌린다()
    {
        byte[] original = WriteRichDesktopState();
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", NewFolder("late-gs2")));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));

        // Snapshot 직후(첫 쓰기 전) 파일이 여전히 stringify 형식이지만 다른 내용으로 바뀌었다 → 해시가 계획과 다르다.
        var hook = new ActionAt(RestoreFaultInjectionPoint.AfterSnapshot, () =>
            File.WriteAllBytes(GlobalStatePath, GlobalStateJson.StrictUtf8.GetBytes(Encoding.UTF8.GetString(original).Replace("0.35", "0.36", StringComparison.Ordinal))));

        RestoreResult result = Apply(plan, hook);

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, Count("projects"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        // Rollback은 Snapshot 바이트(계획 때 확인한 원본)로 되돌린다.
        Assert.Equal(original, File.ReadAllBytes(GlobalStatePath));
    }

    [Fact]
    public void A7_같은_백업을_다시_가져와도_사이드바_항목은_하나다()
    {
        WriteRichDesktopState();
        string root = NewFolder("again-gs");
        string first = NewId();
        string second = NewId();
        string backupPath = Export(new SourceConversation(first, "pa", "A", root), new SourceConversation(second, "pa", "A", root));

        ImportPreview preview1 = Preview(backupPath);
        AssertSucceeded(Apply(Plan(preview1, backupPath, Choices(preview1, include: [first]))));
        ImportPreview preview2 = Preview(backupPath);
        AssertSucceeded(Apply(Plan(preview2, backupPath, Choices(preview2, include: [second]))));

        GlobalStateJsonObject after = ReadState(out _);
        Assert.Equal(2, ((GlobalStateJsonObject)after.Get("local-projects")!).Members.Count);
        Assert.Equal(2, ((GlobalStateJsonArray)after.Get("project-order")!).Items.Count);
        Assert.Empty(SidebarRepairService.Detect(_pcBHome).Candidates);
    }

    private sealed class ActionAt(RestoreFaultInjectionPoint point, Action action) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint current)
        {
            if (current == point)
            {
                action();
            }
        }
    }
}
