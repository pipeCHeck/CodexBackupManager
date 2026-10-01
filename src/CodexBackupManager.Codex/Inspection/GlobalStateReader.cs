using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>.codex-global-state.json</c>(Electron 데스크톱 앱 상태)에서 Phase 1에 필요한 값만 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// Phase 0의 핵심 발견을 그대로 반영한다.
/// </para>
/// <code>
/// "app-server-projects-migration-by-host": {
///   "local:C:\\Users\\User\\.codex": {
///     "version": 1,
///     "projectsMigrated": true,
///     "threadAssignmentsMigrated": false      ← 이 값이 프로젝트↔대화 매핑의 위치를 결정한다
///   }
/// }
/// </code>
/// <para>
/// <c>threadAssignmentsMigrated == false</c>이면 매핑은 아직 이 JSON 파일의
/// <c>thread-project-assignments</c>에 있고 <c>threads.project_id</c>는 전부 NULL이다.
/// Phase 2의 CodexProjectResolver가 어느 경로를 읽을지 이 플래그로 판단한다.
/// </para>
/// <para>
/// <b>이 클래스는 읽기만 한다.</b> 이 파일은 Codex 실행 중 Desktop 앱이 계속 덮어쓴다(Phase 0에서 <c>.bak</c> + 0바이트
/// <c>.tmp-*</c> 잔여물 11개를 확인). 이 앱의 쓰기는 Phase 9_5a부터 <c>CodexBackupManager.Restore.GlobalStateWriter</c> 한 곳에서,
/// <c>RestoreExecutor</c>(새 프로젝트를 만들 때)와 <c>SidebarRepairService</c>(사이드바 보정) 파이프라인 안에서만 한다 —
/// Codex 종료 확인, Snapshot, Journal, 게이트(<see cref="GlobalStateProjectGate"/>) 통과, 레거시 프로젝트 저장소 세 키만 추가,
/// 실패 시 Rollback. 다른 키(<c>thread-project-assignments</c> 등)는 쓰지 않는다.
/// </para>
/// </remarks>
public static class GlobalStateReader
{
    /// <summary>마이그레이션 플래그가 담긴 최상위 키.</summary>
    public const string ProjectsMigrationKey = "app-server-projects-migration-by-host";

    /// <summary>안전장치: 이 크기를 넘는 파일은 읽지 않는다. (실측 732 KB)</summary>
    public const long MaxFileSizeBytes = 32L * 1024 * 1024;

    /// <summary>읽어낸 마이그레이션 상태.</summary>
    /// <param name="HostKey">값을 읽어온 host key.</param>
    /// <param name="ProjectsMigrated"><c>projectsMigrated</c>.</param>
    /// <param name="ThreadAssignmentsMigrated"><c>threadAssignmentsMigrated</c>.</param>
    public sealed record ProjectsMigrationState(
        string HostKey,
        bool? ProjectsMigrated,
        bool? ThreadAssignmentsMigrated);

    /// <summary>
    /// 프로젝트 마이그레이션 플래그를 읽는다.
    /// </summary>
    /// <param name="globalStateFilePath"><c>.codex-global-state.json</c> 전체 경로.</param>
    /// <param name="codexHome">
    /// 현재 Codex Home. host key가 여러 개일 때 이 경로와 일치하는 것을 우선 선택한다.
    /// </param>
    /// <param name="error">실패 이유. 성공 시 <c>null</c>.</param>
    /// <returns>찾지 못하면 <c>null</c>.</returns>
    public static ProjectsMigrationState? TryReadProjectsMigration(
        string globalStateFilePath,
        CanonicalPath? codexHome,
        out string? error)
    {
        error = null;

        try
        {
            var file = new FileInfo(globalStateFilePath);
            if (!file.Exists)
            {
                error = $"{CodexHomeLayout.GlobalStateFileName} 파일이 없습니다.";
                return null;
            }

            if (file.Length > MaxFileSizeBytes)
            {
                error = "데스크톱 앱 상태 파일이 예상보다 너무 커서 읽지 않았습니다.";
                return null;
            }

            // FileShare.ReadWrite: Codex가 이 파일을 열고 있어도 읽을 수 있게 한다.
            using FileStream stream = new(
                globalStateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using JsonDocument document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(ProjectsMigrationKey, out JsonElement byHost) ||
                byHost.ValueKind != JsonValueKind.Object)
            {
                error = $"'{ProjectsMigrationKey}' 항목을 찾을 수 없습니다.";
                return null;
            }

            var candidates = new List<ProjectsMigrationState>();
            foreach (JsonProperty host in byHost.EnumerateObject())
            {
                if (host.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                candidates.Add(new ProjectsMigrationState(
                    host.Name,
                    ReadOptionalBool(host.Value, "projectsMigrated"),
                    ReadOptionalBool(host.Value, "threadAssignmentsMigrated")));
            }

            if (candidates.Count == 0)
            {
                error = $"'{ProjectsMigrationKey}'에 host 항목이 없습니다.";
                return null;
            }

            // host key는 "local:<codex home>" 형태다. 현재 Codex Home과 맞는 것을 우선.
            if (codexHome is not null)
            {
                foreach (ProjectsMigrationState candidate in candidates)
                {
                    if (HostKeyMatches(candidate.HostKey, codexHome))
                    {
                        return candidate;
                    }
                }
            }

            return candidates[0];
        }
        catch (JsonException ex)
        {
            error = $"데스크톱 앱 상태 파일을 JSON으로 해석할 수 없습니다: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"데스크톱 앱 상태 파일을 읽을 수 없습니다: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// <c>.codex-global-state.json</c>에서 프로젝트↔대화 그래프를 읽는다.
    /// docs/codex-storage-format.md §5 "(B) 현재 유효한 경로".
    /// </summary>
    /// <param name="localProjects"><c>id → 프로젝트 정보</c>.</param>
    /// <param name="threadProjectAssignments"><c>threadId → projectId</c>. <c>projectKind != "local"</c>은 무시한다.</param>
    /// <param name="projectlessThreadIds">프로젝트 미지정으로 명시된 thread ID 집합("기타 대화").</param>
    public sealed record ProjectGraph(
        IReadOnlyDictionary<string, LocalProjectInfo> LocalProjects,
        IReadOnlyDictionary<string, string> ThreadProjectAssignments,
        IReadOnlySet<string> ProjectlessThreadIds)
    {
        /// <summary>
        /// (Phase 9_3-00) <c>thread-project-assignments</c>의 키 전부(projectKind·값 형식과 무관). 이 앱이 옮기지 않을 대화를 가리는 데 쓴다.
        /// </summary>
        public IReadOnlySet<string> AllAssignedThreadIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 프로젝트↔대화 그래프를 읽는다. 실패하면 <c>null</c>.
    /// </summary>
    public static ProjectGraph? TryReadProjectGraph(string globalStateFilePath, out string? error)
    {
        error = null;

        try
        {
            var file = new FileInfo(globalStateFilePath);
            if (!file.Exists)
            {
                error = $"{CodexHomeLayout.GlobalStateFileName} 파일이 없습니다.";
                return null;
            }

            if (file.Length > MaxFileSizeBytes)
            {
                error = "데스크톱 앱 상태 파일이 예상보다 너무 커서 읽지 않았습니다.";
                return null;
            }

            using FileStream stream = new(globalStateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "데스크톱 앱 상태 파일의 최상위 구조가 예상과 다릅니다.";
                return null;
            }

            var localProjects = new Dictionary<string, LocalProjectInfo>(StringComparer.Ordinal);
            if (root.TryGetProperty("local-projects", out JsonElement lp) && lp.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in lp.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string id = ReadOptionalString(prop.Value, "id") ?? prop.Name;
                    string? name = ReadOptionalString(prop.Value, "name");
                    var roots = new List<string>();
                    if (prop.Value.TryGetProperty("rootPaths", out JsonElement rootPaths) &&
                        rootPaths.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in rootPaths.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } path)
                            {
                                roots.Add(path);
                            }
                        }
                    }

                    localProjects[id] = new LocalProjectInfo(id, name, roots);
                }
            }

            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            var allAssigned = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("thread-project-assignments", out JsonElement tpa) &&
                tpa.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in tpa.EnumerateObject())
                {
                    allAssigned.Add(prop.Name);
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? kind = ReadOptionalString(prop.Value, "projectKind");
                    string? projectId = ReadOptionalString(prop.Value, "projectId");
                    if (projectId is not null && (kind is null || string.Equals(kind, "local", StringComparison.Ordinal)))
                    {
                        assignments[prop.Name] = projectId;
                    }
                }
            }

            var projectless = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("projectless-thread-ids", out JsonElement pl) && pl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in pl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id)
                    {
                        projectless.Add(id);
                    }
                }
            }

            return new ProjectGraph(localProjects, assignments, projectless) { AllAssignedThreadIds = allAssigned };
        }
        catch (JsonException ex)
        {
            error = $"데스크톱 앱 상태 파일을 JSON으로 해석할 수 없습니다: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"데스크톱 앱 상태 파일을 읽을 수 없습니다: {ex.Message}";
            return null;
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 레거시 프로젝트 ID → SQLite <c>projects.id</c> 매핑이 담긴 최상위 키(Phase 9_1-02).
    /// Desktop이 프로젝트 migration 중에 host별로 기록한다.
    /// </summary>
    /// <remarks>
    /// <code>
    /// "app-server-project-id-by-legacy-project-id-by-host": {
    ///   "local:C:\\Users\\User\\.codex": { "&lt;legacyId&gt;": "&lt;dbProjectId&gt;", ... }
    /// }
    /// </code>
    /// </remarks>
    public const string LegacyProjectIdMappingKey = "app-server-project-id-by-legacy-project-id-by-host";

    private const string LocalHostKeyPrefix = "local:";

    /// <summary>
    /// 레거시 프로젝트 ID → DB 프로젝트 ID 매핑을 읽는다(Phase 9_1-02). 읽기 전용이다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// host key는 <c>"local:" + 경로</c>이고, 그 경로를 <paramref name="codexHome"/>과 canonical로 비교해 고른다.
    /// 다른 host key의 매핑은 쓰지 않는다 — 다른 Codex Home의 ID를 이 Home에 섞지 않기 위함이다.
    /// </para>
    /// <para>
    /// 파일이 없거나, 키가 없거나, 형식이 다르면 <b>예외 없이 빈 매핑</b>을 돌려주고 이유를
    /// <paramref name="warning"/>에 남긴다. 값이 문자열이 아닌 항목은 건너뛴다.
    /// 경고 문구에는 경로/ID 원문을 넣지 않는다.
    /// </para>
    /// </remarks>
    /// <param name="globalStateFilePath"><c>.codex-global-state.json</c> 전체 경로.</param>
    /// <param name="codexHome">현재 Codex Home.</param>
    /// <param name="warning">
    /// 형식 이상으로 빈 매핑을 돌려줬거나 일부 항목을 건너뛴 이유. 문제가 없거나 키/파일이 아예 없으면 <c>null</c>.
    /// </param>
    public static IReadOnlyDictionary<string, string> ReadLegacyProjectIdMapping(
        string globalStateFilePath,
        CanonicalPath codexHome,
        out string? warning)
    {
        ArgumentNullException.ThrowIfNull(codexHome);
        warning = null;
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var file = new FileInfo(globalStateFilePath);
            if (!file.Exists)
            {
                return empty; // global-state 자체가 없는 Home — 매핑 없음은 정상 상태다.
            }

            if (file.Length > MaxFileSizeBytes)
            {
                warning = "데스크톱 앱 상태 파일이 예상보다 너무 커서 프로젝트 ID 매핑을 읽지 않았습니다.";
                return empty;
            }

            using FileStream stream = new(globalStateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(LegacyProjectIdMappingKey, out JsonElement byHost))
            {
                return empty; // 매핑을 기록하지 않은 Desktop 버전 — 경고할 일이 아니다.
            }

            if (byHost.ValueKind != JsonValueKind.Object)
            {
                warning = $"'{LegacyProjectIdMappingKey}' 형식이 예상과 달라 프로젝트 ID 매핑을 쓰지 않습니다.";
                return empty;
            }

            JsonElement? hostMapping = null;
            foreach (JsonProperty host in byHost.EnumerateObject())
            {
                if (host.Name.StartsWith(LocalHostKeyPrefix, StringComparison.Ordinal) &&
                    HostKeyMatches(host.Name, codexHome))
                {
                    hostMapping = host.Value;
                    break;
                }
            }

            if (hostMapping is not { } mapping)
            {
                return empty; // 현재 Home에 대한 매핑이 없다. 다른 host key는 쓰지 않는다.
            }

            if (mapping.ValueKind != JsonValueKind.Object)
            {
                warning = "현재 Codex Home의 프로젝트 ID 매핑 형식이 예상과 달라 쓰지 않습니다.";
                return empty;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int skipped = 0;
            foreach (JsonProperty entry in mapping.EnumerateObject())
            {
                if (entry.Name.Length > 0 &&
                    entry.Value.ValueKind == JsonValueKind.String &&
                    entry.Value.GetString() is { Length: > 0 } dbProjectId)
                {
                    result[entry.Name] = dbProjectId;
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                warning = $"프로젝트 ID 매핑 중 형식이 다른 항목 {skipped}개를 건너뛰었습니다.";
            }

            return result;
        }
        catch (JsonException)
        {
            warning = "데스크톱 앱 상태 파일을 JSON으로 해석할 수 없어 프로젝트 ID 매핑을 쓰지 않습니다.";
            return empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = "데스크톱 앱 상태 파일을 읽을 수 없어 프로젝트 ID 매핑을 쓰지 않습니다.";
            return empty;
        }
    }

    internal static bool HostKeyMatches(string hostKey, CanonicalPath codexHome)
    {
        int separator = hostKey.IndexOf(':');
        if (separator < 0 || separator + 1 >= hostKey.Length)
        {
            return false;
        }

        // "local:C:\Users\..." → ':' 가 드라이브 문자에도 쓰이므로 첫 ':' 뒤 전체를 경로로 본다.
        string pathPart = hostKey[(separator + 1)..];
        return CanonicalPath.TryCreate(pathPart, out CanonicalPath? parsed, out _)
               && parsed!.Equals(codexHome);
    }

    private static bool? ReadOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
