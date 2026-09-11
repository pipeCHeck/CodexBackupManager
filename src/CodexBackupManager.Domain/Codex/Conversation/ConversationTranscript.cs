using System;
using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Conversation;

/// <summary>
/// 대화 하나를 화면에 보여주기 위해 재구성한 전체 메시지 목록.
/// </summary>
/// <remarks>
/// 세그먼트로 나뉜 rollout과 <c>history_base</c>로 이어붙인 부모 구간까지 반영한 최종 결과다.
/// 부모 전체가 아니라 분기 시점(<c>end_ordinal_exclusive</c>) 이전까지만 포함된다.
/// </remarks>
/// <param name="ThreadId">이 transcript가 나타내는 thread ID(체인의 leaf).</param>
/// <param name="Messages">시간순으로 정렬된 메시지. 조상 구간 → 자신 구간 순서.</param>
/// <param name="Warnings">재구성 중 발견한 이상 징후(사용자 원문 없이).</param>
/// <param name="BuildDuration">읽기부터 재구성까지 걸린 시간.</param>
public sealed record ConversationTranscript(
    string ThreadId,
    IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<string> Warnings,
    TimeSpan BuildDuration);
