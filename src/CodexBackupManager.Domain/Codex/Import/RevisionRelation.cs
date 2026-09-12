namespace CodexBackupManager.Domain.Codex.Import;

/// <summary>
/// 같은 ThreadId를 두 PC(로컬 Codex ↔ backup)가 각각 갖고 있을 때 그 관계.
/// </summary>
/// <remarks>
/// <para>
/// <b>timestamp로 판정하지 않는다.</b> <c>updated_at</c>/파일 수정 시각은 참고 정보일 뿐이고, 이
/// 판정은 항상 실제 rollout lineage + 논리적 바이트 내용 비교(<see cref="RolloutSlice"/>)로 결정된다
/// (docs/codexbackup-format-v1.md — Phase 6 §3 참고).
/// </para>
/// <para>이 enum 자체는 Phase 7이 무엇을 할지 결정하지 않는다 — Phase 6은 판정만 한다.</para>
/// </remarks>
public enum RevisionRelation
{
    /// <summary>backup의 ThreadId가 현재 로컬 Codex에 없다. Phase 7 기본 계획: 새로 Import.</summary>
    New = 0,

    /// <summary>로컬과 backup이 완전히 같은 conversation revision이다. Phase 7 기본 계획: NoOp.</summary>
    Identical = 1,

    /// <summary>
    /// 로컬이 backup의 논리적 prefix다 — backup이 같은 lineage 위에서 더 진행됐다.
    /// Phase 7 기본 계획: Fast-forward Update.
    /// </summary>
    IncomingAhead = 2,

    /// <summary>
    /// backup이 로컬의 논리적 prefix다 — 로컬이 같은 lineage 위에서 더 진행됐다.
    /// Phase 7 기본 계획: Skip(로컬을 뒤로 되돌리지 않는다).
    /// </summary>
    LocalAhead = 3,

    /// <summary>
    /// 공통 지점 이후 로컬과 backup 양쪽에서 각각 다른 작업이 진행됐다(또는 같은 ThreadId지만
    /// 서로 무관한 lineage). 자동 merge 금지 — Phase 7에서 사용자 결정을 요구한다.
    /// </summary>
    Diverged = 4,

    /// <summary>
    /// lineage 정보 누락/손상/순환 참조 등으로 안전한 관계 판정 자체가 불가능하다. 적용 금지.
    /// </summary>
    Unverifiable = 5,
}
