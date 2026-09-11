using System;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex;

namespace CodexBackupManager.Codex;

/// <summary>
/// 탐색 + 검증 + 조사를 한 번에 수행하는 얇은 파사드. UI가 사용한다.
/// </summary>
/// <remarks>
/// 로직은 전부 <see cref="CodexLocator"/> / <see cref="CodexHomeValidator"/> /
/// <see cref="CodexInstallationInspector"/>에 있다. 이 클래스는 순서만 엮는다.
/// 거대한 Manager 클래스를 만들지 않기 위한 경계다. (CLAUDE.md §5)
/// </remarks>
public sealed class CodexDetectionService
{
    private readonly CodexLocator _locator;
    private readonly CodexInstallationInspector _inspector;

    /// <summary>기본 구성으로 생성한다. (실제 프로세스 환경 사용)</summary>
    public CodexDetectionService()
        : this(new CodexLocator(new SystemCodexEnvironment(), new CodexHomeValidator()),
               new CodexInstallationInspector())
    {
    }

    /// <summary>의존성을 주입해 생성한다.</summary>
    public CodexDetectionService(CodexLocator locator, CodexInstallationInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(inspector);
        _locator = locator;
        _inspector = inspector;
    }

    /// <summary>탐색 결과와 조사 결과.</summary>
    /// <param name="Located">탐색 기록. 실패해도 후보별 사유가 담긴다.</param>
    /// <param name="Installation">조사 결과. 탐색 실패 시 <c>null</c>.</param>
    public sealed record DetectionResult(CodexLocatorResult Located, CodexInstallationInfo? Installation)
    {
        /// <summary>사용 가능한 Codex Home을 찾았는지.</summary>
        public bool Found => Located.Found && Installation is not null;
    }

    /// <summary>우선순위에 따라 자동 탐지한다.</summary>
    /// <param name="savedManualPath">Settings에 저장된 수동 경로(없으면 <c>null</c>).</param>
    public DetectionResult Detect(string? savedManualPath)
    {
        CodexLocatorResult located = _locator.Locate(savedManualPath);
        return new DetectionResult(located, _inspector.Inspect(located));
    }

    /// <summary>사용자가 직접 선택한 폴더를 검사하고 조사한다.</summary>
    public DetectionResult DetectFromUserSelection(string? selectedPath)
    {
        CodexLocatorResult located = _locator.EvaluateUserSelection(selectedPath);
        return new DetectionResult(located, _inspector.Inspect(located));
    }
}
