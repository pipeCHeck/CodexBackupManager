namespace CodexBackupManager.Domain.Codex.Sessions;

/// <summary>
/// rollout <c>session_meta.payload.history_base</c>. 분기/이어붙이기된 대화가
/// 부모 thread의 어느 지점까지를 이어받는지 가리킨다. (docs/codex-storage-format.md §3)
/// </summary>
/// <param name="ThreadId">부모 thread ID.</param>
/// <param name="EndOrdinalExclusive">부모 쪽에서 이어받는 마지막 ordinal(미포함).</param>
/// <param name="EndByteOffset">부모 rollout 파일에서 이어받는 바이트 위치.</param>
public sealed record HistoryBaseReference(string ThreadId, long? EndOrdinalExclusive, long? EndByteOffset);
