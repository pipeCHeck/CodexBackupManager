using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexBackupManager.Codex.Locating;

/// <summary>
/// Codex Home 안에서 우리가 다루는 파일/폴더 이름을 한 곳에 모아둔다.
/// </summary>
/// <remarks>
/// <b><c>state_5.sqlite</c> 같은 구체적 파일명을 하드코딩하지 않는다.</b>
/// Phase 0에서 state DB generation이 버전에 따라 올라가는 것을 확인했으므로
/// (`_sqlx_migrations` 52개, `sqlite\` 하위에 구버전 세트 존재) 항상 패턴으로 찾는다.
/// </remarks>
public static partial class CodexHomeLayout
{
    /// <summary>대화 원본 JSONL이 들어 있는 디렉터리 이름.</summary>
    public const string SessionsDirectoryName = "sessions";

    /// <summary>아카이브된 대화 JSONL 디렉터리 이름.</summary>
    public const string ArchivedSessionsDirectoryName = "archived_sessions";

    /// <summary>제목 보조 인덱스 파일 이름.</summary>
    public const string SessionIndexFileName = "session_index.jsonl";

    /// <summary>Codex 설정 파일 이름.</summary>
    public const string ConfigFileName = "config.toml";

    /// <summary>Electron 데스크톱 앱 상태 파일 이름.</summary>
    public const string GlobalStateFileName = ".codex-global-state.json";

    /// <summary>state DB 검색 패턴.</summary>
    public const string StateDatabaseSearchPattern = "state_*.sqlite";

    /// <summary>rollout JSONL 검색 패턴.</summary>
    public const string RolloutSearchPattern = "*.jsonl";

    /// <summary>압축된 rollout 검색 패턴. Phase 1에서는 개수만 센다.</summary>
    public const string CompressedRolloutSearchPattern = "*.jsonl.zst";

    /// <summary>Codex가 thread를 쓰는 동안 사용하는 lock 디렉터리 이름.</summary>
    public const string ThreadWriterLocksDirectoryName = "thread-writer-locks";

    [GeneratedRegex(@"^state_(\d+)\.sqlite$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateDatabaseNameRegex();

    /// <summary>
    /// Codex Home 루트에서 <c>state_*.sqlite</c> 파일명을 찾는다.
    /// </summary>
    /// <remarks>
    /// <see cref="Directory.EnumerateFiles(string, string)"/>의 와일드카드는 Windows에서
    /// 8.3 단축 이름 때문에 의도보다 넓게 매칭될 수 있으므로, 결과를 확장자로 한 번 더 걸러낸다.
    /// </remarks>
    /// <returns>파일명(경로 제외) 목록. generation 내림차순 → 파일명 순.</returns>
    public static IReadOnlyList<string> FindStateDatabaseFileNames(string codexHome)
    {
        if (!Directory.Exists(codexHome))
        {
            return [];
        }

        var found = new List<(string Name, int? Generation)>();
        foreach (string full in Directory.EnumerateFiles(codexHome, StateDatabaseSearchPattern, SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(full);

            // 8.3 단축 이름 매칭 방어: 반드시 ".sqlite"로 끝나야 한다.
            if (!name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add((name, TryParseGeneration(name)));
        }

        found.Sort(static (a, b) =>
        {
            int byGeneration = (b.Generation ?? -1).CompareTo(a.Generation ?? -1);
            return byGeneration != 0
                ? byGeneration
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var names = new List<string>(found.Count);
        foreach ((string name, int? _) in found)
        {
            names.Add(name);
        }

        return names;
    }

    /// <summary><c>state_&lt;N&gt;.sqlite</c>에서 N을 파싱한다. 패턴 불일치 시 <c>null</c>.</summary>
    public static int? TryParseGeneration(string stateDatabaseFileName)
    {
        Match match = StateDatabaseNameRegex().Match(stateDatabaseFileName);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out int generation) ? generation : null;
    }
}
