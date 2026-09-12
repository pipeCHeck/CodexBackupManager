using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore;

/// <summary>
/// write 전에 <c>state_&lt;N&gt;.sqlite</c>의 <c>threads</c> 테이블이 우리가 아는 구조와 호환되는지
/// 확인한다(Phase 7 — 요구사항 6, <c>docs/safe-restore-phase7.md</c> §1.H).
/// </summary>
/// <remarks>
/// <para>
/// <b>버전 숫자가 아니라 구조로 판정한다.</b> 공식 소스(<c>codex-rs/state/src/migrations.rs</c>)를
/// 조사한 결과 <c>PRAGMA user_version</c>은 전혀 쓰이지 않고, <c>_sqlx_migrations</c>의 최신
/// version은 "Codex 엔진 자신도 자기보다 앞선 DB를 허용"하도록 설계돼 있다(<c>ignore_missing:
/// true</c>) — 그래서 이 값만으로 "낮으면 구버전, 높으면 호환 안 됨"이라고 판단하지 않는다.
/// 대신 우리가 INSERT할 때 반드시 값을 채워야 하는(기본값이 없는 NOT NULL) 컬럼 9개가
/// <c>PRAGMA table_info(threads)</c>에 실제로 존재하는지만 확인한다 — 이 컬럼들이 있으면
/// (다른 컬럼이 늘어나 있어도, nullable이거나 기본값이 있는 한) INSERT는 안전하게 성공한다.
/// </para>
/// </remarks>
public static class SchemaCompatibilityChecker
{
    /// <summary>
    /// Phase 7이 New Import(INSERT)에서 반드시 값을 채워야 하는, 기본값 없는 NOT NULL 컬럼 9개
    /// (실측 확정, <c>docs/safe-restore-phase7.md</c> §1.A — 공식 소스
    /// <c>codex-rs/state/migrations/0001_threads.sql</c>과 정확히 일치함을 확인했다).
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredNotNullColumnsWithoutDefault =
    [
        "rollout_path", "created_at", "updated_at", "source", "model_provider",
        "cwd", "title", "sandbox_policy", "approval_mode",
    ];

    /// <summary>판정 결과.</summary>
    /// <param name="IsCompatible">Apply를 진행해도 되는지.</param>
    /// <param name="MissingColumns">없어서 문제인 컬럼 목록(호환되면 빈 목록).</param>
    /// <param name="MigrationVersion">참고용 — 실제 <c>_sqlx_migrations</c> 최신 version(읽을 수 있었으면).</param>
    public sealed record Result(bool IsCompatible, IReadOnlyList<string> MissingColumns, long? MigrationVersion);

    /// <summary>이미 열려 있는 read-only 연결로 확인한다.</summary>
    public static Result Check(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(threads)";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        List<string> missing = RequiredNotNullColumnsWithoutDefault
            .Where(column => !existingColumns.Contains(column))
            .ToList();

        long? migrationVersion = null;
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(version) FROM _sqlx_migrations";
            object? scalar = cmd.ExecuteScalar();
            if (scalar is long v)
            {
                migrationVersion = v;
            }
        }
        catch (SqliteException)
        {
            // _sqlx_migrations 자체가 없으면(예상치 못한 구조) 참고 정보 없이 진행한다 —
            // 위의 컬럼 존재 여부가 실제 판정 기준이다.
        }

        return new Result(missing.Count == 0, missing, migrationVersion);
    }

    /// <summary>
    /// 파일 경로로 read-only 연결을 열어 확인한다. 파일을 열 수 없으면(존재하지 않음 등)
    /// 호환되지 않는 것으로 취급한다.
    /// </summary>
    public static Result CheckFile(string stateDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDbPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = stateDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };

        try
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            return Check(connection);
        }
        catch (SqliteException ex)
        {
            return new Result(false, [$"state DB를 열 수 없습니다: {ex.Message}"], null);
        }
    }
}
