using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
/// Phase 06_02 — <see cref="ImportPlanPreflightValidator"/>가 Phase 7의 실제 write 직전에
/// "Preview했던 그 backup, 그 로컬 상태가 여전히 맞는지"를 정확히 확인하는지 검증한다. 실제
/// 사용자 데이터는 쓰지 않는다 — 전부 이 테스트가 만든 합성 임시 파일이다.
/// </summary>
public sealed class ImportPlanPreflightValidatorTests : IDisposable
{
    private readonly string _pcADir = Path.Combine(Path.GetTempPath(), "cbm-preflight-tests", Guid.NewGuid().ToString("N"), "pcA");
    private readonly string _pcBDir = Path.Combine(Path.GetTempPath(), "cbm-preflight-tests", Guid.NewGuid().ToString("N"), "pcB");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "cbm-preflight-tests", Guid.NewGuid().ToString("N"), "out");

    public ImportPlanPreflightValidatorTests()
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

    private static CodexCatalog EmptyCatalog() => new(
        [], [], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow,
        new CodexCatalogStats(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero));

    private static CodexCatalog SingleConversationCatalog(
        string threadId, IReadOnlyList<RolloutFileReference> files, string? projectId = null, string? projectRootPath = null)
    {
        ConversationEntry entry = MakeEntry(threadId, projectId);
        List<ProjectEntry> projects = projectId is null
            ? []
            : [new ProjectEntry(projectId, projectId, projectRootPath is null ? [] : [projectRootPath], [entry])];
        return new CodexCatalog(
            projects, [entry], new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, files) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(files.Count, 1, projects.Count, TimeSpan.Zero, TimeSpan.Zero));
    }

    private string ExportToBackup(CodexCatalog catalog, IReadOnlySet<string> selectedThreadIds, string fileName, DateTimeOffset? createdAtUtc = null)
    {
        ExportPlan plan = ExportPlanBuilder.Build(catalog, selectedThreadIds);
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(
            plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: createdAtUtc ?? DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, fileName);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    private static ImportPlan BuildPlan(string backupPath, CodexCatalog previewLocalCatalog)
    {
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, previewLocalCatalog);
        Assert.True(preview.Success, string.Join("; ", preview.ValidationErrors));
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        return plan!;
    }

    private static Dictionary<string, string> SnapshotHashes(params string[] filePaths)
    {
        var result = new Dictionary<string, string>();
        foreach (string path in filePaths)
        {
            result[path] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }

        return result;
    }

    // ── 1) 아무것도 안 바뀌었으면 Ready ──────────────────────────────────────────────

    [Fact]
    public void backup와_local이_그대로면_Ready다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "ready.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcB = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcB);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.True(result.IsReady);
        Assert.Equal(ImportPlanPreflightStatus.Ready, result.Status);
    }

    // ── 2~4) backup identity ────────────────────────────────────────────────────────

    [Fact]
    public void preview_후_backup_파일이_1바이트_바뀌면_BackupChanged다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "tampered.codexbackup");

        CodexCatalog pcB = EmptyCatalog();
        ImportPlan plan = BuildPlan(backupPath, pcB);

        byte[] bytes = File.ReadAllBytes(backupPath);
        bytes[bytes.Length / 2] ^= 0xFF; // 중간 바이트 하나를 뒤집는다(ZIP 구조 파괴 여부와 무관하게 BackupChanged여야 한다).
        File.WriteAllBytes(backupPath, bytes);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.Equal(ImportPlanPreflightStatus.BackupChanged, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void 같은_경로에_다른_valid_backup으로_교체되면_BackupChanged다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "swapped.codexbackup");

        CodexCatalog pcB = EmptyCatalog();
        ImportPlan plan = BuildPlan(backupPath, pcB);

        // 같은 경로에 완전히 다른(하지만 여전히 유효한) backup을 새로 만들어 덮어쓴다.
        string otherThread = NewId();
        string otherFile = WriteRollout(_pcADir, otherThread, null, Line(0, "different conversation entirely"));
        CodexCatalog otherCatalog = SingleConversationCatalog(otherThread, [Ref(otherThread, null, otherFile)]);
        ExportPlan otherPlan = ExportPlanBuilder.Build(otherCatalog, new HashSet<string> { otherThread });
        BackupManifest otherManifest = ManifestBuilder.Build(otherPlan, null, null, DateTimeOffset.UtcNow);
        BackupWriter.WriteResult overwriteResult = BackupWriter.Write(otherPlan, otherManifest, backupPath, overwrite: true);
        Assert.True(overwriteResult.Success, overwriteResult.FailureReason);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.Equal(ImportPlanPreflightStatus.BackupChanged, result.Status);
    }

    [Fact]
    public void CreatedAt_AppVersion_개수가_우연히_같아도_hash가_다르면_BackupChanged다()
    {
        DateTimeOffset sameCreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        string t1 = NewId();
        string aFile = WriteRollout(_pcADir, t1, null, Line(0, "content-A"));
        CodexCatalog pcA = SingleConversationCatalog(t1, [Ref(t1, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t1 }, "coincidence.codexbackup", sameCreatedAt);

        CodexCatalog pcB = EmptyCatalog();
        ImportPlan plan = BuildPlan(backupPath, pcB);

        // 완전히 다른 thread/내용이지만 대화 수(1개)와 createdAtUtc는 의도적으로 똑같이 맞춘 backup으로 교체.
        string t2 = NewId();
        string otherFile = WriteRollout(_pcADir, t2, null, Line(0, "content-B-totally-different"));
        CodexCatalog otherCatalog = SingleConversationCatalog(t2, [Ref(t2, null, otherFile)]);
        ExportPlan otherPlan = ExportPlanBuilder.Build(otherCatalog, new HashSet<string> { t2 });
        BackupManifest otherManifest = ManifestBuilder.Build(otherPlan, null, null, sameCreatedAt);
        BackupWriter.WriteResult overwriteResult = BackupWriter.Write(otherPlan, otherManifest, backupPath, overwrite: true);
        Assert.True(overwriteResult.Success, overwriteResult.FailureReason);

        using (BackupReadOnlyManifestCheck check = new(backupPath))
        {
            Assert.Equal(plan.Backup.CreatedAtUtc, check.Manifest.CreatedAtUtc);
            Assert.Equal(plan.Backup.AppVersion, check.Manifest.AppVersion);
            Assert.Equal(plan.Backup.TotalConversationCount, check.Manifest.ConversationCount);
        }

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.Equal(ImportPlanPreflightStatus.BackupChanged, result.Status);
    }

    private sealed class BackupReadOnlyManifestCheck : IDisposable
    {
        private readonly CodexBackupManager.Backup.Reading.BackupReader _reader;
        public BackupManifest Manifest { get; }

        public BackupReadOnlyManifestCheck(string path)
        {
            _reader = CodexBackupManager.Backup.Reading.BackupReader.Open(path);
            Manifest = _reader.ReadManifest();
        }

        public void Dispose() => _reader.Dispose();
    }

    // ── 5) New precondition ──────────────────────────────────────────────────────

    [Fact]
    public void New_preview_후_local에_같은_ThreadId가_생기면_LocalStateChanged다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "new-then-appears.codexbackup");

        ImportPlan plan = BuildPlan(backupPath, EmptyCatalog());
        Assert.Contains(plan.Conversations, c => c.ThreadId == t && c.Relation == RevisionRelation.New);

        // Apply 직전에 로컬에 같은 ThreadId가 생겼다고 가정한다(예: 사용자가 그 사이 같은 대화를 새로 시작).
        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "unrelated local content"));
        CodexCatalog pcBNow = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcBNow);

        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, result.Status);
    }

    // ── 6~8) IncomingAhead precondition ─────────────────────────────────────────

    [Fact]
    public void IncomingAhead_preview_후_local이_이어써지면_LocalStateChanged다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "incoming-ahead.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcBAtPreview = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcBAtPreview);
        Assert.Contains(plan.Conversations, c => c.ThreadId == t && c.Relation == RevisionRelation.IncomingAhead);

        // 사용자가 Apply 전에 계속 작업해서 로컬 rollout이 이어써졌다.
        File.WriteAllText(bFile, string.Join("\n", [Line(0, "hello"), Line(1, "appended-by-user")]) + "\n");

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcBAtPreview);

        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, result.Status);
    }

    [Fact]
    public void IncomingAhead_preview_후_local이_다른_branch로_바뀌면_LocalStateChanged다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "incoming-ahead-diverge.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcBAtPreview = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcBAtPreview);
        Assert.Contains(plan.Conversations, c => c.ThreadId == t && c.Relation == RevisionRelation.IncomingAhead);

        // 완전히 다른 branch로 갈아치워진 상태(길이는 같을 수도 있지만 내용이 다르다).
        File.WriteAllText(bFile, "totally-different-content-not-a-prefix-relation\n");

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcBAtPreview);

        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, result.Status);
    }

    [Fact]
    public void IncomingAhead_preview_때의_local_revision_그대로면_Ready다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "incoming-ahead-unchanged.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcBAtPreview = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcBAtPreview);
        Assert.Contains(plan.Conversations, c => c.ThreadId == t && c.Relation == RevisionRelation.IncomingAhead);

        // 아무것도 바뀌지 않았다 — 같은 카탈로그/파일 그대로.
        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcBAtPreview);

        Assert.True(result.IsReady, string.Join("; ", result.Issues));
    }

    // ── 9~10) Target path preflight ─────────────────────────────────────────────

    [Fact]
    public void 대상_프로젝트_폴더가_삭제되면_TargetPathUnavailable이다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)], projectId: "proj-a", projectRootPath: @"C:\Some\Root");
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "target-path.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        string overridePath = Path.Combine(_pcBDir, "will-be-deleted");
        Directory.CreateDirectory(overridePath);
        ImportPreview withPath = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "proj-a", overridePath);

        ImportPlan? plan = ImportPlanBuilder.Build(withPath, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Projects, p => p.TargetProjectPath == overridePath);

        Directory.Delete(overridePath, recursive: true);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, EmptyCatalog());

        Assert.Equal(ImportPlanPreflightStatus.TargetPathUnavailable, result.Status);
    }

    [Fact]
    public void projectless_대화는_경로_확인_없이_정상이다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        // projectId를 주지 않아 "기타 대화"(미분류)로 만든다 — TargetProjectPath가 null이 된다.
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "projectless.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcB = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcB);
        Assert.Contains(plan.Projects, p => p.TargetProjectPath is null);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.True(result.IsReady, string.Join("; ", result.Issues));
    }

    // ── 11~12) Diverged / Unverifiable → Ready 아님 ─────────────────────────────

    [Fact]
    public void Diverged가_있으면_preflight도_Ready가_아니다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "incoming-branch"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "diverged-preflight.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"), Line(1, "local-branch"));
        CodexCatalog pcB = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcB);
        Assert.True(plan.HasUnresolvedDivergence);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.Equal(ImportPlanPreflightStatus.UnresolvedDivergence, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Unverifiable이_있으면_preflight도_Ready가_아니다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "unverifiable-preflight.codexbackup");

        // 로컬에 metadata는 있지만 chain은 없는 손상 상태(Phase 06_01 정책) → Unverifiable.
        CodexCatalog pcB = new(
            [], [MakeEntry(t)], new Dictionary<string, ThreadChain>(), [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(0, 1, 0, TimeSpan.Zero, TimeSpan.Zero));

        ImportPlan plan = BuildPlan(backupPath, pcB);
        Assert.True(plan.HasBlockingIssues);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.Equal(ImportPlanPreflightStatus.Blocked, result.Status);
        Assert.False(result.IsReady);
    }

    // ── 대형 backup의 whole-file hash도 정상 동작한다(스트리밍 원리 자체는 StreamingHashCopy가
    //    Phase 5에서 이미 100~300MB급으로 bounded-memory 검증을 마쳤다 — 여기서는 그 함수를
    //    ImportPlanBuilder/ImportPlanPreflightValidator 경로에서 재사용해도 기능적으로 올바른지만 본다) ──

    [Fact]
    public void 대형_backup_파일도_whole_file_hash로_정상적으로_Ready_판정된다()
    {
        string t = NewId();
        // 약 24MB짜리 단일 rollout(수십만 줄) — payload 자체가 커서 backup 파일 전체도 커진다.
        string[] bigLines = Enumerable.Range(0, 300_000).Select(i => Line(i, $"line-{i}")).ToArray();
        string aFile = WriteRollout(_pcADir, t, null, bigLines);
        Assert.True(new FileInfo(aFile).Length > 20_000_000);

        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "large.codexbackup");
        // ZIP entry는 원본 rollout 바이트를 그대로 담지만 반복적인 텍스트라 압축이 잘 되므로(Deflate),
        // backup 파일 자체는 원본보다 훨씬 작을 수 있다 — 여기서는 payload 원본 크기만 확인한다.

        CodexCatalog pcB = EmptyCatalog();
        ImportPlan plan = BuildPlan(backupPath, pcB);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, pcB);

        Assert.True(result.IsReady, string.Join("; ", result.Issues));
    }

    // ── 13) Read-only 보장 ───────────────────────────────────────────────────────

    [Fact]
    public void preflight_검증_동안_backup_파일과_로컬_파일을_전혀_수정하지_않는다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"), Line(1, "world"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "readonly-preflight.codexbackup");

        string bFile = WriteRollout(_pcBDir, t, null, Line(0, "hello"));
        CodexCatalog pcB = SingleConversationCatalog(t, [Ref(t, null, bFile)]);

        ImportPlan plan = BuildPlan(backupPath, pcB);

        Dictionary<string, string> before = SnapshotHashes(backupPath, bFile);

        ImportPlanPreflightValidator.Validate(plan, pcB);

        Dictionary<string, string> after = SnapshotHashes(backupPath, bFile);

        Assert.Equal(before, after);
    }
}
