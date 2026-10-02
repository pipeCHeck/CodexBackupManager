using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Summary;

/// <summary>(Phase 9_6-03) 내보내기 목록의 대화 한 줄.</summary>
/// <param name="Title">한 줄로 정리한 제목(Manifest에 없으면 "(제목 없음)").</param>
/// <param name="CreatedAtUtc">만든 시각(Manifest에 있으면).</param>
/// <param name="UpdatedAtUtc">마지막 수정 시각(Manifest에 있으면).</param>
/// <param name="Folder">작업 폴더(cwd) 표시 경로(Manifest에 있으면).</param>
public sealed record ExportSummaryConversation(string Title, DateTimeOffset? CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, string? Folder);

/// <summary>(Phase 9_6-03) 프로젝트 하나(또는 기타 대화).</summary>
/// <param name="Name">한 줄로 정리한 프로젝트 이름.</param>
/// <param name="Folders">
/// 옮겨야 할 작업 폴더(표시 경로, <c>\\?\</c> 없음, 중복 없음). 프로젝트는 루트 폴더, 기타 대화는 대화들의 cwd다.
/// </param>
/// <param name="Conversations">대화(만든 시각 순).</param>
public sealed record ExportSummaryGroup(string Name, IReadOnlyList<string> Folders, IReadOnlyList<ExportSummaryConversation> Conversations);

/// <summary>(Phase 9_6-03) 내보내기 완료 안내와 목록 텍스트의 모델.</summary>
/// <param name="BackupFileName">백업 파일 이름(경로 없음).</param>
/// <param name="BackupSizeBytes">백업 파일 크기.</param>
/// <param name="CreatedAtUtc">백업을 만든 시각.</param>
/// <param name="ConversationCount">선택한 대화 수.</param>
/// <param name="Projects">프로젝트(이름 순, 기타 대화 제외).</param>
/// <param name="Uncategorized">기타 대화(없으면 <c>null</c>).</param>
public sealed record ExportSummary(
    string BackupFileName,
    long BackupSizeBytes,
    DateTimeOffset CreatedAtUtc,
    int ConversationCount,
    IReadOnlyList<ExportSummaryGroup> Projects,
    ExportSummaryGroup? Uncategorized)
{
    /// <summary>프로젝트 수(기타 대화 제외).</summary>
    public int ProjectCount => Projects.Count;

    /// <summary>기타 대화 수.</summary>
    public int UncategorizedConversationCount => Uncategorized?.Conversations.Count ?? 0;

    /// <summary>
    /// (Phase 9_U-09) 이번 내보내기의 경고를 사용자 말로 바꾼 것(<see cref="ExportSummaryBuilder.DescribeWarning"/>). 없으면 빈 목록.
    /// 화면과 텍스트 파일에만 쓰고 로그에는 개수만 남긴다.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// (Phase 9_6-03) 이번 내보내기의 Manifest로 "옮겨야 할 작업 폴더" 목록과 텍스트 파일 내용을 만드는 <b>순수 함수</b>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>파일 시스템을 보지 않는다(폴더가 이 PC에 있는지는 App이 확인한다). 텍스트에는 "이 PC에 없음"을 적지 않는다.</item>
///   <item>값은 Manifest에 실제로 있는 것만 쓴다. 없는 날짜·폴더는 그 부분을 생략한다(CLAUDE.md §12).</item>
///   <item>대화 목록은 선택한 대화(<see cref="BackupConversationMetadata.IsSelected"/>)만이다. 자동 포함된 원본 대화는 목록에 넣지 않는다.</item>
///   <item>이 텍스트는 백업 파일 안에 넣지 않는다(Backup Format V1 FROZEN). 가져오기도 이 파일을 읽지 않는다.</item>
/// </list>
/// </remarks>
public static class ExportSummaryBuilder
{
    /// <summary>제목이 없을 때.</summary>
    public const string UntitledText = "(제목 없음)";

    /// <summary>텍스트 파일의 작업 폴더 안내.</summary>
    public const string FoldersNotice = "※ 이 백업에는 작업 폴더의 파일이 들어 있지 않습니다. 아래 작업 폴더는 따로 옮겨 주세요.";

    /// <summary>Manifest로 모델을 만든다.</summary>
    public static ExportSummary Build(BackupManifest manifest, string backupFileName, long backupSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFileName);

        Dictionary<string, BackupConversationMetadata> byId = manifest.Conversations
            .Where(c => c.IsSelected)
            .GroupBy(c => c.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var projects = new List<ExportSummaryGroup>();
        ExportSummaryGroup? uncategorized = null;
        foreach (BackupProjectMetadata project in manifest.Projects)
        {
            List<ExportSummaryConversation> conversations = project.ConversationThreadIds
                .Select(id => byId.TryGetValue(id, out BackupConversationMetadata? c) ? c : null)
                .OfType<BackupConversationMetadata>()
                .Select(ToConversation)
                .OrderBy(c => c.CreatedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(c => c.Title, StringComparer.CurrentCulture)
                .ToList();

            if (project.IsUncategorized)
            {
                if (conversations.Count > 0)
                {
                    uncategorized = new ExportSummaryGroup(
                        OneLine(project.DisplayName), DistinctFolders(conversations.Select(c => c.Folder)), conversations);
                }

                continue;
            }

            projects.Add(new ExportSummaryGroup(OneLine(project.DisplayName), DistinctFolders(project.OriginalRootPaths), conversations));
        }

        projects = projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return new ExportSummary(backupFileName, backupSizeBytes, manifest.CreatedAtUtc, manifest.ConversationCount, projects, uncategorized)
        {
            Warnings = (manifest.Warnings ?? []).Select(DescribeWarning).ToList(),
        };
    }

    /// <summary>
    /// (Phase 9_U-09) Export 경고 한 줄을 사용자 말로 바꾼다. 지금 Export가 내는 경고는 "첨부 이미지 누락"(<c>ExportPlanBuilder</c>, 개수만,
    /// 경로 없음) 한 종류다. 모르는 경고는 원문을 한 줄로 정리해 그대로 보여 준다(경고 원문에는 경로·제목을 넣지 않는 것이 Export 규칙이다).
    /// </summary>
    public static string DescribeWarning(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        System.Text.RegularExpressions.Match missingImages = System.Text.RegularExpressions.Regex.Match(
            warning, @"^참조된 첨부 이미지 파일 (\d+)개를 찾을 수 없어 Export에서 제외했습니다\.$");
        return missingImages.Success
            ? $"대화에 붙인 이미지 파일 {missingImages.Groups[1].Value}개를 이 PC에서 찾지 못해 백업에 넣지 못했습니다. " +
              "대화 내용은 모두 들어 있고, 다른 PC에서는 그 이미지만 보이지 않습니다."
            : OneLine(warning);
    }

    /// <summary>
    /// 텍스트 파일 내용(줄바꿈 CRLF, BOM은 저장하는 쪽이 붙인다). 시각은 <paramref name="timeZone"/> 기준으로 적는다.
    /// </summary>
    public static string ToText(ExportSummary summary, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(timeZone);

        var lines = new List<string>
        {
            "Codex Backup Manager — 내보내기 목록",
            "만든 시각: " + FormatDateTimeWithOffset(summary.CreatedAtUtc, timeZone),
            $"백업 파일: {OneLine(summary.BackupFileName)} ({FormatSize(summary.BackupSizeBytes)})",
            CountsLine(summary),
        };

        // (Phase 9_U-09) 경고가 있으면 맨 위 요약 바로 아래에 한 줄씩 적는다.
        if (summary.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"[경고 {summary.Warnings.Count}건]"));
            lines.AddRange(summary.Warnings.Select(w => "  - " + w));
        }

        lines.Add(string.Empty);
        lines.Add(FoldersNotice);

        foreach (ExportSummaryGroup project in summary.Projects)
        {
            lines.Add(string.Empty);
            lines.Add("[프로젝트] " + project.Name);
            lines.AddRange(project.Folders.Select(f => "  작업 폴더: " + f));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  대화 {project.Conversations.Count}개"));
            lines.AddRange(project.Conversations.Select(c => "    - " + c.Title + ProjectConversationSuffix(c, timeZone)));
        }

        if (summary.Uncategorized is { } other)
        {
            lines.Add(string.Empty);
            lines.Add("[기타 대화]");
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  대화 {other.Conversations.Count}개"));
            lines.AddRange(other.Conversations.Select(c => "    - " + c.Title + UncategorizedConversationSuffix(c, timeZone)));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>"대화 5개 · 프로젝트 3개 · 기타 대화 1개"(기타 대화가 없으면 그 부분 생략).</summary>
    public static string CountsLine(ExportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string line = string.Create(CultureInfo.InvariantCulture, $"대화 {summary.ConversationCount}개 · 프로젝트 {summary.ProjectCount}개");
        return summary.UncategorizedConversationCount > 0
            ? line + string.Create(CultureInfo.InvariantCulture, $" · 기타 대화 {summary.UncategorizedConversationCount}개")
            : line;
    }

    /// <summary>사람이 읽는 크기("512 B", "3.4 KB", "12.3 MB", "1.2 GB").</summary>
    public static string FormatSize(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / Kb:0.0} KB"),
            < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / Mb:0.0} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb:0.0} GB"),
        };
    }

    /// <summary>
    /// 한 줄로 정리한다: 줄바꿈·탭 등 제어 문자는 공백 하나로, 연속 공백은 하나로, 앞뒤 공백 제거. 목록 줄이 깨지지 않게 하기 위함이다.
    /// </summary>
    public static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool lastWasSpace = false;
        foreach (char c in value)
        {
            bool space = char.IsControl(c) || char.IsWhiteSpace(c) || c == (char)0x2028 || c == (char)0x2029;
            if (space)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static ExportSummaryConversation ToConversation(BackupConversationMetadata c)
    {
        string title = OneLine(c.ResolvedTitle);
        return new ExportSummaryConversation(
            title.Length == 0 ? UntitledText : title,
            c.CreatedAtUtc,
            c.UpdatedAtUtc,
            string.IsNullOrWhiteSpace(c.OriginalCwd) ? null : DisplayPath(c.OriginalCwd));
    }

    private static IReadOnlyList<string> DistinctFolders(IEnumerable<string?> paths)
    {
        var result = new List<string>();
        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string display = DisplayPath(path);
            if (!result.Exists(existing => CanonicalPath.AreSameLocation(existing, display) || string.Equals(existing, display, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(display);
            }
        }

        return result;
    }

    /// <summary>표시 경로: <c>\\?\</c> 접두사 제거, 구분자 통일, 끝 구분자 제거(정규화할 수 없으면 한 줄로 정리한 원문).</summary>
    private static string DisplayPath(string path)
        => CanonicalPath.TryCreate(path, out CanonicalPath? canonical, out _) ? canonical!.Display : OneLine(path);

    private static string ProjectConversationSuffix(ExportSummaryConversation c, TimeZoneInfo timeZone)
    {
        var parts = new List<string>();
        if (c.CreatedAtUtc is { } created)
        {
            parts.Add("만든 날 " + FormatDate(created, timeZone));
        }

        if (c.UpdatedAtUtc is { } updated)
        {
            parts.Add("마지막 수정 " + FormatDate(updated, timeZone));
        }

        return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
    }

    private static string UncategorizedConversationSuffix(ExportSummaryConversation c, TimeZoneInfo timeZone)
    {
        var parts = new List<string>();
        if (c.Folder is { } folder)
        {
            parts.Add("작업 폴더: " + folder);
        }

        if (c.UpdatedAtUtc is { } updated)
        {
            parts.Add("마지막 수정 " + FormatDate(updated, timeZone));
        }

        return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
    }

    private static string FormatDate(DateTimeOffset value, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(value, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"2026-10-01 14:03 (+09:00)".</summary>
    public static string FormatDateTimeWithOffset(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(value, timeZone);
        TimeSpan offset = local.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
               string.Create(CultureInfo.InvariantCulture, $" ({sign}{offset.Duration():hh\\:mm})");
    }
}
