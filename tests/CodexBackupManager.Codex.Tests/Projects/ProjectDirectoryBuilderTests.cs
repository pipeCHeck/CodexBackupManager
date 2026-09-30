using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Projects;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Projects;

/// <summary>
/// Phase 9_1-T1 — <see cref="ProjectDirectoryBuilder"/>/<see cref="ProjectDirectory"/>의 통합 규칙(설계 §4.2).
/// 파일을 읽지 않는 순수 입력만 쓴다(루트 실존 판정만 temp 폴더를 쓴다).
/// </summary>
public sealed class ProjectDirectoryBuilderTests
{
    private static ProjectTableReader.ProjectRow Db(string id, string? name = null) => new(id, name);

    private static ProjectTableReader.ProjectRootRow DbRoot(string projectId, string path) => new(projectId, path);

    private static Dictionary<string, LocalProjectInfo> Legacy(params LocalProjectInfo[] projects)
        => projects.ToDictionary(p => p.ProjectId, StringComparer.Ordinal);

    private static CanonicalPath C(string path) => CanonicalPath.Create(path);

    [Fact]
    public void DB_전용_프로젝트는_DB_ID가_Key다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", "Alpha")], [DbRoot("db-1", @"C:\Fixture\Alpha"), DbRoot("db-1", @"C:\Fixture\Alpha2")],
            legacyProjects: null, legacyToDbProjectIds: null, conversationCountsByProjectId: null);

        KnownProject project = Assert.Single(directory.Projects);
        Assert.Equal("db-1", project.Key);
        Assert.Equal("db-1", project.DbProjectId);
        Assert.Equal("db-1", project.PrimaryId);
        Assert.False(project.IsLegacyOnly);
        Assert.Empty(project.LegacyProjectIds);
        Assert.Equal("Alpha", project.DisplayName);
        Assert.Equal(2, project.Roots.Count);
        Assert.Same(project, directory.FindById("db-1"));
        Assert.Same(project, directory.FindByRoot(C(@"c:\fixture\alpha2\")).Project);
    }

    [Fact]
    public void 레거시_전용_프로젝트는_legacy_접두사_Key이고_DbProjectId가_없다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [], [],
            Legacy(new LocalProjectInfo("legacy-1", "Beta", [@"C:\Fixture\Beta"])),
            legacyToDbProjectIds: null, conversationCountsByProjectId: null);

        KnownProject project = Assert.Single(directory.Projects);
        Assert.Equal("legacy:legacy-1", project.Key);
        Assert.Null(project.DbProjectId);
        Assert.True(project.IsLegacyOnly);
        Assert.Equal("legacy-1", project.PrimaryId);
        Assert.Equal(["legacy-1"], project.LegacyProjectIds);
        Assert.Equal("Beta", project.DisplayName);
        Assert.Same(project, directory.FindById("legacy-1"));
        Assert.Same(project, directory.FindByKey("legacy:legacy-1"));
        Assert.Null(directory.FindById("legacy:legacy-1")); // Key는 ID 조회에 쓰지 않는다
    }

    [Fact]
    public void 매핑이_가리키는_DB_프로젝트에_레거시를_합치고_루트를_합집합으로_만든다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", "Gamma DB")], [DbRoot("db-1", @"C:\Fixture\Gamma")],
            Legacy(new LocalProjectInfo("legacy-1", "Gamma Legacy", [@"C:\Fixture\Gamma", @"D:\Other\Gamma"])),
            new Dictionary<string, string> { ["legacy-1"] = "db-1" },
            conversationCountsByProjectId: null);

        KnownProject project = Assert.Single(directory.Projects);
        Assert.Equal("db-1", project.Key);
        Assert.Equal(["legacy-1"], project.LegacyProjectIds);
        Assert.Equal("Gamma DB", project.DisplayName); // DB 이름 우선
        Assert.Equal([@"C:\Fixture\Gamma", @"D:\Other\Gamma"], project.Roots.Select(r => r.DisplayPath));
        Assert.Same(directory.FindById("db-1"), directory.FindById("legacy-1"));
        Assert.Equal(ProjectLookupKind.Found, directory.FindByRoot(C(@"D:\Other\Gamma")).Kind);
    }

    [Fact]
    public void 매핑이_없으면_canonical_루트가_같은_DB_프로젝트가_하나일_때만_합친다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", null)], [DbRoot("db-1", @"C:\Fixture\Delta")],
            Legacy(new LocalProjectInfo("legacy-1", "Delta", [@"\\?\c:\FIXTURE\delta\"])),
            legacyToDbProjectIds: null, conversationCountsByProjectId: null);

        KnownProject project = Assert.Single(directory.Projects);
        Assert.Equal("db-1", project.DbProjectId);
        Assert.Equal(["legacy-1"], project.LegacyProjectIds);
        Assert.Equal("Delta", project.DisplayName); // DB 이름이 없으면 레거시 이름
        Assert.Single(project.Roots); // canonical 기준 중복 제거
    }

    [Fact]
    public void 매핑이_실존하지_않는_DB_프로젝트를_가리키면_루트_규칙으로_판단한다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", "Delta")], [DbRoot("db-1", @"C:\Fixture\Delta")],
            Legacy(
                new LocalProjectInfo("legacy-1", "Delta", [@"C:\Fixture\Delta"]),
                new LocalProjectInfo("legacy-2", "Elsewhere", [@"C:\Fixture\Elsewhere"])),
            new Dictionary<string, string> { ["legacy-1"] = "db-missing", ["legacy-2"] = "db-missing" },
            conversationCountsByProjectId: null);

        Assert.Equal("db-1", directory.FindById("legacy-1")!.DbProjectId);
        KnownProject elsewhere = directory.FindById("legacy-2")!;
        Assert.True(elsewhere.IsLegacyOnly);
        Assert.Null(directory.FindById("db-missing"));
    }

    [Fact]
    public void 같은_루트를_가진_DB_프로젝트가_둘이면_Ambiguous이고_레거시는_자동으로_합치지_않는다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", "A"), Db("db-2", "B")],
            [DbRoot("db-1", @"C:\Fixture\Shared"), DbRoot("db-2", @"C:\Fixture\Shared\")],
            Legacy(new LocalProjectInfo("legacy-1", "Shared", [@"C:\Fixture\Shared"])),
            legacyToDbProjectIds: null, conversationCountsByProjectId: null);

        Assert.Equal(3, directory.Projects.Count);
        Assert.True(directory.FindById("legacy-1")!.IsLegacyOnly);

        ProjectLookupResult lookup = directory.FindByRoot(C(@"c:\fixture\shared"));
        Assert.Equal(ProjectLookupKind.Ambiguous, lookup.Kind);
        Assert.Null(lookup.Project);
        Assert.Equal(["db-1", "db-2", "legacy:legacy-1"], lookup.Candidates.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 매핑이_있으면_루트가_여러_DB와_겹쳐도_매핑_대상에_합친다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1", "A"), Db("db-2", "B")],
            [DbRoot("db-1", @"C:\Fixture\Shared"), DbRoot("db-2", @"C:\Fixture\Shared")],
            Legacy(new LocalProjectInfo("legacy-1", "Shared", [@"C:\Fixture\Shared"])),
            new Dictionary<string, string> { ["legacy-1"] = "db-2" },
            conversationCountsByProjectId: null);

        Assert.Equal(2, directory.Projects.Count);
        Assert.Equal("db-2", directory.FindById("legacy-1")!.Key);
        Assert.Equal(ProjectLookupKind.Ambiguous, directory.FindByRoot(C(@"C:\Fixture\Shared")).Kind);
    }

    [Fact]
    public void 찾을_수_없는_루트는_None이다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1")], [DbRoot("db-1", @"C:\Fixture\Alpha")], null, null, null);

        Assert.Equal(ProjectLookupKind.None, directory.FindByRoot(C(@"C:\Fixture\Alpha\Sub")).Kind); // 하위 폴더는 일치 아님
        Assert.Equal(ProjectLookupKind.None, ProjectDirectory.Empty.FindByRoot(C(@"C:\Fixture\Alpha")).Kind);
        Assert.Null(directory.FindById("nope"));
        Assert.Null(directory.FindById(null));
    }

    [Fact]
    public void 루트_실존은_Directory_Exists로_판정한다()
    {
        string existing = Path.Combine(Path.GetTempPath(), "cbm-projdir-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existing);
        string missing = existing + "-missing";

        try
        {
            ProjectDirectory directory = ProjectDirectoryBuilder.Build(
                [Db("db-1")], [DbRoot("db-1", existing), DbRoot("db-1", missing)], null, null, null);

            KnownProject project = Assert.Single(directory.Projects);
            Assert.True(project.Roots.Single(r => r.DisplayPath == existing).ExistsOnDisk);
            Assert.False(project.Roots.Single(r => r.DisplayPath == missing).ExistsOnDisk);
        }
        finally
        {
            Directory.Delete(existing);
        }
    }

    [Fact]
    public void 대화_수는_DB_ID와_레거시_ID_배정을_합친다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1"), Db("db-empty")], [DbRoot("db-1", @"C:\Fixture\Alpha")],
            Legacy(new LocalProjectInfo("legacy-1", null, [@"C:\Fixture\Alpha"])),
            new Dictionary<string, string> { ["legacy-1"] = "db-1" },
            new Dictionary<string, int> { ["db-1"] = 2, ["legacy-1"] = 3, ["unknown"] = 7 });

        Assert.Equal(5, directory.FindById("db-1")!.ConversationCount);
        Assert.Equal(0, directory.FindById("db-empty")!.ConversationCount);
    }

    [Fact]
    public void local_projects에_없는_레거시_ID도_매핑이_실존_DB를_가리키면_조회된다()
    {
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("db-1")], [DbRoot("db-1", @"C:\Fixture\Alpha")],
            legacyProjects: null,
            new Dictionary<string, string> { ["legacy-stale"] = "db-1", ["legacy-orphan"] = "db-gone" },
            conversationCountsByProjectId: null);

        Assert.Equal("db-1", directory.FindById("legacy-stale")!.Key);
        Assert.Null(directory.FindById("legacy-orphan"));
        Assert.Single(directory.Projects);
    }

    [Fact]
    public void Key가_충돌하는_비정상_입력은_예외_없이_충돌_항목만_제외하고_경고를_남긴다()
    {
        // DB 프로젝트 ID가 우연히 레거시 전용 프로젝트의 Key("legacy:" + 레거시 ID)와 같은 비정상 데이터.
        var warnings = new List<string>();
        ProjectDirectory directory = ProjectDirectoryBuilder.Build(
            [Db("legacy:dup", "Weird DB"), Db("db-ok", "Ok")],
            [DbRoot("legacy:dup", @"C:\Fixture\Weird"), DbRoot("db-ok", @"C:\Fixture\Ok")],
            Legacy(new LocalProjectInfo("dup", "Dup", [@"C:\Fixture\Dup"])),
            legacyToDbProjectIds: null, conversationCountsByProjectId: null, warnings);

        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.DoesNotContain("dup", w)); // ID 원문 없음
        Assert.Equal(2, directory.Projects.Count);
        Assert.NotNull(directory.FindById("db-ok"));
        Assert.Equal("legacy:dup", directory.FindByKey("legacy:dup")!.DbProjectId); // 먼저 온 DB 프로젝트가 남는다
        Assert.Null(directory.FindById("dup"));                                   // 충돌한 레거시 전용 항목은 제외
    }

    [Fact]
    public void ProjectDirectory는_한_ID가_두_프로젝트에_걸리면_거부한다()
    {
        var a = new KnownProject("db-1", "db-1", new HashSet<string> { "legacy-1" }, "A", [], 0);
        var b = new KnownProject("db-2", "db-2", new HashSet<string> { "legacy-1" }, "B", [], 0);

        Assert.Throws<ArgumentException>(() => new ProjectDirectory([a, b]));
    }
}
