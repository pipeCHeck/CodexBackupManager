namespace CodexBackupManager.Domain.Codex.Sessions;

/// <summary>rollout <c>session_meta.payload.git</c>에서 읽은 정보.</summary>
public sealed record GitInfo(string? CommitHash, string? Branch, string? RepositoryUrl);
