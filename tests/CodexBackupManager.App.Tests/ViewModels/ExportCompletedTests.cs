using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_6-T1 — 내보내기 성공 직후 "내보내기 완료" 대화상자(작업 폴더 안내)와 목록 텍스트 저장. 모두 temp 폴더만 쓴다.
/// fixture: Alpha 프로젝트(루트 C:\Fixture\Projects\Alpha) 대화 1개, 기타 대화 2개(cwd Beta, Gamma).
/// </summary>
public sealed class ExportCompletedTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(MainViewModel Main, string BackupPath)> ExportAllAsync(Func<string, bool>? directoryExists = null, string? backupPath = null)
    {
        backupPath ??= Path.Combine(_h.TestDir, "out", $"codex-backup-test-{Guid.NewGuid():N}.codexbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        MainViewModel main = _h.CreateMainViewModel(_h.SourceHome, _ => backupPath);
        if (directoryExists is not null)
        {
            main.DirectoryExists = directoryExists;
        }

        await ConnectAsync(main);
        main.SelectAllConversationsCommand.Execute(null);
        main.ExportCommand.Execute(null);
        await WaitUntil(() => !main.IsExporting, "Export");
        return (main, backupPath);
    }

    [Fact]
    public async Task 성공하면_완료_대화상자를_한_번_띄우고_요약과_작업_폴더를_보여준다()
    {
        (MainViewModel main, string backupPath) = await ExportAllAsync(directoryExists: path => path.EndsWith("Alpha", StringComparison.Ordinal));

        ExportCompletedViewModel completed = Assert.Single(_h.ExportCompletedShown);
        Assert.False(main.IsExporting); // 대화상자는 내보내기를 끝낸 뒤에 뜬다
        Assert.StartsWith(Path.GetFileName(backupPath) + " (", completed.BackupFileText, StringComparison.Ordinal);
        Assert.Equal("대화 3개 · 프로젝트 1개 · 기타 대화 2개", completed.CountsText);
        Assert.Contains("작업 폴더의 파일(소스 코드, 문서, 에셋 등)은 들어 있지 않습니다", completed.Notice, StringComparison.Ordinal);

        Assert.Equal(["Alpha", "기타 대화"], completed.Groups.Select(g => g.Name));
        ExportFolderRow alpha = Assert.Single(completed.Groups[0].Folders);
        Assert.Equal(@"C:\Fixture\Projects\Alpha", alpha.Text); // \\?\ 없음, 있음 → 표시 없음
        Assert.Equal(
            [@"C:\Fixture\Projects\Beta (이 PC에 없음)", @"C:\Fixture\Projects\Gamma (이 PC에 없음)"],
            completed.Groups[1].Folders.Select(f => f.Text).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(2, completed.MissingFolderCount);
    }

    [Fact]
    public async Task 실패하거나_저장_위치를_고르지_않으면_대화상자를_띄우지_않는다()
    {
        // 실패: 대상 경로가 이미 폴더라 쓸 수 없다.
        string blocked = Path.Combine(_h.TestDir, "blocked.codexbackup");
        Directory.CreateDirectory(blocked);
        (MainViewModel failed, _) = await ExportAllAsync(backupPath: blocked);
        Assert.False(string.IsNullOrEmpty(failed.ExportStatusText));
        Assert.DoesNotContain("내보내기 완료", failed.ExportStatusText, StringComparison.Ordinal);

        // 취소: 저장 위치를 고르지 않았다.
        MainViewModel cancelled = _h.CreateMainViewModel(_h.SourceHome, _ => null);
        await ConnectAsync(cancelled);
        cancelled.SelectAllConversationsCommand.Execute(null);
        cancelled.ExportCommand.Execute(null);
        await WaitUntil(() => !cancelled.IsExporting, "Export");

        Assert.Empty(_h.ExportCompletedShown);
    }

    [Fact]
    public async Task 텍스트_저장은_백업과_같은_폴더_같은_이름이_기본이고_BOM과_CRLF로_쓴다()
    {
        (_, string backupPath) = await ExportAllAsync(directoryExists: _ => false);
        ExportCompletedViewModel completed = Assert.Single(_h.ExportCompletedShown);
        string? askedDirectory = null;
        string? askedName = null;
        string target = Path.Combine(Path.GetDirectoryName(backupPath)!, "목록.txt");
        var vm = new ExportCompletedViewModel(
            Backup.Summary.ExportSummaryBuilder.Build(ReadManifest(backupPath), Path.GetFileName(backupPath), new FileInfo(backupPath).Length),
            backupPath, _ => false,
            (dir, name) =>
            {
                askedDirectory = dir;
                askedName = name;
                return target;
            },
            TimeZoneInfo.Local, new CodexBackupManager.App.Services.FileLogger(Path.Combine(_h.TestDir, "logs")));

        Assert.Equal(Path.GetDirectoryName(backupPath), completed.DefaultTextFileDirectory);
        Assert.Equal(Path.GetFileNameWithoutExtension(backupPath) + ".txt", completed.DefaultTextFileName);

        vm.SaveTextCommand.Execute(null);

        Assert.Equal(Path.GetDirectoryName(backupPath), askedDirectory);
        Assert.Equal(Path.GetFileNameWithoutExtension(backupPath) + ".txt", askedName);
        Assert.Equal("저장했습니다: 목록.txt", vm.SaveStatusText);
        Assert.False(vm.IsSaveError);
        byte[] bytes = File.ReadAllBytes(target);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Codex Backup Manager — 내보내기 목록\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(@"  작업 폴더: C:\Fixture\Projects\Alpha", text, StringComparison.Ordinal);
        Assert.DoesNotContain("이 PC에 없음", text, StringComparison.Ordinal); // 텍스트에는 적지 않는다
        Assert.Equal(text, vm.BuildText());
    }

    [Fact]
    public async Task 저장에_실패하면_사유를_보여주고_취소하면_아무것도_하지_않는다()
    {
        await ExportAllAsync();
        ExportCompletedViewModel completed = Assert.Single(_h.ExportCompletedShown);
        // 대화상자는 MainViewModel의 선택기를 생성 시 받는다 → 실패·취소는 새 대화상자로 확인한다.
        string missingDir = Path.Combine(_h.TestDir, "no-such-dir", "x.txt");
        var failing = Recreate(completed, (_, _) => missingDir);
        failing.SaveTextCommand.Execute(null);
        Assert.Equal("저장하지 못했습니다(DirectoryNotFoundException). 다른 위치를 골라 다시 시도해 주세요.", failing.SaveStatusText);
        Assert.True(failing.IsSaveError);

        var cancelled = Recreate(completed, (_, _) => null);
        cancelled.SaveTextCommand.Execute(null);
        Assert.Null(cancelled.SaveStatusText);
    }

    [Fact]
    public async Task 로그에는_제목_경로_파일_이름이_없다()
    {
        (_, string backupPath) = await ExportAllAsync();
        ExportCompletedViewModel completed = Assert.Single(_h.ExportCompletedShown);
        string savePath = Path.Combine(_h.TestDir, "out", "로그확인-목록.txt");
        Recreate(completed, (_, _) => savePath).SaveTextCommand.Execute(null);
        Recreate(completed, (_, _) => Path.Combine(_h.TestDir, "missing-dir", "실패-목록.txt")).SaveTextCommand.Execute(null);

        string logs = string.Concat(Directory.EnumerateFiles(Path.Combine(_h.TestDir, "logs"), "*", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f, Encoding.UTF8)));
        Assert.Contains("내보내기 완료 안내", logs, StringComparison.Ordinal);
        Assert.Contains("내보내기 목록 텍스트 저장", logs, StringComparison.Ordinal);
        foreach (string secret in new[]
                 {
                     Path.GetFileNameWithoutExtension(backupPath), "로그확인-목록", "실패-목록", @"Fixture\Projects", "Fixture name", _h.TestDir,
                 })
        {
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Phase 9_U-09 (나) 경고 내용 ──────────────────────────────────────────────────

    [Fact]
    public async Task 경고가_없으면_경고_영역을_숨긴다()
    {
        await ExportAllAsync();
        ExportCompletedViewModel completed = Assert.Single(_h.ExportCompletedShown);

        Assert.False(completed.HasWarnings);
        Assert.Empty(completed.Warnings);
        Assert.DoesNotContain("[경고", completed.BuildText(), StringComparison.Ordinal);
    }

    [Fact]
    public void 경고가_있으면_사용자_말로_보여주고_텍스트에도_적지만_로그에는_개수만_남긴다()
    {
        Backup.Summary.ExportSummary summary = new Backup.Summary.ExportSummary("b.codexbackup", 10, DateTimeOffset.UtcNow, 1, [], null)
        {
            Warnings =
            [
                Backup.Summary.ExportSummaryBuilder.DescribeWarning("참조된 첨부 이미지 파일 2개를 찾을 수 없어 Export에서 제외했습니다."),
                "비밀표시-경고 원문",
            ],
        };
        string savePath = Path.Combine(_h.TestDir, "warn-list.txt");
        var vm = new ExportCompletedViewModel(
            summary, Path.Combine(_h.TestDir, "b.codexbackup"), _ => true, (_, _) => savePath, TimeZoneInfo.Utc,
            new CodexBackupManager.App.Services.FileLogger(Path.Combine(_h.TestDir, "logs")));

        Assert.True(vm.HasWarnings);
        Assert.Equal("경고 2건", vm.WarningsTitle);
        Assert.StartsWith("대화에 붙인 이미지 파일 2개를 이 PC에서 찾지 못해 백업에 넣지 못했습니다.", vm.Warnings[0], StringComparison.Ordinal);

        vm.SaveTextCommand.Execute(null);
        string text = File.ReadAllText(savePath, Encoding.UTF8);
        Assert.Contains("[경고 2건]", text, StringComparison.Ordinal);
        Assert.Contains("  - 비밀표시-경고 원문", text, StringComparison.Ordinal);

        string logs = string.Concat(Directory.EnumerateFiles(Path.Combine(_h.TestDir, "logs"), "*", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f, Encoding.UTF8)));
        Assert.DoesNotContain("비밀표시", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("이미지 파일", logs, StringComparison.Ordinal);
    }

    private ExportCompletedViewModel Recreate(ExportCompletedViewModel completed, Func<string, string, string?> picker)
    {
        string backup = Path.Combine(completed.DefaultTextFileDirectory, Path.GetFileNameWithoutExtension(completed.DefaultTextFileName) + ".codexbackup");
        return new ExportCompletedViewModel(
            Backup.Summary.ExportSummaryBuilder.Build(ReadManifest(backup), Path.GetFileName(backup), new FileInfo(backup).Length),
            backup, _ => true, picker, TimeZoneInfo.Local, new CodexBackupManager.App.Services.FileLogger(Path.Combine(_h.TestDir, "logs")));
    }

    private static Backup.Manifest.BackupManifest ReadManifest(string backupPath)
    {
        using var reader = Backup.Reading.BackupReader.Open(backupPath);
        return reader.ReadManifest();
    }
}
