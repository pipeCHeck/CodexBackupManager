using System;
using System.IO;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// rollout 파일 개수만 센다. <b>파일을 열지도, 파싱하지도 않는다.</b>
/// </summary>
/// <remarks>
/// Phase 1 범위는 "개수"까지다. JSONL 파싱은 Phase 2, <c>.zst</c> 압축 해제는 그 이후다.
/// 다만 압축 파일이 존재하는지 여부는 지금 알아두는 것이 유용하므로 개수만 따로 센다.
/// (Phase 0 조사 기준 이 PC에는 <c>.zst</c>가 0개였지만, 공식 Codex는 오래된 rollout을 Zstandard로 압축한다)
/// </remarks>
public static class SessionFileCounter
{
    /// <summary>세기 결과.</summary>
    /// <param name="JsonlCount"><c>*.jsonl</c> 개수.</param>
    /// <param name="CompressedCount"><c>*.jsonl.zst</c> 개수.</param>
    /// <param name="DirectoryExists">대상 디렉터리가 존재했는지.</param>
    /// <param name="Truncated">열거 중 접근 오류로 일부를 세지 못했는지.</param>
    public sealed record Counts(int JsonlCount, int CompressedCount, bool DirectoryExists, bool Truncated);

    /// <summary>
    /// 디렉터리 아래의 rollout 파일 개수를 센다.
    /// </summary>
    /// <param name="directory">대상 디렉터리.</param>
    /// <param name="recursive">하위 디렉터리까지 내려갈지. <c>sessions\</c>는 <c>YYYY\MM\DD</c> 구조라 참.</param>
    public static Counts Count(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            return new Counts(0, 0, DirectoryExists: false, Truncated: false);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        int jsonl = 0;
        int compressed = 0;
        bool truncated = false;

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl*", options))
            {
                // 와일드카드가 넓으므로 확장자를 직접 확인한다. (*.jsonl.tmp 등을 제외)
                if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    jsonl++;
                }
                else if (path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase))
                {
                    compressed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            truncated = true;
        }

        return new Counts(jsonl, compressed, DirectoryExists: true, Truncated: truncated);
    }

    /// <summary>
    /// 텍스트 파일의 줄 수를 센다. 내용은 보관하지 않는다.
    /// </summary>
    /// <remarks>
    /// <c>session_index.jsonl</c>은 append-only 로그이며 중복 항목이 존재한다
    /// (Phase 0 실측: 136줄 / 고유 129건). Phase 1에서는 줄 수만 알려준다.
    /// <b>줄 내용(대화 제목)은 읽지 않는다.</b>
    /// </remarks>
    public static int? TryCountLines(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);

            int lines = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    lines++;
                }
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
