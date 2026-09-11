using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex;

/// <summary>
/// Codex Home 후보 하나를 검사한 기록. 왜 채택/탈락했는지 UI와 로그에 설명하기 위해 남긴다.
/// </summary>
/// <param name="Source">후보 출처(= 우선순위).</param>
/// <param name="RawPath">후보 경로 원문. 비어 있으면 해당 출처에 값이 없었다는 뜻.</param>
/// <param name="Skipped">후보 자체가 없어 검사하지 않았는지.</param>
/// <param name="Validation">검사 결과. <paramref name="Skipped"/>가 참이면 <c>null</c>.</param>
/// <param name="Note">보조 설명.</param>
public sealed record CodexHomeProbe(
    CodexHomeSource Source,
    string? RawPath,
    bool Skipped,
    CodexHomeValidation? Validation,
    string Note);

/// <summary>
/// Codex Home 탐색 결과.
/// </summary>
/// <param name="Found">사용 가능한 Codex Home을 찾았는지.</param>
/// <param name="Source">채택된 후보의 출처.</param>
/// <param name="Home">채택된 Codex Home의 정규화 경로.</param>
/// <param name="HomeDisplayPath">채택된 Codex Home의 원본 표기.</param>
/// <param name="Validation">채택된 후보의 검사 결과.</param>
/// <param name="Probes">모든 후보의 검사 기록(우선순위 순).</param>
public sealed record CodexLocatorResult(
    bool Found,
    CodexHomeSource? Source,
    Paths.CanonicalPath? Home,
    string? HomeDisplayPath,
    CodexHomeValidation? Validation,
    IReadOnlyList<CodexHomeProbe> Probes);
