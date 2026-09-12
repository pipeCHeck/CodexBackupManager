using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Attachments;

/// <summary>
/// rollout 파일의 <c>event_msg/item_completed</c> 메시지 <c>content</c> 배열에서
/// <c>{"type":"local_image","path":"…"}</c> 형태로 참조된 로컬 이미지 파일 경로를 찾는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>실측 근거</b>(docs/codexbackup-format-v1.md §2): 실제 <c>.codex</c> 데이터(371개 rollout 파일,
/// 161,048줄)에서 텍스트가 없는 <c>content</c> 원소는 <c>{type:"local_image", path}</c>(367건, path는
/// 전부 <c>attachments\</c> 폴더 밖의 외부 경로)와 <c>{type:"image_url", image_url}</c>(22건, 값이
/// data URI/원격 URL이든 rollout 파일 바이트 자체에 이미 들어있어 별도 처리가 필요 없다) 두 모양뿐이었다.
/// <c>attachments\</c>(붙여넣기 텍스트)/<c>visualizations\</c>/<c>generated_images\</c>는 구조적으로
/// 안전하게 추적할 참조 방법을 찾지 못해 이 스캐너의 범위 밖이다(같은 문서 §2.3 — 알려진 한계).
/// </para>
/// <para>
/// <c>response_item</c>(API wire 레벨)은 스캔하지 않는다 — Export의 목적은 "실제로 화면에 보여줄
/// 콘텐츠가 참조하는 파일"이고, authoritative-source 정책(<see cref="Conversation.ConversationItemParser"/>
/// 참고)과 마찬가지로 <c>event_msg/item_completed</c>만 신뢰한다.
/// </para>
/// </remarks>
public static class LocalImageAttachmentScanner
{
    /// <summary>
    /// 한 rollout 파일에서 참조된 local_image 경로를 전부 찾는다(중복 제거하지 않음 — 호출자가
    /// 여러 파일에 걸쳐 dedupe한다).
    /// </summary>
    public static IReadOnlyList<string> ScanFile(
        RolloutFileReference file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var found = new List<string>();
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

                if (!root.TryGetProperty("type", out JsonElement typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String ||
                    typeEl.GetString() != "event_msg")
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!payload.TryGetProperty("type", out JsonElement payloadType) ||
                    payloadType.ValueKind != JsonValueKind.String ||
                    payloadType.GetString() != "item_completed")
                {
                    continue;
                }

                if (!payload.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
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
                            found.Add(path);
                        }
                    }
                }
            }
        }

        return found;
    }
}
