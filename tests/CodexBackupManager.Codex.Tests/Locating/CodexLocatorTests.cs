using System.IO;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Locating;

/// <summary>
/// <see cref="CodexLocator"/> 테스트.
/// </summary>
/// <remarks>
/// 환경변수는 <see cref="FakeCodexEnvironment"/>로 대체하고, 폴더는 임시 디렉터리에 실제로 만든다.
/// 실제 프로세스 환경변수를 변경하지 않으므로 테스트 병렬 실행에도 안전하다.
/// </remarks>
public sealed class CodexLocatorTests
{
    private static CodexLocator CreateLocator(FakeCodexEnvironment environment)
        => new(environment, new CodexHomeValidator());

    // ── 1순위: CODEX_HOME ────────────────────────────────────────────

    [Fact]
    public void CODEX_HOME이_유효하면_1순위로_채택한다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();
        var environment = new FakeCodexEnvironment().WithCodexHome(home.Path);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.True(result.Found);
        Assert.Equal(CodexHomeSource.CodexHomeEnvironmentVariable, result.Source);
        Assert.NotNull(result.Home);
        Assert.True(CodexBackupManager.Domain.Paths.CanonicalPath.AreSameLocation(home.Path, result.HomeDisplayPath));
    }

    [Fact]
    public void CODEX_HOME이_USERPROFILE보다_우선한다()
    {
        using FakeCodexHome fromVariable = FakeCodexHome.CreateComplete();
        using FakeCodexHome profileRoot = FakeCodexHome.CreateEmpty();

        // %USERPROFILE%\.codex 도 유효하게 만들어 둔다.
        string profileCodex = Path.Combine(profileRoot.Path, CodexLocator.DefaultCodexDirectoryName);
        CreateCompleteAt(profileCodex);

        var environment = new FakeCodexEnvironment()
            .WithCodexHome(fromVariable.Path)
            .WithUserProfile(profileRoot.Path);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.Equal(CodexHomeSource.CodexHomeEnvironmentVariable, result.Source);
        Assert.True(CodexBackupManager.Domain.Paths.CanonicalPath.AreSameLocation(
            fromVariable.Path, result.HomeDisplayPath));
    }

    [Fact]
    public void CODEX_HOME이_유효하지_않으면_다음_후보로_넘어간다()
    {
        using FakeCodexHome invalid = FakeCodexHome.CreateEmpty();       // 빈 폴더
        using FakeCodexHome profileRoot = FakeCodexHome.CreateEmpty();
        string profileCodex = Path.Combine(profileRoot.Path, CodexLocator.DefaultCodexDirectoryName);
        CreateCompleteAt(profileCodex);

        var environment = new FakeCodexEnvironment()
            .WithCodexHome(invalid.Path)
            .WithUserProfile(profileRoot.Path);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.True(result.Found);
        Assert.Equal(CodexHomeSource.UserProfileDotCodex, result.Source);

        // 탈락한 1순위 후보의 사유가 기록되어야 한다.
        CodexHomeProbe first = Assert.Single(
            result.Probes,
            probe => probe.Source == CodexHomeSource.CodexHomeEnvironmentVariable);
        Assert.False(first.Skipped);
        Assert.NotNull(first.Validation);
        Assert.Equal(CodexHomeStatus.Invalid, first.Validation!.Status);
    }

    // ── 2순위: %USERPROFILE%\.codex ─────────────────────────────────

    [Fact]
    public void 환경변수가_없으면_USERPROFILE_아래_codex를_찾는다()
    {
        using FakeCodexHome profileRoot = FakeCodexHome.CreateEmpty();
        string profileCodex = Path.Combine(profileRoot.Path, CodexLocator.DefaultCodexDirectoryName);
        CreateCompleteAt(profileCodex);

        var environment = new FakeCodexEnvironment()
            .WithCodexHome(null)
            .WithUserProfile(profileRoot.Path);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.True(result.Found);
        Assert.Equal(CodexHomeSource.UserProfileDotCodex, result.Source);
        Assert.True(CodexBackupManager.Domain.Paths.CanonicalPath.AreSameLocation(
            profileCodex, result.HomeDisplayPath));

        CodexHomeProbe skipped = Assert.Single(
            result.Probes,
            probe => probe.Source == CodexHomeSource.CodexHomeEnvironmentVariable);
        Assert.True(skipped.Skipped);
    }

    [Fact]
    public void USERPROFILE을_확인할_수_없으면_건너뛴다()
    {
        var environment = new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.False(result.Found);
        CodexHomeProbe probe = Assert.Single(
            result.Probes,
            p => p.Source == CodexHomeSource.UserProfileDotCodex);
        Assert.True(probe.Skipped);
    }

    // ── 3순위: 저장된 수동 경로 ─────────────────────────────────────

    [Fact]
    public void 앞선_후보가_모두_실패하면_저장된_수동_경로를_쓴다()
    {
        using FakeCodexHome manual = FakeCodexHome.CreateComplete();
        var environment = new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null);

        CodexLocatorResult result = CreateLocator(environment).Locate(manual.Path);

        Assert.True(result.Found);
        Assert.Equal(CodexHomeSource.SavedManualPath, result.Source);
    }

    [Fact]
    public void 저장된_수동_경로가_없으면_건너뛴다()
    {
        var environment = new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null);

        CodexLocatorResult result = CreateLocator(environment).Locate(savedManualPath: null);

        Assert.False(result.Found);
        CodexHomeProbe probe = Assert.Single(
            result.Probes,
            p => p.Source == CodexHomeSource.SavedManualPath);
        Assert.True(probe.Skipped);
    }

    [Fact]
    public void 저장된_수동_경로가_유효하지_않으면_탐지에_실패한다()
    {
        using FakeCodexHome invalid = FakeCodexHome.CreateEmpty();
        var environment = new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null);

        CodexLocatorResult result = CreateLocator(environment).Locate(invalid.Path);

        Assert.False(result.Found);
        Assert.Null(result.Source);
        Assert.Equal(3, result.Probes.Count);
    }

    // ── 4순위: 사용자 직접 선택 ─────────────────────────────────────

    [Fact]
    public void 사용자가_고른_유효한_폴더를_채택한다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();
        var environment = new FakeCodexEnvironment();

        CodexLocatorResult result = CreateLocator(environment).EvaluateUserSelection(home.Path);

        Assert.True(result.Found);
        Assert.Equal(CodexHomeSource.UserSelected, result.Source);
    }

    [Fact]
    public void 사용자가_고른_잘못된_폴더는_사유와_함께_거부한다()
    {
        using FakeCodexHome invalid = FakeCodexHome.CreateEmpty();
        var environment = new FakeCodexEnvironment();

        CodexLocatorResult result = CreateLocator(environment).EvaluateUserSelection(invalid.Path);

        Assert.False(result.Found);
        CodexHomeProbe probe = Assert.Single(result.Probes);
        Assert.NotNull(probe.Validation);
        Assert.Equal(CodexHomeStatus.Invalid, probe.Validation!.Status);
        Assert.NotEmpty(probe.Validation.Reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 사용자가_아무것도_고르지_않으면_실패로_처리한다(string? selected)
    {
        CodexLocatorResult result = CreateLocator(new FakeCodexEnvironment()).EvaluateUserSelection(selected);

        Assert.False(result.Found);
        Assert.True(Assert.Single(result.Probes).Skipped);
    }

    // ── 경로 표기 ────────────────────────────────────────────────────

    [Fact]
    public void CODEX_HOME에_확장_길이_prefix가_있어도_동작한다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();
        var environment = new FakeCodexEnvironment().WithCodexHome(@"\\?\" + home.Path);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.True(result.Found);
        Assert.NotNull(result.Home);
        Assert.True(result.Home!.HadExtendedLengthPrefix);
        Assert.DoesNotContain(@"\\?\", result.HomeDisplayPath!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 모든_후보가_실패하면_후보별_사유가_남는다()
    {
        var environment = new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null);

        CodexLocatorResult result = CreateLocator(environment).Locate();

        Assert.False(result.Found);
        Assert.Equal(3, result.Probes.Count);
        Assert.All(result.Probes, probe => Assert.False(string.IsNullOrWhiteSpace(probe.Note)));
    }

    private static void CreateCompleteAt(string path)
    {
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "sessions"));
        Directory.CreateDirectory(Path.Combine(path, "archived_sessions"));
        File.WriteAllText(Path.Combine(path, "state_5.sqlite"), "placeholder");
        File.WriteAllText(Path.Combine(path, "session_index.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(path, "config.toml"), "model = \"x\"\n");
        File.WriteAllText(Path.Combine(path, ".codex-global-state.json"), "{}");
    }
}
