using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 백업(<c>.codexbackup</c>) 안의 rollout entry를 원시 스트림으로 연다(Phase 9_2b-01). 가져오기 화면의 대화 내용 미리보기용이다.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="BackupCatalogReader"/>가 만든 체인은 <see cref="RolloutFileReference.FullPath"/>가 ZIP entry 경로다.</item>
///   <item>
///     <see cref="Open"/>은 백업 파일을 <see cref="FileShare.ReadWrite"/>|<see cref="FileShare.Delete"/>로 연다. 미리보기가 열려 있는 동안에도
///     Plan 생성(백업 SHA-256 재확인)과 Apply가 막히지 않고, 파일이 바뀌면 그 확인이 잡아낸다. 미리보기 자체는 읽기 전용이다.
///   </item>
///   <item>
///     <see cref="System.IO.Compression.ZipArchive"/>는 동시 읽기를 지원하지 않으므로 entry를 열 때마다 내용을 메모리로 복사해
///     (<b>임시 파일을 만들지 않는다</b>) 잠금 안에서만 ZIP에 접근한다. 반환한 스트림은 이 인스턴스와 독립적이다.
///   </item>
/// </list>
/// </remarks>
public sealed class BackupRolloutContentSource : IRolloutContentSource, IDisposable
{
    private readonly BackupReader _reader;
    private readonly object _gate = new();
    private bool _disposed;

    private BackupRolloutContentSource(BackupReader reader) => _reader = reader;

    /// <summary>
    /// 이미 열린 <see cref="BackupReader"/>를 감싼다. reader의 수명은 호출자가 관리한다(<see cref="Dispose"/>는 reader를 닫지 않는다).
    /// </summary>
    public static BackupRolloutContentSource Wrap(BackupReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new BackupRolloutContentSource(reader) { OwnsReader = false };
    }

    /// <summary>백업 파일을 공유 읽기로 열어 미리보기용 source를 만든다(<see cref="Dispose"/> 때 닫는다).</summary>
    public static BackupRolloutContentSource Open(string backupFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        var stream = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return new BackupRolloutContentSource(BackupReader.OpenFromStream(stream, leaveOpen: false)) { OwnsReader = true };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private bool OwnsReader { get; init; }

    /// <summary>백업 entry로 대화 체인(분기·세그먼트)을 다시 만든다(<see cref="BackupCatalogReader.Build"/>, ZIP 접근은 잠금 안에서).</summary>
    public BackupCatalogReader.Result ReadCatalog(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return BackupCatalogReader.Build(_reader, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Stream OpenRaw(RolloutFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                using Stream entry = _reader.OpenEntry(file.FullPath)
                    ?? throw new FileNotFoundException("백업에서 대화 기록 항목을 찾을 수 없습니다.");
                var buffer = new MemoryStream();
                entry.CopyTo(buffer);
                buffer.Position = 0;
                return buffer;
            }
            catch (InvalidDataException ex)
            {
                // 손상된 ZIP entry — 읽는 쪽이 "파일을 읽을 수 없음" 경고로 처리하도록 IOException으로 바꾼다.
                throw new IOException("백업의 대화 기록 항목을 읽을 수 없습니다.", ex);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (OwnsReader)
            {
                _reader.Dispose();
            }
        }
    }
}
