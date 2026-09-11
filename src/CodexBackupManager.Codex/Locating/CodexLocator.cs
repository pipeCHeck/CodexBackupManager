using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Locating;

/// <summary>
/// Codex Home을 탐색한다.
/// </summary>
/// <remarks>
/// <para>탐색 우선순위 (CLAUDE.md §4):</para>
/// <list type="number">
///   <item>런타임 <c>CODEX_HOME</c> 환경변수</item>
///   <item><c>%USERPROFILE%\.codex</c></item>
///   <item>프로그램 Settings에 저장된 수동 경로</item>
///   <item>사용자가 직접 선택한 폴더 (<see cref="EvaluateUserSelection"/>)</item>
/// </list>
/// <para>
/// <b>어떤 구체적 경로도 하드코딩하지 않는다.</b> <c>C:\Users\User\.codex</c>는 이 코드에 존재하지 않는다.
/// 1순위는 항상 런타임 환경변수를 읽고, 2순위는 런타임 사용자 홈에서 조립한다.
/// </para>
/// <para>
/// 후보는 우선순위대로 검사하고, <see cref="CodexHomeValidation.IsUsable"/>인 첫 후보를 채택한다.
/// 채택되지 않은 후보도 모두 <see cref="CodexLocatorResult.Probes"/>에 사유와 함께 남긴다.
/// </para>
/// </remarks>
public sealed class CodexLocator
{
    private readonly ICodexEnvironment _environment;
    private readonly CodexHomeValidator _validator;

    /// <summary>기본 디렉터리 이름. <c>%USERPROFILE%</c> 아래에서 찾는다.</summary>
    public const string DefaultCodexDirectoryName = ".codex";

    /// <summary>생성자.</summary>
    public CodexLocator(ICodexEnvironment environment, CodexHomeValidator validator)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(validator);
        _environment = environment;
        _validator = validator;
    }

    /// <summary>
    /// 우선순위에 따라 Codex Home을 찾는다.
    /// </summary>
    /// <param name="savedManualPath">
    /// Settings에 저장된 수동 경로. 없으면 <c>null</c>.
    /// </param>
    public CodexLocatorResult Locate(string? savedManualPath = null)
    {
        var probes = new List<CodexHomeProbe>();

        // 1순위 — CODEX_HOME
        string? fromVariable = _environment.GetEnvironmentVariable(SystemCodexEnvironment.CodexHomeVariable);
        if (TryAccept(CodexHomeSource.CodexHomeEnvironmentVariable, fromVariable,
                emptyNote: $"{SystemCodexEnvironment.CodexHomeVariable} 환경변수가 설정되어 있지 않습니다.",
                probes, out CodexLocatorResult? accepted))
        {
            return accepted!;
        }

        // 2순위 — %USERPROFILE%\.codex
        string? userProfile = _environment.GetUserProfileDirectory();
        string? defaultHome = null;
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // Path.Combine을 쓰지 않는 이유: userProfile이 '/' 구분자로 들어와도
            // CanonicalPath가 정규화하므로, 여기서는 단순 결합으로 충분하고 예측 가능하다.
            defaultHome = System.IO.Path.Combine(userProfile, DefaultCodexDirectoryName);
        }

        if (TryAccept(CodexHomeSource.UserProfileDotCodex, defaultHome,
                emptyNote: "사용자 홈 디렉터리를 확인할 수 없습니다.",
                probes, out accepted))
        {
            return accepted!;
        }

        // 3순위 — 저장된 수동 경로
        if (TryAccept(CodexHomeSource.SavedManualPath, savedManualPath,
                emptyNote: "저장된 수동 경로가 없습니다.",
                probes, out accepted))
        {
            return accepted!;
        }

        return new CodexLocatorResult(
            Found: false,
            Source: null,
            Home: null,
            HomeDisplayPath: null,
            Validation: null,
            Probes: probes);
    }

    /// <summary>
    /// 사용자가 UI에서 직접 선택한 폴더를 검사한다. (4순위)
    /// </summary>
    /// <remarks>
    /// 자동 탐색과 달리, 실패해도 다른 후보로 넘어가지 않는다.
    /// 사용자가 명시적으로 고른 폴더이므로 실패 사유를 그대로 보여준다.
    /// </remarks>
    public CodexLocatorResult EvaluateUserSelection(string? selectedPath)
    {
        var probes = new List<CodexHomeProbe>();

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            probes.Add(new CodexHomeProbe(
                CodexHomeSource.UserSelected, selectedPath, Skipped: true, Validation: null,
                Note: "선택된 폴더가 없습니다."));
            return new CodexLocatorResult(false, null, null, null, null, probes);
        }

        CodexHomeValidation validation = _validator.Validate(selectedPath);
        CanonicalPath.TryCreate(selectedPath, out CanonicalPath? canonical, out _);

        probes.Add(new CodexHomeProbe(
            CodexHomeSource.UserSelected, selectedPath, Skipped: false, validation,
            Note: validation.IsUsable ? "채택" : "탈락"));

        return new CodexLocatorResult(
            Found: validation.IsUsable && canonical is not null,
            Source: validation.IsUsable ? CodexHomeSource.UserSelected : null,
            Home: validation.IsUsable ? canonical : null,
            HomeDisplayPath: validation.IsUsable ? canonical?.Display : null,
            Validation: validation,
            Probes: probes);
    }

    private bool TryAccept(
        CodexHomeSource source,
        string? rawPath,
        string emptyNote,
        List<CodexHomeProbe> probes,
        out CodexLocatorResult? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            probes.Add(new CodexHomeProbe(source, rawPath, Skipped: true, Validation: null, Note: emptyNote));
            return false;
        }

        CodexHomeValidation validation = _validator.Validate(rawPath);

        if (!validation.IsUsable)
        {
            probes.Add(new CodexHomeProbe(source, rawPath, Skipped: false, validation, Note: "탈락"));
            return false;
        }

        if (!CanonicalPath.TryCreate(rawPath, out CanonicalPath? canonical, out string? pathError))
        {
            probes.Add(new CodexHomeProbe(
                source, rawPath, Skipped: false, validation, Note: $"경로 정규화 실패: {pathError}"));
            return false;
        }

        probes.Add(new CodexHomeProbe(source, rawPath, Skipped: false, validation, Note: "채택"));
        result = new CodexLocatorResult(
            Found: true,
            Source: source,
            Home: canonical,
            HomeDisplayPath: canonical!.Display,
            Validation: validation,
            Probes: probes);
        return true;
    }
}
