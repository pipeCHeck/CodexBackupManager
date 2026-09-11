namespace CodexBackupManager.Domain.Paths;

/// <summary>
/// Windows 경로의 루트 형태. Phase 0 조사에서 확인된 표기 차이를 분류하기 위한 값.
/// </summary>
public enum PathRootKind
{
    /// <summary>상대 경로 (루트 없음). 예: <c>sub\dir</c></summary>
    Relative = 0,

    /// <summary>드라이브 루트. 예: <c>C:\_UserProjects</c></summary>
    DriveRooted,

    /// <summary>드라이브 상대 경로. 예: <c>C:sub</c> (현재 디렉터리 의존 → 비교 불가로 취급)</summary>
    DriveRelative,

    /// <summary>드라이브 없는 루트 경로. 예: <c>\Windows</c> (현재 드라이브 의존)</summary>
    RootedWithoutDrive,

    /// <summary>UNC 경로. 예: <c>\\server\share\dir</c></summary>
    Unc,

    /// <summary>기타 디바이스 경로. 예: <c>\\.\pipe\name</c></summary>
    Device,
}
