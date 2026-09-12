using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Codex.Revisions;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;
using ZstdSharp;

namespace CodexBackupManager.Codex.Tests.Revisions;

/// <summary>
/// Phase 6 — <see cref="ConversationRevisionBuilder"/>/<see cref="ConversationRevisionComparer"/>가
/// timestamp가 아니라 실제 rollout lineage + 논리적 바이트 내용으로 <see cref="RevisionRelation"/>을
/// 판정하는지 확인한다. "local"/"incoming"을 각각 별도 임시 디렉터리로 시뮬레이션한다(PC A/PC B 대신).
/// </summary>
public sealed class ConversationRevisionComparerTests : IDisposable
{
    private readonly string _localDir = Path.Combine(Path.GetTempPath(), "cbm-revision-tests", Guid.NewGuid().ToString("N"), "local");
    private readonly string _incomingDir = Path.Combine(Path.GetTempPath(), "cbm-revision-tests", Guid.NewGuid().ToString("N"), "incoming");

    public ConversationRevisionComparerTests()
    {
        Directory.CreateDirectory(_localDir);
        Directory.CreateDirectory(_incomingDir);
    }

    public void Dispose()
    {
        TryDelete(Path.GetDirectoryName(_localDir)!);
        TryDelete(Path.GetDirectoryName(_incomingDir)!);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Line(long ordinal, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
           "\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private static string WriteJsonl(string dir, string fileName, params string[] lines)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string WriteZst(string dir, string fileName, params string[] lines)
    {
        string path = Path.Combine(dir, fileName);
        string content = string.Join("\n", lines) + "\n";
        using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
        using CompressionStream compressionStream = new(fileStream, leaveOpen: true);
        using StreamWriter writer = new(compressionStream);
        writer.Write(content);
        return path;
    }

    private static RolloutFileReference Ref(string threadId, string? segmentId, string fullPath, RolloutFileKind kind = RolloutFileKind.PlainJsonl)
        => new(fullPath, Path.GetFileName(fullPath), threadId, segmentId, DateTimeOffset.UnixEpoch, IsArchived: false, kind);

    private static ThreadChain Chain(
        string threadId,
        IReadOnlyList<RolloutFileReference> files,
        string? parentThreadId = null,
        long? parentEndOrdinalExclusive = null,
        long? parentEndByteOffset = null,
        IReadOnlyList<HistoryBaseReference?>? fileHistoryBases = null)
        => new(threadId, files, fileHistoryBases ?? new HistoryBaseReference?[files.Count], parentThreadId, parentEndOrdinalExclusive, parentEndByteOffset, []);

    private static ConversationRevision BuildOrThrow(string threadId, IReadOnlyDictionary<string, ThreadChain> chains)
    {
        ConversationRevisionBuildResult result = ConversationRevisionBuilder.Build(threadId, chains, LocalFileRolloutSliceReader.Instance);
        Assert.NotNull(result.Revision);
        return result.Revision!;
    }

    // ── 단일 파일, 조상 없음 ─────────────────────────────────────────────────────────

    [Fact]
    public void 완전히_동일하면_Identical()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"));
        string incomingFile = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "hello"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingFile)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Identical, relation);
    }

    [Fact]
    public void 같은_파일이_incoming에서_이어써졌으면_IncomingAhead()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"));
        string incomingFile = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "world"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingFile)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.IncomingAhead, relation);
    }

    [Fact]
    public void 같은_파일이_local에서_이어써졌으면_LocalAhead()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "world"));
        string incomingFile = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "hello"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingFile)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.LocalAhead, relation);
    }

    [Fact]
    public void 공통_prefix_이후_길이가_다르게_각자_이어써지면_Diverged()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "local-branch-longer-text"));
        string incomingFile = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "inc"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingFile)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Diverged, relation);
    }

    [Fact]
    public void 공통_prefix_이후_길이가_같아도_내용이_다르면_Diverged()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "aaaa"));
        string incomingFile = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "bbbb"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingFile)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Diverged, relation);
    }

    // ── 세그먼트(같은 thread 안에서 파일이 늘어남) ───────────────────────────────────

    [Fact]
    public void incoming에_새_segment가_추가되면_IncomingAhead()
    {
        string localSeg1 = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "seg1"));
        string incomingSeg1 = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "seg1"));
        string incomingSeg2 = WriteJsonl(_incomingDir, "rollout-t_seg2.jsonl", Line(0, "seg2"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localSeg1)]) };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["t"] = Chain("t", [Ref("t", null, incomingSeg1), Ref("t", "seg2", incomingSeg2)]),
        };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.IncomingAhead, relation);
    }

    [Fact]
    public void local에_새_segment가_추가되면_LocalAhead()
    {
        string localSeg1 = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "seg1"));
        string localSeg2 = WriteJsonl(_localDir, "rollout-t_seg2.jsonl", Line(0, "seg2"));
        string incomingSeg1 = WriteJsonl(_incomingDir, "rollout-t.jsonl", Line(0, "seg1"));

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["t"] = Chain("t", [Ref("t", null, localSeg1), Ref("t", "seg2", localSeg2)]),
        };
        var incomingChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, incomingSeg1)]) };

        ConversationRevision local = BuildOrThrow("t", localChains);
        ConversationRevision incoming = BuildOrThrow("t", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.LocalAhead, relation);
    }

    // ── 조상(부모) 체인 + cutoff ─────────────────────────────────────────────────────

    [Fact]
    public void parent의_cutoff_이후가_달라도_child_revision은_Identical()
    {
        string localParent = WriteJsonl(_localDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "p1"), Line(2, "local-only-after-cutoff"));
        string incomingParent = WriteJsonl(_incomingDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "p1"), Line(2, "incoming-only-after-cutoff"));
        string localChild = WriteJsonl(_localDir, "rollout-child.jsonl", Line(0, "child"));
        string incomingChild = WriteJsonl(_incomingDir, "rollout-child.jsonl", Line(0, "child"));

        RolloutFileReference localParentRef = Ref("parent", null, localParent);
        RolloutFileReference incomingParentRef = Ref("parent", null, incomingParent);

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [localParentRef]),
            ["child"] = Chain("child", [Ref("child", null, localChild)], parentThreadId: localParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [incomingParentRef]),
            ["child"] = Chain("child", [Ref("child", null, incomingChild)], parentThreadId: incomingParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };

        ConversationRevision local = BuildOrThrow("child", localChains);
        ConversationRevision incoming = BuildOrThrow("child", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Identical, relation);
    }

    [Fact]
    public void ancestor의_cutoff_이전_바이트가_다르면_Diverged()
    {
        string localParent = WriteJsonl(_localDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "LOCAL-DIFFERENT"));
        string incomingParent = WriteJsonl(_incomingDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "INCOMING-DIFFERENT"));
        string localChild = WriteJsonl(_localDir, "rollout-child.jsonl", Line(0, "child"));
        string incomingChild = WriteJsonl(_incomingDir, "rollout-child.jsonl", Line(0, "child"));

        RolloutFileReference localParentRef = Ref("parent", null, localParent);
        RolloutFileReference incomingParentRef = Ref("parent", null, incomingParent);

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [localParentRef]),
            ["child"] = Chain("child", [Ref("child", null, localChild)], parentThreadId: localParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [incomingParentRef]),
            ["child"] = Chain("child", [Ref("child", null, incomingChild)], parentThreadId: incomingParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };

        ConversationRevision local = BuildOrThrow("child", localChains);
        ConversationRevision incoming = BuildOrThrow("child", incomingChains);

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            local, LocalFileRolloutSliceReader.Instance, incoming, LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Diverged, relation);
    }

    [Fact]
    public void end_ordinal_exclusive_경계는_정확히_그_줄부터_제외한다()
    {
        // cutoff=2 → ordinal 0,1은 포함, ordinal 2는 제외. ordinal 2에서만 서로 다르면 여전히 Identical이어야 한다.
        string localParent = WriteJsonl(_localDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "p1"), Line(2, "local-excluded"));
        string incomingParent = WriteJsonl(_incomingDir, "rollout-parent.jsonl", Line(0, "p0"), Line(1, "p1"), Line(2, "incoming-excluded"));
        string localChild = WriteJsonl(_localDir, "rollout-child.jsonl", Line(0, "child"));
        string incomingChild = WriteJsonl(_incomingDir, "rollout-child.jsonl", Line(0, "child"));

        RolloutFileReference localParentRef = Ref("parent", null, localParent);
        RolloutFileReference incomingParentRef = Ref("parent", null, incomingParent);

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [localParentRef]),
            ["child"] = Chain("child", [Ref("child", null, localChild)], parentThreadId: localParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["parent"] = Chain("parent", [incomingParentRef]),
            ["child"] = Chain("child", [Ref("child", null, incomingChild)], parentThreadId: incomingParentRef.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            BuildOrThrow("child", localChains), LocalFileRolloutSliceReader.Instance,
            BuildOrThrow("child", incomingChains), LocalFileRolloutSliceReader.Instance);
        Assert.Equal(RevisionRelation.Identical, relation);

        // 대조 실험: cutoff 안쪽(ordinal 1)이 다르면 진짜로 Diverged여야 한다 — 위 결과가 "아무거나 다 통과"가 아님을 증명.
        string localParent2 = WriteJsonl(_localDir, "rollout-parent2.jsonl", Line(0, "p0"), Line(1, "local-included"), Line(2, "excluded"));
        string incomingParent2 = WriteJsonl(_incomingDir, "rollout-parent2.jsonl", Line(0, "p0"), Line(1, "incoming-included"), Line(2, "excluded"));
        RolloutFileReference localParentRef2 = Ref("parent2", null, localParent2);
        RolloutFileReference incomingParentRef2 = Ref("parent2", null, incomingParent2);
        var localChains2 = new Dictionary<string, ThreadChain>
        {
            ["parent2"] = Chain("parent2", [localParentRef2]),
            ["child2"] = Chain("child2", [Ref("child2", null, localChild)], parentThreadId: localParentRef2.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };
        var incomingChains2 = new Dictionary<string, ThreadChain>
        {
            ["parent2"] = Chain("parent2", [incomingParentRef2]),
            ["child2"] = Chain("child2", [Ref("child2", null, incomingChild)], parentThreadId: incomingParentRef2.OwnRolloutId, parentEndOrdinalExclusive: 2),
        };
        RevisionRelation relation2 = ConversationRevisionComparer.Compare(
            BuildOrThrow("child2", localChains2), LocalFileRolloutSliceReader.Instance,
            BuildOrThrow("child2", incomingChains2), LocalFileRolloutSliceReader.Instance);
        Assert.Equal(RevisionRelation.Diverged, relation2);
    }

    [Fact]
    public void end_byte_offset_경계도_ordinal과_같은_원칙으로_동작한다()
    {
        // ordinal 필드 없이 byte offset만으로 자르는 경우. 첫 줄만 포함하도록 그 줄의 정확한
        // 바이트 길이(줄바꿈 포함)를 컷오프로 준다.
        string firstLine = "{\"no_ordinal_field\":true,\"n\":0}";
        string commonPrefixFile = Path.Combine(_localDir, "rollout-byteparent.jsonl");
        File.WriteAllText(commonPrefixFile, firstLine + "\n");
        long cutoff = new FileInfo(commonPrefixFile).Length;

        string localParent = WriteJsonl(_localDir, "rollout-byteparent.jsonl", firstLine, "{\"local\":\"tail\"}");
        string incomingParent = WriteJsonl(_incomingDir, "rollout-byteparent.jsonl", firstLine, "{\"incoming\":\"tail\"}");
        string localChild = WriteJsonl(_localDir, "rollout-bytechild.jsonl", Line(0, "child"));
        string incomingChild = WriteJsonl(_incomingDir, "rollout-bytechild.jsonl", Line(0, "child"));

        RolloutFileReference localParentRef = Ref("byteparent", null, localParent);
        RolloutFileReference incomingParentRef = Ref("byteparent", null, incomingParent);

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["byteparent"] = Chain("byteparent", [localParentRef]),
            ["bytechild"] = Chain(
                "bytechild", [Ref("bytechild", null, localChild)],
                parentThreadId: localParentRef.OwnRolloutId, parentEndByteOffset: cutoff),
        };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["byteparent"] = Chain("byteparent", [incomingParentRef]),
            ["bytechild"] = Chain(
                "bytechild", [Ref("bytechild", null, incomingChild)],
                parentThreadId: incomingParentRef.OwnRolloutId, parentEndByteOffset: cutoff),
        };

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            BuildOrThrow("bytechild", localChains), LocalFileRolloutSliceReader.Instance,
            BuildOrThrow("bytechild", incomingChains), LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Identical, relation);
    }

    // ── .jsonl vs .jsonl.zst ────────────────────────────────────────────────────────

    [Fact]
    public void jsonl과_동일_내용의_jsonl_zst는_Identical이다()
    {
        string localFile = WriteJsonl(_localDir, "rollout-t.jsonl", Line(0, "hello"), Line(1, "world"));
        string incomingFile = WriteZst(_incomingDir, "rollout-t.jsonl.zst", Line(0, "hello"), Line(1, "world"));

        var localChains = new Dictionary<string, ThreadChain> { ["t"] = Chain("t", [Ref("t", null, localFile)]) };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["t"] = Chain("t", [Ref("t", null, incomingFile, RolloutFileKind.ZstdCompressed)]),
        };

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            BuildOrThrow("t", localChains), LocalFileRolloutSliceReader.Instance,
            BuildOrThrow("t", incomingChains), LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Identical, relation);
    }

    // ── 같은 ThreadId, 서로 다른 lineage ────────────────────────────────────────────

    [Fact]
    public void 같은_ThreadId지만_조상_lineage_자체가_다르면_Diverged()
    {
        string localParent = WriteJsonl(_localDir, "rollout-parentA.jsonl", Line(0, "A"));
        string incomingParent = WriteJsonl(_incomingDir, "rollout-parentB.jsonl", Line(0, "B"));
        string localChild = WriteJsonl(_localDir, "rollout-child.jsonl", Line(0, "child"));
        string incomingChild = WriteJsonl(_incomingDir, "rollout-child.jsonl", Line(0, "child"));

        RolloutFileReference localParentRef = Ref("parentA", null, localParent);
        RolloutFileReference incomingParentRef = Ref("parentB", null, incomingParent);

        var localChains = new Dictionary<string, ThreadChain>
        {
            ["parentA"] = Chain("parentA", [localParentRef]),
            ["child"] = Chain("child", [Ref("child", null, localChild)], parentThreadId: localParentRef.OwnRolloutId),
        };
        var incomingChains = new Dictionary<string, ThreadChain>
        {
            ["parentB"] = Chain("parentB", [incomingParentRef]),
            ["child"] = Chain("child", [Ref("child", null, incomingChild)], parentThreadId: incomingParentRef.OwnRolloutId),
        };

        RevisionRelation relation = ConversationRevisionComparer.Compare(
            BuildOrThrow("child", localChains), LocalFileRolloutSliceReader.Instance,
            BuildOrThrow("child", incomingChains), LocalFileRolloutSliceReader.Instance);

        Assert.Equal(RevisionRelation.Diverged, relation);
    }

    // ── ConversationRevisionBuilder 자체의 Unverifiable 판정 ────────────────────────

    [Fact]
    public void 체인이_없으면_Unverifiable이다()
    {
        var chains = new Dictionary<string, ThreadChain>();
        ConversationRevisionBuildResult result = ConversationRevisionBuilder.Build("missing", chains, LocalFileRolloutSliceReader.Instance);

        Assert.Null(result.Revision);
        Assert.NotNull(result.UnverifiableReason);
    }

    [Fact]
    public void 순환_참조가_있으면_Unverifiable이다()
    {
        string fileA = WriteJsonl(_localDir, "rollout-a.jsonl", Line(0, "a"));
        string fileB = WriteJsonl(_localDir, "rollout-b.jsonl", Line(0, "b"));

        var chains = new Dictionary<string, ThreadChain>
        {
            ["a"] = Chain("a", [Ref("a", null, fileA)], parentThreadId: "b"),
            ["b"] = Chain("b", [Ref("b", null, fileB)], parentThreadId: "a"),
        };

        ConversationRevisionBuildResult result = ConversationRevisionBuilder.Build("a", chains, LocalFileRolloutSliceReader.Instance);

        Assert.Null(result.Revision);
        Assert.NotNull(result.UnverifiableReason);
    }

    [Fact]
    public void 조상_rollout_파일을_찾을_수_없으면_Unverifiable이다()
    {
        string childFile = WriteJsonl(_localDir, "rollout-child.jsonl", Line(0, "child"));
        var chains = new Dictionary<string, ThreadChain>
        {
            ["child"] = Chain("child", [Ref("child", null, childFile)], parentThreadId: "missing-parent"),
        };

        ConversationRevisionBuildResult result = ConversationRevisionBuilder.Build("child", chains, LocalFileRolloutSliceReader.Instance);

        Assert.Null(result.Revision);
        Assert.NotNull(result.UnverifiableReason);
    }
}
