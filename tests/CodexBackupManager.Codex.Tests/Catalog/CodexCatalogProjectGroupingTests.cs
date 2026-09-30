using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Catalog;

/// <summary>
/// Phase 9_1-T2 (결함 D) — 같은 폴더가 레거시 ID 그룹과 DB ID 그룹으로 나뉘어 중복 표시되지 않는지.
/// 합성 Codex Home(<see cref="FakeCodexHome"/> + <see cref="FakeStateDatabaseBuilder"/>)과 테스트 코드에서
/// 만든 global-state JSON만 쓴다.
/// </summary>
public sealed class CodexCatalogProjectGroupingTests : IDisposable
{
    private const string LegacyAssignedThread = "01d00000-0000-7000-8000-000000000001";
    private const string CwdFallbackThread = "01d00000-0000-7000-8000-000000000002";
    private const string DbAssignedThread = "01d00000-0000-7000-8000-000000000003";
    private const string GammaRoot = @"C:\Fixture\Gamma";

    private readonly FakeCodexHome _home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();

    public void Dispose() => _home.Dispose();

    private void WriteRollout(string threadId, string cwd)
    {
        string day = Path.Combine(_home.Path, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(day);
        string json = "{\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + threadId +
                      "\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}}\n";
        File.WriteAllText(Path.Combine(day, $"rollout-2026-01-02T03-04-05-{threadId}.jsonl"), json);
    }

    /// <summary>global-state를 쓴다. <paramref name="mapping"/>은 현재 Home의 host key 아래에 들어간다.</summary>
    private void WriteGlobalState(
        IReadOnlyDictionary<string, string> threadAssignments,
        IReadOnlyDictionary<string, string>? mapping)
    {
        var root = new Dictionary<string, object>
        {
            ["local-projects"] = new Dictionary<string, object>
            {
                ["legacy-gamma"] = new { id = "legacy-gamma", name = "Gamma (legacy)", rootPaths = new[] { GammaRoot } },
            },
            ["thread-project-assignments"] = threadAssignments.ToDictionary(
                p => p.Key, p => (object)new { projectKind = "local", projectId = p.Value }),
            ["app-server-projects-migration-by-host"] = new Dictionary<string, object>
            {
                ["local:" + _home.Path] = new { version = 1, projectsMigrated = true, threadAssignmentsMigrated = false },
            },
        };

        if (mapping is not null)
        {
            root["app-server-project-id-by-legacy-project-id-by-host"] = new Dictionary<string, object>
            {
                ["local:" + _home.Path] = mapping,
            };
        }

        File.WriteAllText(Path.Combine(_home.Path, ".codex-global-state.json"), JsonSerializer.Serialize(root));
    }

    private CodexInstallationInfo BuildInstallation(string stateDbPath)
    {
        string stateDbFileName = Path.GetFileName(stateDbPath);
        File.Copy(stateDbPath, Path.Combine(_home.Path, stateDbFileName), overwrite: true);

        CanonicalPath.TryCreate(_home.Path, out CanonicalPath? home, out _);
        var validation = new CodexHomeValidation(CodexHomeStatus.Valid, 5, [], [], [stateDbFileName]);
        var fileInfo = new FileInfo(Path.Combine(_home.Path, stateDbFileName));

        return new CodexInstallationInfo
        {
            Home = home!,
            HomeDisplayPath = home!.Display,
            Source = CodexHomeSource.UserSelected,
            Validation = validation,
            ActiveStateDatabase = new StateDatabaseInfo(stateDbFileName, 1, fileInfo.Length, fileInfo.LastWriteTimeUtc, false, false),
            ThreadAssignmentsMigrated = false,
        };
    }

    private static void AssertSingleGammaGroup(CodexCatalog catalog, params string[] expectedThreadIds)
    {
        List<ProjectEntry> named = catalog.Projects.Where(p => !p.IsUncategorized).ToList();
        ProjectEntry group = Assert.Single(named);
        Assert.Equal("db-gamma", group.ProjectId); // 그룹 ID = DbProjectId ?? 레거시 ID (설계 §4.3)
        Assert.Equal("Gamma", group.DisplayName);
        Assert.Equal(
            expectedThreadIds.OrderBy(x => x, StringComparer.Ordinal),
            group.Conversations.Select(c => c.ThreadId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(GammaRoot, Assert.Single(group.RootPaths));
    }

    [Fact]
    public void 레거시_ID로_배정된_대화와_cwd_폴백_대화가_매핑으로_같은_프로젝트면_한_그룹이다()
    {
        WriteRollout(LegacyAssignedThread, GammaRoot);
        WriteRollout(CwdFallbackThread, GammaRoot + @"\sub");
        WriteGlobalState(
            new Dictionary<string, string> { [LegacyAssignedThread] = "legacy-gamma" },
            new Dictionary<string, string> { ["legacy-gamma"] = "db-gamma" });

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(LegacyAssignedThread, cwd: GammaRoot, createdAtMs: 1)
            .WithThread(CwdFallbackThread, cwd: GammaRoot + @"\sub", createdAtMs: 2)
            .WithProject("db-gamma", "Gamma")
            .WithProjectRoot("db-gamma", GammaRoot)
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        AssertSingleGammaGroup(catalog, LegacyAssignedThread, CwdFallbackThread);

        // 원시 배정 결과(ConversationEntry.Project)는 바꾸지 않는다 — authority 규칙은 그대로다.
        ConversationEntry legacy = catalog.AllConversations.Single(c => c.ThreadId == LegacyAssignedThread);
        Assert.Equal("legacy-gamma", legacy.Project.ProjectId);
        Assert.Equal(ProjectAssignmentSource.GlobalStateAssignment, legacy.Project.Source);
        ConversationEntry fallback = catalog.AllConversations.Single(c => c.ThreadId == CwdFallbackThread);
        Assert.Equal("db-gamma", fallback.Project.ProjectId);
        Assert.Equal(ProjectAssignmentSource.CwdFallback, fallback.Project.Source);
    }

    [Fact]
    public void 매핑이_없어도_루트가_같은_DB_프로젝트가_하나뿐이면_한_그룹이다()
    {
        WriteRollout(LegacyAssignedThread, GammaRoot);
        WriteRollout(CwdFallbackThread, GammaRoot);
        WriteGlobalState(new Dictionary<string, string> { [LegacyAssignedThread] = "legacy-gamma" }, mapping: null);

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(LegacyAssignedThread, cwd: GammaRoot, createdAtMs: 1)
            .WithThread(CwdFallbackThread, cwd: GammaRoot, createdAtMs: 2)
            .WithProject("db-gamma", "Gamma")
            .WithProjectRoot("db-gamma", GammaRoot)
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        AssertSingleGammaGroup(catalog, LegacyAssignedThread, CwdFallbackThread);
    }

    [Fact]
    public void 레거시_ID로_배정된_대화와_DB_ID로_배정된_대화가_같은_프로젝트면_한_그룹이다()
    {
        WriteRollout(LegacyAssignedThread, GammaRoot);
        WriteRollout(DbAssignedThread, GammaRoot);
        WriteGlobalState(
            new Dictionary<string, string> { [LegacyAssignedThread] = "legacy-gamma" },
            new Dictionary<string, string> { ["legacy-gamma"] = "db-gamma" });

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(LegacyAssignedThread, cwd: GammaRoot, createdAtMs: 1)
            .WithThread(DbAssignedThread, cwd: GammaRoot, projectId: "db-gamma", createdAtMs: 2)
            .WithProject("db-gamma", "Gamma")
            .WithProjectRoot("db-gamma", GammaRoot)
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        AssertSingleGammaGroup(catalog, LegacyAssignedThread, DbAssignedThread);
        Assert.Equal(
            ProjectAssignmentSource.StateProjectId,
            catalog.AllConversations.Single(c => c.ThreadId == DbAssignedThread).Project.Source);
    }

    [Fact]
    public void 프로젝트_ID가_충돌하는_비정상_데이터여도_카탈로그는_만들어지고_경고가_남는다()
    {
        // Phase 9_1-11 — DB 프로젝트 ID가 레거시 전용 프로젝트("legacy-gamma", 매핑 없음·루트 다름)의 Key와 같다.
        WriteRollout(LegacyAssignedThread, GammaRoot);
        WriteGlobalState(new Dictionary<string, string> { [LegacyAssignedThread] = "legacy-gamma" }, mapping: null);

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(LegacyAssignedThread, cwd: GammaRoot, createdAtMs: 1)
            .WithProject("legacy:legacy-gamma", "Weird")
            .WithProjectRoot("legacy:legacy-gamma", @"C:\Fixture\Weird")
            .WithProject("db-ok", "Ok")
            .WithProjectRoot("db-ok", @"C:\Fixture\Ok")
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        Assert.Contains(catalog.Warnings, w => w.Contains("제외"));
        Assert.DoesNotContain(catalog.Warnings, w => w.Contains("legacy-gamma") || w.Contains(GammaRoot));
        Assert.NotNull(catalog.ProjectDirectory.FindById("db-ok"));
        Assert.Equal(2, catalog.ProjectDirectory.Projects.Count);
        Assert.Equal(1, catalog.UserConversationCount); // 대화 목록은 그대로
    }

    [Fact]
    public void 카탈로그는_대화가_0개인_프로젝트까지_ProjectDirectory로_노출한다()
    {
        WriteRollout(LegacyAssignedThread, GammaRoot);
        WriteGlobalState(
            new Dictionary<string, string> { [LegacyAssignedThread] = "legacy-gamma" },
            new Dictionary<string, string> { ["legacy-gamma"] = "db-gamma" });

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(LegacyAssignedThread, cwd: GammaRoot, createdAtMs: 1)
            .WithProject("db-gamma", "Gamma")
            .WithProjectRoot("db-gamma", GammaRoot)
            .WithProject("db-empty", "Empty")
            .WithProjectRoot("db-empty", @"C:\Fixture\Empty")
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        Assert.Single(catalog.Projects, p => !p.IsUncategorized); // 트리는 여전히 대화가 있는 그룹만
        Assert.Equal(2, catalog.ProjectDirectory.Projects.Count);

        KnownProject gamma = catalog.ProjectDirectory.FindById("legacy-gamma")!;
        Assert.Equal("db-gamma", gamma.DbProjectId);
        Assert.Equal(1, gamma.ConversationCount);

        KnownProject empty = catalog.ProjectDirectory.FindById("db-empty")!;
        Assert.Equal(0, empty.ConversationCount);
        Assert.Equal(ProjectLookupKind.Found, catalog.ProjectDirectory.FindByRoot(CanonicalPath.Create(@"c:\fixture\empty\")).Kind);
    }
}
