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

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 06_01 요구사항 4 — <see cref="ImportPlanBuilder"/>가 <see cref="ImportPreview"/>를
/// Phase 7이 그대로 받아 적용할 수 있는 <see cref="ImportPlan"/>으로 정확히 freeze하는지 확인한다.
/// 여기서 실제 Apply는 절대 하지 않는다 — Plan을 "만드는 것"까지만 검증한다.
/// </summary>
public sealed class ImportPlanBuilderTests : IDisposable
{
    private readonly string _pcADir = Path.Combine(Path.GetTempPath(), "cbm-import-plan-tests", Guid.NewGuid().ToString("N"), "pcA");
    private readonly string _pcBDir = Path.Combine(Path.GetTempPath(), "cbm-import-plan-tests", Guid.NewGuid().ToString("N"), "pcB");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "cbm-import-plan-tests", Guid.NewGuid().ToString("N"), "out");

    public ImportPlanBuilderTests()
    {
        Directory.CreateDirectory(_pcADir);
        Directory.CreateDirectory(_pcBDir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        TryDelete(Path.GetDirectoryName(_pcADir)!);
        TryDelete(Path.GetDirectoryName(_pcBDir)!);
        TryDelete(Path.GetDirectoryName(_outDir)!);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string NewId() => Guid.NewGuid().ToString();

    private static string Line(long ordinal, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
           "\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private static string WriteRollout(string dir, string threadId, string? segmentId, params string[] lines)
    {
        string fileName = segmentId is null
            ? $"rollout-2026-01-02T03-04-05-{threadId}.jsonl"
            : $"rollout-2026-01-02T03-04-05-{threadId}_{segmentId}.jsonl";
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static RolloutFileReference Ref(string threadId, string? segmentId, string fullPath)
        => new(fullPath, Path.GetFileName(fullPath), threadId, segmentId, DateTimeOffset.UnixEpoch, IsArchived: false, RolloutFileKind.PlainJsonl);

    private static ThreadChain Chain(string threadId, IReadOnlyList<RolloutFileReference> files, string? parentThreadId = null)
        => new(threadId, files, new HistoryBaseReference?[files.Count], parentThreadId, null, null, []);

    private static ConversationEntry MakeEntry(string threadId, string? projectId = null) => new()
    {
        ThreadId = threadId,
        Row = new ThreadRow { Id = threadId, ProjectId = projectId },
        Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
        Project = new ProjectAssignment(projectId, projectId is null ? ProjectAssignmentSource.Unassigned : ProjectAssignmentSource.StateProjectId),
    };

    private string ExportToBackup(CodexCatalog catalog, IReadOnlySet<string> selectedThreadIds, string fileName)
    {
        ExportPlan plan = ExportPlanBuilder.Build(catalog, selectedThreadIds);
        Assert.Empty(plan.FatalErrors);

        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, fileName);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    private static CodexCatalog EmptyCatalog() => new(
        [], [], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow,
        new CodexCatalogStats(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero));

    [Fact]
    public void 검증에_실패한_Preview는_Plan을_만들지_않는다()
    {
        string path = Path.Combine(_outDir, "broken.codexbackup");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);
        ImportPreview preview = ImportPreviewBuilder.Build(path, EmptyCatalog());

        ImportPlan? plan = ImportPlanBuilder.Build(preview, path);

        Assert.Null(plan);
    }

    [Fact]
    public void New_대화만_있으면_ApplyReady이고_BackupIdentity가_manifest와_일치한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        ConversationEntry entry = MakeEntry(t, projectId: "proj-a");
        var pcA = new CodexCatalog(
            [new ProjectEntry("proj-a", "Project A", [@"C:\Root"], [entry])],
            [entry],
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 1, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "plan-new.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.NotNull(plan);
        Assert.True(plan!.IsApplyReady);
        Assert.False(plan.HasBlockingIssues);
        Assert.False(plan.HasUnresolvedDivergence);
        Assert.Equal(backupPath, plan.Backup.BackupFilePath);
        Assert.Equal(preview.Manifest!.CreatedAtUtc, plan.Backup.CreatedAtUtc);
        Assert.Equal(preview.Manifest.AppVersion, plan.Backup.AppVersion);
        Assert.Equal(preview.Manifest.ConversationCount, plan.Backup.TotalConversationCount);

        ImportPlanConversation conversation = Assert.Single(plan.Conversations);
        Assert.Equal(t, conversation.ThreadId);
        Assert.Equal(RevisionRelation.New, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Import, conversation.PlannedAction);
    }

    [Fact]
    public void Diverged가_있으면_Plan은_만들어지되_ApplyReady가_아니다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "incoming-branch"));
        var pcA = new CodexCatalog(
            [], [MakeEntry(t)],
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "plan-diverged.codexbackup");

        string localFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"), Line(1, "local-branch"));
        var localCatalog = new CodexCatalog(
            [], [MakeEntry(t)],
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, localFile)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, localCatalog);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.NotNull(plan);
        Assert.True(plan!.HasUnresolvedDivergence);
        Assert.False(plan.HasBlockingIssues);
        Assert.False(plan.IsApplyReady);

        ImportPlanConversation conversation = Assert.Single(plan.Conversations);
        Assert.Equal(RevisionRelation.Diverged, conversation.Relation);
        Assert.Equal(ImportPlannedAction.RequiresDecision, conversation.PlannedAction);
    }

    [Fact]
    public void Unverifiable가_있으면_항상_Blocking이라_ApplyReady가_아니다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        var pcA = new CodexCatalog(
            [], [MakeEntry(t)],
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "plan-unverifiable.codexbackup");

        // 로컬에 metadata는 있지만 chain은 없는 손상 상태(Phase 06_01 정책) → Unverifiable.
        var localCatalog = new CodexCatalog(
            [], [MakeEntry(t)], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(0, 1, 0, TimeSpan.Zero, TimeSpan.Zero));

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, localCatalog);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.NotNull(plan);
        Assert.True(plan!.HasBlockingIssues);
        Assert.False(plan.IsApplyReady);

        ImportPlanConversation conversation = Assert.Single(plan.Conversations);
        Assert.Equal(RevisionRelation.Unverifiable, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Blocked, conversation.PlannedAction);
    }

    [Fact]
    public void TargetProjectPath는_프로젝트에_속한_대화에만_채워지고_dependency_only는_null이다()
    {
        string parentId = NewId();
        string childId = NewId();
        string parentFile = WriteRollout(_pcADir, parentId, null, Line(0, "parent"));
        string childFile = WriteRollout(_pcADir, childId, null, Line(0, "child"));
        RolloutFileReference parentRef = Ref(parentId, null, parentFile);

        ConversationEntry childEntry = MakeEntry(childId, projectId: "proj-a");
        var pcA = new CodexCatalog(
            [new ProjectEntry("proj-a", "Project A", [@"C:\Root"], [childEntry])],
            [MakeEntry(parentId), childEntry],
            new Dictionary<string, ThreadChain>
            {
                [parentId] = Chain(parentId, [parentRef]),
                [childId] = Chain(childId, [Ref(childId, null, childFile)], parentThreadId: parentRef.OwnRolloutId),
            },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(2, 2, 1, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { childId }, "plan-target-path.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        string overridePath = Path.Combine(_outDir, "resolved-target");
        Directory.CreateDirectory(overridePath);
        ImportPreview withPath = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "proj-a", overridePath);

        ImportPlan? plan = ImportPlanBuilder.Build(withPath, backupPath);

        Assert.NotNull(plan);
        ImportPlanConversation childPlan = Assert.Single(plan!.Conversations, c => c.ThreadId == childId);
        Assert.NotNull(childPlan.TargetProjectPath);
        Assert.True(childPlan.IsSelected);

        ImportPlanConversation parentPlan = Assert.Single(plan.Conversations, c => c.ThreadId == parentId);
        Assert.Null(parentPlan.TargetProjectPath);
        Assert.False(parentPlan.IsSelected);
    }
}
