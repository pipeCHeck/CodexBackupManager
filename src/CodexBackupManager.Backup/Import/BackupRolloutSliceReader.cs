using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <c>.codexbackup</c>의 ZIP entry에서 직접 바이트를 읽는 <see cref="IRolloutSliceReader"/>.
/// <see cref="RolloutFileReference.FullPath"/>는 <see cref="BackupCatalogReader"/>가 넣어 둔 ZIP
/// entry 경로(<c>payload/rollouts/…</c>)로 해석한다.
/// </summary>
public sealed class BackupRolloutSliceReader(BackupReader reader) : IRolloutSliceReader
{
    private readonly BackupReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public RolloutSliceHasher.Result Hash(
        RolloutFileReference file,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        using Stream stream = _reader.OpenEntry(file.FullPath)
            ?? throw new InvalidDataException($"backup에서 entry를 찾을 수 없습니다: {file.FileName}");

        return RolloutSliceHasher.HashRawStream(stream, file.Kind, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, cancellationToken);
    }
}
