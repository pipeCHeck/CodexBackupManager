using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// Phase 9_1-T1 — <see cref="GlobalStateReader.ReadLegacyProjectIdMapping"/>. 합성 JSON만 쓴다.
/// </summary>
public sealed class GlobalStateReaderLegacyMappingTests : IDisposable
{
    private const string Home = @"C:\Fixture\CodexHome";
    private const string OtherHome = @"C:\Fixture\OtherHome";

    private readonly string _path = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N") + ".json");

    public GlobalStateReaderLegacyMappingTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private static CanonicalPath HomePath => CanonicalPath.Create(Home);

    private void WriteMapping(object byHost)
        => File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [GlobalStateReader.LegacyProjectIdMappingKey] = byHost,
        }));

    [Fact]
    public void 현재_Home의_host_key_매핑을_읽는다()
    {
        WriteMapping(new Dictionary<string, object>
        {
            ["local:" + Home] = new Dictionary<string, string> { ["legacy-1"] = "db-1", ["legacy-2"] = "db-2" },
        });

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out string? warning);

        Assert.Null(warning);
        Assert.Equal(2, mapping.Count);
        Assert.Equal("db-1", mapping["legacy-1"]);
    }

    [Fact]
    public void host_key는_canonical로_비교한다()
    {
        WriteMapping(new Dictionary<string, object>
        {
            [@"local:\\?\c:\FIXTURE\codexhome\"] = new Dictionary<string, string> { ["legacy-1"] = "db-1" },
        });

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out _);

        Assert.Equal("db-1", mapping["legacy-1"]);
    }

    [Fact]
    public void 다른_host_key의_매핑은_쓰지_않는다()
    {
        WriteMapping(new Dictionary<string, object>
        {
            ["local:" + OtherHome] = new Dictionary<string, string> { ["legacy-1"] = "db-other" },
            ["remote:" + Home] = new Dictionary<string, string> { ["legacy-1"] = "db-remote" },
        });

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out string? warning);

        Assert.Empty(mapping);
        Assert.Null(warning);
    }

    [Fact]
    public void 다른_host_key가_섞여_있어도_현재_Home_것만_쓴다()
    {
        WriteMapping(new Dictionary<string, object>
        {
            ["local:" + OtherHome] = new Dictionary<string, string> { ["legacy-1"] = "db-other" },
            ["local:" + Home] = new Dictionary<string, string> { ["legacy-1"] = "db-mine" },
        });

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out _);

        Assert.Equal("db-mine", Assert.Single(mapping).Value);
    }

    [Theory]
    [InlineData("""{ "app-server-project-id-by-legacy-project-id-by-host": ["x"] }""")]
    [InlineData("""{ "app-server-project-id-by-legacy-project-id-by-host": { "local:C:\\Fixture\\CodexHome": "not-an-object" } }""")]
    [InlineData("""{ "app-server-project-id-by-legacy-project-id-by-host": { "local:C:\\Fixture\\CodexHome": [1, 2] } }""")]
    [InlineData("""{ "app-server-project-id-by-legacy-project-id-by-host": """)]
    public void 매핑_형식이_이상하면_예외_없이_빈_매핑과_경고다(string json)
    {
        File.WriteAllText(_path, json);

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out string? warning);

        Assert.Empty(mapping);
        Assert.NotNull(warning);
        Assert.DoesNotContain(Home, warning); // 경로 원문을 경고에 넣지 않는다
    }

    [Fact]
    public void 문자열이_아닌_값은_건너뛰고_경고한다()
    {
        File.WriteAllText(_path, """
            { "app-server-project-id-by-legacy-project-id-by-host": {
                "local:C:\\Fixture\\CodexHome": { "legacy-1": "db-1", "legacy-2": 42, "legacy-3": null, "legacy-4": "" } } }
            """);

        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out string? warning);

        Assert.Equal("db-1", Assert.Single(mapping).Value);
        Assert.Contains("3", warning);
    }

    [Fact]
    public void 파일이나_키가_없으면_경고_없이_빈_매핑이다()
    {
        Assert.Empty(GlobalStateReader.ReadLegacyProjectIdMapping(_path + ".missing", HomePath, out string? missingFileWarning));
        Assert.Null(missingFileWarning);

        File.WriteAllText(_path, """{ "local-projects": {} }""");
        Assert.Empty(GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out string? missingKeyWarning));
        Assert.Null(missingKeyWarning);
    }

    [Fact]
    public void 읽기만_하고_파일을_바꾸지_않는다()
    {
        WriteMapping(new Dictionary<string, object>
        {
            ["local:" + Home] = new Dictionary<string, string> { ["legacy-1"] = "db-1" },
        });
        byte[] before = File.ReadAllBytes(_path);
        DateTime writeTimeBefore = File.GetLastWriteTimeUtc(_path);

        GlobalStateReader.ReadLegacyProjectIdMapping(_path, HomePath, out _);

        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(_path));
    }
}
