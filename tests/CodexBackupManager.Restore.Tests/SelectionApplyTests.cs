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
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_2-T7 — 사용자 선택(<see cref="ImportUserChoices"/>)으로 만든 Plan을 실제로 적용한다(합성 Codex Home, 외래키 포함).
/// 충돌 대화를 빼면 나머지가 적용되고, 뺀 대화의 파일과 행은 바이트 단위로 그대로인지 본다.
/// </summary>
public sealed class SelectionApplyTests : IDisposable
{
    private static readonly DateTimeOffset Ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-selection-apply-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _pcBHome;
    private readonly string _outDir;
    private readonly string _snapshotRoot;

    public SelectionApplyTests()
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

    // ── 합성 rollout / PC A ───────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString();

    private static string Line(long ordinal, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
           "\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private static string Meta(string threadId, string? historyBaseRolloutId = null)
    {
        string historyBase = historyBaseRolloutId is null
            ? string.Empty
            : ",\"history_base\":{\"thread_id\":\"" + historyBaseRolloutId + "\",\"end_ordinal_exclusive\":2,\"end_byte_offset\":1}";
        return "{\"timestamp\":\"2026-03-04T05:06:07.000Z\",\"ordinal\":0,\"type\":\"session_meta\"," +
               "\"payload\":{\"id\":\"" + threadId + "\",\"session_id\":\"" + threadId + "\"," +
               "\"timestamp\":\"2026-03-04T05:06:07.000Z\",\"cwd\":\"C:\\\\Fixture\\\\Proj\"," +
               "\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.1.0\",\"source\":\"vscode\",\"model_provider\":\"openai\"" +
               historyBase + "}}";
    }

    private static string Content(string threadId, string? historyBase, params string[] lines)
        => Meta(threadId, historyBase) + "\n" + string.Join("\n", lines) + "\n";

    private static string FileName(string threadId) => $"rollout-{Ts:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl";

    /// <summary>PC A 대화 하나(파일 + 카탈로그 항목 + 체인).</summary>
    private sealed record SourceThread(string ThreadId, string Content, string? ParentThreadId = null, bool Selected = true);

    private string Export(params SourceThread[] threads)
    {
        var entries = new List<ConversationEntry>();
        var chains = new Dictionary<string, ThreadChain>();
        foreach (SourceThread thread in threads)
        {
            string path = Path.Combine(_pcADir, FileName(thread.ThreadId));
            File.WriteAllText(path, thread.Content);
            var file = new RolloutFileReference(path, Path.GetFileName(path), thread.ThreadId, null, Ts, IsArchived: false, RolloutFileKind.PlainJsonl);
            chains[thread.ThreadId] = new ThreadChain(
                thread.ThreadId, [file], new HistoryBaseReference?[1], thread.ParentThreadId, thread.ParentThreadId is null ? null : 2, null, []);
            entries.Add(new ConversationEntry
            {
                ThreadId = thread.ThreadId,
                Row = new ThreadRow
                {
                    Id = thread.ThreadId, ModelProvider = "openai", Source = "vscode", Cwd = @"C:\Fixture\Proj",
                    Title = "t", SandboxPolicy = "{}", ApprovalMode = "on-request", ThreadSource = "user",
                    CreatedAtSeconds = 1_770_000_000, UpdatedAtSeconds = 1_770_000_100,
                },
                Title = new ThreadTitle(thread.ThreadId, ThreadTitleSource.StateTitle),
                Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
            });
        }

        var pcA = new CodexCatalog(
            [new ProjectEntry(null, "기타 대화", [], entries)], entries, chains, [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(chains.Count, entries.Count, 1, TimeSpan.Zero, TimeSpan.Zero));
        ExportPlan plan = ExportPlanBuilder.Build(pcA, threads.Where(t => t.Selected).Select(t => t.ThreadId).ToHashSet());
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, $"{Guid.NewGuid():N}.codexbackup");
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    /// <summary>PC B에 이미 있는 대화(로컬 rollout + 행).</summary>
    private string InsertLocal(string threadId, string content)
    {
        string path = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, FileName(threadId), content, archived: false, Ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, path, @"C:\Fixture\Proj", threadSource: "user");
        return path;
    }

    private ImportPreview Preview(string backupPath)
    {
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, RestoreExecutor.BuildFreshCatalog(_pcBHome));
        Assert.True(preview.Success, string.Join(";", preview.ValidationErrors));
        return preview;
    }

    private RestoreResult Apply(ImportPlan plan)
        => RestoreExecutor.Apply(plan, _pcBHome, () => [], RestoreExecutor.BuildFreshCatalog, _snapshotRoot);

    private static void AssertSucceeded(RestoreResult result)
        => Assert.True(result.Outcome == RestoreOutcome.Succeeded, $"{result.Outcome}: {result.Message}");

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private IReadOnlyList<string> Threads() => TestCodexHomeBuilder.ReadThreadIds(_pcBHome);

    private bool RolloutExistsInSessions(string threadId)
        => Directory.EnumerateFiles(Path.Combine(_pcBHome, "sessions"), $"*{threadId}*", SearchOption.AllDirectories).Any();

    private static RevisionRelation RelationOf(ImportPreview preview, string threadId)
        => preview.Projects.SelectMany(p => p.Conversations).Concat(preview.DependencyOnlyConversations)
            .Single(c => c.ThreadId == threadId).Relation;

    // ── (a) Diverged 제외 → 나머지 적용, Diverged 로컬 무변경 ──────────────────

    [Fact]
    public void T7a_Diverged를_빼면_New_2개가_적용되고_Diverged의_로컬_rollout과_행은_그대로다()
    {
        string diverged = NewId();
        string new1 = NewId();
        string new2 = NewId();
        string backupPath = Export(
            new SourceThread(diverged, Content(diverged, null, Line(1, "incoming-branch"))),
            new SourceThread(new1, Content(new1, null, Line(1, "one"))),
            new SourceThread(new2, Content(new2, null, Line(1, "two"))));
        string localDivergedPath = InsertLocal(diverged, Content(diverged, null, Line(1, "local-branch")));
        string rolloutHashBefore = Hash(localDivergedPath);
        object? updatedBefore = TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, diverged, "updated_at");

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(RevisionRelation.Diverged, RelationOf(preview, diverged));

        // 이전 방식(선택 없음)은 전체가 막힌다.
        Assert.False(ImportPlanBuilder.Build(preview, backupPath)!.IsApplyReady);

        ImportUserChoices choices = ImportUserChoices.CreateDefault(preview);
        Assert.DoesNotContain(diverged, choices.IncludedThreadIds);
        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, choices)!;
        Assert.True(plan.IsApplyReady);
        Assert.Equal(ImportSkipReason.UserExcluded, plan.Conversations.Single(c => c.ThreadId == diverged).SkipReason);

        AssertSucceeded(Apply(plan));

        Assert.Contains(new1, Threads());
        Assert.Contains(new2, Threads());
        Assert.Equal(rolloutHashBefore, Hash(localDivergedPath));
        Assert.Equal(updatedBefore, TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, diverged, "updated_at"));
        Assert.Equal(localDivergedPath, TestCodexHomeBuilder.ReadThreadColumn(_pcBHome, diverged, "rollout_path"));
    }

    // ── (b) 제외한 New는 rollout·행이 생기지 않는다 ─────────────────────────────

    [Fact]
    public void T7b_제외한_New_대화는_rollout_파일과_threads_행이_생기지_않는다()
    {
        string keep = NewId();
        string skip = NewId();
        string backupPath = Export(
            new SourceThread(keep, Content(keep, null, Line(1, "keep"))),
            new SourceThread(skip, Content(skip, null, Line(1, "skip"))));
        ImportPreview preview = Preview(backupPath);

        var choices = new ImportUserChoices(
            new HashSet<string> { keep }, new Dictionary<string, ProjectTargetDecision>());
        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, choices)!;

        AssertSucceeded(Apply(plan));

        Assert.Contains(keep, Threads());
        Assert.DoesNotContain(skip, Threads());
        Assert.True(RolloutExistsInSessions(keep));
        Assert.False(RolloutExistsInSessions(skip));
    }

    // ── (c) 분기 자식만 선택 → 조상 자동 포함 ────────────────────────────────

    [Fact]
    public void T7c_분기_자식만_선택하면_조상이_자동_포함되어_둘_다_적용된다()
    {
        string parent = NewId();
        string child = NewId();
        string backupPath = Export(
            new SourceThread(parent, Content(parent, null, Line(1, "p1"), Line(2, "p2")), Selected: false),
            new SourceThread(child, Content(child, parent, Line(1, "c1")), ParentThreadId: parent));
        ImportPreview preview = Preview(backupPath);
        Assert.Contains(preview.DependencyOnlyConversations, c => c.ThreadId == parent);

        var choices = new ImportUserChoices(new HashSet<string> { child }, new Dictionary<string, ProjectTargetDecision>());
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.Equal(1, summary.AutoIncludedAncestorCount);
        Assert.True(summary.CanApply, string.Join(";", summary.BlockingReasons));

        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, choices)!;
        Assert.Equal(ImportPlannedAction.Import, plan.Conversations.Single(c => c.ThreadId == parent).PlannedAction);

        AssertSucceeded(Apply(plan));
        Assert.Contains(parent, Threads());
        Assert.Contains(child, Threads());
    }

    // ── (d) 제외 대화의 로컬 상태가 Plan 이후 바뀌어도 나머지 적용 ──────────────────

    [Fact]
    public void T7d_제외한_대화의_로컬이_Plan_이후_바뀌어도_나머지는_적용된다()
    {
        string identical = NewId();
        string diverged = NewId();
        string fresh = NewId();
        string identicalContent = Content(identical, null, Line(1, "same"));
        string backupPath = Export(
            new SourceThread(identical, identicalContent),
            new SourceThread(diverged, Content(diverged, null, Line(1, "incoming"))),
            new SourceThread(fresh, Content(fresh, null, Line(1, "new"))));
        string identicalPath = InsertLocal(identical, identicalContent);
        string divergedPath = InsertLocal(diverged, Content(diverged, null, Line(1, "local")));

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(RevisionRelation.Identical, RelationOf(preview, identical));
        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, ImportUserChoices.CreateDefault(preview))!;
        ImportPlan legacyPlan = ImportPlanBuilder.Build(preview, backupPath)!;

        // Plan 이후 제외한 두 대화를 Codex가 계속 이어 썼다.
        File.AppendAllText(identicalPath, Line(2, "continued") + "\n");
        File.AppendAllText(divergedPath, Line(2, "continued") + "\n");
        string identicalHash = Hash(identicalPath);
        string divergedHash = Hash(divergedPath);

        // 이전 방식 Plan이었다면 Identical의 사전조건 때문에 막혔을 상태다(9_2-21이 없으면 RED).
        ImportPlanPreflightValidator.Result legacyPreflight =
            ImportPlanPreflightValidator.Validate(legacyPlan with { HasUnresolvedDivergence = false }, RestoreExecutor.BuildFreshCatalog(_pcBHome));
        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, legacyPreflight.Status);

        AssertSucceeded(Apply(plan));
        Assert.Contains(fresh, Threads());
        Assert.Equal(identicalHash, Hash(identicalPath));
        Assert.Equal(divergedHash, Hash(divergedPath));
    }

    // ── (e) LocalAhead Skip은 기존 동작 그대로 ───────────────────────────────

    [Fact]
    public void T7e_LocalAhead는_Skip_LocalAhead이고_적용해도_로컬이_그대로다()
    {
        string ahead = NewId();
        string fresh = NewId();
        string backupPath = Export(
            new SourceThread(ahead, Content(ahead, null, Line(1, "a"))),
            new SourceThread(fresh, Content(fresh, null, Line(1, "n"))));
        string aheadPath = InsertLocal(ahead, Content(ahead, null, Line(1, "a"), Line(2, "local-more")));
        string aheadHash = Hash(aheadPath);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(RevisionRelation.LocalAhead, RelationOf(preview, ahead));
        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, ImportUserChoices.CreateDefault(preview))!;
        ImportPlanConversation aheadPlan = plan.Conversations.Single(c => c.ThreadId == ahead);
        Assert.Equal(ImportPlannedAction.Skip, aheadPlan.PlannedAction);
        Assert.Equal(ImportSkipReason.LocalAhead, aheadPlan.SkipReason);
        Assert.False(aheadPlan.IsExcludedFromApply);

        AssertSucceeded(Apply(plan));
        Assert.Contains(fresh, Threads());
        Assert.Equal(aheadHash, Hash(aheadPath));
    }

    [Fact]
    public void T7e_LocalAhead_Skip은_지금처럼_사전조건을_확인한다_로컬이_바뀌면_거부()
    {
        string ahead = NewId();
        string fresh = NewId();
        string backupPath = Export(
            new SourceThread(ahead, Content(ahead, null, Line(1, "a"))),
            new SourceThread(fresh, Content(fresh, null, Line(1, "n"))));
        string aheadPath = InsertLocal(ahead, Content(ahead, null, Line(1, "a"), Line(2, "local-more")));

        ImportPreview preview = Preview(backupPath);
        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, ImportUserChoices.CreateDefault(preview))!;
        File.AppendAllText(aheadPath, Line(3, "even-more") + "\n");

        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal(ImportPlanPreflightStatus.LocalStateChanged, result.PreflightStatus);
        Assert.DoesNotContain(fresh, Threads());
    }

    // ── closure 안의 충돌 조상은 계속 막는다 ─────────────────────────────────

    [Fact]
    public void 조상이_Diverged인_자식을_선택하면_Plan은_적용_불가이고_쓰기가_없다()
    {
        string parent = NewId();
        string child = NewId();
        string backupPath = Export(
            new SourceThread(parent, Content(parent, null, Line(1, "p1"), Line(2, "incoming")), Selected: false),
            new SourceThread(child, Content(child, parent, Line(1, "c1")), ParentThreadId: parent));
        string parentPath = InsertLocal(parent, Content(parent, null, Line(1, "p1"), Line(2, "local")));
        string parentHash = Hash(parentPath);

        ImportPreview preview = Preview(backupPath);
        Assert.Equal(RevisionRelation.Diverged, RelationOf(preview, parent));
        var choices = new ImportUserChoices(new HashSet<string> { child }, new Dictionary<string, ProjectTargetDecision>());
        ImportSelectionSummary summary = ImportSelection.Compute(preview, choices);
        Assert.False(summary.CanApply);
        Assert.Contains(summary.BlockingReasons, r => r.Contains(child) && r.Contains(parent));

        ImportPlan plan = ImportPlanBuilder.Build(preview, backupPath, choices)!;
        Assert.True(plan.HasUnresolvedDivergence);
        RestoreResult result = Apply(plan);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.DoesNotContain(child, Threads());
        Assert.Equal(parentHash, Hash(parentPath));
    }
}
