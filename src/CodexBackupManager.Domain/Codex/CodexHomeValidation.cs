using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex;

/// <summary>Codex Home 판정 결과.</summary>
public enum CodexHomeStatus
{
    /// <summary>Codex Home이 아니다. 사용하지 않는다.</summary>
    Invalid = 0,

    /// <summary>Codex Home일 가능성이 있다. 사용은 허용하되 UI에 경고를 표시한다.</summary>
    Probable = 1,

    /// <summary>Codex Home으로 확정.</summary>
    Valid = 2,
}

/// <summary>
/// 개별 검사 신호 하나.
/// </summary>
/// <param name="Name">검사 이름. 예: <c>sessions\</c>, <c>state_*.sqlite</c></param>
/// <param name="Present">발견되었는지.</param>
/// <param name="Weight">점수 가중치.</param>
/// <param name="Detail">사용자 표시용 보조 설명. 경로 원문을 담지 않는다.</param>
public sealed record CodexHomeSignal(string Name, bool Present, int Weight, string Detail);

/// <summary>
/// Codex Home 검증 결과.
/// </summary>
/// <param name="Status">판정.</param>
/// <param name="Score">신호 가중치 합계 (0..<see cref="MaxScore"/>).</param>
/// <param name="Signals">개별 신호 목록.</param>
/// <param name="Reasons">판정 사유(사용자 표시용).</param>
/// <param name="StateDatabaseFileNames">발견된 <c>state_*.sqlite</c> 파일명(경로 아님).</param>
public sealed record CodexHomeValidation(
    CodexHomeStatus Status,
    int Score,
    IReadOnlyList<CodexHomeSignal> Signals,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> StateDatabaseFileNames)
{
    /// <summary>가능한 최대 점수.</summary>
    public const int MaxScore = 5;

    /// <summary><see cref="CodexHomeStatus.Valid"/> 판정 최소 점수.</summary>
    public const int ValidThreshold = 3;

    /// <summary>사용 가능한(Valid 또는 Probable) 판정인지.</summary>
    public bool IsUsable => Status is CodexHomeStatus.Valid or CodexHomeStatus.Probable;
}
