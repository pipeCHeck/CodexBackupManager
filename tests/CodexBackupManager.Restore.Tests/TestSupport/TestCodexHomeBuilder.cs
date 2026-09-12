using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore.Tests.TestSupport;

/// <summary>
/// 실제 스키마(<c>docs/safe-restore-phase7.md</c> §1.A — 실측+공식 소스로 확정)를 그대로 반영한
/// 합성 Codex Home을 처음부터 만든다. 실제 사용자 <c>.codex</c>는 전혀 건드리지 않는다.
/// </summary>
public static class TestCodexHomeBuilder
{
    /// <summary>빈 Codex Home을 만든다(state DB + 필수 파일들, thread 0개).</summary>
    public static string CreateEmpty(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "archived_sessions"));

        string dbPath = Path.Combine(root, "state_5.sqlite");
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = ThreadsTableSql;
            cmd.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(root, "config.toml"), "# test fixture config\n");
        File.WriteAllText(Path.Combine(root, "session_index.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(root, ".codex-global-state.json"), "{}");

        return root;
    }

    /// <summary>실제와 같은 규칙(YYYY/MM/DD 또는 archived_sessions)으로 rollout 파일을 만든다.</summary>
    public static string WriteRolloutFile(string codexHome, string fileName, string content, bool archived, DateTimeOffset timestamp)
    {
        string dir = archived
            ? Path.Combine(codexHome, "archived_sessions")
            : Path.Combine(codexHome, "sessions", timestamp.Year.ToString("D4"), timestamp.Month.ToString("D2"), timestamp.Day.ToString("D2"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>이미 로컬에 존재하는 thread 하나를 직접 INSERT한다(Identical/LocalAhead/IncomingAhead 시나리오의 "기존 상태").</summary>
    public static void InsertExistingThread(
        string codexHome,
        string threadId,
        string rolloutPathAbsolute,
        string cwd,
        bool archived = false,
        string? projectId = null)
    {
        string dbPath = FindStateDbPath(codexHome);
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO threads (
                id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
                sandbox_policy, approval_mode, archived, project_id
            ) VALUES (
                $id, $rollout_path, $created_at, $updated_at, 'vscode', 'openai', $cwd, 'test title',
                '{}', 'on-request', $archived, $project_id
            )
            """;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("$id", threadId);
        cmd.Parameters.AddWithValue("$rollout_path", rolloutPathAbsolute);
        cmd.Parameters.AddWithValue("$created_at", now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        cmd.Parameters.AddWithValue("$cwd", cwd);
        cmd.Parameters.AddWithValue("$archived", archived ? 1L : 0L);
        cmd.Parameters.AddWithValue("$project_id", (object?)projectId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>지금 <c>threads</c> 테이블에 있는 thread ID 전체(진단/검증용).</summary>
    public static IReadOnlyList<string> ReadThreadIds(string codexHome)
    {
        string dbPath = FindStateDbPath(codexHome);
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM threads";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>단일 컬럼 값을 읽는다(진단/검증용).</summary>
    public static object? ReadThreadColumn(string codexHome, string threadId, string column)
    {
        string dbPath = FindStateDbPath(codexHome);
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM threads WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", threadId);
        return cmd.ExecuteScalar();
    }

    public static string FindStateDbPath(string codexHome)
    {
        foreach (string file in Directory.EnumerateFiles(codexHome, "state_*.sqlite"))
        {
            return file;
        }

        throw new InvalidOperationException("state DB를 찾을 수 없습니다.");
    }

    private const string ThreadsTableSql = """
        CREATE TABLE threads (
            id TEXT PRIMARY KEY,
            rollout_path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            source TEXT NOT NULL,
            model_provider TEXT NOT NULL,
            cwd TEXT NOT NULL,
            title TEXT NOT NULL,
            sandbox_policy TEXT NOT NULL,
            approval_mode TEXT NOT NULL,
            tokens_used INTEGER NOT NULL DEFAULT 0,
            has_user_event INTEGER NOT NULL DEFAULT 0,
            archived INTEGER NOT NULL DEFAULT 0,
            archived_at INTEGER,
            git_sha TEXT,
            git_branch TEXT,
            git_origin_url TEXT,
            cli_version TEXT NOT NULL DEFAULT '',
            first_user_message TEXT NOT NULL DEFAULT '',
            agent_nickname TEXT,
            agent_role TEXT,
            memory_mode TEXT NOT NULL DEFAULT 'enabled',
            model TEXT,
            reasoning_effort TEXT,
            agent_path TEXT,
            created_at_ms INTEGER,
            updated_at_ms INTEGER,
            thread_source TEXT,
            preview TEXT NOT NULL DEFAULT '',
            recency_at INTEGER NOT NULL DEFAULT 0,
            recency_at_ms INTEGER NOT NULL DEFAULT 0,
            history_mode TEXT NOT NULL DEFAULT 'legacy',
            name TEXT,
            is_pinned INTEGER NOT NULL DEFAULT 0,
            thread_section_id TEXT,
            section_position INTEGER,
            section_entered_at_ms INTEGER,
            project_id TEXT
        );
        CREATE TRIGGER threads_created_at_ms_after_insert
        AFTER INSERT ON threads
        WHEN NEW.created_at_ms IS NULL
        BEGIN
            UPDATE threads SET created_at_ms = NEW.created_at * 1000 WHERE id = NEW.id;
        END;
        CREATE TRIGGER threads_updated_at_ms_after_insert
        AFTER INSERT ON threads
        WHEN NEW.updated_at_ms IS NULL
        BEGIN
            UPDATE threads SET updated_at_ms = NEW.updated_at * 1000 WHERE id = NEW.id;
        END;
        CREATE TRIGGER threads_recency_at_after_insert
        AFTER INSERT ON threads
        WHEN NEW.recency_at_ms = 0
        BEGIN
            UPDATE threads SET recency_at = NEW.updated_at, recency_at_ms = COALESCE(NEW.updated_at_ms, NEW.updated_at * 1000) WHERE id = NEW.id;
        END;
        """;
}
