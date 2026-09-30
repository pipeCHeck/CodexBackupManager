using System.Collections.Generic;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// backup 프로젝트의 원본 경로를 현재 PC의 로컬 카탈로그 프로젝트와 연결해본다(요구사항 12).
/// 판정만 한다 — 어떤 Codex 파일도 읽거나 쓰지 않는다(로컬 카탈로그는 이미 메모리에 있는 것을 쓴다).
/// </summary>
/// <remarks>
/// Phase 9_1b부터 <see cref="ImportPreviewBuilder"/>는 이 클래스를 쓰지 않는다. 대화가 있는 카탈로그 그룹만 보고
/// 폴더 실존을 확인하지 않아 결함 A/B의 원인이었다. 목적지는 <see cref="ProjectTargetResolver"/>가
/// ProjectDirectory 기준으로 판정하고, 호환 매핑은 <see cref="ProjectTargetResolver.ToAutomaticMapping"/>이 만든다.
/// </remarks>
public static class ProjectPathMapper
{
    /// <summary>
    /// backup 프로젝트 하나를 현재 PC의 프로젝트 목록과 대조한다. "기타 대화"(미분류) 그룹은
    /// 애초에 경로가 없으므로 <see cref="ProjectPathMappingStatus.NotApplicable"/>이다.
    /// </summary>
    public static ProjectPathMapping Resolve(BackupProjectMetadata backupProject, IReadOnlyList<ProjectEntry> localProjects)
    {
        if (backupProject.IsUncategorized || backupProject.OriginalRootPaths.Count == 0)
        {
            return new ProjectPathMapping(
                backupProject.ProjectId, backupProject.DisplayName, backupProject.OriginalRootPaths,
                ProjectPathMappingStatus.NotApplicable, null, null);
        }

        foreach (string originalPath in backupProject.OriginalRootPaths)
        {
            foreach (ProjectEntry local in localProjects)
            {
                foreach (string localRoot in local.RootPaths)
                {
                    if (CanonicalPath.AreSameLocation(originalPath, localRoot))
                    {
                        return new ProjectPathMapping(
                            backupProject.ProjectId, backupProject.DisplayName, backupProject.OriginalRootPaths,
                            ProjectPathMappingStatus.AutoLinked, local.ProjectId, localRoot);
                    }
                }
            }
        }

        return new ProjectPathMapping(
            backupProject.ProjectId, backupProject.DisplayName, backupProject.OriginalRootPaths,
            ProjectPathMappingStatus.NotFound, null, null);
    }
}
