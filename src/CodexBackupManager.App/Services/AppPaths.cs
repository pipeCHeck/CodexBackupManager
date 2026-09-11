using System;
using System.IO;

namespace CodexBackupManager.App.Services;

/// <summary>
/// 이 프로그램이 파일을 쓰는 곳. <b>Codex Home 안에는 아무것도 쓰지 않는다.</b>
/// </summary>
/// <remarks>
/// CLAUDE.md §2.2 / 지시 §7 §9: 설정과 로그는 <c>%APPDATA%\CodexBackupManager\</c> 아래에만 둔다.
/// </remarks>
public static class AppPaths
{
    /// <summary>제품 폴더 이름.</summary>
    public const string ProductFolderName = "CodexBackupManager";

    /// <summary><c>%APPDATA%\CodexBackupManager</c></summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductFolderName);

    /// <summary><c>%APPDATA%\CodexBackupManager\settings.json</c></summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary><c>%APPDATA%\CodexBackupManager\logs</c></summary>
    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>필요한 폴더를 만든다. 실패해도 예외를 전파하지 않는다.</summary>
    public static void EnsureCreated()
    {
        TryCreate(Root);
        TryCreate(LogDirectory);
    }

    private static void TryCreate(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 설정/로그를 못 만들어도 조사 기능은 동작해야 한다.
        }
    }
}
