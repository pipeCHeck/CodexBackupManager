using System;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;
using Xunit;
using ZstdSharp;

namespace CodexBackupManager.Codex.Tests.Rollout;

public sealed class RolloutStreamReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));

    public RolloutStreamReaderTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void 일반_jsonl을_줄_단위로_읽는다()
    {
        string path = Path.Combine(_directory, "a.jsonl");
        File.WriteAllText(path, "line1\nline2\nline3\n");

        string[] lines = RolloutStreamReader.ReadLines(path, RolloutFileKind.PlainJsonl).ToArray();

        Assert.Equal(["line1", "line2", "line3"], lines);
    }

    [Fact]
    public void ReadFirstLines는_지정한_줄만_읽고_멈춘다()
    {
        string path = Path.Combine(_directory, "a.jsonl");
        File.WriteAllText(path, "line1\nline2\nline3\nline4\n");

        string[] lines = RolloutStreamReader.ReadFirstLines(path, RolloutFileKind.PlainJsonl, 2).ToArray();

        Assert.Equal(["line1", "line2"], lines);
    }

    [Fact]
    public void 직접_압축한_zst_fixture를_원문과_동일하게_복원한다()
    {
        // 실제 Codex .zst 실물은 이 PC에 없다(docs/codex-storage-format.md §9 미확정 항목).
        // 그래서 작은 JSONL을 우리가 직접 Zstandard로 압축해 왕복 검증한다.
        string[] originalLines =
        [
            """{"timestamp":"2026-01-02T03:04:05.000Z","ordinal":0,"type":"session_meta","payload":{"session_id":"01a00000-0000-7000-8000-000000000001"}}""",
            """{"timestamp":"2026-01-02T03:04:06.000Z","ordinal":1,"type":"event_msg","payload":{"type":"item_completed"}}""",
        ];
        string original = string.Join("\n", originalLines) + "\n";

        string path = Path.Combine(_directory, "a.jsonl.zst");
        using (FileStream fileStream = new(path, FileMode.Create, FileAccess.Write))
        using (CompressionStream compressionStream = new(fileStream, leaveOpen: true))
        using (StreamWriter writer = new(compressionStream))
        {
            writer.Write(original);
        }

        string[] decoded = RolloutStreamReader.ReadLines(path, RolloutFileKind.ZstdCompressed).ToArray();

        Assert.Equal(originalLines, decoded);
    }
}
