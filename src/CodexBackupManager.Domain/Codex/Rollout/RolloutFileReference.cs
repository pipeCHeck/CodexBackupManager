using System;

namespace CodexBackupManager.Domain.Codex.Rollout;

/// <summary>
/// <c>sessions\</c> 또는 <c>archived_sessions\</c> 아래에서 찾은 rollout 파일 하나.
/// 파일명 규칙(docs/codex-storage-format.md §2)에서 파싱한 값만 담는다. 내용은 읽지 않는다.
/// </summary>
/// <param name="FullPath">파일 전체 경로.</param>
/// <param name="FileName">파일명만.</param>
/// <param name="ThreadId">파일명에서 파싱한 thread ID.</param>
/// <param name="SegmentId">
/// 파일명 <c>_&lt;segmentId&gt;</c> 부분. 페이지네이션 세그먼트가 아니면 <c>null</c>.
/// </param>
/// <param name="TimestampFromFileName">파일명에서 파싱한 생성 시각. 파싱 실패 시 <c>null</c>.</param>
/// <param name="IsArchived"><c>archived_sessions\</c> 아래에 있었는지.</param>
/// <param name="Kind">압축 형태.</param>
public sealed record RolloutFileReference(
    string FullPath,
    string FileName,
    string ThreadId,
    string? SegmentId,
    DateTimeOffset? TimestampFromFileName,
    bool IsArchived,
    RolloutFileKind Kind)
{
    /// <summary>페이지네이션 세그먼트 파일인지(파일명에 <c>_&lt;segmentId&gt;</c>가 있는지).</summary>
    public bool IsSegment => SegmentId is not null;
}
