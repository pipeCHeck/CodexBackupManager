using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.App.Services;

/// <summary>
/// (Phase 9_5-07) [새 폴더 만들기] 결과. 성공이면 <see cref="FolderPath"/>, 실패면 <see cref="FailureReason"/>(예외 형식 이름 정도, 경로 없음).
/// </summary>
/// <param name="FolderPath">새로 만든 폴더(완전히 비어 있는 새 폴더).</param>
/// <param name="CreatedBase">기준 폴더도 이번에 새로 만들었는지(기준 폴더는 정리 대상이 아니다).</param>
/// <param name="FailureReason">실패 사유. 성공이면 <c>null</c>.</param>
public sealed record NewProjectFolderResult(string? FolderPath, bool CreatedBase, string? FailureReason)
{
    /// <summary>성공했는지.</summary>
    public bool Success => FolderPath is not null;

    /// <summary>실패 결과.</summary>
    public static NewProjectFolderResult Failed(string reason) => new(null, false, reason);
}

/// <summary>
/// (Phase 9_5-07 ~ 09) 가져오기 화면의 [새 폴더 만들기]: 폴더 이름 정리, 기준 폴더 검증, 새 폴더 만들기, 빈 폴더만 지우기.
/// </summary>
/// <remarks>
/// <para>
/// Codex 데이터가 아니라 사용자 폴더 아래 빈 폴더 하나를 만들 뿐이라 RestoreExecutor 파이프라인 밖에서 한다. 대신:
/// 기존 폴더를 재사용하지 않고(같은 이름이면 " (2)"…), 지울 때는 비재귀 <see cref="Directory.Delete(string, bool)"/>만 쓰며
/// 지우기 직전에 비어 있는지 다시 확인한다. 이 클래스는 로그를 남기지 않는다(경로·이름을 로그에 쓰지 않기 위함).
/// </para>
/// </remarks>
public static class NewProjectFolderService
{
    /// <summary>이름이 비었을 때 쓰는 폴더 이름.</summary>
    public const string EmptyNameFallback = "Codex 프로젝트";

    /// <summary>폴더 이름 최대 길이(UTF-16 코드 단위, 번호 접미사 전).</summary>
    public const int MaxNameLength = 80;

    /// <summary>같은 이름이 있을 때 붙이는 번호의 상한(" (2)" ~ " (999)").</summary>
    public const int MaxSuffixNumber = 999;

    private static readonly HashSet<char> ExtraInvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(i => "COM" + i))
            .Concat(Enumerable.Range(1, 9).Select(i => "LPT" + i)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 기본 기준 폴더: <c>문서\ChatGPT</c>(<see cref="Environment.SpecialFolder.MyDocuments"/> — OneDrive로 옮겨진 문서 폴더도 이 API가 알려 주는 곳).
    /// 근거는 관찰이다(이 PC 등록 프로젝트 여러 개와 다른 PC 백업의 루트가 그 아래였다). 공식 확인은 아니다.
    /// </summary>
    public static string DefaultBase()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChatGPT");

    /// <summary>
    /// 폴더 이름 정리(순수 함수): 금지 문자와 제어 문자 → <c>_</c>, 앞뒤 공백·끝의 점 제거, 예약 이름이면 뒤에 <c>_</c>,
    /// 80자(서로게이트 쌍을 가르지 않음)로 자른 뒤 끝 공백·점 다시 제거, 비면 <see cref="EmptyNameFallback"/>.
    /// </summary>
    public static string SanitizeFolderName(string? name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder((name ?? string.Empty).Length);
        foreach (char c in name ?? string.Empty)
        {
            builder.Append(char.IsControl(c) || ExtraInvalidChars.Contains(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = TrimEnds(builder.ToString());
        if (result.Length > MaxNameLength)
        {
            int cut = MaxNameLength;
            if (char.IsHighSurrogate(result[cut - 1]))
            {
                cut--; // 쌍의 앞쪽만 남기지 않는다
            }

            result = TrimEnds(result[..cut]);
        }

        if (result.Length == 0)
        {
            return EmptyNameFallback;
        }

        // 예약 이름은 확장자가 붙어도 해당한다(예: "con.txt"). 점 앞부분(공백 제거)으로 판단하고, "_"는 그 앞부분 바로 뒤에 붙인다
        // ("CON" → "CON_", "con.txt" → "con_.txt" — 끝에 붙이면 "con.txt_"도 여전히 예약 이름이다).
        int dot = result.IndexOf('.');
        string stemRaw = dot >= 0 ? result[..dot] : result;
        string stem = stemRaw.TrimEnd(' ');
        return ReservedNames.Contains(stem) ? stem + "_" + result[stemRaw.Length..] : result;
    }

    private static string TrimEnds(string value) => value.Trim().TrimEnd('.', ' ').Trim();

    /// <summary>
    /// 기준 폴더로 쓸 수 없는 이유. 쓸 수 있으면 <c>null</c>. 거부: 절대 경로가 아님, Codex Home 자체나 그 안,
    /// 이 앱의 데이터 폴더(설정·로그·Snapshot 루트 등) 자체나 그 안(canonical 비교).
    /// </summary>
    /// <param name="basePath">기준 폴더 후보.</param>
    /// <param name="codexHome">지금 연결된 Codex Home(없으면 <c>null</c>).</param>
    /// <param name="protectedRoots">이 앱의 데이터 폴더들.</param>
    public static string? ValidateBase(string? basePath, string? codexHome, IEnumerable<string> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(protectedRoots);
        if (string.IsNullOrWhiteSpace(basePath) ||
            !CanonicalPath.TryCreate(basePath, out CanonicalPath? canonical, out _) ||
            canonical!.RootKind is not (PathRootKind.DriveRooted or PathRootKind.Unc))
        {
            return "새 폴더 위치는 드라이브부터 시작하는 전체 경로여야 합니다.";
        }

        if (codexHome is not null && IsSameOrInside(canonical, codexHome))
        {
            return "Codex 데이터 폴더 안에는 새 폴더를 만들 수 없습니다. 다른 위치를 골라 주세요.";
        }

        if (protectedRoots.Any(root => IsSameOrInside(canonical, root)))
        {
            return "이 프로그램의 데이터 폴더 안에는 새 폴더를 만들 수 없습니다. 다른 위치를 골라 주세요.";
        }

        return null;
    }

    private static bool IsSameOrInside(CanonicalPath candidate, string root)
    {
        if (!CanonicalPath.TryCreate(root, out CanonicalPath? canonicalRoot, out _))
        {
            return false;
        }

        string value = candidate.Value;
        string rootValue = canonicalRoot!.Value.TrimEnd('\\');
        return string.Equals(value, rootValue, StringComparison.Ordinal) ||
               value.StartsWith(rootValue + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="basePath"/> 아래에 <paramref name="displayName"/>을 정리한 이름으로 새 폴더를 만든다. 기준 폴더가 없으면 만든다.
    /// 같은 이름의 파일이나 폴더가 있으면 " (2)", " (3)" …(상한 <see cref="MaxSuffixNumber"/>)을 붙인다. 기존 폴더를 재사용하지 않는다.
    /// </summary>
    public static NewProjectFolderResult Create(string basePath, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        try
        {
            if (File.Exists(basePath))
            {
                return NewProjectFolderResult.Failed("새 폴더 위치가 파일입니다");
            }

            bool createdBase = !Directory.Exists(basePath);
            if (createdBase)
            {
                Directory.CreateDirectory(basePath);
            }

            string name = SanitizeFolderName(displayName);
            for (int n = 1; n <= MaxSuffixNumber; n++)
            {
                string candidate = Path.Combine(basePath, n == 1 ? name : $"{name} ({n})");
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                return new NewProjectFolderResult(candidate, createdBase, null);
            }

            return NewProjectFolderResult.Failed("같은 이름의 폴더가 너무 많습니다");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NewProjectFolderResult.Failed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// (Phase 9_5-09) 폴더가 <b>완전히 비어 있으면</b>(파일·하위 폴더 0개) 비재귀로 지운다. 지우기 직전에 비어 있는지 다시 확인한다.
    /// 지웠으면 <c>true</c>. 비어 있지 않거나, 없거나, 지우지 못했으면 <c>false</c>(예외를 던지지 않는다).
    /// </summary>
    public static bool TryDeleteIfEmpty(string folderPath, out bool failed)
    {
        failed = false;
        try
        {
            if (!Directory.Exists(folderPath) || Directory.EnumerateFileSystemEntries(folderPath).Any())
            {
                return false;
            }

            Directory.Delete(folderPath, recursive: false); // 재귀 삭제는 쓰지 않는다 — 그 사이 무언가 생기면 여기서 실패한다
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = true;
            return false;
        }
    }
}
