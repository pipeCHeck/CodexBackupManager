using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexBackupManager.Domain.Paths;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore.Undo;

/// <summary>
/// (Phase 9_4-01) <c>threads</c> 행 fingerprint. Codex Desktop이 켜지기만 해도 바꾸는 값은 넣지 않는다(9_0-A 실측):
/// <c>updated_at</c>·<c>updated_at_ms</c>(그리고 그로부터 파생되는 <c>recency_at*</c>)는 빼고, <c>cwd</c>·<c>rollout_path</c>는 원문이 아니라
/// canonical 경로로 넣는다(<c>\\?\</c> 형식으로 바뀌어도 같다).
/// </summary>
public static class ThreadRowFingerprint
{
    /// <summary>
    /// 넣는 컬럼(이 표에 있는 컬럼 중 실제 테이블에 있는 것만). 사이드바 UI 상태(<c>is_pinned</c>·섹션)·파생 미리보기(<c>preview</c>)·
    /// 모드(<c>memory_mode</c>·<c>history_mode</c>)·<c>updated_at*</c>·<c>recency_at*</c>는 넣지 않는다.
    /// </summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "id", "rollout_path", "created_at", "created_at_ms", "source", "model_provider", "cwd", "title", "name",
        "sandbox_policy", "approval_mode", "tokens_used", "has_user_event", "archived", "archived_at",
        "git_sha", "git_branch", "git_origin_url", "cli_version", "first_user_message", "model", "reasoning_effort",
        "thread_source", "project_id",
    ];

    /// <summary>canonical로 비교할 경로 컬럼.</summary>
    public static readonly IReadOnlySet<string> PathColumns = new HashSet<string>(StringComparer.Ordinal) { "cwd", "rollout_path" };

    /// <summary>지금 테이블에 있는 fingerprint 컬럼.</summary>
    public static IReadOnlyList<string> AvailableColumns(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "PRAGMA table_info(threads)";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            existing.Add(reader.GetString(1));
        }

        return Columns.Where(existing.Contains).ToList();
    }

    /// <summary>행의 지정 컬럼 값을 읽는다. 행이 없으면 <c>null</c>.</summary>
    public static IReadOnlyList<UndoColumnValue>? ReadValues(
        SqliteConnection connection, string threadId, IReadOnlyList<string> columns, SqliteTransaction? transaction = null)
    {
        foreach (string column in columns)
        {
            if (!column.All(c => char.IsAsciiLetterLower(c) || c == '_'))
            {
                throw new ArgumentException("컬럼 이름이 올바르지 않습니다.", nameof(columns));
            }
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT {string.Join(", ", columns)} FROM threads WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", threadId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var values = new List<UndoColumnValue>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            values.Add(reader.IsDBNull(i)
                ? new UndoColumnValue(columns[i], null, null)
                : reader.GetValue(i) is long or int or short or byte or bool
                    ? new UndoColumnValue(columns[i], null, Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture))
                    : new UndoColumnValue(columns[i], Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture), null));
        }

        return values;
    }

    /// <summary>값 목록의 fingerprint(경로 컬럼은 canonical, 순서는 주어진 대로).</summary>
    public static string Compute(IReadOnlyList<UndoColumnValue> values)
    {
        var builder = new StringBuilder();
        foreach (UndoColumnValue value in values)
        {
            builder.Append(value.Column).Append('\u0001');
            if (value.Integer is { } integer)
            {
                builder.Append('i').Append(integer.ToString(CultureInfo.InvariantCulture));
            }
            else if (value.Text is { } text)
            {
                builder.Append('s').Append(PathColumns.Contains(value.Column) ? CanonicalOrRaw(text) : text);
            }
            else
            {
                builder.Append('n');
            }

            builder.Append('\u0002');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>경로를 canonical 비교 값으로(정규화할 수 없으면 원문).</summary>
    public static string CanonicalOrRaw(string path)
        => CanonicalPath.TryCreate(path, out CanonicalPath? canonical, out _) ? "c:" + canonical!.Value : "r:" + path;
}
