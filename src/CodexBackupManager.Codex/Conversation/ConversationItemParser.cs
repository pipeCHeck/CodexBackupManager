using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
/// <b>주입 마커 필터는 <c>role:"user"</c>에만 적용한다.</b> OpenAI Codex 공식 소스
/// (<c>codex-rs/core/src/context/</c>)를 조사한 결과, 확인된 5개 마커는 전부
/// <c>role:"user"</c> 또는 <c>role:"developer"</c> fragment이고, <c>role:"assistant"</c>로
/// 정의된 fragment(멀티 에이전트 inter-agent 메시지 등)는 애초에 시작/종료 마커가 빈 문자열이라
/// <c>matches_marked_text</c>가 절대 매치하지 않는다. 즉 확인된 마커는 assistant가 실제로
/// 만들어낼 수 있는 모양이 아니므로, assistant 메시지에는 필터를 적용하지 않고 content를 그대로
/// 보존한다(<c>role:"developer"</c>는 이미 role 검사만으로 항상 숨겨진다).
/// </para>
/// <para>
/// <b>"소문자 태그로 시작"하는 구조 규칙은 쓰지 않는다.</b> 예전에는 새 마커를 놓치지 않으려고
/// <c>^&lt;[a-z][a-z0-9_]*&gt;</c> 형태의 구조 규칙으로 광범위하게 걸렀는데, 이 규칙은 사용자가
/// 실제로 <c>&lt;code&gt;</c>/<c>&lt;summary&gt;</c>/<c>&lt;xml&gt;</c> 같은 정상 HTML/코드
/// 조각으로 메시지를 시작했을 때도 오탐으로 삭제해버린다(실측으로 확인된 false positive).
/// 내부 메시지 하나를 잘못 보여주는 것보다 실제 사용자 메시지를 누락하는 쪽이 훨씬 심각한 오류이므로,
/// OpenAI Codex 공식 소스(<c>codex-rs/context-fragments/src/fragment.rs</c>의
/// <c>ContextualUserFragment::matches_marked_text</c>)와 동일하게 <b>확인된 마커의 시작+종료
/// 태그 쌍이 정확히 양 끝에서 일치할 때만</b> 숨긴다. 알려지지 않은 새 마커는 이 목록에 없으면
/// 숨기지 않는다 — future-proofing을 이유로 판정 범위를 넓히지 않는다는 원칙을 명시적으로 따른다.
/// </para>
/// <para>
/// <b>주입 마커는 <c>content</c> 배열 원소 단위로 판정한다.</b> 실측(2026-08-27 rollout)에서
/// <c>&lt;recommended_plugins&gt;...&lt;/recommended_plugins&gt;</c>와
/// <c>&lt;environment_context&gt;...&lt;/environment_context&gt;</c>가 한 <c>response_item</c>의
/// <c>content</c> 배열 안에 서로 다른 원소로 함께 들어오는 사례를 확인했다. 이걸 먼저 하나로
/// 합친 뒤 "시작 마커 + 종료 마커"로 판정하면 서로 다른 두 마커의 시작/끝이 섞여 어느 쌍과도
/// 맞지 않아 필터를 통과해버린다. 그래서 각 <c>content</c> 원소의 텍스트를 개별적으로 판정해서
/// 주입된 조각만 제외하고 나머지(실제 사용자 텍스트가 섞여 있다면 그것)를 합친다.
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
    /// 실제 <c>.codex</c> 데이터와 OpenAI Codex 공식 소스(<c>codex-rs/core/src/context/</c>)로
    /// 확인된 내부 주입 마커의 시작/종료 태그 쌍. 이 목록에 없는 마커는 숨기지 않는다 —
    /// 새 마커에 대한 future-proofing을 이유로 판정 범위를 넓히지 않는다(클래스 remarks 참고).
    /// </summary>
    private static readonly (string Start, string End)[] InjectedContentMarkers =
    [
        ("<app-context>", "</app-context>"),
        ("<recommended_plugins>", "</recommended_plugins>"),
        ("<turn_aborted>", "</turn_aborted>"),
        ("<multi_agent_mode>", "</multi_agent_mode>"),
        ("<environment_context>", "</environment_context>"),
    ];

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

        string? text = ResolveDisplayText(CombineContentText(item), item);
        if (text is null)
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

        // 확인된 5개 주입 마커는 전부 role="user" 또는 role="developer" fragment다(공식 소스
        // codex-rs/core/src/context/ 조사 확인). role="assistant"로 정의된 fragment(멀티 에이전트
        // inter-agent 메시지 등)는 애초에 빈 마커("", "")를 써서 텍스트와 매치되지 않으므로, 이
        // 필터를 assistant에도 적용하면 얻는 것 없이 모델이 그 태그를 언급한 진짜 출력만 위험에
        // 노출시킨다. 그래서 필터는 role="user"에서만 적용한다(클래스 remarks 참고).
        string rawText = mappedRole == ConversationRole.User
            ? CombineNonInjectedContentText(payload)
            : CombineContentText(payload);
        string? text = ResolveDisplayText(rawText, payload);
        if (text is null)
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

    /// <summary>
    /// 텍스트 전체(양 끝 공백 제외)가 확인된 마커의 시작 태그로 시작하고 그 마커의 종료 태그로
    /// 끝나는지 판정한다. 시작 마커와 종료 마커는 반드시 같은 쌍이어야 한다 — 서로 다른 두 마커의
    /// 시작/끝이 섞인 경우는 매치하지 않는다(클래스 remarks의 <c>content</c> 배열 원소 단위 판정 참고).
    /// </summary>
    private static bool IsInjectedContent(string text)
    {
        string trimmed = text.Trim();

        foreach ((string start, string end) in InjectedContentMarkers)
        {
            if (trimmed.StartsWith(start, StringComparison.OrdinalIgnoreCase) &&
                trimmed.EndsWith(end, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>실제로 표시할 텍스트를 정한다. 텍스트가 없으면 비-텍스트 콘텐츠 placeholder나 <c>null</c>을 돌려준다.</summary>
    /// <remarks>
    /// <para>
    /// 공식 Codex 소스(<c>tui/src/chatwidget.rs</c>의 <c>on_user_message_display</c>,
    /// <c>app-server-protocol</c>의 <c>build_user_inputs</c>)를 확인한 결과, 공식 정책은 정확히
    /// "trim한 텍스트가 비어 있고 텍스트/이미지 등 어떤 콘텐츠도 없으면 메시지 자체를 만들지
    /// 않는다"였다. 그래서 <c>text.Length == 0</c> 대신 <see cref="string.IsNullOrWhiteSpace(string?)"/>로
    /// 공백만 있는 메시지도 숨긴다 — 단, 공식 소스도 zero-width/control 문자를 특별 취급하지 않고
    /// 표준 trim()만 쓰므로, 우리도 그 이상으로 판정 범위를 넓히지 않는다(과설계 방지, 실측으로도
    /// 이 PC의 <c>.codex</c> 데이터에는 공백/zero-width-only User 메시지가 하나도 없었다).
    /// </para>
    /// <para>
    /// 텍스트가 없어도 이미지 등 텍스트가 아닌 콘텐츠가 있으면 공식 Codex의 <c>"[Image #N]"</c>과
    /// 같은 취지로 <see cref="DescribeNonTextContent"/> placeholder를 대신 보여준다 — 사용자가
    /// 실제로 보낸 첨부를 조용히 지우지 않는다(핵심 원칙: 메시지 누락이 오탐 노출보다 더 심각하다).
    /// 텍스트도 비-텍스트 콘텐츠도 전혀 없으면 <c>null</c>을 돌려줘 메시지 자체를 만들지 않는다.
    /// </para>
    /// </remarks>
    private static string? ResolveDisplayText(string combinedText, JsonElement itemOrPayload)
    {
        if (!string.IsNullOrWhiteSpace(combinedText))
        {
            return combinedText;
        }

        return HasNonTextContent(itemOrPayload) ? DescribeNonTextContent(itemOrPayload) : null;
    }

    /// <summary>
    /// <c>content</c> 배열에 <c>text</c> 속성 자체가 아예 없는 원소(이미지/첨부 등)가 있는지.
    /// </summary>
    /// <remarks>
    /// 실측(2026-08-31 rollout)에서 <c>{"type":"...","text":""}</c>처럼 <c>text</c> 키는 있지만
    /// 값이 빈 문자열인 원소를 발견했다 — 이건 이미지/첨부가 아니라 그냥 빈 텍스트다. 그래서
    /// "값이 비어 있는지"가 아니라 "<c>text</c> 키 자체가 있는지"로 판정해야 한다. 키의 유무가
    /// 아니라 값의 빈 여부로 판단하면 진짜 빈 텍스트를 첨부파일로 잘못 분류해 오해를 주는
    /// placeholder를 보여주게 된다.
    /// </remarks>
    private static bool HasNonTextContent(JsonElement itemOrPayload)
    {
        if (!itemOrPayload.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement element in content.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty("text", out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>text</c> 속성 자체가 없는 <c>content</c> 원소들의 <c>type</c> 값에서 "image"/"audio"를
    /// 대소문자 무시로 찾아 종류를 분류한다. 확정되지 않은 <c>type</c> 문자열을 추측해 하드코딩하지
    /// 않기 위해, 어느 쪽에도 해당하지 않으면 안전하게 "첨부 파일"로 표시한다.
    /// </summary>
    private static string DescribeNonTextContent(JsonElement itemOrPayload)
    {
        var kinds = new List<string>();

        if (itemOrPayload.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in content.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty("text", out _))
                {
                    continue;
                }

                string type = GetString(element, "type") ?? string.Empty;
                string kind = type.Contains("image", StringComparison.OrdinalIgnoreCase) ? "이미지"
                    : type.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "음성"
                    : "첨부 파일";

                if (!kinds.Contains(kind))
                {
                    kinds.Add(kind);
                }
            }
        }

        return kinds.Count == 0 ? "[첨부 파일]" : $"[{string.Join("/", kinds)}]";
    }

    /// <summary>
    /// <c>content</c> 배열의 각 원소에서 문자열 <c>text</c> 속성을 모아 합친다.
    /// <c>type</c> 값의 대소문자/어휘 차이에 의존하지 않는다(클래스 remarks 참고).
    /// </summary>
    private static string CombineContentText(JsonElement itemOrPayload)
        => CombineContentText(itemOrPayload, filterInjected: false);

    /// <summary>
    /// <c>CombineContentText</c>와 같지만, 확인된 주입 마커 쌍과 정확히 일치하는 원소는 제외하고
    /// 나머지만 합친다. <c>response_item</c>(API wire 포맷) 폴백 경로의 <c>role:"user"</c>에서만
    /// 쓴다 — <c>event_msg</c>는 항상 UI가 이미 정제한 메시지이므로 필요 없고, <c>role:"assistant"</c>는
    /// 확인된 마커를 만들어낼 수 없는 role이라 필요 없다(클래스 remarks 참고).
    /// </summary>
    private static string CombineNonInjectedContentText(JsonElement payload)
        => CombineContentText(payload, filterInjected: true);

    private static string CombineContentText(JsonElement itemOrPayload, bool filterInjected)
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

            if (filterInjected && IsInjectedContent(text))
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
