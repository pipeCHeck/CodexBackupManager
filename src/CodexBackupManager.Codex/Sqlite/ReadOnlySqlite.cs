using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;

// Microsoft.Data.Sqlite.SqliteOpenMode 와 이름이 겹치므로 우리 enum은 별칭으로 쓴다.
using DomainSqliteOpenMode = CodexBackupManager.Domain.Codex.SqliteOpenMode;

namespace CodexBackupManager.Codex.Sqlite;

/// <summary>
/// Codex의 SQLite 파일을 <b>읽기 전용</b>으로만 여는 유일한 통로.
/// </summary>
/// <remarks>
/// <para>
/// Phase 0 조사에서 <c>state_5.sqlite-wal</c>(4.1 MB)과 <c>-shm</c>이 활성 상태인 것을 확인했다.
/// 쓰기 가능한 연결로 WAL 데이터베이스를 열면 SQLite가 <b>자동 checkpoint</b>를 수행해
/// 원본 파일을 변경할 수 있다. 따라서 다음을 강제한다.
/// </para>
/// <list type="bullet">
///   <item><c>Mode=ReadOnly</c> — 쓰기 불가. 읽기 전용 연결은 checkpoint를 수행하지 않는다.</item>
///   <item><c>Pooling=False</c> — 연결을 풀에 남기지 않아 파일 핸들을 즉시 놓는다.</item>
///   <item><c>Cache=Private</c> — 다른 연결과 페이지 캐시를 공유하지 않는다.</item>
/// </list>
/// <para>
/// 읽기 전용 연결이 <c>-shm</c>을 만들 수 없어 실패하는 경우
/// (<c>SQLITE_READONLY_CANTINIT</c> 등)에 한해 <c>immutable=1</c> URI로 재시도한다.
/// 이 폴백은 원본을 절대 건드리지 않지만, <b>WAL에만 있는 최신 변경을 보지 못할 수 있다</b>.
/// 그래서 사용한 모드를 <see cref="ReadOnlyDatabase.OpenMode"/>로 호출자에게 알린다.
/// </para>
/// <para>
/// 또한 <see cref="ReadOnlyDatabase"/>는 SELECT / 읽기 전용 PRAGMA 이외의 SQL을 거부한다.
/// 방어선을 연결 옵션 한 곳에만 두지 않기 위한 이중 장치다.
/// </para>
/// </remarks>
public static class ReadOnlySqlite
{
    /// <summary>
    /// 읽기 전용으로 연다. 실패하면 <c>null</c>을 반환하고 <paramref name="error"/>에 이유를 담는다.
    /// </summary>
    /// <param name="databaseFilePath">SQLite 파일의 전체 경로.</param>
    /// <param name="error">실패 이유. 성공 시 <c>null</c>.</param>
    public static ReadOnlyDatabase? TryOpen(string databaseFilePath, out string? error)
    {
        error = null;

        // 1차: 표준 읽기 전용 연결. WAL에 커밋된 내용까지 보인다.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };

        try
        {
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return new ReadOnlyDatabase(connection, DomainSqliteOpenMode.ReadOnly);
        }
        catch (SqliteException primaryFailure)
        {
            // 2차: immutable 폴백. -wal/-shm 을 전혀 건드리지 않는다.
            try
            {
                var connection = new SqliteConnection(BuildImmutableConnectionString(databaseFilePath));
                connection.Open();
                return new ReadOnlyDatabase(connection, DomainSqliteOpenMode.ReadOnlyImmutableFallback);
            }
            catch (Exception fallbackFailure)
            {
                error =
                    $"읽기 전용으로 열 수 없습니다. 1차: {primaryFailure.Message} / " +
                    $"immutable 폴백: {fallbackFailure.Message}";
                return null;
            }
        }
        catch (Exception ex)
        {
            error = $"읽기 전용으로 열 수 없습니다: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// <c>immutable=1</c> URI 연결 문자열을 만든다.
    /// </summary>
    /// <remarks>
    /// SQLite URI 파일명 규칙에 맞추기 위해 구분자를 <c>/</c>로 바꾸고
    /// <c>?</c> <c>#</c> <c>%</c>를 퍼센트 인코딩한다.
    /// </remarks>
    internal static string BuildImmutableConnectionString(string databaseFilePath)
        => $"Data Source='{BuildSqliteFileUri(databaseFilePath)}';Pooling=False";

    /// <summary><c>file:///C:/dir/state_5.sqlite?mode=ro&amp;immutable=1</c> 형태의 URI를 만든다.</summary>
    internal static string BuildSqliteFileUri(string databaseFilePath)
    {
        string slashed = databaseFilePath.Replace('\\', '/');

        var encoded = new StringBuilder(slashed.Length + 16);
        foreach (char c in slashed)
        {
            switch (c)
            {
                case '?':
                    encoded.Append("%3f");
                    break;
                case '#':
                    encoded.Append("%23");
                    break;
                case '%':
                    encoded.Append("%25");
                    break;
                default:
                    encoded.Append(c);
                    break;
            }
        }

        string path = encoded.ToString();

        // UNC (\\server\share\x → //server/share/x) 는 file://server/share/x,
        // 그 외(C:/dir/x, /dir/x)는 file:/// 로 시작한다.
        string uri = path.StartsWith("//", StringComparison.Ordinal)
            ? "file:" + path
            : path.StartsWith("/", StringComparison.Ordinal)
                ? "file://" + path
                : "file:///" + path;

        return uri + "?mode=ro&immutable=1";
    }
}

/// <summary>
/// 읽기 전용으로 열린 SQLite 연결. SELECT / 읽기 PRAGMA만 실행할 수 있다.
/// </summary>
public sealed class ReadOnlyDatabase : IDisposable
{
    private static readonly string[] AllowedPrefixes =
    [
        "SELECT ",
        "PRAGMA TABLE_INFO",
        "PRAGMA TABLE_LIST",
        "PRAGMA DATABASE_LIST",
        "PRAGMA USER_VERSION;",
        "PRAGMA USER_VERSION",
    ];

    private readonly SqliteConnection _connection;

    internal ReadOnlyDatabase(SqliteConnection connection, DomainSqliteOpenMode openMode)
    {
        _connection = connection;
        OpenMode = openMode;
    }

    /// <summary>실제로 사용된 열기 방식.</summary>
    public DomainSqliteOpenMode OpenMode { get; }

    /// <summary>
    /// 스칼라 하나를 읽는다. 결과가 없으면 <c>null</c>.
    /// </summary>
    public object? ReadScalar(string sql)
    {
        Guard(sql);
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>여러 행의 첫 컬럼을 문자열로 읽는다.</summary>
    public IReadOnlyList<string> ReadFirstColumnStrings(string sql)
    {
        Guard(sql);
        var results = new List<string>();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                results.Add(reader.GetString(0));
            }
        }

        return results;
    }

    /// <summary>한 행을 읽어 컬럼 값 배열로 돌려준다. 행이 없으면 <c>null</c>.</summary>
    public object?[]? ReadSingleRow(string sql)
    {
        Guard(sql);
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var values = new object?[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return values;
    }

    /// <summary>테이블이 존재하는지 확인한다.</summary>
    public bool TableExists(string tableName)
    {
        // 테이블 이름을 문자열 리터럴로 붙이지 않고 파라미터로 넘긴다.
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>테이블의 컬럼 이름 집합을 읽는다. 테이블이 없으면 빈 집합.</summary>
    public IReadOnlySet<string> GetColumnNames(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(tableName))
        {
            return columns;
        }

        using SqliteCommand command = _connection.CreateCommand();
        // PRAGMA는 파라미터를 지원하지 않으므로 식별자를 직접 검증한 뒤 삽입한다.
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string QuoteIdentifier(string identifier)
    {
        foreach (char c in identifier)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException("허용되지 않는 식별자입니다.", nameof(identifier));
            }
        }

        return identifier;
    }

    /// <summary>
    /// 쓰기 계열 SQL을 거부한다. 연결 옵션(<c>Mode=ReadOnly</c>) 외의 이중 방어선.
    /// </summary>
    private static void Guard(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        string upper = sql.TrimStart().ToUpperInvariant();
        foreach (string prefix in AllowedPrefixes)
        {
            if (upper.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "이 프로그램은 Codex의 SQLite에 SELECT 및 읽기 전용 PRAGMA만 실행합니다. " +
            $"거부된 SQL 시작 부분: '{upper[..Math.Min(16, upper.Length)]}'");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
