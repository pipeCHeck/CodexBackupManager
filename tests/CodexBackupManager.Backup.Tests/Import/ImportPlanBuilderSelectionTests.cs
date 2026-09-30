using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using Xunit;
using static CodexBackupManager.Backup.Tests.Import.ImportSelectionTests;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_2-T2 — <see cref="ImportPlanBuilder"/>의 선택 오버로드와 선택 없는 오버로드.
/// backup identity pinning을 실제로 확인하려고 진짜 Export 파일을 하나 만들고, 그 Preview의 프로젝트/대화만 합성 레코드로 바꿔 쓴다
/// (Plan 빌더는 Preview 레코드와 backup identity만 본다).
/// </summary>
public sealed class ImportPlanBuilderSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-plan-selection-tests", Guid.NewGuid().ToString("N"));
    private readonly string _backupPath;
    private readonly ImportPreview _realPreview;

    public ImportPlanBuilderSelectionTests()
    {
        Directory.CreateDirectory(_root);
        string threadId = Guid.NewGuid().ToString();
        string rollout = Path.Combine(_root, $"rollout-2026-01-02T03-04-05-{threadId}.jsonl");
        File.WriteAllText(rollout, "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":0,\"type\":\"event_msg\",\"payload\":{}}\n");
        var entry = new ConversationEntry
        {
            ThreadId = threadId,
            Row = new ThreadRow { Id = threadId },
            Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
        };
        var file = new RolloutFileReference(rollout, Path.GetFileName(rollout), threadId, null, DateTimeOffset.UnixEpoch, false, RolloutFileKind.PlainJsonl);
        var catalog = new CodexCatalog(
            [new ProjectEntry(null, "기타 대화", [], [entry])], [entry],
            new Dictionary<string, ThreadChain> { [threadId] = new(threadId, [file], new HistoryBaseReference?[1], null, null, null, []) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 1, TimeSpan.Zero, TimeSpan.Zero));
        ExportPlan exportPlan = ExportPlanBuilder.Build(catalog, new HashSet<string> { threadId });
        BackupManifest manifest = ManifestBuilder.Build(exportPlan, null, null, DateTimeOffset.UtcNow);
        _backupPath = Path.Combine(_root, "selection.codexbackup");
        Assert.True(BackupWriter.Write(exportPlan, manifest, _backupPath).Success);

        _realPreview = ImportPreviewBuilder.Build(_backupPath, new CodexCatalog(
            [], [], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow, new CodexCatalogStats(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero)));
        Assert.True(_realPreview.Success);
        Assert.NotNull(_realPreview.SourceBackupIdentity);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>진짜 backup identity를 가진 Preview에 합성 프로젝트/대화를 넣는다.</summary>
    private ImportPreview With(IReadOnlyList<ImportProjectPreview> projects, IReadOnlyList<ImportConversationPreview>? dependencyOnly = null, ProjectDirectory? directory = null)
        => _realPreview with
        {
            Projects = projects,
            DependencyOnlyConversations = dependencyOnly ?? [],
            LocalProjectDirectory = directory ?? ProjectDirectory.Empty,
        };

    private ImportPreview Mixed()
    {
        ProjectTarget userUnregistered = ProjectTarget.Uncategorized(ProjectTargetReason.UserSelectedUnregistered, @"C:\User\Chosen");
        ImportProjectPreview manual = Project("manual", userUnregistered, Conv("m1", RevisionRelation.New)) with
        {
            PathMapping = new ProjectPathMapping("manual", "manual", [@"C:\Orig\manual"], ProjectPathMappingStatus.ManuallyLinked, null, @"C:\User\Chosen"),
        };

        return With(
        [
            Project("linked", Linked,
                Conv("n1", RevisionRelation.New),
                Conv("u1", RevisionRelation.IncomingAhead),
                Conv("s1", RevisionRelation.Identical),
                Conv("l1", RevisionRelation.LocalAhead),
                Conv("d1", RevisionRelation.Diverged),
                Conv("c1", RevisionRelation.New, ancestors: ["dep-new", "dep-same"]),
                Conv("c2", RevisionRelation.New, ancestors: ["dep-broken"])),
            Project("missing", Missing, Conv("n2", RevisionRelation.New), Conv("z1", RevisionRelation.IncomingAhead, compressed: true)),
            manual,
            Project(null, NotApplicable, Conv("n3", RevisionRelation.New), Conv("x1", RevisionRelation.Unverifiable)),
        ],
        [
            Conv("dep-new", RevisionRelation.New, selected: false),
            Conv("dep-same", RevisionRelation.Identical, selected: false),
            Conv("dep-broken", RevisionRelation.Unverifiable, selected: false),
            Conv("dep-unused", RevisionRelation.New, selected: false),
        ]);
    }

    // ── 선택 없는 오버로드 = 이전 결과 ─────────────────────────────────────────

    [Fact]
    public void 선택이_없으면_이전과_같은_Plan이다_필드_단위()
    {
        ImportPreview preview = Mixed();

        ImportPlan plan = ImportPlanBuilder.Build(preview, _backupPath)!;

        Assert.Null(plan.UserChoices);
        Assert.Equal(_realPreview.SourceBackupIdentity, plan.Backup);
        Assert.Equal(preview.Projects.Count, plan.Projects.Count);
        for (int i = 0; i < preview.Projects.Count; i++)
        {
            ImportProjectPreview expected = preview.Projects[i];
            ImportPlanProject actual = plan.Projects[i];
            Assert.Equal(expected.ProjectId, actual.ProjectId);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.PathMapping.Status, actual.PathStatus);
            Assert.Equal(expected.PathMapping.ResolvedLocalPath, actual.TargetProjectPath);
            Assert.Equal(expected.SuggestedTarget, actual.ResolvedTarget);
            Assert.True(plan.UsesProjectTarget(actual));
        }

        var expectedConversations = preview.Projects
            .SelectMany(p => p.Conversations.Select(c => (c, p.PathMapping.ResolvedLocalPath)))
            .Concat(preview.DependencyOnlyConversations.Select(c => (c, (string?)null)))
            .ToList();
        Assert.Equal(expectedConversations.Count, plan.Conversations.Count);
        for (int i = 0; i < expectedConversations.Count; i++)
        {
            (ImportConversationPreview expected, string? targetPath) = expectedConversations[i];
            ImportPlanConversation actual = plan.Conversations[i];
            Assert.Equal(expected.ThreadId, actual.ThreadId);
            Assert.Equal(expected.IsSelected, actual.IsSelected);
            Assert.Equal(expected.Relation, actual.Relation);
            Assert.Equal(expected.PlannedAction, actual.PlannedAction);
            Assert.Equal(targetPath, actual.TargetProjectPath);
            Assert.Equal(expected.Relation == RevisionRelation.New ? ExpectedLocalPresence.MustNotExist : ExpectedLocalPresence.MustExist, actual.Precondition.ExpectedPresence);
            Assert.Equal(ImportSkipReason.None, actual.SkipReason);
            Assert.Null(actual.TargetProjectKey);
            Assert.False(actual.IsExcludedFromApply);
        }

        Assert.True(plan.HasBlockingIssues);        // x1, dep-broken(Unverifiable)
        Assert.True(plan.HasUnresolvedDivergence);  // d1
        Assert.False(plan.IsApplyReady);
    }

    // ── 선택 반영 ────────────────────────────────────────────────────────────

    [Fact]
    public void 기본_선택_Plan은_선택을_반영하고_closure_기준_플래그로_적용_가능하다()
    {
        ImportPreview preview = Mixed();
        var choices = new ImportUserChoices(
            new HashSet<string> { "n1", "u1", "c1", "n2", "m1", "n3" },
            new Dictionary<string, ProjectTargetDecision>());

        ImportPlan plan = ImportPlanBuilder.Build(preview, _backupPath, choices)!;
        ImportPlanConversation Get(string id) => plan.Conversations.Single(c => c.ThreadId == id);

        Assert.Same(choices, plan.UserChoices);
        Assert.False(plan.HasBlockingIssues);        // x1, dep-broken은 closure 밖
        Assert.False(plan.HasUnresolvedDivergence);  // d1은 closure 밖
        Assert.True(plan.IsApplyReady);

        Assert.Equal(ImportPlannedAction.Import, Get("n1").PlannedAction);
        Assert.Equal(ImportPlannedAction.Update, Get("u1").PlannedAction);
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.UserExcluded), (Get("d1").PlannedAction, Get("d1").SkipReason));
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.UserExcluded), (Get("s1").PlannedAction, Get("s1").SkipReason));
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.LocalAhead), (Get("l1").PlannedAction, Get("l1").SkipReason));
        Assert.Equal(ImportPlannedAction.Import, Get("dep-new").PlannedAction);
        Assert.Equal(ImportPlannedAction.NoOp, Get("dep-same").PlannedAction);
        Assert.Equal(ImportSkipReason.NotSelectedDependencyNotNeeded, Get("dep-broken").SkipReason);
        Assert.True(Get("dep-broken").IsExcludedFromApply);
        Assert.Equal(ImportSkipReason.UserExcluded, Get("x1").SkipReason);

        // 목적지: LinkExisting만 TargetProjectPath가 있다.
        Assert.Equal("linked", Get("n1").TargetProjectKey);
        Assert.Equal(@"C:\Linked", Get("n1").TargetProjectPath);
        Assert.Null(Get("n2").TargetProjectPath);
        Assert.Null(Get("m1").TargetProjectPath); // 사용자 지정 미등록 폴더도 연결이 아니므로 null
        Assert.Equal(ImportUserChoices.UncategorizedProjectKey, Get("n3").TargetProjectKey);
        Assert.Null(Get("dep-new").TargetProjectKey);

        ImportPlanProject manual = plan.Projects.Single(p => p.ProjectId == "manual");
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, manual.PathStatus); // 제안(수동 재지정 결과)을 그대로 쓴 경우
        Assert.Null(manual.TargetProjectPath);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, manual.ResolvedTarget!.Reason);
    }

    [Fact]
    public void 폴더_결정은_ManuallyLinked와_결정_기준_ResolvedTarget이_되고_연결되면_TargetProjectPath가_생긴다()
    {
        string folder = Path.Combine(_root, "registered-folder");
        Directory.CreateDirectory(folder);
        var directory = new ProjectDirectory(
        [
            new KnownProject("db-f", "db-f", new HashSet<string>(), "F", [new KnownProjectRoot(folder, Domain.Paths.CanonicalPath.Create(folder), true)], 0),
        ]);
        ImportPreview preview = With([Project("missing", Missing, Conv("n", RevisionRelation.New))], directory: directory);
        var choices = new ImportUserChoices(
            new HashSet<string> { "n" },
            new Dictionary<string, ProjectTargetDecision> { ["missing"] = ProjectTargetDecision.Folder(folder) });

        ImportPlan plan = ImportPlanBuilder.Build(preview, _backupPath, choices)!;

        ImportPlanProject project = Assert.Single(plan.Projects);
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, project.PathStatus);
        Assert.Equal("db-f", project.ResolvedTarget!.LinkDbProjectId);
        Assert.Equal(project.ResolvedTarget.FolderPath, project.TargetProjectPath);
        Assert.Equal(project.TargetProjectPath, Assert.Single(plan.Conversations).TargetProjectPath);
    }

    [Fact]
    public void 새로_가져올_대화가_없는_프로젝트는_목적지를_쓰지_않는다()
    {
        ImportPreview preview = Mixed();
        var choices = new ImportUserChoices(new HashSet<string> { "n2" }, new Dictionary<string, ProjectTargetDecision>());

        ImportPlan plan = ImportPlanBuilder.Build(preview, _backupPath, choices)!;

        Assert.True(plan.UsesProjectTarget(plan.Projects.Single(p => p.ProjectId == "missing")));
        Assert.False(plan.UsesProjectTarget(plan.Projects.Single(p => p.ProjectId == "linked")));
        Assert.False(plan.UsesProjectTarget(plan.Projects.Single(p => p.ProjectId is null)));
    }

    [Fact]
    public void closure에_충돌_조상이_있으면_Plan_플래그가_선다()
    {
        ImportPreview preview = Mixed();

        ImportPlan withBroken = ImportPlanBuilder.Build(preview, _backupPath, new ImportUserChoices(new HashSet<string> { "c2" }, new Dictionary<string, ProjectTargetDecision>()))!;

        Assert.True(withBroken.HasBlockingIssues);
        Assert.False(withBroken.IsApplyReady);
        Assert.Equal(ImportPlannedAction.Blocked, withBroken.Conversations.Single(c => c.ThreadId == "dep-broken").PlannedAction);
    }

    [Fact]
    public void backup이_바뀌면_선택_Plan도_만들지_않는다()
    {
        ImportPreview preview = Mixed();
        File.AppendAllText(_backupPath, "x");

        Assert.Null(ImportPlanBuilder.Build(preview, _backupPath, ImportUserChoices.CreateDefault(preview)));
    }

    [Fact]
    public void 결정_오류가_있으면_Plan을_만들지_않는다()
    {
        ImportPreview preview = Mixed();
        var choices = new ImportUserChoices(
            new HashSet<string> { "n2" },
            new Dictionary<string, ProjectTargetDecision> { ["missing"] = new(null, "새 이름", UseSuggestion: false) });

        Assert.Null(ImportPlanBuilder.Build(preview, _backupPath, choices));
    }

    // ── 요약과 Plan 교차 검증 ─────────────────────────────────────────────────

    public static IEnumerable<object[]> ChoiceSets()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "n1" }];
        yield return [new[] { "n1", "u1", "c1", "n2", "m1", "n3" }];
        yield return [new[] { "c1", "c2" }];
        yield return [new[] { "s1", "l1", "d1", "x1", "z1", "dep-new" }];
        yield return [new[] { "n1", "u1", "s1", "l1", "d1", "c1", "c2", "n2", "z1", "m1", "n3", "x1", "dep-unused", "ghost" }];
    }

    [Theory]
    [MemberData(nameof(ChoiceSets))]
    public void Plan의_대화별_동작은_ImportSelection_요약과_같다(string[] included)
    {
        ImportPreview preview = Mixed();
        var choices = new ImportUserChoices(new HashSet<string>(included), new Dictionary<string, ProjectTargetDecision>());

        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        ImportPlan plan = ImportPlanBuilder.Build(preview, _backupPath, choices)!;

        Assert.Equal(summary.Conversations.Select(c => c.ThreadId), plan.Conversations.Select(c => c.ThreadId));
        foreach ((ImportSelectionConversation expected, ImportPlanConversation actual) in summary.Conversations.Zip(plan.Conversations))
        {
            Assert.Equal(expected.FinalAction, actual.PlannedAction);
            Assert.Equal(expected.SkipReason, actual.SkipReason);
            Assert.Equal(expected.ProjectKey, actual.TargetProjectKey);
        }

        Assert.Equal(summary.HasBlockingIssues, plan.HasBlockingIssues);
        Assert.Equal(summary.HasUnresolvedDivergence, plan.HasUnresolvedDivergence);
        Assert.Equal(summary.ImportCount, plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.Import));
        Assert.Equal(summary.UpdateCount, plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.Update));
    }
}
