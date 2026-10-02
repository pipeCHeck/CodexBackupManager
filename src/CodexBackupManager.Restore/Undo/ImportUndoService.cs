using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Paths;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore.Undo;

/// <summary>(Phase 9_4) 기록 단위로 되돌릴 수 없는 이유.</summary>
public enum UndoUnavailableReason
{
    /// <summary>되돌릴 수 있다(항목별 전제 조건은 <see cref="UndoAssessment.Blockers"/>).</summary>
    None = 0,

    /// <summary>역연산 기록이 없는 이전 버전 기록(9_4 이전 Apply, 기록 파일이 없는 경우).</summary>
    OldFormat,

    /// <summary>사이드바 보정 기록(V1에서는 되돌리지 않는다).</summary>
    SidebarRepair,

    /// <summary>되돌리기 자체의 기록.</summary>
    UndoRecord,

    /// <summary>완료되지 않은 기록(진행 중, 실패해 되돌려짐 등).</summary>
    NotCompleted,

    /// <summary>이미 되돌렸다.</summary>
    AlreadyUndone,

    /// <summary>다른 Codex Home의 기록.</summary>
    OtherHome,

    /// <summary>기록(journal·Snapshot manifest·역연산 기록 파일)이 손상됐다.</summary>
    RecordCorrupt,
}

/// <summary>(Phase 9_4) 대화 하나가 되돌리기를 막는 이유.</summary>
public enum UndoBlockReason
{
    /// <summary>가져온 rollout 파일이 없다.</summary>
    RolloutMissing,

    /// <summary>rollout 파일이 가져온 뒤 바뀌었다(이어 씀 등).</summary>
    RolloutChanged,

    /// <summary>가져온 행이 없다.</summary>
    RowMissing,

    /// <summary>가져온 행이 바뀌었다(Desktop이 켜지기만 해도 바꾸는 값은 제외하고 비교).</summary>
    RowChanged,

    /// <summary>Codex에서 열어 본 흔적이 있다(thread_history 행, electron-persisted-atom-state 언급, session_index 줄).</summary>
    OpenedInCodex,

    /// <summary>Codex 흔적을 확인할 수 없다(파일은 있는데 스키마를 모름 등) — 보수적으로 거부.</summary>
    TraceUnknown,

    /// <summary>옮긴 대화의 위치가 옮긴 뒤 바뀌었다.</summary>
    LinkChanged,
}

/// <summary>(Phase 9_4) 새로 만든 프로젝트를 남겨 두는 이유.</summary>
public enum ProjectKeepReason
{
    /// <summary>되돌린 뒤에도 그 프로젝트를 가리키는 대화가 있다.</summary>
    ThreadsStillUseIt,

    /// <summary>Codex Desktop 배정(<c>thread-project-assignments</c>)이 그 프로젝트를 가리킨다.</summary>
    DesktopAssigned,

    /// <summary>Codex Desktop에서 고정했다(<c>pinned-project-ids</c>).</summary>
    Pinned,

    /// <summary>프로젝트의 이름·루트가 만들 때와 다르거나 프로젝트가 없다.</summary>
    ProjectChanged,

    /// <summary>사이드바(global-state) 항목이 추가했을 때와 다르다.</summary>
    SidebarEntryChanged,

    /// <summary>Codex Desktop 상태 파일을 확인하지 못했다(9_5a 게이트 실패).</summary>
    DesktopStateUnavailable,
}

/// <summary>되돌리기를 막는 대화.</summary>
public sealed record UndoBlocker(string ThreadId, UndoBlockReason Reason);

/// <summary>새로 만든 프로젝트 하나의 처리(지움/남김과 이유).</summary>
public sealed record UndoProjectDecision(string DbProjectId, string Name, bool Delete, ProjectKeepReason? KeepReason, UndoGlobalStateEntry? SidebarEntry);

/// <summary>(Phase 9_4-03) 되돌리기 판정(읽기 전용).</summary>
public sealed record UndoAssessment(
    UndoUnavailableReason Unavailable,
    IReadOnlyList<UndoBlocker> Blockers,
    IReadOnlyList<UndoProjectDecision> Projects,
    UndoRecord? Record,
    RestoreTransactionJournal? Journal)
{
    /// <summary>되돌릴 수 있는지(기록 단위 사유 없음 + 막는 대화 없음).</summary>
    public bool CanUndo => Unavailable == UndoUnavailableReason.None && Blockers.Count == 0;

    internal static UndoAssessment Unavailable_(UndoUnavailableReason reason, RestoreTransactionJournal? journal = null) => new(reason, [], [], null, journal);
}

/// <summary>(Phase 9_4-09) 되돌리기 결과.</summary>
/// <param name="Outcome">결과 분류.</param>
/// <param name="Message">사용자 안내(경로·제목 없음).</param>
/// <param name="UndoSnapshotId">되돌리기 Snapshot ID(만들었으면).</param>
/// <param name="UndoneConversationCount">되돌린 대화 수.</param>
/// <param name="DeletedProjectCount">지운 프로젝트 수.</param>
/// <param name="KeptProjects">남겨 둔 프로젝트와 이유.</param>
/// <param name="Assessment">판정(거부됐으면 이유가 여기 있다).</param>
public sealed record UndoResult(
    RestoreOutcome Outcome,
    string Message,
    string? UndoSnapshotId,
    int UndoneConversationCount,
    int DeletedProjectCount,
    IReadOnlyList<UndoProjectDecision> KeptProjects,
    UndoAssessment? Assessment);

/// <summary>
/// (Phase 9_4-03) 성공한 가져오기를 그 가져오기가 쓴 것만 역으로 처리해 되돌린다. Snapshot 사본 전체를 live에 덮어쓰지 않는다.
/// </summary>
/// <remarks>
/// <para>순서(CLAUDE.md §2.2): 프로세스 락 → Codex 실행 확인 → 미완료 작업 확인 → 판정(<see cref="Assess"/>, 하나라도 막히면 쓰기 0건) →
/// 되돌리기 전용 새 Snapshot → journal(Prepared → Undoing) → Codex 재확인 → DB 한 트랜잭션 → rollout 파일 → global-state → 사후 검증 →
/// 되돌리기 journal Completed + 가져오기 journal Undone. 도중 실패는 되돌리기 Snapshot으로 Rollback한다.</para>
/// <para>한 기록 안에 되돌릴 수 없는 대화가 하나라도 있으면 기록 전체를 거부한다(부분 되돌리기는 V1 밖). 새 프로젝트는 쓰이고 있으면 남겨 둔다.</para>
/// </remarks>
public static class ImportUndoService
{
    /// <summary>되돌리기 Snapshot manifest의 표시 접두사(뒤에 가져오기 Snapshot ID).</summary>
    public const string SnapshotMarkerPrefix = "undo:";

    /// <summary>되돌리기 실패 문구.</summary>
    public const string RolledBackMessage = "되돌리기를 취소하고 원래대로 두었습니다.";

    private const string CodexRunningMessage = "Codex가 현재 실행 중입니다. 안전한 되돌리기를 위해 Codex를 종료한 뒤 다시 시도해 주세요.";

    /// <summary>읽기 전용 판정.</summary>
    public static UndoAssessment Assess(string codexHomePath, string importSnapshotDirectory, string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importSnapshotDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);

        SnapshotManifest? manifest;
        try
        {
            manifest = SnapshotService.ReadManifest(importSnapshotDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.RecordCorrupt);
        }

        if (manifest is null)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.RecordCorrupt);
        }

        if (!CanonicalPath.AreSameLocation(manifest.CodexHomePath, codexHomePath))
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.OtherHome);
        }

        if (manifest.ImportPlanBackupSha256 == SidebarRepairService.SnapshotMarker)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.SidebarRepair);
        }

        if (manifest.ImportPlanBackupSha256.StartsWith(SnapshotMarkerPrefix, StringComparison.Ordinal))
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.UndoRecord);
        }

        RestoreTransactionJournalReadResult read = RestoreTransactionJournalStore.TryReadDetailed(importSnapshotDirectory);
        if (read.Status == RestoreTransactionJournalReadStatus.Corrupt)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.RecordCorrupt);
        }

        if (read.Status == RestoreTransactionJournalReadStatus.Missing)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.OldFormat);
        }

        RestoreTransactionJournal journal = read.Journal!;
        if (journal.State == RestoreTransactionState.Undone || IsUndoneByAnotherRecord(snapshotRoot, manifest.SnapshotId))
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.AlreadyUndone, journal);
        }

        if (journal.State != RestoreTransactionState.Completed)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.NotCompleted, journal);
        }

        if (journal.UndoRecordSha256 is not { } recordSha)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.OldFormat, journal);
        }

        if (UndoRecordStore.TryRead(importSnapshotDirectory, recordSha) is not { } record ||
            !string.Equals(record.ImportSnapshotId, manifest.SnapshotId, StringComparison.Ordinal))
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.RecordCorrupt, journal);
        }

        try
        {
            (List<UndoBlocker> blockers, List<UndoProjectDecision> projects) = CheckPreconditions(codexHomePath, record);
            return new UndoAssessment(UndoUnavailableReason.None, blockers, projects, record, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return UndoAssessment.Unavailable_(UndoUnavailableReason.RecordCorrupt, journal);
        }
    }

    /// <summary>production 진입점.</summary>
    public static UndoResult Undo(
        string codexHomePath, string importSnapshotDirectory, string? snapshotRoot = null, CodexProcessGuard.RunningProcessLister? processLister = null)
        => Undo(codexHomePath, importSnapshotDirectory, snapshotRoot ?? SnapshotService.DefaultSnapshotRoot(),
            processLister ?? CodexProcessGuard.SystemRunningProcessLister, faultInjection: null);

    /// <summary>테스트 진입점(결함 주입).</summary>
    internal static UndoResult Undo(
        string codexHomePath,
        string importSnapshotDirectory,
        string snapshotRoot,
        CodexProcessGuard.RunningProcessLister processLister,
        IRestoreFaultInjectionHook? faultInjection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importSnapshotDirectory);
        ArgumentNullException.ThrowIfNull(processLister);
        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;

        RestoreProcessLock.AcquireResult lockResult = RestoreProcessLock.TryAcquire(codexHomePath, TimeSpan.Zero);
        if (!lockResult.Acquired)
        {
            return NotReady("다른 Codex Backup Manager 인스턴스가 이 Codex Home을 처리 중입니다.");
        }

        try
        {
            return UndoLocked(codexHomePath, importSnapshotDirectory, snapshotRoot, processLister, faultInjection);
        }
        finally
        {
            lockResult.Handle!.Dispose();
        }
    }

    private static UndoResult NotReady(string message, UndoAssessment? assessment = null, string? snapshotId = null)
        => new(RestoreOutcome.NotReady, message, snapshotId, 0, 0, [], assessment);

    private static UndoResult UndoLocked(
        string home, string importDirectory, string snapshotRoot, CodexProcessGuard.RunningProcessLister processLister, IRestoreFaultInjectionHook fault)
    {
        if (CodexProcessGuard.Check(processLister).IsRunning)
        {
            return NotReady(CodexRunningMessage);
        }

        if (IncompleteApplyRecoveryService.FindIncompleteForHome(snapshotRoot, home).Count > 0)
        {
            return NotReady("이전 복원 작업이 완료되지 않았습니다. 먼저 이전 상태로 복구해야 합니다.");
        }

        UndoAssessment assessment = Assess(home, importDirectory, snapshotRoot);
        if (!assessment.CanUndo)
        {
            return NotReady(assessment.Unavailable != UndoUnavailableReason.None
                ? $"이 기록은 되돌릴 수 없습니다({assessment.Unavailable})."
                : $"되돌릴 수 없는 대화가 {assessment.Blockers.Select(b => b.ThreadId).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개 있어 아무것도 바꾸지 않았습니다.",
                assessment);
        }

        UndoRecord record = assessment.Record!;
        List<UndoProjectDecision> deletes = assessment.Projects.Where(p => p.Delete).ToList();
        List<GlobalStateWriter.GlobalStateProjectRemoval> removals = deletes
            .Where(p => p.SidebarEntry is not null)
            .Select(p => new GlobalStateWriter.GlobalStateProjectRemoval(
                p.SidebarEntry!.LegacyProjectId, p.SidebarEntry.DbProjectId, p.SidebarEntry.Name, p.SidebarEntry.RootPaths, p.SidebarEntry.CreatedAt))
            .ToList();

        // 되돌리기 전용 Snapshot: 되돌리기가 바꿀 파일 전부(레이블은 RollbackService의 temp 정리 규칙과 같은 이름을 쓴다).
        string stateDbPath = Path.Combine(home, CodexHomeLayout.FindStateDatabaseFileNames(home)[0]);
        var targets = new List<(string Label, string AbsolutePath)>
        {
            ("state-db", stateDbPath), ("state-db-wal", stateDbPath + "-wal"), ("state-db-shm", stateDbPath + "-shm"),
        };
        targets.AddRange(record.NewRolloutFiles.Select((f, i) => ($"new-rollout-{i}", f.Path)));
        targets.AddRange(record.AppendedRollouts.Select((a, i) => ($"appended-rollout-{i}", a.Path)));
        if (removals.Count > 0)
        {
            targets.Add((GlobalStateWriter.SnapshotLabel, GlobalStateProjectStep.PathFor(home)));
        }

        SnapshotCreateResult snapshot = SnapshotService.Create(snapshotRoot, home, SnapshotMarkerPrefix + record.ImportSnapshotId, targets);
        if (!snapshot.Success)
        {
            return NotReady($"되돌리기용 복구 지점을 만들지 못해 시작하지 않았습니다: {snapshot.FailureReason}", assessment);
        }

        string undoId = snapshot.Manifest!.SnapshotId;
        var journal = new RestoreTransactionJournal(undoId, home, RestoreTransactionState.Prepared, DateTimeOffset.UtcNow)
        {
            UndoOfSnapshotId = record.ImportSnapshotId,
        };
        try
        {
            RestoreTransactionJournalStore.Write(snapshot.SnapshotDirectory!, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NotReady($"되돌리기 진행 기록을 만들지 못해 시작하지 않았습니다: {ex.GetType().Name}", assessment, undoId);
        }

        try
        {
            fault.Check(RestoreFaultInjectionPoint.AfterSnapshot);
            if (CodexProcessGuard.Check(processLister).IsRunning)
            {
                RestoreExecutor.TryWriteJournalBestEffort(snapshot.SnapshotDirectory!, journal with { State = RestoreTransactionState.RolledBack, UpdatedAtUtc = DateTimeOffset.UtcNow });
                return NotReady(CodexRunningMessage, assessment, undoId);
            }

            RestoreTransactionJournalStore.Write(snapshot.SnapshotDirectory!, journal with { State = RestoreTransactionState.Undoing, UpdatedAtUtc = DateTimeOffset.UtcNow });

            List<string> deletedProjects = UndoDatabase(stateDbPath, record, deletes);
            fault.Check(RestoreFaultInjectionPoint.AfterUndoDbCommit);

            bool first = true;
            foreach (UndoNewRolloutFile file in record.NewRolloutFiles)
            {
                File.Delete(file.Path);
                if (first)
                {
                    fault.Check(RestoreFaultInjectionPoint.AfterUndoFileDelete);
                    first = false;
                }
            }

            foreach (UndoAppendedRollout appended in record.AppendedRollouts)
            {
                TruncateToBefore(appended);
                fault.Check(RestoreFaultInjectionPoint.AfterUndoTruncate);
            }

            byte[]? globalBefore = null;
            byte[]? globalAfter = null;
            if (removals.Count > 0)
            {
                string path = GlobalStateProjectStep.PathFor(home);
                globalBefore = File.ReadAllBytes(path);
                SnapshotFileEntry entry = snapshot.Manifest.Files.First(f => f.RelativeLabel == GlobalStateWriter.SnapshotLabel);
                if (!string.Equals(GlobalStateWriter.Sha256(globalBefore), entry.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(GlobalStateProjectStep.ChangedAfterPlanMessage);
                }

                globalAfter = GlobalStateWriter.RemoveLegacyProjects(globalBefore, GlobalStateProjectStep.HomeOf(home), removals);
                WriteAtomically(path, globalAfter);
                fault.Check(RestoreFaultInjectionPoint.AfterUndoGlobalStateReplace);
            }

            fault.Check(RestoreFaultInjectionPoint.BeforeUndoValidation);
            string? error = Validate(home, stateDbPath, record, assessment.Projects, deletedProjects)
                            ?? (globalBefore is not null
                                ? GlobalStateWriter.VerifyRemoval(globalBefore, File.ReadAllBytes(GlobalStateProjectStep.PathFor(home)), GlobalStateProjectStep.HomeOf(home), removals)
                                : null);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            RestoreExecutor.TryWriteJournalBestEffort(snapshot.SnapshotDirectory!, journal with { State = RestoreTransactionState.Completed, UpdatedAtUtc = DateTimeOffset.UtcNow });
            RestoreExecutor.TryWriteJournalBestEffort(importDirectory, assessment.Journal! with { State = RestoreTransactionState.Undone, UpdatedAtUtc = DateTimeOffset.UtcNow });

            int conversations = record.InsertedThreads.Select(t => t.ThreadId)
                .Concat(record.UpdatedThreads.Select(u => u.ThreadId))
                .Concat(record.AppendedRollouts.Select(a => a.ThreadId))
                .Concat(record.ThreadLinks.Select(l => l.ThreadId))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            List<UndoProjectDecision> kept = assessment.Projects.Where(p => !p.Delete).ToList();
            return new UndoResult(RestoreOutcome.Succeeded, "가져오기를 되돌렸습니다.", undoId, conversations, deletedProjects.Count, kept, assessment);
        }
        catch (Exception)
        {
            RollbackService.Result rollback = RollbackService.Rollback(snapshot.Manifest, snapshot.SnapshotDirectory!);
            if (!rollback.Success)
            {
                return new UndoResult(
                    RestoreOutcome.RollbackFailedCritical,
                    $"CRITICAL: 되돌리기 도중 실패했고 자동 복구에도 실패했습니다. 복구 지점({undoId})을 이용해 수동으로 복구해야 합니다: {rollback.FailureReason}",
                    undoId, 0, 0, [], assessment);
            }

            RestoreExecutor.TryWriteJournalBestEffort(snapshot.SnapshotDirectory!, journal with { State = RestoreTransactionState.RolledBack, UpdatedAtUtc = DateTimeOffset.UtcNow });
            return new UndoResult(RestoreOutcome.RolledBack, RolledBackMessage, undoId, 0, 0, [], assessment);
        }
    }

    // ── 판정 ─────────────────────────────────────────────────────────────────

    private static (List<UndoBlocker> Blockers, List<UndoProjectDecision> Projects) CheckPreconditions(string home, UndoRecord record)
    {
        var blockers = new List<UndoBlocker>();

        foreach (UndoNewRolloutFile file in record.NewRolloutFiles)
        {
            if (!File.Exists(file.Path))
            {
                blockers.Add(new UndoBlocker(file.OwningThreadId, UndoBlockReason.RolloutMissing));
            }
            else if (HashFile(file.Path) is var (length, sha) && (length != file.Length || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                blockers.Add(new UndoBlocker(file.OwningThreadId, UndoBlockReason.RolloutChanged));
            }
        }

        foreach (UndoAppendedRollout appended in record.AppendedRollouts)
        {
            if (!File.Exists(appended.Path))
            {
                blockers.Add(new UndoBlocker(appended.ThreadId, UndoBlockReason.RolloutMissing));
                continue;
            }

            (long length, string sha) = HashFile(appended.Path);
            if (length != appended.AfterLength || !string.Equals(sha, appended.AfterSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HashPrefix(appended.Path, appended.BeforeLength), appended.BeforeSha256, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(new UndoBlocker(appended.ThreadId, UndoBlockReason.RolloutChanged));
            }
        }

        string stateDbPath = Path.Combine(home, CodexHomeLayout.FindStateDatabaseFileNames(home)[0]);
        var projectReferences = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var projectRows = new Dictionary<string, (string? Name, List<string> Roots)>(StringComparer.Ordinal);
        using (SqliteConnection db = UndoRecordBuilder.OpenReadOnly(stateDbPath))
        {
            foreach (UndoInsertedThread inserted in record.InsertedThreads)
            {
                IReadOnlyList<UndoColumnValue>? values = ThreadRowFingerprint.ReadValues(db, inserted.ThreadId, inserted.FingerprintColumns);
                if (values is null)
                {
                    blockers.Add(new UndoBlocker(inserted.ThreadId, UndoBlockReason.RowMissing));
                }
                else if (!string.Equals(ThreadRowFingerprint.Compute(values), inserted.Fingerprint, StringComparison.Ordinal))
                {
                    blockers.Add(new UndoBlocker(inserted.ThreadId, UndoBlockReason.RowChanged));
                }
            }

            foreach (UndoUpdatedThread updated in record.UpdatedThreads)
            {
                IReadOnlyList<UndoColumnValue>? values = ThreadRowFingerprint.ReadValues(db, updated.ThreadId, updated.CheckedColumns);
                if (values is null)
                {
                    blockers.Add(new UndoBlocker(updated.ThreadId, UndoBlockReason.RowMissing));
                }
                else if (!string.Equals(ThreadRowFingerprint.Compute(values), updated.AfterFingerprint, StringComparison.Ordinal))
                {
                    blockers.Add(new UndoBlocker(updated.ThreadId, UndoBlockReason.RowChanged));
                }
            }

            foreach (UndoThreadLink link in record.ThreadLinks)
            {
                IReadOnlyList<UndoColumnValue>? values = ThreadRowFingerprint.ReadValues(db, link.ThreadId, ["project_id", "cwd"]);
                if (values is null ||
                    !string.Equals(values[0].Text, link.AfterProjectId, StringComparison.Ordinal) ||
                    values[1].Text is not { } cwd ||
                    !string.Equals(ThreadRowFingerprint.CanonicalOrRaw(cwd), ThreadRowFingerprint.CanonicalOrRaw(link.AfterCwd), StringComparison.Ordinal))
                {
                    blockers.Add(new UndoBlocker(link.ThreadId, UndoBlockReason.LinkChanged));
                }
            }

            foreach (UndoCreatedProject project in record.CreatedProjects)
            {
                projectReferences[project.DbProjectId] = ReadStrings(db, "SELECT id FROM threads WHERE project_id = $p", project.DbProjectId);
                string? name = ReadStrings(db, "SELECT name FROM projects WHERE id = $p", project.DbProjectId).FirstOrDefault();
                projectRows[project.DbProjectId] = (name, ReadStrings(db, "SELECT path FROM project_roots WHERE project_id = $p ORDER BY position", project.DbProjectId));
            }
        }

        // Codex 흔적: 가져오기 직후 기준값과 비교(연결 변경만 한 대화는 rollout을 바꾸지 않으므로 대상이 아니다).
        if (record.TraceBaselines.Count > 0)
        {
            IReadOnlyDictionary<string, UndoTraceBaseline> current = CodexTraceInspector.Count(home, record.TraceBaselines.Select(b => b.ThreadId).ToList());
            foreach (UndoTraceBaseline baseline in record.TraceBaselines)
            {
                bool? unchanged = CodexTraceInspector.Unchanged(baseline, current[baseline.ThreadId]);
                if (unchanged != true)
                {
                    blockers.Add(new UndoBlocker(baseline.ThreadId, unchanged is null ? UndoBlockReason.TraceUnknown : UndoBlockReason.OpenedInCodex));
                }
            }
        }

        return (blockers, DecideProjects(home, record, projectReferences, projectRows));
    }

    private static List<UndoProjectDecision> DecideProjects(
        string home,
        UndoRecord record,
        Dictionary<string, List<string>> references,
        Dictionary<string, (string? Name, List<string> Roots)> rows)
    {
        var decisions = new List<UndoProjectDecision>();
        if (record.CreatedProjects.Count == 0)
        {
            return decisions;
        }

        var leaving = new HashSet<string>(record.InsertedThreads.Select(t => t.ThreadId), StringComparer.OrdinalIgnoreCase);
        var relinkedAway = record.ThreadLinks.ToLookup(l => l.AfterProjectId, l => l.ThreadId, StringComparer.Ordinal);

        string globalStatePath = GlobalStateProjectStep.PathFor(home);
        byte[]? globalBytes = File.Exists(globalStatePath) ? File.ReadAllBytes(globalStatePath) : null;
        CanonicalPath homePath = GlobalStateProjectStep.HomeOf(home);
        bool gateOk = globalBytes is not null && GlobalStateProjectGate.CheckBytes(globalBytes, homePath).IsSupported;
        (HashSet<string> assignedTo, HashSet<string> pinned) = gateOk ? ReadDesktopReferences(globalBytes!) : ([], []);

        foreach (UndoCreatedProject project in record.CreatedProjects)
        {
            UndoGlobalStateEntry? entry = record.GlobalStateEntries.FirstOrDefault(e => e.DbProjectId == project.DbProjectId);
            var ids = new HashSet<string>(StringComparer.Ordinal) { project.DbProjectId };
            if (entry is not null)
            {
                ids.Add(entry.LegacyProjectId);
            }

            ProjectKeepReason? keep = null;
            (string? name, List<string> roots) = rows.TryGetValue(project.DbProjectId, out var row) ? row : (null, []);
            List<string> remaining = (references.TryGetValue(project.DbProjectId, out List<string>? refs) ? refs : [])
                .Where(id => !leaving.Contains(id) && !relinkedAway[project.DbProjectId].Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (name is null || !string.Equals(name, project.Name, StringComparison.Ordinal) || !roots.SequenceEqual(project.RootPaths, StringComparer.Ordinal))
            {
                keep = ProjectKeepReason.ProjectChanged;
            }
            else if (remaining.Count > 0)
            {
                keep = ProjectKeepReason.ThreadsStillUseIt;
            }
            else if (entry is not null && !gateOk)
            {
                keep = ProjectKeepReason.DesktopStateUnavailable;
            }
            else if (ids.Overlaps(assignedTo))
            {
                keep = ProjectKeepReason.DesktopAssigned;
            }
            else if (ids.Overlaps(pinned))
            {
                keep = ProjectKeepReason.Pinned;
            }
            else if (entry is not null && !GlobalStateWriter.CanRemove(globalBytes!, homePath,
                         new GlobalStateWriter.GlobalStateProjectRemoval(entry.LegacyProjectId, entry.DbProjectId, entry.Name, entry.RootPaths, entry.CreatedAt)))
            {
                keep = ProjectKeepReason.SidebarEntryChanged;
            }

            decisions.Add(new UndoProjectDecision(project.DbProjectId, project.Name, keep is null, keep, entry));
        }

        return decisions;
    }

    /// <summary>global-state의 배정 값(projectId)과 고정 프로젝트 ID(읽기 전용).</summary>
    private static (HashSet<string> AssignedTo, HashSet<string> Pinned) ReadDesktopReferences(byte[] bytes)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        var root = (GlobalStateJsonObject)GlobalStateJson.Parse(GlobalStateJson.StrictUtf8.GetString(bytes));
        if (root.Get("thread-project-assignments") is GlobalStateJsonObject assignments)
        {
            foreach (KeyValuePair<string, GlobalStateJsonNode> member in assignments.Members)
            {
                if (member.Value is GlobalStateJsonObject value && value.Get("projectId") is GlobalStateJsonString projectId)
                {
                    assigned.Add(projectId.Value);
                }
            }
        }

        if (root.Get("pinned-project-ids") is GlobalStateJsonArray pins)
        {
            foreach (GlobalStateJsonNode item in pins.Items)
            {
                if (item is GlobalStateJsonString id)
                {
                    pinned.Add(id.Value);
                }
            }
        }

        return (assigned, pinned);
    }

    private static bool IsUndoneByAnotherRecord(string snapshotRoot, string importSnapshotId)
    {
        if (!Directory.Exists(snapshotRoot))
        {
            return false;
        }

        foreach (string dir in Directory.EnumerateDirectories(snapshotRoot))
        {
            RestoreTransactionJournalReadResult read = RestoreTransactionJournalStore.TryReadDetailed(dir);
            if (read.Journal is { State: RestoreTransactionState.Completed, UndoOfSnapshotId: { } of } &&
                string.Equals(of, importSnapshotId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ── 역연산 ───────────────────────────────────────────────────────────────

    /// <summary>DB 한 트랜잭션: 연결 변경 복원 → UPDATE 컬럼 복원 → INSERT 행 삭제 → 새 프로젝트 삭제. 지운 프로젝트 ID를 돌려준다.</summary>
    private static List<string> UndoDatabase(string stateDbPath, UndoRecord record, List<UndoProjectDecision> deletes)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = stateDbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, Cache = SqliteCacheMode.Private };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (UndoThreadLink link in record.ThreadLinks)
        {
            Execute(connection, transaction,
                "UPDATE threads SET project_id = $before, cwd = $beforeCwd WHERE id = $id AND project_id IS $after",
                ("$before", link.BeforeProjectId), ("$beforeCwd", link.BeforeCwd), ("$id", link.ThreadId), ("$after", link.AfterProjectId));
        }

        foreach (UndoUpdatedThread updated in record.UpdatedThreads)
        {
            var parameters = new List<(string, object?)> { ("$id", updated.ThreadId) };
            var sets = new List<string>();
            foreach (UndoColumnValue value in updated.Before)
            {
                if (!value.Column.All(c => char.IsAsciiLetterLower(c) || c == '_'))
                {
                    throw new InvalidOperationException("되돌리기 기록의 컬럼 이름이 올바르지 않습니다.");
                }

                sets.Add($"{value.Column} = ${value.Column}");
                parameters.Add(("$" + value.Column, (object?)value.Integer ?? value.Text));
            }

            Execute(connection, transaction, $"UPDATE threads SET {string.Join(", ", sets)} WHERE id = $id", parameters.ToArray());
        }

        foreach (UndoInsertedThread inserted in record.InsertedThreads)
        {
            Execute(connection, transaction, "DELETE FROM threads WHERE id = $id", ("$id", inserted.ThreadId));
        }

        var deleted = new List<string>();
        foreach (UndoProjectDecision project in deletes)
        {
            UndoCreatedProject created = record.CreatedProjects.First(p => p.DbProjectId == project.DbProjectId);
            using (SqliteCommand count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM threads WHERE project_id = $p";
                count.Parameters.AddWithValue("$p", created.DbProjectId);
                if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException("지울 프로젝트를 아직 가리키는 대화가 있습니다.");
                }
            }

            Execute(connection, transaction, "DELETE FROM project_idempotency_keys WHERE key = $k AND project_id = $p",
                ("$k", created.IdempotencyKey), ("$p", created.DbProjectId));
            ExecuteAny(connection, transaction, "DELETE FROM project_roots WHERE project_id = $p", ("$p", created.DbProjectId));
            Execute(connection, transaction, "DELETE FROM projects WHERE id = $p", ("$p", created.DbProjectId));
            deleted.Add(created.DbProjectId);
        }

        transaction.Commit();
        return deleted;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        int affected = ExecuteAny(connection, transaction, sql, parameters);
        if (affected != 1)
        {
            throw new InvalidOperationException($"되돌리기 대상 행이 예상과 다릅니다(영향 행 {affected}).");
        }
    }

    private static int ExecuteAny(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    /// <summary>이어 붙인 rollout을 이전 길이로 자른다(앞부분을 temp에 복사 → flush → 해시 확인 → 원자적 교체).</summary>
    private static void TruncateToBefore(UndoAppendedRollout appended)
    {
        string temp = appended.Path + GlobalStateWriter.TempSuffix;
        try
        {
            using (FileStream source = new(appended.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream dest = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CopyPrefix(source, dest, appended.BeforeLength);
                dest.Flush(flushToDisk: true);
            }

            (long length, string sha) = HashFile(temp);
            if (length != appended.BeforeLength || !string.Equals(sha, appended.BeforeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("잘라 낸 rollout이 가져오기 이전과 다릅니다.");
            }

            File.Move(temp, appended.Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = path + GlobalStateWriter.TempSuffix;
        try
        {
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException("데스크톱 앱 상태 파일 temp 검증에 실패했습니다.");
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // ── 사후 검증 ─────────────────────────────────────────────────────────────

    private static string? Validate(string home, string stateDbPath, UndoRecord record, IReadOnlyList<UndoProjectDecision> projects, List<string> deleted)
    {
        foreach (UndoNewRolloutFile file in record.NewRolloutFiles)
        {
            if (File.Exists(file.Path))
            {
                return "지웠어야 할 rollout 파일이 남아 있습니다.";
            }
        }

        foreach (UndoAppendedRollout appended in record.AppendedRollouts)
        {
            (long length, string sha) = HashFile(appended.Path);
            if (length != appended.BeforeLength || !string.Equals(sha, appended.BeforeSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "이어 붙인 rollout이 가져오기 이전 길이·해시가 아닙니다.";
            }
        }

        using SqliteConnection db = UndoRecordBuilder.OpenReadOnly(stateDbPath);
        foreach (UndoInsertedThread inserted in record.InsertedThreads)
        {
            if (ThreadRowFingerprint.ReadValues(db, inserted.ThreadId, ["id"]) is not null)
            {
                return "지웠어야 할 대화 행이 남아 있습니다.";
            }
        }

        foreach (UndoUpdatedThread updated in record.UpdatedThreads)
        {
            IReadOnlyList<string> columns = updated.Before.Select(v => v.Column).ToList();
            IReadOnlyList<UndoColumnValue>? now = ThreadRowFingerprint.ReadValues(db, updated.ThreadId, columns);
            if (now is null || ThreadRowFingerprint.Compute(now) != ThreadRowFingerprint.Compute(updated.Before))
            {
                return "이어받은 대화의 컬럼이 이전 값으로 돌아가지 않았습니다.";
            }
        }

        foreach (UndoThreadLink link in record.ThreadLinks)
        {
            IReadOnlyList<UndoColumnValue>? now = ThreadRowFingerprint.ReadValues(db, link.ThreadId, ["project_id", "cwd"]);
            if (now is null || now[0].Text != link.BeforeProjectId || now[1].Text != link.BeforeCwd)
            {
                return "옮긴 대화가 이전 위치로 돌아가지 않았습니다.";
            }
        }

        foreach (UndoProjectDecision project in projects)
        {
            bool exists = ReadStrings(db, "SELECT id FROM projects WHERE id = $p", project.DbProjectId).Count == 1;
            if (deleted.Contains(project.DbProjectId) == exists)
            {
                return "새 프로젝트가 예상대로 지워지거나 남지 않았습니다.";
            }
        }

        db.Close();
        return ValidateCatalog(home, record, projects, deleted);
    }

    /// <summary>
    /// (Phase 9_4-10) Apply 사후 검증과 같이 fresh 카탈로그로 본다 — 되돌린 새 대화는 목록에 없고, 이어받기·옮기기를 되돌린 대화는
    /// 가져오기 직전 그룹에 있고, 지운 프로젝트는 프로젝트 목록에 없고 남겨 둔 프로젝트는 그대로 있다.
    /// </summary>
    private static string? ValidateCatalog(string home, UndoRecord record, IReadOnlyList<UndoProjectDecision> projects, List<string> deleted)
    {
        if (new CodexDetectionService().DetectFromUserSelection(home).Installation is not { } installation)
        {
            return "되돌린 뒤 Codex Home을 다시 읽을 수 없습니다.";
        }

        CodexCatalog catalog = CodexCatalogBuilder.Build(installation);
        var byId = new Dictionary<string, ConversationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ConversationEntry entry in catalog.AllConversations)
        {
            byId.TryAdd(entry.ThreadId, entry);
        }

        foreach (UndoInsertedThread inserted in record.InsertedThreads)
        {
            if (byId.ContainsKey(inserted.ThreadId))
            {
                return "되돌린 새 대화가 아직 목록에 보입니다.";
            }
        }

        foreach (UndoBeforeGroup before in record.BeforeGroups ?? [])
        {
            if (!byId.TryGetValue(before.ThreadId, out ConversationEntry? entry))
            {
                return "되돌린 대화를 목록에서 다시 읽을 수 없습니다.";
            }

            if (!string.Equals(UndoRecordBuilder.GroupKeyOf(catalog, entry), before.GroupKey, StringComparison.Ordinal))
            {
                return "되돌린 대화가 가져오기 직전 위치로 돌아가지 않았습니다.";
            }
        }

        foreach (UndoProjectDecision project in projects)
        {
            bool listed = catalog.ProjectDirectory.FindById(project.DbProjectId) is not null ||
                          catalog.ProjectDirectory.FindById(project.SidebarEntry?.LegacyProjectId) is not null;
            if (deleted.Contains(project.DbProjectId) == listed)
            {
                return "지운 프로젝트가 목록에 남아 있거나 남겨 둔 프로젝트가 목록에서 사라졌습니다.";
            }
        }

        return null;
    }

    // ── 도우미 ───────────────────────────────────────────────────────────────

    private static List<string> ReadStrings(SqliteConnection db, string sql, string parameter)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", parameter);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    private static (long Length, string Sha256) HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string HashPrefix(string path, long length)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            if (read <= 0)
            {
                return string.Empty;
            }

            hash.AppendData(chunk, 0, read);
            remaining -= read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void CopyPrefix(Stream source, Stream dest, long length)
    {
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new InvalidOperationException("rollout 파일이 예상보다 짧습니다.");
            }

            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
