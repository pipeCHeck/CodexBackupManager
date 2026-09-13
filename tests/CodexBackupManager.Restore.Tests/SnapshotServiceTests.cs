using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 8 release-blocker C — <see cref="SnapshotService.Create"/>가 각 snapshot 파일과
/// <c>manifest.json</c>을 rollout append/durable journal과 같은 수준의 durability(스트리밍 복사 →
/// <c>Flush(flushToDisk: true)</c> → close → 재해시 검증, manifest는 temp+flush+atomic move로
/// publish)로 만드는지 확인한다. 대형 파일도 전체를 메모리에 올리지 않고 처리되는지 함께 확인한다.
/// 실제 사용자 <c>.codex</c>는 이 테스트에 전혀 관여하지 않는다.
/// </summary>
public sealed class SnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-snapshot-durability-tests", Guid.NewGuid().ToString("N"));

    public SnapshotServiceTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Snapshot은_대형_파일도_hash가_원본과_정확히_일치하게_만든다()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        string sourcePath = Path.Combine(sourceDir, "large-rollout.jsonl");

        // 20MB급 — 메모리 올림 없이 스트리밍 처리되는지(간접적으로) 확인할 정도의 크기.
        using (FileStream write = new(sourcePath, FileMode.Create, FileAccess.Write))
        {
            byte[] line = System.Text.Encoding.UTF8.GetBytes(
                "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{}}\n");
            for (int i = 0; i < 150_000; i++)
            {
                write.Write(line, 0, line.Length);
            }
        }

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));

        string snapshotRoot = Path.Combine(_root, "snapshots");
        SnapshotCreateResult result = SnapshotService.Create(
            snapshotRoot, sourceDir, "deadbeef", [("large-rollout", sourcePath)]);

        Assert.True(result.Success, result.FailureReason);
        SnapshotFileEntry entry = Assert.Single(result.Manifest!.Files);
        Assert.True(entry.ExistedBefore);
        Assert.Equal(sourceBytes.LongLength, entry.ByteLength);
        Assert.Equal(expectedSha256, entry.Sha256Hex);

        string snapshotFilePath = Path.Combine(result.SnapshotDirectory!, entry.SnapshotFileName);
        Assert.Equal(sourceBytes, File.ReadAllBytes(snapshotFilePath));
    }

    [Fact]
    public void manifest_json은_정상적으로_publish돼_다시_읽을_수_있다()
    {
        string sourceDir = Path.Combine(_root, "source2");
        Directory.CreateDirectory(sourceDir);
        string sourcePath = Path.Combine(sourceDir, "small.jsonl");
        File.WriteAllText(sourcePath, "{\"a\":1}\n");

        string snapshotRoot = Path.Combine(_root, "snapshots2");
        SnapshotCreateResult result = SnapshotService.Create(
            snapshotRoot, sourceDir, "deadbeef", [("small", sourcePath)]);

        Assert.True(result.Success, result.FailureReason);

        // manifest.json이 실제로 디스크에 있고(temp가 아니라), 다시 읽으면 같은 내용이어야 한다.
        string manifestPath = Path.Combine(result.SnapshotDirectory!, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(manifestPath + ".tmp"), "temp manifest 파일이 정리되지 않고 남아 있습니다.");

        SnapshotManifest? reRead = SnapshotService.ReadManifest(result.SnapshotDirectory!);
        Assert.NotNull(reRead);
        Assert.Equal(result.Manifest!.SnapshotId, reRead!.SnapshotId);
        Assert.Single(reRead.Files);
    }

    [Fact]
    public void 존재하지_않는_파일은_ExistedBefore가_false로_기록된다()
    {
        string sourceDir = Path.Combine(_root, "source3");
        Directory.CreateDirectory(sourceDir);
        string missingPath = Path.Combine(sourceDir, "does-not-exist.jsonl");

        string snapshotRoot = Path.Combine(_root, "snapshots3");
        SnapshotCreateResult result = SnapshotService.Create(
            snapshotRoot, sourceDir, "deadbeef", [("missing", missingPath)]);

        Assert.True(result.Success, result.FailureReason);
        SnapshotFileEntry entry = Assert.Single(result.Manifest!.Files);
        Assert.False(entry.ExistedBefore);
        Assert.Null(entry.Sha256Hex);
    }
}
