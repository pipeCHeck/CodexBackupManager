using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>(Phase 9_5a-04) 사이드바에 보이지 않는 이 앱 프로젝트 하나.</summary>
/// <param name="DbProjectId"><c>projects.id</c>.</param>
/// <param name="Name">DB 이름.</param>
/// <param name="RootPaths">DB 루트(position 순).</param>
/// <param name="CreatedAtMs">DB <c>created_at_ms</c>(레거시 항목의 createdAt·updatedAt).</param>
/// <param name="Position">DB <c>position</c>(이 순서로 <c>project-order</c> 끝에 붙인다).</param>
public sealed record SidebarRepairCandidate(string DbProjectId, string Name, IReadOnlyList<string> RootPaths, long CreatedAtMs, long Position);

/// <summary>감지 결과(읽기 전용).</summary>
/// <param name="Candidates">보정 대상(DB position 오름차순).</param>
/// <param name="GateFailure">지금 global-state 게이트 결과(<see cref="GlobalStateGateFailure.None"/>이면 보정할 수 있다).</param>
/// <param name="GlobalStateSha256">감지 때 읽은 global-state 해시(읽었으면).</param>
public sealed record SidebarRepairDetection(
    IReadOnlyList<SidebarRepairCandidate> Candidates, GlobalStateGateFailure GateFailure, string? GlobalStateSha256)
{
    /// <summary>대상이 없는 결과.</summary>
    public static SidebarRepairDetection None { get; } = new([], GlobalStateGateFailure.None, null);
}

/// <summary>보정 결과.</summary>
/// <param name="Outcome">결과 분류(<see cref="RestoreOutcome"/>와 같은 의미).</param>
/// <param name="Message">사용자 안내(경로·이름·ID 없음).</param>
/// <param name="SnapshotId">Snapshot을 만들었으면 그 ID.</param>
/// <param name="AddedCount">사이드바에 추가한 프로젝트 수.</param>
public sealed record SidebarRepairResult(RestoreOutcome Outcome, string Message, string? SnapshotId, int AddedCount);

/// <summary>
/// (Phase 9_5a-04) 이 앱이 만들었지만 Codex Desktop 사이드바(global-state 레거시 저장소)에 없는 DB 프로젝트를 찾아 추가한다.
/// </summary>
/// <remarks>
/// <para>
/// 감지(<see cref="Detect"/>)는 읽기 전용이다: idempotency key가 <c>codex-backup-manager:import:v1:</c>로 시작하는 실존 DB 프로젝트 중
/// 현재 host의 레거시 ID 매핑 값에 없는 것. Desktop이 만든 프로젝트(키 = 레거시 ID)와 지운 프로젝트의 고아 키·매핑은 대상이 아니다.
/// </para>
/// <para>
/// 보정(<see cref="Repair(string,string?,CodexProcessGuard.RunningProcessLister?)"/>)은 <see cref="RestoreExecutor"/>와 같은 구성요소만 쓴다: 프로세스 락 → 미완료 작업 확인 →
/// Codex 실행 확인 → fresh 재감지·게이트 → Snapshot(global-state) → Journal(Prepared) → Codex 재확인 → Journal(Applying) →
/// <see cref="GlobalStateWriter"/> → 검증(파일 + fresh 카탈로그 병합 + 재감지 0개) → Journal(Completed). 실패하면 Snapshot으로 Rollback한다.
/// 중단된 작업은 <see cref="IncompleteApplyRecoveryService"/>가 다른 Apply와 똑같이 찾아 복구한다. DB는 바꾸지 않는다.
/// </para>
/// </remarks>
public static class SidebarRepairService
{
    /// <summary>Snapshot manifest의 백업 해시 자리에 넣는 표시(가져오기 Snapshot과 구별용, 백업 파일이 없다).</summary>
    public const string SnapshotMarker = "sidebar-repair";

    private const string CodexRunningMessage = "Codex가 현재 실행 중입니다. 안전한 보정을 위해 Codex를 종료한 뒤 다시 시도해 주세요.";

    /// <summary>읽기 전용 감지. 읽을 수 없으면 대상 0개로 본다(예외 없음).</summary>
    public static SidebarRepairDetection Detect(string codexHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        if (!CanonicalPath.TryCreate(codexHomePath, out CanonicalPath? home, out _))
        {
            return SidebarRepairDetection.None;
        }

        IReadOnlyList<string> stateDbNames = CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath);
        if (stateDbNames.Count == 0)
        {
            return SidebarRepairDetection.None;
        }

        List<SidebarRepairCandidate> appProjects;
        using (ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(Path.Combine(codexHomePath, stateDbNames[0]), out _))
        {
            if (database is null)
            {
                return SidebarRepairDetection.None;
            }

            appProjects = ReadAppProjects(database);
        }

        if (appProjects.Count == 0)
        {
            return SidebarRepairDetection.None;
        }

        string globalStatePath = GlobalStateProjectStep.PathFor(codexHomePath);
        IReadOnlyDictionary<string, string> mapping = GlobalStateReader.ReadLegacyProjectIdMapping(globalStatePath, home!, out _);
        var mappedDbIds = new HashSet<string>(mapping.Values, StringComparer.Ordinal);
        List<SidebarRepairCandidate> candidates = appProjects.Where(p => !mappedDbIds.Contains(p.DbProjectId)).ToList();
        if (candidates.Count == 0)
        {
            return SidebarRepairDetection.None;
        }

        GlobalStateProjectGate.Result gate = GlobalStateProjectGate.Check(globalStatePath, home!);
        return new SidebarRepairDetection(candidates, gate.Failure, gate.Sha256Hex);
    }

    /// <summary>production 진입점.</summary>
    /// <param name="codexHomePath">Codex Home.</param>
    /// <param name="snapshotRoot">Snapshot 루트(기본 <see cref="SnapshotService.DefaultSnapshotRoot"/>).</param>
    /// <param name="processLister">프로세스 목록(기본 실제 목록). App이 메인 화면과 같은 목록을 넘긴다.</param>
    public static SidebarRepairResult Repair(string codexHomePath, string? snapshotRoot = null, CodexProcessGuard.RunningProcessLister? processLister = null)
        => Repair(codexHomePath, processLister ?? CodexProcessGuard.SystemRunningProcessLister, snapshotRoot, faultInjection: null, catalogBuilder: null);

    /// <summary>테스트 전용 진입점(프로세스 목록·결함 주입·카탈로그 생성기).</summary>
    internal static SidebarRepairResult Repair(
        string codexHomePath,
        CodexProcessGuard.RunningProcessLister processLister,
        string? snapshotRoot,
        IRestoreFaultInjectionHook? faultInjection,
        Func<string, CodexCatalog>? catalogBuilder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(processLister);
        snapshotRoot ??= SnapshotService.DefaultSnapshotRoot();
        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;
        catalogBuilder ??= RestoreExecutor.BuildFreshCatalog;

        if (CodexProcessGuard.Check(processLister).IsRunning)
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, CodexRunningMessage, null, 0);
        }

        RestoreProcessLock.AcquireResult lockResult = RestoreProcessLock.TryAcquire(codexHomePath, TimeSpan.Zero);
        if (!lockResult.Acquired)
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, "다른 Codex Backup Manager 인스턴스가 이 Codex Home을 처리 중입니다.", null, 0);
        }

        try
        {
            return RepairLocked(codexHomePath, processLister, snapshotRoot, faultInjection, catalogBuilder);
        }
        finally
        {
            lockResult.Handle!.Dispose();
        }
    }

    private static SidebarRepairResult RepairLocked(
        string codexHomePath,
        CodexProcessGuard.RunningProcessLister processLister,
        string snapshotRoot,
        IRestoreFaultInjectionHook faultInjection,
        Func<string, CodexCatalog> catalogBuilder)
    {
        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncompleteForHome(snapshotRoot, codexHomePath);
        if (incomplete.Count > 0)
        {
            return new SidebarRepairResult(
                RestoreOutcome.NotReady, "이전 복원 작업이 완료되지 않았습니다. 먼저 이전 상태로 복구해야 합니다.", incomplete[0].SnapshotId, 0);
        }

        if (CodexProcessGuard.Check(processLister).IsRunning)
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, CodexRunningMessage, null, 0);
        }

        // fresh 재감지(화면이 본 개수와 달라도 지금 상태 기준으로 한다).
        SidebarRepairDetection detection = Detect(codexHomePath);
        if (detection.Candidates.Count == 0)
        {
            return new SidebarRepairResult(RestoreOutcome.NothingToDo, "사이드바에 추가할 프로젝트가 없습니다.", null, 0);
        }

        if (detection.GateFailure != GlobalStateGateFailure.None || detection.GlobalStateSha256 is null)
        {
            return new SidebarRepairResult(
                RestoreOutcome.NotReady,
                $"Codex 데스크톱 앱 상태 파일이 확인한 형태와 달라 사이드바에 추가하지 않았습니다(사유: {detection.GateFailure}).",
                null, 0);
        }

        SnapshotCreateResult snapshot = SnapshotService.Create(
            snapshotRoot, codexHomePath, SnapshotMarker,
            [(GlobalStateWriter.SnapshotLabel, GlobalStateProjectStep.PathFor(codexHomePath))]);
        if (!snapshot.Success)
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, $"복구용 Snapshot을 만들지 못해 시작하지 않았습니다: {snapshot.FailureReason}", null, 0);
        }

        string snapshotId = snapshot.Manifest!.SnapshotId;
        if (!GlobalStateProjectStep.SnapshotMatches(snapshot.Manifest, detection.GlobalStateSha256))
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, GlobalStateProjectStep.ChangedAfterPlanMessage, snapshotId, 0);
        }

        try
        {
            RestoreTransactionJournalStore.Write(
                snapshot.SnapshotDirectory!,
                new RestoreTransactionJournal(snapshotId, codexHomePath, RestoreTransactionState.Prepared, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SidebarRepairResult(RestoreOutcome.NotReady, $"진행 기록을 만들지 못해 시작하지 않았습니다: {ex.GetType().Name}", snapshotId, 0);
        }

        try
        {
            faultInjection.Check(RestoreFaultInjectionPoint.AfterSnapshot);

            // 첫 쓰기 직전 Codex 실행 재확인(아직 아무것도 쓰지 않았다 → Rollback 없이 중단).
            if (CodexProcessGuard.Check(processLister).IsRunning)
            {
                TryWriteJournal(snapshot.SnapshotDirectory!, snapshotId, codexHomePath, RestoreTransactionState.RolledBack);
                return new SidebarRepairResult(RestoreOutcome.NotReady, CodexRunningMessage, snapshotId, 0);
            }

            RestoreTransactionJournalStore.Write(
                snapshot.SnapshotDirectory!,
                new RestoreTransactionJournal(snapshotId, codexHomePath, RestoreTransactionState.Applying, DateTimeOffset.UtcNow));

            List<GlobalStateProjectAddition> additions = detection.Candidates
                .OrderBy(c => c.Position)
                .ThenBy(c => c.DbProjectId, StringComparer.Ordinal)
                .Select(c => new GlobalStateProjectAddition(c.DbProjectId, c.Name, c.RootPaths, c.CreatedAtMs))
                .ToList();
            GlobalStateWriteResult write = GlobalStateProjectStep.Write(codexHomePath, detection.GlobalStateSha256, additions, faultInjection);

            faultInjection.Check(RestoreFaultInjectionPoint.BeforeGlobalStateValidation);
            string? error = GlobalStateProjectStep.VerifyFile(codexHomePath, write)
                            ?? GlobalStateProjectStep.VerifyMerged(catalogBuilder(codexHomePath).ProjectDirectory, write.Added);
            if (error is null)
            {
                var stillMissing = new HashSet<string>(Detect(codexHomePath).Candidates.Select(c => c.DbProjectId), StringComparer.Ordinal);
                if (write.Added.Any(a => stillMissing.Contains(a.DbProjectId)))
                {
                    error = "보정 뒤에도 사이드바에 없는 프로젝트가 남아 있습니다.";
                }
            }

            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            TryWriteJournal(snapshot.SnapshotDirectory!, snapshotId, codexHomePath, RestoreTransactionState.Completed);
            return new SidebarRepairResult(
                RestoreOutcome.Succeeded,
                string.Create(CultureInfo.InvariantCulture, $"Codex 사이드바에 프로젝트 {write.Added.Count}개를 추가했습니다."),
                snapshotId, write.Added.Count);
        }
        catch (Exception)
        {
            RollbackService.Result rollback = RollbackService.Rollback(snapshot.Manifest, snapshot.SnapshotDirectory!);
            if (!rollback.Success)
            {
                return new SidebarRepairResult(
                    RestoreOutcome.RollbackFailedCritical,
                    $"CRITICAL: 자동 복구에 실패했습니다. Snapshot({snapshotId})을 이용해 수동으로 복구해야 합니다: {rollback.FailureReason}",
                    snapshotId, 0);
            }

            TryWriteJournal(snapshot.SnapshotDirectory!, snapshotId, codexHomePath, RestoreTransactionState.RolledBack);
            return new SidebarRepairResult(RestoreOutcome.RolledBack, "사이드바 보정 중 오류가 발생해 이전 상태로 되돌렸습니다.", snapshotId, 0);
        }
    }

    private static List<SidebarRepairCandidate> ReadAppProjects(ReadOnlyDatabase database)
    {
        string prefix = RestoreOperationPlanner.IdempotencyKeyPrefix;
        string sql = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT p.id, p.name, p.position, p.created_at_ms FROM projects p WHERE EXISTS (SELECT 1 FROM project_idempotency_keys k " +
            $"WHERE k.project_id = p.id AND substr(k.key, 1, {prefix.Length}) = '{prefix}') ORDER BY p.position, p.id");

        IReadOnlyList<object?[]> rows;
        IReadOnlyList<object?[]> roots;
        try
        {
            rows = database.ReadRows(sql);
            roots = database.ReadRows("SELECT project_id, path FROM project_roots ORDER BY project_id, position");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return []; // 프로젝트 테이블이 없는 스키마 — 이 앱이 만든 프로젝트도 없다.
        }

        ILookup<string, string> rootsById = roots
            .Where(r => r[0] is string && r[1] is string)
            .ToLookup(r => (string)r[0]!, r => (string)r[1]!, StringComparer.Ordinal);

        var result = new List<SidebarRepairCandidate>();
        foreach (object?[] row in rows)
        {
            if (row[0] is not string id || row[1] is not string name || row[2] is null || row[3] is null)
            {
                continue;
            }

            List<string> projectRoots = rootsById[id].ToList();
            if (projectRoots.Count == 0)
            {
                continue; // 루트 없는 프로젝트는 레거시 항목을 만들 수 없다(추측하지 않는다).
            }

            result.Add(new SidebarRepairCandidate(
                id, name, projectRoots,
                Convert.ToInt64(row[3], CultureInfo.InvariantCulture),
                Convert.ToInt64(row[2], CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private static void TryWriteJournal(string snapshotDirectory, string snapshotId, string codexHomePath, RestoreTransactionState state)
    {
        try
        {
            RestoreTransactionJournalStore.Write(snapshotDirectory, new RestoreTransactionJournal(snapshotId, codexHomePath, state, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
