using System;
using System.Collections.Generic;
using System.IO;
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
        using Stream contentStream = kind == RolloutFileKind.ZstdCompressed
            ? new DecompressionStream(fileStream, leaveOpen: true)
            : fileStream;
        using StreamReader reader = new(contentStream);

        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
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
}
