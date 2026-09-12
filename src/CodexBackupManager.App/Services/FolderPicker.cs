using Microsoft.Win32;

namespace CodexBackupManager.App.Services;

/// <summary>
/// 폴더 선택 대화상자. WPF에 내장된 <see cref="OpenFolderDialog"/>를 사용한다.
/// (WinForms 참조나 외부 패키지가 필요 없다)
/// </summary>
public static class FolderPicker
{
    /// <summary>Codex Home 폴더를 고르게 한다. 취소하면 <c>null</c>.</summary>
    public static string? PickCodexHome()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Codex 데이터 폴더(.codex)를 선택하세요",
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Phase 06_01 — Import Preview에서 프로젝트 경로를 수동으로 재지정할 때 쓴다. 취소하면 <c>null</c>.
    /// </summary>
    public static string? PickProjectFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "프로젝트 폴더를 선택하세요",
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
