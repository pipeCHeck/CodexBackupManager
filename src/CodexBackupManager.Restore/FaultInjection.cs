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
