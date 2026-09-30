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
using CodexBackupManager.Restore.Tests.TestSupport;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_1b — 가져온 대화의 프로젝트 목적지가 대상 PC의 <see cref="ProjectDirectory"/>(루트 경로 기준)로만
/// 판정되고, <c>threads.project_id</c>에는 실존 <c>projects.id</c>만 쓰이는지 끝까지(Apply) 확인한다.
/// 대상 PC는 <see cref="TestCodexHomeBuilder"/>로 만든 합성 Codex Home이다(<c>threads.project_id</c> 외래키 포함).
/// fresh catalog는 production과 같은 생성기(<see cref="RestoreExecutor.BuildFreshCatalog"/>)로 Apply 시점에 만든다.
/// </summary>
public sealed class ProjectTargetApplyTests : IDisposable
{
    private const string SourceProjectId = "pcA-project";
    private const string OriginalCwd = @"C:\Fixture\Proj";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-restore-target-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _pcBHome;
    private readonly string _outDir;
    private readonly string _snapshotRoot;

    public ProjectTargetApplyTests()
    {
        _pcADir = Path.Combine(_root, "pcA-rollouts");
        _pcBHome = Path.Combine(_root, "pcB-home");
        _outDir = Path.Combine(_root, "out");
        _snapshotRoot = Path.Combine(_root, "snapshots");
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

    // ── 합성 PC A / backup ────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString();

    private static readonly DateTimeOffset Ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static string RolloutContent(string threadId)
        => "{\"timestamp\":\"2026-03-04T05:06:07.000Z\",\"ordinal\":0,\"type\":\"session_meta\"," +
           "\"payload\":{\"id\":\"" + threadId + "\",\"session_id\":\"" + threadId + "\"," +
           "\"timestamp\":\"2026-03-04T05:06:07.000Z\",\"cwd\":\"C:\\\\Fixture\\\\Proj\"," +
           "\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.1.0\",\"source\":\"vscode\",\"model_provider\":\"openai\"}}\n" +
           "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\"," +
           "\"item\":{\"type\":\"UserMessage\",\"id\":\"i1\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}}\n";

    private static string RolloutFileName(string threadId) => $"rollout-{Ts:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl";

    private string NewFolder(string label)
    {
        string path = Path.Combine(_root, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private string MissingFolder(string label) => Path.Combine(_root, $"{label}-missing-{Guid.NewGuid():N}");

    /// <summary>PC A에서 프로젝트 하나(원본 루트 <paramref name="originalRoot"/>)의 대화 1개를 Export한다.</summary>
    private string ExportProjectConversation(string threadId, string originalRoot, string? sourceProjectId = SourceProjectId)
    {
        string aFile = Path.Combine(_pcADir, RolloutFileName(threadId));
        File.WriteAllText(aFile, RolloutContent(threadId));

        var entry = new ConversationEntry
        {
            ThreadId = threadId,
            Row = new ThreadRow
            {
                Id = threadId,
                ProjectId = sourceProjectId,
                ModelProvider = "openai",
                Source = "vscode",
                Cwd = OriginalCwd,
                Title = "test title",
                SandboxPolicy = "{}",
                ApprovalMode = "on-request",
                ThreadSource = "user",
                CreatedAtSeconds = 1_770_000_000,
                UpdatedAtSeconds = 1_770_000_100,
            },
            Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(sourceProjectId, ProjectAssignmentSource.StateProjectId),
        };
        var file = new RolloutFileReference(aFile, Path.GetFileName(aFile), threadId, null, Ts, IsArchived: false, RolloutFileKind.PlainJsonl);
        var chain = new ThreadChain(threadId, [file], new HistoryBaseReference?[1], null, null, null, []);
        var pcA = new CodexCatalog(
            [new ProjectEntry(sourceProjectId, "X Project", [originalRoot], [entry])],
            [entry],
            new Dictionary<string, ThreadChain> { [threadId] = chain },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 1, TimeSpan.Zero, TimeSpan.Zero));

        ExportPlan plan = ExportPlanBuilder.Build(pcA, new HashSet<string> { threadId });
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, $"{threadId}.codexbackup");
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    /// <summary>PC B에 이미 있는 사용자 대화 하나(프로젝트 그룹을 만들기 위함).</summary>
    private void InsertLocalUserThread(string threadId, string cwd, string? projectId = null)
    {
        string path = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, RolloutFileName(threadId), RolloutContent(threadId), archived: false, Ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, path, cwd, projectId: projectId, threadSource: "user");
    }

    private ImportPreview Preview(string backupPath)
    {
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, RestoreExecutor.BuildFreshCatalog(_pcBHome));
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        return preview;
    }

    private ImportPlan Plan(ImportPreview preview, string backupPath)
    {
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        Assert.NotNull(plan);
        Assert.True(plan!.IsApplyReady);
        return plan;
    }

    private RestoreResult Apply(ImportPlan plan)
        => RestoreExecutor.Apply(plan, _pcBHome, () => [], RestoreExecutor.BuildFreshCatalog, _snapshotRoot);

    private void AssertSucceeded(RestoreResult result)
        => Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");

    private object? ProjectIdOf(string threadId) => TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "project_id") is DBNull
        ? null
        : TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "project_id");

    private object? CwdOf(string threadId) => TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, "cwd");

    private static ProjectTarget TargetOf(ImportPreview preview) => Assert.Single(preview.Projects).SuggestedTarget!;

    // ── 9_1-T3 (결함 A) 대화 0개 등록 프로젝트 ────────────────────────────────

    [Fact]
    public void T3_원본_루트가_대화_0개_등록_프로젝트면_자동_연결되고_project_id와_cwd가_반영된다()
    {
        string root = NewFolder("x-root");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-x", "X", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, root, "db-x"), TargetOf(preview));
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Equal("db-x", ProjectIdOf(threadId));
        Assert.Equal(root, CwdOf(threadId));
    }

    [Fact]
    public void T3_대화_0개_등록_프로젝트_폴더를_수동_지정하면_연결된다()
    {
        string folder = NewFolder("y-root");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-y", "Y", folder);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));

        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal(ProjectTargetReason.UserSelectedRegistered, TargetOf(preview).Reason);
        Assert.Equal("db-y", TargetOf(preview).LinkDbProjectId);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Equal("db-y", ProjectIdOf(threadId));
        Assert.Equal(folder, CwdOf(threadId));
    }

    // ── 9_1-T4 (결함 B) 미등록 폴더 ───────────────────────────────────────────

    [Fact]
    public void T4_미등록_폴더를_수동_지정해도_선택_없는_이전_방식_Plan은_기타_대화로_들어가고_cwd는_원본이다()
    {
        string folder = NewFolder("unregistered");
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));

        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, TargetOf(preview).Reason);
        Assert.Equal(ProjectTargetKind.CreateNew, TargetOf(preview).Kind); // 9_5: 미리보기 제안은 새 프로젝트
        Assert.Equal(ProjectPathMappingStatus.ManuallyLinked, Assert.Single(preview.Projects).PathMapping.Status);
        // 설계 §8-9: 선택 없는 이전 방식 Plan은 0.1.3과 같다(새 프로젝트를 만들지 않는다).
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Null(ProjectIdOf(threadId));
        Assert.Equal(OriginalCwd, CwdOf(threadId));
    }

    // ── 9_1-T5 (결함 C) 레거시 ID / 레거시 전용 / Ambiguous ─────────────────────

    [Fact]
    public void T5a_레거시_ID로만_배정된_대화가_있는_프로젝트_폴더로_지정하면_매핑된_DB_ID로_연결된다()
    {
        string folder = NewFolder("legacy-mapped");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-z", "Z", folder);
        string localThread = NewId();
        InsertLocalUserThread(localThread, folder);
        TestCodexHomeBuilder.WriteGlobalState(
            _pcBHome,
            [new TestCodexHomeBuilder.LegacyProject("leg-z", "Z", folder)],
            new Dictionary<string, string> { [localThread] = "leg-z" },
            new Dictionary<string, string> { ["leg-z"] = "db-z" });

        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));
        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal("db-z", TargetOf(preview).LinkDbProjectId);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Equal("db-z", ProjectIdOf(threadId));
        Assert.Equal(folder, CwdOf(threadId));
    }

    [Fact]
    public void T5b_레거시_전용_프로젝트_폴더로_지정해도_이전_방식_Plan은_외래키_위반_없이_기타_대화로_들어간다()
    {
        string folder = NewFolder("legacy-only");
        string localThread = NewId();
        InsertLocalUserThread(localThread, folder);
        TestCodexHomeBuilder.WriteGlobalState(
            _pcBHome,
            [new TestCodexHomeBuilder.LegacyProject("leg-w", "W", folder)],
            new Dictionary<string, string> { [localThread] = "leg-w" });

        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));
        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal(ProjectTargetKind.CreateNew, TargetOf(preview).Kind); // 9_5: 레거시 전용은 새 프로젝트 제안
        Assert.Equal(ProjectTargetReason.LegacyOnlyProject, TargetOf(preview).Reason);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Null(ProjectIdOf(threadId));
        Assert.Equal(OriginalCwd, CwdOf(threadId));
    }

    [Fact]
    public void T5c_같은_루트가_여러_프로젝트에_등록된_Ambiguous_폴더는_자동으로_연결하지_않는다()
    {
        string folder = NewFolder("ambiguous");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-a1", "A1", folder);
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-a2", "A2", folder);
        InsertLocalUserThread(NewId(), folder, projectId: "db-a1");

        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));
        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal(ProjectTargetReason.AmbiguousRoot, TargetOf(preview).Reason);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Null(ProjectIdOf(threadId));
        Assert.Equal(OriginalCwd, CwdOf(threadId));
    }

    // ── 9_1-T6 Plan 이후 이 PC의 프로젝트 구성이 바뀜 ────────────────────────────

    [Fact]
    public void T6_Plan_이후_연결_대상_DB_프로젝트가_삭제되면_적용을_거부하고_아무것도_쓰지_않는다()
    {
        string root = NewFolder("deleted-root");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-del", "Del", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        TestCodexHomeBuilder.DeleteProject(_pcBHome, "db-del");
        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateFileSystemEntries(_snapshotRoot).Any());
    }

    [Fact]
    public void T6_Plan_이후_같은_루트가_새로_등록되면_적용을_거부하고_아무것도_쓰지_않는다()
    {
        string root = NewFolder("late-registered");
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-late", "Late", root);
        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Contains(RestoreOperationPlanner.ProjectTargetChangedMessage, result.Message);
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateFileSystemEntries(_snapshotRoot).Any());
    }

    // ── 9_1-T4 원본 폴더 실존·미등록 / 원본 없음 (Apply까지) ──────────────────────

    [Fact]
    public void T4_원본_폴더가_실존하지만_미등록이면_새_프로젝트_제안이고_이전_방식_Plan은_기타_대화로_들어간다()
    {
        string root = NewFolder("orig-unregistered");
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(
            ProjectTarget.Create(ProjectTargetReason.OriginalRootExistsUnregistered, CanonicalPath.Create(root).Display, Path.GetFileName(root)),
            TargetOf(preview));
        Assert.Equal(ProjectPathMappingStatus.NotFound, Assert.Single(preview.Projects).PathMapping.Status);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Null(ProjectIdOf(threadId));
        Assert.Equal(OriginalCwd, CwdOf(threadId));
    }

    [Fact]
    public void T4_원본_폴더가_없으면_OriginalRootMissing이고_기타_대화로_들어간다()
    {
        string missing = MissingFolder("orig");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-registered-but-missing", "Gone", missing);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, missing);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null), TargetOf(preview));
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Null(ProjectIdOf(threadId));
        Assert.Equal(OriginalCwd, CwdOf(threadId));
    }

    // ── 9_1-T9 구버전 백업(manifest 프로젝트 ID = PC A의 레거시 ID) ───────────────

    [Fact]
    public void T9_레거시_ID_manifest_백업도_루트_기준으로_이_PC의_DB_프로젝트에_연결되어_적용된다()
    {
        string root = NewFolder("t9");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-on-b", "X on B", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root, sourceProjectId: "0e4a4695-legacy-on-pc-a");

        ImportPreview preview = Preview(backupPath);
        Assert.Equal("0e4a4695-legacy-on-pc-a", preview.Manifest!.Projects[0].ProjectId);
        Assert.Equal("0e4a4695-legacy-on-pc-a", preview.Manifest.Conversations[0].ResolvedProjectId);
        RestoreResult result = Apply(Plan(preview, backupPath));

        AssertSucceeded(result);
        Assert.Equal("db-on-b", ProjectIdOf(threadId));
        Assert.Equal(root, CwdOf(threadId));
    }

    // ── 9_1-09 Preflight ─────────────────────────────────────────────────────

    [Fact]
    public void Preflight_연결_대상_DB_프로젝트가_사라지면_LocalStateChanged다()
    {
        string root = NewFolder("pf-deleted");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-pf", "PF", root);
        string backupPath = ExportProjectConversation(NewId(), root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        TestCodexHomeBuilder.DeleteProject(_pcBHome, "db-pf");
        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, RestoreExecutor.BuildFreshCatalog(_pcBHome));

        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, result.Status);
    }

    [Fact]
    public void Preflight_연결_대상_폴더가_사라지면_TargetPathUnavailable이다()
    {
        string root = NewFolder("pf-folder");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-pf2", "PF2", root);
        string backupPath = ExportProjectConversation(NewId(), root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        Directory.Delete(root);
        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(plan, RestoreExecutor.BuildFreshCatalog(_pcBHome));

        Assert.Equal(ImportPlanPreflightStatus.TargetPathUnavailable, result.Status);
    }

    [Fact]
    public void Preflight_ResolvedTarget이_없는_이전_방식_Plan도_기존_판정대로_Ready다()
    {
        string root = NewFolder("pf-legacy-plan");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-pf3", "PF3", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);
        ImportPlan oldStylePlan = plan with { Projects = plan.Projects.Select(p => p with { ResolvedTarget = null }).ToList() };

        ImportPlanPreflightValidator.Result result = ImportPlanPreflightValidator.Validate(oldStylePlan, RestoreExecutor.BuildFreshCatalog(_pcBHome));
        Assert.Equal(ImportPlanPreflightStatus.Ready, result.Status);

        // 이전 방식 Plan도 Apply 결과는 같다(TargetProjectPath → fresh ProjectDirectory로 DB ID 해석).
        AssertSucceeded(Apply(oldStylePlan));
        Assert.Equal("db-pf3", ProjectIdOf(threadId));
    }

    // ── 9_1-14 자동 연결 cwd = 이 PC project_roots 표기 ─────────────────────────────

    [Fact]
    public void T9_1_14_원본_루트와_등록_루트가_대소문자만_다르면_cwd는_등록_루트_표기다()
    {
        string root = NewFolder("case-root");
        string registered = root.ToUpperInvariant();
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-case", "Case", registered);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(registered, TargetOf(preview).FolderPath);
        AssertSucceeded(Apply(Plan(preview, backupPath)));

        Assert.Equal("db-case", ProjectIdOf(threadId));
        Assert.Equal(registered, CwdOf(threadId));
    }

    [Fact]
    public void T9_1_14_원본_루트에만_extended_prefix가_있으면_cwd는_접두사_없는_등록_루트_표기다()
    {
        string root = NewFolder("prefix-root");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-prefix", "Prefix", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, @"\\?\" + root);

        AssertSucceeded(Apply(Plan(Preview(backupPath), backupPath)));

        Assert.Equal("db-prefix", ProjectIdOf(threadId));
        Assert.Equal(root, CwdOf(threadId));
    }

    [Fact]
    public void T9_1_16_수동_지정_폴더도_cwd는_등록_루트_표기다()
    {
        string folder = NewFolder("manual-case");
        string registered = folder.ToUpperInvariant();
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-manual-case", "ManualCase", registered);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));

        ImportPreview preview = ImportPreviewBuilder.ApplyManualProjectPathOverride(Preview(backupPath), SourceProjectId, folder);
        Assert.Equal(registered, TargetOf(preview).FolderPath);
        AssertSucceeded(Apply(Plan(preview, backupPath)));

        Assert.Equal("db-manual-case", ProjectIdOf(threadId));
        Assert.Equal(registered, CwdOf(threadId));
    }

    // ── 9_1-12 사후 검증 강화(project_id / cwd) ─────────────────────────────────

    /// <summary>검증 직전에 방금 쓴 thread 행을 바꿔, 사후 검증이 실제로 잡아내는지 본다.</summary>
    private sealed class TamperBeforeValidationHook(Action tamper) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint point)
        {
            if (point == RestoreFaultInjectionPoint.BeforePostValidation)
            {
                tamper();
            }
        }
    }

    private RestoreResult ApplyWithTamper(ImportPlan plan, Action tamper)
        => RestoreExecutor.Apply(
            plan, _pcBHome, () => [], RestoreExecutor.BuildFreshCatalog, _snapshotRoot,
            new TamperBeforeValidationHook(tamper));

    [Fact]
    public void PostValidation_기타_대화로_계획했는데_행에_project_id가_있으면_Rollback한다()
    {
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-other", "Other", NewFolder("other"));
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        RestoreResult result = ApplyWithTamper(plan, () => TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, threadId, "project_id", "db-other"));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    [Fact]
    public void PostValidation_연결한_대화의_cwd가_대상_루트가_아니면_Rollback한다()
    {
        string root = NewFolder("pv-root");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-pv", "PV", root);
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, root);
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        RestoreResult result = ApplyWithTamper(plan, () => TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, threadId, "cwd", OriginalCwd));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    [Fact]
    public void PostValidation_기타_대화_행의_cwd가_원본이_아니면_Rollback한다()
    {
        string threadId = NewId();
        string backupPath = ExportProjectConversation(threadId, MissingFolder("orig"));
        ImportPlan plan = Plan(Preview(backupPath), backupPath);

        RestoreResult result = ApplyWithTamper(plan, () => TestCodexHomeBuilder.UpdateThreadColumn(_pcBHome, threadId, "cwd", @"C:\Somewhere\Else"));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
    }
}
