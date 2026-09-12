using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Reading;

namespace CodexBackupManager.Restore;

/// <summary>
/// Apply 전체(계획 수립 + 실제 적용) 동안 "검증했던 바로 그 backup bytes"만 쓰도록 강제한다
/// (Phase 07_01 — 요구사항 5). backup 파일을 <b>한 번만</b> 열어 identity(길이+SHA-256)를 확인하고,
/// 그 뒤 <see cref="Reader"/>를 <see cref="RestoreOperationPlanner"/>와 실제 mutation 양쪽에
/// 계속 재사용한다 — Planner가 따로 열고, Executor가 또 따로 여는 구조(각자 "지금 이 경로의 파일"을
/// 새로 채택하는 TOCTOU)를 없앤다.
/// </summary>
public sealed class PinnedBackupSource : IDisposable
{
    private readonly FileStream _stream;

    private PinnedBackupSource(string backupFilePath, FileStream stream, BackupReader reader, long byteLength, string sha256Hex)
    {
        BackupFilePath = backupFilePath;
        _stream = stream;
        Reader = reader;
        ByteLength = byteLength;
        Sha256Hex = sha256Hex;
    }

    /// <summary>이 source가 열린 파일 경로(진단용).</summary>
    public string BackupFilePath { get; }

    /// <summary>이 pinned 스트림 위에서 동작하는 <see cref="BackupReader"/>. Apply가 끝날 때까지 계속 산다.</summary>
    public BackupReader Reader { get; }

    /// <summary>연 시점에 스트리밍으로 확인한 파일 전체 길이.</summary>
    public long ByteLength { get; }

    /// <summary>연 시점에 스트리밍으로 확인한 파일 전체의 SHA-256(소문자 hex).</summary>
    public string Sha256Hex { get; }

    /// <summary>
    /// <paramref name="backupFilePath"/>를 한 번 열어 길이+SHA-256을 스트리밍으로 계산한 뒤, 같은
    /// 스트림 위에서 ZIP을 다시 파싱한다(<c>Seek(0)</c> — 파일을 다시 열지 않는다). 이 인스턴스가
    /// 살아있는 동안 <see cref="Reader"/>가 참조하는 바이트는 이 시점에 확인한 바로 그 바이트다.
    /// </summary>
    public static PinnedBackupSource Open(string backupFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);

        FileStream stream = new(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            StreamingHashCopy.Result hashed = StreamingHashCopy.HashOnly(stream, cancellationToken);
            stream.Seek(0, SeekOrigin.Begin);
            BackupReader reader = BackupReader.OpenFromStream(stream, leaveOpen: true);
            return new PinnedBackupSource(backupFilePath, stream, reader, hashed.ByteLength, hashed.Sha256Hex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 지금 이 pinned identity가 frozen <see cref="ImportBackupIdentity"/>와 길이+SHA-256 기준으로
    /// 정확히 같은지. 다르면 이 source를 아예 계획 수립에 쓰지 않아야 한다.
    /// </summary>
    public bool MatchesExpected(ImportBackupIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ByteLength == expected.BackupFileLength &&
            string.Equals(Sha256Hex, expected.BackupFileSha256, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Reader.Dispose();
        _stream.Dispose();
    }
}
