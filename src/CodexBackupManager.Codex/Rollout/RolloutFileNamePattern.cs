using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// rollout 파일명 규칙을 파싱한다. (docs/codex-storage-format.md §2 "파일명 규칙")
/// </summary>
/// <remarks>
/// <code>
/// rollout-2026-09-11T11-55-17-01a08e64-0ead-7a80-a5a5-a762540c0755.jsonl
///         └── ISO8601, ':' → '-' ──┘ └────────── threadId (UUIDv7) ──────────┘
///
/// rollout-2026-09-11T12-22-35-01a08e64-...-a762540c0755_01a08e7d-...-55505359447b.jsonl
///                             └── 부모 threadId ──┘ └──── 세그먼트 id ────┘
/// </code>
/// 이름 규칙에 맞지 않는 파일은 조용히 무시한다(다른 목적의 파일일 수 있다).
/// </remarks>
public static partial class RolloutFileNamePattern
{
    [GeneratedRegex(
        @"^rollout-(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})T(?<h>\d{2})-(?<mi>\d{2})-(?<s>\d{2})-" +
        @"(?<thread>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})" +
        @"(?:_(?<segment>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}))?" +
        @"\.jsonl(?<zst>\.zst)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// 파일명을 파싱한다. 규칙에 맞지 않으면 <c>null</c>.
    /// </summary>
    /// <param name="fileName">경로 없이 파일명만.</param>
    public static ParsedRolloutFileName? TryParse(string fileName)
    {
        Match match = Pattern().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        DateTimeOffset? timestamp = null;
        string iso = $"{match.Groups["y"].Value}-{match.Groups["mo"].Value}-{match.Groups["d"].Value}T" +
                     $"{match.Groups["h"].Value}:{match.Groups["mi"].Value}:{match.Groups["s"].Value}Z";
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            timestamp = parsed;
        }

        string threadId = match.Groups["thread"].Value;
        string? segmentId = match.Groups["segment"].Success ? match.Groups["segment"].Value : null;
        RolloutFileKind kind = match.Groups["zst"].Success ? RolloutFileKind.ZstdCompressed : RolloutFileKind.PlainJsonl;

        return new ParsedRolloutFileName(threadId, segmentId, timestamp, kind);
    }

    /// <summary>파일명 파싱 결과.</summary>
    public sealed record ParsedRolloutFileName(
        string ThreadId,
        string? SegmentId,
        DateTimeOffset? Timestamp,
        RolloutFileKind Kind);
}
