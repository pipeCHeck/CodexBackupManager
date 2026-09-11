using System;
using System.Collections.Generic;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Threads;

public sealed class ThreadChainResolverTests
{
    private static RolloutFileReference File(string threadId, string? segmentId, DateTimeOffset timestamp)
        => new($"C:\\fixture\\{threadId}{(segmentId is null ? "" : "_" + segmentId)}.jsonl",
            $"{threadId}.jsonl", threadId, segmentId, timestamp, IsArchived: false, RolloutFileKind.PlainJsonl);

    [Fact]
    public void 세그먼트_파일들을_시간순으로_하나의_체인으로_묶는다()
    {
        RolloutFileReference first = File("t1", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        RolloutFileReference second = File("t1", "seg-2", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var chains = ThreadChainResolver.Resolve([second, first], new Dictionary<RolloutFileReference, SessionMetadata?>());

        ThreadChain chain = chains["t1"];
        Assert.True(chain.IsSegmented);
        Assert.Equal(2, chain.Files.Count);
        Assert.Same(first, chain.Files[0]);
        Assert.Same(second, chain.Files[1]);
        Assert.Same(second, chain.LatestFile);
    }

    [Fact]
    public void history_base로_부모_thread를_연결한다()
    {
        RolloutFileReference file = File("child", null, DateTimeOffset.UnixEpoch);
        var metadata = new Dictionary<RolloutFileReference, SessionMetadata?>
        {
            [file] = new SessionMetadata
            {
                ThreadId = "child",
                SourceFilePath = file.FullPath,
                HistoryBase = new HistoryBaseReference("parent", 5, 1200),
            },
        };

        var chains = ThreadChainResolver.Resolve([file], metadata);

        ThreadChain chain = chains["child"];
        Assert.Equal("parent", chain.ParentThreadId);
        Assert.Equal(5, chain.ParentEndOrdinalExclusive);
        Assert.Equal(1200, chain.ParentEndByteOffset);
    }

    [Fact]
    public void 자기_자신을_부모로_참조하면_무시하고_경고를_남긴다()
    {
        RolloutFileReference file = File("self", null, DateTimeOffset.UnixEpoch);
        var metadata = new Dictionary<RolloutFileReference, SessionMetadata?>
        {
            [file] = new SessionMetadata { ThreadId = "self", SourceFilePath = file.FullPath, ForkedFromId = "self" },
        };

        var chains = ThreadChainResolver.Resolve([file], metadata);

        ThreadChain chain = chains["self"];
        Assert.Null(chain.ParentThreadId);
        Assert.NotEmpty(chain.Warnings);
    }

    [Fact]
    public void 부모가_카탈로그에_없으면_있는_데까지만_돌려주고_멈춘다()
    {
        var chains = new Dictionary<string, ThreadChain>
        {
            ["child"] = new ThreadChain("child", [File("child", null, DateTimeOffset.UnixEpoch)], new HistoryBaseReference?[1], "missing-parent", null, null, []),
        };

        IReadOnlyList<string> ancestry = ThreadChainResolver.ResolveAncestry("child", chains, out bool hasCycle);

        // "missing-parent"는 실제 체인 레코드가 없지만, 참조 자체는 존재가 확인된 조상이므로
        // 목록에는 남기고(뿌리 → 자신 순서) 그 다음 조상을 찾으려는 시도만 멈춘다.
        Assert.False(hasCycle);
        Assert.Equal(["missing-parent", "child"], ancestry);
    }

    [Fact]
    public void 순환_참조가_있어도_무한루프_없이_멈추고_사실을_알린다()
    {
        var chains = new Dictionary<string, ThreadChain>
        {
            ["a"] = new ThreadChain("a", [File("a", null, DateTimeOffset.UnixEpoch)], new HistoryBaseReference?[1], "b", null, null, []),
            ["b"] = new ThreadChain("b", [File("b", null, DateTimeOffset.UnixEpoch)], new HistoryBaseReference?[1], "a", null, null, []),
        };

        IReadOnlyList<string> ancestry = ThreadChainResolver.ResolveAncestry("a", chains, out bool hasCycle);

        Assert.True(hasCycle);
        Assert.True(ancestry.Count <= 2);
    }
}
