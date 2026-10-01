using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore.Undo;

/// <summary>(Phase 9_4-02) 기록 종류.</summary>
public enum ImportHistoryKind
{
    /// <summary>가져오기.</summary>
    Import,

    /// <summary>사이드바 보정(<see cref="SidebarRepairService"/>).</summary>
    SidebarRepair,

    /// <summary>되돌리기.</summary>
    Undo,
}

/// <summary>(Phase 9_4-02) 기록 하나(목록용, 빠른 판정만 — 항목별 전제 조건은 <see cref="ImportUndoService.Assess"/>).</summary>
/// <param name="SnapshotId">Snapshot ID.</param>
/// <param name="SnapshotDirectory">Snapshot 디렉터리.</param>
/// <param name="CreatedAtUtc">만든 시각(Snapshot manifest).</param>
/// <param name="Kind">종류.</param>
/// <param name="State">journal 상태(없으면 <c>null</c>).</param>
/// <param name="JournalUnreadable">journal이 손상됐는지.</param>
/// <param name="Summary">가져오기 요약(9_4 이후 기록만).</param>
/// <param name="Availability">기록 단위 되돌리기 가능 여부(<see cref="UndoUnavailableReason.None"/>이면 기록이 있어 시도할 수 있다).</param>
/// <param name="UndoOfSnapshotId">되돌리기 기록이면 되돌린 가져오기의 Snapshot ID.</param>
public sealed record ImportHistoryEntry(
    string SnapshotId,
    string SnapshotDirectory,
    DateTimeOffset CreatedAtUtc,
    ImportHistoryKind Kind,
    RestoreTransactionState? State,
    bool JournalUnreadable,
    ImportRecordSummary? Summary,
    UndoUnavailableReason Availability,
    string? UndoOfSnapshotId)
{
    /// <summary>진행 중이거나 미완료(삭제 금지): Prepared/Applying/Undoing 또는 journal 손상.</summary>
    public bool IsInProgress => JournalUnreadable || State is RestoreTransactionState.Prepared or RestoreTransactionState.Applying or RestoreTransactionState.Undoing;
}

/// <summary>
/// (Phase 9_4-02) 현재 Codex Home의 가져오기·보정·되돌리기 기록 목록(최신순). <b>읽기 전용</b>이다 — manifest와 journal만 읽는다.
/// 다른 Home의 Snapshot은 넣지 않는다.
/// </summary>
public static class ImportHistoryService
{
    /// <summary>목록.</summary>
    public static IReadOnlyList<ImportHistoryEntry> List(string snapshotRoot, string codexHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        var entries = new List<ImportHistoryEntry>();
        if (!Directory.Exists(snapshotRoot))
        {
            return entries;
        }

        var raw = new List<(string Dir, SnapshotManifest Manifest, RestoreTransactionJournalReadResult Journal)>();
        foreach (string dir in Directory.EnumerateDirectories(snapshotRoot))
        {
            SnapshotManifest? manifest;
            try
            {
                manifest = SnapshotService.ReadManifest(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }

            if (manifest is null || !CanonicalPath.AreSameLocation(manifest.CodexHomePath, codexHomePath))
            {
                continue;
            }

            raw.Add((dir, manifest, RestoreTransactionJournalStore.TryReadDetailed(dir)));
        }

        var undoneByUndo = new HashSet<string>(
            raw.Where(r => r.Journal.Journal is { State: RestoreTransactionState.Completed, UndoOfSnapshotId: not null })
                .Select(r => r.Journal.Journal!.UndoOfSnapshotId!),
            StringComparer.Ordinal);

        foreach ((string dir, SnapshotManifest manifest, RestoreTransactionJournalReadResult read) in raw)
        {
            ImportHistoryKind kind = manifest.ImportPlanBackupSha256 == SidebarRepairService.SnapshotMarker
                ? ImportHistoryKind.SidebarRepair
                : manifest.ImportPlanBackupSha256.StartsWith(ImportUndoService.SnapshotMarkerPrefix, StringComparison.Ordinal)
                    ? ImportHistoryKind.Undo
                    : ImportHistoryKind.Import;
            RestoreTransactionJournal? journal = read.Journal;
            UndoUnavailableReason availability = kind switch
            {
                ImportHistoryKind.SidebarRepair => UndoUnavailableReason.SidebarRepair,
                ImportHistoryKind.Undo => UndoUnavailableReason.UndoRecord,
                _ when read.Status == RestoreTransactionJournalReadStatus.Corrupt => UndoUnavailableReason.RecordCorrupt,
                _ when journal is null => UndoUnavailableReason.OldFormat,
                _ when journal.State == RestoreTransactionState.Undone || undoneByUndo.Contains(manifest.SnapshotId) => UndoUnavailableReason.AlreadyUndone,
                _ when journal.State != RestoreTransactionState.Completed => UndoUnavailableReason.NotCompleted,
                _ when journal.UndoRecordSha256 is null || !File.Exists(Path.Combine(dir, UndoRecordStore.FileName)) => UndoUnavailableReason.OldFormat,
                _ => UndoUnavailableReason.None,
            };

            entries.Add(new ImportHistoryEntry(
                manifest.SnapshotId, dir, manifest.CreatedAtUtc, kind, journal?.State,
                read.Status == RestoreTransactionJournalReadStatus.Corrupt, journal?.Summary, availability, journal?.UndoOfSnapshotId));
        }

        return entries.OrderByDescending(e => e.CreatedAtUtc).ToList();
    }
}
