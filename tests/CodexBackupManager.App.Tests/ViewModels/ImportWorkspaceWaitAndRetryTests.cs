using System;
using System.IO;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_2-36(종료 대기 화면의 "확인했음" 표시), 9_2-37(Failed 화면의 [다시 분석]: 다시 시도하면 될 수 있는 실패만).
/// </summary>
public sealed class ImportWorkspaceWaitAndRetryTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ── 9_2-36 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task 대기_화면에서_다시_확인했는데_아직_실행_중이면_확인_시각을_보여준다()
    {
        string backup = await _h.ExportAsync();
        _h.CodexRunning = true;
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        ws.Now = () => new DateTime(2026, 10, 1, 14, 3, 7);
        await ws.OpenAsync();
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
        Assert.Null(ws.CodexRecheckText); // 처음 들어왔을 때는 안내만

        ws.RecheckCodexCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal("아직 Codex가 실행 중입니다(확인 14:03:07)", ws.CodexRecheckText);

        ws.Now = () => new DateTime(2026, 10, 1, 14, 3, 9);
        ws.ReanalyzeCommand.Execute(null); // 우측 상단 버튼도 같은 표시
        await Task.Delay(50);
        Assert.Equal("아직 Codex가 실행 중입니다(확인 14:03:09)", ws.CodexRecheckText);

        _h.CodexRunning = false;
        ws.RecheckCodexCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "분석");
        Assert.Null(ws.CodexRecheckText); // 대기 화면을 벗어나면 지운다
    }

    // ── 9_2-37 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task 분석_중_예외로_실패하면_다시_분석을_켜고_다시_분석하면_편집으로_간다()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        Func<string, System.Threading.CancellationToken, CodexBackupManager.Domain.Codex.Catalog.CodexCatalog> real = ws.CatalogBuilder;
        ws.CatalogBuilder = (_, _) => throw new IOException("잠김");

        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Failed, ws.State);
        Assert.True(ws.CanRetryAnalysis);
        Assert.True(ws.ReanalyzeCommand.CanExecute(null));
        Assert.True(ws.ChooseOtherFileCommand.CanExecute(null));

        ws.CatalogBuilder = real;
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "다시 분석");
        Assert.Equal(2, ws.AnalysisStartCount);
        Assert.False(ws.CanRetryAnalysis);
    }

    [Fact]
    public async Task Codex_Home을_확인할_수_없으면_다시_분석을_켜고_Home이_생기면_분석한다()
    {
        string backup = await _h.ExportAsync();
        string? home = null;
        var ws = new ImportWorkspaceViewModel(
            new FileLogger(Path.Combine(_h.TestDir, "logs")), () => backup, () => null, (_, _) => true,
            () => _h.SnapshotRoot, () => home, () => false, _h.TestNewProjectFolders());
        _h.Configure(ws);

        await ws.OpenAsync();
        Assert.Equal(ImportWorkspaceState.Failed, ws.State);
        Assert.True(ws.CanRetryAnalysis);
        Assert.True(ws.ReanalyzeCommand.CanExecute(null));

        home = _h.TargetHome;
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing, "다시 분석");
    }

    [Fact]
    public async Task 실패_뒤_다시_분석할_때_Codex가_켜져_있으면_종료_대기로_간다()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        ws.CatalogBuilder = (_, _) => throw new InvalidOperationException("일시적");
        await ws.OpenAsync();
        Assert.True(ws.CanRetryAnalysis);

        _h.CodexRunning = true;
        ws.ReanalyzeCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, ws.State);
    }

    [Theory]
    [InlineData("not a zip")]
    [InlineData("")]
    public async Task 백업_파일_자체의_문제면_다시_분석을_켜지_않는다(string content)
    {
        string broken = Path.Combine(_h.TestDir, $"broken-{Guid.NewGuid():N}.codexbackup");
        File.WriteAllText(broken, content);
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => broken);

        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Failed, ws.State);
        Assert.False(ws.CanRetryAnalysis);
        Assert.False(ws.ReanalyzeCommand.CanExecute(null));
        Assert.True(ws.ChooseOtherFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task 체크섬이_다른_백업도_다시_분석을_켜지_않는다()
    {
        string backup = await _h.ExportAsync();
        // ZIP 안 payload 1바이트를 바꿔 체크섬 불일치를 만든다(파일은 여전히 열리는 ZIP).
        using (var archive = System.IO.Compression.ZipFile.Open(backup, System.IO.Compression.ZipArchiveMode.Update))
        {
            System.IO.Compression.ZipArchiveEntry entry = System.Linq.Enumerable.First(archive.Entries, e => e.FullName.StartsWith("payload/", StringComparison.Ordinal));
            string name = entry.FullName;
            byte[] bytes;
            using (Stream s = entry.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                bytes = m.ToArray();
            }

            entry.Delete();
            bytes[^2] ^= 0x01;
            using Stream w = archive.CreateEntry(name).Open();
            w.Write(bytes);
        }

        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => backup);
        await ws.OpenAsync();

        Assert.Equal(ImportWorkspaceState.Failed, ws.State);
        Assert.False(ws.CanRetryAnalysis);
        Assert.False(ws.ReanalyzeCommand.CanExecute(null));
    }
}
