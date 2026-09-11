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
            return EmptyCatalog(files.Count, warnings, totalStopwatch, scanStopwatch);
        }

        string stateDbPath = Path.Combine(root, installation.ActiveStateDatabase.FileName);
        using ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(stateDbPath, out string? openError);
        if (database is null)
        {
            warnings.Add($"state DB를 읽기 전용으로 열 수 없습니다: {openError}");
            return EmptyCatalog(files.Count, warnings, totalStopwatch, scanStopwatch);
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
            Domain.Codex.Projects.ProjectAssignment project = CodexProjectResolver.Resolve(row, projectGraph, rootCandidates);
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

        List<ProjectEntry> projects = BuildProjectGroups(allConversations, rootCandidates, stateProjectNames, projectGraph);

        totalStopwatch.Stop();
        var stats = new CodexCatalogStats(files.Count, threadRows.Count, projects.Count, scanStopwatch.Elapsed, totalStopwatch.Elapsed);
        return new CodexCatalog(projects, allConversations, warnings, DateTimeOffset.UtcNow, stats);
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

    private static List<ProjectEntry> BuildProjectGroups(
        List<ConversationEntry> allConversations,
        List<CodexProjectResolver.RootCandidate> rootCandidates,
        Dictionary<string, string?> stateProjectNames,
        GlobalStateReader.ProjectGraph? projectGraph)
    {
        var byProject = new Dictionary<string, List<ConversationEntry>>(StringComparer.Ordinal);
        var uncategorized = new List<ConversationEntry>();

        foreach (ConversationEntry entry in allConversations)
        {
            if (!entry.IsUserConversation)
            {
                continue; // guardian_review/subagent는 기본 목록에 독립 항목으로 노출하지 않는다.
            }

            if (entry.Project.ProjectId is { } projectId)
            {
                if (!byProject.TryGetValue(projectId, out List<ConversationEntry>? list))
                {
                    list = [];
                    byProject[projectId] = list;
                }

                list.Add(entry);
            }
            else
            {
                uncategorized.Add(entry);
            }
        }

        var result = new List<ProjectEntry>();
        foreach ((string projectId, List<ConversationEntry> conversations) in byProject)
        {
            List<ConversationEntry> sorted = conversations
                .OrderBy(c => c.CreatedAtUtc ?? DateTimeOffset.MinValue)
                .ToList();

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
        List<string> warnings,
        Stopwatch totalStopwatch,
        Stopwatch scanStopwatch)
    {
        totalStopwatch.Stop();
        var stats = new CodexCatalogStats(rolloutFileCount, 0, 0, scanStopwatch.Elapsed, totalStopwatch.Elapsed);
        return new CodexCatalog([], [], warnings, DateTimeOffset.UtcNow, stats);
    }
}
