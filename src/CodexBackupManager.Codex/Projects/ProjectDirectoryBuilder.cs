using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Projects;

/// <summary>
/// SQLite 프로젝트와 global-state 레거시 프로젝트를 하나의 <see cref="ProjectDirectory"/>로 합친다
/// (Phase 9_1-04, docs/import-ux-redesign-phase9.md §4.2).
/// </summary>
/// <remarks>
/// <para>통합 규칙:</para>
/// <list type="number">
///   <item>DB 프로젝트(<c>projects</c>)마다 <see cref="KnownProject"/>를 하나 만든다. 루트는 <c>project_roots</c>.</item>
///   <item>
///     레거시 프로젝트는 <c>app-server-project-id-by-legacy-project-id-by-host</c> 매핑이 가리키는 DB 프로젝트에
///     합친다(<see cref="KnownProject.LegacyProjectIds"/>에 추가, 루트 합집합). 매핑이 가리키는 DB 프로젝트가
///     실제로 없으면 매핑이 없는 것으로 본다.
///   </item>
///   <item>
///     매핑이 없는 레거시 프로젝트는 canonical 루트가 겹치는 DB 프로젝트가 <b>정확히 하나</b>일 때만 합친다.
///     아니면 레거시 전용 프로젝트(<see cref="KnownProject.DbProjectId"/> = <c>null</c>)다.
///   </item>
///   <item>
///     <c>local-projects</c>에 없는 레거시 ID라도 매핑이 실제 DB 프로젝트를 가리키면 그 DB 프로젝트의
///     레거시 ID로 등록한다(그 ID로 배정된 대화를 같은 프로젝트로 찾기 위함). 매핑 대상이 없으면 버린다.
///   </item>
/// </list>
/// <para>
/// 같은 canonical 루트가 서로 다른 프로젝트 둘 이상에 걸리는 것은 그대로 두고
/// <see cref="ProjectDirectory.FindByRoot"/>가 Ambiguous로 알려준다. 루트 실존은 <see cref="Directory.Exists(string?)"/>로만
/// 판정한다(읽기 전용). 이 클래스는 Codex 파일을 읽지 않는다 — 이미 읽은 값만 받는다.
/// </para>
/// </remarks>
public static class ProjectDirectoryBuilder
{
    /// <summary><see cref="ProjectDirectory"/>를 만든다.</summary>
    /// <param name="dbProjects"><see cref="ProjectTableReader.ReadProjects"/> 결과.</param>
    /// <param name="dbProjectRoots"><see cref="ProjectTableReader.ReadProjectRoots"/> 결과.</param>
    /// <param name="legacyProjects">global-state <c>local-projects</c>(<see cref="GlobalStateReader.ProjectGraph.LocalProjects"/>). 없으면 <c>null</c>.</param>
    /// <param name="legacyToDbProjectIds"><see cref="GlobalStateReader.ReadLegacyProjectIdMapping"/> 결과. 없으면 <c>null</c>.</param>
    /// <param name="conversationCountsByProjectId">
    /// 원시 프로젝트 ID(DB 또는 레거시) → 사용자 대화 수. 프로젝트별로 DB ID와 레거시 ID의 수를 합친다. 없으면 <c>null</c>(전부 0).
    /// </param>
    /// <param name="warnings">
    /// (Phase 9_1-11) 비정상 데이터(같은 Key/ID가 두 프로젝트에 걸림)로 제외한 항목이 있으면 여기에 경고를 남긴다
    /// (ID/경로/이름 원문 없음). <c>null</c>이면 경고를 버린다. 어느 경우에도 예외를 던지지 않는다.
    /// </param>
    public static ProjectDirectory Build(
        IReadOnlyList<ProjectTableReader.ProjectRow> dbProjects,
        IReadOnlyList<ProjectTableReader.ProjectRootRow> dbProjectRoots,
        IReadOnlyDictionary<string, LocalProjectInfo>? legacyProjects,
        IReadOnlyDictionary<string, string>? legacyToDbProjectIds,
        IReadOnlyDictionary<string, int>? conversationCountsByProjectId,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(dbProjects);
        ArgumentNullException.ThrowIfNull(dbProjectRoots);
        legacyProjects ??= new Dictionary<string, LocalProjectInfo>(StringComparer.Ordinal);
        legacyToDbProjectIds ??= new Dictionary<string, string>(StringComparer.Ordinal);
        conversationCountsByProjectId ??= new Dictionary<string, int>(StringComparer.Ordinal);

        var existsCache = new Dictionary<CanonicalPath, bool>(CanonicalPath.Comparer);

        // 1) DB 프로젝트.
        var dbDrafts = new List<Draft>();
        var dbById = new Dictionary<string, Draft>(StringComparer.Ordinal);
        foreach (ProjectTableReader.ProjectRow row in dbProjects)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || dbById.ContainsKey(row.Id))
            {
                continue;
            }

            var draft = new Draft(row.Id) { DbName = row.Name };
            dbById[row.Id] = draft;
            dbDrafts.Add(draft);
        }

        foreach (ProjectTableReader.ProjectRootRow root in dbProjectRoots)
        {
            if (dbById.TryGetValue(root.ProjectId, out Draft? draft))
            {
                draft.AddRoot(root.Path);
            }
        }

        // 2)~3) local-projects의 레거시 프로젝트.
        var legacyOnly = new List<Draft>();
        foreach (LocalProjectInfo legacy in legacyProjects.Values.OrderBy(p => p.ProjectId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(legacy.ProjectId))
            {
                continue;
            }

            Draft? target = null;

            // 레거시 ID 문자열이 DB ID와 같다면 같은 프로젝트다(서로 다른 프로젝트로 두면 ID가 두 곳에 걸린다).
            if (dbById.TryGetValue(legacy.ProjectId, out Draft? sameId))
            {
                target = sameId;
            }
            else if (legacyToDbProjectIds.TryGetValue(legacy.ProjectId, out string? mappedDbId) &&
                     dbById.TryGetValue(mappedDbId, out Draft? mapped))
            {
                target = mapped;
            }
            else
            {
                target = FindSingleDbProjectSharingRoot(legacy, dbDrafts);
            }

            if (target is not null)
            {
                target.MergeLegacy(legacy);
            }
            else
            {
                var draft = new Draft(dbProjectId: null);
                draft.MergeLegacy(legacy);
                legacyOnly.Add(draft);
            }
        }

        // 4) local-projects에 없는 레거시 ID의 매핑(실제 DB 프로젝트를 가리킬 때만).
        var knownLegacyIds = new HashSet<string>(
            dbDrafts.Concat(legacyOnly).SelectMany(d => d.LegacyIds), StringComparer.Ordinal);
        foreach ((string legacyId, string dbId) in legacyToDbProjectIds.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (knownLegacyIds.Contains(legacyId) || dbById.ContainsKey(legacyId))
            {
                continue;
            }

            if (dbById.TryGetValue(dbId, out Draft? draft))
            {
                draft.LegacyIds.Add(legacyId);
                knownLegacyIds.Add(legacyId);
            }
        }

        // Phase 9_1-11 — ProjectDirectory 생성자는 같은 Key/ID가 두 프로젝트에 걸리면 예외를 던진다. 비정상
        // state/global-state 데이터 때문에 카탈로그 전체가 실패하지 않도록, 먼저 온 항목을 남기고 충돌한 것만
        // 제외(또는 충돌한 레거시 ID만 떼어냄)한 뒤 경고를 남긴다.
        var projects = new List<KnownProject>(dbDrafts.Count + legacyOnly.Count);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        int excludedProjects = 0;
        int droppedLegacyIds = 0;
        foreach (Draft draft in dbDrafts.Concat(legacyOnly))
        {
            KnownProject project = draft.ToKnownProject(conversationCountsByProjectId, existsCache);

            if (usedKeys.Contains(project.Key) ||
                (project.DbProjectId is { } dbId && usedIds.Contains(dbId)))
            {
                excludedProjects++;
                continue;
            }

            List<string> conflictingLegacyIds = project.LegacyProjectIds.Where(usedIds.Contains).ToList();
            if (conflictingLegacyIds.Count > 0)
            {
                if (project.IsLegacyOnly && conflictingLegacyIds.Count == project.LegacyProjectIds.Count)
                {
                    excludedProjects++;
                    continue;
                }

                droppedLegacyIds += conflictingLegacyIds.Count;
                project = project with
                {
                    LegacyProjectIds = new HashSet<string>(project.LegacyProjectIds.Except(conflictingLegacyIds), StringComparer.Ordinal),
                };
            }

            usedKeys.Add(project.Key);
            if (project.DbProjectId is { } kept)
            {
                usedIds.Add(kept);
            }

            usedIds.UnionWith(project.LegacyProjectIds);
            projects.Add(project);
        }

        if (excludedProjects > 0)
        {
            warnings?.Add($"프로젝트 목록에서 ID가 다른 프로젝트와 겹치는 항목 {excludedProjects}개를 제외했습니다.");
        }

        if (droppedLegacyIds > 0)
        {
            warnings?.Add($"다른 프로젝트와 겹치는 이전 형식 프로젝트 ID {droppedLegacyIds}개를 무시했습니다.");
        }

        return new ProjectDirectory(projects);
    }

    private static Draft? FindSingleDbProjectSharingRoot(LocalProjectInfo legacy, List<Draft> dbDrafts)
    {
        var legacyRoots = new HashSet<CanonicalPath>(CanonicalPath.Comparer);
        foreach (string path in legacy.RootPaths)
        {
            if (CanonicalPath.TryCreate(path, out CanonicalPath? canonical, out _))
            {
                legacyRoots.Add(canonical!);
            }
        }

        if (legacyRoots.Count == 0)
        {
            return null;
        }

        Draft? found = null;
        foreach (Draft draft in dbDrafts)
        {
            if (draft.Roots.Any(r => legacyRoots.Contains(r.Canonical)))
            {
                if (found is not null)
                {
                    return null; // 둘 이상 — 자동으로 합치지 않는다.
                }

                found = draft;
            }
        }

        return found;
    }

    private sealed class Draft(string? dbProjectId)
    {
        public string? DbProjectId { get; } = dbProjectId;

        public string? DbName { get; init; }

        public List<string> LegacyIds { get; } = [];

        public List<string?> LegacyNames { get; } = [];

        public List<(string Display, CanonicalPath Canonical)> Roots { get; } = [];

        public void AddRoot(string path)
        {
            if (!CanonicalPath.TryCreate(path, out CanonicalPath? canonical, out _))
            {
                return;
            }

            if (!Roots.Any(r => r.Canonical.Equals(canonical)))
            {
                Roots.Add((path, canonical!));
            }
        }

        public void MergeLegacy(LocalProjectInfo legacy)
        {
            if (!LegacyIds.Contains(legacy.ProjectId, StringComparer.Ordinal))
            {
                LegacyIds.Add(legacy.ProjectId);
                LegacyNames.Add(legacy.DisplayName);
            }

            foreach (string path in legacy.RootPaths)
            {
                AddRoot(path);
            }
        }

        public KnownProject ToKnownProject(
            IReadOnlyDictionary<string, int> counts,
            Dictionary<CanonicalPath, bool> existsCache)
        {
            string key = DbProjectId ?? KnownProject.LegacyKeyPrefix + LegacyIds[0];
            string displayName =
                (!string.IsNullOrWhiteSpace(DbName) ? DbName : null)
                ?? LegacyNames.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? DbProjectId
                ?? LegacyIds[0]; // 이름을 알 수 없으면 ID 그대로(추측하지 않는다).

            int conversationCount = 0;
            if (DbProjectId is not null && counts.TryGetValue(DbProjectId, out int dbCount))
            {
                conversationCount += dbCount;
            }

            foreach (string legacyId in LegacyIds)
            {
                if (counts.TryGetValue(legacyId, out int legacyCount))
                {
                    conversationCount += legacyCount;
                }
            }

            var roots = new List<KnownProjectRoot>(Roots.Count);
            foreach ((string display, CanonicalPath canonical) in Roots)
            {
                if (!existsCache.TryGetValue(canonical, out bool exists))
                {
                    exists = Directory.Exists(display); // 읽기 전용 판정. 예외를 던지지 않는다.
                    existsCache[canonical] = exists;
                }

                roots.Add(new KnownProjectRoot(display, canonical, exists));
            }

            return new KnownProject(
                key,
                DbProjectId,
                new HashSet<string>(LegacyIds, StringComparer.Ordinal),
                displayName,
                roots,
                conversationCount);
        }
    }
}
