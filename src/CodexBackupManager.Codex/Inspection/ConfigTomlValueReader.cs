using System;
using System.Collections.Generic;
using System.IO;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>config.toml</c>에서 <b>지정한 키의 값만</b> 뽑아내는 최소 리더.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1에서 필요한 값은 두 개뿐이다.
/// </para>
/// <code>
/// BROWSER_USE_CODEX_APP_VERSION = "26.903.61454"
/// CODEX_CLI_PATH = 'C:\Users\User\AppData\Local\OpenAI\Codex\bin\...\codex.exe'
/// </code>
/// <para>
/// 그래서 전체 TOML 파서를 NuGet으로 들이지 않고 줄 단위 스캐너로 처리한다. 이유:
/// </para>
/// <list type="bullet">
///   <item>의존성 0 (CLAUDE.md §2.1 — 외부 의존 최소화)</item>
///   <item>
///     모르는 TOML 구문에 영향을 받지 않는다. 실제 <c>config.toml</c>에는
///     <c>'\\?\C:\...'</c> 같은 literal string과 여러 줄 배열이 들어 있다.
///     문서 전체를 이해하지 않으므로 이런 구문에서 깨지지 않는다.
///   </item>
/// </list>
/// <para>
/// <b>한계(문서화된 의도):</b> 이 리더는 <c>[table]</c> 구조를 해석하지 않는다.
/// 따라서 <b>파일 전체에서 유일한 키</b>에만 사용해야 한다.
/// Phase 0 실측으로 위 두 키가 각각 1회만 등장함을 확인했다.
/// <c>[projects.'&lt;path&gt;']</c> 같은 테이블 구조가 필요해지는 시점(Phase 2 이후)에는
/// 정식 TOML 파서로 교체한다.
/// </para>
/// </remarks>
public static class ConfigTomlValueReader
{
    /// <summary>Codex Desktop 버전이 담긴 키.</summary>
    public const string DesktopVersionKey = "BROWSER_USE_CODEX_APP_VERSION";

    /// <summary>Codex CLI 실행 파일 경로가 담긴 키.</summary>
    public const string CliPathKey = "CODEX_CLI_PATH";

    /// <summary>
    /// 지정한 키들의 값을 읽는다. 파일이 없거나 읽을 수 없으면 빈 딕셔너리.
    /// </summary>
    /// <param name="configTomlPath"><c>config.toml</c> 전체 경로.</param>
    /// <param name="keys">찾을 키 이름들.</param>
    public static IReadOnlyDictionary<string, string> ReadValues(
        string configTomlPath,
        params string[] keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return result;
        }

        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);

        IEnumerable<string> lines;
        try
        {
            if (!File.Exists(configTomlPath))
            {
                return result;
            }

            lines = File.ReadLines(configTomlPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        try
        {
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '[')
                {
                    continue;
                }

                int equals = trimmed.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = trimmed[..equals].Trim();
                if (!wanted.Contains(key) || result.ContainsKey(key))
                {
                    continue;
                }

                if (TryParseScalarString(trimmed[(equals + 1)..].Trim(), out string? value))
                {
                    result[key] = value!;
                }

                if (result.Count == wanted.Count)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 부분 결과라도 반환한다.
        }

        return result;
    }

    /// <summary>
    /// TOML의 basic string(<c>"..."</c>) 또는 literal string(<c>'...'</c>) 하나를 해석한다.
    /// 그 외 형태(배열, 숫자, 여러 줄 문자열)는 지원하지 않고 <c>false</c>.
    /// </summary>
    internal static bool TryParseScalarString(string rawValue, out string? value)
    {
        value = null;
        if (rawValue.Length < 2)
        {
            return false;
        }

        char quote = rawValue[0];
        if (quote != '"' && quote != '\'')
        {
            return false;
        }

        // 여러 줄 문자열("""/''')은 지원 대상 밖.
        if (rawValue.Length >= 6 && rawValue[1] == quote && rawValue[2] == quote)
        {
            return false;
        }

        int closing = rawValue.IndexOf(quote, 1);
        if (closing < 0)
        {
            return false;
        }

        string inner = rawValue[1..closing];

        if (quote == '\'')
        {
            // literal string: 이스케이프 없음. '\\?\C:\...' 를 그대로 쓴다.
            value = inner;
            return true;
        }

        // basic string: 최소한의 이스케이프만 처리한다.
        value = Unescape(inner);
        return true;
    }

    private static string Unescape(string inner)
    {
        if (!inner.Contains('\\', StringComparison.Ordinal))
        {
            return inner;
        }

        var builder = new System.Text.StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            char next = inner[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => next,
            });
        }

        return builder.ToString();
    }
}
