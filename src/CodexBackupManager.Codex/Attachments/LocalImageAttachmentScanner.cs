using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Attachments;

/// <summary>
/// rollout 파일에서 <b>외부 파일을 가리키는</b> 이미지 참조 경로를 찾는다(data URI처럼 이미 rollout
/// 바이트 안에 내용이 들어있는 참조는 별도 처리가 필요 없으므로 대상이 아니다).
/// </summary>
/// <remarks>
/// <para>
/// <b>authoritative-source 정책은 <see cref="Conversation.ConversationItemParser"/>와 같다</b>
/// (Phase 05_01 hardening). 실측(2026-09-12, 371개 rollout/161,048줄) 결과: <c>event_msg/item_completed</c>가
/// 하나도 없는 "fallback-only" 파일 51개 전부 non-text content가 0건이었다. 그래도 이 파일 형식
/// 자체가 그런 콘텐츠를 담을 수 없다는 보장은 아니므로(다른 PC/미래 데이터에서 나타날 수 있음),
/// <see cref="Conversation.ConversationItemParser"/>와 동일하게 <b>파일 단위로</b> "이 파일에
/// event_msg/item_completed가 하나라도 있으면 그것만 신뢰하고, 하나도 없을 때만 response_item을
/// 본다"는 정책을 그대로 따른다 — 둘을 섞어서 중복 판정하지 않는다.
/// </para>
/// <para>
/// <b>event_msg 쪽</b>: <c>content[].{type:"local_image", path}</c>(실측 367건, 전부 외부 경로)만
/// 외부 파일 참조다. <c>{type:"image_url", image_url}</c>는 값이 data URI든 원격 URL이든 rollout
/// 바이트 자체에 이미 있어 대상이 아니다.
/// </para>
/// <para>
/// <b>response_item(API wire) 쪽</b>: 실측 결과 non-text content는 오직
/// <c>{type:"input_image", image_url, detail}</c>(403건) 뿐이었고, <c>image_url</c> 값은 전부
/// <c>data:</c> URI였다(=이미 rollout 바이트 안에 내용이 있음, 대상 아님). 다만 <c>image_url</c>이
/// <c>data:</c>가 아닌 경우(외부 경로/URL)까지 대비해 방어적으로 처리한다 — 이 형식 자체가
/// 그런 값을 담을 수 없다고 확정할 근거는 없기 때문이다(추측 금지).
/// </para>
/// </remarks>
public static class LocalImageAttachmentScanner
{
    /// <summary>
    /// 한 rollout 파일에서 참조된, <b>외부 파일을 가리키는</b> 이미지 경로를 전부 찾는다(중복 제거
    /// 하지 않음 — 호출자가 여러 파일에 걸쳐 dedupe한다).
    /// </summary>
    public static IReadOnlyList<string> ScanFile(
        RolloutFileReference file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var eventMsgPaths = new List<string>();
        var responseItemPaths = new List<string>();
        bool hasEventMsgItemCompleted = false;

        foreach (string line in RolloutStreamReader.ReadLines(file.FullPath, file.Kind, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // 손상된 줄은 조용히 건너뛴다 — 다른 파서들과 같은 관용 정책.
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!root.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string topType = typeEl.GetString()!;

                if (topType == "event_msg")
                {
                    if (TryGetItemCompletedItem(root, out JsonElement item))
                    {
                        hasEventMsgItemCompleted = true;
                        CollectLocalImagePaths(item, eventMsgPaths);
                    }
                }
                else if (topType == "response_item")
                {
                    CollectResponseItemImagePaths(root, responseItemPaths);
                }
            }
        }

        // ConversationItemParser와 동일한 file-level authoritative 정책: event_msg/item_completed가
        // 하나라도 있으면 그것만 신뢰하고 response_item은 버린다(섞지 않는다).
        return hasEventMsgItemCompleted ? eventMsgPaths : responseItemPaths;
    }

    private static bool TryGetItemCompletedItem(JsonElement root, out JsonElement item)
    {
        item = default;
        if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!payload.TryGetProperty("type", out JsonElement payloadType) ||
            payloadType.ValueKind != JsonValueKind.String ||
            payloadType.GetString() != "item_completed")
        {
            return false;
        }

        if (!payload.TryGetProperty("item", out item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static void CollectLocalImagePaths(JsonElement item, List<string> into)
    {
        if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement element in content.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty("type", out JsonElement elementType) ||
                elementType.ValueKind != JsonValueKind.String ||
                !string.Equals(elementType.GetString(), "local_image", StringComparison.Ordinal))
            {
                continue;
            }

            if (element.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String)
            {
                string? path = pathEl.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    into.Add(path);
                }
            }
        }
    }

    /// <summary>
    /// response_item(API wire) content 배열에서 외부 파일을 가리키는 이미지 참조를 찾는다.
    /// 실측 결과 <c>input_image.image_url</c>은 전부 <c>data:</c> URI(=rollout 바이트 안에 내용이
    /// 이미 있음)였지만, 그렇지 않은 값(외부 경로/URL)까지 방어적으로 처리한다 — 이 형식이 그런
    /// 값을 담을 수 없다고 확정할 근거가 없다(클래스 remarks 참고).
    /// </summary>
    private static void CollectResponseItemImagePaths(JsonElement root, List<string> into)
    {
        if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!payload.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement element in content.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty("type", out JsonElement elementType) || elementType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string type = elementType.GetString()!;
            if (type != "input_image" && type != "local_image")
            {
                continue;
            }

            // local_image라면 event_msg 쪽과 같은 필드명("path")을 우선 확인하고,
            // input_image라면 실측된 필드명("image_url")을 확인한다.
            string? value = GetStringProperty(element, "path") ?? GetStringProperty(element, "image_url");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 이미 rollout 바이트 안에 내용이 있다 — 별도 파일 참조가 아니다.
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                continue; // 원격 URL — 로컬 파일 시스템 참조가 아니므로 백업 대상이 아니다.
            }

            into.Add(value);
        }
    }

    private static string? GetStringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
