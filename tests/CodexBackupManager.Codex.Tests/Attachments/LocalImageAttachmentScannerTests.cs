using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Codex.Attachments;
using CodexBackupManager.Domain.Codex.Rollout;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Attachments;

/// <summary>
/// Phase 05_01 hardening — <see cref="LocalImageAttachmentScanner"/>가
/// <see cref="Conversation.ConversationItemParser"/>와 같은 file-level authoritative-source
/// 정책(event_msg/item_completed가 있으면 그것만, 없을 때만 response_item)을 쓰는지 확인한다.
/// </summary>
public sealed class LocalImageAttachmentScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));

    public LocalImageAttachmentScannerTests() => Directory.CreateDirectory(_directory);

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

    private RolloutFileReference WriteFile(string fileName, IEnumerable<string> lines)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return new(path, fileName, "t1", null, DateTimeOffset.UnixEpoch, IsArchived: false, RolloutFileKind.PlainJsonl);
    }

    [Fact]
    public void event_msg의_local_image_참조를_찾는다()
    {
        RolloutFileReference file = WriteFile("a.jsonl",
        [
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
            "\"content\":[{\"type\":\"local_image\",\"path\":\"C:\\\\temp\\\\shot.png\"}]}}}",
        ]);

        IReadOnlyList<string> found = LocalImageAttachmentScanner.ScanFile(file);

        Assert.Equal(["C:\\temp\\shot.png"], found);
    }

    [Fact]
    public void event_msg가_있으면_같은_파일의_response_item은_무시한다()
    {
        RolloutFileReference file = WriteFile("a.jsonl",
        [
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}}",
            "{\"type\":\"response_item\",\"payload\":{\"role\":\"user\"," +
            "\"content\":[{\"type\":\"input_image\",\"image_url\":\"C:\\\\other\\\\ignored.png\"}]}}",
        ]);

        IReadOnlyList<string> found = LocalImageAttachmentScanner.ScanFile(file);

        Assert.Empty(found); // response_item은 이 파일에서 authoritative가 아니므로 무시된다.
    }

    [Fact]
    public void event_msg가_없는_파일은_response_item의_외부_경로_이미지를_찾는다()
    {
        RolloutFileReference file = WriteFile("a.jsonl",
        [
            "{\"type\":\"response_item\",\"payload\":{\"role\":\"user\"," +
            "\"content\":[{\"type\":\"input_image\",\"image_url\":\"C:\\\\legacy\\\\old.png\",\"detail\":\"auto\"}]}}",
        ]);

        IReadOnlyList<string> found = LocalImageAttachmentScanner.ScanFile(file);

        Assert.Equal(["C:\\legacy\\old.png"], found);
    }

    [Fact]
    public void response_item의_data_URI_이미지는_외부_파일이_아니므로_찾지_않는다()
    {
        RolloutFileReference file = WriteFile("a.jsonl",
        [
            "{\"type\":\"response_item\",\"payload\":{\"role\":\"user\"," +
            "\"content\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,AAAA\"}]}}",
        ]);

        IReadOnlyList<string> found = LocalImageAttachmentScanner.ScanFile(file);

        Assert.Empty(found); // 이미 rollout 바이트 안에 내용이 있다 — 별도 첨부가 아니다.
    }

    [Fact]
    public void response_item의_원격_URL_이미지는_로컬_파일이_아니므로_찾지_않는다()
    {
        RolloutFileReference file = WriteFile("a.jsonl",
        [
            "{\"type\":\"response_item\",\"payload\":{\"role\":\"user\"," +
            "\"content\":[{\"type\":\"input_image\",\"image_url\":\"https://example.com/x.png\"}]}}",
        ]);

        IReadOnlyList<string> found = LocalImageAttachmentScanner.ScanFile(file);

        Assert.Empty(found);
    }
}
