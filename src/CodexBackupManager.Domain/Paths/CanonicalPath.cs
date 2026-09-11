using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodexBackupManager.Domain.Paths;

/// <summary>
/// Windows Codex 경로 비교용 정규화 값 객체.
/// </summary>
/// <remarks>
/// <para>
/// Phase 0 조사에서 같은 프로젝트 경로가 최소 3가지 표기로 동시에 존재함을 확인했다.
/// </para>
/// <code>
/// C:\_UserProjects\Unreal\Balhwajeom_Project          (rollout session_meta.cwd)
/// \\?\C:\_UserProjects\Unreal\Balhwajeom_Project      (state_*.sqlite threads.cwd — 346/352건)
/// c:\_userprojects\unreal\balhwajeom_project          (config.toml [projects.'...'] 키)
/// </code>
/// <para>
/// 또한 한글 폴더명이 다수 존재하므로(<c>기획</c>, <c>펫 만들기</c>, <c>미디어 분석</c>)
/// Unicode 정규화 형식(NFC/NFD) 차이로 같은 폴더가 다르게 보일 수 있다.
/// </para>
/// <para>
/// 정규화 순서는 다음과 같으며, 이 순서가 바뀌면 결과가 달라진다.
/// </para>
/// <list type="number">
///   <item>Unicode NFC 정규화</item>
///   <item>구분자 통일 (<c>/</c> → <c>\</c>)</item>
///   <item>확장 길이 prefix 제거 (<c>\\?\</c>, <c>\\?\UNC\</c>)</item>
///   <item>루트 판별 및 분리</item>
///   <item>세그먼트 분해: 빈 세그먼트/<c>.</c> 제거, <c>..</c> 어휘적 해소, trailing separator 제거</item>
///   <item>표시용 문자열(<see cref="Display"/>) 조립 — 대소문자 보존</item>
///   <item>비교용 키(<see cref="Value"/>) = <see cref="Display"/>.ToLowerInvariant()</item>
/// </list>
/// <para>
/// <b>원본 입력 문자열(<see cref="Original"/>)은 절대 변형하지 않고 그대로 보존한다.</b>
/// (CLAUDE.md §6 — "원본 경로 문자열은 보존한다")
/// </para>
/// <para>
/// 이 타입은 파일 시스템에 접근하지 않는다. 어휘적(lexical) 정규화만 수행하므로
/// 현재 작업 디렉터리나 심볼릭 링크에 따라 결과가 달라지지 않는다.
/// </para>
/// </remarks>
public sealed class CanonicalPath : IEquatable<CanonicalPath>
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    private CanonicalPath(
        string original,
        string display,
        string value,
        PathRootKind rootKind,
        bool hadExtendedLengthPrefix,
        IReadOnlyList<string> segments)
    {
        Original = original;
        Display = display;
        Value = value;
        RootKind = rootKind;
        HadExtendedLengthPrefix = hadExtendedLengthPrefix;
        Segments = segments;
    }

    /// <summary>호출자가 넘긴 문자열 원본. 어떤 변형도 가하지 않는다.</summary>
    public string Original { get; }

    /// <summary>사람에게 보여주기 위한 정규화 문자열. 대소문자는 보존된다.</summary>
    public string Display { get; }

    /// <summary>비교/그룹화 전용 키. <see cref="Display"/>를 <c>ToLowerInvariant()</c> 처리한 값.</summary>
    public string Value { get; }

    /// <summary>루트 형태.</summary>
    public PathRootKind RootKind { get; }

    /// <summary>입력에 <c>\\?\</c> 또는 <c>\\?\UNC\</c> prefix가 있었는지.</summary>
    public bool HadExtendedLengthPrefix { get; }

    /// <summary>루트를 제외한 경로 세그먼트(대소문자 보존).</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>
    /// 절대 경로인지. <see cref="PathRootKind.DriveRooted"/> 또는 <see cref="PathRootKind.Unc"/>일 때만 참.
    /// 드라이브 상대(<c>C:sub</c>)나 드라이브 없는 루트(<c>\sub</c>)는 현재 프로세스 상태에 의존하므로 거짓.
    /// </summary>
    public bool IsAbsolute => RootKind is PathRootKind.DriveRooted or PathRootKind.Unc;

    /// <summary>
    /// 정규화를 시도한다. 실패 시 예외를 던지지 않는다.
    /// </summary>
    /// <param name="input">원본 경로 문자열.</param>
    /// <param name="result">성공 시 정규화 결과.</param>
    /// <param name="error">실패 이유(사용자 표시용, 경로 원문을 포함하지 않는다).</param>
    public static bool TryCreate(string? input, out CanonicalPath? result, out string? error)
    {
        result = null;
        error = null;

        if (input is null)
        {
            error = "경로가 null입니다.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "경로가 비어 있습니다.";
            return false;
        }

        // 1) Unicode NFC 정규화.
        //    한글 경로가 NFD(자모 분리)로 들어오는 경우를 NFC로 모은다.
        //    Normalize()는 잘못된 서로게이트 쌍에서 ArgumentException을 던질 수 있다.
        string normalized;
        try
        {
            normalized = input.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            error = "경로에 유효하지 않은 문자가 포함되어 있습니다.";
            return false;
        }

        // 제어 문자는 Windows 경로에 올 수 없다.
        foreach (char c in normalized)
        {
            if (char.IsControl(c))
            {
                error = "경로에 제어 문자가 포함되어 있습니다.";
                return false;
            }
        }

        // 2) 구분자 통일.
        string unified = normalized.Replace('/', '\\');

        // 3) 확장 길이 prefix 제거.
        bool hadExtended = false;
        if (unified.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // \\?\UNC\server\share  ->  \\server\share
            hadExtended = true;
            unified = @"\\" + unified.Substring(ExtendedUncPrefix.Length);
        }
        else if (unified.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            // \\?\C:\dir  ->  C:\dir
            hadExtended = true;
            unified = unified.Substring(ExtendedPrefix.Length);
        }

        if (unified.Length == 0)
        {
            error = "prefix를 제거한 뒤 남은 경로가 없습니다.";
            return false;
        }

        // 4) 루트 판별.
        PathRootKind rootKind;
        string rootPrefix;   // Display 조립 시 세그먼트 앞에 붙는 부분
        string rootOnly;     // 세그먼트가 없을 때의 Display
        string remainder;

        if (unified.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            rootKind = PathRootKind.Device;
            rootPrefix = DevicePrefix;
            rootOnly = DevicePrefix;
            remainder = unified.Substring(DevicePrefix.Length);
        }
        else if (unified.StartsWith(@"\\", StringComparison.Ordinal))
        {
            rootKind = PathRootKind.Unc;
            rootPrefix = @"\\";
            rootOnly = @"\\";
            remainder = unified.Substring(2);
        }
        else if (unified.Length >= 2 && IsDriveLetter(unified[0]) && unified[1] == ':')
        {
            if (unified.Length == 2)
            {
                // "C:" → 드라이브 루트로 정규화한다. ("C:" 와 "C:\" 는 같은 폴더를 가리킨다)
                rootKind = PathRootKind.DriveRooted;
                rootPrefix = unified.Substring(0, 2) + @"\";
                rootOnly = rootPrefix;
                remainder = string.Empty;
            }
            else if (unified[2] == '\\')
            {
                rootKind = PathRootKind.DriveRooted;
                rootPrefix = unified.Substring(0, 2) + @"\";
                rootOnly = rootPrefix;
                remainder = unified.Substring(3);
            }
            else
            {
                // "C:sub" — 드라이브 상대 경로. 현재 디렉터리에 의존하므로 절대 경로로 보지 않는다.
                rootKind = PathRootKind.DriveRelative;
                rootPrefix = unified.Substring(0, 2);
                rootOnly = rootPrefix;
                remainder = unified.Substring(2);
            }
        }
        else if (unified[0] == '\\')
        {
            rootKind = PathRootKind.RootedWithoutDrive;
            rootPrefix = @"\";
            rootOnly = @"\";
            remainder = unified.Substring(1);
        }
        else
        {
            rootKind = PathRootKind.Relative;
            rootPrefix = string.Empty;
            rootOnly = string.Empty;
            remainder = unified;
        }

        // 5) 세그먼트 분해.
        //    빈 세그먼트(중복 구분자) 제거, "." 제거, ".." 어휘적 해소, trailing separator 자동 제거.
        var segments = new List<string>();
        foreach (string raw in remainder.Split('\\'))
        {
            if (raw.Length == 0 || raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                if (rootKind == PathRootKind.Relative)
                {
                    // 상대 경로에서는 앞으로 더 올라갈 수 있으므로 ".."를 유지한다.
                    segments.Add("..");
                    continue;
                }

                // 루트 위로는 올라갈 수 없다. Windows와 동일하게 무시한다.
                continue;
            }

            // 각 세그먼트 끝의 공백/점은 Windows가 허용하지 않는다. 정규화 대신 거부한다.
            if (raw.EndsWith(" ", StringComparison.Ordinal) || raw.EndsWith(".", StringComparison.Ordinal))
            {
                error = "경로 구성요소가 공백 또는 점으로 끝납니다.";
                return false;
            }

            if (ContainsInvalidSegmentChar(raw))
            {
                error = "경로 구성요소에 사용할 수 없는 문자가 포함되어 있습니다.";
                return false;
            }

            segments.Add(raw);
        }

        if (rootKind == PathRootKind.Unc && segments.Count == 0)
        {
            error = "UNC 경로에 서버 이름이 없습니다.";
            return false;
        }

        if (rootKind == PathRootKind.Relative && segments.Count == 0)
        {
            error = "정규화 후 남은 경로 구성요소가 없습니다.";
            return false;
        }

        // 6) Display 조립. 대소문자 보존.
        string display = segments.Count == 0
            ? rootOnly
            : rootPrefix + string.Join(@"\", segments);

        // 7) 비교 키.
        string value = display.ToLowerInvariant();

        result = new CanonicalPath(
            original: input,
            display: display,
            value: value,
            rootKind: rootKind,
            hadExtendedLengthPrefix: hadExtended,
            segments: segments);
        return true;
    }

    /// <summary>정규화한다. 실패 시 <see cref="ArgumentException"/>.</summary>
    public static CanonicalPath Create(string? input)
    {
        if (TryCreate(input, out CanonicalPath? result, out string? error))
        {
            return result!;
        }

        throw new ArgumentException(error, nameof(input));
    }

    /// <summary>두 경로 문자열이 같은 위치를 가리키는지 어휘적으로 비교한다.</summary>
    public static bool AreSameLocation(string? left, string? right)
    {
        if (!TryCreate(left, out CanonicalPath? a, out _))
        {
            return false;
        }

        if (!TryCreate(right, out CanonicalPath? b, out _))
        {
            return false;
        }

        return a!.Equals(b);
    }

    private static bool IsDriveLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool ContainsInvalidSegmentChar(string segment)
    {
        // Windows 파일명 금지 문자. '\' 와 '/' 는 이미 구분자로 소비되었다.
        foreach (char c in segment)
        {
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool Equals(CanonicalPath? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CanonicalPath);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Display;

    /// <summary>비교 키 기준 동등 비교자. 딕셔너리/그룹화 키로 사용한다.</summary>
    public static IEqualityComparer<CanonicalPath> Comparer { get; } = new CanonicalPathComparer();

    private sealed class CanonicalPathComparer : IEqualityComparer<CanonicalPath>
    {
        public bool Equals(CanonicalPath? x, CanonicalPath? y)
            => ReferenceEquals(x, y) || (x is not null && x.Equals(y));

        public int GetHashCode(CanonicalPath obj) => obj.GetHashCode();
    }
}
