using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Locating;

/// <summary>
/// 어떤 폴더가 실제 Codex Home인지 검사한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>"폴더가 존재한다"는 이유만으로 Valid로 판정하지 않는다.</b> (CLAUDE.md §4)
/// </para>
/// <para>판정 규칙 — 게이트 2개 + 가중 점수:</para>
/// <list type="number">
///   <item>폴더가 존재하지 않으면 <see cref="CodexHomeStatus.Invalid"/>.</item>
///   <item><c>sessions\</c> 하위 폴더가 없으면 <see cref="CodexHomeStatus.Invalid"/>.</item>
///   <item>
///     점수 = 2×(<c>state_*.sqlite</c>) + 1×(<c>session_index.jsonl</c>)
///          + 1×(<c>config.toml</c>) + 1×(<c>.codex-global-state.json</c>)  → 최대 5
///   </item>
///   <item>점수 ≥ 3 → Valid / 1~2 → Probable / 0 → Invalid</item>
/// </list>
/// <para>
/// 점수 0(= <c>sessions\</c> 만 있는 폴더)을 Invalid로 두는 이유: "sessions"라는 이름의 폴더는
/// Codex와 무관한 프로젝트에도 흔하다. Codex를 식별하는 신호가 하나도 없으면 오탐 위험이 더 크다.
/// </para>
/// <para>
/// <c>state_*.sqlite</c>에 가중치 2를 주는 이유: Phase 0 조사에서 state DB가 Codex의 대화 목록을
/// 실제로 만들어내는 인덱스이며, 이 파일 없이는 Codex Home으로서 의미가 없음을 확인했다.
/// </para>
/// </remarks>
public sealed class CodexHomeValidator
{
    /// <summary>
    /// 경로를 검사한다.
    /// </summary>
    /// <param name="rawPath">검사할 폴더 경로(원문).</param>
    public CodexHomeValidation Validate(string? rawPath)
    {
        var signals = new List<CodexHomeSignal>();
        var reasons = new List<string>();

        if (!CanonicalPath.TryCreate(rawPath, out CanonicalPath? canonical, out string? pathError))
        {
            reasons.Add($"경로를 해석할 수 없습니다: {pathError}");
            return new CodexHomeValidation(CodexHomeStatus.Invalid, 0, signals, reasons, []);
        }

        string probePath = canonical!.Display;

        bool directoryExists;
        try
        {
            directoryExists = Directory.Exists(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reasons.Add("폴더에 접근할 수 없습니다. 권한을 확인해 주세요.");
            return new CodexHomeValidation(CodexHomeStatus.Invalid, 0, signals, reasons, []);
        }

        if (!directoryExists)
        {
            reasons.Add("폴더가 존재하지 않습니다.");
            return new CodexHomeValidation(CodexHomeStatus.Invalid, 0, signals, reasons, []);
        }

        // ── 게이트: sessions\ ─────────────────────────────────────────────
        bool hasSessions = SafeDirectoryExists(Path.Combine(probePath, CodexHomeLayout.SessionsDirectoryName));
        signals.Add(new CodexHomeSignal(
            @"sessions\",
            hasSessions,
            0,
            hasSessions ? "대화 원본 폴더 확인" : "대화 원본 폴더 없음 (필수)"));

        if (!hasSessions)
        {
            reasons.Add(@"sessions\ 폴더가 없습니다. Codex 대화 원본이 저장되는 폴더이므로 필수입니다.");
            return new CodexHomeValidation(CodexHomeStatus.Invalid, 0, signals, reasons, []);
        }

        // ── 가중 신호 ─────────────────────────────────────────────────────
        IReadOnlyList<string> stateDbNames = SafeFindStateDatabases(probePath);
        bool hasStateDb = stateDbNames.Count > 0;
        signals.Add(new CodexHomeSignal(
            CodexHomeLayout.StateDatabaseSearchPattern,
            hasStateDb,
            2,
            hasStateDb
                ? $"{stateDbNames.Count}개 발견: {string.Join(", ", stateDbNames)}"
                : "state DB 없음"));

        bool hasSessionIndex = SafeFileExists(Path.Combine(probePath, CodexHomeLayout.SessionIndexFileName));
        signals.Add(new CodexHomeSignal(
            CodexHomeLayout.SessionIndexFileName,
            hasSessionIndex,
            1,
            hasSessionIndex ? "제목 인덱스 확인" : "제목 인덱스 없음"));

        bool hasConfig = SafeFileExists(Path.Combine(probePath, CodexHomeLayout.ConfigFileName));
        signals.Add(new CodexHomeSignal(
            CodexHomeLayout.ConfigFileName,
            hasConfig,
            1,
            hasConfig ? "Codex 설정 파일 확인" : "Codex 설정 파일 없음"));

        bool hasGlobalState = SafeFileExists(Path.Combine(probePath, CodexHomeLayout.GlobalStateFileName));
        signals.Add(new CodexHomeSignal(
            CodexHomeLayout.GlobalStateFileName,
            hasGlobalState,
            1,
            hasGlobalState ? "데스크톱 앱 상태 확인" : "데스크톱 앱 상태 없음"));

        // 정보용 신호 (점수 0)
        bool hasArchived = SafeDirectoryExists(Path.Combine(probePath, CodexHomeLayout.ArchivedSessionsDirectoryName));
        signals.Add(new CodexHomeSignal(
            @"archived_sessions\",
            hasArchived,
            0,
            hasArchived ? "아카이브 폴더 확인" : "아카이브 폴더 없음 (정상일 수 있음)"));

        int score = 0;
        foreach (CodexHomeSignal signal in signals)
        {
            if (signal.Present)
            {
                score += signal.Weight;
            }
        }

        CodexHomeStatus status = score >= CodexHomeValidation.ValidThreshold
            ? CodexHomeStatus.Valid
            : score >= 1
                ? CodexHomeStatus.Probable
                : CodexHomeStatus.Invalid;

        switch (status)
        {
            case CodexHomeStatus.Valid:
                reasons.Add($"Codex Home으로 확인되었습니다. (신호 점수 {score}/{CodexHomeValidation.MaxScore})");
                break;

            case CodexHomeStatus.Probable:
                reasons.Add($"Codex Home으로 보이지만 일부 구성 요소가 없습니다. (신호 점수 {score}/{CodexHomeValidation.MaxScore})");
                foreach (CodexHomeSignal signal in signals)
                {
                    if (!signal.Present && signal.Weight > 0)
                    {
                        reasons.Add($"없음: {signal.Name}");
                    }
                }

                break;

            default:
                reasons.Add(
                    @"sessions\ 폴더 외에 Codex를 식별할 수 있는 신호가 없습니다. " +
                    "(state_*.sqlite / session_index.jsonl / config.toml / .codex-global-state.json 모두 없음)");
                break;
        }

        return new CodexHomeValidation(status, score, signals, reasons, stateDbNames);
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> SafeFindStateDatabases(string codexHome)
    {
        try
        {
            return CodexHomeLayout.FindStateDatabaseFileNames(codexHome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
