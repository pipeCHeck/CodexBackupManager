using System.Collections.Generic;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Projects;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Projects;

public sealed class CodexProjectResolverTests
{
    private static ThreadRow Row(string id, string? projectId = null, string? cwd = null)
        => new() { Id = id, ProjectId = projectId, Cwd = cwd };

    [Fact]
    public void global_state가_전혀_없으면_state_project_id를_쓴다()
    {
        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: "proj-1"), threadAssignmentsMigrated: null, projectGraph: null, rootCandidates: []);

        Assert.Equal("proj-1", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.StateProjectId, result.Source);
    }

    [Fact]
    public void state_project_id가_없으면_global_state_할당을_쓴다()
    {
        var graph = new GlobalStateReader.ProjectGraph(
            new Dictionary<string, LocalProjectInfo>(),
            new Dictionary<string, string> { ["t1"] = "proj-from-global" },
            new HashSet<string>());

        ProjectAssignment result = CodexProjectResolver.Resolve(Row("t1"), null, graph, []);

        Assert.Equal("proj-from-global", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.GlobalStateAssignment, result.Source);
    }

    [Fact]
    public void projectless_thread로_명시되어_있으면_cwd_추측을_하지_않는다()
    {
        var graph = new GlobalStateReader.ProjectGraph(
            new Dictionary<string, LocalProjectInfo>(),
            new Dictionary<string, string>(),
            new HashSet<string> { "t1" });

        CanonicalPath.TryCreate(@"C:\Fixture\Alpha", out CanonicalPath? root, out _);
        var candidates = new List<CodexProjectResolver.RootCandidate> { new("proj-1", root!) };

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", cwd: @"C:\Fixture\Alpha\Sub"), null, graph, candidates);

        Assert.Null(result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.Unassigned, result.Source);
    }

    [Fact]
    public void cwd가_프로젝트_루트_하위면_폴백으로_연결한다()
    {
        CanonicalPath.TryCreate(@"C:\Fixture\Alpha", out CanonicalPath? root, out _);
        var candidates = new List<CodexProjectResolver.RootCandidate> { new("proj-1", root!) };

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", cwd: @"\\?\C:\Fixture\Alpha\Sub"), null, projectGraph: null, candidates);

        Assert.Equal("proj-1", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.CwdFallback, result.Source);
    }

    [Fact]
    public void cwd가_루트와_정확히_같아도_연결한다()
    {
        CanonicalPath.TryCreate(@"C:\Fixture\Alpha", out CanonicalPath? root, out _);
        var candidates = new List<CodexProjectResolver.RootCandidate> { new("proj-1", root!) };

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", cwd: @"c:\fixture\alpha"), null, projectGraph: null, candidates);

        Assert.Equal("proj-1", result.ProjectId);
    }

    [Fact]
    public void 다른_프로젝트의_비슷한_이름_경로는_잘못_매칭되지_않는다()
    {
        CanonicalPath.TryCreate(@"C:\Fixture\Alpha", out CanonicalPath? root, out _);
        var candidates = new List<CodexProjectResolver.RootCandidate> { new("proj-1", root!) };

        // "AlphaBeta"는 "Alpha"로 시작하지만 하위 폴더가 아니다.
        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", cwd: @"C:\Fixture\AlphaBeta"), null, projectGraph: null, candidates);

        Assert.Equal(ProjectAssignmentSource.Unassigned, result.Source);
    }

    [Fact]
    public void 아무_경로에도_속하지_않으면_기타_대화다()
    {
        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", cwd: @"C:\Other\Place"), null, projectGraph: null, rootCandidates: []);

        Assert.Null(result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.Unassigned, result.Source);
    }

    // ── migration authority 정책 ──────────────────────────────────────────

    private static GlobalStateReader.ProjectGraph GraphWithAssignment(string threadId, string projectId)
        => new(
            new Dictionary<string, LocalProjectInfo>(),
            new Dictionary<string, string> { [threadId] = projectId },
            new HashSet<string>());

    [Fact]
    public void migration_false_이고_할당과_state가_다르면_global이_이긴다()
    {
        GlobalStateReader.ProjectGraph graph = GraphWithAssignment("t1", "proj-from-global");

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: "proj-from-state"), threadAssignmentsMigrated: false, graph, []);

        Assert.Equal("proj-from-global", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.GlobalStateAssignment, result.Source);
    }

    [Fact]
    public void migration_true_이고_할당과_state가_다르면_state가_이긴다()
    {
        GlobalStateReader.ProjectGraph graph = GraphWithAssignment("t1", "proj-from-global");

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: "proj-from-state"), threadAssignmentsMigrated: true, graph, []);

        Assert.Equal("proj-from-state", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.StateProjectId, result.Source);
    }

    [Fact]
    public void migration_false_이고_projectless면_state_project_id가_있어도_기타_대화다()
    {
        var graph = new GlobalStateReader.ProjectGraph(
            new Dictionary<string, LocalProjectInfo>(),
            new Dictionary<string, string>(),
            new HashSet<string> { "t1" });

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: "proj-from-state"), threadAssignmentsMigrated: false, graph, []);

        Assert.Null(result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.Unassigned, result.Source);
    }

    [Fact]
    public void migration_true_이고_state값이_없으면_예전_global_할당을_호환_폴백으로_쓴다()
    {
        GlobalStateReader.ProjectGraph graph = GraphWithAssignment("t1", "legacy-proj");

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: null), threadAssignmentsMigrated: true, graph, []);

        Assert.Equal("legacy-proj", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.GlobalStateAssignment, result.Source);
    }

    [Fact]
    public void migration_null이면_migration_완료를_추측하지_않고_global을_먼저_신뢰한다()
    {
        GlobalStateReader.ProjectGraph graph = GraphWithAssignment("t1", "proj-from-global");

        ProjectAssignment result = CodexProjectResolver.Resolve(
            Row("t1", projectId: "proj-from-state"), threadAssignmentsMigrated: null, graph, []);

        Assert.Equal("proj-from-global", result.ProjectId);
        Assert.Equal(ProjectAssignmentSource.GlobalStateAssignment, result.Source);
    }
}
