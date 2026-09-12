using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Domain.Codex.Import;

/// <summary>
/// 어떤 rollout 파일 하나가, 이 경계 판정 때문에 파일 전체가 아니라 어디까지만 포함됐는지.
/// </summary>
public enum RolloutSliceBoundary
{
    /// <summary>컷오프 없이 파일 전체가 포함됐다.</summary>
    Full = 0,

    /// <summary><c>ordinal</c> 컷오프로 잘렸다.</summary>
    OrdinalCutoff = 1,

    /// <summary><c>byte offset</c> 컷오프로 잘렸다(ordinal을 쓸 수 없을 때만).</summary>
    ByteOffsetCutoff = 2,
}

/// <summary>
/// 한 대화(thread)의 revision을 이루는 rollout 파일 하나의 "논리적 슬라이스" fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// 원본 파일 전체가 아니라 <b>이 conversation이 실제로 소비하는 부분</b>만 가리킨다 — child가
/// parent rollout의 중간에서 분기했다면, parent가 그 이후에도 계속 쓰였더라도 이 슬라이스는
/// 분기 지점까지만 포함한다(docs/codexbackup-format-v1.md의 Export dependency closure 정책과
/// 동일한 경계 판정, <see cref="Threads.ThreadDependencyResolver"/> 참고 — Codex.Threads는
/// CodexBackupManager.Codex 프로젝트에 있다).
/// </para>
/// <para>
/// <see cref="Sha256Hex"/>는 이 슬라이스의 "논리적 바이트"(압축 해제된 JSONL 줄, 줄바꿈 포함)의
/// SHA-256이다. <c>.jsonl.zst</c> 원본은 압축 해제된 내용 기준으로 비교한다 — 한쪽 PC는
/// <c>.jsonl</c>, 다른 쪽은 <c>.jsonl.zst</c>일 수 있기 때문이다.
/// </para>
/// </remarks>
/// <param name="RolloutId">이 슬라이스가 속한 파일의 <see cref="RolloutFileReference.OwnRolloutId"/>.</param>
/// <param name="File">
/// 이 슬라이스의 출처 파일 참조. 로컬이면 실제 파일, backup이면 ZIP entry 경로를 담은 참조다
/// (<see cref="RolloutFileReference.FullPath"/>의 의미는 출처에 따라 달라진다 — 비교/재해싱에만 쓴다).
/// </param>
/// <param name="Boundary">경계 종류.</param>
/// <param name="LogicalByteLength">포함된 논리적 바이트 길이.</param>
/// <param name="Sha256Hex">포함된 논리적 바이트의 SHA-256(소문자 hex).</param>
public sealed record RolloutSlice(
    string RolloutId,
    RolloutFileReference File,
    RolloutSliceBoundary Boundary,
    long LogicalByteLength,
    string Sha256Hex);

/// <summary>
/// 한 대화(thread)의 revision fingerprint — 뿌리 조상부터 자기 자신까지, 실제로 소비되는
/// rollout 슬라이스를 순서대로 담는다.
/// </summary>
/// <param name="ThreadId">이 revision이 나타내는 thread ID(체인의 leaf).</param>
/// <param name="OrderedSlices">뿌리(가장 오래된 조상) → leaf 순서의 슬라이스 목록. 최소 1개.</param>
public sealed record ConversationRevision(string ThreadId, IReadOnlyList<RolloutSlice> OrderedSlices);

/// <summary>
/// <see cref="ConversationRevision"/>을 만들 수 있었는지의 결과. 만들지 못했으면(체인 누락/순환
/// 참조/조상 정보 누락) <see cref="Revision"/>이 <c>null</c>이고 <see cref="UnverifiableReason"/>에
/// 이유가 남는다 — 이 경우 호출자는 <see cref="RevisionRelation.Unverifiable"/>로 취급해야 한다.
/// </summary>
public sealed record ConversationRevisionBuildResult(ConversationRevision? Revision, string? UnverifiableReason)
{
    /// <summary>성공 결과.</summary>
    public static ConversationRevisionBuildResult Ok(ConversationRevision revision) => new(revision, null);

    /// <summary>실패(Unverifiable) 결과.</summary>
    public static ConversationRevisionBuildResult Unverifiable(string reason) => new(null, reason);
}
