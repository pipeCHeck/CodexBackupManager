using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex.Rollout;

namespace CodexBackupManager.Codex.Rollout;

/// <summary>
/// <c>sessions\</c>와 <c>archived_sessions\</c>를 탐색해 rollout 파일 목록을 만든다.
/// </summary>
/// <remarks>
/// <b>파일 내용을 읽지 않는다.</b> 파일명과 경로만 보고 <see cref="RolloutFileReference"/>를 만든다.
/// Phase 0 실측 기준 1.45 GB / 365개 파일이므로, 열거만 하고 내용은 절대 메모리에 올리지 않는다.
/// </remarks>
public static class RolloutFileLocator
{
    /// <summary>
    /// Codex Home 아래에서 rollout 파일을 전부 찾는다.
    /// </summary>
    /// <param name="codexHome">Codex Home 경로.</param>
    /// <param name="cancellationToken">취소 토큰. 대용량 폴더 열거 중간에 취소할 수 있다.</param>
    public static IReadOnlyList<RolloutFileReference> Locate(string codexHome, CancellationToken cancellationToken = default)
    {
        var results = new List<RolloutFileReference>();
        LocateInto(Path.Combine(codexHome, CodexHomeLayout.SessionsDirectoryName), isArchived: false, results, cancellationToken);
        LocateInto(Path.Combine(codexHome, CodexHomeLayout.ArchivedSessionsDirectoryName), isArchived: true, results, cancellationToken);
        return results;
    }

    private static void LocateInto(
        string directory,
        bool isArchived,
        List<RolloutFileReference> results,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(fullPath);
            RolloutFileNamePattern.ParsedRolloutFileName? parsed = RolloutFileNamePattern.TryParse(fileName);
            if (parsed is null)
            {
                // 이름 규칙에 맞지 않는 파일(다른 목적의 파일일 수 있다)은 조용히 건너뛴다.
                continue;
            }

            results.Add(new RolloutFileReference(
                FullPath: fullPath,
                FileName: fileName,
                ThreadId: parsed.ThreadId,
                SegmentId: parsed.SegmentId,
                TimestampFromFileName: parsed.Timestamp,
                IsArchived: isArchived,
                Kind: parsed.Kind));
        }
    }
}
