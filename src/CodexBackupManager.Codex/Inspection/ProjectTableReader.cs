using System;
using System.Collections.Generic;
using CodexBackupManager.Codex.Sqlite;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>state_*.sqlite</c>의 <c>projects</c> / <c>project_roots</c> 테이블을 읽는다.
/// </summary>
/// <remarks>
/// Phase 0 실측: 이 두 테이블은 채워져 있지만(45/52행) <c>threads.project_id</c>는 전부 NULL이었다
/// (마이그레이션 중간 상태). <see cref="CodexBackupManager.Codex.Projects.CodexProjectResolver"/>가
/// cwd 폴백 후보를 만들 때 이 테이블의 경로도 사용한다.
/// </remarks>
public static class ProjectTableReader
{
    /// <summary><c>id, name</c>.</summary>
    public sealed record ProjectRow(string Id, string? Name);

    /// <summary><c>project_id, path</c>.</summary>
    public sealed record ProjectRootRow(string ProjectId, string Path);

    /// <summary><c>projects</c> 테이블 전체. 테이블이 없으면 빈 목록.</summary>
    public static IReadOnlyList<ProjectRow> ReadProjects(ReadOnlyDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!database.TableExists("projects"))
        {
            return [];
        }

        IReadOnlySet<string> columns = database.GetColumnNames("projects");
        if (!columns.Contains("id"))
        {
            return [];
        }

        string select = columns.Contains("name") ? "id, name" : "id";
        var result = new List<ProjectRow>();
        foreach (object?[] row in database.ReadRows($"SELECT {select} FROM projects"))
        {
            if (row[0] is not string id)
            {
                continue;
            }

            result.Add(new ProjectRow(id, row.Length > 1 ? row[1] as string : null));
        }

        return result;
    }

    /// <summary><c>project_roots</c> 테이블 전체. 테이블이 없으면 빈 목록.</summary>
    public static IReadOnlyList<ProjectRootRow> ReadProjectRoots(ReadOnlyDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!database.TableExists("project_roots"))
        {
            return [];
        }

        IReadOnlySet<string> columns = database.GetColumnNames("project_roots");
        if (!columns.Contains("project_id") || !columns.Contains("path"))
        {
            return [];
        }

        var result = new List<ProjectRootRow>();
        foreach (object?[] row in database.ReadRows("SELECT project_id, path FROM project_roots"))
        {
            if (row[0] is string projectId && row[1] is string path)
            {
                result.Add(new ProjectRootRow(projectId, path));
            }
        }

        return result;
    }
}
