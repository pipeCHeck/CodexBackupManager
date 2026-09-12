using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CodexBackupManager.App.ViewModels;

namespace CodexBackupManager.App.Services;

/// <summary>
/// 임시 진단 장치(Phase 4 UI 크래시 조사용). 실제 사용자가 긴 대화를 스크롤하는 중 앱이 반복적으로
/// 종료되는 크래시를 재현/보고받았는데, 지금까지는 UI 스레드 미처리 예외를 잡는 장치가 없어서
/// 스택 트레이스를 확보하지 못했다.
/// </summary>
/// <remarks>
/// <para>
/// 이 클래스는 예외를 <b>삼키지 않는다</b>. <see cref="LogCrash"/>는 증거를 남기기만 하고,
/// 호출부(<c>App.xaml.cs</c>)는 <c>Handled</c>를 <c>true</c>로 바꾸지 않으므로 앱은 그대로 죽는다 —
/// 증상을 은폐하는 것이 아니라 원인을 확인하기 위한 진단 목적이다.
/// </para>
/// <para>
/// 개인정보 보호: 대화 원문/제목/thread ID 원문/경로는 절대 남기지 않는다(지시 §9, CLAUDE.md §29와
/// 동일한 규칙). 스택 프레임은 <see cref="StackTrace"/>를 <c>fNeedFileInfo:false</c>로 만들어
/// 파일 경로/줄 번호 자체가 애초에 담기지 않게 하고, 타입/메서드 이름만 뽑아 남긴다.
/// </para>
/// </remarks>
public static class CrashDiagnostics
{
    /// <summary>
    /// 크래시 직전 증거를 한 줄로 남긴다. 이 메서드 자체가 실패해도(예: Process 정보 조회 실패)
    /// 원본 예외 전파를 막지 않도록 내부에서 방어적으로 처리한다.
    /// </summary>
    /// <param name="logger">기록할 로거.</param>
    /// <param name="source">
    /// 어느 핸들러에서 잡았는지("Dispatcher"/"AppDomain"/"UnobservedTask") — 세 경로 모두 같은
    /// 예외를 중복으로 잡을 수 있어 구분해서 남긴다.
    /// </param>
    /// <param name="exception">잡힌 예외.</param>
    /// <param name="viewModel">현재 메인 ViewModel. Conversation 개수/렌더링 개수 집계용(없으면 <c>null</c>).</param>
    public static void LogCrash(FileLogger logger, string source, Exception exception, MainViewModel? viewModel)
    {
        try
        {
            var line = new StringBuilder();
            line.Append("[CRASH-DIAG] source=").Append(source);
            line.Append(" exceptionType=").Append(exception.GetType().FullName);
            line.Append(" hresult=0x").Append(exception.HResult.ToString("X8"));
            line.Append(" innerType=").Append(exception.InnerException?.GetType().FullName ?? "(없음)");

            if (viewModel is not null)
            {
                int total = SafeCount(() => viewModel.ConversationMessages.Count);
                int rendered = SafeCount(() => viewModel.ConversationMessages.Count(m => m.IsBodyRendered));
                line.Append(" conversationMessages=").Append(total);
                line.Append(" bodyRendered=").Append(rendered);
            }
            else
            {
                line.Append(" conversationMessages=(viewModel 없음)");
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                line.Append(" workingSetKB=").Append(process.WorkingSet64 / 1024);
                line.Append(" privateMemKB=").Append(process.PrivateMemorySize64 / 1024);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                line.Append(" workingSetKB=(조회 실패) privateMemKB=(조회 실패)");
            }

            line.Append(" topFrames=[").Append(DescribeTopFrames(exception)).Append(']');

            logger.Error(line.ToString());
        }
        catch (Exception diagnosticFailure)
        {
            // 진단 코드 자체의 실패가 원본 크래시 전파를 막아서는 안 된다. 최소한의 흔적만 남긴다.
            try
            {
                logger.Error($"[CRASH-DIAG] 진단 기록 중 추가 오류: {diagnosticFailure.GetType().Name}");
            }
            catch
            {
                // 그래도 실패하면 포기한다 — 로깅 실패가 크래시 전파를 막지 않는다.
            }
        }
    }

    private static int SafeCount(Func<int> countFn)
    {
        try
        {
            return countFn();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 상위 몇 프레임의 타입/메서드 이름만 뽑는다. <c>fNeedFileInfo:false</c>라 파일 경로/줄 번호는
    /// 애초에 <see cref="StackFrame"/>에 담기지 않는다.
    /// </summary>
    private static string DescribeTopFrames(Exception exception, int maxFrames = 6)
    {
        try
        {
            var stackTrace = new StackTrace(exception, fNeedFileInfo: false);
            var frames = stackTrace.GetFrames();
            if (frames is null || frames.Length == 0)
            {
                return "(스택 프레임 없음)";
            }

            return string.Join(" | ", frames.Take(maxFrames).Select(DescribeFrame));
        }
        catch
        {
            return "(스택 프레임 조회 실패)";
        }
    }

    private static string DescribeFrame(StackFrame frame)
    {
        System.Reflection.MethodBase? method = frame.GetMethod();
        if (method is null)
        {
            return "(알 수 없음)";
        }

        string typeName = method.DeclaringType?.FullName ?? "(알 수 없는 타입)";
        return $"{typeName}.{method.Name}";
    }
}
