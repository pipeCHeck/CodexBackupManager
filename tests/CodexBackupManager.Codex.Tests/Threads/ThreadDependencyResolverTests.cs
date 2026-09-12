using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Threads;

/// <summary>
/// Phase 5 Export가 Viewer(<see cref="Conversation.ConversationTranscriptBuilder"/>)와 똑같은
/// lineage 판단을 쓰는지 확인한다. 이 파일은 <see cref="ThreadDependencyResolver"/> 자체를,
/// <see cref="Conversation.ConversationTranscriptBuilderTests"/>는 메시지 결과를 검증한다 — 두
/// 테스트가 같은 합성 체인 모양(세그먼트/분기)을 겹치지 않게 각자 검증한다.
/// </summary>
public sealed class ThreadDependencyResolverTests
{
    private static RolloutFileReference File(string threadId, string? segmentId, DateTimeOffset timestamp)
        => new($"C:\\fixture\\{threadId}{(segmentId is null ? "" : "_" + segmentId)}.jsonl",
            $"{threadId}.jsonl", threadId, segmentId, timestamp, IsArchived: false, RolloutFileKind.PlainJsonl);

    [Fact]
    public void 조상이_없으면_자기_체인_전체를_돌려준다()
    {
        RolloutFileReference f1 = File("solo", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        RolloutFileReference f2 = File("solo", "seg2", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var chain = new ThreadChain("solo", [f1, f2], new HistoryBaseReference?[2], null, null, null, []);
        var chains = new Dictionary<string, ThreadChain> { ["solo"] = chain };
        var warnings = new List<string>();

        var links = ThreadDependencyResolver.ResolveChainLinks("solo", chains, warnings, out bool hasCycle);

        Assert.False(hasCycle);
        var link = Assert.Single(links);
        Assert.Equal("solo", link.ThreadId);
        Assert.Equal([f1, f2], link.Files); // leaf는 세그먼트를 자르지 않는다.
    }

    [Fact]
    public void 조상이_여러_세그먼트로_나뉘어_있고_분기가_첫_세그먼트를_가리키면_그_뒤_세그먼트는_제외한다()
    {
        // 실측(docs/codex-storage-format.md §3)으로 확인된 시나리오: 조상이 세그먼트 2개로 나뉘고,
        // 자식은 조상의 "마지막"이 아니라 "첫 번째" 세그먼트 지점에서 분기했다 — 조상 자신은 그 뒤에도
        // 별도로 계속됐지만(두 번째 세그먼트), 그 내용은 이 자식과 무관하므로 포함하면 안 된다.
        RolloutFileReference parentSeg1 = File("parent", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        RolloutFileReference parentSeg2 = File("parent", "seg2", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        RolloutFileReference childFile = File("child", null, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var parentChain = new ThreadChain(
            "parent", [parentSeg1, parentSeg2], new HistoryBaseReference?[2], null, null, null, []);
        var childChain = new ThreadChain(
            "child", [childFile], new HistoryBaseReference?[1],
            ParentThreadId: parentSeg1.OwnRolloutId, ParentEndOrdinalExclusive: 10, ParentEndByteOffset: null, []);

        var chains = new Dictionary<string, ThreadChain> { ["parent"] = parentChain, ["child"] = childChain };
        var warnings = new List<string>();

        var links = ThreadDependencyResolver.ResolveChainLinks("child", chains, warnings, out bool hasCycle);

        Assert.False(hasCycle);
        Assert.Equal(2, links.Count);
        Assert.Equal("parent", links[0].ThreadId);
        Assert.Equal([parentSeg1], links[0].Files); // seg2는 제외 — 분기 이후 조상의 별도 연속.
        Assert.Equal("child", links[1].ThreadId);
        Assert.Equal([childFile], links[1].Files);
    }

    [Fact]
    public void 대상_rollout_ID를_찾지_못하면_잘라내지_않고_전체를_포함한다()
    {
        // ThreadChainResolver가 이미 검증해서 실제로는 거의 발생하지 않는 경로지만(§ ThreadChainResolver
        // Resolve의 방어적 검증), SelectFiles 단독으로도 "확신 없으면 전체 포함"이라는 안전한 기본값을
        // 지키는지 직접 확인한다.
        RolloutFileReference f1 = File("t", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        RolloutFileReference f2 = File("t", "seg2", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var chain = new ThreadChain("t", [f1, f2], new HistoryBaseReference?[2], null, null, null, []);

        IReadOnlyList<RolloutFileReference> files = ThreadDependencyResolver.SelectFiles(chain, "no-such-rollout-id");

        Assert.Equal([f1, f2], files);
    }

    [Fact]
    public void SelectFiles는_외부_대상이_없으면_체인_전체를_돌려준다()
    {
        RolloutFileReference f = File("t", null, DateTimeOffset.UnixEpoch);
        var chain = new ThreadChain("t", [f], new HistoryBaseReference?[1], null, null, null, []);

        IReadOnlyList<RolloutFileReference> files = ThreadDependencyResolver.SelectFiles(chain, null);

        Assert.Same(chain.Files, files);
    }
}
