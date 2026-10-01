using System;
using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Catalog;

/// <summary>
/// (Phase 9_3-00) Codex Desktop이 global-state에 따로 기록한 대화 위치(읽기 전용). 이 앱은 여기 들어 있는 대화의 프로젝트를 옮기지 않는다 —
/// Desktop이 DB <c>threads.project_id</c>보다 이 기록을 우선할 수 있는데 확인되지 않았기 때문이다(docs/import-ux-redesign-phase9.md §6 "9_3").
/// </summary>
/// <param name="IsAvailable">
/// global-state를 읽었고 9_5a 게이트(<c>GlobalStateProjectGate</c>)를 통과했는지. 아니면 어떤 대화도 옮기지 않는다.
/// </param>
/// <param name="AssignedThreadIds"><c>thread-project-assignments</c>의 키 전부(projectKind와 무관).</param>
/// <param name="ProjectlessThreadIds"><c>projectless-thread-ids</c>.</param>
public sealed record DesktopThreadPlacement(
    bool IsAvailable,
    IReadOnlySet<string> AssignedThreadIds,
    IReadOnlySet<string> ProjectlessThreadIds)
{
    /// <summary>확인하지 못한 상태(기본값 — 직접 만든 카탈로그 포함).</summary>
    public static DesktopThreadPlacement Unavailable { get; } = new(
        false, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}
