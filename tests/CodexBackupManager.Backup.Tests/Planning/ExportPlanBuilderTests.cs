using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Planning;

/// <summary>
/// <see cref="ExportPlanBuilder"/>가 실제 rollout 파일(합성 fixture, 실제 사용자 데이터 아님)에 대해
/// dependency closure/dedupe/attachment 정책을 정확히 지키는지 확인한다.
/// </summary>
public sealed class ExportPlanBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-backup-tests", Guid.NewGuid().ToString("N"));

    public ExportPlanBuilderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string EventMsgLine(long ordinal, string itemType, string itemId, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-1\"," +
           "\"item\":{\"type\":\"" + itemType + "\",\"id\":\"" + itemId + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private static string LocalImageLine(long ordinal, string itemId, string path)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-1\"," +
           "\"item\":{\"type\":\"UserMessage\",\"id\":\"" + itemId + "\",\"content\":[{\"type\":\"local_image\",\"path\":\"" +
           path.Replace("\\", "\\\\") + "\"}]}}}";

    private string WriteRolloutFile(string fileName, IEnumerable<string> lines)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private RolloutFileReference RolloutRef(string threadId, string? segmentId, DateTimeOffset timestamp, string fullPath)
        => new(fullPath, Path.GetFileName(fullPath), threadId, segmentId, timestamp, IsArchived: false, RolloutFileKind.PlainJsonl);

    private static ThreadChain MakeChain(
        string threadId,
        IReadOnlyList<RolloutFileReference> files,
        string? parentThreadId = null,
        long? parentEndOrdinalExclusive = null,
        IReadOnlyList<HistoryBaseReference?>? fileHistoryBases = null)
        => new(threadId, files, fileHistoryBases ?? new HistoryBaseReference?[files.Count], parentThreadId, parentEndOrdinalExclusive, null, []);

    /// <summary>
    /// 선택 가능한 최소 metadata를 가진 <see cref="ConversationEntry"/>를 만든다. 실제 UI 흐름에서는
    /// 선택 목록 자체가 <c>catalog.Projects</c>(=state DB 행이 있는 thread만)에서만 채워지므로,
    /// "선택했지만 metadata가 없는" 상황은 이론상 발생하지 않는다 — 그래도 방어적으로
    /// <see cref="ExportPlanBuilder"/>가 이를 fatal로 처리하므로, 이 헬퍼 없이 만든 테스트는
    /// 일부러 그 fatal 경로를 확인하는 테스트뿐이어야 한다.
    /// </summary>
    private static ConversationEntry MakeEntry(string threadId) => new()
    {
        ThreadId = threadId,
        Row = new ThreadRow { Id = threadId },
        Title = new Domain.Codex.Titles.ThreadTitle(threadId, Domain.Codex.Titles.ThreadTitleSource.StateTitle),
        Project = new Domain.Codex.Projects.ProjectAssignment(null, Domain.Codex.Projects.ProjectAssignmentSource.Unassigned),
    };

    private static CodexCatalog MakeCatalog(
        IReadOnlyDictionary<string, ThreadChain> chains,
        IReadOnlyList<ConversationEntry>? allConversations = null,
        IReadOnlyList<ProjectEntry>? projects = null)
        => new(
            projects ?? [],
            allConversations ?? [],
            chains,
            [],
            DateTimeOffset.UtcNow,
            new CodexCatalogStats(chains.Count, allConversations?.Count ?? 0, projects?.Count ?? 0, TimeSpan.Zero, TimeSpan.Zero));

    [Fact]
    public void 조상_없는_선택_대화_1개는_자기_파일만_포함한다()
    {
        string path = WriteRolloutFile("solo.jsonl", [EventMsgLine(0, "UserMessage", "u1", "hello")]);
        RolloutFileReference file = RolloutRef("solo", null, DateTimeOffset.UnixEpoch, path);
        var chains = new Dictionary<string, ThreadChain> { ["solo"] = MakeChain("solo", [file]) };

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains, [MakeEntry("solo")]), new HashSet<string> { "solo" });

        Assert.Equal(1, plan.SelectedConversationCount);
        Assert.Equal(0, plan.DependencyConversationCount);
        PlannedConversation conversation = Assert.Single(plan.Conversations);
        Assert.True(conversation.IsSelected);
        RolloutFileReference onlyFile = Assert.Single(conversation.RolloutFiles);
        Assert.Equal(path, onlyFile.FullPath);
        Assert.Single(plan.RolloutFiles);
    }

    [Fact]
    public void 조상이_중간_세그먼트를_가리키면_그_이후_세그먼트는_dependency에서_제외한다()
    {
        string parentSeg1Path = WriteRolloutFile("parent-seg1.jsonl", [EventMsgLine(0, "AgentMessage", "a1", "seg1")]);
        string parentSeg2Path = WriteRolloutFile("parent-seg2.jsonl", [EventMsgLine(0, "AgentMessage", "a2", "seg2")]);
        string childPath = WriteRolloutFile("child.jsonl", [EventMsgLine(0, "UserMessage", "u1", "child")]);

        RolloutFileReference parentSeg1 = RolloutRef("parent", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), parentSeg1Path);
        RolloutFileReference parentSeg2 = RolloutRef("parent", "seg2", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), parentSeg2Path);
        RolloutFileReference childFile = RolloutRef("child", null, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), childPath);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [parentSeg1, parentSeg2]),
            ["child"] = MakeChain("child", [childFile], parentThreadId: parentSeg1.OwnRolloutId, parentEndOrdinalExclusive: 5),
        };

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains, [MakeEntry("child")]), new HashSet<string> { "child" });

        Assert.Empty(plan.FatalErrors);
        Assert.Equal(1, plan.SelectedConversationCount);
        Assert.Equal(1, plan.DependencyConversationCount);

        PlannedConversation parentPlan = Assert.Single(plan.Conversations, c => c.ThreadId == "parent");
        Assert.False(parentPlan.IsSelected);
        RolloutFileReference parentOnlyFile = Assert.Single(parentPlan.RolloutFiles);
        Assert.Equal(parentSeg1Path, parentOnlyFile.FullPath); // seg2는 제외돼야 한다.

        Assert.Equal(2, plan.RolloutFiles.Count); // parentSeg1 + child (parentSeg2는 포함 안 됨).
        Assert.DoesNotContain(plan.RolloutFiles, f => f.SourceFullPath == parentSeg2Path);
    }

    [Fact]
    public void 두_선택_대화가_같은_조상을_공유하면_조상_payload는_한_번만_포함한다()
    {
        string parentPath = WriteRolloutFile("parent.jsonl", [EventMsgLine(0, "AgentMessage", "a1", "shared ancestor")]);
        string child1Path = WriteRolloutFile("child1.jsonl", [EventMsgLine(0, "UserMessage", "u1", "child1")]);
        string child2Path = WriteRolloutFile("child2.jsonl", [EventMsgLine(0, "UserMessage", "u2", "child2")]);

        RolloutFileReference parentFile = RolloutRef("parent", null, DateTimeOffset.UnixEpoch, parentPath);
        RolloutFileReference child1File = RolloutRef("child1", null, DateTimeOffset.UnixEpoch.AddDays(1), child1Path);
        RolloutFileReference child2File = RolloutRef("child2", null, DateTimeOffset.UnixEpoch.AddDays(2), child2Path);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [parentFile]),
            ["child1"] = MakeChain("child1", [child1File], parentThreadId: parentFile.OwnRolloutId),
            ["child2"] = MakeChain("child2", [child2File], parentThreadId: parentFile.OwnRolloutId),
        };

        ExportPlan plan = ExportPlanBuilder.Build(
            MakeCatalog(chains, [MakeEntry("child1"), MakeEntry("child2")]),
            new HashSet<string> { "child1", "child2" });

        Assert.Empty(plan.FatalErrors);
        Assert.Equal(2, plan.SelectedConversationCount);
        Assert.Equal(1, plan.DependencyConversationCount); // parent는 한 번만 dependency로 집계된다.
        Assert.Equal(3, plan.RolloutFiles.Count); // parent + child1 + child2, 중복 없음.
        Assert.Single(plan.RolloutFiles, f => f.SourceFullPath == parentPath);
    }

    [Fact]
    public void 선택되지_않은_대화는_독립적으로_포함되지_않는다()
    {
        string selectedPath = WriteRolloutFile("selected.jsonl", [EventMsgLine(0, "UserMessage", "u1", "selected")]);
        string otherPath = WriteRolloutFile("other.jsonl", [EventMsgLine(0, "UserMessage", "u2", "other")]);

        RolloutFileReference selectedFile = RolloutRef("selected", null, DateTimeOffset.UnixEpoch, selectedPath);
        RolloutFileReference otherFile = RolloutRef("other", null, DateTimeOffset.UnixEpoch, otherPath);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["selected"] = MakeChain("selected", [selectedFile]),
            ["other"] = MakeChain("other", [otherFile]), // 조상 관계 없음, snapshot에도 없음.
        };

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains, [MakeEntry("selected")]), new HashSet<string> { "selected" });

        Assert.Empty(plan.FatalErrors);
        Assert.Single(plan.Conversations);
        Assert.DoesNotContain(plan.Conversations, c => c.ThreadId == "other");
        Assert.DoesNotContain(plan.RolloutFiles, f => f.SourceFullPath == otherPath);
    }

    [Fact]
    public void 실존하는_첨부는_dedupe해서_포함하고_없는_첨부는_경고만_남긴다()
    {
        string sharedImagePath = Path.Combine(_directory, "shared.png");
        File.WriteAllBytes(sharedImagePath, [1, 2, 3, 4]);
        string missingImagePath = Path.Combine(_directory, "does-not-exist.png");

        string thread1Path = WriteRolloutFile("t1.jsonl",
            [LocalImageLine(0, "img1", sharedImagePath), LocalImageLine(1, "img2", missingImagePath)]);
        string thread2Path = WriteRolloutFile("t2.jsonl", [LocalImageLine(0, "img3", sharedImagePath)]);

        RolloutFileReference file1 = RolloutRef("t1", null, DateTimeOffset.UnixEpoch, thread1Path);
        RolloutFileReference file2 = RolloutRef("t2", null, DateTimeOffset.UnixEpoch.AddDays(1), thread2Path);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["t1"] = MakeChain("t1", [file1]),
            ["t2"] = MakeChain("t2", [file2]),
        };

        ExportPlan plan = ExportPlanBuilder.Build(
            MakeCatalog(chains, [MakeEntry("t1"), MakeEntry("t2")]),
            new HashSet<string> { "t1", "t2" });

        Assert.Empty(plan.FatalErrors);
        Assert.Single(plan.Attachments); // shared.png 하나만(두 thread가 같은 파일을 공유 — dedupe).
        Assert.Equal(sharedImagePath, plan.Attachments[0].SourceFullPath);
        Assert.Contains(plan.Warnings, w => w.Contains('1') && w.Contains("찾을 수 없"));
    }

    [Fact]
    public void 선택된_대화가_속한_프로젝트만_계획에_포함한다()
    {
        string selectedPath = WriteRolloutFile("selected.jsonl", [EventMsgLine(0, "UserMessage", "u1", "x")]);
        string unrelatedPath = WriteRolloutFile("unrelated.jsonl", [EventMsgLine(0, "UserMessage", "u2", "y")]);

        RolloutFileReference selectedFile = RolloutRef("selected", null, DateTimeOffset.UnixEpoch, selectedPath);
        RolloutFileReference unrelatedFile = RolloutRef("unrelated", null, DateTimeOffset.UnixEpoch, unrelatedPath);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["selected"] = MakeChain("selected", [selectedFile]),
            ["unrelated"] = MakeChain("unrelated", [unrelatedFile]),
        };

        var selectedEntry = new ConversationEntry
        {
            ThreadId = "selected",
            Row = new ThreadRow { Id = "selected" },
            Title = new Domain.Codex.Titles.ThreadTitle("Selected", Domain.Codex.Titles.ThreadTitleSource.StateTitle),
            Project = new Domain.Codex.Projects.ProjectAssignment("proj-a", Domain.Codex.Projects.ProjectAssignmentSource.StateProjectId),
        };
        var unrelatedEntry = new ConversationEntry
        {
            ThreadId = "unrelated",
            Row = new ThreadRow { Id = "unrelated" },
            Title = new Domain.Codex.Titles.ThreadTitle("Unrelated", Domain.Codex.Titles.ThreadTitleSource.StateTitle),
            Project = new Domain.Codex.Projects.ProjectAssignment("proj-b", Domain.Codex.Projects.ProjectAssignmentSource.StateProjectId),
        };

        var projects = new List<ProjectEntry>
        {
            new("proj-a", "Project A", [], [selectedEntry]),
            new("proj-b", "Project B", [], [unrelatedEntry]),
        };

        ExportPlan plan = ExportPlanBuilder.Build(
            MakeCatalog(chains, [selectedEntry, unrelatedEntry], projects),
            new HashSet<string> { "selected" });

        PlannedProject onlyProject = Assert.Single(plan.Projects);
        Assert.Equal("proj-a", onlyProject.ProjectId);
        Assert.Equal(["selected"], onlyProject.SelectedThreadIds);
    }

    // ── Phase 05_01 hardening: 선택 대화를 완전하게 백업할 수 없으면 FAIL(warning으로 넘어가지 않는다) ──

    [Fact]
    public void 선택한_대화_중_하나의_체인이_없으면_FatalError를_남긴다()
    {
        string okPath = WriteRolloutFile("ok.jsonl", [EventMsgLine(0, "UserMessage", "u1", "ok")]);
        RolloutFileReference okFile = RolloutRef("ok", null, DateTimeOffset.UnixEpoch, okPath);
        var chains = new Dictionary<string, ThreadChain> { ["ok"] = MakeChain("ok", [okFile]) };

        ExportPlan plan = ExportPlanBuilder.Build(
            MakeCatalog(chains, [MakeEntry("ok")]),
            new HashSet<string> { "ok", "missing-chain" }); // "missing-chain"은 chains에 아예 없다.

        Assert.NotEmpty(plan.FatalErrors);
        Assert.Contains(plan.FatalErrors, e => e.Contains("rollout 파일 체인을 찾을 수 없"));
    }

    [Fact]
    public void 선택한_대화의_metadata가_없으면_FatalError를_남긴다()
    {
        string path = WriteRolloutFile("orphan.jsonl", [EventMsgLine(0, "UserMessage", "u1", "orphan")]);
        RolloutFileReference file = RolloutRef("orphan", null, DateTimeOffset.UnixEpoch, path);
        var chains = new Dictionary<string, ThreadChain> { ["orphan"] = MakeChain("orphan", [file]) };

        // 일부러 MakeEntry를 넘기지 않는다 — chain은 있지만 state DB 행(metadata)이 없는 고아 thread.
        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "orphan" });

        Assert.NotEmpty(plan.FatalErrors);
        Assert.Contains(plan.FatalErrors, e => e.Contains("metadata를 찾을 수 없"));
    }

    [Fact]
    public void 조상_rollout_파일을_찾을_수_없으면_FatalError를_남긴다()
    {
        string childPath = WriteRolloutFile("child.jsonl", [EventMsgLine(0, "UserMessage", "u1", "child")]);
        RolloutFileReference childFile = RolloutRef("child", null, DateTimeOffset.UnixEpoch, childPath);

        // "child"가 "missing-parent-rollout-id"를 조상으로 가리키지만, 그 rollout ID를 가진 파일이
        // 어느 체인에도 없다 — 완전한 dependency closure를 만들 수 없는 상황.
        var chains = new Dictionary<string, ThreadChain>
        {
            ["child"] = MakeChain("child", [childFile], parentThreadId: "missing-parent-rollout-id"),
        };

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains, [MakeEntry("child")]), new HashSet<string> { "child" });

        Assert.NotEmpty(plan.FatalErrors);
        Assert.Contains(plan.FatalErrors, e => e.Contains("조상 rollout 파일"));
    }

    [Fact]
    public void 순환_참조가_있으면_FatalError를_남긴다()
    {
        RolloutFileReference fileA = RolloutRef("a", null, DateTimeOffset.UnixEpoch, WriteRolloutFile("a.jsonl", [EventMsgLine(0, "UserMessage", "u1", "a")]));
        RolloutFileReference fileB = RolloutRef("b", null, DateTimeOffset.UnixEpoch, WriteRolloutFile("b.jsonl", [EventMsgLine(0, "UserMessage", "u2", "b")]));

        var chains = new Dictionary<string, ThreadChain>
        {
            ["a"] = MakeChain("a", [fileA], parentThreadId: "b"),
            ["b"] = MakeChain("b", [fileB], parentThreadId: "a"),
        };

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains, [MakeEntry("a")]), new HashSet<string> { "a" });

        Assert.NotEmpty(plan.FatalErrors);
        Assert.Contains(plan.FatalErrors, e => e.Contains("순환 참조"));
    }
}
