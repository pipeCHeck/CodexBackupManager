using Microsoft.Win32;

namespace CodexBackupManager.App.Services;

/// <summary>
/// <c>.codexbackup</c> 저장 위치를 고르는 대화상자. WPF 내장 <see cref="SaveFileDialog"/>를 쓴다
/// (외부 패키지 불필요, <see cref="FolderPicker"/>와 같은 원칙).
/// </summary>
public static class BackupFilePicker
{
    /// <summary>저장할 <c>.codexbackup</c> 경로를 고르게 한다. 취소하면 <c>null</c>.</summary>
    /// <param name="suggestedFileName">기본 파일 이름(확장자 제외). 대화 제목 원문을 쓰지 않는다.</param>
    public static string? PickSaveLocation(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "백업 내보내기",
            Filter = "Codex Backup (*.codexbackup)|*.codexbackup",
            DefaultExt = ".codexbackup",
            FileName = suggestedFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
