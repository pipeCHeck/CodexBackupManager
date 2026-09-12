using System;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 로컬 <see cref="ConversationEntry"/>와 backup <see cref="BackupConversationMetadata"/>의 metadata
/// 차이를 비교한다. <see cref="RevisionRelation"/>(conversation 내용)과는 완전히 분리된 판정이다
/// (요구사항 8) — 여기서는 병합 정책을 정하지 않고 차이 여부만 보여준다.
/// </summary>
public static class MetadataDifferenceAnalyzer
{
    /// <summary>
    /// 차이를 비교한다. <paramref name="local"/>이 <c>null</c>이면(=backup에만 있는 New 대화) 비교
    /// 대상 자체가 없으므로 <see cref="MetadataDifferences.None"/>을 돌려준다.
    /// </summary>
    public static MetadataDifferences Compare(ConversationEntry? local, BackupConversationMetadata incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (local is null)
        {
            return MetadataDifferences.None;
        }

        ThreadRow row = local.Row;

        return new MetadataDifferences(
            CwdDiffers: !PathsEqual(row.Cwd, incoming.OriginalCwd),
            ProjectAssignmentDiffers: !string.Equals(local.Project.ProjectId, incoming.ResolvedProjectId, StringComparison.Ordinal),
            PinnedDiffers: row.IsPinned != incoming.IsPinned,
            SectionDiffers: !string.Equals(row.ThreadSectionId, incoming.ThreadSectionId, StringComparison.Ordinal),
            TitleDiffers: !string.Equals(row.Title, incoming.Title, StringComparison.Ordinal) ||
                          !string.Equals(row.Name, incoming.Name, StringComparison.Ordinal),
            RecencyDiffers: row.RecencyAtMs != incoming.RecencyAtMs);
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return CanonicalPath.AreSameLocation(a, b);
    }
}
