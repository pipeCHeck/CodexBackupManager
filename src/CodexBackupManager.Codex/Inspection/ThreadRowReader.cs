using System;
using System.Collections.Generic;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>state_*.sqlite</c>의 <c>threads</c> 테이블 전체를 <see cref="ThreadRow"/>로 읽는다.
/// </summary>
/// <remarks>
/// 컬럼 구성이 버전마다 다르므로(docs/codex-storage-format.md §4), 쿼리를 조립하기 전에
/// 항상 실제 컬럼 목록을 확인하고 존재하는 컬럼만 선택한다. <c>id</c> 컬럼이 없으면 읽을 수 없다.
/// </remarks>
public static class ThreadRowReader
{
    /// <summary>대화 메타데이터 테이블 이름.</summary>
    public const string ThreadsTable = "threads";

    private static readonly string[] KnownColumns =
    [
        "id", "rollout_path", "cwd", "title", "name", "first_user_message", "preview",
        "thread_source", "project_id", "archived", "archived_at", "created_at_ms", "updated_at_ms",
        "history_mode", "cli_version", "model_provider", "model", "git_sha", "git_branch", "git_origin_url",
    ];

    /// <summary><c>threads</c> 테이블 전체를 읽는다. 테이블/<c>id</c> 컬럼이 없으면 빈 목록.</summary>
    public static IReadOnlyList<ThreadRow> Read(ReadOnlyDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!database.TableExists(ThreadsTable))
        {
            return [];
        }

        IReadOnlySet<string> existing = database.GetColumnNames(ThreadsTable);
        if (!existing.Contains("id"))
        {
            return [];
        }

        var selected = new List<string>();
        foreach (string column in KnownColumns)
        {
            if (existing.Contains(column))
            {
                selected.Add(column);
            }
        }

        string sql = $"SELECT {string.Join(", ", selected)} FROM {ThreadsTable}";
        IReadOnlyList<object?[]> rows = database.ReadRows(sql);

        var result = new List<ThreadRow>(rows.Count);
        foreach (object?[] row in rows)
        {
            var byColumn = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < selected.Count; i++)
            {
                byColumn[selected[i]] = row[i];
            }

            if (GetString(byColumn, "id") is not { } id)
            {
                continue; // id가 NULL인 행은 있을 수 없지만 방어적으로 건너뛴다.
            }

            result.Add(new ThreadRow
            {
                Id = id,
                RolloutPath = GetString(byColumn, "rollout_path"),
                Cwd = GetString(byColumn, "cwd"),
                Title = GetString(byColumn, "title"),
                Name = GetString(byColumn, "name"),
                FirstUserMessage = GetString(byColumn, "first_user_message"),
                Preview = GetString(byColumn, "preview"),
                ThreadSource = GetString(byColumn, "thread_source"),
                ProjectId = GetString(byColumn, "project_id"),
                Archived = GetInt64(byColumn, "archived") == 1,
                ArchivedAtSeconds = GetInt64(byColumn, "archived_at"),
                CreatedAtMs = GetInt64(byColumn, "created_at_ms"),
                UpdatedAtMs = GetInt64(byColumn, "updated_at_ms"),
                HistoryMode = GetString(byColumn, "history_mode"),
                CliVersion = GetString(byColumn, "cli_version"),
                ModelProvider = GetString(byColumn, "model_provider"),
                Model = GetString(byColumn, "model"),
                GitSha = GetString(byColumn, "git_sha"),
                GitBranch = GetString(byColumn, "git_branch"),
                GitOriginUrl = GetString(byColumn, "git_origin_url"),
            });
        }

        return result;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out object? value) ? value as string : null;

    private static long? GetInt64(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long l => l,
            int i => i,
            _ => null,
        };
    }
}
