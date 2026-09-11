using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Conversation;

public sealed class ConversationTranscriptBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));

    public ConversationTranscriptBuilderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string EventMsgLine(long ordinal, string threadId, string itemType, string itemId, string text, string? phase = null)
    {
        string phaseJson = phase is null ? string.Empty : ",\"phase\":\"" + phase + "\"";
        return "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
               ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"thread_id\":\"" + threadId + "\",\"turn_id\":\"turn-1\"," +
               "\"item\":{\"type\":\"" + itemType + "\",\"id\":\"" + itemId + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]" + phaseJson + "}}}";
    }

    private RolloutFileReference WriteFile(string threadId, string fileName, string? segmentId, DateTimeOffset timestamp, IEnumerable<string> lines)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return new RolloutFileReference(path, fileName, threadId, segmentId, timestamp, IsArchived: false, RolloutFileKind.PlainJsonl);
    }

    /// <summary>
    /// 테스트용 <see cref="ThreadChain"/> 생성 편의 함수. <c>fileHistoryBases</c>를 지정하지 않으면
    /// 전부 <c>null</c>(내부 세그먼트 경계 없음)로 채운다 — 대부분의 테스트는 파일이 1개뿐이라
    /// 이 값이 의미가 없다.
    /// </summary>
    private static ThreadChain MakeChain(
        string threadId,
        IReadOnlyList<RolloutFileReference> files,
        string? parentThreadId = null,
        long? parentEndOrdinalExclusive = null,
        long? parentEndByteOffset = null,
        IReadOnlyList<HistoryBaseReference?>? fileHistoryBases = null,
        IReadOnlyList<string>? warnings = null)
        => new(
            threadId,
            files,
            fileHistoryBases ?? new HistoryBaseReference?[files.Count],
            parentThreadId,
            parentEndOrdinalExclusive,
            parentEndByteOffset,
            warnings ?? []);

    [Fact]
    public void 세그먼트_파일_순서대로_메시지를_이어붙인다()
    {
        RolloutFileReference first = WriteFile("t1", "t1-a.jsonl", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            [EventMsgLine(0, "t1", "UserMessage", "u1", "첫 파일 메시지")]);
        RolloutFileReference second = WriteFile("t1", "t1-b.jsonl", "seg2", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            [EventMsgLine(0, "t1", "AgentMessage", "a1", "둘째 파일 메시지", "final")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["t1"] = MakeChain("t1", [first, second]),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("t1", chains);

        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal("첫 파일 메시지", transcript.Messages[0].Text);
        Assert.Equal("둘째 파일 메시지", transcript.Messages[1].Text);
    }

    [Fact]
    public void history_base_부모의_상속_구간을_포함한다()
    {
        RolloutFileReference parentFile = WriteFile("parent", "parent.jsonl", null, DateTimeOffset.UnixEpoch,
        [
            EventMsgLine(0, "parent", "UserMessage", "pu1", "부모 상속됨 1"),
            EventMsgLine(1, "parent", "AgentMessage", "pa1", "부모 상속됨 2", "final"),
            EventMsgLine(2, "parent", "UserMessage", "pu2", "부모 이후 활동(제외)"),
        ]);
        RolloutFileReference childFile = WriteFile("child", "child.jsonl", null, DateTimeOffset.UnixEpoch.AddDays(1),
            [EventMsgLine(0, "child", "UserMessage", "cu1", "자식 자신의 메시지")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [parentFile]),
            ["child"] = MakeChain("child", [childFile], parentThreadId: "parent", parentEndOrdinalExclusive: 2),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("child", chains);

        Assert.Equal(
            ["부모 상속됨 1", "부모 상속됨 2", "자식 자신의 메시지"],
            transcript.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void end_ordinal_exclusive_이후_부모_메시지는_제외된다()
    {
        RolloutFileReference parentFile = WriteFile("parent", "parent.jsonl", null, DateTimeOffset.UnixEpoch,
        [
            EventMsgLine(0, "parent", "UserMessage", "pu1", "포함"),
            EventMsgLine(1, "parent", "UserMessage", "pu2", "경계(제외)"),
        ]);
        RolloutFileReference childFile = WriteFile("child", "child.jsonl", null, DateTimeOffset.UnixEpoch.AddDays(1),
            [EventMsgLine(0, "child", "UserMessage", "cu1", "자식")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [parentFile]),
            ["child"] = MakeChain("child", [childFile], parentThreadId: "parent", parentEndOrdinalExclusive: 1),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("child", chains);

        Assert.DoesNotContain(transcript.Messages, m => m.Text == "경계(제외)");
        Assert.Equal(["포함", "자식"], transcript.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void 같은_thread의_세그먼트끼리도_자기_history_base_경계를_지킨다()
    {
        // 실측 확인(Phase 3 pre-commit audit, 공식 소스 codex-rs/protocol/src/protocol.rs
        // HistoryPosition 주석과 일치): 세그먼트는 이전 세그먼트를 무조건 전부 잇지 않는다.
        // 두 번째 세그먼트 자신이 history_base를 가지며, 그 thread_id는 안정적인 thread ID가
        // 아니라 "바로 앞 세그먼트 파일 자신의 rollout ID"를 가리킨다.
        RolloutFileReference seg1 = WriteFile("parent", "parent-1.jsonl", null, DateTimeOffset.UnixEpoch,
        [
            EventMsgLine(0, "parent", "UserMessage", "p0", "포함(seg1 ordinal 0)"),
            EventMsgLine(1, "parent", "UserMessage", "p1", "제외(seg1이 계속되며 남긴 별도 활동)"),
        ]);
        RolloutFileReference seg2 = WriteFile("parent", "parent-2.jsonl", "seg2", DateTimeOffset.UnixEpoch.AddHours(1),
        [
            EventMsgLine(1, "parent", "UserMessage", "p2", "포함(seg2 자신의 새 내용)"),
        ]);

        IReadOnlyList<HistoryBaseReference?> fileHistoryBases =
            [null, new HistoryBaseReference(seg1.OwnRolloutId, EndOrdinalExclusive: 1, EndByteOffset: null)];
        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [seg1, seg2], fileHistoryBases: fileHistoryBases),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("parent", chains);

        Assert.Equal(["포함(seg1 ordinal 0)", "포함(seg2 자신의 새 내용)"], transcript.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void 다른_thread가_조상의_마지막이_아닌_중간_세그먼트에서_분기해도_정확한_지점을_찾는다()
    {
        // cross-thread 분기도 같은 메커니즘이라, 조상이 여러 세그먼트로 나뉜 뒤에도 분기 지점이
        // "가장 최근" 세그먼트가 아니라 중간 세그먼트일 수 있다(그 세그먼트 이후 조상 자신은
        // 별도로 계속 이어졌을 수 있음). child는 seg2(중간)에서 분기하고, seg3(seg2 이후 조상 자신의
        // 계속된 활동)은 child의 관점에서 완전히 무관하므로 포함되면 안 된다.
        RolloutFileReference seg1 = WriteFile("parent", "parent-1.jsonl", null, DateTimeOffset.UnixEpoch,
            [EventMsgLine(0, "parent", "UserMessage", "p0", "포함(seg1)")]);
        RolloutFileReference seg2 = WriteFile("parent", "parent-2.jsonl", "seg2", DateTimeOffset.UnixEpoch.AddHours(1),
        [
            EventMsgLine(1, "parent", "UserMessage", "p1", "포함(seg2 자신, child가 상속)"),
            EventMsgLine(2, "parent", "UserMessage", "p2", "제외(child의 cutoff 이상)"),
        ]);
        RolloutFileReference seg3 = WriteFile("parent", "parent-3.jsonl", "seg3", DateTimeOffset.UnixEpoch.AddHours(2),
            [EventMsgLine(3, "parent", "UserMessage", "p3", "제외(seg2 이후 parent 자신의 별도 연속, child와 무관)")]);
        RolloutFileReference childFile = WriteFile("child", "child.jsonl", null, DateTimeOffset.UnixEpoch.AddDays(1),
            [EventMsgLine(0, "child", "UserMessage", "cu1", "자식 자신")]);

        IReadOnlyList<HistoryBaseReference?> fileHistoryBases =
            [null, new HistoryBaseReference(seg1.OwnRolloutId, 1, null), new HistoryBaseReference(seg2.OwnRolloutId, 3, null)];
        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [seg1, seg2, seg3], fileHistoryBases: fileHistoryBases),
            ["child"] = MakeChain("child", [childFile], parentThreadId: seg2.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("child", chains);

        Assert.Equal(
            ["포함(seg1)", "포함(seg2 자신, child가 상속)", "자식 자신"],
            transcript.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void ordinal이_없을_때는_byte_offset_경계를_쓴다()
    {
        string line0 = EventMsgLine(0, "parent", "UserMessage", "pu1", "포함(byte 안쪽)");
        string line1 = EventMsgLine(1, "parent", "UserMessage", "pu2", "제외(byte 바깥)");
        long cutoffByteOffset = System.Text.Encoding.UTF8.GetByteCount(line0) + 1; // line0 끝(개행 포함) 직후

        RolloutFileReference parentFile = WriteFile("parent", "parent.jsonl", null, DateTimeOffset.UnixEpoch, [line0, line1]);
        RolloutFileReference childFile = WriteFile("child", "child.jsonl", null, DateTimeOffset.UnixEpoch.AddDays(1),
            [EventMsgLine(0, "child", "UserMessage", "cu1", "자식")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = MakeChain("parent", [parentFile]),
            ["child"] = MakeChain("child", [childFile], parentThreadId: "parent", parentEndByteOffset: cutoffByteOffset),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("child", chains);

        Assert.Equal(["포함(byte 안쪽)", "자식"], transcript.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void 부모가_없으면_경고를_남기고_자신의_메시지만_보여준다()
    {
        RolloutFileReference childFile = WriteFile("child", "child.jsonl", null, DateTimeOffset.UnixEpoch,
            [EventMsgLine(0, "child", "UserMessage", "cu1", "자식만")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["child"] = MakeChain("child", [childFile], parentThreadId: "missing-parent", parentEndOrdinalExclusive: 3),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("child", chains);

        Assert.Equal(["자식만"], transcript.Messages.Select(m => m.Text).ToArray());
        Assert.NotEmpty(transcript.Warnings);
    }

    [Fact]
    public void 순환_참조가_있어도_무한루프_없이_경고와_함께_처리한다()
    {
        RolloutFileReference fileA = WriteFile("a", "a.jsonl", null, DateTimeOffset.UnixEpoch,
            [EventMsgLine(0, "a", "UserMessage", "au1", "A 메시지")]);
        RolloutFileReference fileB = WriteFile("b", "b.jsonl", null, DateTimeOffset.UnixEpoch,
            [EventMsgLine(0, "b", "UserMessage", "bu1", "B 메시지")]);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["a"] = MakeChain("a", [fileA], parentThreadId: "b", parentEndOrdinalExclusive: 0),
            ["b"] = MakeChain("b", [fileB], parentThreadId: "a", parentEndOrdinalExclusive: 0),
        };

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("a", chains);

        Assert.NotEmpty(transcript.Warnings);
        Assert.True(transcript.Messages.Count <= 2);
    }

    [Fact]
    public void 존재하지_않는_thread면_경고와_빈_transcript를_돌려준다()
    {
        ConversationTranscript transcript = ConversationTranscriptBuilder.Build("no-such-thread", new Dictionary<string, ThreadChain>());

        Assert.Empty(transcript.Messages);
        Assert.NotEmpty(transcript.Warnings);
    }

    [Fact]
    public void 취소된_토큰이면_OperationCanceledException을_던진다()
    {
        RolloutFileReference file = WriteFile("t1", "t1.jsonl", null, DateTimeOffset.UnixEpoch,
            [EventMsgLine(0, "t1", "UserMessage", "u1", "메시지")]);
        var chains = new Dictionary<string, ThreadChain> { ["t1"] = MakeChain("t1", [file]) };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ConversationTranscriptBuilder.Build("t1", chains, cts.Token));
    }
}
