using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;

namespace CodexBackupManager.Codex.Sessions;

/// <summary>
/// rollout 파일에서 <c>session_meta</c> 한 줄을 찾아 <see cref="SessionMetadata"/>로 파싱한다.
/// </summary>
/// <remarks>
/// <para>
/// <c>session_meta</c>는 보통 <c>ordinal:0</c>(첫 줄)이지만, 혹시 그렇지 않거나 앞에
/// 알 수 없는 줄이 섞여 있어도 세션 전체 파싱이 실패하지 않도록 처음 몇 줄만 훑어서 찾는다.
/// </para>
/// <para>
/// <b>확인되지 않은 필드는 추측하지 않는다.</b> 있는 그대로 없으면 <c>null</c>이다.
/// 손상된/알 수 없는 줄은 건너뛰고 계속 진행하며, 예외를 던지지 않는다.
/// </para>
/// </remarks>
public static class CodexSessionParser
{
    /// <summary><c>session_meta</c>를 찾기 위해 앞에서부터 훑는 최대 줄 수.</summary>
    public const int MaxLinesToSearch = 5;

    /// <summary>파싱 결과. 둘 다 실패할 수도, <see cref="Warning"/>만 있을 수도 있다.</summary>
    /// <param name="Metadata">찾았으면 값, 못 찾았으면 <c>null</c>.</param>
    /// <param name="Warning">문제가 있었다면 사용자 원문 없이 남기는 설명.</param>
    public sealed record ParseResult(SessionMetadata? Metadata, string? Warning);

    /// <summary>rollout 파일 하나에서 <c>session_meta</c>를 읽는다.</summary>
    public static ParseResult ParseSessionMetadata(RolloutFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            foreach (string line in RolloutStreamReader.ReadFirstLines(file.FullPath, file.Kind, MaxLinesToSearch))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                SessionMetadata? parsed = TryParseSessionMetaLine(line, file);
                if (parsed is not null)
                {
                    return new ParseResult(parsed, null);
                }
            }

            return new ParseResult(
                null,
                $"{file.FileName}: 처음 {MaxLinesToSearch}줄 안에서 session_meta를 찾지 못했습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ParseResult(null, $"{file.FileName}: 파일을 읽을 수 없습니다 ({ex.GetType().Name}).");
        }
    }

    private static SessionMetadata? TryParseSessionMetaLine(string line, RolloutFileReference file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // 잘린 줄 / 손상된 줄. 이 줄만 건너뛰고 다음 줄을 계속 찾는다.
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                GetString(root, "type") != "session_meta" ||
                !root.TryGetProperty("payload", out JsonElement payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? threadId = GetString(payload, "session_id") ?? GetString(payload, "id");
            if (threadId is null)
            {
                return null;
            }

            DateTimeOffset? timestamp = ParseTimestamp(GetString(payload, "timestamp") ?? GetString(root, "timestamp"));

            GitInfo? git = null;
            if (payload.TryGetProperty("git", out JsonElement gitElement) && gitElement.ValueKind == JsonValueKind.Object)
            {
                git = new GitInfo(
                    GetString(gitElement, "commit_hash"),
                    GetString(gitElement, "branch"),
                    GetString(gitElement, "repository_url"));
            }

            HistoryBaseReference? historyBase = null;
            if (payload.TryGetProperty("history_base", out JsonElement hb) && hb.ValueKind == JsonValueKind.Object)
            {
                string? parentThreadId = GetString(hb, "thread_id");
                if (parentThreadId is not null)
                {
                    historyBase = new HistoryBaseReference(
                        parentThreadId,
                        GetInt64(hb, "end_ordinal_exclusive"),
                        GetInt64(hb, "end_byte_offset"));
                }
            }

            return new SessionMetadata
            {
                ThreadId = threadId,
                Timestamp = timestamp,
                Cwd = GetString(payload, "cwd"),
                Originator = GetString(payload, "originator"),
                CliVersion = GetString(payload, "cli_version"),
                Source = GetString(payload, "source"),
                ThreadSource = GetString(payload, "thread_source"),
                ModelProvider = GetString(payload, "model_provider"),
                Model = GetString(payload, "model"),
                HistoryMode = GetString(payload, "history_mode"),
                Git = git,
                ForkedFromId = GetString(payload, "forked_from_id"),
                ForkedFromOrdinalExclusive = GetInt64(payload, "forked_from_ordinal_exclusive"),
                HistoryBase = historyBase,
                SourceFilePath = file.FullPath,
                IsArchived = file.IsArchived,
            };
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
        => raw is not null &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long l) => l,
            _ => null,
        };
    }
}
