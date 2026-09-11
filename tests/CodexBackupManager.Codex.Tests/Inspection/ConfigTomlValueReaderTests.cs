using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// <see cref="ConfigTomlValueReader"/> 테스트.
/// </summary>
public sealed class ConfigTomlValueReaderTests
{
    [Fact]
    public void 커밋된_픽스처에서_두_값을_읽는다()
    {
        string configPath = Path.Combine(RepositoryFixtures.CodexHomeFixture, "config.toml");

        IReadOnlyDictionary<string, string> values = ConfigTomlValueReader.ReadValues(
            configPath,
            ConfigTomlValueReader.DesktopVersionKey,
            ConfigTomlValueReader.CliPathKey);

        Assert.Equal("99.123.45678", values[ConfigTomlValueReader.DesktopVersionKey]);
        Assert.Equal(@"C:\Fixture\bin\codex.exe", values[ConfigTomlValueReader.CliPathKey]);
    }

    [Fact]
    public void 파일이_없으면_빈_결과를_돌려준다()
    {
        IReadOnlyDictionary<string, string> values = ConfigTomlValueReader.ReadValues(
            Path.Combine(Path.GetTempPath(), "cbm-tests", Path.GetRandomFileName() + ".toml"),
            ConfigTomlValueReader.DesktopVersionKey);

        Assert.Empty(values);
    }

    [Fact]
    public void 요청하지_않은_키는_돌려주지_않는다()
    {
        string configPath = Path.Combine(RepositoryFixtures.CodexHomeFixture, "config.toml");

        IReadOnlyDictionary<string, string> values = ConfigTomlValueReader.ReadValues(
            configPath,
            ConfigTomlValueReader.DesktopVersionKey);

        Assert.Single(values);
        Assert.False(values.ContainsKey(ConfigTomlValueReader.CliPathKey));
    }

    [Theory]
    // literal string — 이스케이프 없음. 실제 Codex config.toml이 경로에 사용하는 형태.
    [InlineData(@"'C:\Users\User\.codex'", @"C:\Users\User\.codex")]
    [InlineData(@"'\\?\C:\Fixture\.tmp'", @"\\?\C:\Fixture\.tmp")]
    [InlineData(@"'{""browser"":""C:/x.mjs""}'", @"{""browser"":""C:/x.mjs""}")]
    // basic string — 최소 이스케이프 처리.
    [InlineData("\"26.903.61454\"", "26.903.61454")]
    [InlineData("\"C:\\\\dir\\\\file.exe\"", @"C:\dir\file.exe")]
    public void 스칼라_문자열을_해석한다(string raw, string expected)
    {
        Assert.True(ConfigTomlValueReader.TryParseScalarString(raw, out string? value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("[ 'a', 'b' ]")]
    [InlineData("\"\"\"multi\nline\"\"\"")]
    [InlineData("\"unterminated")]
    public void 지원하지_않는_값_형태는_거부한다(string raw)
    {
        Assert.False(ConfigTomlValueReader.TryParseScalarString(raw, out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void 주석과_테이블_헤더는_무시한다()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cbm-tests", Path.GetRandomFileName() + ".toml");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);

        try
        {
            File.WriteAllText(temporary, string.Join(
                '\n',
                "# TARGET_KEY = \"주석 안의 값은 무시된다\"",
                "[some.table]",
                "other = 1",
                "TARGET_KEY = \"real\"",
                "TARGET_KEY = \"두 번째는 무시된다\""));

            IReadOnlyDictionary<string, string> values = ConfigTomlValueReader.ReadValues(temporary, "TARGET_KEY");

            Assert.Equal("real", values["TARGET_KEY"]);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [Fact]
    public void 키를_하나도_요청하지_않으면_빈_결과다()
    {
        string configPath = Path.Combine(RepositoryFixtures.CodexHomeFixture, "config.toml");

        Assert.Empty(ConfigTomlValueReader.ReadValues(configPath));
    }
}
