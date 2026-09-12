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

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "solo" });

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

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "child" });

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

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "child1", "child2" });

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

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "selected" });

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

        ExportPlan plan = ExportPlanBuilder.Build(MakeCatalog(chains), new HashSet<string> { "t1", "t2" });

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
}
