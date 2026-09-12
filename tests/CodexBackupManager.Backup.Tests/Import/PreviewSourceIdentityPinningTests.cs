using System;
using System.Collections.Generic;
using System.IO;
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
/// Phase 06_03 — <see cref="ImportPreviewBuilder"/>가 분석 직후 고정한
/// <see cref="ImportPreview.SourceBackupIdentity"/>와, <see cref="ImportPlanBuilder"/>가 Plan을
/// 만들기 직전에 그 identity를 재확인하는 동작(Preview↔Plan TOCTOU 방지)을 검증한다. 실제
/// 사용자 데이터는 쓰지 않는다 — 전부 이 테스트가 만든 합성 임시 파일이다.
/// </summary>
public sealed class PreviewSourceIdentityPinningTests : IDisposable
{
    private readonly string _pcADir = Path.Combine(Path.GetTempPath(), "cbm-source-pin-tests", Guid.NewGuid().ToString("N"), "pcA");
    private readonly string _pcBDir = Path.Combine(Path.GetTempPath(), "cbm-source-pin-tests", Guid.NewGuid().ToString("N"), "pcB");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "cbm-source-pin-tests", Guid.NewGuid().ToString("N"), "out");

    public PreviewSourceIdentityPinningTests()
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

    private string ExportToBackup(
        CodexCatalog catalog, IReadOnlySet<string> selectedThreadIds, string fileName, DateTimeOffset? createdAtUtc = null)
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

    private void OverwriteWithDifferentBackup(string backupPath, string differentThreadId, DateTimeOffset? createdAtUtc = null)
    {
        string otherFile = WriteRollout(_pcADir, differentThreadId, null, Line(0, "completely different content"));
        CodexCatalog otherCatalog = SingleConversationCatalog(differentThreadId, [Ref(differentThreadId, null, otherFile)]);
        ExportPlan otherPlan = ExportPlanBuilder.Build(otherCatalog, new HashSet<string> { differentThreadId });
        BackupManifest otherManifest = ManifestBuilder.Build(otherPlan, null, null, createdAtUtc ?? DateTimeOffset.UtcNow);
        BackupWriter.WriteResult overwriteResult = BackupWriter.Write(otherPlan, otherManifest, backupPath, overwrite: true);
        Assert.True(overwriteResult.Success, overwriteResult.FailureReason);
    }

    // ── 1) 아무것도 안 바뀌면 정상적으로 Plan이 만들어진다 ──────────────────────────

    [Fact]
    public void Preview와_같은_backup으로_Plan을_만들면_성공한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "unchanged.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);
        Assert.NotNull(preview.SourceBackupIdentity);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.NotNull(plan);
        Assert.Equal(preview.SourceBackupIdentity!.BackupFileSha256, plan!.Backup.BackupFileSha256);
    }

    // ── 2) backup 파일이 1바이트라도 바뀌면 Plan 생성을 거부한다 ────────────────────

    [Fact]
    public void Preview_후_backup_파일이_1바이트_바뀌면_Plan_생성이_실패한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "tampered.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        byte[] bytes = File.ReadAllBytes(backupPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(backupPath, bytes);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.Null(plan);
    }

    // ── 3) 같은 경로에 완전히 다른 valid backup으로 교체되면 Plan 생성을 거부한다 ──

    [Fact]
    public void Preview_후_같은_경로에_다른_valid_backup으로_교체되면_Plan_생성이_실패한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "swapped.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        OverwriteWithDifferentBackup(backupPath, NewId());

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.Null(plan);
    }

    // ── 4) rollout/thread 내용은 같아도 manifest metadata만 달라지면 여전히 실패 ───

    [Fact]
    public void 대화_내용은_같아도_manifest_metadata만_달라지면_Plan_생성이_실패한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        DateTimeOffset firstCreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "same-content-diff-metadata.codexbackup", firstCreatedAt);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        // 정확히 같은 thread/rollout 내용을 다시 export하되, manifest의 CreatedAtUtc만 다르게 만든다
        // — payload 바이트는 같아도 manifest.json이 달라지므로 whole-file hash는 달라진다.
        ExportPlan replan = ExportPlanBuilder.Build(pcA, new HashSet<string> { t });
        BackupManifest differentMetadata = ManifestBuilder.Build(replan, null, null, firstCreatedAt.AddDays(1));
        BackupWriter.WriteResult overwriteResult = BackupWriter.Write(replan, differentMetadata, backupPath, overwrite: true);
        Assert.True(overwriteResult.Success, overwriteResult.FailureReason);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.Null(plan);
    }

    // ── 5) CreatedAt/AppVersion/개수가 우연히 같아도 whole-file hash가 다르면 실패 ─

    [Fact]
    public void CreatedAt_AppVersion_개수까지_같아도_whole_file_hash가_다르면_Plan_생성이_실패한다()
    {
        DateTimeOffset sameCreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        string t1 = NewId();
        string aFile = WriteRollout(_pcADir, t1, null, Line(0, "content-A"));
        CodexCatalog pcA = SingleConversationCatalog(t1, [Ref(t1, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t1 }, "coincidence.codexbackup", sameCreatedAt);

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        OverwriteWithDifferentBackup(backupPath, NewId(), sameCreatedAt);

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);

        Assert.Null(plan);
    }

    // ── 6) 수동 프로젝트 경로 재지정 후에도 backup이 그대로면 Plan은 정상적으로 만들어진다 ─

    [Fact]
    public void 수동_경로_재지정_후_backup이_그대로면_Plan_생성이_성공한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)], projectId: "proj-a", projectRootPath: @"C:\Some\Root");
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "override-unchanged.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);
        ImportBackupIdentity originalIdentity = preview.SourceBackupIdentity!;

        string overridePath = Path.Combine(_outDir, "resolved-target");
        Directory.CreateDirectory(overridePath);
        ImportPreview withOverride = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "proj-a", overridePath);

        // 경로 override는 SourceBackupIdentity에 영향을 주지 않는다(요구사항 4).
        Assert.Equal(originalIdentity, withOverride.SourceBackupIdentity);

        ImportPlan? plan = ImportPlanBuilder.Build(withOverride, backupPath);

        Assert.NotNull(plan);
        Assert.Contains(plan!.Projects, p => p.TargetProjectPath == overridePath);
    }

    // ── 7) 사용자가 폴더를 고르는 동안 backup이 바뀌면 Plan 생성을 거부한다 ────────

    [Fact]
    public void 수동_경로_재지정_동안_backup이_바뀌면_Plan_생성이_실패한다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)], projectId: "proj-a", projectRootPath: @"C:\Some\Root");
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "override-then-changed.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        Assert.True(preview.Success);

        string overridePath = Path.Combine(_outDir, "resolved-target-2");
        Directory.CreateDirectory(overridePath);
        ImportPreview withOverride = ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, "proj-a", overridePath);

        // 사용자가 폴더를 고르는 사이(가상의 시간 경과) backup 파일이 바뀌었다.
        byte[] bytes = File.ReadAllBytes(backupPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(backupPath, bytes);

        ImportPlan? plan = ImportPlanBuilder.Build(withOverride, backupPath);

        Assert.Null(plan);
    }

    // ── 8) Plan 생성 후 backup이 바뀌는 경우는 기존 Phase 06_02 Preflight의 몫이다(회귀 확인) ─

    [Fact]
    public void Plan_생성_후_backup이_바뀌면_Preflight가_BackupChanged로_잡아낸다()
    {
        string t = NewId();
        string aFile = WriteRollout(_pcADir, t, null, Line(0, "hello"));
        CodexCatalog pcA = SingleConversationCatalog(t, [Ref(t, null, aFile)]);
        string backupPath = ExportToBackup(pcA, new HashSet<string> { t }, "plan-then-changed.codexbackup");

        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, EmptyCatalog());
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);

        byte[] bytes = File.ReadAllBytes(backupPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(backupPath, bytes);

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan!, EmptyCatalog());

        Assert.Equal(ImportPlanPreflightStatus.BackupChanged, result.Status);
    }
}
