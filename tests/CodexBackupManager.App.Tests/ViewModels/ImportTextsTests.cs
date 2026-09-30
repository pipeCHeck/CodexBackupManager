using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_2-2 — 가져오기 화면 문구(<see cref="ImportTexts"/>). 9_1b의 <c>ImportPreviewRowTextTests</c>를 옮기고
/// 새 화면 문구(배지, 요약, 제목으로 바꾼 차단 사유, 결과)를 더했다. 판정은 Core 결과를 그대로 쓴다.
/// </summary>
public sealed class ImportTextsTests
{
    private const string Folder = @"C:\Fixture\Target";

    private static readonly ProjectDirectory Directory = new(
    [
        new KnownProject("db-1", "db-1", new HashSet<string>(), "알파", [new KnownProjectRoot(Folder, CanonicalPath.Create(Folder), true)], 0),
    ]);

    // ── 작업 폴더 문구(9_1b ImportPreviewRowTextTests에서 옮김) ────────────────────

    [Fact]
    public void LinkExisting이면_연결될_프로젝트_이름을_보여주고_직접_지정은_따로_표시한다()
    {
        string auto = ImportTexts.TargetStatus(ProjectTarget.Link(ProjectTargetReason.OriginalRootRegistered, Folder, "db-1"), Directory, []);
        string manual = ImportTexts.TargetStatus(ProjectTarget.Link(ProjectTargetReason.UserSelectedRegistered, Folder, "db-1"), Directory, []);

        Assert.Equal("이 PC의 '알파' 프로젝트에 연결됩니다.", auto);
        Assert.Equal("'알파' 프로젝트에 연결됩니다(직접 지정).", manual);
    }

    [Theory]
    [InlineData(ProjectTargetReason.UserSelectedUnregistered, "Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.OriginalRootExistsUnregistered, "Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.OriginalRootMissing, "원본 폴더가 이 PC에 없습니다. 폴더를 지정하지 않으면 기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.AmbiguousRoot, "여러 프로젝트로 등록되어 있어 자동으로 연결하지 않습니다. 다른 폴더를 고르거나 기타 대화로 가져옵니다")]
    [InlineData(ProjectTargetReason.LegacyOnlyProject, "기타 대화로 들어갑니다")]
    [InlineData(ProjectTargetReason.NotApplicable, "기타 대화로 들어갑니다")]
    public void 연결되지_않는_목적지는_기타_대화로_들어간다고_말한다(ProjectTargetReason reason, string expected)
    {
        string text = ImportTexts.TargetStatus(ProjectTarget.Uncategorized(reason, Folder), Directory, []);

        Assert.Contains(expected, text);
        Assert.DoesNotContain("사용자가 지정함", text);
        Assert.DoesNotContain("연결됩니다", text.Replace("연결됩니다(직접", string.Empty).Replace("[새로고침]하면 연결됩니다", string.Empty));
    }

    [Fact]
    public void 원본_루트가_등록됐지만_폴더가_없으면_보충_문구를_붙인다()
    {
        var missing = new ProjectDirectory(
        [
            new KnownProject("db-9", "db-9", new HashSet<string>(), "사라진", [new KnownProjectRoot(@"C:\Gone", CanonicalPath.Create(@"C:\Gone"), false)], 0),
        ]);

        string text = ImportTexts.TargetStatus(ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null), missing, [@"c:\gone\"]);
        string plain = ImportTexts.TargetStatus(ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null), missing, [@"C:\Other"]);

        Assert.Contains("같은 경로의 프로젝트가 이 PC Codex에 있지만 폴더가 없습니다", text);
        Assert.DoesNotContain("같은 경로의 프로젝트", plain);
    }

    // ── 이 PC 위치 / 배지 / 제약 ───────────────────────────────────────────────

    [Fact]
    public void 이_PC_위치는_프로젝트_이름이나_기타_대화로_보여준다()
    {
        Assert.Equal("알파 프로젝트", ImportTexts.LocalLocation(new ConversationLocalLocation(true, "db-1", "알파", false)));
        Assert.Equal("기타 대화 (보관됨)", ImportTexts.LocalLocation(new ConversationLocalLocation(true, null, null, true)));
        Assert.Null(ImportTexts.LocalLocation(ConversationLocalLocation.NotPresent));
        Assert.Null(ImportTexts.LocalLocation(null));
    }

    private static ImportConversationPreview Conv(string id, RevisionRelation relation, string? title = null, bool compressed = false, bool selected = true, params string[] ancestors)
        => new(id, selected, title, relation, ImportConflictAnalyzer.Decide(relation), MetadataDifferences.None, [], null, null)
        {
            IsCompressedRollout = compressed,
            RequiredAncestorThreadIds = ancestors,
        };

    private static ImportPreview Preview(IReadOnlyList<ImportConversationPreview> conversations, IReadOnlyList<ImportConversationPreview>? dependencies = null)
    {
        var mapping = new ProjectPathMapping("p", "P", [@"C:\Orig"], ProjectPathMappingStatus.NotFound, null, null);
        var project = new ImportProjectPreview("p", "P", mapping, conversations)
        {
            SuggestedTarget = ProjectTarget.Uncategorized(ProjectTargetReason.OriginalRootMissing, null),
        };
        return new ImportPreview(true, [], null, [project], dependencies ?? [], [], null);
    }

    [Theory]
    [InlineData(RevisionRelation.New, "새 대화", ImportBadgeKind.New)]
    [InlineData(RevisionRelation.IncomingAhead, "이어받기", ImportBadgeKind.IncomingAhead)]
    [InlineData(RevisionRelation.Identical, "이미 있음", ImportBadgeKind.Neutral)]
    [InlineData(RevisionRelation.LocalAhead, "이 PC가 최신", ImportBadgeKind.Neutral)]
    [InlineData(RevisionRelation.Diverged, "충돌", ImportBadgeKind.Diverged)]
    [InlineData(RevisionRelation.Unverifiable, "확인 불가", ImportBadgeKind.Unverifiable)]
    public void 배지는_설계_7_4_표를_따른다(RevisionRelation relation, string text, ImportBadgeKind kind)
    {
        ImportSelectionSummary summary = ImportSelection.Compute(Preview([Conv("t", relation)]), new ImportUserChoices(new HashSet<string>(), new Dictionary<string, ProjectTargetDecision>()));

        Assert.Equal((text, kind), ImportTexts.Badge(summary.Conversations.Single()));
    }

    [Fact]
    public void 자동_포함_조상은_자동_포함_배지와_끌_수_없다는_툴팁이다()
    {
        ImportPreview preview = Preview([Conv("child", RevisionRelation.New, ancestors: ["parent"])], [Conv("parent", RevisionRelation.New, selected: false)]);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, new ImportUserChoices(new HashSet<string> { "child" }, new Dictionary<string, ProjectTargetDecision>()));
        ImportSelectionConversation parent = summary.Conversations.Single(c => c.ThreadId == "parent");

        Assert.Equal(("자동 포함", ImportBadgeKind.AutoIncluded), ImportTexts.Badge(parent));
        Assert.Contains("끌 수 없습니다", ImportTexts.UnselectableTooltip(parent));
        Assert.Equal("선택한 대화에 필요한 원본 대화라 함께 가져옵니다.", ImportTexts.StatusSentence(parent));
    }

    [Fact]
    public void 알려진_제약은_해당하는_대화에만_보여준다()
    {
        Assert.Null(ImportTexts.Limitation(Conv("a", RevisionRelation.New), attachmentCount: 0));
        Assert.Contains(".jsonl.zst", ImportTexts.Limitation(Conv("b", RevisionRelation.IncomingAhead, compressed: true), 0));
        Assert.Contains("분기된 대화", ImportTexts.Limitation(Conv("c", RevisionRelation.Diverged), 0));
        Assert.Contains("첨부 이미지", ImportTexts.Limitation(Conv("d", RevisionRelation.New), 2));
    }

    // ── 요약 / 차단 사유 제목 표시 ──────────────────────────────────────────────

    [Fact]
    public void 차단_사유의_thread_ID를_대화_제목으로_바꿔_보여준다()
    {
        const string child = "01d00000-0000-7000-8000-00000000c1d1";
        const string parent = "01d00000-0000-7000-8000-0000000000a1";
        ImportPreview preview = Preview(
            [Conv(child, RevisionRelation.New, title: "자식 대화", ancestors: [parent])],
            [Conv(parent, RevisionRelation.Diverged, title: null, selected: false)]);
        ImportSelectionSummary summary = ImportSelection.Compute(preview, new ImportUserChoices(new HashSet<string> { child }, new Dictionary<string, ProjectTargetDecision>()));
        var titles = summary.Conversations.ToDictionary(c => c.ThreadId, c => ImportTexts.TitleOf(c.Preview));

        string hint = ImportTexts.SummaryHint(summary, titles)!;

        Assert.False(summary.CanApply);
        Assert.Contains("'자식 대화'", hint);
        Assert.Contains($"'{ImportTexts.FallbackTitle(parent)}'", hint); // 제목 없음 → 짧은 대체 표기
        Assert.DoesNotContain(child, hint);
        Assert.DoesNotContain(parent, hint);
    }

    [Fact]
    public void 쓸_것이_없는_사유를_평범한_말로_보여준다()
    {
        var titles = new Dictionary<string, string>();
        ImportPreview present = Preview([Conv("s", RevisionRelation.Identical)]);
        ImportPreview none = Preview([Conv("d", RevisionRelation.Diverged)]);
        ImportPreview selectable = Preview([Conv("n", RevisionRelation.New)]);
        var empty = new ImportUserChoices(new HashSet<string>(), new Dictionary<string, ProjectTargetDecision>());

        Assert.Equal("선택한 대화는 모두 이미 이 PC에 있습니다. 가져올 것이 없습니다.", ImportTexts.SummaryHint(ImportSelection.Compute(present, empty), titles));
        Assert.Equal("이 백업에는 지금 가져올 수 있는 대화가 없습니다.", ImportTexts.SummaryHint(ImportSelection.Compute(none, empty), titles));
        Assert.Equal("가져올 대화를 선택해 주세요.", ImportTexts.SummaryHint(ImportSelection.Compute(selectable, empty), titles));

        ImportSelectionSummary one = ImportSelection.Compute(selectable, ImportUserChoices.CreateDefault(selectable));
        Assert.Equal("가져오기: 새 대화 1 · 이어받기 0 · 기타 대화로 1", ImportTexts.SummaryLine(one));
        Assert.Equal("선택한 대화 중 \"기타 대화\"로 들어가는 것이 1개 있습니다.", ImportTexts.SummaryHint(one, titles));
        Assert.Equal("대화 1개 가져오기", ImportTexts.ImportButton(one));
        Assert.DoesNotContain("새로 만들 프로젝트", ImportTexts.ConfirmMessage(one)); // 9_5 전
    }

    // ── 결과 문구(설계 §7.6, §9) ────────────────────────────────────────────────

    [Theory]
    [InlineData(RestoreOutcome.Succeeded, "가져오기 완료")]
    [InlineData(RestoreOutcome.NothingToDo, "적용할 변경이 없었습니다. 선택한 대화는 모두 이미 이 PC에 있습니다.")]
    [InlineData(RestoreOutcome.Cancelled, "적용을 취소하여 이전 상태로 되돌렸습니다")]
    [InlineData(RestoreOutcome.RolledBack, "적용 중 문제가 생겨 이전 상태로 되돌렸습니다. 기존 데이터는 그대로입니다.")]
    [InlineData(RestoreOutcome.RollbackFailedCritical, "[치명] 자동 복구에 실패했습니다. 복구 지점 snap-1로 수동 복구가 필요합니다.")]
    public void 결과_제목은_메시지_카탈로그를_따른다(RestoreOutcome outcome, string expected)
    {
        (string title, _) = ImportTexts.ResultHeadline(new RestoreResult(outcome, "internal", null, "snap-1"));

        Assert.Contains(expected, title);
        Assert.DoesNotContain("internal", title);
    }

    [Fact]
    public void NotReady는_사전_검사_상태별_문구를_쓴다()
    {
        Assert.Equal("미리보기 이후 백업 파일이 바뀌었습니다.",
            ImportTexts.ResultHeadline(new RestoreResult(RestoreOutcome.NotReady, "x", ImportPlanPreflightStatus.BackupChanged, null)).Title);
        Assert.Equal("미리보기 이후 이 PC의 Codex 데이터가 바뀌었습니다.",
            ImportTexts.ResultHeadline(new RestoreResult(RestoreOutcome.NotReady, "x", ImportPlanPreflightStatus.LocalStateChanged, null)).Title);
        Assert.Equal("Codex가 현재 실행 중입니다.",
            ImportTexts.ResultHeadline(new RestoreResult(RestoreOutcome.NotReady, "Codex가 현재 실행 중입니다.", null, null)).Title);
    }

    // ── 9_2-28 만든 앱 버전 / 9_2-29b 복구 지점 표기 ───────────────────────────────

    [Theory]
    [InlineData("0.1.1.0", "만든 앱 v0.1.1")]
    [InlineData("0.1.3", "만든 앱 v0.1.3")]
    [InlineData("0.1.0.0", "만든 앱 v0.1.0")]
    [InlineData("1.2.3.4", "만든 앱 v1.2.3.4")]
    [InlineData("0.2.0+abc123", "만든 앱 v0.2.0")]
    [InlineData("0.2.0-beta", "만든 앱 v0.2.0-beta")]
    [InlineData("1.2", "만든 앱 v1.2")]
    [InlineData(null, "만든 앱 버전 알 수 없음")]
    [InlineData("", "만든 앱 버전 알 수 없음")]
    public void 백업을_만든_앱_버전을_읽기_좋게_줄인다(string? raw, string expected)
        => Assert.Equal(expected, ImportTexts.BackupAppVersion(raw));

    [Fact]
    public void 복구_지점은_이_PC_시각으로_짧게_보여준다()
    {
        string id = "20260930-181730-e0b793d859764646a7df9b07f55af865";
        string expected = new DateTimeOffset(2026, 9, 30, 18, 17, 30, TimeSpan.Zero).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, ImportTexts.SnapshotLabel(id));
        Assert.Equal("unexpected-form", ImportTexts.SnapshotLabel("unexpected-form"));
    }

    // ── 9_2-30 사람이 읽는 경로 표기 ────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\?\C:\_UserProjects\Unreal\Balhwajeom_Project", @"C:\_UserProjects\Unreal\Balhwajeom_Project")]
    [InlineData(@"C:\Projects\Test\", @"C:\Projects\Test")]
    [InlineData(@"C:/Projects/Test", @"C:\Projects\Test")]
    [InlineData(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"C:\기획\펫 만들기", @"C:\기획\펫 만들기")]
    [InlineData(@"relative\path", @"relative\path")]
    public void 경로는_내부_접두사_없이_사람이_읽는_표기로_보인다(string raw, string expected)
        => Assert.Equal(expected, ImportTexts.DisplayPath(raw));
}
