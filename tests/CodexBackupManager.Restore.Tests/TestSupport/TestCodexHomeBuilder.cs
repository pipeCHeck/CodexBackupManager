using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
            cmd.CommandText = ProjectTablesSql + ThreadsTableSql;
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
        string? projectId = null,
        string? threadSource = null)
    {
        string dbPath = FindStateDbPath(codexHome);
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO threads (
                id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
                sandbox_policy, approval_mode, archived, project_id, thread_source
            ) VALUES (
                $id, $rollout_path, $created_at, $updated_at, 'vscode', 'openai', $cwd, 'test title',
                '{}', 'on-request', $archived, $project_id, $thread_source
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
        cmd.Parameters.AddWithValue("$thread_source", (object?)threadSource ?? DBNull.Value);
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

    /// <summary>
    /// Phase 9_2-1 — thread 행의 컬럼 하나를 직접 바꾼다(사후 검증 실패 경로 재현, 제외 대화의 로컬 변화 재현용).
    /// <paramref name="column"/>은 테스트 코드의 상수만 넘긴다.
    /// </summary>
    public static void UpdateThreadColumn(string codexHome, string threadId, string column, object? value)
    {
        using SqliteConnection connection = OpenReadWrite(codexHome);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE threads SET {column} = $value WHERE id = $id";
        cmd.Parameters.AddWithValue("$value", value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", threadId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Phase 9_1b — 등록 프로젝트 하나를 실측 스키마 그대로 INSERT한다(<c>projects</c> + <c>project_roots</c>).
    /// <c>position</c>은 기존 최대값 + 1, 루트 position은 0부터.
    /// </summary>
    public static void InsertProject(string codexHome, string projectId, string name, params string[] rootPaths)
    {
        using SqliteConnection connection = OpenReadWrite(codexHome);
        using SqliteTransaction transaction = connection.BeginTransaction();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO projects (id, name, metadata, position, created_at_ms, updated_at_ms)
                VALUES ($id, $name, '{}', (SELECT COALESCE(MAX(position), -1) + 1 FROM projects), $now, $now)
                """;
            cmd.Parameters.AddWithValue("$id", projectId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        for (int i = 0; i < rootPaths.Length; i++)
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO project_roots (project_id, position, path) VALUES ($id, $position, $path)";
            cmd.Parameters.AddWithValue("$id", projectId);
            cmd.Parameters.AddWithValue("$position", (long)i);
            cmd.Parameters.AddWithValue("$path", rootPaths[i]);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>등록 프로젝트를 지운다(<c>project_roots</c>는 ON DELETE CASCADE와 같은 효과가 나도록 직접 지운다).</summary>
    public static void DeleteProject(string codexHome, string projectId)
    {
        using SqliteConnection connection = OpenReadWrite(codexHome);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM project_roots WHERE project_id = $id; DELETE FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Desktop 레거시 프로젝트 하나(<c>local-projects</c> 항목).</summary>
    public sealed record LegacyProject(string Id, string Name, params string[] RootPaths);

    /// <summary>
    /// Phase 9_1b — 합성 <c>.codex-global-state.json</c>을 쓴다. 레거시 매핑은 현재 Home의 host key(<c>local:</c> + 경로)
    /// 아래에 들어간다. <c>threadAssignmentsMigrated</c>는 실측과 같이 <c>false</c>다.
    /// </summary>
    public static void WriteGlobalState(
        string codexHome,
        IReadOnlyList<LegacyProject> legacyProjects,
        IReadOnlyDictionary<string, string>? threadAssignments = null,
        IReadOnlyDictionary<string, string>? legacyToDbProjectIds = null)
    {
        var localProjects = new Dictionary<string, object>();
        foreach (LegacyProject project in legacyProjects)
        {
            localProjects[project.Id] = new { id = project.Id, name = project.Name, rootPaths = project.RootPaths };
        }

        var assignments = new Dictionary<string, object>();
        foreach (KeyValuePair<string, string> pair in threadAssignments ?? new Dictionary<string, string>())
        {
            assignments[pair.Key] = new { projectKind = "local", projectId = pair.Value };
        }

        string hostKey = "local:" + codexHome;
        var root = new Dictionary<string, object>
        {
            ["local-projects"] = localProjects,
            ["thread-project-assignments"] = assignments,
            ["app-server-projects-migration-by-host"] = new Dictionary<string, object>
            {
                [hostKey] = new { version = 1, projectsMigrated = true, threadAssignmentsMigrated = false },
            },
        };

        if (legacyToDbProjectIds is not null)
        {
            root["app-server-project-id-by-legacy-project-id-by-host"] = new Dictionary<string, object>
            {
                [hostKey] = legacyToDbProjectIds,
            };
        }

        File.WriteAllText(Path.Combine(codexHome, ".codex-global-state.json"), JsonSerializer.Serialize(root));
    }

    private static SqliteConnection OpenReadWrite(string codexHome)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = FindStateDbPath(codexHome), Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    public static string FindStateDbPath(string codexHome)
    {
        foreach (string file in Directory.EnumerateFiles(codexHome, "state_*.sqlite"))
        {
            return file;
        }

        throw new InvalidOperationException("state DB를 찾을 수 없습니다.");
    }

    /// <summary>실측 스키마(state_5.sqlite, 읽기 전용 확인)와 같은 프로젝트 테이블 3개.</summary>
    private const string ProjectTablesSql = """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            metadata TEXT NOT NULL DEFAULT '{}',
            position INTEGER NOT NULL,
            created_at_ms INTEGER NOT NULL,
            updated_at_ms INTEGER NOT NULL
        );
        CREATE TABLE project_roots (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            path TEXT NOT NULL,
            PRIMARY KEY (project_id, position)
        );
        CREATE TABLE project_idempotency_keys (
            key TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            created_at_ms INTEGER NOT NULL
        );

        """;

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
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL
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
