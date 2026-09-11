using System;

namespace CodexBackupManager.Codex.Locating;

/// <summary>실제 프로세스 환경을 읽는 구현.</summary>
public sealed class SystemCodexEnvironment : ICodexEnvironment
{
    /// <summary>Codex Home을 지정하는 환경변수 이름.</summary>
    public const string CodexHomeVariable = "CODEX_HOME";

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc />
    public string? GetUserProfileDirectory()
    {
        // USERPROFILE을 우선 보고, 없으면 SpecialFolder로 폴백한다.
        string? fromVariable = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(fromVariable))
        {
            return fromVariable;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }
}
