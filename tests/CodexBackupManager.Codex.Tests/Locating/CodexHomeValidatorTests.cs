using System.IO;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Locating;

/// <summary>
/// <see cref="CodexHomeValidator"/> 테스트.
/// </summary>
public sealed class CodexHomeValidatorTests
{
    private readonly CodexHomeValidator _validator = new();

    // ── 게이트 ───────────────────────────────────────────────────────

    [Fact]
    public void 존재하지_않는_폴더는_Invalid다()
    {
        string missing = Path.Combine(Path.GetTempPath(), "cbm-tests", "does-not-exist-" + Path.GetRandomFileName());

        CodexHomeValidation result = _validator.Validate(missing);

        Assert.Equal(CodexHomeStatus.Invalid, result.Status);
        Assert.False(result.IsUsable);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void 빈_폴더는_Invalid다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateEmpty();

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Invalid, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("sessions", System.StringComparison.Ordinal));
    }

    [Fact]
    public void sessions만_있는_폴더는_Invalid다()
    {
        // "sessions"라는 이름의 폴더는 Codex와 무관한 프로젝트에도 흔하다.
        // Codex를 식별하는 다른 신호가 하나도 없으면 오탐 위험이 더 크다.
        using FakeCodexHome home = FakeCodexHome.CreateEmpty().WithSessionsDirectory();

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Invalid, result.Status);
        Assert.Equal(0, result.Score);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void 폴더가_존재한다는_이유만으로는_Valid가_되지_않는다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateEmpty()
            .WithConfigToml()
            .WithSessionIndex()
            .WithGlobalState()
            .WithStateDatabaseFile("state_5.sqlite");
        // sessions\ 가 없다.

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Invalid, result.Status);
    }

    // ── 전체 구조 ────────────────────────────────────────────────────

    [Fact]
    public void 전체_구조가_있으면_Valid다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
        Assert.Equal(CodexHomeValidation.MaxScore, result.Score);
        Assert.True(result.IsUsable);
    }

    // ── state_*.sqlite 패턴 (하드코딩 금지 검증) ─────────────────────

    [Theory]
    [InlineData("state_4.sqlite", 4)]
    [InlineData("state_5.sqlite", 5)]
    [InlineData("state_6.sqlite", 6)]
    [InlineData("state_12.sqlite", 12)]
    [InlineData("state_99.sqlite", 99)]
    public void 어떤_generation의_state_DB라도_인식한다(string fileName, int expectedGeneration)
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete(fileName);

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
        Assert.Contains(fileName, result.StateDatabaseFileNames);
        Assert.Equal(expectedGeneration, CodexHomeLayout.TryParseGeneration(fileName));
    }

    [Fact]
    public void state_DB가_여러_개면_generation_내림차순으로_돌려준다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete("state_3.sqlite")
            .WithStateDatabaseFile("state_5.sqlite")
            .WithStateDatabaseFile("state_4.sqlite");

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(3, result.StateDatabaseFileNames.Count);
        Assert.Equal("state_5.sqlite", result.StateDatabaseFileNames[0]);
        Assert.Equal("state_4.sqlite", result.StateDatabaseFileNames[1]);
        Assert.Equal("state_3.sqlite", result.StateDatabaseFileNames[2]);
    }

    [Fact]
    public void state_DB의_wal_파일은_state_DB로_세지_않는다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete("state_5.sqlite")
            .WithStateDatabaseFile("state_5.sqlite-wal")
            .WithStateDatabaseFile("state_5.sqlite-shm");

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Single(result.StateDatabaseFileNames);
        Assert.Equal("state_5.sqlite", result.StateDatabaseFileNames[0]);
    }

    [Fact]
    public void generation을_파싱할_수_없는_state_파일도_신호로는_인정한다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete("state_next.sqlite");

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
        Assert.Contains("state_next.sqlite", result.StateDatabaseFileNames);
        Assert.Null(CodexHomeLayout.TryParseGeneration("state_next.sqlite"));
    }

    // ── Probable ─────────────────────────────────────────────────────

    [Fact]
    public void state_DB만_있으면_Probable이다()
    {
        // state_*.sqlite 가중치 2 → 점수 2 → Probable
        using FakeCodexHome home = FakeCodexHome.CreateEmpty()
            .WithSessionsDirectory()
            .WithStateDatabaseFile("state_5.sqlite");

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Probable, result.Status);
        Assert.Equal(2, result.Score);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public void state_DB_없이_보조_파일만_있으면_Probable이다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateEmpty()
            .WithSessionsDirectory()
            .WithSessionIndex()
            .WithConfigToml();

        CodexHomeValidation result = _validator.Validate(home.Path);

        Assert.Equal(CodexHomeStatus.Probable, result.Status);
        Assert.Equal(2, result.Score);
        Assert.Contains(result.Reasons, reason => reason.Contains("state_*.sqlite", System.StringComparison.Ordinal));
    }

    // ── 경로 표기 차이 ───────────────────────────────────────────────

    [Fact]
    public void 확장_길이_prefix가_붙은_경로도_검사할_수_있다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();

        CodexHomeValidation result = _validator.Validate(@"\\?\" + home.Path);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
    }

    [Fact]
    public void 끝에_구분자가_붙은_경로도_검사할_수_있다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete();

        CodexHomeValidation result = _validator.Validate(home.Path + Path.DirectorySeparatorChar);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
    }

    // ── 잘못된 입력 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\bad\path*")]
    public void 해석할_수_없는_경로는_Invalid다(string? path)
    {
        CodexHomeValidation result = _validator.Validate(path);

        Assert.Equal(CodexHomeStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Reasons);
    }

    // ── 커밋된 픽스처 ────────────────────────────────────────────────

    [Fact]
    public void 커밋된_픽스처는_Valid다()
    {
        CodexHomeValidation result = _validator.Validate(RepositoryFixtures.CodexHomeFixture);

        Assert.Equal(CodexHomeStatus.Valid, result.Status);
        Assert.Contains("state_4.sqlite", result.StateDatabaseFileNames);
    }
}
