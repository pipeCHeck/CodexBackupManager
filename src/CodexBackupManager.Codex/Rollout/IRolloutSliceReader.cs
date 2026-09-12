using System.Threading;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// <see cref="RolloutSliceHasher"/>가 실제로 바이트를 어디서 읽어올지를 추상화한다. 로컬 파일과
/// backup ZIP entry가 같은 <see cref="Threads.ConversationRevisionBuilder"/> 코드를 공유하기 위한
/// 유일한 변경 지점이다 — revision 계산 로직 자체는 이 인터페이스 뒤에서 무엇을 비교하는지 몰라도 된다.
/// </summary>
public interface IRolloutSliceReader
{
    /// <summary><paramref name="file"/>의 지정된 슬라이스(컷오프)를 해시한다.</summary>
    RolloutSliceHasher.Result Hash(
        RolloutFileReference file,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken = default);
}

/// <summary>로컬 파일 시스템의 rollout 파일을 직접 여는 <see cref="IRolloutSliceReader"/>.</summary>
public sealed class LocalFileRolloutSliceReader : IRolloutSliceReader
{
    /// <summary>공유 인스턴스. 상태가 없으므로 매번 새로 만들 필요가 없다.</summary>
    public static readonly LocalFileRolloutSliceReader Instance = new();

    /// <inheritdoc />
    public RolloutSliceHasher.Result Hash(
        RolloutFileReference file,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken = default)
        => RolloutSliceHasher.HashFile(file, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, cancellationToken);
}
