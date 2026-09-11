using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Rollout;

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
/// </remarks>
/// <param name="ThreadId">이 체인이 나타내는 thread ID.</param>
/// <param name="Files">시간순으로 정렬된 rollout 파일 목록. 최소 1개.</param>
/// <param name="ParentThreadId">분기 부모 thread ID. 없으면 <c>null</c>.</param>
/// <param name="ParentEndOrdinalExclusive">부모 쪽에서 이어받는 마지막 ordinal(미포함).</param>
/// <param name="Warnings">체인을 구성하며 발견한 이상 징후(사용자 원문을 담지 않는다).</param>
public sealed record ThreadChain(
    string ThreadId,
    IReadOnlyList<RolloutFileReference> Files,
    string? ParentThreadId,
    long? ParentEndOrdinalExclusive,
    IReadOnlyList<string> Warnings)
{
    /// <summary>가장 최근(마지막) 세그먼트 파일. Codex의 <c>threads.rollout_path</c>가 가리키는 파일과 같다.</summary>
    public RolloutFileReference LatestFile => Files[^1];

    /// <summary>파일이 2개 이상으로 나뉜 thread인지.</summary>
    public bool IsSegmented => Files.Count > 1;
}
