using System;
using System.IO;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Domain.Codex.Projects;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// Phase 9_5-03 — 새 프로젝트 생성 스키마 게이트. 실측 스키마(state_5.sqlite, 2026-10-01 읽기 전용 재확인)와 정확히 같을 때만 허용한다.
/// </summary>
public sealed class ProjectCreationSchemaGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cbm-schema-gate-tests", Guid.NewGuid().ToString("N"));

    public ProjectCreationSchemaGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string Projects = """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            metadata TEXT NOT NULL DEFAULT '{}',
            position INTEGER NOT NULL,
            created_at_ms INTEGER NOT NULL,
            updated_at_ms INTEGER NOT NULL
        );
        """;

    private const string Roots = """
        CREATE TABLE project_roots (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            path TEXT NOT NULL,
            PRIMARY KEY (project_id, position)
        );
        """;

    private const string Keys = "CREATE TABLE project_idempotency_keys (key TEXT PRIMARY KEY, project_id TEXT NOT NULL, created_at_ms INTEGER NOT NULL);";

    private const string Threads = "CREATE TABLE threads (id TEXT PRIMARY KEY, cwd TEXT NOT NULL, project_id TEXT REFERENCES projects(id) ON DELETE SET NULL);";

    private ProjectCreationSchemaGate.Result Check(string schemaSql)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = schemaSql;
            cmd.ExecuteNonQuery();
        }

        using ReadOnlyDatabase database = ReadOnlySqlite.TryOpen(path, out string? error) ?? throw new InvalidOperationException(error);
        return ProjectCreationSchemaGate.Check(database);
    }

    [Fact]
    public void 실측_스키마와_같으면_허용한다()
    {
        ProjectCreationSchemaGate.Result result = Check(Projects + Roots + Keys + Threads);

        Assert.True(result.IsSupported, string.Join(" | ", result.Mismatches));
        Assert.Equal(ProjectCreationSupport.Supported, ProjectCreationSchemaGate.ToSupport(result));
    }

    [Theory]
    [InlineData("keys-missing")]
    [InlineData("keys-column-renamed")]
    [InlineData("projects-extra-column")]
    [InlineData("projects-name-nullable")]
    [InlineData("roots-type-changed")]
    [InlineData("roots-no-cascade")]
    [InlineData("roots-pk-changed")]
    [InlineData("threads-no-fk")]
    [InlineData("threads-no-project-id")]
    public void 한_곳이라도_다르면_생성을_허용하지_않는다(string variant)
    {
        string projects = Projects, roots = Roots, keys = Keys, threads = Threads;
        switch (variant)
        {
            case "keys-missing": keys = string.Empty; break;
            case "keys-column-renamed": keys = keys.Replace("created_at_ms", "created_ms", StringComparison.Ordinal); break;
            case "projects-extra-column": projects = projects.Replace("updated_at_ms INTEGER NOT NULL", "updated_at_ms INTEGER NOT NULL, color TEXT NOT NULL", StringComparison.Ordinal); break;
            case "projects-name-nullable": projects = projects.Replace("name TEXT NOT NULL", "name TEXT", StringComparison.Ordinal); break;
            case "roots-type-changed": roots = roots.Replace("path TEXT NOT NULL", "path BLOB NOT NULL", StringComparison.Ordinal); break;
            case "roots-no-cascade": roots = roots.Replace(" ON DELETE CASCADE", string.Empty, StringComparison.Ordinal); break;
            case "roots-pk-changed": roots = roots.Replace("PRIMARY KEY (project_id, position)", "PRIMARY KEY (project_id)", StringComparison.Ordinal); break;
            case "threads-no-fk": threads = "CREATE TABLE threads (id TEXT PRIMARY KEY, cwd TEXT NOT NULL, project_id TEXT);"; break;
            case "threads-no-project-id": threads = "CREATE TABLE threads (id TEXT PRIMARY KEY, cwd TEXT NOT NULL);"; break;
        }

        ProjectCreationSchemaGate.Result result = Check(projects + roots + keys + threads);

        Assert.False(result.IsSupported);
        Assert.NotEmpty(result.Mismatches);
        Assert.Equal(ProjectCreationSupport.SchemaUnsupported, ProjectCreationSchemaGate.ToSupport(result));
    }

    [Fact]
    public void 읽기_전용_연결은_foreign_key_list는_허용하고_foreign_keys_변경은_거부한다()
    {
        string path = Path.Combine(_dir, "guard.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = Projects + Roots;
            cmd.ExecuteNonQuery();
        }

        using ReadOnlyDatabase database = ReadOnlySqlite.TryOpen(path, out _)!;
        Assert.Single(database.ReadRows("PRAGMA foreign_key_list(project_roots)"));
        Assert.Throws<InvalidOperationException>(() => database.ReadRows("PRAGMA foreign_keys = OFF"));
        Assert.Throws<InvalidOperationException>(() => database.ReadRows("PRAGMA foreign_keys"));
    }
}
