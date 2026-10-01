using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_2-T1 — <see cref="ImportSelection"/>(순수 로직). Preview는 합성 레코드로 만든다(backup 파일 없음).
/// </summary>
public sealed class ImportSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-selection-tests", Guid.NewGuid().ToString("N"));

    public ImportSelectionTests() => Directory.CreateDirectory(_root);

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

    internal static ImportConversationPreview Conv(
        string id, RevisionRelation relation, bool selected = true, bool compressed = false, params string[] ancestors)
        => new(id, selected, id, relation, ImportConflictAnalyzer.Decide(relation), MetadataDifferences.None, [], null, null)
        {
            IsCompressedRollout = compressed,
            RequiredAncestorThreadIds = ancestors,
        };

    internal static ImportProjectPreview Project(string? id, ProjectTarget target, params ImportConversationPreview[] conversations)
    {
        ProjectPathMappingStatus status = target.Reason == ProjectTargetReason.NotApplicable
            ? ProjectPathMappingStatus.NotApplicable
            : target.Kind == ProjectTargetKind.LinkExisting ? ProjectPathMappingStatus.AutoLinked : ProjectPathMappingStatus.NotFound;
        var mapping = new ProjectPathMapping(
            id, id ?? "기타 대화", id is null ? [] : [@"C:\Orig\" + id], status,
            target.LinkDbProjectId, target.Kind == ProjectTargetKind.LinkExisting ? target.FolderPath : null);
        return new ImportProjectPreview(id, id ?? "기타 대화", mapping, conversations) { SuggestedTarget = target };
    }

    internal static ImportPreview Preview(
        IReadOnlyList<ImportProjectPreview> projects,
        IReadOnlyList<ImportConversationPreview>? dependencyOnly = null,
        ProjectDirectory? directory = null)
        => new(true, [], null, projects, dependencyOnly ?? [], [], null) { LocalProjectDirectory = directory ?? ProjectDirectory.Empty };

    internal static readonly ProjectTarget Linked = ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, @"C:\Linked", "db-1");
    internal static readonly ProjectTarget Missing = ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null);
    internal static readonly ProjectTarget NotApplicable = ProjectTarget.Uncategorized(ProjectTargetReason.NotApplicable, null);

    private static ImportUserChoices Choose(params string[] ids)
        => new(new HashSet<string>(ids), new Dictionary<string, ProjectTargetDecision>());

    private static ImportSelectionConversation Result(ImportSelectionSummary summary, string id)
        => summary.Conversations.Single(c => c.ThreadId == id);

    // ── 기본 선택 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 기본_선택은_New와_압축이_아닌_IncomingAhead만_포함한다()
    {
        ImportPreview preview = Preview(
        [
            Project("p1", Linked,
                Conv("new", RevisionRelation.New),
                Conv("ahead", RevisionRelation.IncomingAhead),
                Conv("ahead-zst", RevisionRelation.IncomingAhead, compressed: true),
                Conv("same", RevisionRelation.Identical),
                Conv("local", RevisionRelation.LocalAhead),
                Conv("div", RevisionRelation.Diverged),
                Conv("unv", RevisionRelation.Unverifiable)),
        ],
        [Conv("dep", RevisionRelation.New, selected: false)]);

        ImportUserChoices choices = ImportUserChoices.CreateDefault(preview);

        Assert.Equal(["ahead", "new"], choices.IncludedThreadIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ProjectTargetDecision.Suggested, choices.DecisionFor("p1"));
        Assert.Equal(ImportUnselectableReason.CompressedUpdate, ImportSelection.GetUnselectableReason(preview.Projects[0].Conversations[2]));
        Assert.Equal(ImportUnselectableReason.DependencyOnly, ImportSelection.GetUnselectableReason(preview.DependencyOnlyConversations[0]));
    }

    [Fact]
    public void 제외한_대화는_Skip_UserExcluded이고_LocalAhead는_Skip_LocalAhead_필요없는_조상은_NotSelectedDependencyNotNeeded다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("a", RevisionRelation.New), Conv("b", RevisionRelation.New), Conv("l", RevisionRelation.LocalAhead), Conv("d", RevisionRelation.Diverged))],
            [Conv("dep", RevisionRelation.New, selected: false)]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("a"));

        Assert.Equal(ImportPlannedAction.Import, Result(summary, "a").FinalAction);
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.UserExcluded), (Result(summary, "b").FinalAction, Result(summary, "b").SkipReason));
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.LocalAhead), (Result(summary, "l").FinalAction, Result(summary, "l").SkipReason));
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.UserExcluded), (Result(summary, "d").FinalAction, Result(summary, "d").SkipReason));
        Assert.Equal((ImportPlannedAction.Skip, ImportSkipReason.NotSelectedDependencyNotNeeded), (Result(summary, "dep").FinalAction, Result(summary, "dep").SkipReason));
        Assert.Equal(ProjectTargetKind.LinkExisting, Result(summary, "a").Target!.Kind);
        Assert.Equal("p1", Result(summary, "a").ProjectKey);
        Assert.Null(Result(summary, "dep").ProjectKey);
    }

    // ── 조상 closure ──────────────────────────────────────────────────────────

    [Fact]
    public void 포함한_대화의_조상은_자동_포함되고_동작은_Preview_값을_따른다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("child", RevisionRelation.New, ancestors: ["root", "mid"]), Conv("other", RevisionRelation.New))],
            [Conv("root", RevisionRelation.Identical, selected: false), Conv("mid", RevisionRelation.New, selected: false), Conv("unused", RevisionRelation.New, selected: false)]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("child"));

        Assert.True(Result(summary, "root").IsAutoIncludedAncestor);
        Assert.Equal(ImportPlannedAction.NoOp, Result(summary, "root").FinalAction);
        Assert.Equal(ImportPlannedAction.Import, Result(summary, "mid").FinalAction);
        Assert.Equal(ImportSkipReason.None, Result(summary, "mid").SkipReason);
        Assert.Equal(ImportSkipReason.NotSelectedDependencyNotNeeded, Result(summary, "unused").SkipReason);
        Assert.Equal(2, summary.AutoIncludedAncestorCount);
        Assert.Equal(2, summary.ImportCount);   // child + mid
        Assert.Equal(1, summary.NoOpCount);     // root
        Assert.Equal(2, summary.SkipCount);     // other + unused
        Assert.Equal(1, summary.UncategorizedImportCount); // mid(조상 전용)은 기타 대화로 들어간다. child는 연결.
        Assert.True(summary.CanApply);
    }

    [Fact]
    public void 선택_가능한_대화가_다른_선택_대화의_조상이면_체크하지_않아도_자동_포함된다()
    {
        ImportPreview preview = Preview([Project("p1", Linked, Conv("parent", RevisionRelation.New), Conv("child", RevisionRelation.New, ancestors: ["parent"]))]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("child"));

        Assert.True(Result(summary, "parent").IsAutoIncludedAncestor);
        Assert.False(Result(summary, "parent").IsIncludedByUser);
        Assert.Equal(ImportPlannedAction.Import, Result(summary, "parent").FinalAction);
    }

    // ── 차단 판정 ────────────────────────────────────────────────────────────

    [Fact]
    public void Diverged와_Unverifiable을_빼면_적용할_수_있다()
    {
        ImportPreview preview = Preview([Project("p1", Linked, Conv("n", RevisionRelation.New), Conv("d", RevisionRelation.Diverged), Conv("u", RevisionRelation.Unverifiable))]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, ImportUserChoices.CreateDefault(preview));

        Assert.True(summary.CanApply);
        Assert.False(summary.HasBlockingIssues);
        Assert.False(summary.HasUnresolvedDivergence);
        Assert.Empty(summary.BlockingReasons);
    }

    [Fact]
    public void closure에_Blocked_조상이_있으면_적용_불가이고_사유에_대화와_조상_ID가_있다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("child", RevisionRelation.New, ancestors: ["broken"]), Conv("fine", RevisionRelation.New))],
            [Conv("broken", RevisionRelation.Unverifiable, selected: false)]);

        ImportSelectionSummary blocked = ImportSelection.Compute(preview, Choose("child", "fine"));
        Assert.False(blocked.CanApply);
        Assert.True(blocked.HasBlockingIssues);
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("child") && r.Contains("broken"));
        Assert.Equal(ImportPlannedAction.Blocked, Result(blocked, "broken").FinalAction);

        // 그 자식을 빼면 closure 밖이 되어 막지 않는다.
        ImportSelectionSummary withoutChild = ImportSelection.Compute(preview, Choose("fine"));
        Assert.True(withoutChild.CanApply);
        Assert.Equal(ImportSkipReason.NotSelectedDependencyNotNeeded, Result(withoutChild, "broken").SkipReason);
    }

    [Fact]
    public void closure에_Diverged_조상이_있으면_HasUnresolvedDivergence다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("child", RevisionRelation.New, ancestors: ["div"]))],
            [Conv("div", RevisionRelation.Diverged, selected: false)]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("child"));

        Assert.False(summary.CanApply);
        Assert.True(summary.HasUnresolvedDivergence);
        Assert.False(summary.HasBlockingIssues);
    }

    [Fact]
    public void 백업에_없는_조상과_압축_rollout_이어받기_조상도_적용_불가다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("a", RevisionRelation.New, ancestors: ["ghost"]), Conv("b", RevisionRelation.New, ancestors: ["zst"]))],
            [Conv("zst", RevisionRelation.IncomingAhead, selected: false, compressed: true)]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("a", "b"));

        Assert.False(summary.CanApply);
        Assert.True(summary.HasBlockingIssues);
        Assert.Contains(summary.BlockingReasons, r => r.Contains("ghost"));
        Assert.Contains(summary.BlockingReasons, r => r.Contains("zst"));
    }

    // ── 선택 불가 대화 ────────────────────────────────────────────────────────

    [Fact]
    public void 선택_불가_대화가_Included에_들어와도_무시하고_경고한다()
    {
        ImportPreview preview = Preview(
            [Project("p1", Linked, Conv("n", RevisionRelation.New), Conv("zst", RevisionRelation.IncomingAhead, compressed: true), Conv("d", RevisionRelation.Diverged))],
            [Conv("dep", RevisionRelation.New, selected: false)]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("n", "zst", "d", "dep", "ghost"));

        Assert.Equal(4, summary.Warnings.Count);
        Assert.False(Result(summary, "zst").IsIncludedByUser);
        Assert.Equal(ImportSkipReason.UserExcluded, Result(summary, "d").SkipReason);
        Assert.Equal(ImportSkipReason.NotSelectedDependencyNotNeeded, Result(summary, "dep").SkipReason);
        Assert.True(summary.CanApply); // 경고일 뿐 차단 사유는 아니다
        Assert.Equal(1, summary.ImportCount);
    }

    // ── 요약 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 요약_개수를_센다()
    {
        ImportPreview preview = Preview(
        [
            Project("linked", Linked, Conv("n1", RevisionRelation.New), Conv("u1", RevisionRelation.IncomingAhead), Conv("s1", RevisionRelation.Identical)),
            Project("missing", Missing, Conv("n2", RevisionRelation.New), Conv("n3", RevisionRelation.New)),
            Project(null, NotApplicable, Conv("n4", RevisionRelation.New)),
        ]);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, Choose("n1", "u1", "n2", "n4"));

        Assert.Equal(3, summary.ImportCount);
        Assert.Equal(1, summary.UpdateCount);
        Assert.Equal(0, summary.NoOpCount);
        Assert.Equal(2, summary.SkipCount); // s1(이미 있음), n3(제외)
        Assert.Equal(2, summary.UncategorizedImportCount); // n2(원본 없음), n4(기타 대화 그룹)
        Assert.Equal(ImportNothingToWriteReason.None, summary.NothingToWriteReason);
        Assert.True(summary.CanApply);
        Assert.Equal(ImportUserChoices.UncategorizedProjectKey, Result(summary, "n4").ProjectKey);
    }

    [Fact]
    public void 쓸_것이_0개인_사유_3종()
    {
        ImportPreview selectable = Preview([Project("p", Linked, Conv("n", RevisionRelation.New), Conv("s", RevisionRelation.Identical))]);
        ImportSelectionSummary nothingSelected = ImportSelection.Compute(selectable, Choose());
        Assert.Equal(ImportNothingToWriteReason.NothingSelected, nothingSelected.NothingToWriteReason);
        Assert.False(nothingSelected.CanApply);
        Assert.Empty(nothingSelected.BlockingReasons);

        ImportPreview allPresent = Preview([Project("p", Linked, Conv("s", RevisionRelation.Identical), Conv("l", RevisionRelation.LocalAhead))]);
        Assert.Equal(ImportNothingToWriteReason.AllAlreadyPresent,
            ImportSelection.Compute(allPresent, ImportUserChoices.CreateDefault(allPresent)).NothingToWriteReason);

        ImportPreview noneSelectable = Preview([Project("p", Linked, Conv("s", RevisionRelation.Identical), Conv("d", RevisionRelation.Diverged))]);
        ImportSelectionSummary none = ImportSelection.Compute(noneSelectable, ImportUserChoices.CreateDefault(noneSelectable));
        Assert.Equal(ImportNothingToWriteReason.NoneSelectable, none.NothingToWriteReason);
        Assert.False(none.CanApply);
    }

    // ── 프로젝트 결정 ─────────────────────────────────────────────────────────

    [Fact]
    public void 새_프로젝트가_아닌_목적지에서는_NewProjectName을_쓰지_않는다()
    {
        // Phase 9_5-01: NewProjectName은 이제 정상 값이다(9_2까지는 "적용 불가"였다). 목적지가 새 프로젝트가 아니면 무시한다.
        ImportPreview preview = Preview([Project("p", Missing, Conv("n", RevisionRelation.New))]);
        var choices = new ImportUserChoices(
            new HashSet<string> { "n" },
            new Dictionary<string, ProjectTargetDecision> { ["p"] = new(null, "새 프로젝트", UseSuggestion: true) });

        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);

        Assert.True(summary.CanApply);
        Assert.Empty(summary.BlockingReasons);
        Assert.Equal(Missing, summary.Projects[0].Target);
        Assert.Empty(summary.NewProjects);
    }

    [Fact]
    public void 폴더_결정은_등록이면_UserSelectedRegistered_미등록이면_UserSelectedUnregistered다()
    {
        string registered = Path.Combine(_root, "registered");
        string unregistered = Path.Combine(_root, "unregistered");
        Directory.CreateDirectory(registered);
        Directory.CreateDirectory(unregistered);
        var directory = new ProjectDirectory(
        [
            new KnownProject("db-9", "db-9", new HashSet<string>(), "Nine", [new KnownProjectRoot(registered, CanonicalPath.Create(registered), true)], 0),
        ]);
        ImportProjectPreview project = Project("p", Missing, Conv("n", RevisionRelation.New));

        ProjectTargetResolution toRegistered = ImportSelection.ResolveTarget(project, ProjectTargetDecision.Folder(registered), directory);
        Assert.Null(toRegistered.Error);
        Assert.Equal(ProjectTargetReason.UserSelectedRegistered, toRegistered.Target.Reason);
        Assert.Equal("db-9", toRegistered.Target.LinkDbProjectId);
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, toRegistered.PathStatus);

        ProjectTargetResolution toUnregistered = ImportSelection.ResolveTarget(project, ProjectTargetDecision.Folder(unregistered), directory);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, toUnregistered.Target.Reason);

        ProjectTargetResolution suggested = ImportSelection.ResolveTarget(project, ProjectTargetDecision.Suggested, directory);
        Assert.Equal(Missing, suggested.Target);
        Assert.Equal(ProjectPathMappingStatus.NotFound, suggested.PathStatus);

        // Compute도 같은 판정을 쓴다.
        ImportPreview preview = Preview([project], directory: directory);
        var choices = new ImportUserChoices(
            new HashSet<string> { "n" }, new Dictionary<string, ProjectTargetDecision> { ["p"] = ProjectTargetDecision.Folder(registered) });
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(ProjectTargetKind.LinkExisting, Result(summary, "n").Target!.Kind);
        Assert.Equal(0, summary.UncategorizedImportCount);
    }

    [Fact]
    public void 없는_폴더_지정은_결정_오류이고_기타_대화_그룹에는_폴더를_지정할_수_없다()
    {
        ImportProjectPreview project = Project("p", Missing, Conv("n", RevisionRelation.New));
        ImportProjectPreview uncategorized = Project(null, NotApplicable, Conv("u", RevisionRelation.New));
        string existing = Path.Combine(_root, "exists");
        Directory.CreateDirectory(existing);

        Assert.NotNull(ImportSelection.ResolveTarget(project, ProjectTargetDecision.Folder(Path.Combine(_root, "nope")), ProjectDirectory.Empty).Error);
        Assert.NotNull(ImportSelection.ResolveTarget(project, new ProjectTargetDecision(null, null, UseSuggestion: false), ProjectDirectory.Empty).Error);

        // Phase 9_5-11: 백업의 "기타 대화" 그룹에는 폴더·새 프로젝트 이름을 지정할 수 없다(결정 오류, 목적지는 항상 기타 대화).
        foreach (ProjectTargetDecision decision in new[]
                 {
                     ProjectTargetDecision.Folder(existing),
                     ProjectTargetDecision.Suggested with { NewProjectName = "이름" },
                 })
        {
            ProjectTargetResolution rejected = ImportSelection.ResolveTarget(uncategorized, decision, ProjectDirectory.Empty);
            Assert.Equal(ImportSelection.UncategorizedGroupDecisionError, rejected.Error);
            Assert.Equal(ProjectTarget.Uncategorized(ProjectTargetReason.NotApplicable, null), rejected.Target);
        }

        ProjectTargetResolution suggested = ImportSelection.ResolveTarget(uncategorized, ProjectTargetDecision.Suggested, ProjectDirectory.Empty);
        Assert.Null(suggested.Error);
        Assert.Equal(ProjectTargetKind.Uncategorized, suggested.Target.Kind);
        Assert.Equal(ProjectTargetReason.NotApplicable, suggested.Target.Reason);
    }
}
