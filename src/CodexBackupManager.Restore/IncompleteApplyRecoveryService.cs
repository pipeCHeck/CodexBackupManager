using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexBackupManager.Restore;

/// <summary>완료되지 못하고 중단된 이전 Apply 시도 하나(Phase 07_02 요구사항 7).</summary>
/// <param name="SnapshotDirectory">그 Apply가 만든 Snapshot 디렉터리 전체 경로.</param>
/// <param name="Journal">그 안의 transaction journal.</param>
public sealed record IncompleteApply(string SnapshotDirectory, RestoreTransactionJournal Journal);

/// <summary>
/// 앱 시작 시(또는 새 Apply를 시작하기 전) <see cref="RestoreTransactionState.Applying"/> 상태로
/// 남아 있는 이전 Apply가 있는지 찾고, 사용자가 명시적으로 승인하면 그 Snapshot으로 Rollback한다
/// (Phase 07_02 요구사항 7 — "사용자 모르게 자동으로 덮어쓰지 않는다").
/// </summary>
public static class IncompleteApplyRecoveryService
{
    /// <summary>
    /// <paramref name="snapshotRoot"/> 아래의 모든 Snapshot을 훑어 <see cref="RestoreTransactionState.Applying"/>
    /// 상태로 멈춰 있는 것을 전부 찾는다(최신순). manifest/journal이 없거나 손상된 디렉터리는
    /// 조용히 건너뛴다 — 이 스캔 자체가 실패하면 안 된다(단순 진단 목적이므로).
    /// </summary>
    public static IReadOnlyList<IncompleteApply> FindIncomplete(string snapshotRoot)
    {
        var found = new List<IncompleteApply>();

        if (!Directory.Exists(snapshotRoot))
        {
            return found;
        }

        foreach (string snapshotDir in Directory.EnumerateDirectories(snapshotRoot))
        {
            RestoreTransactionJournal? journal = RestoreTransactionJournalStore.TryRead(snapshotDir);
            if (journal is { State: RestoreTransactionState.Applying })
            {
                found.Add(new IncompleteApply(snapshotDir, journal));
            }
        }

        return [.. found.OrderByDescending(f => f.Journal.UpdatedAtUtc)];
    }

    /// <summary>
    /// 사용자가 "[이전 상태로 복구]"를 명시적으로 눌렀을 때만 부른다. Codex가 실행 중이면 즉시
    /// 거부한다(강제 종료하지 않는다). Snapshot manifest를 다시 읽어 <see cref="RollbackService"/>로
    /// byte-for-byte 복구하고, 성공하면 journal을 <see cref="RestoreTransactionState.RolledBack"/>으로
    /// 갱신한다.
    /// </summary>
    public static RestoreResult Recover(
        string snapshotDirectory,
        CodexProcessGuard.RunningProcessLister? processLister = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        processLister ??= CodexProcessGuard.SystemRunningProcessLister;

        if (CodexProcessGuard.Check(processLister).IsRunning)
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                "Codex가 현재 실행 중입니다. 안전한 복구를 위해 Codex를 종료한 뒤 다시 시도해 주세요.",
                null, null);
        }

        SnapshotManifest? manifest = SnapshotService.ReadManifest(snapshotDirectory);
        if (manifest is null)
        {
            return new RestoreResult(
                RestoreOutcome.RollbackFailedCritical,
                "CRITICAL: Snapshot manifest를 찾을 수 없어 자동 복구할 수 없습니다. 수동으로 확인해야 합니다.",
                null, null);
        }

        RollbackService.Result rollback = RollbackService.Rollback(manifest, snapshotDirectory);
        if (!rollback.Success)
        {
            return new RestoreResult(
                RestoreOutcome.RollbackFailedCritical,
                $"CRITICAL: 이전 복원 작업을 복구하지 못했습니다: {rollback.FailureReason}",
                null, manifest.SnapshotId);
        }

        RestoreTransactionJournalStore.Write(
            snapshotDirectory,
            new RestoreTransactionJournal(manifest.SnapshotId, manifest.CodexHomePath, RestoreTransactionState.RolledBack, DateTimeOffset.UtcNow));

        return new RestoreResult(
            RestoreOutcome.RolledBack,
            "이전에 완료되지 못한 적용 작업을 이전 상태로 복원했습니다.",
            null, manifest.SnapshotId);
    }
}
