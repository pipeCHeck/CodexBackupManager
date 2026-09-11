using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;

namespace CodexBackupManager.Domain.Codex.Threads;

/// <summary>
/// 하나의 사용자 대화(thread)를 이루는 rollout 파일 체인.
/// </summary>
/// <remarks>
/// <para>
/// 한 thread는 페이지네이션으로 여러 파일에 나뉠 수 있다(<c>_&lt;segmentId&gt;</c>).
/// <see cref="Files"/>는 그 파일들을 시간순(오래된 것 → 최신 것)으로 담는다.
/// </para>
/// <para>
/// <see cref="ParentThreadId"/>는 <c>forked_from_id</c>/<c>history_base.thread_id</c>에서 읽은
/// 분기 부모다. Phase 2는 이 정보를 저장만 하고 실제로 부모 내용과 합치지 않는다 — Export에서 쓴다.
/// </para>
/// <para>
/// <b>실측으로 확인된 사실(Phase 3 pre-commit audit):</b> 같은 thread의 세그먼트끼리도 이전 세그먼트를
/// "무조건 전부" 잇지 않는다. 세그먼트 2개 이상마다 <b>자기 자신의 <c>history_base</c></b>가 있을 수 있고,
/// 그 <c>thread_id</c>는 이 thread의 안정적인 ID가 아니라 <b>바로 앞 세그먼트 파일 자신의 rollout ID</b>
/// (<see cref="RolloutFileReference.OwnRolloutId"/>)를 가리킨다 — 공식 소스 주석
/// ("HistoryPosition ... Treat its value as a rollout_id")과 정확히 일치한다.
/// <see cref="FileHistoryBases"/>가 세그먼트별로 이 정보를 보존한다.
/// </para>
/// </remarks>
/// <param name="ThreadId">이 체인이 나타내는 thread ID.</param>
/// <param name="Files">시간순으로 정렬된 rollout 파일 목록. 최소 1개.</param>
/// <param name="FileHistoryBases">
/// <see cref="Files"/>와 같은 순서/길이. 각 파일 자신의 <c>session_meta.history_base</c>(있으면).
/// <c>Files[i]</c>가 이 값을 가지면 <c>Files[i-1]</c>(또는 그 이전 체인)을 그 경계까지만 상속한다는 뜻이다.
/// 첫 파일(<c>Files[0]</c>)의 값은 <see cref="ParentThreadId"/>와 같은 정보이며(cross-thread 분기),
/// 그 뒤 파일들의 값은 같은 thread 안에서의 세그먼트 간 경계다.
/// </param>
/// <param name="ParentThreadId">분기 부모 thread ID. 없으면 <c>null</c>.</param>
/// <param name="ParentEndOrdinalExclusive">
/// 부모 쪽에서 이어받는 마지막 ordinal(미포함). Phase 3 Conversation Viewer가 부모 rollout을
/// 어디까지 상속하는지 판단하는 기본 기준이다(실측으로 신뢰성 확인됨, docs/codex-storage-format.md §3).
/// </param>
/// <param name="ParentEndByteOffset">
/// 부모 rollout 파일에서 이어받는 바이트 위치. <see cref="ParentEndOrdinalExclusive"/>를 쓸 수 없을 때만
/// (예: ordinal 필드가 없는 손상된 줄) 폴백으로 쓴다.
/// </param>
/// <param name="Warnings">체인을 구성하며 발견한 이상 징후(사용자 원문을 담지 않는다).</param>
public sealed record ThreadChain(
    string ThreadId,
    IReadOnlyList<RolloutFileReference> Files,
    IReadOnlyList<HistoryBaseReference?> FileHistoryBases,
    string? ParentThreadId,
    long? ParentEndOrdinalExclusive,
    long? ParentEndByteOffset,
    IReadOnlyList<string> Warnings)
{
    /// <summary>가장 최근(마지막) 세그먼트 파일. Codex의 <c>threads.rollout_path</c>가 가리키는 파일과 같다.</summary>
    public RolloutFileReference LatestFile => Files[^1];

    /// <summary>파일이 2개 이상으로 나뉜 thread인지.</summary>
    public bool IsSegmented => Files.Count > 1;
}
