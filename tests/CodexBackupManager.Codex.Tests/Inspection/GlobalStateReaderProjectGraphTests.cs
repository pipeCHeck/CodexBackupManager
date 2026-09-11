using System;
using System.IO;
using CodexBackupManager.Codex.Inspection;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

public sealed class GlobalStateReaderProjectGraphTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N") + ".json");

    public GlobalStateReaderProjectGraphTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

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
    public void local_projects와_할당과_projectless를_읽는다()
    {
        File.WriteAllText(_path, """
            {
              "local-projects": {
                "local-fixture1": { "id": "local-fixture1", "name": "Alpha", "rootPaths": ["C:\\Fixture\\Alpha"] }
              },
              "thread-project-assignments": {
                "t1": { "projectKind": "local", "projectId": "local-fixture1" }
              },
              "projectless-thread-ids": ["t2"]
            }
            """);

        GlobalStateReader.ProjectGraph? graph = GlobalStateReader.TryReadProjectGraph(_path, out string? error);

        Assert.Null(error);
        Assert.NotNull(graph);
        Assert.Equal("Alpha", graph!.LocalProjects["local-fixture1"].DisplayName);
        Assert.Equal(@"C:\Fixture\Alpha", graph.LocalProjects["local-fixture1"].RootPaths[0]);
        Assert.Equal("local-fixture1", graph.ThreadProjectAssignments["t1"]);
        Assert.Contains("t2", graph.ProjectlessThreadIds);
    }

    [Fact]
    public void projectKind가_local이_아니면_할당에서_제외한다()
    {
        File.WriteAllText(_path, """
            { "thread-project-assignments": { "t1": { "projectKind": "remote", "projectId": "x" } } }
            """);

        GlobalStateReader.ProjectGraph? graph = GlobalStateReader.TryReadProjectGraph(_path, out _);

        Assert.NotNull(graph);
        Assert.False(graph!.ThreadProjectAssignments.ContainsKey("t1"));
    }

    [Fact]
    public void 파일이_없으면_오류와_함께_null이다()
    {
        GlobalStateReader.ProjectGraph? graph = GlobalStateReader.TryReadProjectGraph(_path, out string? error);

        Assert.Null(graph);
        Assert.NotNull(error);
    }
}
