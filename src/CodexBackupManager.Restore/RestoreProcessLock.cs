using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>
/// Codex Home 하나에 대해 동시에 두 개의 Restore(Apply 또는 Recovery)가 진행되지 않도록 막는
/// 프로세스 간(inter-process) named Mutex(Phase 07_03 요구사항 4).
/// </summary>
/// <remarks>
/// 한 앱 인스턴스 안에서는 <c>IsApplying</c>으로 이미 막고 있지만, EXE를 두 번 실행하면 서로 다른
/// 프로세스가 같은 Codex Home에 동시에 Apply할 수 있다 — 배포 전 반드시 막아야 한다(요구사항 4).
/// Viewer/Export 등 읽기 전용 기능은 여러 창/프로세스가 동시에 있어도 상관없으므로, 이 lock은
/// <see cref="RestoreExecutor.Apply(ImportPlan,string,string?,IRestoreFaultInjectionHook?,Action{string}?,CancellationToken)"/>과
/// <see cref="IncompleteApplyRecoveryService.Recover"/> 전체를 감싸는 데만 쓴다.
/// </remarks>
public static class RestoreProcessLock
{
    private const string NamePrefix = @"Local\CodexBackupManager.Restore.";

    /// <summary>lock을 쥔 동안만 유효한 핸들. Dispose하면 즉시 놓아준다.</summary>
    public sealed class Handle : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        internal Handle(Mutex mutex) => _mutex = mutex;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (ApplicationException)
            {
                // 이 스레드가 소유하지 않은 mutex를 놓으려 한 경우(이론상 발생하지 않아야 하지만,
                // 놓아주는 것 자체가 목적이므로 여기서 예외를 전파하지 않는다).
            }

            _mutex.Dispose();
        }
    }

    /// <summary>lock 획득 시도 결과.</summary>
    /// <param name="Acquired">이 호출이 lock을 잡았는지.</param>
    /// <param name="Handle">잡았다면 놓아줄 때 쓸 핸들(<see cref="Acquired"/>가 <c>true</c>일 때만 non-null).</param>
    /// <param name="WasAbandoned">
    /// 이전 소유자가 놓아주지 않고(=프로세스가 죽어서) 넘어온 lock인지. Apply 흐름은 이 값 자체로
    /// 특별히 분기하지 않는다 — lock을 잡은 직후 어차피 incomplete-apply 검사를 다시 하므로, 이전
    /// 소유자가 Applying 상태를 남겼다면 그 검사가 새 Apply를 막고 복구를 요구한다(요구사항 4).
    /// </param>
    public sealed record AcquireResult(bool Acquired, Handle? Handle, bool WasAbandoned);

    /// <summary>
    /// <paramref name="codexHomePath"/>에 대응하는 이름의 named Mutex를 <paramref name="timeout"/>
    /// 동안 시도해 획득한다. 실패 성공 여부는 대기 없이 즉시 알아야 하는 경우 <see cref="TimeSpan.Zero"/>를 넘긴다.
    /// </summary>
    public static AcquireResult TryAcquire(string codexHomePath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        string name = BuildMutexName(codexHomePath);
        var mutex = new Mutex(initiallyOwned: false, name, out _);

        bool acquired;
        bool wasAbandoned = false;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // 이전 소유자가 mutex를 놓지 않고 죽었다 — 이번 호출이 획득한 것으로 처리한다(요구사항 4).
            acquired = true;
            wasAbandoned = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return new AcquireResult(false, null, false);
        }

        return new AcquireResult(true, new Handle(mutex), wasAbandoned);
    }

    /// <summary>
    /// Codex Home 경로를 <see cref="CanonicalPath"/>로 정규화한 뒤 해시해 Mutex 이름을 만든다 —
    /// 대소문자/<c>\\?\</c> prefix/trailing slash 차이로 서로 다른 lock이 되지 않게 한다.
    /// </summary>
    public static string BuildMutexName(string codexHomePath)
    {
        CanonicalPath canonical = CanonicalPath.Create(codexHomePath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.Value));
        return NamePrefix + Convert.ToHexStringLower(hash)[..32];
    }
}
