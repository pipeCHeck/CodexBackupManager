using System;
using System.IO;
using System.Threading;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 07_03 요구사항 4 — <see cref="RestoreProcessLock"/> 자체의 mutex 의미론을 빠르고
/// 결정적으로(단일 프로세스, 여러 스레드) 검증한다. named Mutex의 소유권은 스레드 단위이므로, 같은
/// 프로세스 안에서도 스레드를 분리하면 실제 프로세스 경계를 넘는 것과 동일한 동시성 규칙이
/// 적용된다. 진짜 두 프로세스로 검증하는 통합 테스트는
/// <see cref="CrashRecoveryIntegrationTests"/>에 별도로 있다.
/// </summary>
public sealed class RestoreProcessLockTests
{
    private static string NewFakeHomePath() => Path.Combine(Path.GetTempPath(), "cbm-lock-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 같은_Home에_대해_동시에_획득하면_한쪽만_성공한다()
    {
        string home = NewFakeHomePath();

        RestoreProcessLock.AcquireResult first = RestoreProcessLock.TryAcquire(home, TimeSpan.Zero);
        Assert.True(first.Acquired);

        bool secondAcquired = false;
        var thread = new Thread(() =>
        {
            RestoreProcessLock.AcquireResult second = RestoreProcessLock.TryAcquire(home, TimeSpan.Zero);
            secondAcquired = second.Acquired;
            second.Handle?.Dispose();
        });
        thread.Start();
        thread.Join();

        Assert.False(secondAcquired);

        first.Handle!.Dispose();
    }

    [Fact]
    public void 다른_Home은_동시에_획득할_수_있다()
    {
        string homeA = NewFakeHomePath();
        string homeB = NewFakeHomePath();

        RestoreProcessLock.AcquireResult a = RestoreProcessLock.TryAcquire(homeA, TimeSpan.Zero);
        RestoreProcessLock.AcquireResult b = RestoreProcessLock.TryAcquire(homeB, TimeSpan.Zero);

        Assert.True(a.Acquired);
        Assert.True(b.Acquired);

        a.Handle!.Dispose();
        b.Handle!.Dispose();
    }

    [Fact]
    public void 소유_스레드가_놓지_않고_끝나면_다음_획득은_Abandoned로_처리된다()
    {
        // 요구사항 4 — "이전 프로세스 crash로 mutex가 풀린 상황"을 스레드 종료로 흉내낸다: 소유
        // 스레드가 ReleaseMutex 없이 끝나면 OS가 이 mutex를 abandon 상태로 만든다.
        string home = NewFakeHomePath();
        bool heldAcquired = false;

        var thread = new Thread(() =>
        {
            RestoreProcessLock.AcquireResult held = RestoreProcessLock.TryAcquire(home, TimeSpan.Zero);
            heldAcquired = held.Acquired;
        });
        thread.Start();
        thread.Join();

        Assert.True(heldAcquired);

        RestoreProcessLock.AcquireResult next = RestoreProcessLock.TryAcquire(home, TimeSpan.Zero);

        Assert.True(next.Acquired);
        Assert.True(next.WasAbandoned);

        next.Handle!.Dispose();
    }

    [Fact]
    public void 같은_경로의_다른_표기도_같은_lock으로_취급한다()
    {
        // 대소문자/`\\?\` prefix 차이로 서로 다른 lock이 되면 안 된다 — CanonicalPath로 정규화한다.
        // named Mutex의 소유권은 스레드 단위라 같은 스레드가 두 번 WaitOne하면 재귀적으로 그냥
        // 성공해 버린다 — 반드시 다른 스레드에서 두 번째를 시도해야 실제로 검증된다.
        string home = @"C:\Fixture\Some Home\" + Guid.NewGuid().ToString("N");
        string extendedPrefixed = @"\\?\" + home.ToUpperInvariant();

        RestoreProcessLock.AcquireResult first = RestoreProcessLock.TryAcquire(home, TimeSpan.Zero);
        Assert.True(first.Acquired);

        bool secondAcquired = false;
        var thread = new Thread(() =>
        {
            RestoreProcessLock.AcquireResult second = RestoreProcessLock.TryAcquire(extendedPrefixed, TimeSpan.Zero);
            secondAcquired = second.Acquired;
            second.Handle?.Dispose();
        });
        thread.Start();
        thread.Join();

        Assert.False(secondAcquired);

        first.Handle!.Dispose();
    }
}
