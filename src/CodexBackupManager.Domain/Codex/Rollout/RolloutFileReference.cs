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

    /// <summary>
    /// 이 파일 자신의 rollout ID. 세그먼트 파일이면 <c>_&lt;segmentId&gt;</c> 부분이고,
    /// 아니면 <see cref="ThreadId"/>(파일명 앞부분) 그 자체다.
    /// </summary>
    /// <remarks>
    /// 공식 Codex 소스(<c>codex-rs/protocol/src/protocol.rs</c> <c>HistoryPosition</c> 주석) 확인:
    /// "HistoryPosition predates thread/revert, so this field is named thread_id. Treat its value
    /// as a rollout_id" — 즉 뒤에 이어지는 세그먼트의 <c>history_base.thread_id</c>는 안정적인
    /// 대화 ID(<see cref="ThreadId"/>)가 아니라 <b>바로 앞 세그먼트 파일 자신의 rollout ID</b>를
    /// 가리킨다. 실측으로 확인됨: 세그먼트 2의 rollout ID는 파일명의 <c>_&lt;segmentId&gt;</c>이고,
    /// 세그먼트 3의 <c>history_base.thread_id</c>는 세그먼트 1이 아니라 세그먼트 2의 이 값과 일치했다.
    /// </remarks>
    public string OwnRolloutId => SegmentId ?? ThreadId;
}
