using System.Collections.Generic;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Domain.Codex.Projects;

/// <summary>
/// <see cref="KnownProject"/>의 루트 하나(Phase 9_1, docs/import-ux-redesign-phase9.md §4.2).
/// </summary>
/// <param name="DisplayPath">원본 표기 그대로의 경로(<c>project_roots.path</c> 또는 <c>local-projects.rootPaths</c>).</param>
/// <param name="Canonical">비교용 정규화 경로.</param>
/// <param name="ExistsOnDisk">
/// 카탈로그를 만든 시점에 이 PC에 폴더가 실제로 있었는지(<c>Directory.Exists</c>, 읽기 전용 판정).
/// </param>
public sealed record KnownProjectRoot(string DisplayPath, CanonicalPath Canonical, bool ExistsOnDisk);

/// <summary>
/// 이 PC에 등록된 프로젝트 하나. 대화 유무와 관계없다(Phase 9_1, 설계 §4.2).
/// </summary>
/// <remarks>
/// <para>
/// SQLite <c>projects</c>(DB 프로젝트)와 <c>.codex-global-state.json local-projects</c>(레거시 프로젝트)를
/// 하나로 합친 결과다. <c>threads.project_id</c>에 쓸 수 있는 값은 오직 <see cref="DbProjectId"/>다
/// (<c>threads.project_id REFERENCES projects(id)</c>). 레거시 ID는 조회에만 쓴다.
/// </para>
/// </remarks>
/// <param name="Key">
/// 프로젝트를 구분하는 안정 키. <c>DbProjectId ?? "legacy:" + 대표 레거시 ID</c>.
/// DB 프로젝트가 없는 레거시 전용 프로젝트는 <c>"legacy:"</c> 접두사로 DB ID와 섞이지 않게 한다.
/// </param>
/// <param name="DbProjectId">SQLite <c>projects.id</c>. 레거시 전용 프로젝트면 <c>null</c>.</param>
/// <param name="LegacyProjectIds">이 프로젝트를 가리키는 global-state 레거시 ID 집합. 없으면 빈 집합.</param>
/// <param name="DisplayName">표시 이름. DB 이름 → 레거시 이름 → ID 순.</param>
/// <param name="Roots">루트 합집합(canonical 기준 중복 제거, 원본 표기 보존).</param>
/// <param name="ConversationCount">
/// 이 프로젝트(DB ID 또는 레거시 ID)로 배정된 사용자 대화 수. 카탈로그 없이 만들면 0.
/// </param>
public sealed record KnownProject(
    string Key,
    string? DbProjectId,
    IReadOnlySet<string> LegacyProjectIds,
    string DisplayName,
    IReadOnlyList<KnownProjectRoot> Roots,
    int ConversationCount)
{
    /// <summary><see cref="Key"/>에서 레거시 전용 프로젝트를 나타내는 접두사.</summary>
    public const string LegacyKeyPrefix = "legacy:";

    /// <summary>DB 프로젝트가 없는 레거시 전용 프로젝트인지.</summary>
    public bool IsLegacyOnly => DbProjectId is null;

    /// <summary>
    /// 카탈로그 그룹의 <c>ProjectEntry.ProjectId</c>로 쓰는 원시 ID(설계 §4.3): <c>DbProjectId ?? 대표 레거시 ID</c>.
    /// <see cref="Key"/>와 달리 접두사가 없는, Codex가 실제로 쓰는 ID다.
    /// </summary>
    public string PrimaryId => DbProjectId
        ?? (Key.StartsWith(LegacyKeyPrefix, System.StringComparison.Ordinal) ? Key[LegacyKeyPrefix.Length..] : Key);
}
