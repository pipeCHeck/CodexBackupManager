using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Codex.Threads;

/// <summary>
/// 어떤 thread의 대화를 완전히 재구성하려면 <b>어느 rollout 파일들이 필요한지</b>를 계산한다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConversationTranscriptBuilder"/>(Viewer)와 Export(Phase 5,
/// <c>CodexBackupManager.Backup</c>)는 "조상 체인을 얼마나 포함해야 하는가"에 대해 정확히 같은
/// 판단을 해야 한다 — 하나는 메시지를 만들고 하나는 파일을 복사할 뿐, lineage 규칙 자체는 같아야
/// 한다. 이 클래스가 그 공통 규칙(<see cref="SelectFiles"/>)을 <see cref="ThreadChainResolver"/>에서
/// 뽑아내 두 쪽 다 같은 코드를 쓰게 한다. 여기서 새로운 lineage 규칙을 만들지 않는다 — 전부
/// <see cref="ThreadChainResolver.ResolveAncestry"/>와 <see cref="ThreadChain.ParentThreadId"/>에
/// 이미 있는 판단을 그대로 재사용한다.
/// </para>
/// </remarks>
public static class ThreadDependencyResolver
{
    /// <summary>
    /// 조상 체인의 한 연결 고리. <see cref="Files"/>는 이 thread의 체인 전체
    /// (<see cref="ThreadChain.Files"/>)가 아니라 <b>실제로 필요한 파일만</b>이다 — 다음(자식) 링크가
    /// 가리키는 rollout ID까지만 자르고, 그 이후 세그먼트(있다면)는 그 자식 입장에서 무관한 조상의
    /// 별도 연속이라 제외한다. 마지막 링크(leaf, 즉 원래 조회한 thread 자신)는 자르지 않는다.
    /// </summary>
    /// <param name="ThreadId">이 링크의 thread ID.</param>
    /// <param name="Chain">이 thread의 전체 체인(내부 세그먼트 경계 판단에 필요).</param>
    /// <param name="Files">실제로 필요한 파일 부분집합. 시간순, 최소 1개.</param>
    public sealed record ChainLink(string ThreadId, ThreadChain Chain, IReadOnlyList<RolloutFileReference> Files);

    /// <summary>
    /// <paramref name="threadId"/>의 대화를 완전히 재구성하는 데 필요한 조상 체인을 뿌리→leaf
    /// 순서로 계산한다. 순환/누락된 부모는 <see cref="ThreadChainResolver.ResolveAncestry"/>가 이미
    /// 안전하게 처리한다(무한 루프 없음) — 여기서는 그 결과에 경고만 덧붙인다.
    /// </summary>
    /// <param name="threadId">대상 thread ID(체인의 leaf).</param>
    /// <param name="chains"><see cref="Domain.Codex.Catalog.CodexCatalog.Chains"/>.</param>
    /// <param name="warnings">체인 정보가 없는 조상을 만나면 여기에 경고를 추가한다.</param>
    /// <param name="hasCycle">조상 체인에서 순환 참조를 발견했는지.</param>
    public static IReadOnlyList<ChainLink> ResolveChainLinks(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> chains,
        List<string> warnings,
        out bool hasCycle)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(warnings);

        IReadOnlyList<string> ancestry = ThreadChainResolver.ResolveAncestry(threadId, chains, out hasCycle);

        var result = new List<ChainLink>(ancestry.Count);
        for (int i = 0; i < ancestry.Count; i++)
        {
            string currentId = ancestry[i];
            if (!chains.TryGetValue(currentId, out ThreadChain? chain))
            {
                warnings.Add("체인 정보가 없는 조상 thread를 건너뛰었습니다.");
                continue;
            }

            string? targetRolloutId = i + 1 < ancestry.Count && chains.TryGetValue(ancestry[i + 1], out ThreadChain? childChain)
                ? childChain.ParentThreadId
                : null;

            IReadOnlyList<RolloutFileReference> files = SelectFiles(chain, targetRolloutId);
            result.Add(new ChainLink(currentId, chain, files));
        }

        return result;
    }

    /// <summary>
    /// 한 thread의 체인에서, 다음(자식) 링크가 가리키는 rollout ID까지만 자른 파일 목록을 돌려준다.
    /// </summary>
    /// <param name="chain">이 thread의 전체 체인.</param>
    /// <param name="externalTargetRolloutId">
    /// 다음(자식) thread가 분기 지점으로 가리킨 rollout ID. <c>null</c>이면(leaf 자신이거나 자식이
    /// 없으면) 자르지 않고 <see cref="ThreadChain.Files"/> 전체를 돌려준다.
    /// </param>
    /// <remarks>
    /// 대상 rollout ID를 이 체인의 파일 중에서 찾지 못하면(이론상 <see cref="ThreadChainResolver"/>가
    /// 이미 검증했으므로 발생하지 않지만) 잘라내지 않고 전체를 포함하는 안전한 기본값을 쓴다 —
    /// "확신 없으면 더 많이 포함한다"는 이 프로젝트의 기존 정책과 같다.
    /// </remarks>
    public static IReadOnlyList<RolloutFileReference> SelectFiles(ThreadChain chain, string? externalTargetRolloutId)
    {
        ArgumentNullException.ThrowIfNull(chain);

        if (externalTargetRolloutId is null)
        {
            return chain.Files;
        }

        for (int i = 0; i < chain.Files.Count; i++)
        {
            if (string.Equals(chain.Files[i].OwnRolloutId, externalTargetRolloutId, StringComparison.OrdinalIgnoreCase))
            {
                return chain.Files.Take(i + 1).ToList();
            }
        }

        return chain.Files;
    }

    /// <summary>
    /// <see cref="ChainLink"/> 하나에 실제로 적용할 파일별 컷오프(ordinal/byte)를 계산한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Conversation.ConversationTranscriptBuilder"/>(메시지 읽기)와 Phase 6의
    /// <c>ConversationRevisionBuilder</c>(fingerprint 해싱)가 이 메서드를 공유한다 — "어느 파일까지,
    /// 어디까지 포함하는가"를 두 곳이 각자 다시 구현하면 언젠가 미묘하게 어긋난다(drift). 여기서
    /// 새로운 경계 규칙을 만들지 않는다 — 전부 기존 <see cref="ThreadChain.FileHistoryBases"/>와
    /// 외부에서 넘어온 컷오프(다음 링크의 <c>ParentEndOrdinalExclusive</c>/<c>ParentEndByteOffset</c>)를
    /// 그대로 적용할 뿐이다.
    /// </para>
    /// <para>
    /// <paramref name="link"/>.Files는 이미 <see cref="SelectFiles"/>가 골라 둔 파일 목록이다(외부
    /// 분기 대상까지만 자른 부분집합, 또는 leaf 자신이면 전체). 이 메서드는 그 목록의 각 파일에
    /// 컷오프를 매기고, 외부 분기 대상 파일을 만나면 그 파일에서 멈춘다(그 뒤는 이 링크가 상속하는
    /// 대상 관점에서 무관한 별도 연속이므로 애초에 <see cref="SelectFiles"/>가 잘라냈어야 한다 — 그래도
    /// 안전하게 다시 한번 멈춘다).
    /// </para>
    /// </remarks>
    /// <param name="link">대상 링크(이미 <see cref="SelectFiles"/>로 파일이 골라져 있다).</param>
    /// <param name="externalTargetRolloutId">
    /// 다음(자식) thread가 분기 지점으로 가리킨 rollout ID. <c>null</c>이면 외부 컷오프를 적용하지 않는다.
    /// </param>
    /// <param name="externalCutoffOrdinalExclusive">외부 분기 대상 파일에 적용할 ordinal 컷오프.</param>
    /// <param name="externalCutoffByteOffsetExclusive">외부 분기 대상 파일에 적용할 byte offset 컷오프.</param>
    public static IReadOnlyList<FileSlice> ResolveFileSlices(
        ChainLink link,
        string? externalTargetRolloutId,
        long? externalCutoffOrdinalExclusive,
        long? externalCutoffByteOffsetExclusive)
    {
        ArgumentNullException.ThrowIfNull(link);

        var result = new List<FileSlice>(link.Files.Count);
        for (int i = 0; i < link.Files.Count; i++)
        {
            RolloutFileReference file = link.Files[i];

            bool isExternalTarget = externalTargetRolloutId is not null &&
                string.Equals(externalTargetRolloutId, file.OwnRolloutId, StringComparison.OrdinalIgnoreCase);

            if (isExternalTarget)
            {
                result.Add(new FileSlice(file, externalCutoffOrdinalExclusive, externalCutoffByteOffsetExclusive));
                return result; // 이 지점 이후로 이어지는 세그먼트는 무관한 별도 연속이다.
            }

            HistoryBaseReference? nextOwnHistoryBase = i + 1 < link.Chain.Files.Count ? link.Chain.FileHistoryBases[i + 1] : null;
            result.Add(new FileSlice(file, nextOwnHistoryBase?.EndOrdinalExclusive, nextOwnHistoryBase?.EndByteOffset));
        }

        return result;
    }

    /// <summary>
    /// <see cref="ResolveFileSlices"/>가 계산한, 파일 하나 + 그 파일에 적용할 컷오프.
    /// 둘 다 <c>null</c>이면 파일 전체가 포함된다.
    /// </summary>
    public sealed record FileSlice(RolloutFileReference File, long? CutoffOrdinalExclusive, long? CutoffByteOffsetExclusive);
}
