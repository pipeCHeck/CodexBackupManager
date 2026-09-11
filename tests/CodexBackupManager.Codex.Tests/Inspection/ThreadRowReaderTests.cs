using System;
using System.IO;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

public sealed class ThreadRowReaderTests : IDisposable
{
    private readonly string _dbPath;

    public ThreadRowReaderTests()
    {
        _dbPath = new FakeStateDatabaseBuilder()
            .WithThread("t-user", cwd: @"C:\Fixture\Alpha", threadSource: "user", createdAtMs: 100, updatedAtMs: 200)
            .WithThread("t-archived", threadSource: "user", archived: true)
            .WithThread("t-subagent", threadSource: "subagent")
            .BuildToTempFile();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 전체_행을_읽고_archive_여부를_보존한다()
    {
        using ReadOnlyDatabase? db = ReadOnlySqlite.TryOpen(_dbPath, out string? error);
        Assert.NotNull(db);
        Assert.Null(error);

        var rows = ThreadRowReader.Read(db!);

        Assert.Equal(3, rows.Count);
        ThreadRow archived = Assert.Single(rows, r => r.Id == "t-archived");
        Assert.True(archived.Archived);
        ThreadRow user = Assert.Single(rows, r => r.Id == "t-user");
        Assert.Equal(@"C:\Fixture\Alpha", user.Cwd);
        Assert.Equal(100, user.CreatedAtMs);
        Assert.Equal("subagent", Assert.Single(rows, r => r.Id == "t-subagent").ThreadSource);
    }
}
