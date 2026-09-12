using System;
using System.Collections.Generic;
using System.Threading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Codex.Revisions;

/// <summary>
/// thread ID + <see cref="ThreadChain"/> 맵 + <see cref="IRolloutSliceReader"/>로
/// <see cref="ConversationRevision"/>을 만든다. 로컬 카탈로그와 backup 양쪽에서 같은 코드를 쓴다 —
/// 어느 쪽인지는 <see cref="IRolloutSliceReader"/> 구현이 결정한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>lineage 규칙을 새로 만들지 않는다.</b> 어느 파일이 필요한지는
/// <see cref="ThreadDependencyResolver.ResolveChainLinks"/>가, 파일별 컷오프는
/// <see cref="ThreadDependencyResolver.ResolveFileSlices"/>가 그대로 결정한다(Export/Viewer와 동일한
/// 코드). 이 클래스는 그 결과를 <see cref="RolloutSliceHasher"/>로 해시해 fingerprint로 바꿀 뿐이다.
/// </para>
/// <para>
/// <b>실패는 항상 Unverifiable로 수렴한다.</b> 체인이 없거나, 조상 체인에 순환 참조가 있거나,
/// 조상 rollout 파일 일부를 찾을 수 없으면(=Export의 FatalErrors와 같은 조건) revision 자체를
/// 신뢰할 수 없다고 보고 <see cref="ConversationRevisionBuildResult.Unverifiable"/>을 돌려준다 —
/// "확신 없으면 안전한 쪽(적용 금지)"이라는 이 프로젝트의 기존 원칙과 같다.
/// </para>
/// </remarks>
public static class ConversationRevisionBuilder
{
    /// <summary>대상 thread의 revision을 만든다.</summary>
    /// <param name="threadId">대상 thread ID(체인의 leaf).</param>
    /// <param name="chains">thread ID → <see cref="ThreadChain"/> 맵(로컬 카탈로그 또는 backup 재구성 결과).</param>
    /// <param name="sliceReader">실제 바이트를 읽어 해시할 방법(로컬 파일 또는 backup ZIP entry).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    public static ConversationRevisionBuildResult Build(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> chains,
        IRolloutSliceReader sliceReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(sliceReader);

        if (!chains.ContainsKey(threadId))
        {
            return ConversationRevisionBuildResult.Unverifiable("이 대화의 rollout 파일 체인을 찾을 수 없습니다.");
        }

        var chainWarnings = new List<string>();
        IReadOnlyList<ThreadDependencyResolver.ChainLink> links =
            ThreadDependencyResolver.ResolveChainLinks(threadId, chains, chainWarnings, out bool hasCycle);

        if (hasCycle)
        {
            return ConversationRevisionBuildResult.Unverifiable("조상 체인에서 순환 참조가 발견되었습니다.");
        }

        if (chainWarnings.Count > 0)
        {
            return ConversationRevisionBuildResult.Unverifiable("조상 rollout 파일 일부를 찾을 수 없어 완전한 lineage를 구성할 수 없습니다.");
        }

        var slices = new List<RolloutSlice>();
        for (int i = 0; i < links.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThreadDependencyResolver.ChainLink link = links[i];

            string? targetRolloutId = null;
            long? cutoffOrdinal = null;
            long? cutoffByteOffset = null;
            if (i + 1 < links.Count)
            {
                ThreadChain childChain = links[i + 1].Chain;
                targetRolloutId = childChain.ParentThreadId;
                cutoffOrdinal = childChain.ParentEndOrdinalExclusive;
                cutoffByteOffset = childChain.ParentEndByteOffset;
            }

            IReadOnlyList<ThreadDependencyResolver.FileSlice> fileSlices =
                ThreadDependencyResolver.ResolveFileSlices(link, targetRolloutId, cutoffOrdinal, cutoffByteOffset);

            foreach (ThreadDependencyResolver.FileSlice fileSlice in fileSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RolloutSliceHasher.Result hashed = sliceReader.Hash(
                    fileSlice.File, fileSlice.CutoffOrdinalExclusive, fileSlice.CutoffByteOffsetExclusive, cancellationToken);

                RolloutSliceBoundary boundary = fileSlice.CutoffOrdinalExclusive is not null
                    ? RolloutSliceBoundary.OrdinalCutoff
                    : fileSlice.CutoffByteOffsetExclusive is not null
                        ? RolloutSliceBoundary.ByteOffsetCutoff
                        : RolloutSliceBoundary.Full;

                slices.Add(new RolloutSlice(
                    fileSlice.File.OwnRolloutId, fileSlice.File, boundary, hashed.LogicalByteLength, hashed.Sha256Hex));
            }
        }

        return ConversationRevisionBuildResult.Ok(new ConversationRevision(threadId, slices));
    }
}
