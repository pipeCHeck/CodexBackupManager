using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Conversation;

/// <summary>
/// rollout 파일 하나에서 Viewer에 표시할 User/Assistant 메시지를 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>authoritative source 정책</b>(docs/codex-storage-format.md §3, 실제 데이터로 검증됨):
/// 이 파일에 <c>event_msg</c>/<c>item_completed</c>(UI 레벨) 메시지가 하나라도 있으면 그것만 쓰고
/// 같은 파일의 <c>response_item</c>(API wire 레벨)은 전부 버린다. <c>item_completed</c>가
/// 하나도 없을 때만 <c>response_item</c>을 폴백으로 쓴다. 한 파일 안에서 두 소스를 섞어 쓰지
/// 않으므로 같은 메시지가 중복 표시되지 않는다.
/// </para>
/// <para>
/// <b>response_item 폴백은 <c>role</c>만으로 안전하지 않다.</b> 실제 <c>.codex</c> 데이터에서
/// <c>role:"user"</c>인 <c>response_item</c>이 <c>&lt;recommended_plugins&gt;</c> 같은
/// 시스템 주입 문자열을 담고 있는 사례를 확인했다(진짜 사용자 입력이 아니다). 그래서 폴백 경로는
/// <c>role:"developer"</c>를 숨기는 것에 더해 이런 주입 envelope도 숨긴다.
/// </para>
/// <para>
/// <b>고정 문자열 목록만으로는 부족함이 실측으로 드러났다(Phase 3 pre-commit audit).</b>
/// 알려진 <c>&lt;app-context&gt;</c>/<c>&lt;recommended_plugins&gt;</c>/<c>&lt;turn_aborted&gt;</c>/
/// <c>&lt;multi_agent_mode&gt;</c> 외에도 <c>&lt;environment_context&gt;</c>가 추가로 발견됐다.
/// 그래서 <see cref="InjectedTagPattern"/>로 <b>"소문자/밑줄 태그로 즉시 시작하는" 구조</b>를
/// 함께 본다 — 새 마커가 나와도 이름을 미리 몰라도 걸러진다. 속성이 있는 태그(<c>&lt;a href=...&gt;</c>)나
/// 대문자/숫자로 시작하는 것은 매치하지 않아 일반 텍스트를 과도하게 숨기지 않는다.
/// </para>
/// <para>
/// <c>item.content</c>/<c>payload.content</c> 배열의 각 원소에서 <b>타입 이름을 따지지 않고</b>
/// 문자열 <c>text</c> 속성이 있으면 그대로 합친다 — 실측 결과 <c>UserMessage</c>는 <c>type:"text"</c>,
/// <c>AgentMessage</c>는 <c>type:"Text"</c>로 대소문자가 다르게 나타났고, 이런 표기 차이에
/// 흔들리지 않기 위한 의도적인 선택이다.
/// </para>
/// <para>Reasoning의 <c>encrypted_content</c>는 절대 읽거나 복호화를 시도하지 않는다.</para>
/// </remarks>
public static class ConversationItemParser
{
    /// <summary>
    /// 실측으로 확인된 대표적인 주입 마커. <see cref="IsInjectedContent"/>는 이 목록에 없는 새 마커도
    /// <see cref="InjectedTagPattern"/> 구조 판정으로 걸러낸다 — 이 목록은 예시/문서화 목적이 크다.
    /// </summary>
    private static readonly string[] InjectedContentMarkers =
    [
        "<app-context>",
        "<recommended_plugins>",
        "<turn_aborted>",
        "<multi_agent_mode>",
        "<environment_context>",
    ];

    /// <summary>
    /// "소문자로 시작해 소문자/숫자/밑줄만 쓰는 태그 이름으로, 속성 없이 즉시 시작"하는지 판정한다.
    /// Codex의 시스템 주입 envelope들이 공통으로 이 모양이다. 사용자가 실제로 타이핑한 텍스트가
    /// 우연히 이 모양으로 시작할 가능성은 매우 낮다(대문자 태그, 속성이 있는 HTML 등은 매치되지 않는다).
    /// </summary>
    private static readonly Regex InjectedTagPattern = new(@"^<[a-z][a-z0-9_]*>", RegexOptions.Compiled);

    /// <summary>파싱 결과.</summary>
    /// <param name="Messages">
    /// 이 파일에서 최종적으로 채택된 메시지(시간순). event_msg가 있으면 그것, 없으면 response_item 폴백.
    /// </param>
    /// <param name="UsedFallback"><c>response_item</c> 폴백을 사용했는지.</param>
    /// <param name="Warning">파일을 읽을 수 없었을 때의 이유. 정상 처리되면 <c>null</c>.</param>
    public sealed record ParseResult(IReadOnlyList<ConversationMessage> Messages, bool UsedFallback, string? Warning)
    {
        /// <summary>빈 결과.</summary>
        public static ParseResult Empty(string? warning = null) => new([], false, warning);
    }

    /// <summary>파일 전체를 읽는다.</summary>
    public static ParseResult ParseFile(RolloutFileReference file, CancellationToken cancellationToken = default)
        => Parse(file, maxOrdinalExclusive: null, maxByteOffsetExclusive: null, cancellationToken);

    /// <summary>
    /// <c>ordinal</c>이 <paramref name="maxOrdinalExclusive"/> 미만인 줄까지만 읽는다.
    /// <c>history_base.end_ordinal_exclusive</c>로 부모 rollout을 잘라 상속할 때 쓴다.
    /// ordinal은 파일 안에서 단조 증가하므로(실측 확인) 경계를 넘는 순간 읽기를 멈춘다.
    /// </summary>
    public static ParseResult ParseFileWithOrdinalCutoff(
        RolloutFileReference file, long maxOrdinalExclusive, CancellationToken cancellationToken = default)
        => Parse(file, maxOrdinalExclusive, maxByteOffsetExclusive: null, cancellationToken);

    /// <summary>
    /// 파일에서 <paramref name="maxByteOffsetExclusive"/> 바이트 이전까지만 읽는다.
    /// <c>ordinal</c> 정보를 쓸 수 없을 때만 쓰는 폴백이다(클래스 remarks 참고).
    /// </summary>
    public static ParseResult ParseFileWithByteOffsetCutoff(
        RolloutFileReference file, long maxByteOffsetExclusive, CancellationToken cancellationToken = default)
        => Parse(file, maxOrdinalExclusive: null, maxByteOffsetExclusive, cancellationToken);

    private static ParseResult Parse(
        RolloutFileReference file,
        long? maxOrdinalExclusive,
        long? maxByteOffsetExclusive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var eventMessages = new List<ConversationMessage>();
        var responseItemMessages = new List<ConversationMessage>();

        try
        {
            if (maxByteOffsetExclusive is { } byteCutoff)
            {
                foreach ((string line, _, long endOffset) in RolloutStreamReader.ReadLinesWithByteOffsets(file.FullPath, file.Kind, cancellationToken))
                {
                    if (endOffset > byteCutoff)
                    {
                        break;
                    }

                    ProcessLine(line, eventMessages, responseItemMessages, maxOrdinalExclusive: null);
                }
            }
            else
            {
                foreach (string line in RolloutStreamReader.ReadLines(file.FullPath, file.Kind, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    bool exceededCutoff = ProcessLine(line, eventMessages, responseItemMessages, maxOrdinalExclusive);
                    if (exceededCutoff)
                    {
                        break; // ordinal은 단조 증가하므로(실측 확인) 여기서 멈춰도 안전하다.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ParseResult.Empty($"{file.FileName}: 파일을 읽을 수 없습니다 ({ex.GetType().Name}).");
        }

        return eventMessages.Count > 0
            ? new ParseResult(eventMessages, UsedFallback: false, null)
            : new ParseResult(responseItemMessages, UsedFallback: responseItemMessages.Count > 0, null);
    }

    /// <summary>
    /// 한 줄을 파싱해 해당 리스트에 메시지를 추가한다.
    /// <paramref name="maxOrdinalExclusive"/>를 넘는 줄은 <b>추가하지 않고</b> 참을 돌려준다
    /// (호출자가 그 신호로 읽기를 멈춘다) — 경계에 걸친 줄이 새어 들어가지 않게 먼저 검사한다.
    /// </summary>
    private static bool ProcessLine(
        string line,
        List<ConversationMessage> eventMessages,
        List<ConversationMessage> responseItemMessages,
        long? maxOrdinalExclusive)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false; // 손상된/잘린 줄. 건너뛴다.
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? ordinal = GetInt64(root, "ordinal");
            if (maxOrdinalExclusive is { } cutoff && ordinal is { } ord && ord >= cutoff)
            {
                return true; // 이 줄부터는 상속 대상이 아니다. 추가하지 않고 멈추라고 알린다.
            }

            DateTimeOffset? timestamp = ParseTimestamp(GetString(root, "timestamp"));
            string? topLevelType = GetString(root, "type");

            if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (topLevelType == "event_msg" && GetString(payload, "type") == "item_completed")
            {
                ConversationMessage? message = TryParseEventItem(payload, ordinal, timestamp);
                if (message is not null)
                {
                    eventMessages.Add(message);
                }
            }
            else if (topLevelType == "response_item")
            {
                ConversationMessage? message = TryParseResponseItem(payload, ordinal, timestamp);
                if (message is not null)
                {
                    responseItemMessages.Add(message);
                }
            }

            return false;
        }
    }

    private static ConversationMessage? TryParseEventItem(JsonElement payload, long? ordinal, DateTimeOffset? timestamp)
    {
        if (!payload.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        ConversationRole? role = GetString(item, "type") switch
        {
            "UserMessage" => ConversationRole.User,
            "AgentMessage" => ConversationRole.Assistant,
            _ => null, // Reasoning / CommandExecution / FileChange / McpToolCall / WebSearch 등은 표시하지 않는다.
        };

        if (role is null)
        {
            return null;
        }

        string text = CombineContentText(item);
        if (text.Length == 0)
        {
            return null;
        }

        AssistantPhase? phase = role == ConversationRole.Assistant ? ParsePhase(GetString(item, "phase")) : null;

        return new ConversationMessage
        {
            ItemId = GetString(item, "id"),
            Role = role.Value,
            Text = text,
            Phase = phase,
            Timestamp = timestamp,
            Ordinal = ordinal,
        };
    }

    private static ConversationMessage? TryParseResponseItem(JsonElement payload, long? ordinal, DateTimeOffset? timestamp)
    {
        string? role = GetString(payload, "role");
        ConversationRole? mappedRole = role switch
        {
            "user" => ConversationRole.User,
            "assistant" => ConversationRole.Assistant,
            _ => null, // "developer"를 포함한 그 외 role은 절대 표시하지 않는다.
        };

        if (mappedRole is null)
        {
            return null;
        }

        string text = CombineContentText(payload);
        if (text.Length == 0 || IsInjectedContent(text))
        {
            return null;
        }

        AssistantPhase? phase = mappedRole == ConversationRole.Assistant ? ParsePhase(GetString(payload, "phase")) : null;

        return new ConversationMessage
        {
            ItemId = GetString(payload, "id"),
            Role = mappedRole.Value,
            Text = text,
            Phase = phase,
            Timestamp = timestamp,
            Ordinal = ordinal,
        };
    }

    private static bool IsInjectedContent(string text)
    {
        string trimmed = text.TrimStart();

        foreach (string marker in InjectedContentMarkers)
        {
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return InjectedTagPattern.IsMatch(trimmed);
    }

    /// <summary>
    /// <c>content</c> 배열의 각 원소에서 문자열 <c>text</c> 속성을 전부 모아 합친다.
    /// <c>type</c> 값의 대소문자/어휘 차이에 의존하지 않는다(클래스 remarks 참고).
    /// </summary>
    private static string CombineContentText(JsonElement itemOrPayload)
    {
        if (!itemOrPayload.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement element in content.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = GetString(element, "text");
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static AssistantPhase? ParsePhase(string? raw) => raw switch
    {
        "commentary" => AssistantPhase.Commentary,
        "final" => AssistantPhase.Final,
        _ => null,
    };

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
