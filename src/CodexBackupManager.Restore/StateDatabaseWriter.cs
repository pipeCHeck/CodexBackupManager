using System;
using System.Collections.Generic;
using System.Text;
using CodexBackupManager.Backup.Manifest;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore;

/// <summary>
/// <c>state_&lt;N&gt;.sqlite</c>의 <c>threads</c> 테이블에 New/IncomingAhead 연산을 실제로 쓴다
/// (Phase 7 요구사항 14). 호출자가 이미 열어 둔 트랜잭션 안에서만 동작한다 — 이 클래스는 연결을
/// 열거나 커밋/롤백하지 않는다(<see cref="RestoreExecutor"/>가 소유한다).
/// </summary>
/// <remarks>
/// 기본값이 있는 컬럼(<c>tokens_used</c>/<c>has_user_event</c>/<c>cli_version</c>/
/// <c>first_user_message</c>/<c>memory_mode</c>/<c>preview</c>/<c>history_mode</c>/<c>is_pinned</c>)은
/// 원본 metadata에 값이 있을 때만 컬럼 목록에 포함한다 — 값이 없으면 컬럼 자체를 뺘서 SQLite
/// 스키마의 기본값이 적용되게 둔다(<c>NULL</c>을 명시적으로 넣으면 <c>NOT NULL</c> 제약을
/// 위반한다). <c>recency_at</c>/<c>recency_at_ms</c>는 항상 뺀다 — <c>threads_recency_at_after_insert</c>
/// 트리거가 <c>updated_at</c>에서 파생시키도록 맡긴다(원본 값을 그대로 옮기지 않는다, Phase 7 감사
/// 결론). <c>created_at_ms</c>/<c>updated_at_ms</c>는 원본에 있으면 그대로 쓰고, 없으면 역시
/// 트리거가 초 단위 값에서 채우게 둔다.
/// </remarks>
public static class StateDatabaseWriter
{
    /// <summary>New thread 하나를 INSERT한다.</summary>
    public static void InsertThread(SqliteConnection connection, SqliteTransaction transaction, PlannedThreadInsert insert)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(insert);

        // 스키마 세대마다 선택적 컬럼(예: agent_nickname/is_pinned/recency_at 등)의 존재 여부가
        // 다를 수 있다(docs/safe-restore-phase7.md §1.H) — 값이 있어도 그 컬럼이 실제로 없으면
        // INSERT 자체가 실패하므로, 지금 이 DB에 실제로 있는 컬럼만 골라 쓴다. 필수 9개 컬럼은
        // SchemaCompatibilityChecker가 이미 존재를 보장했다.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA table_info(threads)";
            using SqliteDataReader r = pragma.ExecuteReader();
            while (r.Read())
            {
                existingColumns.Add(r.GetString(1));
            }
        }

        BackupConversationMetadata m = insert.Source;
        var columns = new List<string>();
        var values = new List<(string Name, object Value)>();

        void Required(string column, object? value)
        {
            if (value is null)
            {
                throw new InvalidOperationException($"필수 컬럼 '{column}'에 넣을 원본 값이 없습니다(New Import 전에 검증됐어야 합니다).");
            }

            if (!existingColumns.Contains(column))
            {
                throw new InvalidOperationException($"필수 컬럼 '{column}'이 이 state DB 스키마에 없습니다(호환성 게이트가 통과했어야 합니다).");
            }

            columns.Add(column);
            values.Add(("$" + column, value));
        }

        void Optional(string column, object? value)
        {
            if (value is null || !existingColumns.Contains(column))
            {
                return;
            }

            columns.Add(column);
            values.Add(("$" + column, value));
        }

        Required("id", m.ThreadId);
        Required("rollout_path", insert.ResolvedRolloutPathAbsolute);
        Required("created_at", m.CreatedAtSeconds);
        Required("updated_at", m.UpdatedAtSeconds);
        Required("source", m.Source);
        Required("model_provider", m.ModelProvider);
        Required("cwd", m.OriginalCwd);
        Required("title", m.Title);
        Required("sandbox_policy", m.SandboxPolicyRaw);
        Required("approval_mode", m.ApprovalMode);

        Optional("tokens_used", m.TokensUsed);
        Optional("has_user_event", m.HasUserEvent is null ? null : (m.HasUserEvent.Value ? 1L : 0L));
        Required("archived", m.Archived ? 1L : 0L);
        Optional("archived_at", m.ArchivedAtSeconds);
        Optional("git_sha", m.GitSha);
        Optional("git_branch", m.GitBranch);
        Optional("git_origin_url", m.GitOriginUrl);
        Optional("cli_version", m.CliVersion);
        Optional("first_user_message", m.FirstUserMessage);
        Optional("agent_nickname", m.AgentNickname);
        Optional("agent_role", m.AgentRole);
        Optional("memory_mode", m.MemoryMode);
        Optional("model", m.Model);
        Optional("reasoning_effort", m.ReasoningEffort);
        Optional("agent_path", m.AgentPath);
        Optional("created_at_ms", m.CreatedAtMs);
        Optional("updated_at_ms", m.UpdatedAtMs);
        Optional("thread_source", m.ThreadSource);
        Optional("preview", m.Preview);
        Optional("history_mode", m.HistoryMode);
        Optional("name", m.Name);
        Optional("is_pinned", m.IsPinned is null ? null : (m.IsPinned.Value ? 1L : 0L));
        Optional("thread_section_id", m.ThreadSectionId);
        Optional("section_position", m.SectionPosition);
        Optional("section_entered_at_ms", m.SectionEnteredAtMs);
        Optional("project_id", insert.ResolvedProjectId);

        var sql = new StringBuilder("INSERT INTO threads (");
        sql.Append(string.Join(", ", columns));
        sql.Append(") VALUES (");
        sql.Append(string.Join(", ", values.ConvertAll(v => v.Name)));
        sql.Append(')');

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql.ToString();
        foreach ((string name, object value) in values)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>IncomingAhead가 새 segment를 만들어 leaf 파일이 바뀐 기존 thread의 <c>rollout_path</c>만 갱신한다.</summary>
    public static void UpdateRolloutPath(SqliteConnection connection, SqliteTransaction transaction, PlannedThreadRolloutPathUpdate update)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(update);

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE threads SET rollout_path = $rollout_path WHERE id = $id";
        cmd.Parameters.AddWithValue("$rollout_path", update.NewRolloutPathAbsolute);
        cmd.Parameters.AddWithValue("$id", update.ThreadId);
        int affected = cmd.ExecuteNonQuery();
        if (affected != 1)
        {
            throw new InvalidOperationException($"rollout_path 갱신 대상 thread를 찾지 못했습니다(영향받은 행: {affected}).");
        }
    }

    /// <summary>
    /// IncomingAhead의 "안전한" metadata만 갱신한다(Phase 07_01 요구사항 8) —
    /// <see cref="RestoreOperationPlanner"/>가 이미 target-PC 고유 값(cwd/project_id/사이드바 배치
    /// 등)을 걸러내고 넘긴 필드만 여기서 SQL로 옮긴다. 값이 <c>null</c>인 필드는 컬럼 목록에서
    /// 아예 빼서 건드리지 않는다(그 필드는 "이번에 갱신할 것 없음"이라는 뜻).
    /// </summary>
    public static void UpdateMetadata(SqliteConnection connection, SqliteTransaction transaction, PlannedThreadMetadataUpdate update)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(update);

        var setClauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        void Set(string column, object? value)
        {
            if (value is null)
            {
                return;
            }

            setClauses.Add($"{column} = ${column}");
            parameters.Add(("$" + column, value));
        }

        Set("updated_at", update.UpdatedAtSeconds);
        Set("updated_at_ms", update.UpdatedAtMs);
        Set("tokens_used", update.TokensUsed);
        Set("has_user_event", update.HasUserEvent is null ? null : (update.HasUserEvent.Value ? 1L : 0L));
        Set("name", update.NameIfLocalMissing);
        Set("model", update.ModelIfLocalMissing);
        Set("cli_version", update.CliVersionIfLocalMissing);

        if (setClauses.Count == 0)
        {
            // RestoreOperationPlanner는 최소 하나의 필드가 실제로 바뀔 때만 이 연산을 만들지만,
            // 방어적으로 한 번 더 확인한다 — 빈 UPDATE 문을 만들지 않는다.
            return;
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"UPDATE threads SET {string.Join(", ", setClauses)} WHERE id = $id";
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.Parameters.AddWithValue("$id", update.ThreadId);
        int affected = cmd.ExecuteNonQuery();
        if (affected != 1)
        {
            throw new InvalidOperationException($"metadata 갱신 대상 thread를 찾지 못했습니다(영향받은 행: {affected}).");
        }
    }
}
