using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using CodexBackupManager.Domain.Codex.Rollout;
using ZstdSharp;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// rollout 파일(또는 그 논리적 슬라이스)의 SHA-256/길이를 스트리밍으로 계산한다. Phase 6 Import
/// Preview의 revision fingerprint(<see cref="Domain.Codex.Import.ConversationRevision"/>)가 쓴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>경계 판정은 <see cref="Conversation.ConversationItemParser"/>와 완전히 같다</b> — ordinal이
/// 있으면 ordinal 우선, 없을 때만 byte offset. 여기서 새로운 cutoff 규칙을 만들지 않는다. ordinal
/// 컷오프는 <c>ordinal &gt;= cutoff</c>인 줄부터 제외하고, byte 컷오프는 그 줄까지 포함했을 때의
/// 끝 위치가 컷오프를 넘으면 제외한다 — 둘 다 <c>ConversationItemParser.Parse</c>와 동일한 경계다.
/// </para>
/// <para>
/// 전체 파일을 메모리에 올리지 않는다. 컷오프가 없으면(온전한 파일 전체) 8KB 고정 버퍼로 그대로
/// 스트리밍 복사+해시한다(가장 흔한 경로 — leaf 자신의 최신 파일). 컷오프가 있을 때만 줄 단위로
/// 원시 바이트를 모았다가(한 줄 크기만큼만) <see cref="IncrementalHash"/>에 먹인다.
/// </para>
/// <para>
/// <b>재인코딩으로 인한 바이트 불일치를 피한다.</b> 줄을 <see cref="Encoding"/>으로 디코드했다가
/// 다시 인코드하지 않고, 원시 바이트를 그대로 모아 해시에 넣는다 — 손상된/비표준 UTF-8 바이트가
/// 있어도(드물지만) 원본과 다른 바이트를 해시하지 않는다. ordinal 값만 확인할 때는
/// <see cref="JsonDocument.Parse(System.ReadOnlySpan{byte})"/>로 바이트에서 바로 파싱한다.
/// </para>
/// </remarks>
public static class RolloutSliceHasher
{
    /// <summary>해시 결과.</summary>
    /// <param name="LogicalByteLength">포함된 논리적(압축 해제된) 바이트 길이.</param>
    /// <param name="Sha256Hex">포함된 바이트의 SHA-256(소문자 hex).</param>
    public readonly record struct Result(long LogicalByteLength, string Sha256Hex);

    /// <summary>로컬 rollout 파일을 직접 연다.</summary>
    public static Result HashFile(
        RolloutFileReference file,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        using FileStream fileStream = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return HashRawStream(fileStream, file.Kind, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, cancellationToken);
    }

    /// <summary>
    /// 이미 열린 원시 스트림(로컬 파일 또는 backup ZIP entry)을 해시한다. 호출자가 스트림을 소유·정리한다.
    /// </summary>
    public static Result HashRawStream(
        Stream rawStream,
        RolloutFileKind kind,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawStream);

        Stream? decompression = kind == RolloutFileKind.ZstdCompressed
            ? new DecompressionStream(rawStream, leaveOpen: true)
            : null;
        Stream content = decompression ?? rawStream;

        try
        {
            return HashDecompressedContent(content, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, cancellationToken);
        }
        finally
        {
            decompression?.Dispose();
        }
    }

    private static Result HashDecompressedContent(
        Stream content,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (cutoffOrdinalExclusive is null && cutoffByteOffsetExclusive is null)
        {
            // 컷오프가 없으면 줄 경계를 신경 쓸 필요가 없다 — 그냥 스트리밍 복사+해시.
            long total = 0;
            byte[] buffer = new byte[81920];
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, read);
                total += read;
            }

            return new Result(total, Convert.ToHexStringLower(hasher.GetHashAndReset()));
        }

        long totalIncluded = 0;
        var lineBytes = new List<byte>();

        int b;
        while ((b = content.ReadByte()) != -1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (b != '\n')
            {
                lineBytes.Add((byte)b);
                continue;
            }

            if (!TryIncludeLine(hasher, lineBytes, includeNewline: true, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, totalIncluded, out long updatedTotal))
            {
                return new Result(totalIncluded, Convert.ToHexStringLower(hasher.GetHashAndReset()));
            }

            totalIncluded = updatedTotal;
            lineBytes.Clear();
        }

        if (lineBytes.Count > 0)
        {
            TryIncludeLine(hasher, lineBytes, includeNewline: false, cutoffOrdinalExclusive, cutoffByteOffsetExclusive, totalIncluded, out totalIncluded);
        }

        return new Result(totalIncluded, Convert.ToHexStringLower(hasher.GetHashAndReset()));
    }

    /// <summary>
    /// 한 줄을 컷오프와 비교해 포함할지 정하고, 포함하면 해시에 먹인다.
    /// <c>false</c>를 돌려주면 호출자는 여기서 멈춘다(이 줄부터는 제외).
    /// </summary>
    private static bool TryIncludeLine(
        IncrementalHash hasher,
        List<byte> lineBytes,
        bool includeNewline,
        long? cutoffOrdinalExclusive,
        long? cutoffByteOffsetExclusive,
        long totalBefore,
        out long totalAfter)
    {
        long lineLengthInclNewline = lineBytes.Count + (includeNewline ? 1 : 0);
        long endOffsetExclusive = totalBefore + lineLengthInclNewline;

        // ConversationItemParser.Parse와 동일한 우선순위: ordinal을 쓸 수 있으면 ordinal만 본다.
        if (cutoffOrdinalExclusive is { } ordinalCutoff)
        {
            long? ordinal = TryGetOrdinal(lineBytes);
            if (ordinal is { } ord && ord >= ordinalCutoff)
            {
                totalAfter = totalBefore;
                return false;
            }
        }
        else if (cutoffByteOffsetExclusive is { } byteCutoff && endOffsetExclusive > byteCutoff)
        {
            totalAfter = totalBefore;
            return false;
        }

        byte[] array = lineBytes.ToArray();
        hasher.AppendData(array, 0, array.Length);
        if (includeNewline)
        {
            hasher.AppendData([(byte)'\n']);
        }

        totalAfter = endOffsetExclusive;
        return true;
    }

    private static long? TryGetOrdinal(List<byte> lineBytes)
    {
        if (lineBytes.Count == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(lineBytes.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ordinal", out JsonElement ordinalElement) &&
                ordinalElement.ValueKind == JsonValueKind.Number &&
                ordinalElement.TryGetInt64(out long ordinal))
            {
                return ordinal;
            }
        }
        catch (JsonException)
        {
            // 손상/잘린 줄 — ordinal을 판정할 수 없다. ConversationItemParser.ProcessLine도 이런 줄은
            // (JSON 파싱 실패) 그냥 건너뛰고 계속 진행하므로, 여기서도 배제하지 않고 포함한다.
        }

        return null;
    }
}
