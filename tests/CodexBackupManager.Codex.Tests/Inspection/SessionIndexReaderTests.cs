using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodexBackupManager.Codex.Inspection;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

public sealed class SessionIndexReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N") + ".jsonl");

    public SessionIndexReaderTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

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

    [Fact]
    public void 같은_id가_여러번_나오면_마지막_값이_이긴다()
    {
        var content = new StringBuilder()
            .AppendLine("""{"id":"t1","thread_name":"first"}""")
            .AppendLine("""{"id":"t2","thread_name":"other"}""")
            .AppendLine("""{"id":"t1","thread_name":"renamed"}""")
            .ToString();
        File.WriteAllText(_path, content);

        IReadOnlyDictionary<string, string> map = SessionIndexReader.ReadTitleMap(_path);

        Assert.Equal("renamed", map["t1"]);
        Assert.Equal("other", map["t2"]);
    }

    [Fact]
    public void 손상된_줄은_건너뛰고_나머지는_읽는다()
    {
        var content = new StringBuilder()
            .AppendLine("""{"id":"t1","thread_name":"ok"}""")
            .AppendLine("not-json")
            .ToString();
        File.WriteAllText(_path, content);

        IReadOnlyDictionary<string, string> map = SessionIndexReader.ReadTitleMap(_path);

        Assert.Equal("ok", map["t1"]);
        Assert.Single(map);
    }

    [Fact]
    public void 파일이_없으면_빈_맵이다()
    {
        IReadOnlyDictionary<string, string> map = SessionIndexReader.ReadTitleMap(_path);

        Assert.Empty(map);
    }
}
