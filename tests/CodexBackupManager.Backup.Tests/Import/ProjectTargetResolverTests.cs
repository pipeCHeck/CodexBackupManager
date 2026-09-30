using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_1-06 / 9_1-T4 — <see cref="ProjectTargetResolver"/> 판정 규칙. 대상 PC의 프로젝트 목록은
/// <see cref="ProjectDirectory"/>를 직접 만들어 주고, 폴더 실존은 temp 폴더로 만든다.
/// </summary>
public sealed class ProjectTargetResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-target-resolver-tests", Guid.NewGuid().ToString("N"));

    public ProjectTargetResolverTests() => Directory.CreateDirectory(_root);

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

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Missing(string name) => Path.Combine(_root, name + "-missing");

    private static KnownProject Db(string id, string name, params string[] roots)
        => new(id, id, new HashSet<string>(), name, Roots(roots), 0);

    private static KnownProject LegacyOnly(string legacyId, params string[] roots)
        => new(KnownProject.LegacyKeyPrefix + legacyId, null, new HashSet<string> { legacyId }, legacyId, Roots(roots), 0);

    private static List<KnownProjectRoot> Roots(string[] roots)
    {
        var result = new List<KnownProjectRoot>();
        foreach (string root in roots)
        {
            result.Add(new KnownProjectRoot(root, CanonicalPath.Create(root), true));
        }

        return result;
    }

    [Fact]
    public void 원본_루트가_실존하고_등록_DB_프로젝트면_LinkExisting_OriginalRootRegistered다()
    {
        string root = Folder("registered");
        var directory = new ProjectDirectory([Db("db-1", "One", root)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([root], null, directory);

        Assert.Equal(ProjectTargetKind.LinkExisting, target.Kind);
        Assert.Equal(ProjectTargetReason.OriginalRootRegistered, target.Reason);
        Assert.Equal("db-1", target.LinkDbProjectId);
        Assert.Equal(root, target.FolderPath);
        Assert.Null(target.NewProjectName);
    }

    // ── 9_1-13 ProjectPathMapperTests에서 옮김 + 9_1-14(자동 연결 표기) ─────────────

    [Fact]
    public void 등록_루트가_extended_prefix로_저장돼_있어도_같은_위치로_자동_연결하고_이_PC_표기를_쓴다()
    {
        // (옮김) 원본 "C:\...\Foo" ↔ 로컬 "\\?\C:\...\Foo". 9_1-14: FolderPath/ResolvedLocalPath는 이 PC project_roots 표기.
        string root = Folder("Foo");
        string registeredDisplay = @"\\?\" + root;
        var directory = new ProjectDirectory([Db("local-1", "Foo (로컬)", registeredDisplay)]);
        var backupProject = new BackupProjectMetadata
        {
            ProjectId = "proj-a", DisplayName = "Project A", OriginalRootPaths = [root], ConversationThreadIds = [],
        };

        ProjectTarget target = ProjectTargetResolver.Resolve(backupProject, null, directory);
        ProjectPathMapping mapping = ProjectTargetResolver.ToAutomaticMapping(backupProject, target);

        Assert.Equal(ProjectTargetKind.LinkExisting, target.Kind);
        Assert.Equal(registeredDisplay, target.FolderPath);
        Assert.Equal(ProjectPathMappingStatus.AutoLinked, mapping.Status);
        Assert.Equal("local-1", mapping.LinkedLocalProjectId);
        Assert.Equal(registeredDisplay, mapping.ResolvedLocalPath);
    }

    [Fact]
    public void 대소문자만_다른_경로도_같은_위치로_자동_연결하고_이_PC_표기를_쓴다()
    {
        // (옮김) 원본 "…\Foo" ↔ 로컬 "…\foo"(소문자). 9_1-14: 이 PC 등록 표기를 쓴다.
        string root = Folder("CaseFoo");
        string registeredDisplay = root.ToLowerInvariant();
        var directory = new ProjectDirectory([Db("local-1", "Foo", registeredDisplay)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([root], null, directory);

        Assert.Equal(ProjectTargetKind.LinkExisting, target.Kind);
        Assert.Equal(registeredDisplay, target.FolderPath);
    }

    [Fact]
    public void 원본_루트가_여럿이면_이_PC에_실존하는_첫_번째를_쓴다()
    {
        string registeredButMissing = Missing("gone");
        string existing = Folder("second");
        var directory = new ProjectDirectory([Db("db-gone", "Gone", registeredButMissing), Db("db-2", "Two", existing)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([registeredButMissing, existing], null, directory);

        Assert.Equal("db-2", target.LinkDbProjectId);
    }

    [Fact]
    public void T4_원본_폴더가_실존하지만_미등록이면_OriginalRootExistsUnregistered다()
    {
        string root = Folder("unregistered");

        ProjectTarget target = ProjectTargetResolver.Resolve([root], null, ProjectDirectory.Empty);

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
        Assert.Equal(ProjectTargetReason.OriginalRootExistsUnregistered, target.Reason);
        Assert.Equal(root, target.FolderPath);
        Assert.Null(target.LinkDbProjectId);
    }

    [Fact]
    public void T4_원본_폴더가_없고_지정도_없으면_OriginalRootMissing이다_등록돼_있어도_연결하지_않는다()
    {
        string missing = Missing("orig");
        var directory = new ProjectDirectory([Db("db-1", "One", missing)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([missing], null, directory);

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
        Assert.Equal(ProjectTargetReason.OriginalRootMissing, target.Reason);
        Assert.Null(target.FolderPath);
    }

    [Fact]
    public void 사용자가_고른_폴더가_등록_프로젝트면_UserSelectedRegistered다()
    {
        string folder = Folder("chosen");
        var directory = new ProjectDirectory([Db("db-9", "Nine", folder)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([Missing("orig")], folder + @"\", directory);

        Assert.Equal(ProjectTargetKind.LinkExisting, target.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedRegistered, target.Reason);
        Assert.Equal("db-9", target.LinkDbProjectId);
        Assert.Equal(CanonicalPath.Create(folder).Display, target.FolderPath);
    }

    [Fact]
    public void T4_사용자가_고른_폴더가_미등록이면_UserSelectedUnregistered다()
    {
        string folder = Folder("chosen-unregistered");
        string registeredOriginal = Folder("orig-registered");
        var directory = new ProjectDirectory([Db("db-1", "One", registeredOriginal)]);

        // 원본 루트가 등록돼 있어도 사용자가 고른 폴더가 우선이다.
        ProjectTarget target = ProjectTargetResolver.Resolve([registeredOriginal], folder, directory);

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, target.Reason);
    }

    [Fact]
    public void 레거시_전용_프로젝트_폴더는_LegacyOnlyProject이고_연결하지_않는다()
    {
        string folder = Folder("legacy");
        var directory = new ProjectDirectory([LegacyOnly("leg-1", folder)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([folder], null, directory);

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
        Assert.Equal(ProjectTargetReason.LegacyOnlyProject, target.Reason);
        Assert.Null(target.LinkDbProjectId);
    }

    [Fact]
    public void 같은_루트가_여러_프로젝트에_있으면_AmbiguousRoot이고_연결하지_않는다()
    {
        string folder = Folder("shared");
        var directory = new ProjectDirectory([Db("db-1", "One", folder), Db("db-2", "Two", folder)]);

        ProjectTarget target = ProjectTargetResolver.Resolve([], folder, directory);

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
        Assert.Equal(ProjectTargetReason.AmbiguousRoot, target.Reason);
        Assert.Null(target.LinkDbProjectId);
    }

    [Fact]
    public void 백업의_기타_대화_그룹은_NotApplicable이고_호환_매핑도_NotApplicable이다()
    {
        var uncategorized = new BackupProjectMetadata { ProjectId = null, DisplayName = "기타 대화", OriginalRootPaths = [], ConversationThreadIds = [] };

        ProjectTarget target = ProjectTargetResolver.Resolve(uncategorized, null, ProjectDirectory.Empty);
        ProjectPathMapping mapping = ProjectTargetResolver.ToAutomaticMapping(uncategorized, target);

        Assert.Equal(ProjectTargetReason.NotApplicable, target.Reason);
        Assert.Equal(ProjectPathMappingStatus.NotApplicable, mapping.Status);
        Assert.False(mapping.CanManuallyOverride);
    }

    [Fact]
    public void 호환_매핑은_LinkExisting만_AutoLinked이고_나머지는_NotFound다()
    {
        string registered = Folder("m-registered");
        string shared = Folder("m-shared");
        string legacy = Folder("m-legacy");
        string unregistered = Folder("m-unregistered");
        var directory = new ProjectDirectory(
            [Db("db-1", "One", registered), Db("db-2", "Two", shared), Db("db-3", "Three", shared), LegacyOnly("leg", legacy)]);

        BackupProjectMetadata Project(string root)
            => new() { ProjectId = "backup-id", DisplayName = "P", OriginalRootPaths = [root], ConversationThreadIds = [] };

        ProjectPathMapping Map(string root)
        {
            BackupProjectMetadata project = Project(root);
            return ProjectTargetResolver.ToAutomaticMapping(project, ProjectTargetResolver.Resolve(project, null, directory));
        }

        ProjectPathMapping linked = Map(registered);
        Assert.Equal(ProjectPathMappingStatus.AutoLinked, linked.Status);
        Assert.Equal("db-1", linked.LinkedLocalProjectId);
        Assert.Equal(registered, linked.ResolvedLocalPath);

        foreach (string root in new[] { shared, legacy, unregistered, Missing("m-missing") })
        {
            ProjectPathMapping notFound = Map(root);
            Assert.Equal(ProjectPathMappingStatus.NotFound, notFound.Status);
            Assert.Null(notFound.ResolvedLocalPath);
            Assert.Null(notFound.LinkedLocalProjectId);
        }
    }

    [Fact]
    public void SameOutcomeAs는_Kind와_LinkDbProjectId만_비교한다()
    {
        ProjectTarget a = ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, @"C:\A", "db-1");
        ProjectTarget b = ProjectTarget.Link(ProjectTargetReason.UserSelectedRegistered, @"C:\B", "db-1");
        ProjectTarget c = ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, @"C:\A", "db-2");
        ProjectTarget d = ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootExistsUnregistered, @"C:\A");

        Assert.True(a.SameOutcomeAs(b));
        Assert.False(a.SameOutcomeAs(c));
        Assert.False(a.SameOutcomeAs(d));
        Assert.True(d.SameOutcomeAs(ProjectTarget.Uncategorized(ProjectTargetReason.AmbiguousRoot, null)));
    }
}
