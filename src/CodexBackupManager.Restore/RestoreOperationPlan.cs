using System.Collections.Generic;
using CodexBackupManager.Backup.Manifest;

namespace CodexBackupManager.Restore;

/// <summary>
/// backup entry를 그대로 복사해 로컬에 <b>새로</b> 만들 rollout 파일 하나(요구사항 8/9).
/// New thread 전체, 또는 IncomingAhead가 새로 얻는 segment 파일에 쓰인다.
/// </summary>
/// <param name="OwningThreadId">이 파일이 속한 thread(파일명에서 파싱한 자신의 thread id).</param>
/// <param name="SourceEntryPath">backup ZIP 안의 경로(<c>payload/rollouts/…</c>).</param>
/// <param name="TargetAbsolutePath">로컬에 새로 만들 절대경로.</param>
/// <param name="ExpectedLength">entry의 바이트 길이(복사 직후 검증용).</param>
/// <param name="ExpectedSha256Hex">entry의 SHA-256(복사 직후 검증용).</param>
public sealed record PlannedNewRolloutFile(
    string OwningThreadId,
    string SourceEntryPath,
    string TargetAbsolutePath,
    long ExpectedLength,
    string ExpectedSha256Hex);

/// <summary>
/// 기존 로컬 rollout 파일 끝에 완결된 JSONL 줄을 이어붙이는 연산(IncomingAhead의 plain <c>.jsonl</c>
/// fast-forward만 지원 — 요구사항 12). 압축 파일에는 이 연산을 만들지 않는다.
/// </summary>
/// <param name="ThreadId">대상 thread.</param>
/// <param name="TargetAbsolutePath">이어붙일 로컬 파일의 절대경로.</param>
/// <param name="ExpectedBeforeLength">append 직전 다시 확인해야 하는 원본 길이.</param>
/// <param name="ExpectedBeforeSha256Hex">append 직전 다시 확인해야 하는 원본 해시.</param>
/// <param name="AppendSourceEntryPath">이어붙일 바이트가 들어 있는 backup entry 경로.</param>
/// <param name="AppendSourceSkipBytes">
/// <paramref name="AppendSourceEntryPath"/>에서 이미 로컬에 있는 만큼(=<see cref="ExpectedBeforeLength"/>)
/// 건너뛰고 그 뒤부터 읽어야 한다는 오프셋(같은 값).
/// </param>
/// <param name="ExpectedAfterLength">append 후 기대하는 전체 길이.</param>
/// <param name="ExpectedAfterSha256Hex">append 후 기대하는 전체 해시.</param>
public sealed record PlannedRolloutAppend(
    string ThreadId,
    string TargetAbsolutePath,
    long ExpectedBeforeLength,
    string ExpectedBeforeSha256Hex,
    string AppendSourceEntryPath,
    long AppendSourceSkipBytes,
    long ExpectedAfterLength,
    string ExpectedAfterSha256Hex);

/// <summary>New thread 하나를 <c>threads</c> 테이블에 INSERT하는 연산.</summary>
/// <param name="Source">원본 38컬럼 metadata(가공하지 않은 원본값 그대로 쓴다).</param>
/// <param name="ResolvedRolloutPathAbsolute">이 thread의 leaf 파일이 로컬에 새로 놓인 절대경로.</param>
/// <param name="ResolvedProjectId">
/// 일치하는 로컬 프로젝트를 찾았을 때만 그 프로젝트의 ID(<c>threads.project_id</c>에 그대로 쓴다).
/// 못 찾았으면 <c>null</c>(기타 대화로 Import).
/// </param>
public sealed record PlannedThreadInsert(
    BackupConversationMetadata Source,
    string ResolvedRolloutPathAbsolute,
    string? ResolvedProjectId);

/// <summary>
/// IncomingAhead로 새 segment가 생겨 <c>threads.rollout_path</c>가 바뀌어야 하는 기존 thread의
/// UPDATE 연산(요구사항 8 — "실제 필요한 metadata field만"). 같은 파일 안에서만 append됐다면(새
/// segment가 없다면) 이 연산 자체가 필요 없다 — rollout_path가 바뀌지 않기 때문이다.
/// </summary>
/// <param name="ThreadId">대상 thread.</param>
/// <param name="NewRolloutPathAbsolute">새로 가리켜야 할 leaf 파일의 절대경로.</param>
public sealed record PlannedThreadRolloutPathUpdate(string ThreadId, string NewRolloutPathAbsolute);

/// <summary>
/// frozen <c>ImportPlan</c> + fresh 로컬 상태로부터 계산한, 실제로 실행할 구체적 file/DB 연산
/// 목록(요구사항 8). <see cref="RestoreOperationPlanner"/>만 이 값을 만든다 — relation/PlannedAction을
/// 다시 판단하지 않는다.
/// </summary>
/// <param name="NewRolloutFiles">새로 만들 rollout 파일(New thread 전체 + IncomingAhead의 새 segment).</param>
/// <param name="RolloutAppends">기존 파일에 이어붙일 연산(plain jsonl만).</param>
/// <param name="ThreadInserts">새 <c>threads</c> 행.</param>
/// <param name="ThreadRolloutPathUpdates">기존 thread의 <c>rollout_path</c> 갱신.</param>
public sealed record RestoreOperationPlan(
    IReadOnlyList<PlannedNewRolloutFile> NewRolloutFiles,
    IReadOnlyList<PlannedRolloutAppend> RolloutAppends,
    IReadOnlyList<PlannedThreadInsert> ThreadInserts,
    IReadOnlyList<PlannedThreadRolloutPathUpdate> ThreadRolloutPathUpdates)
{
    /// <summary>아무 write도 필요 없는(전부 NoOp/Skip인) 계획인지.</summary>
    public bool IsEmpty =>
        NewRolloutFiles.Count == 0 && RolloutAppends.Count == 0 &&
        ThreadInserts.Count == 0 && ThreadRolloutPathUpdates.Count == 0;
}

/// <summary>
/// <see cref="RestoreOperationPlanner.Build"/>가 계획 생성 자체를 거부했을 때의 결과.
/// Plan 생성이 거부되면 Snapshot조차 만들지 않는다 — write는 항상 0건이다(요구사항 8).
/// </summary>
/// <param name="Plan">성공했으면 계획. 실패했으면 <c>null</c>.</param>
/// <param name="RejectionReasons">실패 이유 목록(원문 없음, 사람이 읽을 수 있는 요약만).</param>
public sealed record RestoreOperationPlanResult(RestoreOperationPlan? Plan, IReadOnlyList<string> RejectionReasons)
{
    /// <summary>계획이 성공적으로 만들어졌는지.</summary>
    public bool Success => Plan is not null;
}
