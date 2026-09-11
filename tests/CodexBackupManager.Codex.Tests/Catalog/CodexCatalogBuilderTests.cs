using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Catalog;

/// <summary>
/// <see cref="CodexCatalogBuilder"/> 통합 테스트. 실제 사용자 데이터를 쓰지 않고
/// <see cref="FakeCodexHome"/> + <see cref="FakeStateDatabaseBuilder"/>로 합성한 Codex Home을 쓴다.
/// </summary>
public sealed class CodexCatalogBuilderTests : IDisposable
{
    private readonly FakeCodexHome _home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();

    public void Dispose() => _home.Dispose();

    private void WriteRollout(string threadId, string cwd = @"C:\Fixture\Alpha")
    {
        string day = Path.Combine(_home.Path, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(day);
        string fileName = $"rollout-2026-01-02T03-04-05-{threadId}.jsonl";
        string escapedCwd = cwd.Replace("\\", "\\\\");
        string json = "{\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + threadId +
                       "\",\"cwd\":\"" + escapedCwd + "\"}}\n";
        File.WriteAllText(Path.Combine(day, fileName), json);
    }

    private CodexInstallationInfo BuildInstallation(string stateDbPath)
    {
        string stateDbFileName = Path.GetFileName(stateDbPath);
        File.Copy(stateDbPath, Path.Combine(_home.Path, stateDbFileName), overwrite: true);

        CanonicalPath.TryCreate(_home.Path, out CanonicalPath? home, out _);
        var validation = new CodexHomeValidation(CodexHomeStatus.Valid, 5, [], [], [stateDbFileName]);
        var fileInfo = new FileInfo(Path.Combine(_home.Path, stateDbFileName));

        return new CodexInstallationInfo
        {
            Home = home!,
            HomeDisplayPath = home!.Display,
            Source = CodexHomeSource.UserSelected,
            Validation = validation,
            ActiveStateDatabase = new StateDatabaseInfo(stateDbFileName, 1, fileInfo.Length, fileInfo.LastWriteTimeUtc, false, false),
        };
    }

    private const string ThreadIdA = "01a00000-0000-7000-8000-000000000001";
    private const string ThreadIdB = "01a00000-0000-7000-8000-000000000002";
    private const string ThreadIdSub = "01a00000-0000-7000-8000-000000000003";
    private const string ThreadIdGuardian = "01a00000-0000-7000-8000-000000000004";
    private const string ThreadIdUncategorized = "01a00000-0000-7000-8000-000000000005";

    [Fact]
    public void thread_source가_user인_대화만_프로젝트_그룹에_노출한다()
    {
        WriteRollout(ThreadIdA);
        WriteRollout(ThreadIdSub);
        WriteRollout(ThreadIdGuardian);

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(ThreadIdA, cwd: @"C:\Fixture\Alpha", threadSource: "user", createdAtMs: 1)
            .WithThread(ThreadIdSub, cwd: @"C:\Fixture\Alpha", threadSource: "subagent")
            .WithThread(ThreadIdGuardian, cwd: @"C:\Fixture\Alpha", threadSource: "guardian_review")
            .WithProject("proj-1", "Alpha")
            .WithProjectRoot("proj-1", @"C:\Fixture\Alpha")
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        Assert.Equal(3, catalog.AllConversations.Count); // 데이터는 버리지 않는다.
        Assert.Equal(1, catalog.UserConversationCount);

        ProjectEntry project = Assert.Single(catalog.Projects);
        Assert.Equal("Alpha", project.DisplayName);
        ConversationEntry onlyConversation = Assert.Single(project.Conversations);
        Assert.Equal(ThreadIdA, onlyConversation.ThreadId);
    }

    [Fact]
    public void global_state_할당으로_프로젝트를_연결한다()
    {
        WriteRollout(ThreadIdB, cwd: @"C:\Fixture\Beta");
        File.WriteAllText(Path.Combine(_home.Path, ".codex-global-state.json"), $$"""
            {
              "local-projects": { "local-1": { "id": "local-1", "name": "Beta Project", "rootPaths": ["C:\\Fixture\\Beta"] } },
              "thread-project-assignments": { "{{ThreadIdB}}": { "projectKind": "local", "projectId": "local-1" } }
            }
            """);

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(ThreadIdB, cwd: @"C:\Fixture\Beta", threadSource: "user", createdAtMs: 1)
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        ProjectEntry project = Assert.Single(catalog.Projects);
        Assert.Equal("local-1", project.ProjectId);
        Assert.Equal("Beta Project", project.DisplayName);
        Assert.Equal(ThreadIdB, Assert.Single(project.Conversations).ThreadId);
    }

    [Fact]
    public void 어디에도_연결되지_않으면_기타_대화_그룹에_들어간다()
    {
        WriteRollout(ThreadIdUncategorized, cwd: @"C:\Fixture\Nowhere");

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(ThreadIdUncategorized, cwd: @"C:\Fixture\Nowhere", threadSource: "user", createdAtMs: 1)
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        ProjectEntry group = Assert.Single(catalog.Projects);
        Assert.True(group.IsUncategorized);
        Assert.Equal("기타 대화", group.DisplayName);
    }

    [Fact]
    public void archive_여부와_체인_정보를_ConversationEntry에_담는다()
    {
        WriteRollout(ThreadIdA);

        string dbPath = new FakeStateDatabaseBuilder()
            .WithThread(ThreadIdA, cwd: @"C:\Fixture\Alpha", threadSource: "user", archived: true, createdAtMs: 1)
            .WithProject("proj-1", "Alpha")
            .WithProjectRoot("proj-1", @"C:\Fixture\Alpha")
            .BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        ConversationEntry entry = catalog.AllConversations.Single(c => c.ThreadId == ThreadIdA);
        Assert.True(entry.Archived);
        Assert.NotNull(entry.Chain);
        Assert.Single(entry.Chain!.Files);
    }

    [Fact]
    public void 성능_측정값을_채운다()
    {
        WriteRollout(ThreadIdA);
        string dbPath = new FakeStateDatabaseBuilder().WithThread(ThreadIdA, threadSource: "user").BuildToTempFile();

        CodexCatalog catalog = CodexCatalogBuilder.Build(BuildInstallation(dbPath));

        Assert.Equal(1, catalog.Stats.RolloutFileCount);
        Assert.Equal(1, catalog.Stats.ThreadRowCount);
        Assert.True(catalog.Stats.TotalBuildDuration >= TimeSpan.Zero);
    }
}
