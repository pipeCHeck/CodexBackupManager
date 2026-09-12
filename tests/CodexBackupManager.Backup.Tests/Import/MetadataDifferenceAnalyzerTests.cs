using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Import;

/// <summary>
/// <see cref="MetadataDifferenceAnalyzer"/>는 <see cref="RevisionRelation"/>과 완전히 별개로 metadata
/// 차이만 비교한다(요구사항 8) — 여기서 병합 정책을 정하지 않는다.
/// </summary>
public sealed class MetadataDifferenceAnalyzerTests
{
    private static ConversationEntry LocalEntry(string threadId, string? cwd, bool isPinned, string? title, string? name, long? recencyAtMs, string? resolvedProjectId)
        => new()
        {
            ThreadId = threadId,
            Row = new ThreadRow { Id = threadId, Cwd = cwd, IsPinned = isPinned, Title = title, Name = name, RecencyAtMs = recencyAtMs, ThreadSectionId = null },
            Title = new ThreadTitle(title ?? threadId, ThreadTitleSource.StateTitle),
            Project = new ProjectAssignment(resolvedProjectId, resolvedProjectId is null ? ProjectAssignmentSource.Unassigned : ProjectAssignmentSource.StateProjectId),
        };

    private static BackupConversationMetadata Incoming(string threadId, string? cwd, bool isPinned, string? title, string? name, long? recencyAtMs, string? resolvedProjectId)
        => new()
        {
            ThreadId = threadId,
            IsSelected = true,
            OriginalCwd = cwd,
            IsPinned = isPinned,
            Title = title,
            Name = name,
            RecencyAtMs = recencyAtMs,
            ResolvedProjectId = resolvedProjectId,
            ThreadSectionId = null,
            PayloadRolloutEntries = [],
            PayloadAttachmentEntries = [],
        };

    [Fact]
    public void 전부_같으면_차이가_없다()
    {
        ConversationEntry local = LocalEntry("t", @"C:\P", true, "제목", "이름", 100, "proj-1");
        BackupConversationMetadata incoming = Incoming("t", @"C:\P", true, "제목", "이름", 100, "proj-1");

        MetadataDifferences diff = MetadataDifferenceAnalyzer.Compare(local, incoming);

        Assert.False(diff.HasAny);
    }

    [Fact]
    public void cwd가_다르면_CwdDiffers()
    {
        ConversationEntry local = LocalEntry("t", @"C:\Old", false, null, null, null, null);
        BackupConversationMetadata incoming = Incoming("t", @"D:\New", false, null, null, null, null);

        MetadataDifferences diff = MetadataDifferenceAnalyzer.Compare(local, incoming);

        Assert.True(diff.CwdDiffers);
        Assert.True(diff.HasAny);
    }

    [Fact]
    public void 대소문자만_다른_cwd는_차이로_보지_않는다()
    {
        ConversationEntry local = LocalEntry("t", @"C:\Proj", false, null, null, null, null);
        BackupConversationMetadata incoming = Incoming("t", @"c:\proj", false, null, null, null, null);

        MetadataDifferences diff = MetadataDifferenceAnalyzer.Compare(local, incoming);

        Assert.False(diff.CwdDiffers);
    }

    [Fact]
    public void pinned이_다르면_PinnedDiffers()
    {
        ConversationEntry local = LocalEntry("t", null, false, null, null, null, null);
        BackupConversationMetadata incoming = Incoming("t", null, true, null, null, null, null);

        Assert.True(MetadataDifferenceAnalyzer.Compare(local, incoming).PinnedDiffers);
    }

    [Fact]
    public void 프로젝트_연결이_다르면_ProjectAssignmentDiffers()
    {
        ConversationEntry local = LocalEntry("t", null, false, null, null, null, "proj-old");
        BackupConversationMetadata incoming = Incoming("t", null, false, null, null, null, "proj-new");

        Assert.True(MetadataDifferenceAnalyzer.Compare(local, incoming).ProjectAssignmentDiffers);
    }

    [Fact]
    public void 로컬이_없으면_New_대화라_비교_대상이_없어_None이다()
    {
        BackupConversationMetadata incoming = Incoming("t", @"C:\P", true, "제목", null, 100, "proj-1");

        MetadataDifferences diff = MetadataDifferenceAnalyzer.Compare(null, incoming);

        Assert.Equal(MetadataDifferences.None, diff);
    }
}
