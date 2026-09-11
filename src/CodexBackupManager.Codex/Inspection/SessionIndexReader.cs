using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// <c>session_index.jsonl</c>에서 제목 폴백용 <c>thread_name</c> 맵을 만든다.
/// </summary>
/// <remarks>
/// <para>append-only 로그라 같은 <c>id</c>가 여러 번 나올 수 있다(Phase 0 실측: 136줄/고유 129건).
/// <b>파일에 나중에 나오는 값이 이긴다("last-wins").</b> 이 클래스는 절대 쓰지 않는다(읽기 전용).</para>
/// </remarks>
public static class SessionIndexReader
{
    /// <summary>안전장치: 이보다 큰 파일은 읽지 않는다.</summary>
    public const long MaxFileSizeBytes = 64L * 1024 * 1024;

    /// <summary>
    /// <c>threadId → thread_name</c> 맵을 만든다. 파일이 없거나 읽을 수 없으면 빈 맵.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadTitleMap(string sessionIndexFilePath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var file = new FileInfo(sessionIndexFilePath);
            if (!file.Exists || file.Length > MaxFileSizeBytes)
            {
                return result;
            }

            using FileStream stream = new(sessionIndexFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                (string? id, string? threadName) = TryParseLine(line);
                if (id is not null && threadName is not null)
                {
                    // append-only 로그: 뒤에 나온 값이 이긴다.
                    result[id] = threadName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 부분 결과라도 반환한다.
        }

        return result;
    }

    private static (string? Id, string? ThreadName) TryParseLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            string? name = root.TryGetProperty("thread_name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;

            return (id, name);
        }
        catch (JsonException)
        {
            return (null, null); // 손상된 줄은 건너뛴다.
        }
    }
}
