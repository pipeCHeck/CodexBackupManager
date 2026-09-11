using System.Collections.Generic;
using CodexBackupManager.Codex.Locating;

namespace CodexBackupManager.Codex.Tests.TestSupport;

/// <summary>테스트용 환경변수 제공자. 실제 프로세스 환경을 건드리지 않는다.</summary>
public sealed class FakeCodexEnvironment : ICodexEnvironment
{
    private readonly Dictionary<string, string?> _variables = new();

    /// <summary><c>%USERPROFILE%</c>로 보고할 값.</summary>
    public string? UserProfileDirectory { get; set; }

    /// <summary>환경변수를 설정한다. <c>null</c>이면 없는 것으로 취급한다.</summary>
    public FakeCodexEnvironment Set(string name, string? value)
    {
        _variables[name] = value;
        return this;
    }

    /// <summary><c>CODEX_HOME</c>을 설정한다.</summary>
    public FakeCodexEnvironment WithCodexHome(string? value)
        => Set(SystemCodexEnvironment.CodexHomeVariable, value);

    /// <summary><c>%USERPROFILE%</c>을 설정한다.</summary>
    public FakeCodexEnvironment WithUserProfile(string? value)
    {
        UserProfileDirectory = value;
        return this;
    }

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => _variables.TryGetValue(name, out string? value) ? value : null;

    /// <inheritdoc />
    public string? GetUserProfileDirectory() => UserProfileDirectory;
}
