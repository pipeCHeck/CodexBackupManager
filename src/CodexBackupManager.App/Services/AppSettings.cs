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

    /// <summary>
    /// (Phase 9_5-08) 가져오기 화면 [새 폴더 만들기]의 기준 폴더. 없으면(<c>null</c>) 기본값 <c>문서\ChatGPT</c>를 쓴다.
    /// 이 필드가 없는 이전 설정 파일도 그대로 읽힌다(없는 필드는 <c>null</c>) — 그래서 <see cref="Version"/>은 올리지 않는다.
    /// </summary>
    public string? NewProjectFolderBase { get; set; }
}
