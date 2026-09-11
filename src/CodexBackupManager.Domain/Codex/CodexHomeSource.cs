namespace CodexBackupManager.Domain.Codex;

/// <summary>
/// Codex Home 후보가 어디에서 왔는지. 값의 순서가 곧 탐색 우선순위다.
/// </summary>
public enum CodexHomeSource
{
    /// <summary>1순위 — 런타임 <c>CODEX_HOME</c> 환경변수.</summary>
    CodexHomeEnvironmentVariable = 1,

    /// <summary>2순위 — <c>%USERPROFILE%\.codex</c>.</summary>
    UserProfileDotCodex = 2,

    /// <summary>3순위 — 프로그램 Settings에 저장된 수동 경로.</summary>
    SavedManualPath = 3,

    /// <summary>4순위 — 사용자가 UI에서 직접 선택한 폴더.</summary>
    UserSelected = 4,
}
