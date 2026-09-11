using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Projects;

/// <summary>
/// 프로젝트↔대화 연결을 어느 경로로 해결했는지.
/// docs/codex-storage-format.md §5 "프로젝트 ↔ 대화 연결 — 3중 구조".
/// </summary>
public enum ProjectAssignmentSource
{
    /// <summary>신규(마이그레이션 완료) 경로 — <c>threads.project_id</c>.</summary>
    StateProjectId = 1,

    /// <summary>현재 유효한 경로 — <c>.codex-global-state.json</c>의 <c>thread-project-assignments</c>.</summary>
    GlobalStateAssignment = 2,

    /// <summary>폴백 — canonicalized <c>cwd</c>와 프로젝트 루트 비교.</summary>
    CwdFallback = 3,

    /// <summary>어디에도 없음 — "기타 대화".</summary>
    Unassigned = 4,
}

/// <summary>thread 하나의 프로젝트 연결 해결 결과.</summary>
/// <param name="ProjectId">해결된 프로젝트 ID. <see cref="ProjectAssignmentSource.Unassigned"/>면 <c>null</c>.</param>
/// <param name="Source">어느 경로로 해결했는지.</param>
public sealed record ProjectAssignment(string? ProjectId, ProjectAssignmentSource Source);

/// <summary>
/// 프로젝트 하나의 식별 정보. <c>state_*.sqlite projects</c> 또는
/// <c>.codex-global-state.json local-projects</c>에서 읽는다.
/// </summary>
/// <param name="ProjectId">프로젝트 ID.</param>
/// <param name="DisplayName">표시 이름. 없으면 <c>null</c>.</param>
/// <param name="RootPaths">이 프로젝트에 연결된 루트 경로들(원본 표기).</param>
public sealed record LocalProjectInfo(string ProjectId, string? DisplayName, IReadOnlyList<string> RootPaths);
