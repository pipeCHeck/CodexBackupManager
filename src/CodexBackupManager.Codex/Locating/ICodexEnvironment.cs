namespace CodexBackupManager.Codex.Locating;

/// <summary>
/// Codex Home 탐색이 필요한 프로세스 환경 값. 테스트에서 대체하기 위해 추상화한다.
/// </summary>
/// <remarks>
/// 파일 시스템은 추상화하지 않는다. 테스트는 임시 디렉터리에 작은 가짜 Codex Home을
/// 실제로 만들어 검증한다. (실제 파일 시스템 동작까지 함께 검증하기 위함)
/// </remarks>
public interface ICodexEnvironment
{
    /// <summary>환경변수 값을 읽는다. 없으면 <c>null</c>.</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>현재 사용자 홈 디렉터리. 확인할 수 없으면 <c>null</c>.</summary>
    string? GetUserProfileDirectory();
}
