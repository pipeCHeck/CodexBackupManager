using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 백업 프로젝트 하나의 대상 PC 목적지(<see cref="ProjectTarget"/>)를 판정한다(Phase 9_1-06, 설계 §6 "9_1").
/// </summary>
/// <remarks>
/// <para>
/// 판정은 <b>루트 경로 → 대상 PC의 <see cref="ProjectDirectory"/></b>로만 한다. 백업 manifest의 프로젝트 ID나
/// 대화의 <c>resolvedProjectId</c>는 쓰지 않는다(설계 §4.4 — ID는 PC마다 다르다).
/// </para>
/// <list type="number">
///   <item>폴더 = 사용자가 고른 폴더, 없으면 원본 루트 중 이 PC에 실존(<see cref="Directory.Exists(string?)"/>)하는 첫 번째.</item>
///   <item>폴더가 없으면 → Uncategorized / <see cref="ProjectTargetReason.OriginalRootMissing"/>.</item>
///   <item>
///     <see cref="ProjectDirectory.FindByRoot"/>: Found + DB ID → LinkExisting, Found + 레거시 전용 → Uncategorized /
///     <see cref="ProjectTargetReason.LegacyOnlyProject"/>, Ambiguous → Uncategorized / <see cref="ProjectTargetReason.AmbiguousRoot"/>,
///     None → Uncategorized / <c>*Unregistered</c>.
///   </item>
///   <item>
///     (Phase 9_5-01) None과 레거시 전용은 <see cref="ProjectDirectory.ProjectCreation"/>에 따라 바뀐다:
///     Supported → <see cref="ProjectTargetKind.CreateNew"/>(같은 사유, 이름 기본값 = 폴더 이름),
///     SchemaUnsupported → Uncategorized / <see cref="ProjectTargetReason.CreationUnsupported"/>,
///     Disabled → 위와 같은 Uncategorized(9_2까지의 동작). 절대 경로가 아닌 폴더로는 만들지 않는다.
///   </item>
/// </list>
/// <para>파일 I/O는 <see cref="Directory.Exists(string?)"/>뿐이다.</para>
/// </remarks>
public static class ProjectTargetResolver
{
    /// <summary>
    /// 백업 프로젝트의 목적지를 판정한다. 백업의 "기타 대화" 그룹이나 원본 루트가 없는 그룹은 사용자가 폴더를 고르지 않았으면
    /// <see cref="ProjectTargetReason.NotApplicable"/>이다(Phase 9_5 — 폴더를 고르면 그 폴더로 판정한다).
    /// </summary>
    public static ProjectTarget Resolve(BackupProjectMetadata backupProject, string? userSelectedFolder, ProjectDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(backupProject);
        ArgumentNullException.ThrowIfNull(directory);
        if (!string.IsNullOrWhiteSpace(userSelectedFolder))
        {
            return ResolveFolder(userSelectedFolder, userSelected: true, directory);
        }

        if (backupProject.IsUncategorized || backupProject.OriginalRootPaths.Count == 0)
        {
            return ProjectTarget.Uncategorized(ProjectTargetReason.NotApplicable, null);
        }

        return Resolve(backupProject.OriginalRootPaths, userSelectedFolder, directory);
    }

    /// <summary>원본 루트들과 (선택) 사용자 폴더로 목적지를 판정한다.</summary>
    public static ProjectTarget Resolve(
        IReadOnlyList<string> originalRootPaths, string? userSelectedFolder, ProjectDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(originalRootPaths);
        ArgumentNullException.ThrowIfNull(directory);

        if (!string.IsNullOrWhiteSpace(userSelectedFolder))
        {
            return ResolveFolder(userSelectedFolder, userSelected: true, directory);
        }

        foreach (string root in originalRootPaths)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                return ResolveFolder(root, userSelected: false, directory);
            }
        }

        return ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null);
    }

    /// <summary>
    /// 이미 정해진 폴더 하나를 <paramref name="directory"/>로 판정한다. Apply 시점에 Plan의 목적지를 fresh 상태로
    /// 다시 판정할 때도 이 메서드를 쓴다(같은 규칙).
    /// </summary>
    /// <param name="folder">판정할 폴더.</param>
    /// <param name="userSelected">사용자가 고른 폴더인지(사유 구분에만 쓴다).</param>
    /// <param name="directory">대상 PC의 프로젝트 목록.</param>
    public static ProjectTarget ResolveFolder(string folder, bool userSelected, ProjectDirectory directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(directory);

        ProjectTargetReason unregistered = userSelected
            ? ProjectTargetReason.UserSelectedUnregistered
            : ProjectTargetReason.OriginalRootExistsUnregistered;

        if (!CanonicalPath.TryCreate(folder, out CanonicalPath? canonical, out _))
        {
            return ProjectTarget.Uncategorized(unregistered, folder);
        }

        string folderPath = userSelected ? canonical!.Display : folder;
        ProjectLookupResult lookup = directory.FindByRoot(canonical!);
        switch (lookup.Kind)
        {
            case ProjectLookupKind.Found when lookup.Project!.DbProjectId is { } dbProjectId:
                // Phase 9_1-14/16 — 연결할 때는 원본 문자열이나 사용자가 고른 표기가 아니라 이 PC project_roots에 저장된
                // 루트 표기를 쓴다(canonical은 같아도 대소문자/\\?\ 접두사가 다를 수 있다. 이 값이 Apply 때 threads.cwd가 된다).
                return ProjectTarget.Link(
                    userSelected ? ProjectTargetReason.UserSelectedRegistered : ProjectTargetReason.OriginalRootRegistered,
                    RegisteredRootDisplay(lookup.Project, canonical!) ?? folderPath,
                    dbProjectId);

            case ProjectLookupKind.Found:
                return Unlinked(ProjectTargetReason.LegacyOnlyProject, folderPath, canonical!, directory);

            case ProjectLookupKind.Ambiguous:
                // 9_2-22 보류: 어느 프로젝트로 연결할지 모르므로 연결하지도, 새로 만들지도 않는다.
                return ProjectTarget.Uncategorized(ProjectTargetReason.AmbiguousRoot, folderPath);

            default:
                return Unlinked(unregistered, folderPath, canonical!, directory);
        }
    }

    /// <summary>
    /// (Phase 9_5-01) 연결할 DB 프로젝트가 없는 폴더. 이 PC가 생성을 지원하면 새 프로젝트(폴더 표기 = canonical Display, 절대 경로만),
    /// 스키마가 다르면 CreationUnsupported, 스위치가 꺼졌으면 지금까지처럼 기타 대화다.
    /// </summary>
    private static ProjectTarget Unlinked(ProjectTargetReason reason, string folderPath, CanonicalPath canonical, ProjectDirectory directory)
    {
        switch (directory.ProjectCreation)
        {
            case ProjectCreationSupport.Supported when IsAbsolute(canonical):
                return ProjectTarget.Create(reason, canonical.Display, DefaultProjectName(canonical));
            case ProjectCreationSupport.SchemaUnsupported:
                return ProjectTarget.Uncategorized(ProjectTargetReason.CreationUnsupported, folderPath);
            default:
                return ProjectTarget.Uncategorized(reason, folderPath);
        }
    }

    /// <summary>공식 <c>validate_roots</c>처럼 절대 경로(드라이브 루트 또는 UNC)만 프로젝트 루트로 쓴다.</summary>
    public static bool IsAbsolute(CanonicalPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.RootKind is PathRootKind.DriveRooted or PathRootKind.Unc;
    }

    /// <summary>새 프로젝트 이름 기본값: 폴더 이름(마지막 경로 요소). 드라이브 루트처럼 요소가 없으면 표시 경로 그대로.</summary>
    public static string DefaultProjectName(CanonicalPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.Segments.Count > 0 ? folder.Segments[^1] : folder.Display;
    }

    private static string? RegisteredRootDisplay(KnownProject project, CanonicalPath canonical)
    {
        foreach (KnownProjectRoot root in project.Roots)
        {
            if (root.Canonical.Equals(canonical))
            {
                return root.DisplayPath;
            }
        }

        return null;
    }

    /// <summary>
    /// 자동 판정 결과를 Phase 6 호환 <see cref="ProjectPathMapping"/>으로 옮긴다. <see cref="ProjectPathMappingStatus"/>의
    /// 기존 의미는 바꾸지 않는다: LinkExisting → AutoLinked(연결 루트), 판정 대상 아님 → NotApplicable, 그 밖 → NotFound.
    /// </summary>
    public static ProjectPathMapping ToAutomaticMapping(BackupProjectMetadata backupProject, ProjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(backupProject);
        ArgumentNullException.ThrowIfNull(target);

        ProjectPathMappingStatus status = target switch
        {
            { Reason: ProjectTargetReason.NotApplicable } => ProjectPathMappingStatus.NotApplicable,
            { Kind: ProjectTargetKind.LinkExisting } => ProjectPathMappingStatus.AutoLinked,
            _ => ProjectPathMappingStatus.NotFound,
        };

        return status == ProjectPathMappingStatus.AutoLinked
            ? new ProjectPathMapping(
                backupProject.ProjectId, backupProject.DisplayName, backupProject.OriginalRootPaths,
                status, target.LinkDbProjectId, target.FolderPath)
            : new ProjectPathMapping(
                backupProject.ProjectId, backupProject.DisplayName, backupProject.OriginalRootPaths,
                status, null, null);
    }
}
