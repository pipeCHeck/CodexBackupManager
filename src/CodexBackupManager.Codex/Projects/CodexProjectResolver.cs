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
/// <para>
/// <b>authoritative source는 <c>threadAssignmentsMigrated</c> 플래그가 결정한다.</b>
/// (<see cref="GlobalStateReader"/> 문서 참고) 이 값을 무시하고 "값이 채워져 있는 쪽을 무조건 신뢰"하면
/// migration이 스레드 단위로 부분 진행된 상태에서 <c>threads.project_id</c>에 남은 낡은/불완전한 값이
/// <c>.codex-global-state.json</c>의 최신 할당을 덮어써 버리는 오판이 생길 수 있다.
/// </para>
/// <para>정책(<see cref="Resolve(ThreadRow, bool?, GlobalStateReader.ProjectGraph?, IReadOnlyList{RootCandidate})"/>):</para>
/// <list type="bullet">
///   <item>
///     <b><c>threadAssignmentsMigrated == false</c></b> — <c>.codex-global-state.json</c>이
///     authoritative. <c>thread-project-assignments</c> → <c>projectless-thread-ids</c> 순으로 먼저 확인한다.
///     <c>threads.project_id</c>는 global-state에 이 thread에 대한 언급이 전혀 없을 때만
///     "약한 폴백"으로 참고한다(값이 있다고 해서 먼저 신뢰하지 않는다).
///   </item>
///   <item>
///     <b><c>threadAssignmentsMigrated == true</c></b> — <c>threads.project_id</c>가 authoritative.
///     값이 없을 때만 예전 global-state 데이터(할당/미지정)를 "호환 폴백"으로 본다.
///   </item>
///   <item>
///     <b><c>threadAssignmentsMigrated == null</c></b> — 어느 쪽이 완료됐다고 추측하지 않는다.
///     <c>false</c>와 동일하게 <b>global-state를 먼저 신뢰</b>한다: 값이 존재한다는 이유만으로
///     <c>threads.project_id</c>를 migration 완료의 증거로 삼지 않기 위한 결정론적 폴백이다.
///   </item>
///   <item>
///     위 두 경로 모두에서 결론이 나지 않으면 canonicalized <c>cwd</c> ↔ 알려진 프로젝트 루트 비교,
///     그래도 못 찾으면 "기타 대화".
///   </item>
/// </list>
/// </remarks>
public static class CodexProjectResolver
{
    /// <summary>알려진 프로젝트 루트 후보 하나. cwd 폴백에 쓴다.</summary>
    /// <param name="ProjectId">이 루트가 속한 프로젝트 ID.</param>
    /// <param name="Root">정규화된 루트 경로.</param>
    public sealed record RootCandidate(string ProjectId, CanonicalPath Root);

    /// <summary>thread 하나의 프로젝트를 해결한다.</summary>
    /// <param name="thread">해결할 thread.</param>
    /// <param name="threadAssignmentsMigrated">
    /// <c>.codex-global-state.json</c>의 <c>app-server-projects-migration-by-host.threadAssignmentsMigrated</c>.
    /// 어느 저장소가 authoritative인지 결정한다(클래스 remarks 참고).
    /// </param>
    /// <param name="projectGraph"><c>.codex-global-state.json</c>에서 읽은 그래프. 못 읽었으면 <c>null</c>.</param>
    /// <param name="rootCandidates">
    /// cwd 폴백에 쓸 후보 목록(state DB <c>project_roots</c> + global-state <c>local-projects.rootPaths</c> 통합).
    /// </param>
    public static ProjectAssignment Resolve(
        ThreadRow thread,
        bool? threadAssignmentsMigrated,
        GlobalStateReader.ProjectGraph? projectGraph,
        IReadOnlyList<RootCandidate> rootCandidates)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(rootCandidates);

        // false와 null 둘 다 "global-state를 먼저 신뢰"한다.
        // null을 false와 다르게 취급해 state project_id를 먼저 믿는 것은
        // "값이 있으니 migration이 끝났을 것"이라는 추측이 되므로 하지 않는다.
        bool trustGlobalStateFirst = threadAssignmentsMigrated != true;

        if (trustGlobalStateFirst)
        {
            if (TryResolveFromGlobalState(thread, projectGraph, out ProjectAssignment fromGlobalState))
            {
                return fromGlobalState;
            }

            // global-state가 이 thread에 대해 아무 말도 하지 않을 때만 state project_id를 약한 폴백으로 쓴다.
            if (!string.IsNullOrWhiteSpace(thread.ProjectId))
            {
                return new ProjectAssignment(thread.ProjectId, ProjectAssignmentSource.StateProjectId);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(thread.ProjectId))
            {
                return new ProjectAssignment(thread.ProjectId, ProjectAssignmentSource.StateProjectId);
            }

            // state에 값이 없을 때만 예전 global-state 데이터를 호환 폴백으로 본다.
            if (TryResolveFromGlobalState(thread, projectGraph, out ProjectAssignment fromGlobalState))
            {
                return fromGlobalState;
            }
        }

        // cwd ↔ 알려진 프로젝트 루트 비교.
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

        // 마지막 수단: 기타 대화.
        return new ProjectAssignment(null, ProjectAssignmentSource.Unassigned);
    }

    /// <summary>
    /// <c>.codex-global-state.json</c>에서만 판단한다: 명시적 할당이 있으면 그것,
    /// 명시적으로 "프로젝트 없음"이면 <see cref="ProjectAssignmentSource.Unassigned"/>.
    /// 둘 다 없으면(이 thread에 대해 global-state가 아무 말도 하지 않으면) <c>false</c>.
    /// </summary>
    private static bool TryResolveFromGlobalState(
        ThreadRow thread,
        GlobalStateReader.ProjectGraph? projectGraph,
        out ProjectAssignment result)
    {
        if (projectGraph is not null)
        {
            if (projectGraph.ThreadProjectAssignments.TryGetValue(thread.Id, out string? assignedProjectId))
            {
                result = new ProjectAssignment(assignedProjectId, ProjectAssignmentSource.GlobalStateAssignment);
                return true;
            }

            if (projectGraph.ProjectlessThreadIds.Contains(thread.Id))
            {
                result = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned);
                return true;
            }
        }

        result = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned);
        return false;
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
