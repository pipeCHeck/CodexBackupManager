using System.Collections.Generic;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_1-10 — Import Preview 행 문구가 목적지 판정(<see cref="ImportProjectPreview.SuggestedTarget"/>)과
/// 이 PC 위치(<see cref="ImportConversationPreview.LocalLocation"/>)를 기준으로 나오는지.
/// </summary>
public sealed class ImportPreviewRowTextTests
{
    private const string Folder = @"C:\Fixture\Target";

    private static readonly ProjectDirectory Directory = new(
    [
        new KnownProject("db-1", "db-1", new HashSet<string>(), "알파", [new KnownProjectRoot(Folder, CanonicalPath.Create(Folder), true)], 0),
    ]);

    private static ImportProjectRowViewModel Row(ProjectTarget? target, ProjectPathMappingStatus status = ProjectPathMappingStatus.ManuallyLinked)
    {
        var mapping = new ProjectPathMapping("pcA-1", "X", [@"C:\Orig"], status, null, Folder);
        var preview = new ImportProjectPreview("pcA-1", "X", mapping, []) { SuggestedTarget = target };
        return new ImportProjectRowViewModel(preview, requestPathOverride: null, Directory);
    }

    [Fact]
    public void LinkExisting이면_연결될_프로젝트_이름을_보여준다()
    {
        string auto = Row(ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, Folder, "db-1"), ProjectPathMappingStatus.AutoLinked).PathMappingStatusText;
        string manual = Row(ProjectTarget.Link(ProjectTargetReason.UserSelectedRegistered, Folder, "db-1")).PathMappingStatusText;

        Assert.Contains("이 PC의 '알파' 프로젝트에 연결됩니다", auto);
        Assert.DoesNotContain("사용자가 지정함", auto);
        Assert.Contains("사용자가 지정함", manual);
        Assert.Contains("'알파' 프로젝트에 연결됩니다", manual);
    }

    [Theory]
    [InlineData(ProjectTargetReason.UserSelectedUnregistered, "Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.OriginalRootExistsUnregistered, "기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.OriginalRootMissing, "원본 폴더가 이 PC에 없습니다. 폴더를 지정하지 않으면 기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.AmbiguousRoot, "여러 프로젝트로 등록되어 있어 자동으로 연결하지 않습니다")]
    [InlineData(ProjectTargetReason.LegacyOnlyProject, "기타 대화로 들어갑니다")]
    public void 연결되지_않는_목적지는_사용자가_지정함이라고_하지_않고_기타_대화라고_말한다(ProjectTargetReason reason, string expected)
    {
        string text = Row(ProjectTarget.Uncategorized(reason, Folder)).PathMappingStatusText;

        Assert.Contains(expected, text);
        Assert.DoesNotContain("사용자가 지정함", text);
    }

    [Fact]
    public void SuggestedTarget이_없는_이전_방식_Preview는_기존_문구를_쓴다()
    {
        Assert.StartsWith("사용자가 지정함", Row(target: null).PathMappingStatusText);
    }

    private static ImportConversationRowViewModel Conversation(MetadataDifferences metadata, ConversationLocalLocation? location)
        => new(new ImportConversationPreview("t1", true, "제목", RevisionRelation.Identical, ImportPlannedAction.NoOp, metadata, [], null, null)
        {
            LocalLocation = location,
        });

    [Fact]
    public void 대화_행은_프로젝트_연결_다름을_표시하지_않고_이_PC_위치를_보여준다()
    {
        var onlyProjectDiff = new MetadataDifferences(false, true, false, false, false, false);

        ImportConversationRowViewModel inProject = Conversation(onlyProjectDiff, new ConversationLocalLocation(true, "db-1", "알파", false));
        Assert.Null(inProject.MetadataDiffSummary);
        Assert.False(inProject.HasMetadataDiff);
        Assert.Equal("이 PC 위치: 알파 프로젝트", inProject.LocalLocationText);

        ImportConversationRowViewModel uncategorized = Conversation(onlyProjectDiff, new ConversationLocalLocation(true, null, null, true));
        Assert.Equal("이 PC 위치: 기타 대화 (보관됨)", uncategorized.LocalLocationText);

        ImportConversationRowViewModel absent = Conversation(MetadataDifferences.None, ConversationLocalLocation.NotPresent);
        Assert.Null(absent.LocalLocationText);
        Assert.False(absent.HasLocalLocation);

        var titleAndProject = new MetadataDifferences(false, true, false, false, true, false);
        Assert.Equal("제목 다름", Conversation(titleAndProject, null).MetadataDiffSummary);
    }
}
