using System;
using System.Collections.Generic;
using System.Globalization;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Domain.Codex;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>state_*.sqlite</c>에서 Phase 1에 필요한 메타데이터만 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// Phase 0에서 확인한 사실을 반영한다.
/// </para>
/// <list type="bullet">
///   <item><c>_sqlx_migrations</c>: 52 rows. 최신 version이 스키마 세대를 말해준다.</item>
///   <item><c>threads</c>: 352 rows, 38 컬럼. <c>cli_version</c>이 Codex CLI 버전의 출처.</item>
///   <item>
///     컬럼 구성이 버전마다 달라진다(<c>project_id</c>, <c>history_mode</c>, <c>preview</c> 등은 후기 추가).
///     따라서 <b>쿼리를 만들기 전에 항상 실제 컬럼 목록을 확인</b>한다.
///   </item>
/// </list>
/// <para>
/// <b>대화 내용/제목/미리보기는 읽지 않는다.</b> Phase 1에서 필요한 것은 개수와 버전 문자열뿐이다.
/// </para>
/// </remarks>
public static class StateDbReader
{
    /// <summary>마이그레이션 이력 테이블 이름.</summary>
    public const string MigrationsTable = "_sqlx_migrations";

    /// <summary>대화 메타데이터 테이블 이름.</summary>
    public const string ThreadsTable = "threads";

    /// <summary>state DB 조사 결과.</summary>
    public sealed record StateDbSnapshot
    {
        /// <summary>사용된 열기 방식.</summary>
        public required SqliteOpenMode OpenMode { get; init; }

        /// <summary>오류 메시지. 없으면 <c>null</c>.</summary>
        public string? Error { get; init; }

        /// <summary><c>_sqlx_migrations</c>의 최신 version.</summary>
        public long? LatestMigrationVersion { get; init; }

        /// <summary>최신 migration의 description.</summary>
        public string? LatestMigrationDescription { get; init; }

        /// <summary><c>threads</c> 전체 행 수.</summary>
        public long? ThreadRowCount { get; init; }

        /// <summary><c>threads.archived = 1</c> 행 수.</summary>
        public long? ArchivedThreadRowCount { get; init; }

        /// <summary>가장 최근에 갱신된 thread의 <c>cli_version</c>.</summary>
        public string? LatestCliVersion { get; init; }
    }

    /// <summary>state DB를 열어 메타데이터를 읽는다. 연결은 즉시 닫힌다.</summary>
    public static StateDbSnapshot Read(string stateDatabaseFilePath)
    {
        using ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(stateDatabaseFilePath, out string? openError);
        if (database is null)
        {
            return new StateDbSnapshot { OpenMode = SqliteOpenMode.Failed, Error = openError };
        }

        long? migrationVersion = null;
        string? migrationDescription = null;
        long? threadCount = null;
        long? archivedCount = null;
        string? cliVersion = null;
        var problems = new List<string>();

        try
        {
            if (database.TableExists(MigrationsTable))
            {
                IReadOnlySet<string> columns = database.GetColumnNames(MigrationsTable);
                if (columns.Contains("version"))
                {
                    object? maxVersion = database.ReadScalar(
                        $"SELECT MAX(version) FROM {MigrationsTable}");
                    migrationVersion = ToNullableInt64(maxVersion);

                    if (migrationVersion.HasValue && columns.Contains("description"))
                    {
                        object?[]? row = database.ReadSingleRow(
                            $"SELECT description FROM {MigrationsTable} " +
                            $"WHERE version = {migrationVersion.Value.ToString(CultureInfo.InvariantCulture)} LIMIT 1");
                        migrationDescription = row is { Length: > 0 } ? row[0] as string : null;
                    }
                }
                else
                {
                    problems.Add($"{MigrationsTable}에 version 컬럼이 없습니다.");
                }
            }
            else
            {
                problems.Add($"{MigrationsTable} 테이블이 없습니다.");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"마이그레이션 정보를 읽는 중 오류: {ex.Message}");
        }

        try
        {
            if (database.TableExists(ThreadsTable))
            {
                IReadOnlySet<string> columns = database.GetColumnNames(ThreadsTable);

                threadCount = ToNullableInt64(database.ReadScalar($"SELECT COUNT(*) FROM {ThreadsTable}"));

                if (columns.Contains("archived"))
                {
                    archivedCount = ToNullableInt64(database.ReadScalar(
                        $"SELECT COUNT(*) FROM {ThreadsTable} WHERE archived = 1"));
                }

                if (columns.Contains("cli_version"))
                {
                    // 최신성 기준 컬럼을 실제 존재하는 것 중에서 고른다.
                    string orderBy = columns.Contains("updated_at_ms")
                        ? "COALESCE(updated_at_ms, 0)"
                        : columns.Contains("updated_at")
                            ? "COALESCE(updated_at, 0)"
                            : "rowid";

                    object?[]? row = database.ReadSingleRow(
                        $"SELECT cli_version FROM {ThreadsTable} " +
                        "WHERE cli_version IS NOT NULL AND TRIM(cli_version) <> '' " +
                        $"ORDER BY {orderBy} DESC LIMIT 1");
                    cliVersion = row is { Length: > 0 } ? row[0] as string : null;
                }
                else
                {
                    problems.Add($"{ThreadsTable}에 cli_version 컬럼이 없습니다. (구버전 state DB)");
                }
            }
            else
            {
                problems.Add($"{ThreadsTable} 테이블이 없습니다.");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"thread 정보를 읽는 중 오류: {ex.Message}");
        }

        return new StateDbSnapshot
        {
            OpenMode = database.OpenMode,
            Error = problems.Count == 0 ? null : string.Join(" / ", problems),
            LatestMigrationVersion = migrationVersion,
            LatestMigrationDescription = migrationDescription,
            ThreadRowCount = threadCount,
            ArchivedThreadRowCount = archivedCount,
            LatestCliVersion = cliVersion,
        };
    }

    private static long? ToNullableInt64(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
        _ => null,
    };
}
