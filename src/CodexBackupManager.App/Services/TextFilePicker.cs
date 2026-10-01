using Microsoft.Win32;

namespace CodexBackupManager.App.Services;

/// <summary>(Phase 9_6-02) 내보내기 목록 텍스트 파일 저장 위치를 고르는 대화상자(WPF 내장 <see cref="SaveFileDialog"/>).</summary>
public static class TextFilePicker
{
    /// <summary>저장할 <c>.txt</c> 경로를 고르게 한다. 취소하면 <c>null</c>.</summary>
    /// <param name="initialDirectory">기본 폴더(백업과 같은 폴더).</param>
    /// <param name="suggestedFileName">기본 파일 이름(<c>&lt;백업 이름&gt;.txt</c>).</param>
    public static string? PickSaveLocation(string initialDirectory, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "목록을 텍스트 파일로 저장",
            Filter = "텍스트 파일 (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = suggestedFileName,
            InitialDirectory = initialDirectory,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
