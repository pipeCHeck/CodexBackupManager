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
/// <b>Phase 06_01 정정 — "같은 rollout id, 다른 내용"은 두 revision이 그 위치에서 각각 끝나는지로
/// 판단한다(마지막 <b>공통</b> 인덱스인지가 아니다).</b> 최초 구현은 "두 revision이 모두 그 인덱스가
/// 자신의 마지막 슬라이스일 때만" prefix 검사를 했는데, 이러면 다음과 같은 정상적인 fast-forward를
/// 오판했다:
/// <code>
/// Local    = [R1-short]
/// Incoming = [R1-long, R2]
/// </code>
/// R1은 로컬의 마지막 슬라이스지만 incoming의 마지막 슬라이스가 아니므로(뒤에 R2가 더 있다) 예전
/// 구현은 이 지점을 무조건 Diverged로 취급했다. 하지만 이건 "로컬이 R1이 아직 짧았을 때의 과거
/// snapshot이고, incoming에서는 그 뒤 R1이 이어써진 다음 새 segment R2까지 생긴" 정상적인
/// IncomingAhead다. 지금은 "이 인덱스가 <b>내</b> 마지막 슬라이스인지"를 각 쪽에서 독립적으로
/// 판단한다 — 한쪽만 끝나고 다른 쪽이 계속되면(그 다른 쪽 슬라이스가 진짜 byte prefix를 포함하는
/// superset일 때) 여전히 fast-forward로 인정한다. <b>양쪽 모두 그 인덱스 뒤에도 계속되는데
/// 내용이 다르면</b>(예: 로컬은 R1-local, R2 / incoming은 R1-incoming, R2) 여전히 안전하게
/// Diverged로 취급한다 — 이건 이 leaf 기준으로 정상적인 fast-forward 시나리오로 설명할 수 없다.
/// </para>
/// <para>
/// 길이만 보고 판단하지 않는다 — 실제로 한쪽이 다른 쪽의 정확한 byte prefix인지 항상
/// <see cref="IRolloutSliceReader"/>로 다시 읽어서 확인한다(같은 길이에서 시작해 서로 다르게 갈라진
/// 뒤 우연히 한쪽이 더 길어졌을 수도 있으므로, 길이 비교만으로는 절대 append라고 가정하지 않는다).
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

            // 같은 rollout id인데 내용이 다르다 — 각 쪽이 "여기서 끝나는지"를 독립적으로 본다
            // (예전처럼 "둘 다 마지막일 때만"이 아니다, 클래스 remarks 참고).
            bool localEndsHere = i == a.Count - 1;
            bool incomingEndsHere = i == b.Count - 1;

            if (localEndsHere && incomingEndsHere)
            {
                return CompareTransitionSlice(ls, localReader, rs, incomingReader, cancellationToken);
            }

            if (localEndsHere) // && !incomingEndsHere
            {
                return CompareAsymmetricTransition(
                    shorterCandidate: ls, shorterReader: localReader,
                    longerCandidate: rs, longerReader: incomingReader,
                    ifGenuinePrefix: RevisionRelation.IncomingAhead, cancellationToken);
            }

            if (incomingEndsHere) // && !localEndsHere
            {
                return CompareAsymmetricTransition(
                    shorterCandidate: rs, shorterReader: incomingReader,
                    longerCandidate: ls, longerReader: localReader,
                    ifGenuinePrefix: RevisionRelation.LocalAhead, cancellationToken);
            }

            // 이 위치 뒤에도 로컬/incoming 양쪽 모두 계속되는데 여기서부터 이미 다르다 — 이 leaf
            // 기준으로 정상적인 fast-forward 시나리오가 아니다(클래스 remarks 참고). 안전하게 발산.
            return RevisionRelation.Diverged;
        }

        if (a.Count == b.Count)
        {
            return RevisionRelation.Identical;
        }

        // 공통 구간 전부가 동일했고 한쪽에만 추가 슬라이스(새 segment)가 있다 — 그쪽이 더 진행된 것.
        return b.Count > a.Count ? RevisionRelation.IncomingAhead : RevisionRelation.LocalAhead;
    }

    /// <summary>
    /// 양쪽 모두 이 인덱스에서 끝나는(=각자의 마지막 슬라이스인) 전이 지점을 비교한다. 길이가 같으면
    /// prefix로 설명할 수 없으므로 바로 Diverged, 다르면 더 짧은 쪽이 더 긴 쪽의 진짜 byte prefix인지
    /// 확인한다.
    /// </summary>
    private static RevisionRelation CompareTransitionSlice(
        RolloutSlice local,
        IRolloutSliceReader localReader,
        RolloutSlice incoming,
        IRolloutSliceReader incomingReader,
        CancellationToken cancellationToken)
    {
        if (local.LogicalByteLength == incoming.LogicalByteLength)
        {
            // 길이가 같은데 해시가 다르면 순수 발산이다(append로 설명할 수 없다).
            return RevisionRelation.Diverged;
        }

        return local.LogicalByteLength < incoming.LogicalByteLength
            ? CompareAsymmetricTransition(local, localReader, incoming, incomingReader, RevisionRelation.IncomingAhead, cancellationToken)
            : CompareAsymmetricTransition(incoming, incomingReader, local, localReader, RevisionRelation.LocalAhead, cancellationToken);
    }

    /// <summary>
    /// <paramref name="shorterCandidate"/>가 이 위치에서 끝나는(더 이상 슬라이스가 없는) 쪽이고,
    /// <paramref name="longerCandidate"/>는 같은 rollout id의 슬라이스를 갖되 그 뒤로도 계속될 수
    /// 있는(같은 슬라이스가 더 길거나, 이어지는 segment가 더 있는) 쪽이다. "끝난" 쪽의 내용이 정확히
    /// "계속되는" 쪽 슬라이스의 byte prefix일 때만 <paramref name="ifGenuinePrefix"/>를 돌려준다 —
    /// 뒤에 segment가 더 있어도 상관없다(그 자체로는 이미 검증된 fast-forward를 무효화하지 않는다).
    /// </summary>
    private static RevisionRelation CompareAsymmetricTransition(
        RolloutSlice shorterCandidate,
        IRolloutSliceReader shorterReader,
        RolloutSlice longerCandidate,
        IRolloutSliceReader longerReader,
        RevisionRelation ifGenuinePrefix,
        CancellationToken cancellationToken)
    {
        if (shorterCandidate.LogicalByteLength >= longerCandidate.LogicalByteLength)
        {
            // "끝난" 쪽이 "계속되는" 쪽의 이 위치 슬라이스와 같거나 더 길다 — 정상적인 이어쓰기로
            // 설명할 수 없다(끝난 쪽이 더 많은/같은 바이트를 갖고 있는데 상대는 별도로 계속됐다는 뜻).
            return RevisionRelation.Diverged;
        }

        // 실제로 byte prefix인지 다시 읽어서 확인한다 — 길이가 더 짧다는 사실만으로 prefix라고
        // 가정하지 않는다(같은 시작점에서 갈라진 뒤 우연히 한쪽이 더 길어졌을 수도 있다).
        RolloutSliceHasher.Result truncated = longerReader.Hash(
            longerCandidate.File, cutoffOrdinalExclusive: null,
            cutoffByteOffsetExclusive: shorterCandidate.LogicalByteLength, cancellationToken);

        bool isGenuinePrefix = truncated.LogicalByteLength == shorterCandidate.LogicalByteLength &&
            string.Equals(truncated.Sha256Hex, shorterCandidate.Sha256Hex, StringComparison.Ordinal);

        return isGenuinePrefix ? ifGenuinePrefix : RevisionRelation.Diverged;
    }
}
