using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Codex.Rollout;
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
/// Phase 6 Import Preview 전체 파이프라인(<see cref="ImportPreviewBuilder"/>)을 실제
/// <c>.codexbackup</c> 파일(진짜 Export 파이프라인으로 만든다)과 별도의 "로컬 PC" 카탈로그로
/// 검증한다. "PC A"(backup을 만든 쪽)와 "PC B"(Import하는 쪽)를 각각 별도 임시 디렉터리로
/// 시뮬레이션한다 — 실제 사용자 데이터는 전혀 쓰지 않는다.
/// </summary>
/// <remarks>
/// backup 쪽 lineage 재구성(<see cref="BackupCatalogReader"/>)은 실제 Codex rollout 파일명 규칙
/// (<c>rollout-YYYY-MM-DDTHH-MM-SS-&lt;UUID&gt;.jsonl</c>)으로 thread ID를 다시 파싱하므로, 이
/// 테스트의 thread ID는 전부 실제 UUID 형식(<see cref="Guid.NewGuid()"/>)을 쓴다 — "t"/"child" 같은
/// 임의 문자열은 파일명 규칙에 맞지 않아 조용히 무시된다(실제 Codex와 동일한 정책).
/// </remarks>
public sealed class ImportPreviewBuilderTests : IDisposable
{
    private readonly string _pcADir = Path.Combine(Path.GetTempPath(), "cbm-import-preview-tests", Guid.NewGuid().ToString("N"), "pcA");
    private readonly string _pcBDir = Path.Combine(Path.GetTempPath(), "cbm-import-preview-tests", Guid.NewGuid().ToString("N"), "pcB");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "cbm-import-preview-tests", Guid.NewGuid().ToString("N"), "out");

    public ImportPreviewBuilderTests()
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

    /// <summary>
    /// 실제 Codex rollout 파일명 규칙을 따르는 이름으로 파일을 쓴다(<see cref="RolloutFileNamePattern"/>).
    /// backup 쪽 lineage 재구성이 파일명에서 thread ID를 다시 파싱하므로 이 규칙을 따라야 한다.
    /// </summary>
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

    private static ThreadChain Chain(
        string threadId, IReadOnlyList<RolloutFileReference> files,
        string? parentThreadId = null, long? parentEndOrdinalExclusive = null)
        => new(threadId, files, new HistoryBaseReference?[files.Count], parentThreadId, parentEndOrdinalExclusive, null, []);

    private static ConversationEntry MakeEntry(
        string threadId, string? cwd = null, bool isPinned = false, string? title = null, string? projectId = null)
        => new()
        {
            ThreadId = threadId,
            Row = new ThreadRow { Id = threadId, Cwd = cwd, IsPinned = isPinned, Title = title, ProjectId = projectId },
            Title = new ThreadTitle(title ?? threadId, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(projectId, projectId is null ? ProjectAssignmentSource.Unassigned : ProjectAssignmentSource.StateProjectId),
        };

    private static CodexCatalog MakeCatalog(
        IReadOnlyDictionary<string, ThreadChain> chains,
        IReadOnlyList<ConversationEntry>? allConversations = null)
    {
        List<ConversationEntry> conversations = (allConversations ?? []).ToList();
        var byProject = new List<ProjectEntry>();
        var uncategorized = new List<ConversationEntry>();
        foreach (IGrouping<string?, ConversationEntry> group in conversations.GroupBy(c => c.Project.ProjectId))
        {
            if (group.Key is null)
            {
                uncategorized.AddRange(group);
            }
            else
            {
                byProject.Add(new ProjectEntry(group.Key, group.Key, [], group.ToList()));
            }
        }

        if (uncategorized.Count > 0)
        {
            byProject.Add(new ProjectEntry(null, "기타 대화", [], uncategorized));
        }

        return new CodexCatalog(
            byProject, conversations, chains, [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(chains.Count, conversations.Count, byProject.Count, TimeSpan.Zero, TimeSpan.Zero));
    }

    private static CodexCatalog EmptyCatalog() => new(
        [], [], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow,
        new CodexCatalogStats(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero));

    /// <summary>실제 Export 파이프라인(ExportPlanBuilder → ManifestBuilder → BackupWriter)으로 진짜 <c>.codexbackup</c>을 만든다.</summary>
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

    private static ImportConversationPreview FindConversation(ImportPreview preview, string threadId)
        => preview.Projects.SelectMany(p => p.Conversations)
            .Concat(preview.DependencyOnlyConversations)
            .Single(c => c.ThreadId == threadId);

    // ── New ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void backup_thread가_local에_없으면_New_이고_계획은_Import다()
    {
        string t = NewId();
        string filePath = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, filePath)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "new.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());

        Assert.True(preview.Success);
        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.New, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Import, conversation.PlannedAction);
    }

    // ── Identical ────────────────────────────────────────────────────────────────

    [Fact]
    public void 로컬과_backup이_완전히_동일하면_Identical_이고_계획은_NoOp다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "identical.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello")); // 동일 내용, 다른 경로.
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) }, [MakeEntry(t)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        Assert.True(preview.Success);
        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.Identical, conversation.Relation);
        Assert.Equal(ImportPlannedAction.NoOp, conversation.PlannedAction);
    }

    // ── IncomingAhead / LocalAhead ───────────────────────────────────────────────

    [Fact]
    public void backup이_더_진행됐으면_IncomingAhead_이고_계획은_Update다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "ahead.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello")); // 뒤처진 로컬(prefix).
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) }, [MakeEntry(t)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.IncomingAhead, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Update, conversation.PlannedAction);
    }

    [Fact]
    public void 로컬이_더_진행됐으면_LocalAhead_이고_계획은_Skip이다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello")); // backup은 옛날 상태.
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "behind.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"), Line(1, "world")); // 로컬이 더 진행됨.
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) }, [MakeEntry(t)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.LocalAhead, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Skip, conversation.PlannedAction);
    }

    // ── Diverged ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 공통_조상_이후_양쪽이_다르게_진행되면_Diverged_이고_자동_적용하지_않는다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "incoming-branch"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "diverged.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"), Line(1, "local-branch"));
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) }, [MakeEntry(t)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.Diverged, conversation.Relation);
        Assert.Equal(ImportPlannedAction.RequiresDecision, conversation.PlannedAction);
    }

    // ── Unverifiable(Blocked) ────────────────────────────────────────────────────

    [Fact]
    public void 로컬_lineage가_순환참조면_Unverifiable_이고_계획은_Blocked다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "unverifiable.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        // 로컬 lineage가 스스로를 조상으로 가리키는 손상된 상태(순환)를 시뮬레이션한다.
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)], parentThreadId: t) }, [MakeEntry(t)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.Unverifiable, conversation.Relation);
        Assert.Equal(ImportPlannedAction.Blocked, conversation.PlannedAction);
        Assert.NotEmpty(conversation.Warnings);
    }

    // ── metadata diff는 revision 관계와 독립적이다 ──────────────────────────────────

    [Fact]
    public void 내용은_동일해도_metadata가_다르면_Identical과_MetadataDiff를_함께_보고한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        var pcA = MakeCatalog(
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) },
            [MakeEntry(t, cwd: @"C:\Old\Project", isPinned: false)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "metadata-diff.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcB = MakeCatalog(
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) },
            [MakeEntry(t, cwd: @"D:\New\Project", isPinned: true)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        ImportConversationPreview conversation = FindConversation(preview, t);
        Assert.Equal(RevisionRelation.Identical, conversation.Relation);
        Assert.True(conversation.Metadata.CwdDiffers);
        Assert.True(conversation.Metadata.PinnedDiffers);
        Assert.True(conversation.Metadata.HasAny);
    }

    // ── 여러 대화 혼합 ────────────────────────────────────────────────────────────

    [Fact]
    public void 여러_대화가_섞여도_각자_독립적으로_판정한다()
    {
        string newId = NewId();
        string identicalId = NewId();
        string aheadId = NewId();

        string newFile = WriteRollout(_pcADir, newId, null, Line(0, "new"));
        string identicalAFile = WriteRollout(_pcADir, identicalId, null, Line(0, "same"));
        string aheadAFile = WriteRollout(_pcADir, aheadId, null, Line(0, "a"), Line(1, "b"));

        var pcA = MakeCatalog(
            new Dictionary<string, ThreadChain>
            {
                [newId] = Chain(newId, [Ref(newId, null, newFile)]),
                [identicalId] = Chain(identicalId, [Ref(identicalId, null, identicalAFile)]),
                [aheadId] = Chain(aheadId, [Ref(aheadId, null, aheadAFile)]),
            },
            [MakeEntry(newId), MakeEntry(identicalId), MakeEntry(aheadId)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { newId, identicalId, aheadId }, "mixed.codexbackup");

        string identicalBFile = WriteRollout(_pcBDir, identicalId, null, Line(0, "same"));
        string aheadBFile = WriteRollout(_pcBDir, aheadId, null, Line(0, "a")); // 로컬은 뒤처짐.
        CodexCatalog pcB = MakeCatalog(
            new Dictionary<string, ThreadChain>
            {
                [identicalId] = Chain(identicalId, [Ref(identicalId, null, identicalBFile)]),
                [aheadId] = Chain(aheadId, [Ref(aheadId, null, aheadBFile)]),
            },
            [MakeEntry(identicalId), MakeEntry(aheadId)]);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcB);

        Assert.Equal(RevisionRelation.New, FindConversation(preview, newId).Relation);
        Assert.Equal(RevisionRelation.Identical, FindConversation(preview, identicalId).Relation);
        Assert.Equal(RevisionRelation.IncomingAhead, FindConversation(preview, aheadId).Relation);
    }

    // ── dependency-only 조상은 일반 목록이 아니라 상세에서만 ─────────────────────────

    [Fact]
    public void dependency_only_조상은_project_목록이_아니라_DependencyOnlyConversations에만_있다()
    {
        string parentId = NewId();
        string childId = NewId();
        string parentFile = WriteRollout(_pcADir, parentId, null, Line(0, "parent"));
        string childFile = WriteRollout(_pcADir, childId, null, Line(0, "child"));
        RolloutFileReference parentRef = Ref(parentId, null, parentFile);

        var pcA = MakeCatalog(
            new Dictionary<string, ThreadChain>
            {
                [parentId] = Chain(parentId, [parentRef]),
                [childId] = Chain(childId, [Ref(childId, null, childFile)], parentThreadId: parentRef.OwnRolloutId),
            },
            [MakeEntry(parentId), MakeEntry(childId)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { childId }, "dependency-only.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());

        Assert.True(preview.Success);
        Assert.Contains(preview.DependencyOnlyConversations, c => c.ThreadId == parentId);
        Assert.DoesNotContain(preview.Projects.SelectMany(p => p.Conversations), c => c.ThreadId == parentId);
        Assert.Contains(preview.Projects.SelectMany(p => p.Conversations), c => c.ThreadId == childId);
    }

    // ── project 그룹이 전혀 없는 선택 대화도 화면에서 사라지면 안 된다 ──────────────

    [Fact]
    public void 어떤_project에도_속하지_않은_선택_대화도_Preview에서_사라지지_않는다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        // 일부러 프로젝트 그룹 없이(catalog.Projects=[]) 카탈로그를 만든다 — MakeCatalog을 쓰지 않고
        // 직접 구성해 "선택했지만 어느 project에도 속하지 않는" 방어적 시나리오를 재현한다.
        var pcA = new CodexCatalog(
            [], [MakeEntry(t)],
            new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "no-project.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());

        Assert.True(preview.Success);
        Assert.Contains(preview.Projects.SelectMany(p => p.Conversations), c => c.ThreadId == t);
    }

    // ── malformed backup ──────────────────────────────────────────────────────────

    [Fact]
    public void malformed_backup는_Preview_생성을_금지한다()
    {
        string path = Path.Combine(_outDir, "broken.codexbackup");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]); // ZIP도 아닌 쓰레기 바이트.

        ImportPreview preview = ImportPreviewBuilder.Build(path, EmptyCatalog());

        Assert.False(preview.Success);
        Assert.NotEmpty(preview.ValidationErrors);
        Assert.Empty(preview.Projects);
        Assert.Null(preview.Manifest);
    }

    // ── Read-only 보장: Import Preview 동안 로컬 파일이 전혀 바뀌지 않는다 ───────────

    [Fact]
    public void Import_Preview_동안_로컬_rollout_파일을_전혀_수정하지_않는다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        var pcA = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, aFile)]) }, [MakeEntry(t)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "readonly.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcB = MakeCatalog(new Dictionary<string, ThreadChain> { [t] = Chain(t, [Ref(t, null, bFile)]) }, [MakeEntry(t)]);

        Dictionary<string, (DateTime WriteTimeUtc, string Sha256)> before = SnapshotDirectory(_pcBDir);

        ImportPreviewBuilder.Build(backupPath, pcB);

        Dictionary<string, (DateTime WriteTimeUtc, string Sha256)> after = SnapshotDirectory(_pcBDir);
        Assert.Equal(before, after);
    }

    private static Dictionary<string, (DateTime WriteTimeUtc, string Sha256)> SnapshotDirectory(string dir)
    {
        var result = new Dictionary<string, (DateTime, string)>();
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            result[file] = (File.GetLastWriteTimeUtc(file), hash);
        }

        return result;
    }
}
