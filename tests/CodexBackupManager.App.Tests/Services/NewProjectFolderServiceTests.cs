using System;
using System.IO;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels.Import;
using Xunit;

namespace CodexBackupManager.App.Tests.Services;

/// <summary>
/// Phase 9_5-T6 — [새 폴더 만들기]의 폴더 이름 정리(순수 함수), 만들기(번호 붙이기, 재사용 금지), 빈 폴더만 지우기, 기준 폴더 거부 조건,
/// 설정 파일 호환. 모두 temp 폴더에서 한다.
/// </summary>
public sealed class NewProjectFolderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-new-folder-tests", Guid.NewGuid().ToString("N"));

    public NewProjectFolderServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true); // 테스트 temp 정리(제품 코드는 재귀 삭제를 쓰지 않는다)
        }
        catch (IOException)
        {
        }
    }

    // ── 이름 정리 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("탭\t제어\u0001문자\u007f", "탭_제어_문자_")]
    [InlineData("  한글 프로젝트 . . ", "한글 프로젝트")]
    [InlineData("끝 점...", "끝 점")]
    [InlineData("CON", "CON_")]
    [InlineData("com1", "com1_")]
    [InlineData("Lpt9", "Lpt9_")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("AUX.tar.gz", "AUX_.tar.gz")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("COM0", "COM0")]
    [InlineData("COM10", "COM10")]
    [InlineData("", NewProjectFolderService.EmptyNameFallback)]
    [InlineData("   ", NewProjectFolderService.EmptyNameFallback)]
    [InlineData(" . . ", NewProjectFolderService.EmptyNameFallback)]
    [InlineData(null, NewProjectFolderService.EmptyNameFallback)]
    [InlineData("???", "___")]
    public void 폴더_이름을_정리한다(string? input, string expected)
        => Assert.Equal(expected, NewProjectFolderService.SanitizeFolderName(input));

    [Fact]
    public void 긴_이름은_80자로_자르고_서로게이트_쌍을_가르지_않으며_끝_공백과_점을_다시_지운다()
    {
        Assert.Equal(new string('a', 80), NewProjectFolderService.SanitizeFolderName(new string('a', 100)));

        string emoji = char.ConvertFromUtf32(0x1F600); // 서로게이트 쌍(2 코드 단위)
        string surrogate = NewProjectFolderService.SanitizeFolderName(new string('a', 79) + emoji + "뒤");
        Assert.Equal(new string('a', 79), surrogate); // 80번째가 앞쪽 서로게이트라 그 앞에서 자른다
        Assert.False(char.IsHighSurrogate(surrogate[^1]));

        string full = NewProjectFolderService.SanitizeFolderName(new string('a', 78) + emoji + "뒤");
        Assert.Equal(new string('a', 78) + emoji, full); // 쌍이 경계 안에 다 들어가면 남긴다

        Assert.Equal(new string('a', 78), NewProjectFolderService.SanitizeFolderName(new string('a', 78) + " .bbbbbbbbbb"));
    }

    // ── 만들기 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 기준_폴더가_없으면_만들고_같은_이름이_있으면_번호를_붙이며_기존_폴더를_재사용하지_않는다()
    {
        string basePath = Path.Combine(_root, "base");

        NewProjectFolderResult first = NewProjectFolderService.Create(basePath, " 프로젝트:이름 ");
        Assert.True(first.Success);
        Assert.True(first.CreatedBase);
        Assert.Equal(Path.Combine(basePath, "프로젝트_이름"), first.FolderPath);

        NewProjectFolderResult second = NewProjectFolderService.Create(basePath, "프로젝트:이름");
        Assert.False(second.CreatedBase);
        Assert.Equal(Path.Combine(basePath, "프로젝트_이름 (2)"), second.FolderPath);

        File.WriteAllText(Path.Combine(basePath, "파일"), "x"); // 같은 이름의 "파일"도 피한다
        Assert.Equal(Path.Combine(basePath, "파일 (2)"), NewProjectFolderService.Create(basePath, "파일").FolderPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(first.FolderPath!));
    }

    [Fact]
    public void 번호_상한을_넘으면_실패한다()
    {
        string basePath = Path.Combine(_root, "full");
        Directory.CreateDirectory(Path.Combine(basePath, "Z"));
        for (int n = 2; n <= NewProjectFolderService.MaxSuffixNumber; n++)
        {
            Directory.CreateDirectory(Path.Combine(basePath, $"Z ({n})"));
        }

        NewProjectFolderResult result = NewProjectFolderService.Create(basePath, "Z");

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void 기준_폴더가_파일이면_경로_없는_사유로_실패한다()
    {
        string basePath = Path.Combine(_root, "is-file");
        File.WriteAllText(basePath, "x");

        NewProjectFolderResult result = NewProjectFolderService.Create(basePath, "A");

        Assert.False(result.Success);
        Assert.DoesNotContain(_root, result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── 빈 폴더만 지우기 ──────────────────────────────────────────────────────

    [Fact]
    public void 완전히_빈_폴더만_지운다()
    {
        string empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        string withFile = Directory.CreateDirectory(Path.Combine(_root, "with-file")).FullName;
        File.WriteAllText(Path.Combine(withFile, "a.txt"), "x");
        string withChild = Directory.CreateDirectory(Path.Combine(_root, "with-child")).FullName;
        Directory.CreateDirectory(Path.Combine(withChild, "sub"));

        Assert.True(NewProjectFolderService.TryDeleteIfEmpty(empty, out bool failed));
        Assert.False(failed);
        Assert.False(Directory.Exists(empty));
        Assert.False(NewProjectFolderService.TryDeleteIfEmpty(withFile, out _));
        Assert.True(File.Exists(Path.Combine(withFile, "a.txt")));
        Assert.False(NewProjectFolderService.TryDeleteIfEmpty(withChild, out _));
        Assert.True(Directory.Exists(Path.Combine(withChild, "sub")));
        Assert.False(NewProjectFolderService.TryDeleteIfEmpty(Path.Combine(_root, "missing"), out bool missingFailed));
        Assert.False(missingFailed);
    }

    // ── 기준 폴더 거부 조건 ────────────────────────────────────────────────────

    [Fact]
    public void 기준_폴더는_절대_경로이고_Codex_Home과_앱_데이터_폴더_밖이어야_한다()
    {
        string home = Path.Combine(_root, ".codex");
        string appData = Path.Combine(_root, "appdata");
        string snapshots = Path.Combine(_root, "snapshots");
        string[] protectedRoots = [appData, snapshots];

        Assert.Null(NewProjectFolderService.ValidateBase(Path.Combine(_root, "ChatGPT"), home, protectedRoots));
        Assert.Null(NewProjectFolderService.ValidateBase(Path.Combine(_root, ".codex-other"), home, protectedRoots)); // 이름만 비슷한 옆 폴더

        Assert.Contains("전체 경로", NewProjectFolderService.ValidateBase(@"relative\dir", home, protectedRoots));
        Assert.Contains("전체 경로", NewProjectFolderService.ValidateBase("", home, protectedRoots));
        Assert.Contains("Codex", NewProjectFolderService.ValidateBase(home, home, protectedRoots));
        Assert.Contains("Codex", NewProjectFolderService.ValidateBase(@"\\?\" + home.ToUpperInvariant() + @"\sessions\", home, protectedRoots));
        Assert.Contains("프로그램", NewProjectFolderService.ValidateBase(Path.Combine(appData, "x"), home, protectedRoots));
        Assert.Contains("프로그램", NewProjectFolderService.ValidateBase(snapshots, home, protectedRoots));
    }

    // ── 설정 호환 ──────────────────────────────────────────────────────────

    [Fact]
    public void 새_폴더_위치가_없는_이전_설정_파일도_읽히고_저장하면_다시_읽힌다()
    {
        string file = Path.Combine(_root, "settings.json");
        File.WriteAllText(file, "{\n  \"Version\": 1,\n  \"ManualCodexHomePath\": \"D:\\\\Codex\"\n}");
        var store = new SettingsStore(file);

        AppSettings old = store.Load();
        Assert.Equal(1, old.Version);
        Assert.Equal(@"D:\Codex", old.ManualCodexHomePath);
        Assert.Null(old.NewProjectFolderBase);

        NewProjectFolderOptions options = NewProjectFolderOptions.ForSettings(store, () => Path.Combine(_root, "snapshots"));
        Assert.Null(options.LoadSavedBase());
        Assert.True(options.SaveBase(@"E:\Work\ChatGPT"));

        AppSettings reloaded = new SettingsStore(file).Load();
        Assert.Equal(@"E:\Work\ChatGPT", reloaded.NewProjectFolderBase);
        Assert.Equal(@"D:\Codex", reloaded.ManualCodexHomePath); // 다른 설정은 그대로
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(@"E:\Work\ChatGPT", options.LoadSavedBase());
    }
}
