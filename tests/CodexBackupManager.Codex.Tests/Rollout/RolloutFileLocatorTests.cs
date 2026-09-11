using System.IO;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex.Rollout;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Rollout;

public sealed class RolloutFileLocatorTests
{
    [Fact]
    public void 세그먼트_파일을_thread_ID로_인식한다()
    {
        using var home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();
        string day = Path.Combine(home.Path, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "rollout-2026-01-02T03-04-05-01a00000-0000-7000-8000-000000000001.jsonl"), "{}");
        File.WriteAllText(
            Path.Combine(day, "rollout-2026-01-02T04-00-00-01a00000-0000-7000-8000-000000000001_01a00000-0000-7000-8000-000000000099.jsonl"),
            "{}");

        var files = RolloutFileLocator.Locate(home.Path);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal("01a00000-0000-7000-8000-000000000001", f.ThreadId));
        Assert.Single(files, f => f.IsSegment);
        Assert.Single(files, f => !f.IsSegment);
    }

    [Fact]
    public void 압축_파일을_ZstdCompressed로_표시한다()
    {
        using var home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();
        string day = Path.Combine(home.Path, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "rollout-2026-01-02T03-04-05-01a00000-0000-7000-8000-000000000001.jsonl.zst"), "x");

        var files = RolloutFileLocator.Locate(home.Path);

        Assert.Single(files);
        Assert.Equal(RolloutFileKind.ZstdCompressed, files[0].Kind);
    }

    [Fact]
    public void 아카이브_폴더의_파일은_IsArchived가_참이다()
    {
        using var home = FakeCodexHome.CreateEmpty().WithArchivedSessionsDirectory();
        File.WriteAllText(
            Path.Combine(home.Path, "archived_sessions", "rollout-2026-01-02T03-04-05-01a00000-0000-7000-8000-000000000003.jsonl"),
            "{}");

        var files = RolloutFileLocator.Locate(home.Path);

        Assert.Single(files);
        Assert.True(files[0].IsArchived);
    }

    [Fact]
    public void 이름_규칙에_맞지_않는_파일은_무시한다()
    {
        using var home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();
        File.WriteAllText(Path.Combine(home.Path, "sessions", "not-a-rollout.jsonl"), "{}");

        var files = RolloutFileLocator.Locate(home.Path);

        Assert.Empty(files);
    }

    [Fact]
    public void 존재하지_않는_폴더는_빈_목록을_돌려준다()
    {
        var files = RolloutFileLocator.Locate(Path.Combine(Path.GetTempPath(), "cbm-tests-missing-" + System.Guid.NewGuid().ToString("N")));

        Assert.Empty(files);
    }
}
