using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 8 release-blocker A — New rollout(<see cref="RolloutRestoreService.CreateNewFile"/>)/append
/// (<see cref="RolloutRestoreService.AppendToFile"/>)가 쓰는 <c>&lt;target&gt;.cbm-restore-tmp</c>
/// 잔재를 <see cref="RollbackService.Rollback"/>이 Snapshot manifest가 실제로 아는 rollout target
/// 에서만 정확히 파생해 정리하는지 확인한다. 임의 glob 삭제가 아니라는 것도 함께 확인한다(예:
/// state-db 라벨 옆의 동일 이름 파일은 건드리지 않는다). 실제 사용자 <c>.codex</c>는 이 테스트
/// 전체에서 전혀 관여하지 않는다.
/// </summary>
public sealed class RollbackStaleTempCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-stale-temp-cleanup-tests", Guid.NewGuid().ToString("N"));

    public RollbackStaleTempCleanupTests() => Directory.CreateDirectory(_root);

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

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void Rollback은_New_rollout이_남긴_stale_temp를_지운다()
    {
        // Apply가 New rollout 생성 도중(target은 아직 없음) 크래시로 죽었다고 가정한다 — target은
        // 없고 ".cbm-restore-tmp"만 남아 있다.
        string targetDir = Path.Combine(_root, "new-rollout-case");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "rollout-newthread.jsonl");
        string staleTemp = target + ".cbm-restore-tmp";
        File.WriteAllText(staleTemp, "{\"partial\":\"crash left this behind\"}");

        var manifest = new SnapshotManifest(
            "test-snapshot", DateTimeOffset.UtcNow, targetDir, "deadbeef",
            [new SnapshotFileEntry("new-rollout-0", target, "0001.bin", ExistedBefore: false, 0, null)]);

        string snapshotDir = Path.Combine(_root, "snapshot-new-rollout");
        Directory.CreateDirectory(snapshotDir);

        RollbackService.Result result = RollbackService.Rollback(manifest, snapshotDir);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(File.Exists(target), "New rollout target은 여전히 없어야 합니다.");
        Assert.False(File.Exists(staleTemp), "stale temp가 정리되지 않았습니다.");
    }

    [Fact]
    public void Rollback은_append가_남긴_stale_temp를_지우고_원본_target은_그대로_복원한다()
    {
        // append가 target을 원본 그대로 둔 채(atomic move 전) 죽었다고 가정한다 — target은 원본
        // 그대로지만, 그 사이 이 Apply가 mutate했을 수 있으므로 snapshot 복사본으로 복원도 확인한다.
        string targetDir = Path.Combine(_root, "append-case");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "rollout-existingthread.jsonl");
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes("{\"session_meta\":true}\n{\"line\":1}\n");
        File.WriteAllBytes(target, originalBytes);

        string staleTemp = target + ".cbm-restore-tmp";
        File.WriteAllText(staleTemp, "original + incoming delta (partial, crashed before move)");

        string snapshotDir = Path.Combine(_root, "snapshot-append");
        Directory.CreateDirectory(snapshotDir);
        File.WriteAllBytes(Path.Combine(snapshotDir, "0001.bin"), originalBytes);

        var manifest = new SnapshotManifest(
            "test-snapshot", DateTimeOffset.UtcNow, targetDir, "deadbeef",
            [new SnapshotFileEntry("appended-rollout-0", target, "0001.bin", ExistedBefore: true, originalBytes.LongLength, Sha256Of(originalBytes))]);

        // Apply가 SQLite 쪽은 이미 mutate했을 수 있어도 rollout target 자체는 여기선 아직 원본 그대로다 —
        // Rollback이 그래도 정상적으로 "복원"(이미 같은 내용이지만 절차상 다시 씀)하고 temp도 지우는지 확인한다.
        RollbackService.Result result = RollbackService.Rollback(manifest, snapshotDir);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(originalBytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(staleTemp), "stale temp가 정리되지 않았습니다.");
    }

    [Fact]
    public void Rollback은_rollout이_아닌_라벨_옆의_동일_이름_파일은_임의로_지우지_않는다()
    {
        // "정확히 파생되는 temp만 지운다" — rollout이 아닌 라벨의 target 옆에 우연히 같은 이름의
        // 파일이 있어도 건드리면 안 된다(이런 라벨은 애초에 그런 temp를 만들지 않지만, 방어적으로
        // "라벨 기반 필터"가 실제로 동작하는지 직접 확인한다). "state-db"라는 실제 라벨을 쓰면
        // RollbackService의 state DB integrity 재확인 분기까지 타므로, 그 분기와 무관한 임의
        // 라벨을 쓴다.
        string targetDir = Path.Combine(_root, "unrelated-label-case");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "some-other-file.bin");
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes("unrelated-bytes");
        File.WriteAllBytes(target, originalBytes);

        string unrelatedFile = target + ".cbm-restore-tmp";
        File.WriteAllText(unrelatedFile, "should not be touched — not a rollout label");

        string snapshotDir = Path.Combine(_root, "snapshot-unrelated-label");
        Directory.CreateDirectory(snapshotDir);
        File.WriteAllBytes(Path.Combine(snapshotDir, "0001.bin"), originalBytes);

        var manifest = new SnapshotManifest(
            "test-snapshot", DateTimeOffset.UtcNow, targetDir, "deadbeef",
            [new SnapshotFileEntry("some-unrelated-label", target, "0001.bin", ExistedBefore: true, originalBytes.LongLength, Sha256Of(originalBytes))]);

        RollbackService.Result result = RollbackService.Rollback(manifest, snapshotDir);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(File.Exists(unrelatedFile), "rollout 라벨이 아닌 entry에서 파생된 파일을 실수로 지우면 안 됩니다.");
    }
}
