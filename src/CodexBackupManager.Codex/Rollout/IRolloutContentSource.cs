using System;
using System.IO;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// rollout 파일 참조(<see cref="RolloutFileReference"/>)로 <b>원시(raw)</b> 바이트 스트림을 연다(Phase 9_2b-01, 설계 §6 "9_2b").
/// 압축 해제(<c>.jsonl.zst</c>)는 여기서 하지 않는다 — 읽는 쪽(<see cref="RolloutStreamReader"/>)이 <see cref="RolloutFileReference.Kind"/>를 보고
/// 기존 관리형 zstd 디코더로 푼다. 반환한 스트림은 호출자가 닫는다.
/// </summary>
/// <remarks>
/// 구현은 로컬 파일(<see cref="LocalFileRolloutContentSource"/>)과 백업 ZIP entry(<c>BackupRolloutContentSource</c>, Backup 계층)다.
/// 백업 쪽은 <see cref="RolloutFileReference.FullPath"/>가 ZIP entry 경로다. 둘 다 읽기 전용이고 임시 파일을 만들지 않는다.
/// 열 수 없으면 <see cref="IOException"/>(또는 <see cref="UnauthorizedAccessException"/>)을 던진다.
/// </remarks>
public interface IRolloutContentSource
{
    /// <summary>원시 스트림을 연다.</summary>
    Stream OpenRaw(RolloutFileReference file);
}

/// <summary>
/// 로컬 파일 시스템의 rollout 파일을 연다. 기존 동작(<see cref="RolloutStreamReader"/>의 경로 오버로드)과 같은 열기 옵션을 쓴다:
/// 읽기 전용, <see cref="FileShare.ReadWrite"/>(Codex가 쓰는 중이어도 읽는다).
/// </summary>
public sealed class LocalFileRolloutContentSource : IRolloutContentSource
{
    /// <summary>공유 인스턴스(상태 없음).</summary>
    public static readonly LocalFileRolloutContentSource Instance = new();

    private LocalFileRolloutContentSource()
    {
    }

    /// <inheritdoc />
    public Stream OpenRaw(RolloutFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
