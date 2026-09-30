using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Domain.Codex.Projects;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// (Phase 9_5-03) 새 Codex 프로젝트를 만들어도 되는 state DB 스키마인지 확인한다. <b>확인한 형태와 정확히 같을 때만</b> 허용한다.
/// </summary>
/// <remarks>
/// <para>
/// 기준은 공식 <c>codex-rs/state</c> 마이그레이션과 이 PC <c>state_5.sqlite</c>를 읽기 전용으로 확인한 결과다
/// (docs/import-ux-redesign-phase9.md §6 "9_5", 2026-10-01 재확인):
/// </para>
/// <code>
/// projects(id TEXT PK, name TEXT NOT NULL, metadata TEXT NOT NULL, position INTEGER NOT NULL,
///          created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL)
/// project_roots(project_id TEXT NOT NULL → projects(id) ON DELETE CASCADE, position INTEGER NOT NULL, path TEXT NOT NULL,
///               PRIMARY KEY(project_id, position))
/// project_idempotency_keys(key TEXT PK, project_id TEXT NOT NULL, created_at_ms INTEGER NOT NULL)
/// threads.project_id → projects(id)
/// </code>
/// <para>
/// 세 프로젝트 테이블은 컬럼 이름·타입·NOT NULL·PK 순서가 모두 같아야 하고 다른 컬럼이 있으면 안 된다(모르는 NOT NULL 컬럼에 값을
/// 추측해 넣지 않기 위함, CLAUDE.md §33). <c>threads</c>는 <c>project_id</c> 컬럼과 <c>projects(id)</c> 외래키만 본다.
/// 이 클래스는 읽기만 한다. 행 읽기는 호출자가 준 함수로 한다(읽기 전용 연결이든 쓰기 트랜잭션 안이든 같은 규칙).
/// </para>
/// </remarks>
public static class ProjectCreationSchemaGate
{
    /// <summary>
    /// 기능 스위치(Phase 9_5-06). 9_0-A 결과가 (a)라 기본은 켜짐이다. 사용자 설정·환경 변수는 없다 — 끄려면 이 값을 바꿔 다시 빌드한다.
    /// 테스트는 이 값을 바꾸지 않고 <see cref="ProjectDirectory.ProjectCreation"/>으로 꺼진 경우를 만든다(병렬 테스트 간섭 방지).
    /// </summary>
    internal static readonly bool FeatureEnabled = true;

    /// <summary>판정 결과.</summary>
    /// <param name="IsSupported">생성을 허용하는지.</param>
    /// <param name="Mismatches">다른 점(테이블/컬럼 이름만, 사용자 데이터 없음). 허용이면 빈 목록.</param>
    public sealed record Result(bool IsSupported, IReadOnlyList<string> Mismatches);

    private sealed record ColumnSpec(string Name, string Type, bool NotNull, int PrimaryKeyOrder);

    private static readonly ColumnSpec[] ProjectsColumns =
    [
        new("id", "TEXT", false, 1),
        new("name", "TEXT", true, 0),
        new("metadata", "TEXT", true, 0),
        new("position", "INTEGER", true, 0),
        new("created_at_ms", "INTEGER", true, 0),
        new("updated_at_ms", "INTEGER", true, 0),
    ];

    private static readonly ColumnSpec[] ProjectRootsColumns =
    [
        new("project_id", "TEXT", true, 1),
        new("position", "INTEGER", true, 2),
        new("path", "TEXT", true, 0),
    ];

    private static readonly ColumnSpec[] IdempotencyColumns =
    [
        new("key", "TEXT", false, 1),
        new("project_id", "TEXT", true, 0),
        new("created_at_ms", "INTEGER", true, 0),
    ];

    /// <summary>읽기 전용 DB로 확인한다.</summary>
    public static Result Check(ReadOnlyDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Check(database.ReadRows);
    }

    /// <summary>
    /// SQL(PRAGMA) 한 줄을 실행해 행을 돌려주는 함수로 확인한다. 테이블이 없으면 PRAGMA가 빈 결과를 돌려주므로 "없음"으로 판정된다.
    /// </summary>
    public static Result Check(Func<string, IReadOnlyList<object?[]>> readRows)
    {
        ArgumentNullException.ThrowIfNull(readRows);
        var mismatches = new List<string>();

        CheckColumns(readRows, "projects", ProjectsColumns, mismatches);
        CheckColumns(readRows, "project_roots", ProjectRootsColumns, mismatches);
        CheckColumns(readRows, "project_idempotency_keys", IdempotencyColumns, mismatches);

        if (!HasForeignKey(readRows, "project_roots", "project_id", "projects", "id", requiredOnDelete: "CASCADE"))
        {
            mismatches.Add("project_roots.project_id 외래키(projects.id, ON DELETE CASCADE)가 없습니다.");
        }

        IReadOnlyList<object?[]> threadColumns = SafeRead(readRows, "PRAGMA table_info(threads)");
        if (!threadColumns.Any(row => string.Equals(AsString(row, 1), "project_id", StringComparison.OrdinalIgnoreCase)))
        {
            mismatches.Add("threads.project_id 컬럼이 없습니다.");
        }
        else if (!HasForeignKey(readRows, "threads", "project_id", "projects", "id", requiredOnDelete: null))
        {
            mismatches.Add("threads.project_id 외래키(projects.id)가 없습니다.");
        }

        return new Result(mismatches.Count == 0, mismatches);
    }

    /// <summary>기능 스위치와 스키마 결과로 <see cref="ProjectCreationSupport"/>를 정한다.</summary>
    public static ProjectCreationSupport ToSupport(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!FeatureEnabled)
        {
            return ProjectCreationSupport.Disabled;
        }

        return result.IsSupported ? ProjectCreationSupport.Supported : ProjectCreationSupport.SchemaUnsupported;
    }

    private static void CheckColumns(
        Func<string, IReadOnlyList<object?[]>> readRows, string table, ColumnSpec[] expected, List<string> mismatches)
    {
        IReadOnlyList<object?[]> rows = SafeRead(readRows, $"PRAGMA table_info({table})");
        if (rows.Count == 0)
        {
            mismatches.Add($"{table} 테이블이 없습니다.");
            return;
        }

        if (rows.Count != expected.Length)
        {
            mismatches.Add($"{table} 컬럼 수가 다릅니다({rows.Count}, 기대 {expected.Length}).");
        }

        foreach (ColumnSpec spec in expected)
        {
            object?[]? row = rows.FirstOrDefault(r => string.Equals(AsString(r, 1), spec.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                mismatches.Add($"{table}.{spec.Name} 컬럼이 없습니다.");
                continue;
            }

            if (!string.Equals(AsString(row, 2), spec.Type, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{table}.{spec.Name} 타입이 다릅니다.");
            }

            if ((AsLong(row, 3) != 0) != spec.NotNull)
            {
                mismatches.Add($"{table}.{spec.Name} NOT NULL 여부가 다릅니다.");
            }

            if (AsLong(row, 5) != spec.PrimaryKeyOrder)
            {
                mismatches.Add($"{table}.{spec.Name} 기본 키 구성이 다릅니다.");
            }
        }
    }

    private static bool HasForeignKey(
        Func<string, IReadOnlyList<object?[]>> readRows, string table, string fromColumn, string toTable, string toColumn, string? requiredOnDelete)
    {
        // PRAGMA foreign_key_list: id, seq, table, from, to, on_update, on_delete, match
        foreach (object?[] row in SafeRead(readRows, $"PRAGMA foreign_key_list({table})"))
        {
            if (string.Equals(AsString(row, 2), toTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AsString(row, 3), fromColumn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AsString(row, 4), toColumn, StringComparison.OrdinalIgnoreCase) &&
                (requiredOnDelete is null || string.Equals(AsString(row, 6), requiredOnDelete, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<object?[]> SafeRead(Func<string, IReadOnlyList<object?[]>> readRows, string sql)
    {
        try
        {
            return readRows(sql);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return [];
        }
    }

    private static string? AsString(object?[] row, int index) => index < row.Length ? row[index] as string : null;

    private static long AsLong(object?[] row, int index)
        => index < row.Length && row[index] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
}
