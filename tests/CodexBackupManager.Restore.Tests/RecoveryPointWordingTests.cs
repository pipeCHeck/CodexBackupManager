using System;
using System.IO;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 9_U-09 (다) — 사용자에게 보이는 Restore 층 메시지는 "Snapshot" 대신 "복구 지점"을 쓴다(뜻은 같다). temp 폴더만 쓴다.
/// </summary>
public sealed class RecoveryPointWordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-wording-tests", Guid.NewGuid().ToString("N"));

    public RecoveryPointWordingTests() => Directory.CreateDirectory(_root);

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
    public void 복구가_필요_없는_기록은_복구_지점이라고_말한다()
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "no-journal")).FullName;

        RestoreResult result = IncompleteApplyRecoveryService.Recover(dir, () => []);

        Assert.Equal(RestoreOutcome.NotReady, result.Outcome);
        Assert.Equal("이 복구 지점은 복구가 필요한 상태가 아닙니다(이미 완료됐거나 처리된 것으로 보입니다).", result.Message);
    }

    [Fact]
    public void 진행_기록이_손상되면_복구_지점_폴더를_확인하라고_말한다()
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "corrupt")).FullName;
        File.WriteAllText(Path.Combine(dir, "restore-transaction.json"), "{ 손상");

        RestoreResult result = IncompleteApplyRecoveryService.Recover(dir, () => []);

        Assert.Equal(RestoreOutcome.RollbackFailedCritical, result.Outcome);
        Assert.Equal("CRITICAL: 복원 진행 기록이 손상되어 있어 자동으로 복구할 수 없습니다. 복구 지점 폴더를 직접 확인해야 합니다.", result.Message);
    }

    [Fact]
    public void 복구_지점을_만들지_못하면_그_사유도_복구_지점이라고_말한다()
    {
        // 루트 자리에 파일이 있으면 디렉터리를 만들 수 없다.
        string fileAsRoot = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(fileAsRoot, "x");

        SnapshotCreateResult result = SnapshotService.Create(fileAsRoot, Path.Combine(_root, "home"), "marker", []);

        Assert.False(result.Success);
        Assert.StartsWith("복구 지점을 만드는 중 오류: ", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Snapshot", result.FailureReason, StringComparison.Ordinal);
    }
}
