using System;
using System.Collections.Generic;
using System.Threading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Codex.Revisions;

/// <summary>
/// 두 <see cref="ConversationRevision"/>(같은 ThreadId, 로컬 ↔ backup)을 비교해
/// <see cref="RevisionRelation"/>을 판정한다. <c>New</c>/<c>Unverifiable</c>은 이 클래스의 영역이
/// 아니다 — 그 두 값은 revision 자체를 만들 수 있었는지에 달려 있으므로 호출자
/// (Import Preview 오케스트레이션)가 결정한다. 이 클래스는 "둘 다 정상적으로 만들어졌을 때"의
/// 4가지 관계(Identical/IncomingAhead/LocalAhead/Diverged)만 판정한다.
/// </summary>
/// <remarks>
/// <para><b>timestamp를 전혀 보지 않는다.</b> 오직 <see cref="RolloutSlice.RolloutId"/> 순서와
/// 논리적 바이트 내용(길이+SHA-256)만 비교한다.</para>
/// <para>
/// <b>같은 rollout id, 다른 길이/해시는 "마지막 공통 슬라이스"일 때만 append로 인정한다.</b>
/// 비-마지막 위치에서 같은 rollout id인데 내용이 다르면(이론상 발생하면 안 되지만) 안전하게
/// <see cref="RevisionRelation.Diverged"/>로 취급한다. 마지막 위치라도 실제로 한쪽이 다른 쪽의
/// 정확한 byte prefix인지 <b>다시 읽어서</b> 확인한다 — 길이가 다르다는 사실만으로 append라고
/// 가정하지 않는다(같은 길이에서 시작해 서로 다르게 갈라진 뒤 우연히 한쪽이 더 길어졌을 수도 있다).
/// </para>
/// </remarks>
public static class ConversationRevisionComparer
{
    /// <summary>두 revision을 비교한다.</summary>
    public static RevisionRelation Compare(
        ConversationRevision local,
        IRolloutSliceReader localReader,
        ConversationRevision incoming,
        IRolloutSliceReader incomingReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(localReader);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(incomingReader);

        IReadOnlyList<RolloutSlice> a = local.OrderedSlices;
        IReadOnlyList<RolloutSlice> b = incoming.OrderedSlices;
        int common = Math.Min(a.Count, b.Count);

        for (int i = 0; i < common; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RolloutSlice ls = a[i];
            RolloutSlice rs = b[i];

            if (!string.Equals(ls.RolloutId, rs.RolloutId, StringComparison.OrdinalIgnoreCase))
            {
                return RevisionRelation.Diverged;
            }

            bool sameContent = ls.LogicalByteLength == rs.LogicalByteLength &&
                string.Equals(ls.Sha256Hex, rs.Sha256Hex, StringComparison.Ordinal);
            if (sameContent)
            {
                continue;
            }

            bool isLastOfBoth = i == a.Count - 1 && i == b.Count - 1;
            if (!isLastOfBoth)
            {
                // 마지막이 아닌 위치에서 같은 rollout id인데 내용이 다르다 — 이 leaf 입장에서 이
                // 파일의 컷오프는 오직 "바로 다음 링크가 가리키는 지점"으로만 정해지므로, 같은 leaf를
                // 비교하는 두 revision에서 여기가 다르다는 건 그 자체로 일관되지 않은 lineage다.
                return RevisionRelation.Diverged;
            }

            return CompareLastCommonSlice(ls, localReader, rs, incomingReader, cancellationToken);
        }

        if (a.Count == b.Count)
        {
            return RevisionRelation.Identical;
        }

        // 공통 구간 전부가 동일했고 한쪽에만 추가 슬라이스(새 segment)가 있다 — 그쪽이 더 진행된 것.
        return b.Count > a.Count ? RevisionRelation.IncomingAhead : RevisionRelation.LocalAhead;
    }

    private static RevisionRelation CompareLastCommonSlice(
        RolloutSlice localSlice,
        IRolloutSliceReader localReader,
        RolloutSlice incomingSlice,
        IRolloutSliceReader incomingReader,
        CancellationToken cancellationToken)
    {
        if (localSlice.LogicalByteLength == incomingSlice.LogicalByteLength)
        {
            // 길이가 같은데 해시가 다르면 순수 발산이다(append로 설명할 수 없다).
            return RevisionRelation.Diverged;
        }

        (RolloutSlice shorter, RolloutSlice longer, IRolloutSliceReader longerReader, RevisionRelation ifPrefix) =
            localSlice.LogicalByteLength < incomingSlice.LogicalByteLength
                ? (localSlice, incomingSlice, incomingReader, RevisionRelation.IncomingAhead)
                : (incomingSlice, localSlice, localReader, RevisionRelation.LocalAhead);

        // 더 긴 쪽을 짧은 쪽의 길이만큼만 잘라 다시 해시해본다 — 이게 짧은 쪽과 정확히 같아야만
        // "진짜로 이어쓴 것"이다. 단순히 더 길다는 이유만으로 prefix라고 가정하지 않는다.
        RolloutSliceHasher.Result truncated = longerReader.Hash(
            longer.File, cutoffOrdinalExclusive: null, cutoffByteOffsetExclusive: shorter.LogicalByteLength, cancellationToken);

        bool isGenuinePrefix = truncated.LogicalByteLength == shorter.LogicalByteLength &&
            string.Equals(truncated.Sha256Hex, shorter.Sha256Hex, StringComparison.Ordinal);

        return isGenuinePrefix ? ifPrefix : RevisionRelation.Diverged;
    }
}
