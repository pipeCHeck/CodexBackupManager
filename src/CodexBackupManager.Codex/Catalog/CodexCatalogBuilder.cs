using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Projects;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Sessions;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Codex.Titles;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Catalog;

/// <summary>
/// Phase 2의 진입점: "Codex Project → User Conversation 목록"을 만드는 오케스트레이터.
/// </summary>
/// <remarks>
/// 이 클래스는 순서만 엮는다. 실제 로직은 <see cref="RolloutFileLocator"/> /
/// <see cref="CodexSessionParser"/> / <see cref="ThreadChainResolver"/> / <see cref="ThreadRowReader"/> /
/// <see cref="SessionIndexReader"/> / <see cref="GlobalStateReader"/> / <see cref="CodexProjectResolver"/> /
/// <see cref="ThreadTitleResolver"/>에 있다. 거대한 Manager 클래스를 만들지 않기 위한 경계다.
/// (CLAUDE.md §5 — <see cref="CodexDetectionService"/>와 같은 패턴)
/// </remarks>
public static class CodexCatalogBuilder
{
    /// <summary>
    /// 카탈로그를 만든다. Phase 1의 <see cref="CodexInstallationInspector"/> 결과를 그대로 받는다.
    /// </summary>
    /// <param name="installation">탐지된 Codex 설치 정보(Home, 활성 state DB 등).</param>
    /// <param name="cancellationToken">취소 토큰. 파일 열거/스캔/DB 조회 중간에 확인한다.</param>
    public static CodexCatalog Build(CodexInstallationInfo installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var totalStopwatch = Stopwatch.StartNew();
        string root = installation.Home.Display;
        var warnings = new List<string>();

        // 1) rollout 파일 열거 — 내용은 아직 읽지 않는다.
        IReadOnlyList<RolloutFileReference> files = RolloutFileLocator.Locate(root, cancellationToken);

        // 2) 각 파일의 session_meta만 스트리밍으로 스캔한다("JSONL scan").
        var scanStopwatch = Stopwatch.StartNew();
        var metadataByFile = new Dictionary<RolloutFileReference, SessionMetadata?>();
        foreach (RolloutFileReference file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodexSessionParser.ParseResult parsed = CodexSessionParser.ParseSessionMetadata(file);
            metadataByFile[file] = parsed.Metadata;
            if (parsed.Warning is not null)
            {
                warnings.Add(parsed.Warning);
            }
        }

        scanStopwatch.Stop();

        // 3) 세그먼트/분기 체인 구성 — 같은 thread를 여러 개로 중복 표시하지 않기 위함.
        IReadOnlyDictionary<string, ThreadChain> chains = ThreadChainResolver.Resolve(files, metadataByFile);

        if (installation.ActiveStateDatabase is null)
        {
            warnings.Add("활성 state DB가 없어 대화 목록을 만들 수 없습니다.");
            return EmptyCatalog(files.Count, chains, warnings, totalStopwatch, scanStopwatch);
        }

        string stateDbPath = Path.Combine(root, installation.ActiveStateDatabase.FileName);
        using ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(stateDbPath, out string? openError);
        if (database is null)
        {
            warnings.Add($"state DB를 읽기 전용으로 열 수 없습니다: {openError}");
            return EmptyCatalog(files.Count, chains, warnings, totalStopwatch, scanStopwatch);
        }

        IReadOnlyList<ThreadRow> threadRows = ThreadRowReader.Read(database);
        IReadOnlyList<ProjectTableReader.ProjectRow> stateProjects = ProjectTableReader.ReadProjects(database);
        IReadOnlyList<ProjectTableReader.ProjectRootRow> stateProjectRoots = ProjectTableReader.ReadProjectRoots(database);

        IReadOnlyDictionary<string, string> sessionIndexTitles = SessionIndexReader.ReadTitleMap(
            Path.Combine(root, CodexHomeLayout.SessionIndexFileName));

        GlobalStateReader.ProjectGraph? projectGraph = GlobalStateReader.TryReadProjectGraph(
            Path.Combine(root, CodexHomeLayout.GlobalStateFileName), out string? graphError);
        if (graphError is not null)
        {
            warnings.Add($"글로벌 상태 읽기 경고: {graphError}");
        }

        var stateProjectNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ProjectTableReader.ProjectRow project in stateProjects)
        {
            stateProjectNames[project.Id] = project.Name;
        }

        List<CodexProjectResolver.RootCandidate> rootCandidates = BuildRootCandidates(stateProjectRoots, projectGraph);

        var allConversations = new List<ConversationEntry>(threadRows.Count);
        foreach (ThreadRow row in threadRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Domain.Codex.Titles.ThreadTitle title = ThreadTitleResolver.Resolve(row, sessionIndexTitles);
            Domain.Codex.Projects.ProjectAssignment project = CodexProjectResolver.Resolve(
                row, installation.ThreadAssignmentsMigrated, projectGraph, rootCandidates);
            chains.TryGetValue(row.Id, out ThreadChain? chain);

            allConversations.Add(new ConversationEntry
            {
                ThreadId = row.Id,
                Row = row,
                Title = title,
                Project = project,
                Chain = chain,
            });
        }

        // Phase 9_1-05 — 이 PC에 등록된 프로젝트 전체(대화 0개 포함)를 하나의 식별 규칙으로 합친다.
        // CodexProjectResolver의 판정(원시 ID)은 그대로 두고, 그룹을 만들 때만 KnownProject.Key로 정규화한다.
        IReadOnlyDictionary<string, string> legacyToDb = GlobalStateReader.ReadLegacyProjectIdMapping(
            Path.Combine(root, CodexHomeLayout.GlobalStateFileName), installation.Home, out string? mappingWarning);
        if (mappingWarning is not null)
        {
            warnings.Add($"글로벌 상태 읽기 경고: {mappingWarning}");
        }

        // Phase 9_1-11 — 비정상 데이터로 프로젝트 목록을 만들지 못해도 카탈로그 전체는 실패시키지 않는다.
        // 충돌 항목은 Builder가 제외하고 경고를 남긴다. 그래도 실패하면(예상 밖) 빈 목록 + 경고다.
        ProjectDirectory projectDirectory;
        try
        {
            projectDirectory = ProjectDirectoryBuilder.Build(
                stateProjects, stateProjectRoots, projectGraph?.LocalProjects, legacyToDb,
                CountUserConversationsByRawProjectId(allConversations), warnings);
        }
        catch (ArgumentException)
        {
            projectDirectory = ProjectDirectory.Empty;
            warnings.Add("프로젝트 목록을 만들 수 없어 프로젝트 연결 정보 없이 계속합니다.");
        }

        List<ProjectEntry> projects = BuildProjectGroups(allConversations, rootCandidates, stateProjectNames, projectGraph, projectDirectory);

        totalStopwatch.Stop();
        var stats = new CodexCatalogStats(files.Count, threadRows.Count, projects.Count, scanStopwatch.Elapsed, totalStopwatch.Elapsed);
        return new CodexCatalog(projects, allConversations, chains, warnings, DateTimeOffset.UtcNow, stats)
        {
            ProjectDirectory = projectDirectory,
        };
    }

    private static Dictionary<string, int> CountUserConversationsByRawProjectId(List<ConversationEntry> allConversations)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ConversationEntry entry in allConversations)
        {
            if (entry.IsUserConversation && entry.Project.ProjectId is { } projectId)
            {
                counts[projectId] = counts.TryGetValue(projectId, out int count) ? count + 1 : 1;
            }
        }

        return counts;
    }

    private static List<CodexProjectResolver.RootCandidate> BuildRootCandidates(
        IReadOnlyList<ProjectTableReader.ProjectRootRow> stateProjectRoots,
        GlobalStateReader.ProjectGraph? projectGraph)
    {
        var candidates = new List<CodexProjectResolver.RootCandidate>();

        foreach (ProjectTableReader.ProjectRootRow root in stateProjectRoots)
        {
            if (CanonicalPath.TryCreate(root.Path, out CanonicalPath? canonical, out _))
            {
                candidates.Add(new CodexProjectResolver.RootCandidate(root.ProjectId, canonical!));
            }
        }

        if (projectGraph is not null)
        {
            foreach (KeyValuePair<string, Domain.Codex.Projects.LocalProjectInfo> pair in projectGraph.LocalProjects)
            {
                foreach (string rootPath in pair.Value.RootPaths)
                {
                    if (CanonicalPath.TryCreate(rootPath, out CanonicalPath? canonical, out _))
                    {
                        candidates.Add(new CodexProjectResolver.RootCandidate(pair.Key, canonical!));
                    }
                }
            }
        }

        return candidates;
    }

    /// <remarks>
    /// Phase 9_1-05(결함 D) — 그룹 키는 원시 배정 ID가 아니라 <see cref="KnownProject.Key"/>다. 레거시 ID로
    /// 배정된 대화와 DB ID/cwd 폴백으로 배정된 대화가 같은 프로젝트면 한 그룹이 된다. 그룹의
    /// <see cref="ProjectEntry.ProjectId"/>는 <see cref="KnownProject.PrimaryId"/>(<c>DbProjectId ?? 레거시 ID</c>)이고,
    /// 이름/루트도 그 <see cref="KnownProject"/>에서 가져온다. 어떤 프로젝트 목록에도 없는 원시 ID
    /// (예: 삭제된 프로젝트를 가리키는 배정)는 예전처럼 그 ID 그대로 그룹을 만든다.
    /// </remarks>
    private static List<ProjectEntry> BuildProjectGroups(
        List<ConversationEntry> allConversations,
        List<CodexProjectResolver.RootCandidate> rootCandidates,
        Dictionary<string, string?> stateProjectNames,
        GlobalStateReader.ProjectGraph? projectGraph,
        ProjectDirectory projectDirectory)
    {
        var byProject = new Dictionary<string, List<ConversationEntry>>(StringComparer.Ordinal);
        var uncategorized = new List<ConversationEntry>();

        foreach (ConversationEntry entry in allConversations)
        {
            if (!entry.IsUserConversation)
            {
                continue; // guardian_review/subagent는 기본 목록에 독립 항목으로 노출하지 않는다.
            }

            if (entry.Project.ProjectId is { } rawProjectId)
            {
                string groupKey = projectDirectory.FindById(rawProjectId)?.Key ?? rawProjectId;
                if (!byProject.TryGetValue(groupKey, out List<ConversationEntry>? list))
                {
                    list = [];
                    byProject[groupKey] = list;
                }

                list.Add(entry);
            }
            else
            {
                uncategorized.Add(entry);
            }
        }

        var result = new List<ProjectEntry>();
        foreach ((string groupKey, List<ConversationEntry> conversations) in byProject)
        {
            List<ConversationEntry> sorted = conversations
                .OrderBy(c => c.CreatedAtUtc ?? DateTimeOffset.MinValue)
                .ToList();

            if (projectDirectory.FindByKey(groupKey) is { } known)
            {
                List<string> knownRoots = known.Roots.Select(r => r.DisplayPath).ToList();
                result.Add(new ProjectEntry(known.PrimaryId, known.DisplayName, knownRoots, sorted));
                continue;
            }

            // 프로젝트 목록 어디에도 없는 원시 ID — 예전 규칙 그대로.
            string projectId = groupKey;
            List<string> roots = rootCandidates
                .Where(c => string.Equals(c.ProjectId, projectId, StringComparison.Ordinal))
                .Select(c => c.Root.Display)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string displayName = DisplayNameFor(projectId, stateProjectNames, projectGraph);
            result.Add(new ProjectEntry(projectId, displayName, roots, sorted));
        }

        result = result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        if (uncategorized.Count > 0)
        {
            List<ConversationEntry> sorted = uncategorized
                .OrderBy(c => c.CreatedAtUtc ?? DateTimeOffset.MinValue)
                .ToList();
            result.Add(new ProjectEntry(null, "기타 대화", [], sorted));
        }

        return result;
    }

    private static string DisplayNameFor(
        string projectId,
        Dictionary<string, string?> stateProjectNames,
        GlobalStateReader.ProjectGraph? projectGraph)
    {
        if (stateProjectNames.TryGetValue(projectId, out string? stateName) && !string.IsNullOrWhiteSpace(stateName))
        {
            return stateName;
        }

        if (projectGraph is not null &&
            projectGraph.LocalProjects.TryGetValue(projectId, out Domain.Codex.Projects.LocalProjectInfo? info) &&
            !string.IsNullOrWhiteSpace(info.DisplayName))
        {
            return info.DisplayName;
        }

        return projectId; // 최후 수단: 이름을 알 수 없으면 ID 그대로 보여준다(추측하지 않는다).
    }

    private static CodexCatalog EmptyCatalog(
        int rolloutFileCount,
        IReadOnlyDictionary<string, ThreadChain> chains,
        List<string> warnings,
        Stopwatch totalStopwatch,
        Stopwatch scanStopwatch)
    {
        totalStopwatch.Stop();
        var stats = new CodexCatalogStats(rolloutFileCount, 0, 0, scanStopwatch.Elapsed, totalStopwatch.Elapsed);
        return new CodexCatalog([], [], chains, warnings, DateTimeOffset.UtcNow, stats);
    }
}
