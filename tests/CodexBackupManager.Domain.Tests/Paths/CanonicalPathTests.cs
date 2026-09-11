using System.Text;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Domain.Tests.Paths;

/// <summary>
/// <see cref="CanonicalPath"/> 테스트. 파일 시스템에 접근하지 않는 순수 문자열 테스트다.
/// </summary>
public sealed class CanonicalPathTests
{
    // Phase 0에서 실제로 관측한 세 가지 표기.
    private const string FromSessionMeta = @"C:\_UserProjects\Unreal\Balhwajeom_Project";
    private const string FromStateDb = @"\\?\C:\_UserProjects\Unreal\Balhwajeom_Project";
    private const string FromConfigToml = @"c:\_userprojects\unreal\balhwajeom_project";

    // ── 요구사항의 핵심 시나리오 ─────────────────────────────────────

    [Fact]
    public void 실측_3종_표기는_같은_경로로_판정된다()
    {
        CanonicalPath a = CanonicalPath.Create(FromSessionMeta);
        CanonicalPath b = CanonicalPath.Create(FromStateDb);
        CanonicalPath c = CanonicalPath.Create(FromConfigToml);

        Assert.Equal(a, b);
        Assert.Equal(b, c);
        Assert.Equal(a.Value, c.Value);
        Assert.Equal(@"c:\_userprojects\unreal\balhwajeom_project", a.Value);
    }

    [Fact]
    public void 원본_문자열은_변형되지_않고_보존된다()
    {
        CanonicalPath path = CanonicalPath.Create(FromStateDb);

        Assert.Equal(FromStateDb, path.Original);
        Assert.True(path.HadExtendedLengthPrefix);
    }

    [Fact]
    public void Display는_대소문자를_보존하고_Value만_소문자다()
    {
        CanonicalPath path = CanonicalPath.Create(FromSessionMeta);

        Assert.Equal(FromSessionMeta, path.Display);
        Assert.Equal(FromSessionMeta.ToLowerInvariant(), path.Value);
    }

    // ── 일반 Windows 경로 ────────────────────────────────────────────

    [Fact]
    public void 일반_드라이브_경로를_해석한다()
    {
        CanonicalPath path = CanonicalPath.Create(@"D:\_User Project\s2\05_Unity\260702_First");

        Assert.Equal(PathRootKind.DriveRooted, path.RootKind);
        Assert.True(path.IsAbsolute);
        Assert.False(path.HadExtendedLengthPrefix);
        Assert.Equal(4, path.Segments.Count);
        Assert.Equal("260702_First", path.Segments[3]);
    }

    // ── 대소문자 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\User\.codex", @"c:\USERS\user\.CODEX")]
    [InlineData(@"C:\A", @"c:\a")]
    public void 대소문자_차이는_무시된다(string left, string right)
        => Assert.True(CanonicalPath.AreSameLocation(left, right));

    // ── 확장 길이 prefix ─────────────────────────────────────────────

    [Fact]
    public void 확장_길이_prefix를_제거한다()
    {
        CanonicalPath path = CanonicalPath.Create(@"\\?\C:\dir\sub");

        Assert.Equal(@"C:\dir\sub", path.Display);
        Assert.Equal(PathRootKind.DriveRooted, path.RootKind);
        Assert.True(path.HadExtendedLengthPrefix);
    }

    // ── UNC ──────────────────────────────────────────────────────────

    [Fact]
    public void 확장_UNC_prefix를_일반_UNC로_변환한다()
    {
        CanonicalPath path = CanonicalPath.Create(@"\\?\UNC\server\share\dir");

        Assert.Equal(@"\\server\share\dir", path.Display);
        Assert.Equal(PathRootKind.Unc, path.RootKind);
        Assert.True(path.IsAbsolute);
        Assert.True(path.HadExtendedLengthPrefix);
    }

    [Fact]
    public void 확장_UNC_prefix는_대소문자를_가리지_않는다()
        => Assert.True(CanonicalPath.AreSameLocation(@"\\?\unc\SERVER\Share\Dir", @"\\server\share\dir"));

    [Fact]
    public void 서버_이름_없는_UNC는_거부된다()
    {
        Assert.False(CanonicalPath.TryCreate(@"\\", out CanonicalPath? result, out string? error));
        Assert.Null(result);
        Assert.NotNull(error);
    }

    // ── trailing separator ───────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a\b")]
    [InlineData(@"C:\a\b\\", @"C:\a\b")]
    [InlineData(@"C:\a\\\b", @"C:\a\b")]
    public void trailing_및_중복_구분자를_정리한다(string input, string expectedDisplay)
        => Assert.Equal(expectedDisplay, CanonicalPath.Create(input).Display);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    public void 드라이브_루트는_역슬래시를_유지한다(string input)
    {
        CanonicalPath path = CanonicalPath.Create(input);

        Assert.Equal(@"C:\", path.Display);
        Assert.Equal(PathRootKind.DriveRooted, path.RootKind);
        Assert.Empty(path.Segments);
    }

    // ── / separator ──────────────────────────────────────────────────

    [Theory]
    [InlineData("C:/a/b", @"C:\a\b")]
    [InlineData("C:/a//b///c", @"C:\a\b\c")]
    [InlineData("//server/share/x", @"\\server\share\x")]
    public void 슬래시를_역슬래시로_정규화한다(string input, string expectedDisplay)
        => Assert.Equal(expectedDisplay, CanonicalPath.Create(input).Display);

    // ── 한글 NFC / NFD ───────────────────────────────────────────────

    [Fact]
    public void 한글_NFC와_NFD는_같은_경로로_판정된다()
    {
        const string raw = @"C:\Users\User\Documents\기획";
        string nfc = raw.Normalize(NormalizationForm.FormC);
        string nfd = raw.Normalize(NormalizationForm.FormD);

        // 테스트 전제: 두 문자열의 바이트열이 실제로 다르다.
        Assert.NotEqual(nfc, nfd);

        Assert.True(CanonicalPath.AreSameLocation(nfc, nfd));
        Assert.Equal(nfc, CanonicalPath.Create(nfd).Display);
    }

    [Theory]
    [InlineData(@"C:\Users\User\Documents\펫 만들기")]
    [InlineData(@"C:\Users\User\Documents\미디어 분석")]
    [InlineData(@"C:\Users\User\Documents\ChatGPT\ㅁㄴㄻㄴㄹ")]
    [InlineData(@"D:\8P_jinwoo\세이더 프로그래밍\(Unity) EvadingEnemyBullet")]
    public void 한글_경로를_정상_처리한다(string input)
    {
        CanonicalPath path = CanonicalPath.Create(input);

        Assert.Equal(input, path.Display);
        Assert.Equal(input.ToLowerInvariant(), path.Value);
    }

    // ── . 과 .. ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\a\.\b", @"C:\a\b")]
    [InlineData(@"C:\a\b\..\c", @"C:\a\c")]
    [InlineData(@"C:\..\a", @"C:\a")]
    public void 점과_상위_참조를_어휘적으로_해소한다(string input, string expectedDisplay)
        => Assert.Equal(expectedDisplay, CanonicalPath.Create(input).Display);

    [Fact]
    public void 상대_경로의_상위_참조는_유지된다()
    {
        CanonicalPath path = CanonicalPath.Create(@"sub\..\..\x");

        Assert.Equal(@"..\x", path.Display);
        Assert.Equal(PathRootKind.Relative, path.RootKind);
    }

    // ── 루트 종류 ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"sub\dir", PathRootKind.Relative, false)]
    [InlineData(@"C:sub", PathRootKind.DriveRelative, false)]
    [InlineData(@"\Windows", PathRootKind.RootedWithoutDrive, false)]
    [InlineData(@"\\.\pipe\codex", PathRootKind.Device, false)]
    [InlineData(@"C:\dir", PathRootKind.DriveRooted, true)]
    [InlineData(@"\\server\share", PathRootKind.Unc, true)]
    public void 루트_종류와_절대경로_여부를_판별한다(string input, PathRootKind expectedKind, bool expectedAbsolute)
    {
        CanonicalPath path = CanonicalPath.Create(input);

        Assert.Equal(expectedKind, path.RootKind);
        Assert.Equal(expectedAbsolute, path.IsAbsolute);
    }

    // ── 잘못된 입력 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(@"C:\a\b:c")]
    [InlineData(@"C:\a\b*")]
    [InlineData(@"C:\a\b?")]
    [InlineData("C:\\a\\\"b\"")]
    [InlineData(@"C:\a\b|c")]
    [InlineData(@"C:\a\b ")]
    [InlineData(@"C:\a\b.")]
    [InlineData("C:\\a\\b\u0001")]
    [InlineData(@"\\?\")]
    public void 잘못된_입력은_거부되고_사유를_돌려준다(string? input)
    {
        Assert.False(CanonicalPath.TryCreate(input, out CanonicalPath? result, out string? error));
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Create는_잘못된_입력에_예외를_던진다()
        => Assert.Throws<System.ArgumentException>(() => CanonicalPath.Create(""));

    [Fact]
    public void AreSameLocation은_잘못된_입력에_false를_돌려준다()
    {
        Assert.False(CanonicalPath.AreSameLocation(null, @"C:\a"));
        Assert.False(CanonicalPath.AreSameLocation(@"C:\a", ""));
    }

    // ── 컬렉션 키로 사용 ─────────────────────────────────────────────

    [Fact]
    public void 동일_경로는_해시_집합에서_하나로_모인다()
    {
        var set = new System.Collections.Generic.HashSet<CanonicalPath>(CanonicalPath.Comparer)
        {
            CanonicalPath.Create(FromSessionMeta),
            CanonicalPath.Create(FromStateDb),
            CanonicalPath.Create(FromConfigToml),
        };

        Assert.Single(set);
    }
}
