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
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_1-07 / 9_1-T9 — <see cref="ImportPreviewBuilder"/>가 목적지(<see cref="ImportProjectPreview.SuggestedTarget"/>)와
/// 대화별 부가 정보(<see cref="ImportConversationPreview.LocalLocation"/>, <see cref="ImportConversationPreview.IsCompressedRollout"/>,
/// <see cref="ImportConversationPreview.RequiredAncestorThreadIds"/>)를 채우는지. 진짜 Export 파이프라인으로 만든 백업을 쓴다.
/// </summary>
public sealed class ImportPreviewProjectTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-preview-target-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _outDir;

    public ImportPreviewProjectTargetTests()
    {
        _pcADir = Path.Combine(_root, "pcA");
        _outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_pcADir);
        Directory.CreateDirectory(_outDir);
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

    private static string NewId() => Guid.NewGuid().ToString();

    private static string Line(long ordinal, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
           "\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    /// <summary>
    /// 백업 쪽 lineage는 파일 내용(<c>session_meta.history_base</c>)으로 다시 만든다 — 분기 자식은 이 줄이 있어야 한다.
    /// </summary>
    private static string SessionMeta(string threadId, string? parentRolloutId)
    {
        string historyBase = parentRolloutId is null
            ? string.Empty
            : ",\"history_base\":{\"thread_id\":\"" + parentRolloutId + "\",\"end_ordinal_exclusive\":2,\"end_byte_offset\":1}";
        return "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{\"id\":\"" + threadId +
               "\",\"session_id\":\"" + threadId + "\",\"timestamp\":\"2026-01-02T03:04:05.000Z\"" + historyBase + "}}";
    }

    private string WriteRollout(string threadId, string text, string? parentRolloutId = null)
    {
        string path = Path.Combine(_pcADir, $"rollout-2026-01-02T03-04-05-{threadId}.jsonl");
        File.WriteAllText(path, SessionMeta(threadId, parentRolloutId) + "\n" + Line(1, text) + "\n");
        return path;
    }

    private string WriteZstRollout(string threadId, string text)
    {
        string path = Path.Combine(_pcADir, $"rollout-2026-01-02T03-04-05-{threadId}.jsonl.zst");
        using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
        using ZstdSharp.CompressionStream compression = new(fileStream, leaveOpen: true);
        using StreamWriter writer = new(compression);
        writer.Write(SessionMeta(threadId, null) + "\n" + Line(1, text) + "\n");
        return path;
    }

    private static RolloutFileReference Ref(string threadId, string path, RolloutFileKind kind = RolloutFileKind.PlainJsonl)
        => new(path, Path.GetFileName(path), threadId, null, DateTimeOffset.UnixEpoch, IsArchived: false, kind);

    private static ThreadChain Chain(string threadId, RolloutFileReference file, string? parentRolloutId = null)
        => new(threadId, [file], new HistoryBaseReference?[1], parentRolloutId, parentRolloutId is null ? null : 1, null, []);

    private static ConversationEntry Entry(string threadId, string? projectId, bool archived = false) => new()
    {
        ThreadId = threadId,
        Row = new ThreadRow { Id = threadId, ProjectId = projectId, ThreadSource = "user", Archived = archived },
        Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
        Project = new ProjectAssignment(projectId, projectId is null ? ProjectAssignmentSource.Unassigned : ProjectAssignmentSource.StateProjectId),
    };

    private static CodexCatalog Catalog(
        IReadOnlyList<ProjectEntry> projects,
        IReadOnlyList<ConversationEntry> conversations,
        IReadOnlyDictionary<string, ThreadChain> chains,
        ProjectDirectory? directory = null)
        => new(projects, conversations, chains, [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(chains.Count, conversations.Count, projects.Count, TimeSpan.Zero, TimeSpan.Zero))
        {
            ProjectDirectory = directory ?? ProjectDirectory.Empty,
        };

    private static KnownProject Db(string id, string name, string root, params string[] legacyIds)
        => new(id, id, new HashSet<string>(legacyIds), name, [new KnownProjectRoot(root, CanonicalPath.Create(root), true)], 0);

    private string Export(CodexCatalog pcA, IReadOnlySet<string> selected)
    {
        ExportPlan plan = ExportPlanBuilder.Build(pcA, selected);
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, $"{Guid.NewGuid():N}.codexbackup");
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    /// <summary>PC A: 프로젝트 <paramref name="sourceProjectId"/>(원본 루트 <paramref name="originalRoot"/>)의 대화 1개.</summary>
    private string ExportOneProjectConversation(string threadId, string sourceProjectId, string originalRoot)
    {
        ConversationEntry entry = Entry(threadId, sourceProjectId);
        CodexCatalog pcA = Catalog(
            [new ProjectEntry(sourceProjectId, "X", [originalRoot], [entry])],
            [entry],
            new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, Ref(threadId, WriteRollout(threadId, "hi"))) });
        return Export(pcA, new HashSet<string> { threadId });
    }

    private string NewFolder(string name)
    {
        string path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ── SuggestedTarget / 호환 매핑 ───────────────────────────────────────────

    [Fact]
    public void 대화_0개_등록_프로젝트로_자동_연결되는_목적지와_호환_매핑을_채운다()
    {
        string root = NewFolder("x-root");
        string threadId = NewId();
        string backupPath = ExportOneProjectConversation(threadId, "pcA-x", root);
        var directory = new ProjectDirectory([Db("db-x", "X on B", root)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, Catalog([], [], new Dictionary<string, ThreadChain>(), directory));

        ImportProjectPreview project = Assert.Single(preview.Projects);
        Assert.Equal(ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, root, "db-x"), project.SuggestedTarget);
        Assert.Equal(ProjectPathMappingStatus.AutoLinked, project.PathMapping.Status);
        Assert.Equal("db-x", project.PathMapping.LinkedLocalProjectId);
        Assert.Equal(root, project.PathMapping.ResolvedLocalPath);
        Assert.Same(directory, preview.LocalProjectDirectory);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        ImportPlanProject planProject = Assert.Single(plan!.Projects);
        Assert.Equal(project.SuggestedTarget, planProject.ResolvedTarget); // 그대로 freeze
        Assert.Equal(root, planProject.TargetProjectPath);                 // 호환용 값도 계속 채운다
    }

    [Fact]
    public void 수동_재지정은_ManuallyLinked를_유지하고_실제_연결_여부는_SuggestedTarget이_말한다()
    {
        string registered = NewFolder("registered");
        string unregistered = NewFolder("unregistered");
        string threadId = NewId();
        string backupPath = ExportOneProjectConversation(threadId, "pcA-x", Path.Combine(_root, "orig-missing"));
        var directory = new ProjectDirectory([Db("db-r", "R", registered)]);
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, Catalog([], [], new Dictionary<string, ThreadChain>(), directory));
        Assert.Equal(ProjectTargetReason.OriginalRootMissing, preview.Projects[0].SuggestedTarget!.Reason);
        Assert.Equal(ProjectPathMappingStatus.NotFound, preview.Projects[0].PathMapping.Status);

        ImportProjectPreview toRegistered = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "pcA-x", registered).Projects[0];
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, toRegistered.PathMapping.Status);
        Assert.Equal(ProjectTargetKind.LinkExisting, toRegistered.SuggestedTarget!.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedRegistered, toRegistered.SuggestedTarget.Reason);
        Assert.Equal("db-r", toRegistered.SuggestedTarget.LinkDbProjectId);

        ImportProjectPreview toUnregistered = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "pcA-x", unregistered).Projects[0];
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, toUnregistered.PathMapping.Status);
        Assert.Equal(ProjectTargetKind.Uncategorized, toUnregistered.SuggestedTarget!.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, toUnregistered.SuggestedTarget.Reason);
    }

    // ── 9_1-T9 구버전 백업(manifest 프로젝트 ID가 레거시 ID) ─────────────────────

    [Fact]
    public void T9_manifest_프로젝트_ID가_레거시_ID인_구버전_백업도_루트_기준으로_연결된다()
    {
        string root = NewFolder("t9-root");
        string threadId = NewId();

        // 9_1a 이전 Export: 그룹 ID와 대화 resolvedProjectId가 모두 PC A의 레거시 ID였다.
        string backupPath = ExportOneProjectConversation(threadId, "0e4a4695-legacy-on-pc-a", root);

        // PC B: 같은 폴더가 전혀 다른 DB ID로 등록돼 있다(ID는 PC마다 다르다).
        var directory = new ProjectDirectory([Db("db-on-pc-b", "X on B", root, "other-legacy-on-pc-b")]);
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, Catalog([], [], new Dictionary<string, ThreadChain>(), directory));

        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        Assert.Equal("0e4a4695-legacy-on-pc-a", preview.Manifest!.Projects[0].ProjectId);
        ImportProjectPreview project = Assert.Single(preview.Projects);
        Assert.Equal(ProjectTargetKind.LinkExisting, project.SuggestedTarget!.Kind);
        Assert.Equal("db-on-pc-b", project.SuggestedTarget.LinkDbProjectId);
        Assert.Equal(ProjectPathMappingStatus.AutoLinked, project.PathMapping.Status);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.True(plan!.IsApplyReady);
    }

    // ── LocalLocation ─────────────────────────────────────────────────────────

    [Fact]
    public void LocalLocation은_이_PC에서_속한_그룹을_KnownProject_Key로_정규화해_알려준다()
    {
        string root = NewFolder("loc-root");
        string inProject = NewId();
        string inUncategorized = NewId();
        string notPresent = NewId();

        ConversationEntry a = Entry(inProject, "pcA-x");
        ConversationEntry b = Entry(inUncategorized, "pcA-x");
        ConversationEntry c = Entry(notPresent, "pcA-x");
        var chainsA = new Dictionary<string, ThreadChain>
        {
            [inProject] = Chain(inProject, Ref(inProject, WriteRollout(inProject, "a"))),
            [inUncategorized] = Chain(inUncategorized, Ref(inUncategorized, WriteRollout(inUncategorized, "b"))),
            [notPresent] = Chain(notPresent, Ref(notPresent, WriteRollout(notPresent, "c"))),
        };
        string backupPath = Export(
            Catalog([new ProjectEntry("pcA-x", "X", [root], [a, b, c])], [a, b, c], chainsA),
            new HashSet<string> { inProject, inUncategorized, notPresent });

        // PC B: inProject는 레거시 ID로 배정돼 있고(그룹은 DB 프로젝트로 정규화됨), inUncategorized는 기타 대화, archived.
        ConversationEntry localA = Entry(inProject, "leg-b");
        ConversationEntry localB = Entry(inUncategorized, null, archived: true);
        var directory = new ProjectDirectory([Db("db-b", "B Project", root, "leg-b")]);
        CodexCatalog pcB = Catalog(
            [new ProjectEntry("db-b", "B Project", [root], [localA]), new ProjectEntry(null, "기타 대화", [], [localB])],
            [localA, localB],
            new Dictionary<string, ThreadChain>
            {
                [inProject] = chainsA[inProject],
                [inUncategorized] = chainsA[inUncategorized],
            },
            directory);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);
        Dictionary<string, ImportConversationPreview> byId = preview.Projects.SelectMany(p => p.Conversations).ToDictionary(x => x.ThreadId);

        Assert.Equal(new ConversationLocalLocation(true, "db-b", "B Project", false), byId[inProject].LocalLocation);
        Assert.Equal(new ConversationLocalLocation(true, null, null, true), byId[inUncategorized].LocalLocation);
        Assert.True(byId[inUncategorized].LocalLocation!.IsUncategorized);
        Assert.Equal(ConversationLocalLocation.NotPresent, byId[notPresent].LocalLocation);

        // 원시 ID 비교(frozen)는 그대로 "다름"이다 — 화면은 이 값 대신 LocalLocation을 쓴다(9_1-10).
        Assert.True(byId[inProject].Metadata.ProjectAssignmentDiffers);
    }

    // ── IsCompressedRollout / RequiredAncestorThreadIds ───────────────────────

    [Fact]
    public void 압축_rollout과_분기_조상을_알려준다()
    {
        string parentId = NewId();
        string childId = NewId();
        string plainId = NewId();
        RolloutFileReference parentRef = Ref(parentId, WriteZstRollout(parentId, "parent"), RolloutFileKind.ZstdCompressed);
        ConversationEntry parent = Entry(parentId, null);
        ConversationEntry child = Entry(childId, null);
        ConversationEntry plain = Entry(plainId, null);
        var chains = new Dictionary<string, ThreadChain>
        {
            [parentId] = Chain(parentId, parentRef),
            [childId] = Chain(childId, Ref(childId, WriteRollout(childId, "child", parentRef.OwnRolloutId)), parentRef.OwnRolloutId),
            [plainId] = Chain(plainId, Ref(plainId, WriteRollout(plainId, "plain"))),
        };
        string backupPath = Export(
            Catalog([new ProjectEntry(null, "기타 대화", [], [parent, child, plain])], [parent, child, plain], chains),
            new HashSet<string> { childId, plainId });

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, Catalog([], [], new Dictionary<string, ThreadChain>()));
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        Dictionary<string, ImportConversationPreview> byId = preview.Projects.SelectMany(p => p.Conversations)
            .Concat(preview.DependencyOnlyConversations).ToDictionary(x => x.ThreadId);

        Assert.Equal([parentId], byId[childId].RequiredAncestorThreadIds);
        Assert.True(byId[childId].IsCompressedRollout); // 조상 체인의 .zst도 포함
        Assert.True(byId[parentId].IsCompressedRollout);
        Assert.Empty(byId[plainId].RequiredAncestorThreadIds);
        Assert.False(byId[plainId].IsCompressedRollout);
    }
}
