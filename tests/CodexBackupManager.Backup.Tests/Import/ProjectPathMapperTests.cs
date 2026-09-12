using System.Collections.Generic;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// <see cref="ProjectPathMapper"/>가 backup 프로젝트의 원본 경로를 현재 PC의 프로젝트와 canonical
/// path 기준으로만 연결하는지(대소문자/<c>\\?\</c> prefix 차이는 무시) 확인한다. 어떤 파일도
/// 읽거나 쓰지 않는다 — 이미 메모리에 있는 두 목록만 비교한다.
/// </summary>
public sealed class ProjectPathMapperTests
{
    private static BackupProjectMetadata BackupProject(string? projectId, string displayName, params string[] rootPaths)
        => new() { ProjectId = projectId, DisplayName = displayName, OriginalRootPaths = rootPaths, ConversationThreadIds = [] };

    [Fact]
    public void 현재_PC에_동일한_canonical_path가_있으면_자동_연결한다()
    {
        BackupProjectMetadata backupProject = BackupProject("proj-a", "Project A", @"C:\_UserProjects\Foo");
        var localProjects = new List<ProjectEntry> { new("local-1", "Foo (로컬)", [@"\\?\C:\_UserProjects\Foo"], []) };

        ProjectPathMapping mapping = ProjectPathMapper.Resolve(backupProject, localProjects);

        Assert.Equal(ProjectPathMappingStatus.AutoLinked, mapping.Status);
        Assert.Equal("local-1", mapping.LinkedLocalProjectId);
        Assert.Equal(@"\\?\C:\_UserProjects\Foo", mapping.ResolvedLocalPath);
    }

    [Fact]
    public void 대소문자만_다른_경로도_같은_위치로_인식해_연결한다()
    {
        BackupProjectMetadata backupProject = BackupProject("proj-a", "Project A", @"C:\_UserProjects\Foo");
        var localProjects = new List<ProjectEntry> { new("local-1", "Foo", [@"c:\_userprojects\foo"], []) };

        ProjectPathMapping mapping = ProjectPathMapper.Resolve(backupProject, localProjects);

        Assert.Equal(ProjectPathMappingStatus.AutoLinked, mapping.Status);
    }

    [Fact]
    public void 일치하는_로컬_프로젝트가_없으면_NotFound다()
    {
        BackupProjectMetadata backupProject = BackupProject("proj-a", "Project A", @"C:\Old\Project");
        var localProjects = new List<ProjectEntry> { new("local-1", "다른 프로젝트", [@"D:\Other"], []) };

        ProjectPathMapping mapping = ProjectPathMapper.Resolve(backupProject, localProjects);

        Assert.Equal(ProjectPathMappingStatus.NotFound, mapping.Status);
        Assert.Null(mapping.LinkedLocalProjectId);
    }

    [Fact]
    public void 미분류_그룹은_NotApplicable이다()
    {
        BackupProjectMetadata backupProject = BackupProject(null, "기타 대화");

        ProjectPathMapping mapping = ProjectPathMapper.Resolve(backupProject, []);

        Assert.Equal(ProjectPathMappingStatus.NotApplicable, mapping.Status);
    }
}
