using System.Reflection;

namespace CodexBackupManager.App.Services;

/// <summary>
/// Phase 8 — 로그/창 제목에 쓰는 배포 버전 표시 문자열. <c>Directory.Build.props</c>의
/// <c>AssemblyVersion</c>에서 직접 읽으므로, 릴리스마다 버전 문자열을 여러 곳에 따로 하드코딩해
/// 두고 깜빡 빠뜨리는 일이 없다.
/// </summary>
public static class AppVersionInfo
{
    /// <summary>예: <c>0.1.0</c>. 어셈블리 버전의 앞 3자리(Major.Minor.Build)만 쓴다.</summary>
    public static string ShortVersion { get; } = (Assembly.GetExecutingAssembly().GetName().Version ?? new System.Version(0, 0, 0)).ToString(3);

    /// <summary>예: <c>Codex Backup Manager 0.1.0</c>. 로그/창 제목에 그대로 쓸 수 있다.</summary>
    public static string DisplayName { get; } = $"Codex Backup Manager {ShortVersion}";
}
