namespace CodexBackupManager.App.Services;

/// <summary>
/// 저장되는 설정. Phase 1에서는 항목 하나뿐이다. 과설계하지 않는다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>설정 파일 스키마 버전.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 사용자가 직접 지정한 Codex Home 경로. 자동 탐색 3순위에서 사용된다.
    /// </summary>
    public string? ManualCodexHomePath { get; set; }
}
