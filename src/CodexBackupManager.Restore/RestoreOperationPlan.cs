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
/// <param name="ExpectedLength">
/// backup entry의 <b>물리(physical) 원본</b> 바이트 길이(복사 직후 검증용) — <c>.jsonl.zst</c>면
/// 압축된 그대로의 길이다. <see cref="RolloutRestoreService.CreateNewFile"/>이 entry 바이트를 그대로
/// 복사하므로 반드시 물리 길이/해시와 비교해야 한다(Phase 07_01에서 바로잡음 — 예전에는 IncomingAhead의
/// 새 segment에서 논리(decompressed) 길이/해시를 넣어 정상 <c>.zst</c> 파일도 검증에 실패했다).
/// </param>
/// <param name="ExpectedSha256Hex">entry의 물리 원본 바이트 SHA-256(복사 직후 검증용).</param>
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
/// <param name="ResolvedTargetCwd">
/// Phase 07_02 요구사항 4 — 공식 소스 조사로 확인한 사실: <c>threads.cwd</c>는 단순 표시용이
/// 아니다. Codex CLI가 이 thread를 다시 열 때(<c>resume_config.rs</c>) 이 값을 실제 작업 디렉터리
/// 후보로 쓸 수 있다(설정에 따라 그대로 채택되거나, 선택 프롬프트의 기본값으로 제시된다 — 존재
/// 여부를 확인하지 않는다). 그래서 원본 PC의 경로를 그대로 두면 대상 PC에 없는 폴더가 resume 시
/// 기본값으로 뜰 수 있다. <see cref="ResolvedProjectId"/>가 있을 때만(=이 대화의 프로젝트 경로가
/// 실제로 대상 PC의 로컬 프로젝트로 해석됐을 때만) 그 프로젝트의 대상 PC 경로로 값을 채운다 —
/// 해석 불가(기타 대화)면 <c>null</c>이고, 이때는 원본 <c>Source.OriginalCwd</c>를 그대로 쓴다(더
/// 나은 값이 없다 — 알려진 한계로 남는다). <b>rollout JSONL 내부의 <c>session_meta.cwd</c>는
/// 절대 건드리지 않는다</b> — 이 필드는 SQLite `threads.cwd`에만 쓰인다.
/// </param>
public sealed record PlannedThreadInsert(
    BackupConversationMetadata Source,
    string ResolvedRolloutPathAbsolute,
    string? ResolvedProjectId,
    string? ResolvedTargetCwd = null);

/// <summary>
/// IncomingAhead로 새 segment가 생겨 <c>threads.rollout_path</c>가 바뀌어야 하는 기존 thread의
/// UPDATE 연산(요구사항 8 — "실제 필요한 metadata field만"). 같은 파일 안에서만 append됐다면(새
/// segment가 없다면) 이 연산 자체가 필요 없다 — rollout_path가 바뀌지 않기 때문이다.
/// </summary>
/// <param name="ThreadId">대상 thread.</param>
/// <param name="NewRolloutPathAbsolute">새로 가리켜야 할 leaf 파일의 절대경로.</param>
public sealed record PlannedThreadRolloutPathUpdate(string ThreadId, string NewRolloutPathAbsolute);

/// <summary>
/// IncomingAhead가 기존 thread에 반영해도 안전한, "대화 자체가 진행되면서 자연스럽게 갱신되는"
/// metadata만 담는다(Phase 07_01 요구사항 8). target PC 고유 값(<c>cwd</c>/<c>project_id</c>/사이드바
/// 배치 등)은 여기 포함하지 않는다 — <see cref="StateDatabaseWriter.UpdateMetadata"/>가 이 필드들만
/// 정확히 그 컬럼에 쓴다.
/// </summary>
/// <param name="ThreadId">대상 thread.</param>
/// <param name="UpdatedAtSeconds">backup 쪽 <c>updated_at</c>(항상 반영 — 대화가 진행된 실제 시각).</param>
/// <param name="UpdatedAtMs">backup 쪽 <c>updated_at_ms</c>(있으면).</param>
/// <param name="TokensUsed">local과 backup 중 더 큰 값(단조 증가 지표이므로 절대 줄이지 않는다).</param>
/// <param name="HasUserEvent">local 또는 backup 중 하나라도 true면 true(한 번 true면 되돌리지 않는다).</param>
/// <param name="NameIfLocalMissing">local의 <c>name</c>이 비어 있을 때만 채워 넣을 값(있으면 절대 덮어쓰지 않는다).</param>
/// <param name="ModelIfLocalMissing">local의 <c>model</c>이 비어 있을 때만 채워 넣을 값.</param>
/// <param name="CliVersionIfLocalMissing">local의 <c>cli_version</c>이 비어 있을 때만 채워 넣을 값.</param>
public sealed record PlannedThreadMetadataUpdate(
    string ThreadId,
    long? UpdatedAtSeconds,
    long? UpdatedAtMs,
    long? TokensUsed,
    bool? HasUserEvent,
    string? NameIfLocalMissing,
    string? ModelIfLocalMissing,
    string? CliVersionIfLocalMissing);

/// <summary>
/// frozen <c>ImportPlan</c> + fresh 로컬 상태로부터 계산한, 실제로 실행할 구체적 file/DB 연산
/// 목록(요구사항 8). <see cref="RestoreOperationPlanner"/>만 이 값을 만든다 — relation/PlannedAction을
/// 다시 판단하지 않는다.
/// </summary>
/// <param name="NewRolloutFiles">새로 만들 rollout 파일(New thread 전체 + IncomingAhead의 새 segment).</param>
/// <param name="RolloutAppends">기존 파일에 이어붙일 연산(plain jsonl만).</param>
/// <param name="ThreadInserts">새 <c>threads</c> 행.</param>
/// <param name="ThreadRolloutPathUpdates">기존 thread의 <c>rollout_path</c> 갱신.</param>
/// <param name="ThreadMetadataUpdates">기존 thread의 안전한 metadata field 갱신(§8 정책).</param>
public sealed record RestoreOperationPlan(
    IReadOnlyList<PlannedNewRolloutFile> NewRolloutFiles,
    IReadOnlyList<PlannedRolloutAppend> RolloutAppends,
    IReadOnlyList<PlannedThreadInsert> ThreadInserts,
    IReadOnlyList<PlannedThreadRolloutPathUpdate> ThreadRolloutPathUpdates,
    IReadOnlyList<PlannedThreadMetadataUpdate> ThreadMetadataUpdates)
{
    /// <summary>아무 write도 필요 없는(전부 NoOp/Skip인) 계획인지.</summary>
    public bool IsEmpty =>
        NewRolloutFiles.Count == 0 && RolloutAppends.Count == 0 &&
        ThreadInserts.Count == 0 && ThreadRolloutPathUpdates.Count == 0 &&
        ThreadMetadataUpdates.Count == 0;
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
