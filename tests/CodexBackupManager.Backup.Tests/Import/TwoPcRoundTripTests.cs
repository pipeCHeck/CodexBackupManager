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
/// Phase 06_01 요구사항 5 — 이 프로그램의 핵심 사용 시나리오(두 PC를 오가며 같은 대화를 이어서
/// 작업)를 처음부터 끝까지 temp fixture로 재현한다. 실제 <c>.codex</c>는 전혀 건드리지 않는다.
/// </summary>
/// <remarks>
/// 시나리오:
/// <list type="number">
/// <item>PC A snapshot: R1 = A B</item>
/// <item>PC B에서 이어 작업: R1 = A B C, R2 = D → PC A가 그 backup을 preview하면 IncomingAhead/Update</item>
/// <item>PC A가 Update됐다고 가정한 상태(R1+R2 동일) → Identical</item>
/// <item>그 상태에서 PC A만 R2에 E를 추가 → LocalAhead/Skip</item>
/// <item>공통 상태에서 PC A는 X, PC B는 Y를 추가 → Diverged/RequiresDecision</item>
/// </list>
/// </remarks>
public sealed class TwoPcRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-two-pc-roundtrip", Guid.NewGuid().ToString("N"));
    private readonly string _threadId = Guid.NewGuid().ToString();

    // backup 쪽 lineage 재구성(BackupCatalogReader)은 파일명에서 세그먼트 ID를 다시 파싱하고,
    // RolloutFileNamePattern은 세그먼트 ID도 실제 UUID 형식을 요구한다(임의 문자열 "seg2" 등은
    // 이름 규칙에 맞지 않아 조용히 무시된다 — 실제 Codex 세그먼트 ID도 항상 UUID다).
    private readonly string _segmentId = Guid.NewGuid().ToString();

    public TwoPcRoundTripTests() => Directory.CreateDirectory(_root);

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

    private string NewDir(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static string CopyInto(string sourceFile, string destDir)
    {
        string dest = Path.Combine(destDir, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, dest);
        return dest;
    }

    private static RolloutFileReference Ref(string threadId, string? segmentId, string fullPath)
        => new(fullPath, Path.GetFileName(fullPath), threadId, segmentId, DateTimeOffset.UnixEpoch, IsArchived: false, RolloutFileKind.PlainJsonl);

    private CodexCatalog SinglePcCatalog(IReadOnlyList<RolloutFileReference> files)
    {
        var entry = new ConversationEntry
        {
            ThreadId = _threadId,
            Row = new ThreadRow { Id = _threadId },
            Title = new ThreadTitle(_threadId, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
        };
        var chains = new Dictionary<string, ThreadChain>
        {
            [_threadId] = new ThreadChain(_threadId, files, new HistoryBaseReference?[files.Count], null, null, null, []),
        };
        return new CodexCatalog([], [entry], chains, [], DateTimeOffset.UtcNow, new CodexCatalogStats(files.Count, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
    }

    private string ExportToBackup(CodexCatalog catalog, string fileName)
    {
        ExportPlan plan = ExportPlanBuilder.Build(catalog, new HashSet<string> { _threadId });
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, null, null, DateTimeOffset.UtcNow);
        string dest = Path.Combine(_root, fileName);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    private ImportConversationPreview PreviewRelation(string backupPath, CodexCatalog localCatalog)
    {
        ImportPreview preview = ImportPreviewBuilder.Build(backupPath, localCatalog);
        Assert.True(preview.Success, string.Join("; ", preview.ValidationErrors));
        foreach (ImportProjectPreview project in preview.Projects)
        {
            foreach (ImportConversationPreview conversation in project.Conversations)
            {
                if (conversation.ThreadId == _threadId)
                {
                    return conversation;
                }
            }
        }

        foreach (ImportConversationPreview conversation in preview.DependencyOnlyConversations)
        {
            if (conversation.ThreadId == _threadId)
            {
                return conversation;
            }
        }

        throw new InvalidOperationException("대상 thread를 Preview에서 찾지 못했습니다.");
    }

    [Fact]
    public void 두_PC_왕복_시나리오_전체를_재현한다()
    {
        string pcADir = NewDir("pcA-snapshot");
        string pcBDir = NewDir("pcB-continued");
        string pcAUpdatedDir = NewDir("pcA-updated");
        string pcAAheadDir = NewDir("pcA-ahead");
        string pcADivergedDir = NewDir("pcA-diverged");
        string pcBDivergedDir = NewDir("pcB-diverged");

        // ── 1) PC A snapshot: R1 = A B ──────────────────────────────────────────
        string pcASnapshotR1 = WriteRollout(pcADir, _threadId, null, Line(0, "A"), Line(1, "B"));
        CodexCatalog pcASnapshotCatalog = SinglePcCatalog([Ref(_threadId, null, pcASnapshotR1)]);

        // ── 2) PC B에서 이어 작업: R1 = A B C, R2 = D ────────────────────────────
        string pcBR1 = WriteRollout(pcBDir, _threadId, null, Line(0, "A"), Line(1, "B"), Line(2, "C"));
        string pcBR2 = WriteRollout(pcBDir, _threadId, _segmentId, Line(0, "D"));
        CodexCatalog pcBCatalog = SinglePcCatalog([Ref(_threadId, null, pcBR1), Ref(_threadId, _segmentId, pcBR2)]);
        string backupFromB = ExportToBackup(pcBCatalog, "step2-pcB.codexbackup");

        // ── 3) 다시 PC A에서 PC B backup을 Preview → IncomingAhead / Update ─────
        ImportConversationPreview step3 = PreviewRelation(backupFromB, pcASnapshotCatalog);
        Assert.Equal(RevisionRelation.IncomingAhead, step3.Relation);
        Assert.Equal(ImportPlannedAction.Update, step3.PlannedAction);

        // ── 4) PC A가 Update됐다고 가정한 temp fixture(R1+R2가 PC B와 동일) → Identical ──
        string pcAUpdatedR1 = WriteRollout(pcAUpdatedDir, _threadId, null, Line(0, "A"), Line(1, "B"), Line(2, "C"));
        string pcAUpdatedR2 = WriteRollout(pcAUpdatedDir, _threadId, _segmentId, Line(0, "D"));
        CodexCatalog pcAUpdatedCatalog = SinglePcCatalog([Ref(_threadId, null, pcAUpdatedR1), Ref(_threadId, _segmentId, pcAUpdatedR2)]);

        ImportConversationPreview step4 = PreviewRelation(backupFromB, pcAUpdatedCatalog);
        Assert.Equal(RevisionRelation.Identical, step4.Relation);
        Assert.Equal(ImportPlannedAction.NoOp, step4.PlannedAction);

        // ── 5) 그 상태에서 PC A만 R2에 E를 추가로 이어씀 → LocalAhead / Skip ─────
        string pcAAheadR1 = CopyInto(pcAUpdatedR1, pcAAheadDir);
        string pcAAheadR2 = WriteRollout(pcAAheadDir, _threadId, _segmentId, Line(0, "D"), Line(1, "E"));
        CodexCatalog pcAAheadCatalog = SinglePcCatalog([Ref(_threadId, null, pcAAheadR1), Ref(_threadId, _segmentId, pcAAheadR2)]);

        ImportConversationPreview step5 = PreviewRelation(backupFromB, pcAAheadCatalog); // backupFromB는 여전히 R2=D만.
        Assert.Equal(RevisionRelation.LocalAhead, step5.Relation);
        Assert.Equal(ImportPlannedAction.Skip, step5.PlannedAction);

        // ── 6) 공통 상태(4단계)에서 PC A는 X, PC B는 Y를 각각 추가 → Diverged ────
        string pcADivergedR1 = CopyInto(pcAUpdatedR1, pcADivergedDir);
        string pcADivergedR2 = WriteRollout(pcADivergedDir, _threadId, _segmentId, Line(0, "D"), Line(1, "X"));
        CodexCatalog pcADivergedCatalog = SinglePcCatalog([Ref(_threadId, null, pcADivergedR1), Ref(_threadId, _segmentId, pcADivergedR2)]);

        string pcBDivergedR1 = CopyInto(pcAUpdatedR1, pcBDivergedDir);
        string pcBDivergedR2 = WriteRollout(pcBDivergedDir, _threadId, _segmentId, Line(0, "D"), Line(1, "Y"));
        CodexCatalog pcBDivergedCatalog = SinglePcCatalog([Ref(_threadId, null, pcBDivergedR1), Ref(_threadId, _segmentId, pcBDivergedR2)]);
        string backupFromBDiverged = ExportToBackup(pcBDivergedCatalog, "step6-pcB-diverged.codexbackup");

        ImportConversationPreview step6 = PreviewRelation(backupFromBDiverged, pcADivergedCatalog);
        Assert.Equal(RevisionRelation.Diverged, step6.Relation);
        Assert.Equal(ImportPlannedAction.RequiresDecision, step6.PlannedAction);
    }
}
