using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex.Catalog;
using Microsoft.Data.Sqlite;

namespace CodexBackupManager.Restore;

/// <summary>Apply 전체 결과.</summary>
public enum RestoreOutcome
{
    /// <summary>성공적으로 적용됐고 post-validation까지 통과했다.</summary>
    Succeeded,

    /// <summary>적용할 것이 아무것도 없었다(전부 NoOp/Skip) — write 0건, 성공으로 본다.</summary>
    NothingToDo,

    /// <summary>Codex 실행 중, fresh preflight 실패, 또는 계획 생성 거부 — write 0건.</summary>
    NotReady,

    /// <summary>사용자가 취소했다 — Snapshot 기반으로 정상 Rollback됐다.</summary>
    Cancelled,

    /// <summary>첫 mutation 이후 실제 오류(취소가 아님)로 실패해 Snapshot 기반으로 정상 Rollback됐다.</summary>
    RolledBack,

    /// <summary>Rollback 자체가 실패했다 — 수동 개입이 필요한 CRITICAL 상태.</summary>
    RollbackFailedCritical,
}

/// <summary>Apply 결과.</summary>
/// <param name="Outcome">결과 분류.</param>
/// <param name="Message">사용자에게 보여줄 안내 문구(원문 없음).</param>
/// <param name="PreflightStatus"><see cref="RestoreOutcome.NotReady"/>일 때 그 근거가 된 preflight 상태.</param>
/// <param name="SnapshotId">Snapshot을 만들었으면 그 ID.</param>
public sealed record RestoreResult(
    RestoreOutcome Outcome,
    string Message,
    ImportPlanPreflightStatus? PreflightStatus,
    string? SnapshotId);

/// <summary>
/// Phase 7/07_01의 최상위 진입점. frozen <see cref="ImportPlan"/>을 받아 순서대로
/// (1)Codex 실행 확인 (2)fresh preflight (3)backup identity pin (4)Operation Plan 생성 (5)Snapshot
/// (6)Codex 실행 재확인 (7)실제 write (8)post-validation을 수행하고, 첫 mutation 이후 어디서든
/// 실패(취소 포함)하면 Snapshot으로 Rollback한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>fresh catalog를 이 클래스가 직접 책임진다(Phase 07_01 요구사항 4).</b> production 진입점
/// (<see cref="Apply(ImportPlan,string,string?,IRestoreFaultInjectionHook?,CancellationToken)"/>)은
/// <see cref="ImportPlan"/>과 Codex Home 경로만 받는다 — 호출자가 예전에 만들어 둔 카탈로그
/// (예: App의 <c>_lastCatalog</c>)를 넘기는 구조는 만들지 않는다. "그 순간"의 카탈로그는 이
/// 메서드 안에서 <see cref="CodexDetectionService"/>/<see cref="CodexCatalogBuilder"/>로 새로
/// 만든다. 테스트는 <see cref="Apply(ImportPlan,string,CodexProcessGuard.RunningProcessLister,Func{string,CodexCatalog},string?,IRestoreFaultInjectionHook?,CancellationToken)"/>
/// (internal)로 프로세스 목록/카탈로그 빌더를 주입한다.
/// </para>
/// <para>
/// <b>backup TOCTOU를 Apply 전체에 걸쳐 닫는다(요구사항 5).</b> <see cref="PinnedBackupSource"/>를
/// Preflight 직전에 한 번만 열고, 계획 수립과 실제 적용 내내 같은 reader를 재사용한다 — Planner와
/// Executor가 각자 <c>plan.Backup.BackupFilePath</c>를 다시 여는 구조는 없앴다.
/// </para>
/// </remarks>
public static class RestoreExecutor
{
    /// <summary>production 진입점. fresh catalog는 이 메서드가 직접 만든다.</summary>
    /// <param name="onStatusChanged">
    /// UI(Phase 07_01 Apply 화면)가 진행 단계를 보여줄 수 있도록 하는 선택적 콜백. 안전성 판단에는
    /// 전혀 관여하지 않는 순수 알림용이다 — 값을 넘기지 않아도 Apply 동작은 동일하다.
    /// </param>
    public static RestoreResult Apply(
        ImportPlan plan,
        string codexHomePath,
        string? snapshotRoot = null,
        IRestoreFaultInjectionHook? faultInjection = null,
        Action<string>? onStatusChanged = null,
        CancellationToken cancellationToken = default)
        => Apply(
            plan, codexHomePath,
            CodexProcessGuard.SystemRunningProcessLister,
            BuildFreshCatalog,
            snapshotRoot, faultInjection, onStatusChanged, cancellationToken);

    /// <summary>
    /// 테스트 전용 확장 진입점(<c>InternalsVisibleTo</c>로 Restore.Tests에만 노출). 프로세스 목록과
    /// fresh catalog 생성 방법을 주입할 수 있다 — production 코드는
    /// <see cref="Apply(ImportPlan,string,string?,IRestoreFaultInjectionHook?,Action{string}?,CancellationToken)"/>만 쓴다.
    /// </summary>
    internal static RestoreResult Apply(
        ImportPlan plan,
        string codexHomePath,
        CodexProcessGuard.RunningProcessLister processLister,
        Func<string, CodexCatalog> catalogBuilder,
        string? snapshotRoot = null,
        IRestoreFaultInjectionHook? faultInjection = null,
        Action<string>? onStatusChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(processLister);
        ArgumentNullException.ThrowIfNull(catalogBuilder);

        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;
        snapshotRoot ??= SnapshotService.DefaultSnapshotRoot();
        onStatusChanged ??= static _ => { };

        onStatusChanged("안전성 확인 중");

        if (CodexProcessGuard.Check(processLister).IsRunning)
        {
            return new RestoreResult(RestoreOutcome.NotReady, CodexRunningMessage, null, null);
        }

        CodexCatalog freshLocalCatalog = catalogBuilder(codexHomePath);

        ImportPlanPreflightValidator.Result preflight = ImportPlanPreflightValidator.Validate(plan, freshLocalCatalog, cancellationToken);
        if (!preflight.IsReady)
        {
            return new RestoreResult(RestoreOutcome.NotReady, MessageForPreflightStatus(preflight.Status), preflight.Status, null);
        }

        using PinnedBackupSource pinnedBackup = PinnedBackupSource.Open(plan.Backup.BackupFilePath, cancellationToken);
        if (!pinnedBackup.MatchesExpected(plan.Backup))
        {
            return new RestoreResult(RestoreOutcome.NotReady, MessageForPreflightStatus(ImportPlanPreflightStatus.BackupChanged), ImportPlanPreflightStatus.BackupChanged, null);
        }

        RestoreOperationPlanResult planResult = RestoreOperationPlanner.Build(plan, freshLocalCatalog, codexHomePath, pinnedBackup, cancellationToken);
        if (!planResult.Success)
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                $"안전하게 적용할 수 없는 항목이 있어 적용을 시작하지 않았습니다: {string.Join("; ", planResult.RejectionReasons)}",
                null, null);
        }

        RestoreOperationPlan opPlan = planResult.Plan!;
        if (opPlan.IsEmpty)
        {
            return new RestoreResult(RestoreOutcome.NothingToDo, "적용할 변경 사항이 없습니다(모두 이미 최신 상태입니다).", null, null);
        }

        onStatusChanged("Snapshot 생성 중");
        IReadOnlyList<(string Label, string AbsolutePath)> snapshotTargets = ComputeSnapshotTargets(codexHomePath, opPlan);
        SnapshotCreateResult snapshot = SnapshotService.Create(snapshotRoot, codexHomePath, plan.Backup.BackupFileSha256, snapshotTargets);
        if (!snapshot.Success)
        {
            return new RestoreResult(RestoreOutcome.NotReady, $"복구용 Snapshot을 만들지 못해 적용을 시작하지 않았습니다: {snapshot.FailureReason}", null, null);
        }

        // 요구사항 12 — AfterSnapshot 지점은 아직 mutation 전이지만, 여기서 강제 예외가 나도
        // 바깥으로 그냥 튀지 않고 일관된 Result를 돌려줘야 한다는 정책을 정했다: 이 지점부터는
        // try 블록 안에서 실행해, 예외가 나면 (아직 아무것도 안 썼더라도) 같은 Rollback 경로를
        // 타 항상 명확한 결과(RolledBack 또는 Cancelled)를 돌려준다 — "Snapshot은 만들었는데 그
        // 다음에 뭔가 실패했다"를 애매하게 던지지 않는다.
        try
        {
            faultInjection.Check(RestoreFaultInjectionPoint.AfterSnapshot);

            // 요구사항 3 — Apply 시작 시 한 번, 그리고 Snapshot 완료 후 첫 mutation 직전에 한 번 더
            // Codex 실행 여부를 확인한다. 아직 mutation을 하나도 하지 않았으므로(Snapshot은 읽기만
            // 한다) Rollback 없이 그냥 중단해도 안전하다 — 여기서는 예외를 던지지 않고 바로 반환한다.
            if (CodexProcessGuard.Check(processLister).IsRunning)
            {
                return new RestoreResult(RestoreOutcome.NotReady, CodexRunningMessage, null, snapshot.Manifest!.SnapshotId);
            }

            onStatusChanged("적용 중");
            ExecuteMutations(codexHomePath, opPlan, pinnedBackup, faultInjection, cancellationToken);

            onStatusChanged("검증 중");
            faultInjection.Check(RestoreFaultInjectionPoint.BeforePostValidation);
            cancellationToken.ThrowIfCancellationRequested();
            RestoreValidator.Result validation = RestoreValidator.Validate(plan, opPlan, codexHomePath, cancellationToken);
            if (!validation.Success)
            {
                throw new InvalidOperationException(validation.FailureReason ?? "post-apply validation 실패");
            }

            return new RestoreResult(RestoreOutcome.Succeeded, "적용이 완료됐습니다.", null, snapshot.Manifest!.SnapshotId);
        }
        catch (Exception ex)
        {
            // 요구사항 13: 취소와 실제 오류를 구분해서 사용자에게 보여준다. 어느 쪽이든 Snapshot을
            // 만든 "이후"의 실패는 항상 Rollback한다 — 원문/path는 메시지에 담지 않는다.
            bool wasCancelled = ex is OperationCanceledException;
            onStatusChanged("Rollback 중");
            RollbackService.Result rollback = RollbackService.Rollback(snapshot.Manifest!, snapshot.SnapshotDirectory!);

            if (!rollback.Success)
            {
                return new RestoreResult(
                    RestoreOutcome.RollbackFailedCritical,
                    $"CRITICAL: 자동 복구에 실패했습니다. Snapshot({snapshot.Manifest!.SnapshotId})을 이용해 수동으로 복구해야 합니다: {rollback.FailureReason}",
                    null, snapshot.Manifest!.SnapshotId);
            }

            return wasCancelled
                ? new RestoreResult(RestoreOutcome.Cancelled, "적용을 취소하여 이전 상태로 복원했습니다.", null, snapshot.Manifest!.SnapshotId)
                : new RestoreResult(RestoreOutcome.RolledBack, "적용 중 오류가 발생해 이전 상태로 복원했습니다.", null, snapshot.Manifest!.SnapshotId);
        }
    }

    private static CodexCatalog BuildFreshCatalog(string codexHomePath)
    {
        var detection = new CodexDetectionService().DetectFromUserSelection(codexHomePath);
        if (detection.Installation is not { } installation)
        {
            throw new InvalidOperationException("Codex Home을 다시 확인할 수 없습니다.");
        }

        return CodexCatalogBuilder.Build(installation);
    }

    private static void ExecuteMutations(
        string codexHomePath,
        RestoreOperationPlan opPlan,
        PinnedBackupSource pinnedBackup,
        IRestoreFaultInjectionHook faultInjection,
        CancellationToken cancellationToken)
    {
        Backup.Reading.BackupReader reader = pinnedBackup.Reader;

        bool firstRolloutDone = false;
        foreach (PlannedNewRolloutFile newFile in opPlan.NewRolloutFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RolloutRestoreService.CreateNewFile(reader, newFile);
            if (!firstRolloutDone)
            {
                faultInjection.Check(RestoreFaultInjectionPoint.AfterFirstRolloutCreate);
                firstRolloutDone = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        foreach (PlannedRolloutAppend append in opPlan.RolloutAppends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RolloutRestoreService.AppendToFile(reader, append);
            faultInjection.Check(RestoreFaultInjectionPoint.AfterRolloutAppend);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (opPlan.ThreadInserts.Count == 0 && opPlan.ThreadRolloutPathUpdates.Count == 0 && opPlan.ThreadMetadataUpdates.Count == 0)
        {
            return;
        }

        faultInjection.Check(RestoreFaultInjectionPoint.BeforeSqliteTransaction);
        cancellationToken.ThrowIfCancellationRequested();

        string stateDbPath = Path.Combine(codexHomePath, CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath)[0]);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = stateDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        // Codex 자신의 연결 설정을 따른다(WAL + 5초 busy timeout, docs/safe-restore-phase7.md §1.H).
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (PlannedThreadInsert insert in opPlan.ThreadInserts)
        {
            StateDatabaseWriter.InsertThread(connection, transaction, insert);
        }

        foreach (PlannedThreadRolloutPathUpdate update in opPlan.ThreadRolloutPathUpdates)
        {
            StateDatabaseWriter.UpdateRolloutPath(connection, transaction, update);
        }

        foreach (PlannedThreadMetadataUpdate metadataUpdate in opPlan.ThreadMetadataUpdates)
        {
            StateDatabaseWriter.UpdateMetadata(connection, transaction, metadataUpdate);
        }

        transaction.Commit();
        faultInjection.Check(RestoreFaultInjectionPoint.AfterSqliteCommit);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<(string Label, string AbsolutePath)> ComputeSnapshotTargets(
        string codexHomePath, RestoreOperationPlan opPlan)
    {
        var targets = new List<(string Label, string AbsolutePath)>();

        bool hasSqlWrite = opPlan.ThreadInserts.Count > 0 || opPlan.ThreadRolloutPathUpdates.Count > 0 || opPlan.ThreadMetadataUpdates.Count > 0;
        IReadOnlyList<string> stateDbNames = CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath);
        if (stateDbNames.Count > 0 && hasSqlWrite)
        {
            // 요구사항 6 — SQL write가 있으면 state DB 본체뿐 아니라 -wal/-shm도 항상 snapshot
            // 대상에 등록한다. Restore가 SQLite를 열면서 이 sidecar들을 "새로" 만들 수 있는데,
            // 원래 없었다면 ExistedBefore=false로 기록돼야 Rollback 시 정확히 지울 수 있다 —
            // 존재 여부 판정 자체는 SnapshotService가 한다(여기서는 항상 등록만 한다).
            string stateDbPath = Path.Combine(codexHomePath, stateDbNames[0]);
            targets.Add(("state-db", stateDbPath));
            targets.Add(("state-db-wal", stateDbPath + "-wal"));
            targets.Add(("state-db-shm", stateDbPath + "-shm"));
        }

        foreach (PlannedNewRolloutFile newFile in opPlan.NewRolloutFiles)
        {
            targets.Add(($"new-rollout-{targets.Count}", newFile.TargetAbsolutePath));
        }

        foreach (PlannedRolloutAppend append in opPlan.RolloutAppends)
        {
            targets.Add(($"appended-rollout-{targets.Count}", append.TargetAbsolutePath));
        }

        return targets;
    }

    private const string CodexRunningMessage = "Codex가 현재 실행 중입니다. 안전한 복원을 위해 Codex를 종료한 뒤 다시 시도해 주세요.";

    private static string MessageForPreflightStatus(ImportPlanPreflightStatus status) => status switch
    {
        ImportPlanPreflightStatus.BackupChanged => "백업 파일이 변경되었습니다. 다시 불러와 주세요.",
        ImportPlanPreflightStatus.LocalStateChanged => "미리보기 이후 Codex 대화가 변경되었습니다. 다시 불러와 주세요.",
        ImportPlanPreflightStatus.TargetPathUnavailable => "프로젝트 경로를 다시 지정해 주세요.",
        ImportPlanPreflightStatus.Blocked => "안전하게 확인할 수 없는 대화가 있습니다.",
        ImportPlanPreflightStatus.UnresolvedDivergence => "분기 충돌을 해결하기 전에는 적용할 수 없습니다.",
        _ => "지금은 적용할 수 없습니다.",
    };
}
