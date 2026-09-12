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

    /// <summary>
    /// 이 클래스가 읽는 컬럼 이름 전체(존재하지 않는 버전에서는 생략된다). 실제 스키마와의 대조
    /// 테스트(<c>ThreadRowSchemaCoverageTests</c>)가 <c>InternalsVisibleTo</c>로 직접 확인한다.
    /// </summary>
    internal static readonly string[] KnownColumns =
    [
        "id", "rollout_path", "cwd", "title", "name", "first_user_message", "preview",
        "thread_source", "project_id", "archived", "archived_at", "created_at_ms", "updated_at_ms",
        "history_mode", "cli_version", "model_provider", "model", "git_sha", "git_branch", "git_origin_url",
        // Phase 5 Restore Sufficiency Audit(docs/codexbackup-format-v1.md §1)에서 추가.
        "created_at", "updated_at", "sandbox_policy", "approval_mode", "tokens_used", "has_user_event",
        "agent_nickname", "agent_role", "agent_path", "memory_mode", "reasoning_effort", "is_pinned",
        "thread_section_id", "section_position", "section_entered_at_ms", "recency_at", "recency_at_ms",
        // Phase 05_01 hardening — 실제 38컬럼과 KnownColumns를 기계적으로 대조해 "source" 누락을
        // 발견했다(tests/…/ThreadRowSchemaCoverageTests.cs). NOT NULL 컬럼인데도 빠져 있었다.
        "source",
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
                Source = GetString(byColumn, "source"),
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
                CreatedAtSeconds = GetInt64(byColumn, "created_at"),
                UpdatedAtSeconds = GetInt64(byColumn, "updated_at"),
                SandboxPolicy = GetString(byColumn, "sandbox_policy"),
                ApprovalMode = GetString(byColumn, "approval_mode"),
                TokensUsed = GetInt64(byColumn, "tokens_used"),
                HasUserEvent = GetBool(byColumn, "has_user_event"),
                AgentNickname = GetString(byColumn, "agent_nickname"),
                AgentRole = GetString(byColumn, "agent_role"),
                AgentPath = GetString(byColumn, "agent_path"),
                MemoryMode = GetString(byColumn, "memory_mode"),
                ReasoningEffort = GetString(byColumn, "reasoning_effort"),
                IsPinned = GetBool(byColumn, "is_pinned"),
                ThreadSectionId = GetString(byColumn, "thread_section_id"),
                SectionPosition = GetInt64(byColumn, "section_position"),
                SectionEnteredAtMs = GetInt64(byColumn, "section_entered_at_ms"),
                RecencyAtSeconds = GetInt64(byColumn, "recency_at"),
                RecencyAtMs = GetInt64(byColumn, "recency_at_ms"),
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

    private static bool? GetBool(IReadOnlyDictionary<string, object?> row, string column)
        => GetInt64(row, column) is { } value ? value != 0 : null;
}
