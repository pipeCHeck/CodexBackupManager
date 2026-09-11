using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Codex.Threads;

/// <summary>
/// 여러 rollout 파일로 나뉜 대화를 하나의 <see cref="ThreadChain"/>으로 묶는다.
/// </summary>
/// <remarks>
/// <para>
/// 같은 thread ID를 가진 파일(페이지네이션 세그먼트, <c>_&lt;segmentId&gt;</c>)을 시간순으로 묶어
/// 한 thread가 여러 개의 독립된 대화로 중복 표시되지 않게 한다.
/// </para>
/// <para>
/// 분기 부모(<c>forked_from_id</c>/<c>history_base.thread_id</c>)는 저장만 하고 따라가지 않는다.
/// 실제로 조상 체인을 계산해야 할 때는 <see cref="ResolveAncestry"/>를 쓴다 — 순환 참조와
/// 누락된 부모를 안전하게 처리한다(무한 루프 없음).
/// </para>
/// </remarks>
public static class ThreadChainResolver
{
    /// <summary>
    /// 파일을 thread ID로 묶어 체인 목록을 만든다.
    /// </summary>
    /// <param name="files">한 Codex Home에서 찾은 모든 rollout 파일.</param>
    /// <param name="metadataByFile">
    /// 각 파일에서 미리 파싱한 <see cref="SessionMetadata"/>. 파싱하지 못한 파일은 값이 <c>null</c>이거나
    /// 딕셔너리에 아예 없을 수 있다 — 두 경우 모두 안전하게 처리한다.
    /// </param>
    public static IReadOnlyDictionary<string, ThreadChain> Resolve(
        IReadOnlyList<RolloutFileReference> files,
        IReadOnlyDictionary<RolloutFileReference, SessionMetadata?> metadataByFile)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(metadataByFile);

        var byThread = new Dictionary<string, List<RolloutFileReference>>(StringComparer.OrdinalIgnoreCase);
        foreach (RolloutFileReference file in files)
        {
            if (!byThread.TryGetValue(file.ThreadId, out List<RolloutFileReference>? group))
            {
                group = [];
                byThread[file.ThreadId] = group;
            }

            group.Add(file);
        }

        var result = new Dictionary<string, ThreadChain>(StringComparer.OrdinalIgnoreCase);
        foreach ((string threadId, List<RolloutFileReference> group) in byThread)
        {
            List<RolloutFileReference> ordered = group
                .OrderBy(f => f.TimestampFromFileName ?? DateTimeOffset.MinValue)
                .ThenBy(f => f.SegmentId ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            var warnings = new List<string>();
            if (ordered.Count != group.Count)
            {
                warnings.Add("정렬 중 파일 수가 달라졌습니다."); // 이론상 발생하지 않지만 방어적으로 기록한다.
            }

            // 실측(Phase 3 pre-commit audit)으로 확인: 세그먼트끼리도 무조건 전체를 잇지 않는다.
            // 첫 파일뿐 아니라 뒤이은 세그먼트도 "자기 자신의" history_base를 가질 수 있고,
            // 그 thread_id는 이 thread의 안정 ID가 아니라 바로 앞 세그먼트 파일 자신의
            // rollout ID(RolloutFileReference.OwnRolloutId)를 가리킨다. 그래서 파일마다
            // 각자의 메타데이터에서 history_base를 따로 읽는다(첫 파일만 보던 이전 버그 수정).
            var fileHistoryBases = new List<HistoryBaseReference?>(ordered.Count);
            foreach (RolloutFileReference file in ordered)
            {
                fileHistoryBases.Add(metadataByFile.GetValueOrDefault(file)?.HistoryBase);
            }

            SessionMetadata? baseMetadata = metadataByFile.GetValueOrDefault(ordered[0]);
            string? parentThreadId = fileHistoryBases[0]?.ThreadId ?? baseMetadata?.ForkedFromId;
            long? parentEndOrdinal = fileHistoryBases[0]?.EndOrdinalExclusive;
            long? parentEndByteOffset = fileHistoryBases[0]?.EndByteOffset;

            if (parentThreadId is not null && string.Equals(parentThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("자기 자신을 분기 부모로 참조합니다. 부모 연결을 무시합니다.");
                parentThreadId = null;
                parentEndOrdinal = null;
                parentEndByteOffset = null;
                fileHistoryBases[0] = null;
            }

            // 세그먼트 간 history_base가 실제로 "바로 앞 세그먼트 자신의 rollout ID"를 가리키는지
            // 검증한다. 어긋나면 추측으로 자르지 않고 경고만 남긴 뒤 안전하게 전체를 포함한다.
            for (int i = 1; i < ordered.Count; i++)
            {
                HistoryBaseReference? hb = fileHistoryBases[i];
                if (hb is null)
                {
                    continue;
                }

                string expectedOwnRolloutId = ordered[i - 1].OwnRolloutId;
                if (!string.Equals(hb.ThreadId, expectedOwnRolloutId, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"{ordered[i].FileName}: history_base가 바로 앞 세그먼트를 가리키지 않아 " +
                        "경계를 확정할 수 없습니다. 해당 구간을 잘라내지 않고 전체를 포함합니다.");
                    fileHistoryBases[i] = null;
                }
            }

            result[threadId] = new ThreadChain(threadId, ordered, fileHistoryBases, parentThreadId, parentEndOrdinal, parentEndByteOffset, warnings);
        }

        return result;
    }

    /// <summary>
    /// 분기 부모를 따라가며 조상 thread ID 목록을 계산한다(뿌리 → 자신 순서).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 순환 참조나 누락된 부모가 있어도 <b>절대 무한 루프에 빠지지 않는다.</b> 이미 방문한 thread를
    /// 다시 만나면 그 지점에서 멈추고 <paramref name="hasCycle"/>을 참으로 돌려준다.
    /// 부모가 카탈로그에 없으면(원본 손상 등) 있는 데까지만 돌려주고 조용히 멈춘다.
    /// </para>
    /// <para>
    /// <c>chain.ParentThreadId</c>는 <see cref="Domain.Codex.Sessions.HistoryBaseReference.ThreadId"/>를
    /// 그대로 옮긴 값이라 <b>rollout ID</b>다(공식 소스 주석 확인, docs/codex-storage-format.md §3) —
    /// 대상 thread의 안정적인 ID(=<paramref name="chains"/>의 key)와 다를 수 있다. 예를 들어
    /// 다중 세그먼트 thread의 비루트 세그먼트에서 분기했다면 그 세그먼트 자신의 rollout ID를
    /// 가리킨다. 그래서 <see cref="BuildRolloutIdIndex"/>로 rollout ID → 실제 소유 thread를
    /// 먼저 찾은 뒤 그 thread로 이동한다.
    /// </para>
    /// </remarks>
    /// <param name="threadId">시작 thread ID.</param>
    /// <param name="chains"><see cref="Resolve"/>가 만든 체인 맵.</param>
    /// <param name="hasCycle">순환 참조를 발견했는지.</param>
    public static IReadOnlyList<string> ResolveAncestry(
        string threadId,
        IReadOnlyDictionary<string, ThreadChain> chains,
        out bool hasCycle)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(chains);

        IReadOnlyDictionary<string, string> rolloutIdIndex = BuildRolloutIdIndex(chains);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        hasCycle = false;

        string? current = threadId;
        while (current is not null)
        {
            if (!visited.Add(current))
            {
                hasCycle = true;
                break;
            }

            path.Add(current);

            if (!chains.TryGetValue(current, out ThreadChain? chain))
            {
                break; // 부모 누락. 있는 데까지만 돌려주고 멈춘다.
            }

            current = chain.ParentThreadId is { } rawTarget
                ? rolloutIdIndex.GetValueOrDefault(rawTarget, rawTarget)
                : null;
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// 모든 체인의 모든 파일에 대해 <c>rollout ID(<see cref="RolloutFileReference.OwnRolloutId"/>) →
    /// 그 파일이 속한 thread의 안정적인 ID</c> 색인을 만든다. 분기/이어붙이기 대상을 찾을 때 쓴다.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildRolloutIdIndex(IReadOnlyDictionary<string, ThreadChain> chains)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string threadId, ThreadChain chain) in chains)
        {
            foreach (RolloutFileReference file in chain.Files)
            {
                index[file.OwnRolloutId] = threadId;
            }
        }

        return index;
    }
}
