namespace CodexBackupManager.Restore;

/// <summary>테스트가 강제로 실패를 주입할 수 있는 지점(Phase 7 요구사항 19). 운영 코드는 항상 no-op이다.</summary>
public enum RestoreFaultInjectionPoint
{
    /// <summary>Snapshot 생성 직후.</summary>
    AfterSnapshot,

    /// <summary>첫 rollout 파일 생성 직후.</summary>
    AfterFirstRolloutCreate,

    /// <summary>rollout append 연산 직후.</summary>
    AfterRolloutAppend,

    /// <summary>
    /// 새 rollout 파일용 temp를 쓰는 도중(Phase 07_03 요구사항 1). 이 지점에서 실패해도(또는 진짜
    /// 프로세스가 죽어도) target 파일은 아직 전혀 만들어지지 않은 상태여야 한다 — temp만 남을 수
    /// 있고, 그 temp는 다음 시도가 안전하게 덮어쓸 수 있어야 한다.
    /// </summary>
    DuringNewRolloutTempWrite,

    /// <summary>새 rollout temp 검증까지 끝나고 target으로 atomic move하기 직전(Phase 07_03 요구사항 1).</summary>
    BeforeNewRolloutMove,

    /// <summary>새 rollout temp를 target으로 atomic move한 직후(Phase 07_03 요구사항 1).</summary>
    AfterNewRolloutMove,

    /// <summary>
    /// append용 temp 파일(원본 복사본 + incoming delta)을 쓰는 도중(Phase 07_02 요구사항 6). 이
    /// 지점에서 실패해도 원본 rollout 파일은 전혀 건드리지 않은 상태여야 한다.
    /// </summary>
    DuringAppendTempWrite,

    /// <summary>temp 검증까지 끝나고 atomic move로 원본을 교체하기 직전(Phase 07_02 요구사항 6).</summary>
    BeforeAtomicReplace,

    /// <summary>atomic move로 원본을 교체한 직후(Phase 07_02 요구사항 6).</summary>
    AfterAtomicReplace,

    /// <summary>SQLite 트랜잭션을 열기 직전.</summary>
    BeforeSqliteTransaction,

    /// <summary>SQLite 커밋 직후.</summary>
    AfterSqliteCommit,

    /// <summary>post-apply validation 직전.</summary>
    BeforePostValidation,
}

/// <summary>fault injection 훅. 테스트 전용 — 운영 코드는 <see cref="NoOpRestoreFaultInjectionHook"/>을 쓴다.</summary>
public interface IRestoreFaultInjectionHook
{
    /// <summary>이 지점에서 강제로 예외를 던지고 싶으면 던진다. 아니면 그냥 돌아온다.</summary>
    void Check(RestoreFaultInjectionPoint point);
}

/// <summary>아무것도 하지 않는 기본 구현(운영 코드가 쓴다).</summary>
public sealed class NoOpRestoreFaultInjectionHook : IRestoreFaultInjectionHook
{
    /// <summary>공유 인스턴스.</summary>
    public static readonly NoOpRestoreFaultInjectionHook Instance = new();

    /// <inheritdoc />
    public void Check(RestoreFaultInjectionPoint point)
    {
    }
}
