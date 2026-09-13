using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>완료되지 못하고 중단된 이전 Apply 시도 하나(Phase 07_02 요구사항 7 / 07_03 요구사항 3).</summary>
/// <param name="SnapshotDirectory">그 Apply가 만든 Snapshot 디렉터리 전체 경로.</param>
/// <param name="Journal">
/// 그 안의 transaction journal. <paramref name="Reason"/>이
/// <see cref="IncompleteApplyReason.JournalUnreadable"/>이면 읽을 수 없었으므로 <c>null</c>이다.
/// </param>
/// <param name="Reason">이 항목이 왜 "완료되지 못한 상태"로 분류됐는지.</param>
public sealed record IncompleteApply(string SnapshotDirectory, RestoreTransactionJournal? Journal, IncompleteApplyReason Reason)
{
    /// <summary>진단/메시지 표시용 Snapshot ID. journal이 있으면 거기서, 없으면 디렉터리 이름에서 얻는다.</summary>
    public string SnapshotId => Journal?.SnapshotId ?? Path.GetFileName(SnapshotDirectory);
}

/// <summary>
/// <see cref="IncompleteApply"/>가 왜 복구가 필요한 상태로 분류됐는지(Phase 07_03 요구사항 3).
/// </summary>
public enum IncompleteApplyReason
{
    /// <summary>journal이 <see cref="RestoreTransactionState.Applying"/>으로 멈춰 있다 — 정상 판별.</summary>
    StuckApplying,

    /// <summary>
    /// journal 파일 자체는 있지만 읽거나 파싱할 수 없다. Applying이었는지 이미 끝난 뒤였는지 알 수
    /// 없으므로, "없는 것"처럼 조용히 넘어가지 않고 보수적으로 위험 상태(RecoveryStateUnknown
    /// 성격)로 취급한다 — 새 Apply를 막고 사용자에게 수동 확인을 안내한다.
    /// </summary>
    JournalUnreadable,
}

/// <summary>
/// 앱 시작 시(또는 새 Apply를 시작하기 전) <see cref="RestoreTransactionState.Applying"/> 상태로
/// 남아 있는 이전 Apply가 있는지 찾고, 사용자가 명시적으로 승인하면 그 Snapshot으로 Rollback한다
/// (Phase 07_02 요구사항 7 — "사용자 모르게 자동으로 덮어쓰지 않는다").
/// </summary>
/// <remarks>
/// Phase 07_03 요구사항 2 — 이 프로그램은 수동으로 Codex Home을 선택할 수 있으므로, "완료되지 못한
/// 이전 Apply"는 <b>그 Apply가 실제로 겨냥했던 Codex Home에 대해서만</b> 새 Apply를 막아야 한다.
/// <see cref="FindIncomplete"/>는 (진단/테스트용으로) 모든 Home을 훑고, <see cref="FindIncompleteForHome"/>이
/// 실제 production 진입점(<see cref="RestoreExecutor"/>, <c>MainViewModel</c>)이 쓰는 API다.
/// </remarks>
public static class IncompleteApplyRecoveryService
{
    /// <summary>
    /// <paramref name="snapshotRoot"/> 아래의 모든 Snapshot을 훑어 복구가 필요한 상태(Applying으로
    /// 멈췄거나, journal이 손상돼 상태를 알 수 없는)를 전부 찾는다(최신순, journal이 없는 항목은
    /// 맨 뒤). manifest 자체가 없는 디렉터리는 조용히 건너뛴다 — 이 스캔 자체가 실패하면 안 된다.
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
            RestoreTransactionJournalReadResult read = RestoreTransactionJournalStore.TryReadDetailed(snapshotDir);
            switch (read.Status)
            {
                case RestoreTransactionJournalReadStatus.Valid when read.Journal!.State == RestoreTransactionState.Applying:
                    found.Add(new IncompleteApply(snapshotDir, read.Journal, IncompleteApplyReason.StuckApplying));
                    break;
                case RestoreTransactionJournalReadStatus.Corrupt:
                    found.Add(new IncompleteApply(snapshotDir, null, IncompleteApplyReason.JournalUnreadable));
                    break;
                default:
                    // Missing(journal 개념이 없던 오래된 정상 Snapshot) 또는 Valid-but-not-Applying
                    // (Prepared/Completed/RolledBack)은 복구가 필요한 상태가 아니다.
                    break;
            }
        }

        return [.. found.OrderByDescending(f => f.Journal?.UpdatedAtUtc ?? DateTimeOffset.MinValue)];
    }

    /// <summary>
    /// <see cref="FindIncomplete"/>와 같지만 <paramref name="codexHomePath"/>와 같은 Codex Home을
    /// 겨냥했던 항목만 돌려준다(요구사항 2). journal도 manifest도 Home을 알 수 없는(둘 다 없거나
    /// 손상된) 극히 드문 경우는, 어느 Home에 영향을 주는지 알 수 없으므로 안전 쪽으로 기울여 모든
    /// Home에 대해 노출한다 — 조용히 무시하지 않는다.
    /// </summary>
    public static IReadOnlyList<IncompleteApply> FindIncompleteForHome(string snapshotRoot, string codexHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        var result = new List<IncompleteApply>();
        foreach (IncompleteApply item in FindIncomplete(snapshotRoot))
        {
            string? home = item.Journal?.CodexHomePath ?? SnapshotService.ReadManifest(item.SnapshotDirectory)?.CodexHomePath;
            if (home is null || CanonicalPath.AreSameLocation(home, codexHomePath))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// 사용자가 "[이전 상태로 복구]"를 명시적으로 눌렀을 때만 부른다. Codex가 실행 중이면 즉시
    /// 거부한다(강제 종료하지 않는다). 복구 전에 journal/manifest의 정합성을 스스로 다시 검증한다
    /// (요구사항 3 — 호출자가 올바른 snapshot만 넘긴다는 것을 신뢰하지 않는다) — Snapshot manifest를
    /// 다시 읽어 <see cref="RollbackService"/>로 byte-for-byte 복구하고, 성공하면 journal을
    /// <see cref="RestoreTransactionState.RolledBack"/>으로 갱신한다.
    /// </summary>
    /// <param name="snapshotDirectory">복구할 Snapshot 디렉터리.</param>
    /// <param name="processLister">테스트 주입용. 기본값은 실제 OS 프로세스 목록.</param>
    /// <param name="expectedCodexHomePath">
    /// 호출자가 이미 "이 Home에 대한 것"이라고 알고 있다면 그 경로를 넘긴다 — manifest의
    /// CodexHomePath와 다르면 거부한다(요구사항 2/3 — 호출자가 실수로 다른 Home의 snapshot을
    /// 넘겨도 여기서 막는다). 모르면 <c>null</c>.
    /// </param>
    public static RestoreResult Recover(
        string snapshotDirectory,
        CodexProcessGuard.RunningProcessLister? processLister = null,
        string? expectedCodexHomePath = null)
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

        RestoreTransactionJournalReadResult journalRead = RestoreTransactionJournalStore.TryReadDetailed(snapshotDirectory);
        if (journalRead.Status == RestoreTransactionJournalReadStatus.Corrupt)
        {
            return new RestoreResult(
                RestoreOutcome.RollbackFailedCritical,
                "CRITICAL: 복원 진행 기록이 손상되어 있어 자동으로 복구할 수 없습니다. Snapshot 폴더를 직접 확인해야 합니다.",
                null, null);
        }

        if (journalRead.Status == RestoreTransactionJournalReadStatus.Missing ||
            journalRead.Journal!.State != RestoreTransactionState.Applying)
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                "이 Snapshot은 복구가 필요한 상태가 아닙니다(이미 완료됐거나 처리된 것으로 보입니다).",
                null, journalRead.Journal?.SnapshotId);
        }

        SnapshotManifest? manifest = SnapshotService.ReadManifest(snapshotDirectory);
        if (manifest is null)
        {
            return new RestoreResult(
                RestoreOutcome.RollbackFailedCritical,
                "CRITICAL: Snapshot manifest를 찾을 수 없어 자동 복구할 수 없습니다. 수동으로 확인해야 합니다.",
                null, null);
        }

        // 요구사항 3 — journal과 manifest가 같은 Snapshot/Home을 가리키는지 다시 확인한다. 둘이
        // 어긋나 있으면(예: 디렉터리가 잘못 합쳐졌거나 손으로 편집된 경우) 절대 Rollback을
        // 진행하지 않는다.
        if (!string.Equals(journalRead.Journal.SnapshotId, manifest.SnapshotId, StringComparison.Ordinal) ||
            !CanonicalPath.AreSameLocation(journalRead.Journal.CodexHomePath, manifest.CodexHomePath))
        {
            return new RestoreResult(
                RestoreOutcome.RollbackFailedCritical,
                "CRITICAL: 복원 진행 기록과 Snapshot 정보가 서로 일치하지 않습니다. 수동으로 확인해야 합니다.",
                null, manifest.SnapshotId);
        }

        if (expectedCodexHomePath is not null && !CanonicalPath.AreSameLocation(manifest.CodexHomePath, expectedCodexHomePath))
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                "이 Snapshot은 다른 Codex Home에 대한 것입니다.",
                null, manifest.SnapshotId);
        }

        // 요구사항 4 — 다른 프로세스가 이미 이 Codex Home을 처리 중이면(Apply든 Recovery든) 여기서도
        // 동시에 진행하지 않는다.
        RestoreProcessLock.AcquireResult lockResult = RestoreProcessLock.TryAcquire(manifest.CodexHomePath, TimeSpan.Zero);
        if (!lockResult.Acquired)
        {
            return new RestoreResult(
                RestoreOutcome.NotReady,
                "다른 Codex Backup Manager 인스턴스가 이 Codex Home을 처리 중입니다.",
                null, manifest.SnapshotId);
        }

        try
        {
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
        finally
        {
            lockResult.Handle!.Dispose();
        }
    }
}
