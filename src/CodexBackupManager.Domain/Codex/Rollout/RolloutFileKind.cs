namespace CodexBackupManager.Domain.Codex.Rollout;

/// <summary>rollout 파일의 압축 형태.</summary>
public enum RolloutFileKind
{
    /// <summary>일반 <c>.jsonl</c>.</summary>
    PlainJsonl = 0,

    /// <summary>Zstandard로 압축된 <c>.jsonl.zst</c>. 오래된 rollout에 쓰인다.</summary>
    ZstdCompressed = 1,
}
