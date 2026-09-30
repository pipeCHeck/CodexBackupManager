using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_5-01 — 미등록 폴더의 목적지 판정(생성 지원 여부별)과 같은 루트 새 프로젝트 합치기. 순수 로직이다.
/// </summary>
public sealed class ProjectCreationTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-create-target-tests", Guid.NewGuid().ToString("N"));

    public ProjectCreationTargetTests() => Directory.CreateDirectory(_root);

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

    private static ProjectDirectory Dir(ProjectCreationSupport support, params KnownProject[] projects)
        => new(projects) { ProjectCreation = support };

    private static KnownProject Db(string id, string root)
        => new(id, id, new HashSet<string>(), id, [new KnownProjectRoot(root, CanonicalPath.Create(root), true)], 0);

    private static KnownProject LegacyOnly(string legacyId, string root)
        => new("legacy:" + legacyId, null, new HashSet<string> { legacyId }, legacyId, [new KnownProjectRoot(root, CanonicalPath.Create(root), true)], 0);

    [Fact]
    public void 생성을_지원하면_미등록_폴더는_CreateNew이고_루트는_표시_표기_이름은_폴더_이름이다()
    {
        string folder = Path.Combine(_root, "한글 폴더");
        ProjectTarget target = ProjectTargetResolver.ResolveFolder(@"\\?\" + folder + @"\", userSelected: true, Dir(ProjectCreationSupport.Supported));

        Assert.Equal(ProjectTargetKind.CreateNew, target.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, target.Reason);
        Assert.Equal(folder, target.FolderPath);
        Assert.Equal("한글 폴더", target.NewProjectName);
        Assert.Null(target.LinkDbProjectId);
    }

    [Fact]
    public void 원본_루트가_미등록이면_사유는_OriginalRootExistsUnregistered다()
    {
        string folder = Path.Combine(_root, "orig");
        ProjectTarget target = ProjectTargetResolver.ResolveFolder(folder, userSelected: false, Dir(ProjectCreationSupport.Supported));

        Assert.Equal(ProjectTarget.Create(ProjectTargetReason.OriginalRootExistsUnregistered, folder, "orig"), target);
    }

    [Fact]
    public void 레거시_전용_프로젝트_폴더도_새_프로젝트를_만든다()
    {
        string folder = Path.Combine(_root, "legacy");
        ProjectTarget target = ProjectTargetResolver.ResolveFolder(folder, userSelected: true, Dir(ProjectCreationSupport.Supported, LegacyOnly("leg-1", folder)));

        Assert.Equal(ProjectTargetKind.CreateNew, target.Kind);
        Assert.Equal(ProjectTargetReason.LegacyOnlyProject, target.Reason);
    }

    [Fact]
    public void 스키마가_다르면_CreationUnsupported_기능이_꺼지면_이전과_같은_기타_대화다()
    {
        string folder = Path.Combine(_root, "x");

        Assert.Equal(
            ProjectTarget.Uncategorized(ProjectTargetReason.CreationUnsupported, CanonicalPath.Create(folder).Display),
            ProjectTargetResolver.ResolveFolder(folder, userSelected: true, Dir(ProjectCreationSupport.SchemaUnsupported)));
        Assert.Equal(
            ProjectTarget.Uncategorized(ProjectTargetReason.UserSelectedUnregistered, CanonicalPath.Create(folder).Display),
            ProjectTargetResolver.ResolveFolder(folder, userSelected: true, Dir(ProjectCreationSupport.Disabled)));
        Assert.Equal(
            ProjectTarget.Uncategorized(ProjectTargetReason.LegacyOnlyProject, folder),
            ProjectTargetResolver.ResolveFolder(folder, userSelected: false, Dir(ProjectCreationSupport.Disabled, LegacyOnly("leg-2", folder))));
    }

    [Fact]
    public void 등록_프로젝트는_연결하고_Ambiguous는_만들지도_연결하지도_않는다()
    {
        string folder = Path.Combine(_root, "reg");
        Assert.Equal(ProjectTargetKind.LinkExisting,
            ProjectTargetResolver.ResolveFolder(folder, userSelected: true, Dir(ProjectCreationSupport.Supported, Db("db-1", folder))).Kind);

        ProjectTarget ambiguous = ProjectTargetResolver.ResolveFolder(
            folder, userSelected: true, Dir(ProjectCreationSupport.Supported, Db("db-1", folder), Db("db-2", folder)));
        Assert.Equal(ProjectTarget.Uncategorized(ProjectTargetReason.AmbiguousRoot, CanonicalPath.Create(folder).Display), ambiguous);
    }

    [Fact]
    public void 절대_경로가_아니면_새_프로젝트를_만들지_않는다()
    {
        ProjectTarget target = ProjectTargetResolver.ResolveFolder(@"relative\dir", userSelected: true, Dir(ProjectCreationSupport.Supported));

        Assert.Equal(ProjectTargetKind.Uncategorized, target.Kind);
    }

    [Fact]
    public void 같은_canonical_루트의_새_프로젝트는_하나로_합치고_첫_이름을_쓴다()
    {
        string folder = Path.Combine(_root, "shared");
        string other = Path.Combine(_root, "other");
        IReadOnlyList<NewProjectGroup> groups = NewProjectGrouping.Group(
        [
            ("a", ProjectTarget.Create(ProjectTargetReason.UserSelectedUnregistered, folder, "첫째")),
            ("b", ProjectTarget.Create(ProjectTargetReason.UserSelectedUnregistered, folder.ToUpperInvariant() + @"\", "둘째")),
            ("c", ProjectTarget.Create(ProjectTargetReason.UserSelectedUnregistered, other, "셋째")),
            ("d", ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null)),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["a", "b"], groups[0].ProjectKeys);
        Assert.Equal("첫째", groups[0].Name);
        Assert.True(groups[0].HasConflictingNames);
        Assert.Equal(folder, groups[0].FolderPath);
        Assert.False(groups[1].HasConflictingNames);
    }
}
