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
            .WithThread(
                "t-user", cwd: @"C:\Fixture\Alpha", threadSource: "user", createdAtMs: 100, updatedAtMs: 200,
                sandboxPolicy: "{\"type\":\"workspace-write\"}", approvalMode: "on-request", tokensUsed: 42,
                hasUserEvent: true, agentNickname: "Sagan", agentRole: "reviewer", agentPath: @"C:\agents\sagan",
                memoryMode: "enabled", reasoningEffort: "high", isPinned: true, recencyAtMs: 300)
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

    [Fact]
    public void Restore_Sufficiency_Audit로_추가된_컬럼도_보존한다()
    {
        using ReadOnlyDatabase? db = ReadOnlySqlite.TryOpen(_dbPath, out string? error);
        Assert.NotNull(db);
        Assert.Null(error);

        ThreadRow user = Assert.Single(ThreadRowReader.Read(db!), r => r.Id == "t-user");

        // sandbox_policy/approval_mode는 공식 소스에서 opaque JSON/enum 직렬화 문자열로 확인됐다
        // (docs/codexbackup-format-v1.md §1.3) — 해석하지 않고 그대로 보존한다.
        Assert.Equal("{\"type\":\"workspace-write\"}", user.SandboxPolicy);
        Assert.Equal("on-request", user.ApprovalMode);
        Assert.Equal(42, user.TokensUsed);
        Assert.True(user.HasUserEvent);
        Assert.Equal("Sagan", user.AgentNickname);
        Assert.Equal("reviewer", user.AgentRole);
        Assert.Equal(@"C:\agents\sagan", user.AgentPath);
        Assert.Equal("enabled", user.MemoryMode);
        Assert.Equal("high", user.ReasoningEffort);
        Assert.True(user.IsPinned);
        Assert.Equal(300, user.RecencyAtMs);
    }
}
