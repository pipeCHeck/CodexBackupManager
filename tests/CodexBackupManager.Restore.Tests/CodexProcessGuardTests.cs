using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 07_01 — <see cref="CodexProcessGuard"/>의 이름/경로 매칭과, 실제 OS 프로세스를 다룰 때
/// Dispose 순서가 안전한지 확인한다.
/// </summary>
public sealed class CodexProcessGuardTests
{
    [Fact]
    public void 이름이_Codex인_프로세스는_실행_중으로_판정한다()
    {
        CodexProcessGuard.Result result = CodexProcessGuard.Check(
            () => [new RunningProcessInfo("Codex", @"C:\Program Files\SomethingElse\app.exe")]);

        Assert.True(result.IsRunning);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void 이름은_다르지만_경로에_OpenAI_Codex가_있으면_실행_중으로_판정한다()
    {
        CodexProcessGuard.Result result = CodexProcessGuard.Check(
            () => [new RunningProcessInfo("helper-renderer", @"C:\Users\User\AppData\Local\OpenAI\Codex\bin\abcd1234\codex.exe")]);

        Assert.True(result.IsRunning);
        Assert.Contains("helper-renderer", result.MatchedProcessNames);
    }

    [Fact]
    public void 대소문자가_달라도_이름_경로_모두_매칭된다()
    {
        CodexProcessGuard.Result byName = CodexProcessGuard.Check(() => [new RunningProcessInfo("CODEX", null)]);
        Assert.True(byName.IsRunning);

        CodexProcessGuard.Result byPath = CodexProcessGuard.Check(
            () => [new RunningProcessInfo("x", @"c:\users\user\appdata\local\openai\codex\bin\x\codex.exe")]);
        Assert.True(byPath.IsRunning);
    }

    [Fact]
    public void MainModulePath가_null이어도_이름만으로_판정한다()
    {
        CodexProcessGuard.Result result = CodexProcessGuard.Check(
            () => [new RunningProcessInfo("Codex", MainModulePath: null)]);

        Assert.True(result.IsRunning);
    }

    [Fact]
    public void 무관한_프로세스는_실행_중이_아니라고_판정한다()
    {
        CodexProcessGuard.Result result = CodexProcessGuard.Check(
            () => [
                new RunningProcessInfo("explorer", @"C:\Windows\explorer.exe"),
                new RunningProcessInfo("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ]);

        Assert.False(result.IsRunning);
        Assert.True(result.CanProceed);
    }

    // ── 실제 OS 프로세스로 SystemRunningProcessLister의 Dispose 안전성 확인 ──

    [Fact]
    public void SystemRunningProcessLister는_실제_프로세스_목록에서도_예외_없이_동작한다()
    {
        // Phase 07_01 코드 리뷰로 발견된 버그(Dispose 이후 ProcessName을 읽던 구조)가 있었다면
        // 이 호출 자체가 실제 프로세스 다수를 순회하며 InvalidOperationException을 던졌을 것이다.
        IReadOnlyList<RunningProcessInfo> processes = CodexProcessGuard.SystemRunningProcessLister();

        Assert.NotEmpty(processes);
        Assert.All(processes, p => Assert.False(string.IsNullOrEmpty(p.ProcessName)));
    }

    [Fact]
    public void 실제로_띄운_자식_프로세스의_ProcessName과_경로를_정확히_캡처한다()
    {
        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 >nul", // 초기화 대기 상한보다 오래 살아 있게 한다(끝나면 finally에서 종료)
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            // Phase 9_1-15 — 이 테스트가 띄운 바로 그 자식을 PID로 찾는다(목록의 "첫 번째 cmd"가 아니다).
            // 재수정: 방금 띄운 프로세스는 초기화가 끝나기 전까지 MainModule을 읽을 수 없어 경로가 null일 수 있다
            // (부하 상황에서 실측 재현). 고정 Sleep 대신, 경로가 채워질 때까지 짧은 간격으로 목록을 다시 읽는다.
            var stopwatch = Stopwatch.StartNew();
            RunningProcessInfo? found = null;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                found = CodexProcessGuard.SystemRunningProcessLister().FirstOrDefault(p => p.ProcessId == child.Id);
                if (found?.MainModulePath is not null)
                {
                    break;
                }

                Thread.Sleep(25);
            }

            Assert.True(found is not null, $"자식 프로세스를 목록에서 찾지 못했습니다. pid={child.Id} elapsedMs={stopwatch.ElapsedMilliseconds}");
            Assert.Equal("cmd", found!.ProcessName, ignoreCase: true);
            Assert.True(
                found.MainModulePath is not null,
                $"자식 프로세스의 실행 파일 경로를 제한 시간 안에 읽지 못했습니다. pid={child.Id} elapsedMs={stopwatch.ElapsedMilliseconds}");
            Assert.Contains("cmd.exe", found.MainModulePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}
