using System.Collections.Generic;

namespace CodexBackupManager.Backup.Checksums;

/// <summary><c>checksums.json</c>의 항목 하나. 스펙: <c>docs/codexbackup-format-v1.md</c> §3.3.</summary>
/// <param name="Path">ZIP 안의 상대 경로(예: <c>payload/rollouts/rollout-….jsonl</c>).</param>
/// <param name="ByteLength">원본 바이트 길이.</param>
/// <param name="Sha256">소문자 16진수 SHA-256.</param>
public sealed record ChecksumEntry(string Path, long ByteLength, string Sha256);

/// <summary><c>checksums.json</c> 최상위 객체.</summary>
public sealed record ChecksumManifest
{
    /// <summary>
    /// 모든 payload 파일의 체크섬. <c>manifest.json</c>/<c>checksums.json</c> 자신은 포함하지
    /// 않는다(순환 참조 회피, §3.3).
    /// </summary>
    public required IReadOnlyList<ChecksumEntry> Entries { get; init; }
}
