using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CodexBackupManager.Domain.Codex.Rollout;
using ZstdSharp;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// rollout 파일을 한 줄씩 스트리밍으로 읽는다. <c>.jsonl</c>과 <c>.jsonl.zst</c> 둘 다 지원한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>파일 전체를 문자열이나 <c>JsonDocument</c> 하나로 올리지 않는다.</b> 단일 세션이 12.8 MB까지
/// 관측되었고 전체는 1.45 GB이므로(docs/codex-storage-format.md §2), 항상 한 줄 단위로 처리한다.
/// </para>
/// <para>
/// <c>.zst</c> 압축은 <c>ZstdSharp.Port</c>(순수 관리형 구현)로 해제한다.
/// 외부 <c>zstd.exe</c>를 실행하지 않는다(CLAUDE.md §2.1).
/// </para>
/// </remarks>
public static class RolloutStreamReader
{
    /// <summary>
    /// 파일을 한 줄씩 읽는다. 파일을 열 수 없으면 예외를 전파한다(호출자가 처리).
    /// </summary>
    /// <param name="filePath">rollout 파일 경로.</param>
    /// <param name="kind">압축 형태.</param>
    /// <param name="cancellationToken">취소 토큰. 줄 단위로 확인한다.</param>
    public static IEnumerable<string> ReadLines(
        string filePath,
        RolloutFileKind kind,
        CancellationToken cancellationToken = default)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (string line in ReadLines(fileStream, kind, cancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// 이미 열린 원시(raw) 스트림에서 한 줄씩 읽는다(Phase 6 — backup ZIP entry처럼 파일 경로가 없는
    /// 출처에서도 같은 압축 해제/줄 읽기 로직을 재사용하기 위함). 호출자가 스트림 소유권을 가진다 —
    /// 이 메서드는 <c>.zst</c>면 내부적으로 <see cref="DecompressionStream"/>으로 감싸 읽을 뿐,
    /// 전달받은 <paramref name="rawStream"/>은 닫지 않는다(leaveOpen).
    /// </summary>
    public static IEnumerable<string> ReadLines(
        Stream rawStream,
        RolloutFileKind kind,
        CancellationToken cancellationToken = default)
    {
        // rawStream은 호출자 소유다. kind가 PlainJsonl이면 contentStream은 rawStream 그 자체이므로
        // "using Stream contentStream = ..." 형태로 감쌌다가는 여기서 rawStream까지 닫아버린다 —
        // DecompressionStream을 새로 만든 경우에만 그것만 닫는다.
        Stream? decompression = kind == RolloutFileKind.ZstdCompressed
            ? new DecompressionStream(rawStream, leaveOpen: true)
            : null;
        Stream contentStream = decompression ?? rawStream;

        try
        {
            using StreamReader reader = new(contentStream, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }
        }
        finally
        {
            decompression?.Dispose();
        }
    }

    /// <summary>
    /// 처음 몇 줄만 읽고 멈춘다. <c>session_meta</c>처럼 파일 앞부분만 필요한 경우에 쓴다.
    /// </summary>
    /// <param name="filePath">rollout 파일 경로.</param>
    /// <param name="kind">압축 형태.</param>
    /// <param name="maxLines">최대로 읽을 줄 수.</param>
    public static IEnumerable<string> ReadFirstLines(string filePath, RolloutFileKind kind, int maxLines)
    {
        int count = 0;
        foreach (string line in ReadLines(filePath, kind))
        {
            if (count >= maxLines)
            {
                yield break;
            }

            count++;
            yield return line;
        }
    }

    /// <summary>처음 몇 줄만 읽고 멈춘다(스트림 버전, 호출자가 스트림을 소유·정리한다).</summary>
    public static IEnumerable<string> ReadFirstLines(Stream rawStream, RolloutFileKind kind, int maxLines)
    {
        int count = 0;
        foreach (string line in ReadLines(rawStream, kind))
        {
            if (count >= maxLines)
            {
                yield break;
            }

            count++;
            yield return line;
        }
    }

    /// <summary>
    /// 줄과 함께 그 줄의 <b>UTF-8 바이트</b> 시작/끝(미포함) 위치를 돌려준다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>history_base.end_byte_offset</c>은 문자 위치가 아니라 원본 파일의 바이트 위치다.
    /// 한글처럼 UTF-8에서 여러 바이트를 쓰는 문자가 섞여 있으면 <see cref="StreamReader"/>가 센
    /// 문자 수와 실제 바이트 수가 달라지므로, 이 메서드는 <b>문자를 세지 않고 원시 바이트를 직접 센다.</b>
    /// </para>
    /// <para>
    /// <c>ordinal</c> 기반 판정(<see cref="RolloutStreamReader"/> 사용부인
    /// <c>ConversationItemParser</c> 참고)이 항상 가능하므로 이 메서드는 그것을 쓸 수 없을 때만 쓰는
    /// 폴백이다. 줄 구분자는 <c>\n</c> 하나만 인식한다(rollout 파일은 LF만 쓴다, 실측 확인).
    /// </para>
    /// </remarks>
    public static IEnumerable<(string Line, long StartByteOffsetInclusive, long EndByteOffsetExclusive)> ReadLinesWithByteOffsets(
        string filePath,
        RolloutFileKind kind,
        CancellationToken cancellationToken = default)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream contentStream = kind == RolloutFileKind.ZstdCompressed
            ? new DecompressionStream(fileStream, leaveOpen: true)
            : fileStream;
        using BufferedStream buffered = new(contentStream);

        long offset = 0;
        var lineBytes = new List<byte>();

        int b;
        while ((b = buffered.ReadByte()) != -1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset++;

            if (b == '\n')
            {
                long start = offset - lineBytes.Count - 1;
                string line = Encoding.UTF8.GetString(lineBytes.ToArray());
                lineBytes.Clear();
                yield return (line, start, offset);
                continue;
            }

            lineBytes.Add((byte)b);
        }

        if (lineBytes.Count > 0)
        {
            long start = offset - lineBytes.Count;
            string line = Encoding.UTF8.GetString(lineBytes.ToArray());
            yield return (line, start, offset);
        }
    }
}
