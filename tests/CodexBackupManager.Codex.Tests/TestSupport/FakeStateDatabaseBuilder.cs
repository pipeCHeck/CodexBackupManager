using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Codex.Tests.TestSupport;

/// <summary>
/// Phase 2 테스트용으로 임시 SQLite 파일에 <c>threads</c>/<c>projects</c>/<c>project_roots</c>/
/// <c>_sqlx_migrations</c> 테이블을 만든다.
/// </summary>
/// <remarks>
/// <b>실제 Codex 데이터를 복사하지 않는다</b>(CLAUDE.md §31). 여기서 만든 파일은 그 자체가
/// 테스트 대상(<see cref="Inspection.ThreadRowReader"/> 등)이 <b>읽기 전용</b>으로 여는 대상이며,
/// 이 빌더 자체는 쓰기 커넥션을 쓰지만 그건 테스트 픽스처를 만드는 것이지 Codex 원본을 건드리는 게 아니다.
/// </remarks>
public sealed class FakeStateDatabaseBuilder
{
    private readonly List<Dictionary<string, object?>> _threadRows = [];
    private readonly List<(string Id, string? Name)> _projects = [];
    private readonly List<(string ProjectId, string Path)> _projectRoots = [];

    /// <summary>thread 행 하나를 추가한다. 지정하지 않은 값은 <c>null</c>(단, <c>archived</c>는 0).</summary>
    public FakeStateDatabaseBuilder WithThread(
        string id,
        string? cwd = null,
        string? title = null,
        string? name = null,
        string? firstUserMessage = null,
        string? preview = null,
        string threadSource = "user",
        string? projectId = null,
        bool archived = false,
        long? createdAtMs = null,
        long? updatedAtMs = null,
        string? historyMode = "paginated",
        string? cliVersion = null,
        string? rolloutPath = null,
        string? sandboxPolicy = null,
        string? approvalMode = null,
        long? tokensUsed = null,
        bool? hasUserEvent = null,
        string? agentNickname = null,
        string? agentRole = null,
        string? agentPath = null,
        string? memoryMode = null,
        string? reasoningEffort = null,
        bool? isPinned = null,
        long? recencyAtMs = null,
        string? source = "vscode")
    {
        _threadRows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["rollout_path"] = rolloutPath,
            ["source"] = source,
            ["cwd"] = cwd,
            ["title"] = title,
            ["name"] = name,
            ["first_user_message"] = firstUserMessage,
            ["preview"] = preview,
            ["thread_source"] = threadSource,
            ["project_id"] = projectId,
            ["archived"] = archived ? 1L : 0L,
            ["archived_at"] = (long?)null,
            ["created_at_ms"] = createdAtMs,
            ["updated_at_ms"] = updatedAtMs,
            ["history_mode"] = historyMode,
            ["cli_version"] = cliVersion,
            ["model_provider"] = "openai",
            ["model"] = null,
            ["git_sha"] = null,
            ["git_branch"] = null,
            ["git_origin_url"] = null,
            ["sandbox_policy"] = sandboxPolicy,
            ["approval_mode"] = approvalMode,
            ["tokens_used"] = tokensUsed,
            ["has_user_event"] = hasUserEvent is { } hue ? (hue ? 1L : 0L) : null,
            ["agent_nickname"] = agentNickname,
            ["agent_role"] = agentRole,
            ["agent_path"] = agentPath,
            ["memory_mode"] = memoryMode,
            ["reasoning_effort"] = reasoningEffort,
            ["is_pinned"] = isPinned is { } pinned ? (pinned ? 1L : 0L) : null,
            ["recency_at_ms"] = recencyAtMs,
        });
        return this;
    }

    /// <summary><c>projects</c> 행 하나를 추가한다.</summary>
    public FakeStateDatabaseBuilder WithProject(string id, string? name)
    {
        _projects.Add((id, name));
        return this;
    }

    /// <summary><c>project_roots</c> 행 하나를 추가한다.</summary>
    public FakeStateDatabaseBuilder WithProjectRoot(string projectId, string path)
    {
        _projectRoots.Add((projectId, path));
        return this;
    }

    /// <summary>임시 파일에 SQLite DB를 만들고 경로를 돌려준다. 호출자가 정리해야 한다.</summary>
    public string BuildToTempFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cbm-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sqlite");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE threads (
                    id TEXT PRIMARY KEY, rollout_path TEXT, source TEXT, cwd TEXT, title TEXT, name TEXT,
                    first_user_message TEXT, preview TEXT, thread_source TEXT, project_id TEXT,
                    archived INTEGER, archived_at INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER,
                    history_mode TEXT, cli_version TEXT, model_provider TEXT, model TEXT,
                    git_sha TEXT, git_branch TEXT, git_origin_url TEXT,
                    sandbox_policy TEXT, approval_mode TEXT, tokens_used INTEGER, has_user_event INTEGER,
                    agent_nickname TEXT, agent_role TEXT, agent_path TEXT, memory_mode TEXT,
                    reasoning_effort TEXT, is_pinned INTEGER, thread_section_id TEXT,
                    section_position INTEGER, section_entered_at_ms INTEGER,
                    recency_at INTEGER, recency_at_ms INTEGER, created_at INTEGER, updated_at INTEGER
                );
                CREATE TABLE projects (id TEXT PRIMARY KEY, name TEXT);
                CREATE TABLE project_roots (project_id TEXT, position INTEGER, path TEXT);
                CREATE TABLE _sqlx_migrations (version INTEGER, description TEXT);
                """;
            create.ExecuteNonQuery();
        }

        foreach (Dictionary<string, object?> row in _threadRows)
        {
            using SqliteCommand insert = connection.CreateCommand();
            var columns = new List<string>(row.Keys);
            insert.CommandText =
                $"INSERT INTO threads ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.ConvertAll(c => "$" + c))})";
            foreach (string column in columns)
            {
                insert.Parameters.AddWithValue("$" + column, row[column] ?? DBNull.Value);
            }

            insert.ExecuteNonQuery();
        }

        foreach ((string id, string? name) in _projects)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO projects (id, name) VALUES ($id, $name)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        foreach ((string projectId, string rootPath) in _projectRoots)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO project_roots (project_id, path) VALUES ($projectId, $path)";
            insert.Parameters.AddWithValue("$projectId", projectId);
            insert.Parameters.AddWithValue("$path", rootPath);
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand migration = connection.CreateCommand())
        {
            migration.CommandText = "INSERT INTO _sqlx_migrations (version, description) VALUES (1, 'fixture')";
            migration.ExecuteNonQuery();
        }

        return path;
    }
}
