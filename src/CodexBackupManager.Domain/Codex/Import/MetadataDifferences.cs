namespace CodexBackupManager.Domain.Codex.Import;

/// <summary>
/// <see cref="RevisionRelation"/>(conversation 내용 관계)과는 완전히 분리된, thread metadata의
/// PC 간 차이. 같은 conversation content라도 cwd/프로젝트 연결/고정 여부/섹션/제목/최근 사용 시각은
/// PC마다 다를 수 있다(요구사항 8) — Phase 6은 이 차이를 보여주기만 하고 병합 정책을 정하지 않는다.
/// </summary>
/// <param name="CwdDiffers"><c>threads.cwd</c>(원본 작업 디렉터리)가 다른지.</param>
/// <param name="ProjectAssignmentDiffers">해결된 프로젝트 연결(<c>ResolvedProjectId</c>)이 다른지.</param>
/// <param name="PinnedDiffers">사이드바 고정 여부가 다른지.</param>
/// <param name="SectionDiffers">사이드바 섹션 ID가 다른지.</param>
/// <param name="TitleDiffers"><c>title</c>/<c>name</c> 원본 값이 다른지.</param>
/// <param name="RecencyDiffers">최근 사용 시각(<c>recency_at_ms</c>)이 다른지.</param>
public sealed record MetadataDifferences(
    bool CwdDiffers,
    bool ProjectAssignmentDiffers,
    bool PinnedDiffers,
    bool SectionDiffers,
    bool TitleDiffers,
    bool RecencyDiffers)
{
    /// <summary>차이가 없는 기본값. backup에만 있는(New) 대화처럼 비교 대상 자체가 없을 때 쓴다.</summary>
    public static readonly MetadataDifferences None = new(false, false, false, false, false, false);

    /// <summary>하나라도 차이가 있는지.</summary>
    public bool HasAny =>
        CwdDiffers || ProjectAssignmentDiffers || PinnedDiffers || SectionDiffers || TitleDiffers || RecencyDiffers;
}
