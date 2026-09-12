using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexBackupManager.Restore;

/// <summary>실행 중인 프로세스 하나에 대해 알아야 할 최소 정보(테스트 주입용 추상화).</summary>
/// <param name="ProcessName">프로세스 이름(확장자 없이, 예: <c>"Codex"</c>, <c>"codex"</c>).</param>
/// <param name="MainModulePath">실행 파일 전체 경로. 접근할 수 없으면 <c>null</c>(예: 권한 문제).</param>
public sealed record RunningProcessInfo(string ProcessName, string? MainModulePath);

/// <summary>
/// Codex(Desktop Electron 앱 또는 CLI)가 지금 실행 중인지 판정한다(Phase 7 — 요구사항 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>보수적으로 설계한다.</b> lock 파일 하나가 존재한다고 무조건 "실행 중"으로 단정하지 않는다 —
/// 실제 프로세스 목록을 근거로만 판정한다(사용자 지시). 프로세스 이름/실행 파일 경로 둘 다
/// 알려진 Codex 식별자와 비교한다:
/// </para>
/// <list type="bullet">
///   <item>프로세스 이름이 <c>"Codex"</c>(Desktop Electron 앱의 제품명, 대소문자 무시)이거나
///     <c>"codex"</c>(CLI 실행 파일 이름)와 정확히 같으면 실행 중으로 판정한다.</item>
///   <item>실행 파일 경로가 있고 그 경로 안에 <c>"OpenAI\Codex"</c>(config.toml의
///     <c>CODEX_CLI_PATH</c>/런타임 경로 구조, <c>docs/codex-storage-format.md</c> §1)가
///     포함되면(대소문자 무시) 실행 중으로 판정한다 — 이름만으로 식별되지 않는 헬퍼/렌더러
///     프로세스까지 잡아내기 위함이다.</item>
/// </list>
/// <para>
/// 이 클래스는 프로세스 목록을 직접 얻지 않는다 — <see cref="RunningProcessLister"/> 델리게이트로
/// 주입받는다. 실제 운영 코드는 <see cref="SystemRunningProcessLister"/>를 쓰고, 테스트는 합성
/// 목록을 주입한다.
/// </para>
/// </remarks>
public static class CodexProcessGuard
{
    /// <summary>지금 실행 중인 프로세스 목록을 돌려주는 델리게이트.</summary>
    public delegate IReadOnlyList<RunningProcessInfo> RunningProcessLister();

    /// <summary>판정 결과.</summary>
    /// <param name="IsRunning">Codex가 실행 중인 것으로 판정됐는지.</param>
    /// <param name="MatchedProcessNames">판정 근거가 된 프로세스 이름(진단용, 경로는 포함하지 않는다).</param>
    public sealed record Result(bool IsRunning, IReadOnlyList<string> MatchedProcessNames)
    {
        /// <summary>Codex가 실행 중이지 않아 Apply를 진행해도 되는지.</summary>
        public bool CanProceed => !IsRunning;
    }

    private static readonly string[] KnownProcessNames = ["codex", "Codex"];
    private const string KnownPathMarker = @"OpenAI\Codex";

    /// <summary>지금 실행 중인 프로세스 목록으로 Codex 실행 여부를 판정한다.</summary>
    public static Result Check(RunningProcessLister processLister)
    {
        ArgumentNullException.ThrowIfNull(processLister);

        IReadOnlyList<RunningProcessInfo> processes = processLister() ?? [];
        var matched = new List<string>();

        foreach (RunningProcessInfo process in processes)
        {
            bool nameMatches = KnownProcessNames.Any(
                known => string.Equals(known, process.ProcessName, StringComparison.OrdinalIgnoreCase));
            bool pathMatches = process.MainModulePath is not null &&
                process.MainModulePath.Contains(KnownPathMarker, StringComparison.OrdinalIgnoreCase);

            if (nameMatches || pathMatches)
            {
                matched.Add(process.ProcessName);
            }
        }

        return new Result(matched.Count > 0, matched);
    }

    /// <summary>
    /// 실제 OS 프로세스 목록을 읽는 기본 구현. <see cref="System.Diagnostics.Process.MainModule"/>
    /// 접근이 실패하면(다른 권한으로 실행 중인 프로세스 등) 그 프로세스는 이름만으로 판정한다 —
    /// 예외를 던지지 않는다.
    /// </summary>
    public static IReadOnlyList<RunningProcessInfo> SystemRunningProcessLister()
    {
        var result = new List<RunningProcessInfo>();
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            // Dispose 전에 필요한 값(이름 + 실행 파일 경로)을 전부 안전하게 캡처해 둔다 — Dispose
            // 이후 Process 속성에 접근하면 내부 핸들이 이미 해제돼 InvalidOperationException이
            // 발생할 수 있다(Phase 07_01 코드 리뷰로 발견 — 예전 코드는 Dispose를 finally에서
            // 부르고 그 다음 줄에서 process.ProcessName을 읽었다).
            string processName;
            string? mainModulePath = null;
            try
            {
                processName = process.ProcessName;
                try
                {
                    mainModulePath = process.MainModule?.FileName;
                }
                catch
                {
                    // 접근 권한 문제 등 — 경로 없이 이름만으로 판정한다.
                }

                result.Add(new RunningProcessInfo(processName, mainModulePath));
            }
            catch
            {
                // 이름조차 읽을 수 없는 경우(프로세스가 그 사이 종료됨 등) — 이 프로세스는 목록에서 건너뛴다.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}
