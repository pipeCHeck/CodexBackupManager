using System;
using System.Collections.Generic;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Projects;

/// <summary>
/// thread 하나를 프로젝트에 연결한다. docs/codex-storage-format.md §5 "3중 구조"를 그대로 따른다.
/// </summary>
/// <remarks>
/// 우선순위:
/// <list type="number">
///   <item><c>threads.project_id</c>가 채워져 있으면 그대로 쓴다(신규 경로가 실제로 활성인 경우).</item>
///   <item>없으면 <c>.codex-global-state.json</c>의 <c>thread-project-assignments</c>.</item>
///   <item><c>projectless-thread-ids</c>에 명시되어 있으면 곧바로 "기타 대화".</item>
///   <item>그래도 없으면 canonicalized <c>cwd</c> ↔ 알려진 프로젝트 루트 비교.</item>
///   <item>그래도 못 찾으면 "기타 대화".</item>
/// </list>
/// <c>threadAssignmentsMigrated</c> 플래그 자체를 분기 조건으로 쓰지 않는 이유: 그 값과 무관하게
/// "실제로 값이 채워져 있는 경로"를 그대로 신뢰하는 편이 다음 마이그레이션 단계에서도 깨지지 않는다.
/// </remarks>
public static class CodexProjectResolver
{
    /// <summary>알려진 프로젝트 루트 후보 하나. cwd 폴백에 쓴다.</summary>
    /// <param name="ProjectId">이 루트가 속한 프로젝트 ID.</param>
    /// <param name="Root">정규화된 루트 경로.</param>
    public sealed record RootCandidate(string ProjectId, CanonicalPath Root);

    /// <summary>thread 하나의 프로젝트를 해결한다.</summary>
    /// <param name="thread">해결할 thread.</param>
    /// <param name="projectGraph"><c>.codex-global-state.json</c>에서 읽은 그래프. 못 읽었으면 <c>null</c>.</param>
    /// <param name="rootCandidates">
    /// cwd 폴백에 쓸 후보 목록(state DB <c>project_roots</c> + global-state <c>local-projects.rootPaths</c> 통합).
    /// </param>
    public static ProjectAssignment Resolve(
        ThreadRow thread,
        GlobalStateReader.ProjectGraph? projectGraph,
        IReadOnlyList<RootCandidate> rootCandidates)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(rootCandidates);

        // 1순위: state DB의 신규 project_id.
        if (!string.IsNullOrWhiteSpace(thread.ProjectId))
        {
            return new ProjectAssignment(thread.ProjectId, ProjectAssignmentSource.StateProjectId);
        }

        // 2순위: global-state의 thread-project-assignments.
        if (projectGraph is not null && projectGraph.ThreadProjectAssignments.TryGetValue(thread.Id, out string? assignedProjectId))
        {
            return new ProjectAssignment(assignedProjectId, ProjectAssignmentSource.GlobalStateAssignment);
        }

        // Codex가 이미 "프로젝트 없음"으로 명시한 thread는 cwd 추측을 시도하지 않는다.
        if (projectGraph is not null && projectGraph.ProjectlessThreadIds.Contains(thread.Id))
        {
            return new ProjectAssignment(null, ProjectAssignmentSource.Unassigned);
        }

        // 3순위: cwd ↔ 알려진 프로젝트 루트 비교.
        if (!string.IsNullOrWhiteSpace(thread.Cwd) && CanonicalPath.TryCreate(thread.Cwd, out CanonicalPath? cwd, out _))
        {
            foreach (RootCandidate candidate in rootCandidates)
            {
                if (IsUnderRoot(cwd!, candidate.Root))
                {
                    return new ProjectAssignment(candidate.ProjectId, ProjectAssignmentSource.CwdFallback);
                }
            }
        }

        // 4순위: 기타 대화.
        return new ProjectAssignment(null, ProjectAssignmentSource.Unassigned);
    }

    private static bool IsUnderRoot(CanonicalPath cwd, CanonicalPath root)
    {
        if (cwd.Equals(root))
        {
            return true;
        }

        string rootPrefix = root.Value.EndsWith('\\') ? root.Value : root.Value + "\\";
        return cwd.Value.StartsWith(rootPrefix, StringComparison.Ordinal);
    }
}
