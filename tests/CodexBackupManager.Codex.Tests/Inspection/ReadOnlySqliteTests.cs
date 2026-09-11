using CodexBackupManager.Codex.Sqlite;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// <see cref="ReadOnlySqlite"/>의 URI 조립 로직 테스트.
/// </summary>
/// <remarks>
/// immutable 폴백은 SQLite URI 파일명을 직접 만들어야 한다.
/// 잘못 만들면 "열기 실패"가 아니라 "다른 파일을 연다"가 될 수 있어 문자열 단위로 고정한다.
/// </remarks>
public sealed class ReadOnlySqliteTests
{
    [Theory]
    [InlineData(@"C:\Users\User\.codex\state_5.sqlite",
                "file:///C:/Users/User/.codex/state_5.sqlite?mode=ro&immutable=1")]
    [InlineData(@"D:\_User Project\state_5.sqlite",
                "file:///D:/_User Project/state_5.sqlite?mode=ro&immutable=1")]
    [InlineData(@"\\server\share\state_5.sqlite",
                "file://server/share/state_5.sqlite?mode=ro&immutable=1")]
    [InlineData(@"C:\Users\User\Documents\기획\state_5.sqlite",
                "file:///C:/Users/User/Documents/기획/state_5.sqlite?mode=ro&immutable=1")]
    public void immutable_URI를_조립한다(string path, string expected)
        => Assert.Equal(expected, ReadOnlySqlite.BuildSqliteFileUri(path));

    [Theory]
    [InlineData(@"C:\dir\a?b.sqlite", "%3f")]
    [InlineData(@"C:\dir\a#b.sqlite", "%23")]
    [InlineData(@"C:\dir\a%b.sqlite", "%25")]
    public void URI에서_특수문자를_퍼센트_인코딩한다(string path, string expectedEncoding)
    {
        string uri = ReadOnlySqlite.BuildSqliteFileUri(path);

        // 쿼리 구분자 '?' 는 마지막에 한 번만 등장해야 한다.
        Assert.Contains(expectedEncoding, uri, System.StringComparison.Ordinal);
        Assert.EndsWith("?mode=ro&immutable=1", uri, System.StringComparison.Ordinal);
    }

    [Fact]
    public void immutable_연결_문자열에_Pooling이_꺼져_있다()
    {
        string connectionString = ReadOnlySqlite.BuildImmutableConnectionString(@"C:\dir\state_5.sqlite");

        Assert.Contains("Pooling=False", connectionString, System.StringComparison.Ordinal);
        Assert.Contains("mode=ro", connectionString, System.StringComparison.Ordinal);
        Assert.Contains("immutable=1", connectionString, System.StringComparison.Ordinal);
    }
}
