using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;

namespace CodexBackupManager.Codex.Titles;

/// <summary>
/// 대화 제목을 우선순위대로 결정한다. docs/codex-storage-format.md §5 "제목의 출처".
/// </summary>
/// <remarks>rollout <c>session_meta</c>에 제목이 있다고 가정하지 않는다 — 실제로 없다.</remarks>
public static class ThreadTitleResolver
{
    /// <summary>
    /// 우선순위: <c>threads.name</c> → <c>session_index.thread_name</c>(last-wins) →
    /// <c>threads.title</c> → <c>first_user_message</c> → <c>preview</c> → thread ID 기반 기본값.
    /// </summary>
    /// <param name="thread">제목을 결정할 thread.</param>
    /// <param name="sessionIndexTitles"><see cref="Inspection.SessionIndexReader.ReadTitleMap"/>의 결과.</param>
    public static ThreadTitle Resolve(ThreadRow thread, IReadOnlyDictionary<string, string> sessionIndexTitles)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(sessionIndexTitles);

        if (!string.IsNullOrWhiteSpace(thread.Name))
        {
            return new ThreadTitle(thread.Name, ThreadTitleSource.StateName);
        }

        if (sessionIndexTitles.TryGetValue(thread.Id, out string? sessionIndexName) &&
            !string.IsNullOrWhiteSpace(sessionIndexName))
        {
            return new ThreadTitle(sessionIndexName, ThreadTitleSource.SessionIndex);
        }

        if (!string.IsNullOrWhiteSpace(thread.Title))
        {
            return new ThreadTitle(thread.Title, ThreadTitleSource.StateTitle);
        }

        if (!string.IsNullOrWhiteSpace(thread.FirstUserMessage))
        {
            return new ThreadTitle(thread.FirstUserMessage, ThreadTitleSource.FirstUserMessage);
        }

        if (!string.IsNullOrWhiteSpace(thread.Preview))
        {
            return new ThreadTitle(thread.Preview, ThreadTitleSource.Preview);
        }

        return new ThreadTitle(FallbackDisplayName(thread.Id), ThreadTitleSource.ThreadIdFallback);
    }

    private static string FallbackDisplayName(string threadId)
    {
        string shortId = threadId.Length <= 8 ? threadId : threadId[..8];
        return $"대화 {shortId}";
    }
}
