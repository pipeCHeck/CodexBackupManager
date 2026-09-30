using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Codex.Conversation;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Sessions;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// Phase 9_2b-T1 — 같은 rollout으로 만든 transcript가 로컬 파일에서 읽든 백업 ZIP entry에서 읽든(<see cref="BackupRolloutContentSource"/>)
/// 같은지(메시지 수와 역할·phase·텍스트 해시). 일반 / 세그먼트 여러 개 / 분기(조상 포함) / <c>.jsonl.zst</c>.
/// 로컬 체인은 실제 탐색 경로(<see cref="RolloutFileLocator"/> → <see cref="CodexSessionParser"/> → <see cref="ThreadChainResolver"/>)로,
/// 백업 체인은 <see cref="BackupCatalogReader"/>로 만든다. 모두 합성 데이터다.
/// </summary>
public sealed class BackupRolloutContentSourceTests : IDisposable
{
    private const string Plain = "01e00000-0000-7000-8000-00000000000a";
    private const string Segmented = "01e00000-0000-7000-8000-00000000000b";
    private const string SegmentId = "01e00000-0000-7000-8000-0000000000b2";
    private const string Fork = "01e00000-0000-7000-8000-00000000000c";
    private const string Compressed = "01e00000-0000-7000-8000-00000000000d";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-content-source-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _day;

    public BackupRolloutContentSourceTests()
    {
        _home = Path.Combine(_root, "pcA");
        _day = Path.Combine(_home, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(_day);
        Directory.CreateDirectory(Path.Combine(_home, "archived_sessions"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Meta(string threadId, string? historyBaseRolloutId = null, long endOrdinal = 0)
    {
        string historyBase = historyBaseRolloutId is null
            ? string.Empty
            : ",\"history_base\":{\"thread_id\":\"" + historyBaseRolloutId + "\",\"end_ordinal_exclusive\":" + endOrdinal + ",\"end_byte_offset\":1}";
        return "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{\"id\":\"" + threadId +
               "\",\"session_id\":\"" + threadId + "\",\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"cwd\":\"C:\\\\Fixture\",\"model_provider\":\"openai\"" +
               historyBase + "}}";
    }

    private static string Message(long ordinal, bool user, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal + ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\"," +
           "\"item\":{\"type\":\"" + (user ? "UserMessage" : "AgentMessage") + "\",\"id\":\"m" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" +
           text + "\"}]" + (user ? string.Empty : ",\"phase\":\"final\"") + "}}}";

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private void Write(string fileName, string content) => File.WriteAllText(Path.Combine(_day, fileName), content);

    private void WriteZst(string fileName, string content)
    {
        using FileStream file = new(Path.Combine(_day, fileName), FileMode.Create, FileAccess.Write);
        using ZstdSharp.CompressionStream zst = new(file, leaveOpen: true);
        using StreamWriter writer = new(zst);
        writer.Write(content);
    }

    /// <summary>합성 PC A: 네 가지 모양의 대화를 만든다.</summary>
    private void WriteConversations()
    {
        Write($"rollout-2026-01-02T03-04-05-{Plain}.jsonl", Lines(
            Meta(Plain), Message(1, true, "평범한 질문 **굵게**"), Message(2, false, "답변 `코드`"), Message(3, true, "두 번째 질문"), Message(4, false, "두 번째 답변")));

        // 세그먼트 2개: 두 번째 파일이 자기 history_base로 첫 파일(자기 rollout ID = thread ID)을 ordinal 3 미만까지 이어받는다.
        Write($"rollout-2026-01-02T03-04-05-{Segmented}.jsonl", Lines(
            Meta(Segmented), Message(1, true, "세그먼트1 질문"), Message(2, false, "세그먼트1 답변"), Message(3, true, "잘려야 할 뒤쪽")));
        Write($"rollout-2026-01-02T03-05-05-{Segmented}_{SegmentId}.jsonl", Lines(
            Meta(Segmented, Segmented, 3), Message(4, true, "세그먼트2 질문"), Message(5, false, "세그먼트2 답변")));

        // 분기: Plain의 ordinal 3 미만까지 이어받는다.
        Write($"rollout-2026-01-02T03-06-05-{Fork}.jsonl", Lines(
            Meta(Fork, Plain, 3), Message(3, true, "분기 질문"), Message(4, false, "분기 답변")));

        WriteZst($"rollout-2026-01-02T03-07-05-{Compressed}.jsonl.zst", Lines(
            Meta(Compressed), Message(1, true, "압축 질문"), Message(2, false, "압축 답변 한글")));
    }

    private IReadOnlyDictionary<string, ThreadChain> LocalChains()
    {
        IReadOnlyList<RolloutFileReference> files = RolloutFileLocator.Locate(_home);
        var metadata = files.ToDictionary(f => f, f => CodexSessionParser.ParseSessionMetadata(f).Metadata);
        return ThreadChainResolver.Resolve(files, metadata);
    }

    private string Export(IReadOnlyDictionary<string, ThreadChain> chains, IReadOnlySet<string> selected)
    {
        List<ConversationEntry> entries = chains.Keys.Select(id => new ConversationEntry
        {
            ThreadId = id,
            Row = new ThreadRow { Id = id, ThreadSource = "user", ModelProvider = "openai" },
            Title = new ThreadTitle(id, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
        }).ToList();
        var catalog = new CodexCatalog(
            [new ProjectEntry(null, "기타 대화", [], entries)], entries, chains, [], DateTimeOffset.UtcNow,
            new CodexCatalogStats(chains.Count, entries.Count, 1, TimeSpan.Zero, TimeSpan.Zero));
        ExportPlan plan = ExportPlanBuilder.Build(catalog, selected);
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, null, null, DateTimeOffset.UtcNow);
        string path = Path.Combine(_root, "content.codexbackup");
        Assert.True(BackupWriter.Write(plan, manifest, path).Success);
        return path;
    }

    private static string Fingerprint(ConversationTranscript transcript)
    {
        string joined = string.Join("\u0001", transcript.Messages.Select(m => $"{m.Role}|{m.Phase}|{m.Text}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    [Fact]
    public void 로컬_파일과_백업_entry의_transcript가_같다_일반_세그먼트_분기_압축()
    {
        WriteConversations();
        IReadOnlyDictionary<string, ThreadChain> local = LocalChains();
        Assert.True(local[Segmented].IsSegmented);
        Assert.Equal(Plain, local[Fork].ParentThreadId);
        string backupPath = Export(local, new HashSet<string> { Plain, Segmented, Fork, Compressed });

        using BackupRolloutContentSource source = BackupRolloutContentSource.Open(backupPath);
        IReadOnlyDictionary<string, ThreadChain> fromBackup = source.ReadCatalog().Chains;
        Assert.All(fromBackup.Values.SelectMany(c => c.Files), f => Assert.False(Path.IsPathRooted(f.FullPath))); // ZIP entry 경로(로컬 파일이 아니다)

        foreach (string threadId in new[] { Plain, Segmented, Fork, Compressed })
        {
            ConversationTranscript expected = ConversationTranscriptBuilder.Build(threadId, local);
            ConversationTranscript actual = ConversationTranscriptBuilder.Build(threadId, fromBackup, source);

            Assert.NotEmpty(expected.Messages);
            Assert.Equal(expected.Messages.Count, actual.Messages.Count);
            Assert.Equal(Fingerprint(expected), Fingerprint(actual));
            Assert.Equal(expected.Messages.Select(m => m.Role), actual.Messages.Select(m => m.Role));
            Assert.Empty(actual.Warnings);
        }

        // 규칙이 실제로 적용됐는지(세그먼트 컷오프, 분기 조상 포함)도 확인한다.
        ConversationTranscript segmented = ConversationTranscriptBuilder.Build(Segmented, fromBackup, source);
        Assert.DoesNotContain(segmented.Messages, m => m.Text.Contains("잘려야 할 뒤쪽", StringComparison.Ordinal));
        Assert.Equal(4, segmented.Messages.Count);
        ConversationTranscript fork = ConversationTranscriptBuilder.Build(Fork, fromBackup, source);
        Assert.Equal(["평범한 질문 **굵게**", "답변 `코드`", "분기 질문", "분기 답변"], fork.Messages.Select(m => m.Text));
    }

    [Fact]
    public void 기존_오버로드는_로컬_source와_같은_결과다()
    {
        WriteConversations();
        IReadOnlyDictionary<string, ThreadChain> local = LocalChains();

        foreach (string threadId in local.Keys)
        {
            Assert.Equal(
                Fingerprint(ConversationTranscriptBuilder.Build(threadId, local)),
                Fingerprint(ConversationTranscriptBuilder.Build(threadId, local, LocalFileRolloutContentSource.Instance)));
        }
    }

    [Fact]
    public void 백업에_없는_조상이면_일부만_보이고_경고가_남는다()
    {
        WriteConversations();
        IReadOnlyDictionary<string, ThreadChain> local = LocalChains();
        string backupPath = Export(local, new HashSet<string> { Fork });

        using BackupRolloutContentSource source = BackupRolloutContentSource.Open(backupPath);
        var chains = source.ReadCatalog().Chains.Where(p => p.Key != Plain).ToDictionary(p => p.Key, p => p.Value);

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build(Fork, chains, source);

        Assert.Equal(["분기 질문", "분기 답변"], transcript.Messages.Select(m => m.Text));
        Assert.NotEmpty(transcript.Warnings);
    }

    [Fact]
    public void 미리보기_source가_열려_있어도_백업을_다시_열고_해시할_수_있다()
    {
        WriteConversations();
        string backupPath = Export(LocalChains(), new HashSet<string> { Plain });

        using BackupRolloutContentSource source = BackupRolloutContentSource.Open(backupPath);
        _ = source.ReadCatalog();

        // Plan 생성(BackupIdentityHasher)·Apply(PinnedBackupSource)와 같은 읽기 열기가 막히지 않는다.
        using (CodexBackupManager.Backup.Reading.BackupReader.Open(backupPath))
        {
        }

        using FileStream again = new(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(again.Length > 0);
    }
}
