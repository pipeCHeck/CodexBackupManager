using System;
using System.IO;
using System.IO.Compression;
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
///     백업 파일은 항상 <see cref="FileShare.ReadWrite"/>|<see cref="FileShare.Delete"/>로 연다. 미리보기가 열려 있는 동안에도
///     Plan 생성(백업 SHA-256 재확인)과 Apply가 막히지 않고, 파일이 바뀌면 그 확인이 잡아낸다. 미리보기 자체는 읽기 전용이다.
///   </item>
///   <item>
///     (Phase 9_2b-06) <b>entry를 메모리로 복사하지 않는다.</b> <see cref="System.IO.Compression.ZipArchive"/>는 동시 읽기를 지원하지 않으므로
///     <see cref="OpenRaw"/>는 호출마다 백업 파일과 읽기 전용 archive를 따로 열어 entry 스트림을 그대로 돌려준다(읽기 단위 격리, 잠금 없음).
///     돌려준 스트림을 Dispose하면 그 archive와 파일도 닫힌다. 임시 파일을 만들지 않는다.
///   </item>
///   <item>인스턴스가 들고 있는 reader는 <see cref="ReadCatalog"/>(앞부분 메타데이터 스트리밍 읽기)에만 쓴다.</item>
/// </list>
/// </remarks>
public sealed class BackupRolloutContentSource : IRolloutContentSource, IDisposable
{
    private const FileShare PreviewShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _backupFilePath;
    private readonly BackupReader _catalogReader;
    private readonly object _gate = new();
    private volatile bool _disposed;

    private BackupRolloutContentSource(string backupFilePath, BackupReader catalogReader)
    {
        _backupFilePath = backupFilePath;
        _catalogReader = catalogReader;
    }

    /// <summary>백업 파일을 공유 읽기로 열어 미리보기용 source를 만든다(<see cref="Dispose"/> 때 닫는다).</summary>
    public static BackupRolloutContentSource Open(string backupFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        string fullPath = Path.GetFullPath(backupFilePath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, PreviewShare);
        try
        {
            return new BackupRolloutContentSource(fullPath, BackupReader.OpenFromStream(stream, leaveOpen: false));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>백업 entry로 대화 체인(분기·세그먼트)을 다시 만든다(<see cref="BackupCatalogReader.Build"/>, 공유 reader 접근은 잠금 안에서).</summary>
    public BackupCatalogReader.Result ReadCatalog(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return BackupCatalogReader.Build(_catalogReader, cancellationToken);
        }
    }

    /// <inheritdoc />
    /// <remarks>반환 스트림은 seek할 수 없을 수 있다(deflate). 파서는 앞에서부터 읽으며 바이트를 센다.</remarks>
    public Stream OpenRaw(RolloutFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(_disposed, this);

        FileStream? fileStream = null;
        ZipArchive? archive = null;
        try
        {
            fileStream = new FileStream(_backupFilePath, FileMode.Open, FileAccess.Read, PreviewShare);
            archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry entry = archive.GetEntry(file.FullPath)
                ?? throw new FileNotFoundException("백업에서 대화 기록 항목을 찾을 수 없습니다.");
            return new EntryReadStream(entry.Open(), archive);
        }
        catch (InvalidDataException ex)
        {
            // 손상된 ZIP/entry — 읽는 쪽이 "파일을 읽을 수 없음" 경고로 처리하도록 IOException으로 바꾼다.
            Close(archive, fileStream);
            throw new IOException("백업의 대화 기록 항목을 읽을 수 없습니다.", ex);
        }
        catch
        {
            Close(archive, fileStream);
            throw;
        }
    }

    private static void Close(ZipArchive? archive, FileStream? fileStream)
    {
        if (archive is not null)
        {
            archive.Dispose(); // 파일 스트림도 닫는다(leaveOpen: false)
        }
        else
        {
            fileStream?.Dispose();
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
            _catalogReader.Dispose();
        }
    }

    /// <summary>entry 스트림을 앞으로만 읽게 감싸고, Dispose 때 그 호출 전용 archive(와 파일)를 함께 닫는다.</summary>
    private sealed class EntryReadStream(Stream entry, ZipArchive archive) : Stream
    {
        private bool _closed;

        public override bool CanRead => !_closed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => entry.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => entry.Read(buffer);

        public override int ReadByte() => entry.ReadByte();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                entry.Dispose();
                archive.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
