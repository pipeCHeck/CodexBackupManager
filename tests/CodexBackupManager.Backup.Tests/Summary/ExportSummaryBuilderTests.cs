using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Summary;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Summary;

/// <summary>Phase 9_6-T1 — <see cref="ExportSummaryBuilder"/>(순수 함수): 폴더 목록, 기타 대화 cwd 중복 제거, 빈 값 생략, 텍스트 형식.</summary>
public sealed class ExportSummaryBuilderTests
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");

    private static BackupConversationMetadata Conv(
        string id, string? title, string? cwd = null, DateTimeOffset? created = null, DateTimeOffset? updated = null, bool selected = true)
        => new()
        {
            ThreadId = id,
            IsSelected = selected,
            ResolvedTitle = title,
            OriginalCwd = cwd,
            CreatedAtUtc = created,
            UpdatedAtUtc = updated,
            PayloadRolloutEntries = [],
            PayloadAttachmentEntries = [],
        };

    private static BackupProjectMetadata Project(string? id, string name, string[] roots, params string[] threadIds)
        => new() { ProjectId = id, DisplayName = name, OriginalRootPaths = roots, ConversationThreadIds = threadIds };

    private static BackupManifest Manifest(IReadOnlyList<BackupProjectMetadata> projects, IReadOnlyList<BackupConversationMetadata> conversations)
        => new()
        {
            BackupFormatVersion = 1,
            AppVersion = "0.1.3",
            CreatedAtUtc = new DateTimeOffset(2026, 10, 1, 5, 3, 1, TimeSpan.Zero),
            SourceOS = "Windows",
            ConversationCount = conversations.Count(c => c.IsSelected),
            DependencyConversationCount = conversations.Count(c => !c.IsSelected),
            ProjectCount = projects.Count,
            PayloadCount = 0,
            Projects = projects,
            Conversations = conversations,
            Attachments = [],
            Warnings = [],
        };

    private static DateTimeOffset Utc(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>합성 fixture: 프로젝트 2개(하나는 루트 2개), 기타 대화 3개(cwd 2종 + 없음), 자동 포함 원본 1개.</summary>
    internal static BackupManifest Sample() => Manifest(
        [
            Project("p-tri", "삼각형 3개", [@"\\?\C:\_User Projects\Unreal\과제(01)_26.0713\"], "t1", "t2"),
            Project("p-alpha", "Alpha", [@"C:\Work\Alpha", @"D:\Mirror\Alpha", @"c:\work\alpha\"], "t3"),
            Project(null, "기타 대화", [], "t4", "t5", "t6"),
        ],
        [
            Conv("t1", "정리된 주석으로 수정", @"C:\_User Projects\Unreal\과제(01)_26.0713", Utc(9, 21, 1), Utc(9, 29, 3)),
            Conv("t2", "숲 출력\n과정 정리", null, Utc(9, 20, 1), Utc(9, 28, 3)),
            Conv("t3", null, null, null, null),
            Conv("t4", "과제 수행", @"C:\Users\User\Documents\Codex", Utc(9, 30, 1), Utc(9, 30, 16)),
            Conv("t5", "두 번째", @"\\?\c:\users\user\documents\codex\", null, Utc(9, 1)),
            Conv("t6", "폴더 없음", null, null, null),
            Conv("t7", "자동 포함 원본", @"C:\Hidden", Utc(1, 1), Utc(1, 2), selected: false),
        ]);

    [Fact]
    public void 경고는_사용자_말로_바꿔_요약과_텍스트_경고_절에_넣고_없으면_절이_없다()
    {
        // Phase 9_U-09
        BackupManifest withWarnings = Sample() with
        {
            Warnings = ["참조된 첨부 이미지 파일 3개를 찾을 수 없어 Export에서 제외했습니다.", "알 수 없는" + (char)10 + "경고"],
        };

        ExportSummary summary = ExportSummaryBuilder.Build(withWarnings, "b.codexbackup", 1);

        Assert.Equal(
            [
                "대화에 붙인 이미지 파일 3개를 이 PC에서 찾지 못해 백업에 넣지 못했습니다. 대화 내용은 모두 들어 있고, 다른 PC에서는 그 이미지만 보이지 않습니다.",
                "알 수 없는 경고",
            ],
            summary.Warnings);
        string text = ExportSummaryBuilder.ToText(summary, Seoul);
        const string crlf = "\r\n";
        Assert.Contains(crlf + crlf + "[경고 2건]" + crlf + "  - 대화에 붙인 이미지 파일 3개를", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("[경고 2건]", StringComparison.Ordinal) < text.IndexOf(ExportSummaryBuilder.FoldersNotice, StringComparison.Ordinal));

        ExportSummary none = ExportSummaryBuilder.Build(Sample(), "b.codexbackup", 1);
        Assert.Empty(none.Warnings);
        Assert.DoesNotContain("[경고", ExportSummaryBuilder.ToText(none, Seoul), StringComparison.Ordinal);
    }

    [Fact]
    public void 프로젝트별_루트와_기타_대화_cwd를_중복_없이_표시_경로로_모은다()
    {
        ExportSummary summary = ExportSummaryBuilder.Build(Sample(), "codex-backup-20261001-140301.codexbackup", 12_900_000);

        Assert.Equal(["Alpha", "삼각형 3개"], summary.Projects.Select(p => p.Name)); // 이름 순
        Assert.Equal([@"C:\Work\Alpha", @"D:\Mirror\Alpha"], summary.Projects[0].Folders); // 같은 위치는 한 번
        Assert.Equal([@"C:\_User Projects\Unreal\과제(01)_26.0713"], summary.Projects[1].Folders); // \\?\·끝 구분자 제거
        Assert.Equal(["숲 출력 과정 정리", "정리된 주석으로 수정"], summary.Projects[1].Conversations.Select(c => c.Title)); // 만든 순, 한 줄

        ExportSummaryGroup other = summary.Uncategorized!;
        Assert.Equal([@"C:\Users\User\Documents\Codex"], other.Folders); // cwd 중복 제거(대소문자·접두사 차이), 없음은 생략
        Assert.Equal(3, other.Conversations.Count);
        Assert.DoesNotContain(summary.Projects.Concat([other]).SelectMany(g => g.Conversations), c => c.Title == "자동 포함 원본");
        Assert.Equal(6, summary.ConversationCount); // 선택 대화만(자동 포함 원본 제외)
        Assert.Equal(2, summary.ProjectCount);
        Assert.Equal(3, summary.UncategorizedConversationCount);
    }

    [Fact]
    public void 텍스트는_설계_예시_형식이고_없는_값은_생략한다()
    {
        ExportSummary summary = ExportSummaryBuilder.Build(Sample(), "codex-backup-20261001-140301.codexbackup", 12_900_000);

        string text = ExportSummaryBuilder.ToText(summary, Seoul);

        string expected = string.Join("\r\n",
            "Codex Backup Manager — 내보내기 목록",
            "만든 시각: 2026-10-01 14:03 (+09:00)",
            "백업 파일: codex-backup-20261001-140301.codexbackup (12.3 MB)",
            "대화 6개 · 프로젝트 2개 · 기타 대화 3개",
            "",
            "※ 이 백업에는 작업 폴더의 파일이 들어 있지 않습니다. 아래 작업 폴더는 따로 옮겨 주세요.",
            "",
            "[프로젝트] Alpha",
            @"  작업 폴더: C:\Work\Alpha",
            @"  작업 폴더: D:\Mirror\Alpha",
            "  대화 1개",
            "    - (제목 없음)",
            "",
            "[프로젝트] 삼각형 3개",
            @"  작업 폴더: C:\_User Projects\Unreal\과제(01)_26.0713",
            "  대화 2개",
            "    - 숲 출력 과정 정리 (만든 날 2026-09-20, 마지막 수정 2026-09-28)",
            "    - 정리된 주석으로 수정 (만든 날 2026-09-21, 마지막 수정 2026-09-29)",
            "",
            "[기타 대화]",
            "  대화 3개",
            @"    - 과제 수행 (작업 폴더: C:\Users\User\Documents\Codex, 마지막 수정 2026-10-01)",
            @"    - 두 번째 (작업 폴더: c:\users\user\documents\codex, 마지막 수정 2026-09-01)",
            "    - 폴더 없음",
            "");
        Assert.Equal(expected, text);
        Assert.DoesNotContain("이 PC에 없음", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal); // 줄바꿈은 모두 CRLF
    }

    [Fact]
    public void 기타_대화가_없으면_그_블록과_개수를_생략한다()
    {
        BackupManifest manifest = Manifest([Project("p", "P", [], "a")], [Conv("a", "제목", created: Utc(9, 1))]);
        ExportSummary summary = ExportSummaryBuilder.Build(manifest, "b.codexbackup", 10);
        string text = ExportSummaryBuilder.ToText(summary, TimeZoneInfo.Utc);

        Assert.Null(summary.Uncategorized);
        Assert.Contains("대화 1개 · 프로젝트 1개\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[기타 대화]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("작업 폴더:", text, StringComparison.Ordinal); // 루트가 없는 프로젝트는 그 줄 생략
        Assert.Contains("    - 제목 (만든 날 2026-09-01)\r\n", text, StringComparison.Ordinal);
        Assert.Contains("만든 시각: 2026-10-01 05:03 (+00:00)", text, StringComparison.Ordinal);
        Assert.Contains("(10 B)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\r\nb", "a b")]
    [InlineData("  a\t\tb  ", "a b")]
    [InlineData("a\u2028b\u0001c", "a b c")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 한_줄로_정리한다(string? input, string expected) => Assert.Equal(expected, ExportSummaryBuilder.OneLine(input));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(12_900_000L, "12.3 MB")]
    [InlineData(3_221_225_472L, "3.0 GB")]
    public void 크기_표기(long bytes, string expected) => Assert.Equal(expected, ExportSummaryBuilder.FormatSize(bytes));

    [Fact]
    public void 음수_시간대도_표기한다()
    {
        TimeZoneInfo minus = TimeZoneInfo.CreateCustomTimeZone("m", TimeSpan.FromHours(-3.5), "m", "m");
        Assert.Equal("2026-10-01 01:33 (-03:30)", ExportSummaryBuilder.FormatDateTimeWithOffset(new DateTimeOffset(2026, 10, 1, 5, 3, 0, TimeSpan.Zero), minus));
    }

    [Fact]
    public void 제목과_경로의_특수_문자는_그대로_두고_줄바꿈만_한_줄로()
    {
        BackupManifest manifest = Manifest(
            [Project(null, "기타 대화", [], "a")],
            [Conv("a", "\"따옴표\" <꺾쇠> & 😀\r\n둘째 줄", @"C:\a b\(괄호)\[대괄호]")]);
        string text = ExportSummaryBuilder.ToText(ExportSummaryBuilder.Build(manifest, "x.codexbackup", 1), TimeZoneInfo.Utc);

        Assert.Contains("    - \"따옴표\" <꺾쇠> & 😀 둘째 줄 (작업 폴더: C:\\a b\\(괄호)\\[대괄호])\r\n", text, StringComparison.Ordinal);
    }
}
