using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Reading;
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

    /// <summary>첫 mutation 이후 실패해 Snapshot 기반으로 정상 Rollback됐다.</summary>
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
/// Phase 7의 최상위 진입점. frozen <see cref="ImportPlan"/>을 받아 순서대로
/// (1)Codex 실행 확인 (2)fresh preflight (3)Operation Plan 생성 (4)Snapshot (5)실제 write
/// (6)post-validation을 수행하고, 첫 mutation 이후 어디서든 실패하면 Snapshot으로 Rollback한다
/// (Phase 7 요구사항 4/5/7/16/17/18).
/// </summary>
/// <remarks>
/// <b>relation을 다시 판정하지 않는다.</b> <paramref name="freshLocalCatalog"/>는 호출자가 Apply
/// 직전에 새로 만들어서 넘겨야 한다(<see cref="ImportPlanPreflightValidator"/>와 같은 계약) —
/// 이 클래스는 그 카탈로그를 다시 만들지 않는다.
/// </remarks>
public static class RestoreExecutor
{
    public static RestoreResult Apply(
        ImportPlan plan,
        string codexHomePath,
        CodexCatalog freshLocalCatalog,
        CodexProcessGuard.RunningProcessLister processLister,
        string? snapshotRoot = null,
        IRestoreFaultInjectionHook? faultInjection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(freshLocalCatalog);
        ArgumentNullException.ThrowIfNull(processLister);

        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;
        snapshotRoot ??= SnapshotService.DefaultSnapshotRoot();

        CodexProcessGuard.Result processCheck = CodexProcessGuard.Check(processLister);
        if (processCheck.IsRunning)
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                "Codex가 현재 실행 중입니다. 안전한 복원을 위해 Codex를 종료한 뒤 다시 시도해 주세요.",
                null, null);
        }

        ImportPlanPreflightValidator.Result preflight = ImportPlanPreflightValidator.Validate(plan, freshLocalCatalog, cancellationToken);
        if (!preflight.IsReady)
        {
            return new RestoreResult(RestoreOutcome.NotReady, MessageForPreflightStatus(preflight.Status), preflight.Status, null);
        }

        RestoreOperationPlanResult planResult = RestoreOperationPlanner.Build(plan, freshLocalCatalog, codexHomePath, cancellationToken);
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

        IReadOnlyList<(string Label, string AbsolutePath)> snapshotTargets = ComputeSnapshotTargets(codexHomePath, opPlan);
        SnapshotCreateResult snapshot = SnapshotService.Create(snapshotRoot, codexHomePath, plan.Backup.BackupFileSha256, snapshotTargets);
        if (!snapshot.Success)
        {
            return new RestoreResult(RestoreOutcome.NotReady, $"복구용 Snapshot을 만들지 못해 적용을 시작하지 않았습니다: {snapshot.FailureReason}", null, null);
        }

        faultInjection.Check(RestoreFaultInjectionPoint.AfterSnapshot);

        try
        {
            ExecuteMutations(plan, codexHomePath, opPlan, faultInjection, cancellationToken);

            faultInjection.Check(RestoreFaultInjectionPoint.BeforePostValidation);
            cancellationToken.ThrowIfCancellationRequested();
            RestoreValidator.Result validation = RestoreValidator.Validate(plan, codexHomePath, cancellationToken);
            if (!validation.Success)
            {
                throw new InvalidOperationException(validation.FailureReason ?? "post-apply validation 실패");
            }

            return new RestoreResult(RestoreOutcome.Succeeded, "적용이 완료됐습니다.", null, snapshot.Manifest!.SnapshotId);
        }
        catch (Exception)
        {
            // 요구사항 16: Snapshot을 만든 "이후"의 실패는 취소(OperationCanceledException 포함)든
            // 다른 예외든 구분하지 않는다 — 첫 mutation 이후에는 중간 상태를 남기지 않고 항상
            // Rollback한다. Snapshot 생성 전 취소/실패는 이 try 블록에 들어오지 않으므로(즉시 return),
            // 그 경우는 원래 예외/취소가 그대로 호출자에게 전파된다(쓰기 자체가 없었으므로 안전하다).
            RollbackService.Result rollback = RollbackService.Rollback(snapshot.Manifest!, snapshot.SnapshotDirectory!);
            return rollback.Success
                ? new RestoreResult(RestoreOutcome.RolledBack, "적용을 취소하여 이전 상태로 복원했습니다.", null, snapshot.Manifest!.SnapshotId)
                : new RestoreResult(
                    RestoreOutcome.RollbackFailedCritical,
                    $"CRITICAL: 자동 복구에 실패했습니다. Snapshot({snapshot.Manifest!.SnapshotId})을 이용해 수동으로 복구해야 합니다: {rollback.FailureReason}",
                    null, snapshot.Manifest!.SnapshotId);
        }
    }

    private static void ExecuteMutations(
        ImportPlan plan,
        string codexHomePath,
        RestoreOperationPlan opPlan,
        IRestoreFaultInjectionHook faultInjection,
        CancellationToken cancellationToken)
    {
        using BackupReader reader = BackupReader.Open(plan.Backup.BackupFilePath);

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

        if (opPlan.ThreadInserts.Count == 0 && opPlan.ThreadRolloutPathUpdates.Count == 0)
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

        transaction.Commit();
        faultInjection.Check(RestoreFaultInjectionPoint.AfterSqliteCommit);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<(string Label, string AbsolutePath)> ComputeSnapshotTargets(
        string codexHomePath, RestoreOperationPlan opPlan)
    {
        var targets = new List<(string Label, string AbsolutePath)>();

        IReadOnlyList<string> stateDbNames = CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath);
        if (stateDbNames.Count > 0 && (opPlan.ThreadInserts.Count > 0 || opPlan.ThreadRolloutPathUpdates.Count > 0))
        {
            string stateDbPath = Path.Combine(codexHomePath, stateDbNames[0]);
            targets.Add(("state-db", stateDbPath));
            string walPath = stateDbPath + "-wal";
            if (File.Exists(walPath))
            {
                targets.Add(("state-db-wal", walPath));
            }
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
