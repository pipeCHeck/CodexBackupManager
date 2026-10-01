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
using CodexBackupManager.Restore.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_5-T1 ~ T4 — 미등록 폴더로 Codex 프로젝트를 자동으로 만들고 가져온 대화를 연결한다(합성 Codex Home, 실측 스키마).
/// 공식 <c>create_project</c> 불변식(position = MAX+1, 루트 position 0, metadata '{}', idempotency 키 행), 중복 생성 금지,
/// fault injection·크래시 복구, 스키마 게이트를 끝까지(Apply) 확인한다.
/// </summary>
public sealed partial class ProjectCreateApplyTests : IDisposable
{
    private const string OriginalCwd = @"C:\Fixture\Proj";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-restore-create-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _pcBHome;
    private readonly string _outDir;
    private readonly string _snapshotRoot;

    public ProjectCreateApplyTests()
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

    private sealed record SourceConversation(string ThreadId, string? ProjectId, string ProjectName, string? Root);

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

    /// <summary>(Phase 9_3-T1) 다음 Export 때 원본 rollout 뒤에 붙일 줄(이어받기 시나리오용, thread ID → 줄들).</summary>
    private readonly Dictionary<string, string> _sourceExtraLines = new(StringComparer.OrdinalIgnoreCase);

    private string NewFolder(string label)
    {
        string path = Path.Combine(_root, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private string MissingFolder(string label) => Path.Combine(_root, $"{label}-missing-{Guid.NewGuid():N}");

    /// <summary>PC A에서 대화 여러 개(프로젝트별, <see cref="SourceConversation.ProjectId"/> = null이면 기타 대화)를 한 백업으로 Export한다.</summary>
    private string Export(params SourceConversation[] conversations)
    {
        var entries = new List<ConversationEntry>();
        var chains = new Dictionary<string, ThreadChain>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceConversation c in conversations)
        {
            string aFile = Path.Combine(_pcADir, RolloutFileName(c.ThreadId));
            File.WriteAllText(aFile, RolloutContent(c.ThreadId) + (_sourceExtraLines.TryGetValue(c.ThreadId, out string? extra) ? extra : string.Empty));
            entries.Add(new ConversationEntry
            {
                ThreadId = c.ThreadId,
                Row = new ThreadRow
                {
                    Id = c.ThreadId,
                    ProjectId = c.ProjectId,
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
                Title = new ThreadTitle(c.ThreadId, ThreadTitleSource.StateTitle),
                Project = c.ProjectId is null
                    ? new ProjectAssignment(null, ProjectAssignmentSource.Unassigned)
                    : new ProjectAssignment(c.ProjectId, ProjectAssignmentSource.StateProjectId),
            });
            var file = new RolloutFileReference(aFile, Path.GetFileName(aFile), c.ThreadId, null, Ts, IsArchived: false, RolloutFileKind.PlainJsonl);
            chains[c.ThreadId] = new ThreadChain(c.ThreadId, [file], new HistoryBaseReference?[1], null, null, null, []);
        }

        List<ProjectEntry> projects = conversations
            .GroupBy(c => c.ProjectId)
            .Select(g => new ProjectEntry(
                g.Key, g.First().ProjectName,
                g.First().Root is { } root ? [root] : [],
                entries.Where(e => g.Any(c => c.ThreadId == e.ThreadId)).ToList()))
            .ToList();
        var pcA = new CodexCatalog(projects, entries, chains, [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(entries.Count, entries.Count, projects.Count, TimeSpan.Zero, TimeSpan.Zero));

        ExportPlan plan = ExportPlanBuilder.Build(pcA, conversations.Select(c => c.ThreadId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, null, null, DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, $"{Guid.NewGuid():N}.codexbackup");
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    private ImportPreview Preview(string backupPath)
    {
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, RestoreExecutor.BuildFreshCatalog(_pcBHome));
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        return preview;
    }

    private static ImportUserChoices Choices(ImportPreview preview, IReadOnlyDictionary<string, ProjectTargetDecision>? decisions = null, IEnumerable<string>? include = null)
    {
        ImportUserChoices defaults = ImportUserChoices.CreateDefault(preview);
        var merged = new Dictionary<string, ProjectTargetDecision>(defaults.ProjectDecisions, StringComparer.Ordinal);
        foreach ((string key, ProjectTargetDecision decision) in decisions ?? new Dictionary<string, ProjectTargetDecision>())
        {
            merged[key] = decision;
        }

        return new ImportUserChoices(
            include is null ? defaults.IncludedThreadIds : include.ToHashSet(StringComparer.OrdinalIgnoreCase), merged);
    }

    private static ImportPlan Plan(ImportPreview preview, string backupPath, ImportUserChoices choices)
    {
        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath, choices);
        Assert.NotNull(plan);
        Assert.True(plan!.IsApplyReady);
        return plan;
    }

    private RestoreResult Apply(ImportPlan plan, IRestoreFaultInjectionHook? hook = null)
        => RestoreExecutor.Apply(plan, _pcBHome, () => [], RestoreExecutor.BuildFreshCatalog, _snapshotRoot, hook);

    private static void AssertSucceeded(RestoreResult result)
        => Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");

    private object? Column(string threadId, string column)
        => TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, column) is DBNull ? null : TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, threadId, column);

    private List<object?[]> Rows(string sql) => TestCodexHomeBuilder.Query(_pcBHome, sql);

    private long Count(string table) => Convert.ToInt64(Rows($"SELECT COUNT(*) FROM {table}")[0][0]);

    private static ProjectTarget TargetOf(ImportPreview preview, string? projectId)
        => preview.Projects.Single(p => p.ProjectId == projectId).SuggestedTarget!;

    private static string KeyOf(string? projectId) => ImportUserChoices.ProjectKeyOf(projectId);

    private sealed class ThrowAt(RestoreFaultInjectionPoint point) : IRestoreFaultInjectionHook
    {
        public void Check(RestoreFaultInjectionPoint current)
        {
            if (current == point)
            {
                throw new InvalidOperationException("fault injection");
            }
        }
    }

    // ── 9_5-T1 생성 + 연결 + 공식 SQL 불변식 ──────────────────────────────────────

    [Fact]
    public void T1_미등록_폴더를_지정하면_새_프로젝트를_만들고_가져온_대화를_연결한다()
    {
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-existing", "Existing", NewFolder("existing")); // position 0
        string folder = NewFolder("new-home");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", MissingFolder("orig")));
        ImportPreview preview = Preview(backupPath);
        Assert.Equal(ProjectTargetReason.OriginalRootMissing, TargetOf(preview, "pa").Reason);

        ImportPlan plan = Plan(preview, backupPath, Choices(preview, new Dictionary<string, ProjectTargetDecision> { [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) }));
        ProjectTarget frozen = plan.Projects.Single().ResolvedTarget!;
        Assert.Equal(ProjectTargetKind.CreateNew, frozen.Kind);
        Assert.Equal(ProjectTargetReason.UserSelectedUnregistered, frozen.Reason);

        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RestoreResult result = Apply(plan);
        AssertSucceeded(result);

        string display = CanonicalPath.Create(folder).Display;
        object?[] project = Assert.Single(Rows("SELECT id, name, metadata, position, created_at_ms, updated_at_ms FROM projects WHERE id <> 'db-existing'"));
        string newId = (string)project[0]!;
        Assert.True(Guid.TryParse(newId, out Guid parsed));
        Assert.Equal(7, parsed.Version);
        Assert.Equal(newId.ToLowerInvariant(), newId);
        Assert.Equal(36, newId.Length);
        Assert.Equal(Path.GetFileName(folder), project[1]);
        Assert.Equal("{}", project[2]);
        Assert.Equal(1L, project[3]); // 기존 MAX(position)=0 → 1
        Assert.Equal(project[4], project[5]);
        Assert.InRange((long)project[4]!, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        object?[] root = Assert.Single(Rows($"SELECT position, path FROM project_roots WHERE project_id = '{newId}'"));
        Assert.Equal(0L, root[0]);
        Assert.Equal(display, root[1]);
        Assert.DoesNotContain(@"\\?\", (string)root[1]!);
        Assert.False(((string)root[1]!).EndsWith('\\'));

        object?[] key = Assert.Single(Rows("SELECT key, project_id, created_at_ms FROM project_idempotency_keys"));
        string keyText = (string)key[0]!;
        Assert.Equal(newId, key[1]);
        Assert.Equal(project[4], key[2]);
        Assert.StartsWith("codex-backup-manager:import:v1:" + plan.Backup.BackupFileSha256.ToLowerInvariant() + ":", keyText);
        Assert.Equal(160, keyText.Length);
        Assert.Equal(keyText.ToLowerInvariant(), keyText);

        Assert.Equal(newId, Column(threadId, "project_id"));
        Assert.Equal(display, Column(threadId, "cwd"));

        // fresh catalog: 새 프로젝트가 DB ID와 루트로 나타나고, 가져온 대화가 그 그룹 아래 있다.
        CodexCatalog fresh = RestoreExecutor.BuildFreshCatalog(_pcBHome);
        ProjectLookupResult lookup = fresh.ProjectDirectory.FindByRoot(CanonicalPath.Create(folder));
        Assert.Equal(ProjectLookupKind.Found, lookup.Kind);
        Assert.Equal(newId, lookup.Project!.DbProjectId);
        Assert.Contains(fresh.Projects.Single(p => p.ProjectId == newId).Conversations, c => c.ThreadId == threadId);
    }

    [Fact]
    public void T1_원본_폴더가_실존하고_미등록이면_기본_제안으로_새_프로젝트를_만든다()
    {
        string root = NewFolder("orig-exists");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);

        ProjectTarget suggested = TargetOf(preview, "pa");
        Assert.Equal(ProjectTarget.Create(ProjectTargetReason.OriginalRootExistsUnregistered, CanonicalPath.Create(root).Display, Path.GetFileName(root)), suggested);

        ImportSelectionSummary summary = ImportSelection.Compute(preview, ImportUserChoices.CreateDefault(preview));
        NewProjectGroup created = Assert.Single(summary.NewProjects);
        Assert.Equal(Path.GetFileName(root), created.Name);
        Assert.Equal(0, summary.UncategorizedImportCount);

        AssertSucceeded(Apply(Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview))));
        Assert.Equal(1, Count("projects"));
        Assert.Equal(CanonicalPath.Create(root).Display, Column(threadId, "cwd"));
        Assert.NotNull(Column(threadId, "project_id"));
    }

    [Fact]
    public void T1_백업의_기타_대화_그룹에는_폴더를_지정할_수_없고_기타_대화로_가져온다()
    {
        // Phase 9_5-11 — 폴더 결정은 결정 오류라 Plan을 만들지 않는다. 기본 선택이면 프로젝트 없이 기타 대화로 들어간다.
        string folder = NewFolder("for-uncategorized");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, null, "기타 대화", null));
        ImportPreview preview = Preview(backupPath);
        Assert.Equal(ProjectTargetReason.NotApplicable, TargetOf(preview, null).Reason);

        ImportUserChoices withFolder = Choices(preview, new Dictionary<string, ProjectTargetDecision> { [ImportUserChoices.UncategorizedProjectKey] = ProjectTargetDecision.Folder(folder) });
        Assert.Contains(ImportSelection.Compute(preview, withFolder).BlockingReasons, r => r.Contains(ImportSelection.UncategorizedGroupDecisionError, StringComparison.Ordinal));
        Assert.Null(ImportPlanBuilder.Build(preview, backupPath, withFolder));

        AssertSucceeded(Apply(Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview))));
        Assert.Equal(0, Count("projects"));
        Assert.Null(Column(threadId, "project_id"));
    }

    [Fact]
    public void T1_Planner도_백업의_기타_대화_그룹으로는_프로젝트를_만들지_않는다()
    {
        // 9_5-11 방어: Selection을 거치지 않고 만든 CreateNew 목적지라도 Planner가 거부한다.
        string folder = NewFolder("forged");
        string backupPath = Export(new SourceConversation(NewId(), null, "기타 대화", null));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));
        ImportPlan forged = plan with
        {
            Projects = plan.Projects.Select(p => p with
            {
                ResolvedTarget = ProjectTarget.Create(ProjectTargetReason.UserSelectedUnregistered, CanonicalPath.Create(folder).Display, "가짜"),
            }).ToList(),
        };
        var rejections = new List<string>();
        (IReadOnlyList<PlannedProjectCreate> creates, _) = RestoreOperationPlanner.PlanProjectCreates(forged, rejections);

        Assert.Empty(creates);
        Assert.Contains(rejections, r => r.Contains("기타 대화", StringComparison.Ordinal));
    }

    [Fact]
    public void T1_이름을_편집하면_그_이름으로_만들고_앞뒤_공백은_지운다()
    {
        string root = NewFolder("rename-me");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);

        ImportPlan plan = Plan(preview, backupPath, Choices(preview, new Dictionary<string, ProjectTargetDecision>
        {
            [KeyOf("pa")] = ProjectTargetDecision.Suggested with { NewProjectName = "  한글 새 프로젝트  " },
        }));
        AssertSucceeded(Apply(plan));

        Assert.Equal("한글 새 프로젝트", Assert.Single(Rows("SELECT name FROM projects"))[0]);
    }

    [Fact]
    public void T1_빈_이름은_결정_오류라_Plan을_만들지_않고_아무것도_쓰지_않는다()
    {
        string root = NewFolder("empty-name");
        string backupPath = Export(new SourceConversation(NewId(), "pa", "A", root));
        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = Choices(preview, new Dictionary<string, ProjectTargetDecision>
        {
            [KeyOf("pa")] = ProjectTargetDecision.Suggested with { NewProjectName = "   " },
        });

        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.False(summary.CanApply);
        Assert.Contains(summary.BlockingReasons, r => r.Contains(ImportSelection.EmptyProjectNameError, StringComparison.Ordinal));
        Assert.Null(ImportPlanBuilder.Build(preview, backupPath, choices));
        Assert.Equal(0, Count("projects"));
    }

    [Fact]
    public void T1_만들지_않기를_고르면_프로젝트를_만들지_않고_기타_대화로_들어간다()
    {
        string root = NewFolder("declined");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = Choices(preview, new Dictionary<string, ProjectTargetDecision>
        {
            [KeyOf("pa")] = ProjectTargetDecision.Suggested with { CreateProject = false },
        });

        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Empty(summary.NewProjects);
        Assert.Equal(1, summary.UncategorizedImportCount);
        Assert.Equal(ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootExistsUnregistered, CanonicalPath.Create(root).Display), summary.Projects[0].Target);

        AssertSucceeded(Apply(Plan(preview, backupPath, choices)));
        Assert.Equal(0, Count("projects"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(OriginalCwd, Column(threadId, "cwd"));
    }

    // ── 9_5-T2 중복 생성 금지 ─────────────────────────────────────────────────

    [Fact]
    public void T2_같은_백업을_다시_가져오면_만든_프로젝트에_연결하고_중복으로_만들지_않는다()
    {
        string root = NewFolder("again");
        string first = NewId();
        string second = NewId();
        string backupPath = Export(new SourceConversation(first, "pa", "A", root), new SourceConversation(second, "pa", "A", root));

        ImportPreview preview1 = Preview(backupPath);
        AssertSucceeded(Apply(Plan(preview1, backupPath, Choices(preview1, include: [first]))));
        string createdId = (string)Assert.Single(Rows("SELECT id FROM projects"))[0]!;

        // 두 번째: 이제 그 루트는 등록 프로젝트다 → 새로 만들지 않고 연결(LinkExisting).
        ImportPreview preview2 = Preview(backupPath);
        Assert.Equal(ProjectTargetKind.LinkExisting, TargetOf(preview2, "pa").Kind);
        Assert.Equal(createdId, TargetOf(preview2, "pa").LinkDbProjectId);
        AssertSucceeded(Apply(Plan(preview2, backupPath, Choices(preview2, include: [second]))));

        Assert.Equal(1, Count("projects"));
        Assert.Equal(1, Count("project_roots"));
        Assert.Equal(1, Count("project_idempotency_keys"));
        Assert.Equal(createdId, Column(first, "project_id"));
        Assert.Equal(createdId, Column(second, "project_id"));
    }

    [Fact]
    public void T2_계획_후_같은_루트가_등록되면_적용을_거부하고_아무것도_쓰지_않는다()
    {
        string root = NewFolder("late");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));
        Assert.Equal(ProjectTargetKind.CreateNew, plan.Projects.Single().ResolvedTarget!.Kind);

        // 그 사이 사용자가 Codex에서 같은 폴더를 열어 프로젝트가 생겼다(표기만 다름: 대문자 + 끝 구분자).
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-late", "Late", root.ToUpperInvariant() + @"\");
        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Contains(RestoreOperationPlanner.ProjectTargetChangedMessage, result.Message);
        Assert.Equal(1, Count("projects"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.False(Directory.Exists(_snapshotRoot) && Directory.EnumerateFileSystemEntries(_snapshotRoot).Any());
    }

    [Fact]
    public void T2_같은_폴더로_지정한_백업_프로젝트_2개는_새_프로젝트_하나로_합치고_첫_이름을_쓴다()
    {
        string folder = NewFolder("shared");
        string a = NewId();
        string b = NewId();
        string backupPath = Export(
            new SourceConversation(a, "pa", "A", MissingFolder("oa")),
            new SourceConversation(b, "pb", "B", MissingFolder("ob")));
        ImportPreview preview = Preview(backupPath);
        ImportUserChoices choices = Choices(preview, new Dictionary<string, ProjectTargetDecision>
        {
            [KeyOf("pa")] = ProjectTargetDecision.Folder(folder) with { NewProjectName = "첫 이름" },
            [KeyOf("pb")] = ProjectTargetDecision.Folder(folder.ToUpperInvariant() + @"\") with { NewProjectName = "둘째 이름" },
        });

        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        NewProjectGroup group = Assert.Single(summary.NewProjects);
        Assert.True(group.HasConflictingNames);
        Assert.Equal("첫 이름", group.Name);
        Assert.Contains(summary.Warnings, w => w.Contains("새 프로젝트 하나", StringComparison.Ordinal));

        AssertSucceeded(Apply(Plan(preview, backupPath, choices)));
        object?[] project = Assert.Single(Rows("SELECT id, name FROM projects"));
        Assert.Equal("첫 이름", project[1]);
        Assert.Equal(project[0], Column(a, "project_id"));
        Assert.Equal(project[0], Column(b, "project_id"));
        Assert.Equal(1, Count("project_roots"));
        Assert.Equal(1, Count("project_idempotency_keys"));
    }

    // ── 9_5-T3 fault injection / 크래시 복구 ─────────────────────────────────────

    [Theory]
    [InlineData(RestoreFaultInjectionPoint.AfterProjectInsert)]
    [InlineData(RestoreFaultInjectionPoint.AfterThreadInsert)]
    [InlineData(RestoreFaultInjectionPoint.AfterSqliteCommit)]
    public void T3_생성_도중_실패하면_Rollback되어_프로젝트_루트_키_행이_남지_않는다(RestoreFaultInjectionPoint point)
    {
        string existingRoot = NewFolder("keep");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-keep", "Keep", existingRoot);
        string root = NewFolder("fault");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));

        RestoreResult result = Apply(plan, new ThrowAt(point));

        Assert.Equal(RestoreOutcome.RolledBack, result.Outcome);
        Assert.Equal("db-keep", Assert.Single(Rows("SELECT id FROM projects"))[0]);
        Assert.Equal(1, Count("project_roots"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    [Fact]
    public void T3_크래시_시뮬레이션_프로젝트_INSERT_직후_강제_종료되면_다음_실행에서_복구되어_행이_없다()
    {
        string root = NewFolder("crash");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));

        CrashRecoveryIntegrationTests.RunAndKillAtCrashPoint(
            _pcBHome, backupPath, RestoreFaultInjectionPoint.AfterThreadInsert, _snapshotRoot, withChoices: true);

        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot);
        Assert.Single(incomplete);
        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(incomplete[0].SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(0, Count("projects"));
        Assert.Equal(0, Count("project_roots"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    [Fact]
    public void T3_크래시_시뮬레이션_커밋_직후_강제_종료되면_다음_실행에서_프로젝트까지_되돌린다()
    {
        string root = NewFolder("crash-commit");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));

        CrashRecoveryIntegrationTests.RunAndKillAtCrashPoint(
            _pcBHome, backupPath, RestoreFaultInjectionPoint.AfterSqliteCommit, _snapshotRoot, withChoices: true);
        Assert.Equal(1, Count("projects")); // 커밋까지는 끝났다

        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot);
        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(Assert.Single(incomplete).SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(0, Count("projects"));
        Assert.Equal(0, Count("project_idempotency_keys"));
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    // ── 9_5-T4 스키마 게이트 ──────────────────────────────────────────────────

    [Fact]
    public void T4_스키마가_한_컬럼_다르면_생성을_생략하고_CreationUnsupported로_기타_대화에_넣고_경고한다()
    {
        TestCodexHomeBuilder.Execute(_pcBHome, "ALTER TABLE project_idempotency_keys RENAME COLUMN created_at_ms TO created_ms");
        string root = NewFolder("gate");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);

        Assert.Equal(ProjectCreationSupport.SchemaUnsupported, preview.LocalProjectDirectory.ProjectCreation);
        Assert.Equal(ProjectTarget.Uncategorized(ProjectTargetReason.CreationUnsupported, root), TargetOf(preview, "pa"));

        ImportSelectionSummary summary = ImportSelection.Compute(preview, ImportUserChoices.CreateDefault(preview));
        Assert.Empty(summary.NewProjects);
        Assert.Equal(1, summary.UncategorizedImportCount);
        Assert.Contains(summary.Warnings, w => w.Contains("새 프로젝트를 만들지 않습니다", StringComparison.Ordinal));

        AssertSucceeded(Apply(Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview))));
        Assert.Equal(0, Count("projects"));
        Assert.Null(Column(threadId, "project_id"));
        Assert.Equal(OriginalCwd, Column(threadId, "cwd"));
    }

    [Fact]
    public void T4_계획_후_스키마가_바뀌면_새_프로젝트_계획을_거부하고_아무것도_쓰지_않는다()
    {
        string root = NewFolder("gate-late");
        string threadId = NewId();
        string backupPath = Export(new SourceConversation(threadId, "pa", "A", root));
        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = Plan(preview, backupPath, ImportUserChoices.CreateDefault(preview));

        TestCodexHomeBuilder.Execute(_pcBHome, "ALTER TABLE projects ADD COLUMN color TEXT");
        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.DoesNotContain(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Equal(0, Count("projects"));
    }

    // ── 9_5-02 StateDatabaseWriter 단위 ───────────────────────────────────────

    private SqliteConnection OpenTarget()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = TestCodexHomeBuilder.FindStateDbPath(_pcBHome), Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static PlannedProjectCreate NewCreate(string root, string key = "codex-backup-manager:import:v1:test")
        => new(Guid.CreateVersion7().ToString("D"), "Name", CanonicalPath.Create(root).Display, key);

    [Fact]
    public void Writer_같은_멱등성_키가_있으면_그_프로젝트를_재사용하고_새로_만들지_않는다()
    {
        string root = NewFolder("idem");
        PlannedProjectCreate first = NewCreate(root);
        using (SqliteConnection connection = OpenTarget())
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            StateDatabaseWriter.ProjectCreateResult created = StateDatabaseWriter.CreateProject(connection, tx, first, 1000);
            Assert.False(created.Reused);
            StateDatabaseWriter.RecordProjectIdempotencyKey(connection, tx, first, created.ProjectId, 1000);
            tx.Commit();
        }

        PlannedProjectCreate retry = NewCreate(root) with { RootPathDisplay = root.ToUpperInvariant() };
        using (SqliteConnection connection = OpenTarget())
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            StateDatabaseWriter.ProjectCreateResult again = StateDatabaseWriter.CreateProject(connection, tx, retry, 2000);
            Assert.True(again.Reused);
            Assert.Equal(first.NewProjectId, again.ProjectId);
            tx.Commit();
        }

        Assert.Equal(1, Count("projects"));
    }

    [Fact]
    public void Writer_멱등성_키가_가리키는_프로젝트가_없거나_루트가_다르면_실패한다()
    {
        TestCodexHomeBuilder.Execute(_pcBHome, "INSERT INTO project_idempotency_keys (key, project_id, created_at_ms) VALUES ('k-orphan', 'gone', 1)");
        using SqliteConnection connection = OpenTarget();
        using SqliteTransaction tx = connection.BeginTransaction();

        Assert.Throws<InvalidOperationException>(() => StateDatabaseWriter.CreateProject(connection, tx, NewCreate(NewFolder("x"), "k-orphan"), 1));
    }

    [Fact]
    public void Writer_트랜잭션_안에서_같은_루트가_이미_있으면_표기가_달라도_실패한다()
    {
        string root = NewFolder("dup");
        TestCodexHomeBuilder.InsertProject(_pcBHome, "db-dup", "Dup", @"\\?\" + root.ToLowerInvariant() + @"\");
        using SqliteConnection connection = OpenTarget();
        using SqliteTransaction tx = connection.BeginTransaction();

        Assert.Throws<InvalidOperationException>(() => StateDatabaseWriter.CreateProject(connection, tx, NewCreate(root), 1));
    }

    [Fact]
    public void 실측_스키마_fixture는_생성_게이트를_통과한다()
    {
        using SqliteConnection connection = OpenTarget();
        var gate = SchemaCompatibilityChecker.CheckProjectCreation(connection);
        Assert.True(gate.IsSupported, string.Join(" | ", gate.Mismatches));
    }
}
