using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Codex.Conversation;

/// <summary>
/// thread ID + <see cref="ThreadChain"/> 맵으로 화면에 보여줄 <see cref="ConversationTranscript"/>를 만든다.
/// </summary>
/// <remarks>
/// <para>
/// ViewModel은 rollout 파일을 직접 열지 않는다. 이 클래스가
/// <see cref="ThreadChainResolver.ResolveAncestry"/>(체인 조상 계산) →
/// <see cref="ConversationItemParser"/>(파일별 메시지 추출)를 엮는다.
/// </para>
/// <para>
/// <b>부모 전체를 이어붙이지 않는다.</b> 조상 체인을 뿌리부터 순서대로 훑으면서, 각 조상은
/// "그 조상의 자식"이 가진 <c>ParentEndOrdinalExclusive</c>(우선) 또는 <c>ParentEndByteOffset</c>
/// (ordinal을 쓸 수 없을 때만) 만큼만 잘라서 포함한다 — 분기 이후 조상 자신의 별도 활동은 제외된다.
/// 다단계 분기(조상의 조상)도 이 방식으로 자연스럽게 처리된다.
/// </para>
/// <para>
/// <b>공식 Codex 소스(<c>codex-rs/protocol/src/protocol.rs</c> <c>HistoryPosition</c>) 확인 결과:</b>
/// <c>end_ordinal_exclusive</c> = "포함되지 않는 첫 rollout ordinal", <c>end_byte_offset</c> =
/// "마지막으로 포함된 JSONL record 바로 다음의 byte 위치" — 우리 구현의 경계 처리(포함: ordinal &lt;
/// cutoff, 제외: ordinal ≥ cutoff / byte 위치 &gt; cutoff)가 이 정의와 정확히 일치한다.
/// </para>
/// <para>
/// <b>실측으로 확인된 사실(Phase 3 pre-commit audit):</b> 같은 thread의 세그먼트끼리도 이전 세그먼트를
/// "무조건 전부" 잇지 않는다 — 세그먼트마다 자기 자신의 <c>history_base</c>가 있을 수 있고, 그
/// <c>thread_id</c>는 이 thread의 안정 ID가 아니라 <b>바로 앞 세그먼트 파일 자신의 rollout ID</b>
/// (<see cref="RolloutFileReference.OwnRolloutId"/>)를 가리킨다(공식 소스 주석과 일치). 다른 thread의
/// 분기(cross-thread fork) 역시 같은 메커니즘이라, 조상이 여러 세그먼트로 나뉘어 있으면 그 분기가
/// 조상의 "마지막" 세그먼트가 아니라 <b>중간의 특정 세그먼트</b>를 가리킬 수도 있다. 그래서 경계는
/// 항상 "가리키는 rollout ID가 어느 파일 자신의 것인지"로 찾고, 그 파일에서 멈춘다 — 그 뒤에 이어지는
/// 세그먼트(있다면)는 이 thread 관점에서는 무관한 별도의 연속이므로 포함하지 않는다.
/// </para>
/// <para>순환 참조나 누락된 부모는 <see cref="ThreadChainResolver.ResolveAncestry"/>가 이미 무한
/// 루프 없이 처리하므로, 여기서는 그 결과에 경고를 덧붙이기만 한다.</para>
/// </remarks>
public static class ConversationTranscriptBuilder
{
    /// <summary>대상 thread의 transcript를 만든다.</summary>
    /// <param name="threadId">보여줄 thread ID(체인의 leaf).</param>
    /// <param name="chains"><see cref="CodexBackupManager.Domain.Codex.Catalog.CodexCatalog.Chains"/>.</param>
    /// <param name="cancellationToken">취소 토큰. 파일 읽기 중간에 확인한다.</param>
    public static ConversationTranscript Build(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> chains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(chains);

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (!chains.ContainsKey(threadId))
        {
            warnings.Add("이 대화의 rollout 파일 체인을 찾을 수 없습니다.");
            stopwatch.Stop();
            return new ConversationTranscript(threadId, [], warnings, stopwatch.Elapsed);
        }

        IReadOnlyList<string> ancestry = ThreadChainResolver.ResolveAncestry(threadId, chains, out bool hasCycle);
        if (hasCycle)
        {
            warnings.Add("분기 조상 체인에서 순환 참조가 발견되어 일부만 포함되었습니다.");
        }

        var messages = new List<ConversationMessage>();
        for (int i = 0; i < ancestry.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentId = ancestry[i];

            if (!chains.TryGetValue(currentId, out ThreadChain? chain))
            {
                // ResolveAncestry는 존재를 확인하지 못한 마지막 조상 ID도 목록에 남기므로 여기서 걸러진다.
                warnings.Add("체인 정보가 없는 조상 thread를 건너뛰었습니다.");
                continue;
            }

            // 다음(자식) thread가 "이 조상의 어느 rollout ID"를 분기 지점으로 가리켰는지 그대로 넘긴다.
            // 반드시 마지막 세그먼트일 필요는 없다 — ReadChainMessages가 rollout ID로 정확한 파일을 찾는다.
            string? targetRolloutId = null;
            long? cutoffOrdinal = null;
            long? cutoffByteOffset = null;
            if (i + 1 < ancestry.Count && chains.TryGetValue(ancestry[i + 1], out ThreadChain? childChain))
            {
                targetRolloutId = childChain.ParentThreadId;
                cutoffOrdinal = childChain.ParentEndOrdinalExclusive;
                cutoffByteOffset = childChain.ParentEndByteOffset;
            }

            List<ConversationMessage> chainMessages = ReadChainMessages(
                chain, targetRolloutId, cutoffOrdinal, cutoffByteOffset, warnings, cancellationToken);
            messages.AddRange(chainMessages);
        }

        stopwatch.Stop();
        return new ConversationTranscript(threadId, messages, warnings, stopwatch.Elapsed);
    }

    /// <summary>
    /// 한 thread(조상 포함)의 체인을 읽는다. 파일마다 다음 둘 중 하나를 확인한다:
    /// <list type="number">
    ///   <item>
    ///     <paramref name="externalTargetRolloutId"/>가 이 파일 자신의 rollout ID와 일치하면 —
    ///     이 파일이 (다른 thread 또는 이 체인 자체의) 분기 지점이다. 외부에서 넘어온 컷오프를
    ///     적용하고, <b>그 뒤에 이어지는 세그먼트는 읽지 않고 멈춘다</b>(무관한 별도 연속이므로).
    ///   </item>
    ///   <item>
    ///     그렇지 않으면 — 바로 다음 세그먼트가 "자기 자신의" history_base로 이 파일을 가리키는지
    ///     본다(<see cref="ThreadChain.FileHistoryBases"/>). 가리키면 그 경계까지만, 아니면 전체를 읽는다.
    ///   </item>
    /// </list>
    /// </summary>
    private static List<ConversationMessage> ReadChainMessages(
        ThreadChain chain,
        string? externalTargetRolloutId,
        long? externalCutoffOrdinal,
        long? externalCutoffByteOffset,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var messages = new List<ConversationMessage>();

        for (int i = 0; i < chain.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RolloutFileReference file = chain.Files[i];

            bool isExternalTarget = externalTargetRolloutId is not null &&
                string.Equals(externalTargetRolloutId, file.OwnRolloutId, StringComparison.OrdinalIgnoreCase);

            if (isExternalTarget)
            {
                messages.AddRange(ReadOneFile(file, externalCutoffOrdinal, externalCutoffByteOffset, warnings, cancellationToken));
                // 이 thread를 상속한 대상(다른 thread의 분기, 또는 이 체인을 상속하는 다음 조상)
                // 입장에서는 이 지점 이후로 이어지는 세그먼트는 무관한 별도 연속이다.
                return messages;
            }

            HistoryBaseReference? nextOwnHistoryBase = i + 1 < chain.Files.Count ? chain.FileHistoryBases[i + 1] : null;
            long? internalOrdinalCutoff = nextOwnHistoryBase?.EndOrdinalExclusive;
            long? internalByteCutoff = nextOwnHistoryBase?.EndByteOffset;

            messages.AddRange(ReadOneFile(file, internalOrdinalCutoff, internalByteCutoff, warnings, cancellationToken));
        }

        return messages;
    }

    private static List<ConversationMessage> ReadOneFile(
        RolloutFileReference file,
        long? cutoffOrdinal,
        long? cutoffByteOffset,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        ConversationItemParser.ParseResult parsed = cutoffOrdinal is { } ordinal
            ? ConversationItemParser.ParseFileWithOrdinalCutoff(file, ordinal, cancellationToken)
            : cutoffByteOffset is { } byteOffset
                ? ConversationItemParser.ParseFileWithByteOffsetCutoff(file, byteOffset, cancellationToken)
                : ConversationItemParser.ParseFile(file, cancellationToken);

        if (parsed.Warning is not null)
        {
            warnings.Add(parsed.Warning);
        }

        return [.. parsed.Messages];
    }
}
