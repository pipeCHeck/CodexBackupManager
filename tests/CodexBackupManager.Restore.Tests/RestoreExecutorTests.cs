using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 7 — <see cref="RestoreExecutor"/>가 frozen <see cref="ImportPlan"/>을 실제 temp Codex
/// Home에 안전하게 적용하는지 검증한다. 실제 사용자 <c>.codex</c>는 이 테스트 전체에서 단 한 번도
/// 열리지 않는다 — 전부 이 테스트가 <see cref="TestCodexHomeBuilder"/>로 만든 합성 temp 폴더다.
/// </summary>
public sealed class RestoreExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-restore-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _pcBHome;
    private readonly string _outDir;

    public RestoreExecutorTests()
    {
        _pcADir = Path.Combine(_root, "pcA-rollouts");
        _pcBHome = Path.Combine(_root, "pcB-home");
        _outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_pcADir);
        Directory.CreateDirectory(_outDir);
        TestCodexHomeBuilder.CreateEmpty(_pcBHome);
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

    private static string SessionMetaLine(
        string threadId, DateTimeOffset ts, string? historyBaseRolloutId = null, long historyBaseEndOrdinalExclusive = 1)
    {
        string historyBase = historyBaseRolloutId is null
            ? string.Empty
            : ",\"history_base\":{\"thread_id\":\"" + historyBaseRolloutId + "\",\"end_ordinal_exclusive\":" + historyBaseEndOrdinalExclusive + ",\"end_byte_offset\":1}";
        return "{\"timestamp\":\"" + ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"ordinal\":0,\"type\":\"session_meta\"," +
           "\"payload\":{\"id\":\"" + threadId + "\",\"session_id\":\"" + threadId + "\"," +
           "\"timestamp\":\"" + ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"cwd\":\"C:\\\\Fixture\\\\Proj\"," +
           "\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.1.0\",\"source\":\"vscode\"," +
           "\"model_provider\":\"openai\"" + historyBase + "}}";
    }

    private string WriteRollout(string dir, string threadId, string? segmentId, DateTimeOffset ts, params string[] extraLines)
    {
        string fileName = segmentId is null
            ? $"rollout-{ts:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl"
            : $"rollout-{ts:yyyy-MM-ddTHH-mm-ss}-{threadId}_{segmentId}.jsonl";
        string path = Path.Combine(dir, fileName);
        var lines = new List<string> { SessionMetaLine(threadId, ts) };
        lines.AddRange(extraLines);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static RolloutFileReference Ref(string threadId, string? segmentId, string fullPath, DateTimeOffset ts)
        => new(fullPath, Path.GetFileName(fullPath), threadId, segmentId, ts, IsArchived: false, RolloutFileKind.PlainJsonl);

    private static ThreadChain Chain(string threadId, IReadOnlyList<RolloutFileReference> files)
        => new(threadId, files, new HistoryBaseReference?[files.Count], null, null, null, []);

    private static ConversationEntry MakeEntry(string threadId, string? projectId = null) => new()
    {
        ThreadId = threadId,
        Row = new ThreadRow
        {
            Id = threadId,
            ProjectId = projectId,
            ModelProvider = "openai",
            Source = "vscode",
            Cwd = @"C:\Fixture\Proj",
            Title = "test title",
            SandboxPolicy = "{}",
            ApprovalMode = "on-request",
            ThreadSource = "user",
            CreatedAtSeconds = 1_770_000_000,
            UpdatedAtSeconds = 1_770_000_100,
        },
        Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
        Project = new ProjectAssignment(projectId, projectId is null ? ProjectAssignmentSource.Unassigned : ProjectAssignmentSource.StateProjectId),
    };

    private static CodexCatalog SourceCatalog(string threadId, IReadOnlyList<RolloutFileReference> files)
    {
        ConversationEntry entry = MakeEntry(threadId);
        return new CodexCatalog(
            [], [entry], new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, files) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(files.Count, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
    }

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

    private CodexCatalog BuildFreshPcBCatalog()
    {
        var detection = new CodexDetectionService().DetectFromUserSelection(_pcBHome);
        Assert.NotNull(detection.Installation);
        return CodexCatalogBuilder.Build(detection.Installation!);
    }

    private static IReadOnlyList<RunningProcessInfo> CodexNotRunning() => [];

    private static Dictionary<string, string> SnapshotHashes(params string[] filePaths)
    {
        var result = new Dictionary<string, string>();
        foreach (string path in filePaths)
        {
            result[path] = File.Exists(path)
                ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                : "<missing>";
        }

        return result;
    }

    // ── 1) New conversation Import 성공 ──────────────────────────────────────

    [Fact]
    public void New_대화가_실제로_Import된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "new-import.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.New);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Contains(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        object? rolloutPath = TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path");
        Assert.NotNull(rolloutPath);
        Assert.True(File.Exists((string)rolloutPath!));
        Assert.Equal(File.ReadAllText(aFile), File.ReadAllText((string)rolloutPath!));
    }

    // ── 2) Identical → NoOp, write 0 ─────────────────────────────────────────

    [Fact]
    public void Identical_대화는_write가_0건이다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "identical.codexbackup");

        string bPath = TestCodexHomeBuilder.WriteRolloutFile(
            _pcBHome, Path.GetFileName(aFile), File.ReadAllText(aFile), archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath);

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.Identical);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NothingToDo, result.Outcome);
        Dictionary<string, string> after = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath);
        Assert.Equal(before, after);
    }

    // ── 3) Codex 실행 중이면 write 0 ──────────────────────────────────────────

    [Fact]
    public void Codex가_실행_중이면_write가_0건이다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "codex-running.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome));

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, () => [new RunningProcessInfo("Codex", null)], _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)));
    }

    // ── 4) backup이 Preview 이후 바뀌면 write 0 ───────────────────────────────

    [Fact]
    public void backup이_Preview_이후_바뀌면_write가_0건이다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "backup-changed.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        byte[] bytes = File.ReadAllBytes(backupPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(backupPath, bytes);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal(ImportPlanPreflightStatus.BackupChanged, result.PreflightStatus);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    // ── IncomingAhead: 같은 파일에 안전하게 append ────────────────────────────

    [Fact]
    public void IncomingAhead_plain_jsonl는_안전하게_append된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "incoming-ahead.codexbackup");

        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), localContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Equal(File.ReadAllText(aFile), File.ReadAllText(bPath));
    }

    // ── IncomingAhead: 새 segment 파일이 추가된다 ─────────────────────────────

    [Fact]
    public void IncomingAhead_새_segment가_안전하게_추가된다()
    {
        string threadId = NewId();
        DateTimeOffset ts1 = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        DateTimeOffset ts2 = ts1.AddMinutes(5);
        string segmentId = NewId();

        string aFile1 = WriteRollout(_pcADir, threadId, null, ts1, Line(1, "hello"));
        string aFile2 = WriteRollout(_pcADir, threadId, segmentId, ts2, Line(1, "continued"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile1, ts1), Ref(threadId, segmentId, aFile2, ts2)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "incoming-new-segment.codexbackup");

        string localContent = SessionMetaLine(threadId, ts1) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile1), localContent, archived: false, ts1);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        string expectedNewSegmentPath = Path.Combine(
            _pcBHome, "sessions", ts2.Year.ToString("D4"), ts2.Month.ToString("D2"), ts2.Day.ToString("D2"), Path.GetFileName(aFile2));
        Assert.True(File.Exists(expectedNewSegmentPath));
        Assert.Equal(File.ReadAllText(aFile2), File.ReadAllText(expectedNewSegmentPath));
        object? rolloutPath = TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path");
        Assert.Equal(expectedNewSegmentPath, (string)rolloutPath!);
        // 원래 파일은 그대로다(건드리지 않았다).
        Assert.Equal(localContent, File.ReadAllText(bPath));
    }

    private static string WriteZst(string dir, string fileName, string content)
    {
        string path = Path.Combine(dir, fileName);
        using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
        using ZstdSharp.CompressionStream compressionStream = new(fileStream, leaveOpen: true);
        using StreamWriter writer = new(compressionStream);
        writer.Write(content);
        return path;
    }

    // ── zst update 안전 미확정 → Blocked, write 0 ─────────────────────────────

    [Fact]
    public void 압축된_rollout_파일에는_append를_시도하지_않고_Apply를_거부한다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string incomingContent = localContent + Line(2, "world") + "\n"; // 로컬 내용 그대로 + 한 줄 추가

        string aZstFile = Path.Combine(_pcADir, $"rollout-{ts:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl.zst");
        using (FileStream fs = new(aZstFile, FileMode.Create, FileAccess.Write))
        using (ZstdSharp.CompressionStream cs = new(fs, leaveOpen: true))
        using (StreamWriter w = new(cs))
        {
            w.Write(incomingContent);
        }

        CodexCatalog pcA = new(
            [], [MakeEntry(threadId)],
            new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, [new RolloutFileReference(aZstFile, Path.GetFileName(aZstFile), threadId, null, ts, false, RolloutFileKind.ZstdCompressed)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "zst-update.codexbackup");

        string bDir = Path.Combine(_pcBHome, "sessions", ts.Year.ToString("D4"), ts.Month.ToString("D2"), ts.Day.ToString("D2"));
        Directory.CreateDirectory(bDir);
        string bZstFile = WriteZst(bDir, Path.GetFileName(aZstFile), localContent);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bZstFile, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bZstFile);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.NotReady, $"{result.Outcome}: {result.Message}");
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bZstFile));
    }

    // ── unsafe physical-tail → Blocked, write 0 ───────────────────────────────

    [Fact]
    public void 물리_파일에_논리_cutoff_이후_여분_바이트가_있으면_Apply_전체가_거부된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "unsafe-tail.codexbackup");

        // Preview 시점의 로컬 상태(짧음, "hello"까지만).
        string atPreviewContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), atPreviewContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        CodexCatalog pcBAtPreview = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBAtPreview);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        // Apply 직전, 로컬 물리 파일에 "논리 cutoff 이후" 별도로 계속 쓰인 바이트가 생겼다고 가정한다
        // (Phase 06_01 실측 위험 재현) — 파일 앞부분은 그대로 두고 뒤에 다른 내용을 추가한다.
        File.AppendAllText(bPath, Line(2, "unofficial-continuation") + "\n");
        CodexCatalog pcBAtApply = BuildFreshPcBCatalog();

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBAtApply);

        // Preflight가 이미 LocalStateChanged로 잡거나(파일이 바뀌었으므로), 혹시 통과하더라도
        // OperationPlanner가 물리 안전성 재확인에서 거부해야 한다 — 어느 경로든 write는 0건이어야 한다.
        Assert.NotEqual(RestoreOutcome.Succeeded, result.Outcome);
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath));
    }

    // ── 다른 thread가 history_base 조상으로 참조하는 파일에는 append하지 않는다 ─

    [Fact]
    public void 다른_대화의_history_base_조상으로_참조되는_파일에는_append하지_않는다()
    {
        string parentThreadId = NewId();
        string childThreadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        string aFile = WriteRollout(_pcADir, parentThreadId, null, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = SourceCatalog(parentThreadId, [Ref(parentThreadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { parentThreadId }, "ancestor-guard.codexbackup");

        string parentContent = SessionMetaLine(parentThreadId, ts) + "\n" + Line(1, "hello") + "\n";
        string parentPath = TestCodexHomeBuilder.WriteRolloutFile(
            _pcBHome, Path.GetFileName(aFile), parentContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, parentThreadId, parentPath, @"C:\Fixture\Proj");

        // 로컬에만 있는 자식 thread — session_meta.history_base가 parent의 rollout id를 가리킨다.
        DateTimeOffset childTs = ts.AddMinutes(1);
        string childContent = SessionMetaLine(childThreadId, childTs, historyBaseRolloutId: parentThreadId) + "\n" + Line(1, "forked") + "\n";
        string childPath = TestCodexHomeBuilder.WriteRolloutFile(
            _pcBHome, $"rollout-{childTs:yyyy-MM-ddTHH-mm-ss}-{childThreadId}.jsonl", childContent, archived: false, childTs);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, childThreadId, childPath, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        Assert.True(pcBBefore.Chains.ContainsKey(childThreadId));
        Assert.Equal(parentThreadId, pcBBefore.Chains[childThreadId].ParentThreadId);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == parentThreadId && c.Relation == RevisionRelation.IncomingAhead);

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), parentPath, childPath);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), parentPath, childPath));
    }

    // ── Fault injection → Rollback 후 원래 hash와 동일 ───────────────────────

    [Fact]
    public void SQLite_커밋_직전_강제_실패해도_Rollback_후_원래_상태로_돌아간다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-injection.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome));
        IReadOnlyList<string> sessionFilesBefore = Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories);
        Assert.Empty(sessionFilesBefore);

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.BeforeSqliteTransaction));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)));
        // rollout 파일도 새로 생겼던 게 있으면 삭제됐어야 한다.
        Assert.Empty(Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SQLite_커밋_직후_강제_실패해도_Rollback_후_원래_상태로_돌아간다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-after-commit.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome));

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.AfterSqliteCommit));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void 첫_rollout_파일_생성_직후_강제_실패해도_Rollback된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-after-rollout.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.AfterFirstRolloutCreate));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Empty(Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    // ── Two-PC round trip: New → Update(IncomingAhead) → Identical ──────────

    [Fact]
    public void Two_PC_왕복_New에서_Update를_거쳐_Identical까지_실제로_동작한다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        // 1단계: PC A에 Thread X = "AB" 생성 → Export → PC B에 New Import.
        string stepAFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "A"), Line(2, "B"));
        CodexCatalog stepACatalog = SourceCatalog(threadId, [Ref(threadId, null, stepAFile, ts)]);
        string backup1 = ExportToBackup(stepACatalog, new HashSet<string> { threadId }, "step1-new.codexbackup");

        CodexCatalog pcBEmpty = BuildFreshPcBCatalog();
        ImportPreview preview1 = ImportPreviewBuilder.Build(backup1, pcBEmpty);
        Assert.True(preview1.Success);
        ImportPlan? plan1 = ImportPlanBuilder.Build(preview1, backup1);
        Assert.NotNull(plan1);
        Assert.Contains(plan1!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.New);

        RestoreResult result1 = RestoreExecutor.Apply(plan1, _pcBHome, CodexNotRunning, _ => pcBEmpty);
        Assert.True(result1.Outcome == RestoreOutcome.Succeeded, $"{result1.Outcome}: {result1.Message}");
        Assert.Contains(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));

        // 2단계: PC A에서 Thread X가 "AB"+"C"까지 이어짐 → Export → PC B가 IncomingAhead로 인식 → Apply.
        string stepBFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "A"), Line(2, "B"), Line(3, "C"));
        CodexCatalog stepBCatalog = SourceCatalog(threadId, [Ref(threadId, null, stepBFile, ts)]);
        string backup2 = ExportToBackup(stepBCatalog, new HashSet<string> { threadId }, "step2-update.codexbackup");

        CodexCatalog pcBAfterNew = BuildFreshPcBCatalog();
        ImportPreview preview2 = ImportPreviewBuilder.Build(backup2, pcBAfterNew);
        Assert.True(preview2.Success);
        ImportPlan? plan2 = ImportPlanBuilder.Build(preview2, backup2);
        Assert.NotNull(plan2);
        Assert.Contains(plan2!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result2 = RestoreExecutor.Apply(plan2, _pcBHome, CodexNotRunning, _ => pcBAfterNew);
        Assert.True(result2.Outcome == RestoreOutcome.Succeeded, $"{result2.Outcome}: {result2.Message}");

        object? rolloutPathAfterUpdate = TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path");
        Assert.Equal(File.ReadAllText(stepBFile), File.ReadAllText((string)rolloutPathAfterUpdate!));

        // 3단계: 이제 PC A/PC B가 완전히 같은 내용 → 다시 Preview하면 Identical/NoOp이어야 한다.
        CodexCatalog pcBAfterUpdate = BuildFreshPcBCatalog();
        ImportPreview preview3 = ImportPreviewBuilder.Build(backup2, pcBAfterUpdate);
        Assert.True(preview3.Success);
        Assert.Contains(preview3.Projects.SelectMany(p => p.Conversations)
                .Concat(preview3.DependencyOnlyConversations),
            c => c.ThreadId == threadId && c.Relation == RevisionRelation.Identical);

        ImportPlan? plan3 = ImportPlanBuilder.Build(preview3, backup2);
        Assert.NotNull(plan3);
        RestoreResult result3 = RestoreExecutor.Apply(plan3, _pcBHome, CodexNotRunning, _ => pcBAfterUpdate);
        Assert.Equal(RestoreOutcome.NothingToDo, result3.Outcome);

        // ThreadId는 시종일관 동일하게 유지됐다 — 삭제 후 재생성 방식이 아니다.
        Assert.Single(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Contains(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    private sealed class ThrowingFaultInjectionHook(RestoreFaultInjectionPoint throwAt) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint point)
        {
            if (point == throwAt)
            {
                throw new InvalidOperationException($"의도적으로 주입된 실패({point}) — 테스트 전용.");
            }
        }
    }

    private sealed class CancellingFaultInjectionHook(RestoreFaultInjectionPoint cancelAt, CancellationTokenSource cts) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint point)
        {
            if (point == cancelAt)
            {
                cts.Cancel();
            }
        }
    }

    // ── 취소(첫 mutation 이후) → Rollback ─────────────────────────────────────

    [Fact]
    public void 첫_rollout_생성_후_취소하면_Rollback된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "cancel-after-mutation.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        using var cts = new CancellationTokenSource();

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new CancellingFaultInjectionHook(RestoreFaultInjectionPoint.AfterFirstRolloutCreate, cts),
            cancellationToken: cts.Token);

        // Phase 07_01 요구사항 13 — 사용자 취소는 실제 오류(RolledBack)와 구분된 Cancelled로 보고한다.
        Assert.Equal(RestoreOutcome.Cancelled, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Empty(Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    // ── Diverged/Unverifiable → write 0 (기존 Preflight가 이미 차단) ───────

    [Fact]
    public void Diverged_대화는_Apply가_전체_차단되어_write가_0건이다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "incoming-branch"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "diverged.codexbackup");

        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "local-branch") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), localContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        Dictionary<string, string> before = SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath);

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.True(plan!.HasUnresolvedDivergence);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal(ImportPlanPreflightStatus.UnresolvedDivergence, result.PreflightStatus);
        Assert.Equal(before, SnapshotHashes(TestCodexHomeBuilder.FindStateDbPath(_pcBHome), bPath));
    }

    // ── 요구사항 5: Apply 도중 backup 파일이 바뀌어도 pin된 바이트만 계속 쓴다 ─────

    [Fact]
    public void Snapshot_이후_backup_파일이_바뀌어도_pin된_바이트로_계속_적용된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "toctou-guard.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        byte[] originalBytes = File.ReadAllBytes(backupPath);
        bool tamperAttempted = false;
        bool tamperBlockedByOs = false;

        // Snapshot이 끝난 뒤(=PinnedBackupSource가 이미 열려 identity를 확인한 뒤) 같은 경로의
        // 파일을 다른 내용으로 바꿔치기 시도한다. Windows에서는 PinnedBackupSource가 FileShare.Read로
        // 파일을 열어 둔 상태라 다른 쓰기 핸들을 여는 것 자체가 공유 위반으로 막힌다 — 이는 OS
        // 수준에서도 "한 번 pin되면 그 이후로는 바꿔치기할 수 없다"는 것을 보여주는 추가 증거다.
        // 혹시(다른 OS 등에서) 실제로 바꿔치기가 성공하더라도, Planner/Executor가 여전히 같은
        // pinned reader만 쓰므로 최종 결과는 원본 바이트 그대로여야 한다.
        var tamperHook = new ActionFaultInjectionHook(RestoreFaultInjectionPoint.AfterSnapshot, () =>
        {
            tamperAttempted = true;
            try
            {
                byte[] tampered = (byte[])originalBytes.Clone();
                tampered[tampered.Length / 2] ^= 0xFF;
                File.WriteAllBytes(backupPath, tampered);
            }
            catch (IOException)
            {
                tamperBlockedByOs = true;
            }
        });

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore, faultInjection: tamperHook);

        Assert.True(tamperAttempted);
        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Contains(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        object? rolloutPath = TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "rollout_path");
        // pin된 원본 바이트로 복사됐어야 한다 — 바꿔치기 시도가 있었든(OS가 막았든, 성공했든) 무관하게.
        Assert.Equal(File.ReadAllText(aFile), File.ReadAllText((string)rolloutPath!));
        _ = tamperBlockedByOs; // 진단용 — Windows에서는 보통 true.
    }

    // ── 요구사항 6: WAL/SHM도 snapshot/rollback 대상이다 ──────────────────────

    [Fact]
    public void SQLite_커밋_직후_실패하면_WAL_SHM도_원래_없던_상태로_되돌아간다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "wal-shm-rollback.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        string stateDbPath = TestCodexHomeBuilder.FindStateDbPath(_pcBHome);
        string walPath = stateDbPath + "-wal";
        string shmPath = stateDbPath + "-shm";
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.AfterSqliteCommit));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        // WAL 모드로 연 SQLite가 만들었을 수 있는 -wal/-shm이 원래(둘 다 없던) 상태로 되돌아가야 한다.
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    // ── 요구사항 7: .jsonl.zst 새 segment는 물리 바이트 기준으로 검증돼야 한다 ──

    [Fact]
    public void IncomingAhead_zst_새_segment는_물리_바이트로_정확히_복사되고_논리_revision도_일치한다()
    {
        string threadId = NewId();
        DateTimeOffset ts1 = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        DateTimeOffset ts2 = ts1.AddMinutes(5);
        string segmentId = NewId();

        string aFile1 = WriteRollout(_pcADir, threadId, null, ts1, Line(1, "hello"));
        string zstFileName = $"rollout-{ts2:yyyy-MM-ddTHH-mm-ss}-{threadId}_{segmentId}.jsonl.zst";
        string aZstFile = Path.Combine(_pcADir, zstFileName);
        string zstContent = SessionMetaLine(threadId, ts2, historyBaseRolloutId: threadId, historyBaseEndOrdinalExclusive: 2) + "\n" + Line(1, "continued-in-zst") + "\n";
        using (FileStream fs = new(aZstFile, FileMode.Create, FileAccess.Write))
        using (ZstdSharp.CompressionStream cs = new(fs, leaveOpen: true))
        using (StreamWriter w = new(cs))
        {
            w.Write(zstContent);
        }

        CodexCatalog pcA = new(
            [], [MakeEntry(threadId)],
            new Dictionary<string, ThreadChain>
            {
                [threadId] = Chain(threadId, [
                    Ref(threadId, null, aFile1, ts1),
                    new RolloutFileReference(aZstFile, Path.GetFileName(aZstFile), threadId, segmentId, ts2, false, RolloutFileKind.ZstdCompressed),
                ]),
            },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(2, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "zst-new-segment.codexbackup");

        string localContent = SessionMetaLine(threadId, ts1) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile1), localContent, archived: false, ts1);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        string expectedNewSegmentPath = Path.Combine(
            _pcBHome, "sessions", ts2.Year.ToString("D4"), ts2.Month.ToString("D2"), ts2.Day.ToString("D2"), zstFileName);
        Assert.True(File.Exists(expectedNewSegmentPath));
        // 물리(압축) 바이트가 원본과 정확히 같아야 한다.
        Assert.Equal(File.ReadAllBytes(aZstFile), File.ReadAllBytes(expectedNewSegmentPath));
        // post-validation이 이미 논리 revision까지 확인했으므로(Succeeded), 여기서는 결과가
        // Succeeded라는 사실 자체가 논리 revision 일치의 증거다 — RestoreValidator가 실패했다면
        // Rollback돼 RolledBack이 됐을 것이다.
    }

    // ── 요구사항 8: IncomingAhead metadata 병합 정책 ─────────────────────────

    [Fact]
    public void IncomingAhead는_updated_at와_tokens_used를_반영하고_비어있는_name만_채운다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = new(
            [], [new ConversationEntry
            {
                ThreadId = threadId,
                Row = new ThreadRow
                {
                    Id = threadId, ModelProvider = "openai", Source = "vscode", Cwd = @"C:\Fixture\Proj",
                    Title = "t", SandboxPolicy = "{}", ApprovalMode = "on-request", ThreadSource = "user",
                    CreatedAtSeconds = 1_770_000_000, UpdatedAtSeconds = 1_770_005_000, TokensUsed = 500,
                    Name = "backup-name",
                },
                Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
                Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
            }],
            new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, [Ref(threadId, null, aFile, ts)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "metadata-merge.codexbackup");

        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), localContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");
        // local이 이미 name을 갖고 있으면 절대 덮어쓰지 않아야 한다.
        UpdateLocalThreadColumn(threadId, "name", "local-name");
        UpdateLocalThreadColumn(threadId, "updated_at", 1_000L);
        UpdateLocalThreadColumn(threadId, "tokens_used", 10L);

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");
        Assert.Equal(1_770_005_000L, (long)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "updated_at")!);
        Assert.Equal(500L, (long)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "tokens_used")!);
        // local이 이미 "local-name"을 갖고 있었으므로 backup의 "backup-name"으로 덮어써지지 않아야 한다.
        Assert.Equal("local-name", (string)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "name")!);
        // target PC 고유 값(cwd)은 절대 바뀌지 않는다.
        Assert.Equal(@"C:\Fixture\Proj", (string)TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "cwd")!);
    }

    // ── 요구사항 9: thread_source 없는 New Import는 거부되지만, dependency-only 조상은 예외 ──

    [Fact]
    public void thread_source가_없는_선택된_대화는_New_Import가_거부된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = new(
            [], [new ConversationEntry
            {
                ThreadId = threadId,
                Row = new ThreadRow
                {
                    Id = threadId, ModelProvider = "openai", Source = "vscode", Cwd = @"C:\Fixture\Proj",
                    Title = "t", SandboxPolicy = "{}", ApprovalMode = "on-request", ThreadSource = null,
                    CreatedAtSeconds = 1_770_000_000, UpdatedAtSeconds = 1_770_000_100,
                },
                Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
                Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
            }],
            new Dictionary<string, ThreadChain> { [threadId] = Chain(threadId, [Ref(threadId, null, aFile, ts)]) },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "missing-thread-source.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        Assert.True(preview.Success);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        RestoreResult result = RestoreExecutor.Apply(plan, _pcBHome, CodexNotRunning, _ => pcBBefore);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    // ── 요구사항 12: 나머지 fault injection 지점 ─────────────────────────────

    [Fact]
    public void rollout_append_직후_강제_실패해도_Rollback된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-after-append.codexbackup");

        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), localContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Conversations, c => c.ThreadId == threadId && c.Relation == RevisionRelation.IncomingAhead);

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.AfterRolloutAppend));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal(localContent, File.ReadAllText(bPath));
    }

    [Fact]
    public void post_validation_직전_강제_실패해도_Rollback된다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-before-validation.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.BeforePostValidation));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Empty(Directory.GetFiles(Path.Combine(_pcBHome, "sessions"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Snapshot_직후_강제_실패해도_일관된_결과를_돌려준다()
    {
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, null, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, [Ref(threadId, null, aFile, ts)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { threadId }, "fault-after-snapshot.codexbackup");

        CodexCatalog pcBBefore = BuildFreshPcBCatalog();
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBBefore);
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        RestoreResult result = RestoreExecutor.Apply(
            plan, _pcBHome, CodexNotRunning, _ => pcBBefore,
            faultInjection: new ThrowingFaultInjectionHook(RestoreFaultInjectionPoint.AfterSnapshot));

        // 아직 mutation을 하나도 하지 않았지만(Snapshot은 읽기만 한다), 이 지점 이후의 예외는
        // 일관되게 Rollback 경로를 타 RolledBack으로 보고한다(빈 Rollback이라도 수행).
        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    private void UpdateLocalThreadColumn(string threadId, string column, object value)
    {
        string dbPath = TestCodexHomeBuilder.FindStateDbPath(_pcBHome);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE threads SET {column} = $value WHERE id = $id";
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$id", threadId);
        cmd.ExecuteNonQuery();
    }

    private sealed class ActionFaultInjectionHook(RestoreFaultInjectionPoint triggerAt, Action action) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint point)
        {
            if (point == triggerAt)
            {
                action();
            }
        }
    }
}
