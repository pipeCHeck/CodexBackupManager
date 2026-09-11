using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexBackupManager.App.Services;

/// <summary>로그 수준.</summary>
public enum LogLevel
{
    /// <summary>상세 진단.</summary>
    Debug = 0,

    /// <summary>일반 정보.</summary>
    Info = 1,

    /// <summary>경고.</summary>
    Warning = 2,

    /// <summary>오류.</summary>
    Error = 3,
}

/// <summary>
/// 날짜별 파일에 기록하는 최소 로거. 외부 로깅 패키지를 쓰지 않는다.
/// </summary>
/// <remarks>
/// <para>
/// 직접 만든 이유는 두 가지다.
/// </para>
/// <list type="bullet">
///   <item>NuGet 의존성을 늘리지 않는다 (CLAUDE.md §2.1)</item>
///   <item>
///     민감 정보 redaction 규칙을 우리가 완전히 통제한다.
///     호출부는 <see cref="CodexBackupManager.Domain.Diagnostics.Redact"/>를 통과한 값만 넘겨야 하며,
///     이 로거는 그 규칙을 문서화된 계약으로 강제한다.
///   </item>
/// </list>
/// <para>
/// <b>로그에 남기지 않는 것</b> (지시 §9 / CLAUDE.md §29):
/// 대화 내용, <c>first_user_message</c>, <c>preview</c>, 대화 제목, 개인 프로젝트 경로 전문.
/// </para>
/// </remarks>
public sealed class FileLogger
{
    private readonly string _directory;
    private readonly object _gate = new();

    /// <summary>기본 로그 폴더로 생성한다.</summary>
    public FileLogger()
        : this(AppPaths.LogDirectory)
    {
    }

    /// <summary>폴더를 지정해 생성한다. (테스트용)</summary>
    public FileLogger(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>기록할 최소 수준.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>오늘 로그 파일 경로.</summary>
    public string CurrentFilePath =>
        Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Debug 기록.</summary>
    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    /// <summary>Info 기록.</summary>
    public void Info(string message) => Write(LogLevel.Info, message, null);

    /// <summary>Warning 기록.</summary>
    public void Warning(string message) => Write(LogLevel.Warning, message, null);

    /// <summary>Error 기록.</summary>
    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = new StringBuilder();
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        line.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ");
        line.Append(message.Replace('\r', ' ').Replace('\n', ' '));

        if (exception is not null)
        {
            // 예외 메시지에 경로가 섞일 수 있으므로 타입과 메시지 첫 줄만 남긴다.
            string firstLine = exception.Message.Split('\n', 2)[0].Trim();
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(firstLine);
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFilePath, line.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 로깅 실패가 기능을 막지 않는다.
        }
    }
}
