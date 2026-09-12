using System.Windows;

namespace CodexBackupManager.App.Services;

/// <summary>
/// Apply(Phase 07_01)처럼 되돌리기 어려운 동작 전에 사용자에게 확인을 받는다. WPF에 내장된
/// <see cref="MessageBox"/>를 쓴다 — 외부 패키지가 필요 없다.
/// </summary>
public static class ConfirmDialog
{
    /// <summary>예/아니오 확인. "예"를 누르면 <c>true</c>.</summary>
    public static bool Confirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
