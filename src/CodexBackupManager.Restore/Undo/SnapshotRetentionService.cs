using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexBackupManager.Restore.Undo;

/// <summary>(Phase 9_4-05) Snapshot을 지우지 않은 이유.</summary>
public enum RetentionRefusal
{
    /// <summary>진행 중이거나 미완료(Prepared/Applying/Undoing, journal 손상).</summary>
    InProgress,

    /// <summary>현재 Codex Home의 기록이 아니다(목록에 없다).</summary>
    NotInHistory,

    /// <summary>되돌리기 가능한 기록이라 확인이 필요하다(<c>allowUndoable</c> 없이 요청됨).</summary>
    UndoableNeedsConfirmation,

    /// <summary>경로가 Snapshot 루트 바로 아래가 아니거나 링크·정션이 있다.</summary>
    UnsafePath,

    /// <summary>지우는 중 오류.</summary>
    DeleteFailed,
}

/// <summary>삭제 결과.</summary>
public sealed record RetentionResult(int DeletedCount, IReadOnlyList<(string SnapshotId, RetentionRefusal Reason)> Refused)
{
    /// <summary>되돌리기 가능 기록 때문에 확인이 필요한지.</summary>
    public bool NeedsUndoableConfirmation => Refused.Any(r => r.Reason == RetentionRefusal.UndoableNeedsConfirmation);
}

/// <summary>
/// (Phase 9_4-05) Snapshot 정리. <b>자동 삭제는 없다</b> — 사용자가 고른 기록, 또는 "30일 이상 지났고 되돌리기 불가인 기록"만, 확인을 받은 뒤 지운다.
/// </summary>
/// <remarks>
/// 삭제 금지: 진행 중·미완료 기록, 다른 Home의 Snapshot(목록에 없음). 되돌리기 가능한 기록은 <c>allowUndoable</c>로 다시 요청해야 지운다
/// ("되돌리기를 할 수 없게 됩니다" 확인을 거친 뒤). 지우는 것은 Snapshot 루트 바로 아래의 그 디렉터리뿐이고, 루트 밖·링크·정션은 거부한다.
/// 같은 Home의 Apply/되돌리기와 겹치지 않게 프로세스 락을 잡는다.
/// </remarks>
public static class SnapshotRetentionService
{
    /// <summary>정리 기준 나이.</summary>
    public static readonly TimeSpan CleanupAge = TimeSpan.FromDays(30);

    /// <summary>"30일 이상 지났고 되돌리기 불가인 기록" 후보(읽기 전용).</summary>
    public static IReadOnlyList<ImportHistoryEntry> FindCleanupCandidates(string snapshotRoot, string codexHomePath, DateTimeOffset now)
        => ImportHistoryService.List(snapshotRoot, codexHomePath)
            .Where(e => !e.IsInProgress && e.Availability != UndoUnavailableReason.None && now - e.CreatedAtUtc >= CleanupAge)
            .ToList();

    /// <summary>고른 기록을 지운다.</summary>
    public static RetentionResult Delete(string snapshotRoot, string codexHomePath, IReadOnlyCollection<string> snapshotIds, bool allowUndoable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(snapshotIds);

        var refused = new List<(string, RetentionRefusal)>();
        RestoreProcessLock.AcquireResult lockResult = RestoreProcessLock.TryAcquire(codexHomePath, TimeSpan.Zero);
        if (!lockResult.Acquired)
        {
            return new RetentionResult(0, snapshotIds.Select(id => (id, RetentionRefusal.InProgress)).ToList());
        }

        try
        {
            Dictionary<string, ImportHistoryEntry> history = ImportHistoryService.List(snapshotRoot, codexHomePath)
                .ToDictionary(e => e.SnapshotId, StringComparer.Ordinal);
            var allowed = new List<ImportHistoryEntry>();
            foreach (string id in snapshotIds.Distinct(StringComparer.Ordinal))
            {
                if (!history.TryGetValue(id, out ImportHistoryEntry? entry))
                {
                    refused.Add((id, RetentionRefusal.NotInHistory));
                }
                else if (entry.IsInProgress)
                {
                    refused.Add((id, RetentionRefusal.InProgress));
                }
                else if (entry.Availability == UndoUnavailableReason.None && !allowUndoable)
                {
                    refused.Add((id, RetentionRefusal.UndoableNeedsConfirmation));
                }
                else if (!IsSafeSnapshotDirectory(snapshotRoot, entry.SnapshotDirectory))
                {
                    refused.Add((id, RetentionRefusal.UnsafePath));
                }
                else
                {
                    allowed.Add(entry);
                }
            }

            // 되돌리기 가능 기록 확인이 필요하면 아무것도 지우지 않는다(사용자가 경고를 보고 다시 요청하게).
            if (refused.Any(r => r.Item2 == RetentionRefusal.UndoableNeedsConfirmation))
            {
                return new RetentionResult(0, refused);
            }

            int deleted = 0;
            foreach (ImportHistoryEntry entry in allowed)
            {
                try
                {
                    Directory.Delete(entry.SnapshotDirectory, recursive: true);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    refused.Add((entry.SnapshotId, RetentionRefusal.DeleteFailed));
                }
            }

            return new RetentionResult(deleted, refused);
        }
        finally
        {
            lockResult.Handle!.Dispose();
        }
    }

    /// <summary>Snapshot 루트 바로 아래의 실제 디렉터리이고, 그 안(과 루트)에 링크·정션이 없는지.</summary>
    internal static bool IsSafeSnapshotDirectory(string snapshotRoot, string directory)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rootInfo = new DirectoryInfo(root);
            var info = new DirectoryInfo(full);
            if (!info.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            return !info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Any(i => i.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
