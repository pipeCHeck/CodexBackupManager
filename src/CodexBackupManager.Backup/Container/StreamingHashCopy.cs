using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace CodexBackupManager.Backup.Container;

/// <summary>
/// 파일 전체를 메모리에 올리지 않고(<c>File.ReadAllBytes</c> 금지, 요구사항 7) 고정 크기 버퍼로
/// 스트리밍 복사하면서 SHA-256을 같은 경로에서 incremental로 계산한다.
/// </summary>
public static class StreamingHashCopy
{
    private const int BufferSize = 81920;

    /// <summary>복사 결과.</summary>
    /// <param name="ByteLength">복사한 바이트 수.</param>
    /// <param name="Sha256Hex">소문자 16진수 SHA-256.</param>
    public sealed record Result(long ByteLength, string Sha256Hex);

    /// <summary><paramref name="source"/>를 <paramref name="destination"/>으로 스트리밍 복사한다.</summary>
    public static Result CopyWithHash(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        byte[] digest = hash.GetHashAndReset();
        return new Result(total, Convert.ToHexStringLower(digest));
    }

    /// <summary><paramref name="source"/>의 SHA-256만 계산한다(쓰지 않는다).</summary>
    public static Result HashOnly(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        byte[] digest = hash.GetHashAndReset();
        return new Result(total, Convert.ToHexStringLower(digest));
    }
}
