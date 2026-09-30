using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>대화 배지 종류(색은 View가 정한다, 설계 §7.4).</summary>
public enum ImportBadgeKind
{
    /// <summary>새 대화(초록).</summary>
    New,

    /// <summary>이어받기(파랑).</summary>
    IncomingAhead,

    /// <summary>이미 있음 / 이 PC가 최신(회색).</summary>
    Neutral,

    /// <summary>충돌(주황).</summary>
    Diverged,

    /// <summary>확인 불가(빨강).</summary>
    Unverifiable,

    /// <summary>자동 포함(흐림).</summary>
    AutoIncluded,
}

/// <summary>
/// 가져오기 화면의 사용자 문구(Phase 9_2-2, 설계 §7·§9). 판정은 하지 않고 Core 결과를 평범한 말로 바꾸기만 한다.
/// 내부 용어(enum 이름, "계획 생성" 등)는 화면에 내보내지 않는다.
/// </summary>
public static class ImportTexts
{
    /// <summary>제목이 없을 때 쓰는 짧은 대체 표기("대화 1a2b3c4d").</summary>
    public static string FallbackTitle(string threadId)
        => "대화 " + (threadId.Length > 8 ? threadId[..8] : threadId);

    /// <summary>대화 표시 제목.</summary>
    public static string TitleOf(ImportConversationPreview conversation)
        => string.IsNullOrWhiteSpace(conversation.ResolvedTitle) ? FallbackTitle(conversation.ThreadId) : conversation.ResolvedTitle!;

    /// <summary>배지 문구와 종류.</summary>
    public static (string Text, ImportBadgeKind Kind) Badge(ImportSelectionConversation conversation)
    {
        if (conversation.IsAutoIncludedAncestor)
        {
            return ("자동 포함", ImportBadgeKind.AutoIncluded);
        }

        return conversation.Preview.Relation switch
        {
            RevisionRelation.New => ("새 대화", ImportBadgeKind.New),
            RevisionRelation.IncomingAhead => ("이어받기", ImportBadgeKind.IncomingAhead),
            RevisionRelation.Identical => ("이미 있음", ImportBadgeKind.Neutral),
            RevisionRelation.LocalAhead => ("이 PC가 최신", ImportBadgeKind.Neutral),
            RevisionRelation.Diverged => ("충돌", ImportBadgeKind.Diverged),
            _ => ("확인 불가", ImportBadgeKind.Unverifiable),
        };
    }

    /// <summary>오른쪽 상세의 상태 설명 문장(설계 §7.4 "오른쪽 설명").</summary>
    public static string StatusSentence(ImportSelectionConversation conversation)
    {
        if (conversation.IsAutoIncludedAncestor)
        {
            return "선택한 대화에 필요한 원본 대화라 함께 가져옵니다.";
        }

        return conversation.Preview.Relation switch
        {
            RevisionRelation.New => "이 PC에 없는 대화입니다. 가져옵니다.",
            RevisionRelation.IncomingAhead when conversation.Preview.IsCompressedRollout =>
                "이 PC에 있지만 백업이 더 깁니다. 압축된 기록이라 이어받을 수 없습니다.",
            RevisionRelation.IncomingAhead => "이 PC에 있지만 백업이 더 깁니다. 뒤에 이어붙입니다.",
            RevisionRelation.Identical => "이미 이 PC에 있습니다. 내용이 같습니다.",
            RevisionRelation.LocalAhead => "이 PC 쪽이 더 깁니다. 건너뜁니다.",
            RevisionRelation.Diverged => "양쪽이 다르게 이어졌습니다. 이번 버전에서는 가져올 수 없습니다(다른 대화는 가져올 수 있습니다).",
            _ => "이 PC 데이터가 불완전해 비교할 수 없습니다.",
        };
    }

    /// <summary>체크할 수 없는 대화의 툴팁.</summary>
    public static string? UnselectableTooltip(ImportSelectionConversation conversation)
    {
        if (conversation.IsAutoIncludedAncestor)
        {
            return "선택한 대화에 필요한 원본 대화라 끌 수 없습니다.";
        }

        return conversation.UnselectableReason switch
        {
            null => null,
            ImportUnselectableReason.AlreadyPresent => "이미 이 PC에 있어 가져올 것이 없습니다.",
            ImportUnselectableReason.LocalAhead => "이 PC 쪽이 더 최신이라 건너뜁니다.",
            ImportUnselectableReason.Diverged => "양쪽이 다르게 이어져 이번 버전에서는 가져올 수 없습니다.",
            ImportUnselectableReason.Unverifiable => "이 PC 데이터가 불완전해 비교할 수 없습니다.",
            ImportUnselectableReason.CompressedUpdate => "압축된 기록(.jsonl.zst)이라 이어받기를 지원하지 않습니다.",
            ImportUnselectableReason.DependencyOnly => "다른 대화의 원본으로만 들어 있는 대화입니다. 필요하면 자동으로 함께 가져옵니다.",
            _ => null,
        };
    }

    /// <summary>
    /// 이 대화에 실제로 해당하는 알려진 제약(Phase 9_2-18 — 예전에는 항상 전체 목록을 보여줬다). 없으면 <c>null</c>.
    /// </summary>
    public static string? Limitation(ImportConversationPreview conversation, int attachmentCount)
    {
        var parts = new List<string>();
        if (conversation.IsCompressedRollout && conversation.Relation != RevisionRelation.New)
        {
            parts.Add("압축된 기록(.jsonl.zst)이라 이어받기는 지원하지 않습니다.");
        }

        if (conversation.Relation == RevisionRelation.Diverged)
        {
            parts.Add("분기된 대화는 자동으로 합치지 않습니다.");
        }

        if (attachmentCount > 0)
        {
            parts.Add("첨부 이미지 파일은 함께 옮겨지지 않습니다(대화 내용은 그대로입니다).");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>이 PC 위치 문구. 이 PC에 없으면 <c>null</c>.</summary>
    public static string? LocalLocation(ConversationLocalLocation? location)
    {
        if (location is not { ExistsLocally: true })
        {
            return null;
        }

        string text = location.IsUncategorized
            ? "기타 대화"
            : $"{location.ProjectDisplayName ?? location.KnownProjectKey} 프로젝트";
        return location.Archived ? text + " (보관됨)" : text;
    }

    /// <summary>
    /// 작업 폴더 상태 문구(설계 §7.3, 9_2는 CreateNew 대신 A안 문구). "사용자가 지정함"은 실제로 연결될 때만 쓴다.
    /// </summary>
    /// <param name="target">목적지.</param>
    /// <param name="directory">이 PC 프로젝트 목록(연결 대상 이름, 폴더 없는 등록 프로젝트 판정에 쓴다).</param>
    /// <param name="originalRootPaths">백업의 원본 루트(OriginalRootMissing 보충 문구 판정용).</param>
    public static string TargetStatus(ProjectTarget target, ProjectDirectory directory, IReadOnlyList<string> originalRootPaths)
    {
        string projectName = target.LinkDbProjectId is { } id && directory.FindById(id) is { } known
            ? known.DisplayName
            : "등록된";

        return target.Reason switch
        {
            ProjectTargetReason.NotApplicable => "작업 폴더 지정 안 함 → 기타 대화로 들어갑니다.",
            ProjectTargetReason.OriginalRootRegistered when target.Kind == ProjectTargetKind.LinkExisting =>
                $"이 PC의 '{projectName}' 프로젝트에 연결됩니다.",
            ProjectTargetReason.UserSelectedRegistered when target.Kind == ProjectTargetKind.LinkExisting =>
                $"'{projectName}' 프로젝트에 연결됩니다(직접 지정).",
            ProjectTargetReason.UserSelectedUnregistered or ProjectTargetReason.OriginalRootExistsUnregistered =>
                "Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다. Codex에서 이 폴더를 한 번 연 뒤 [새로고침]하면 연결됩니다.",
            ProjectTargetReason.OriginalRootMissing =>
                "원본 폴더가 이 PC에 없습니다. 폴더를 지정하지 않으면 기타 대화로 들어갑니다." +
                (HasRegisteredButMissingRoot(directory, originalRootPaths)
                    ? " 같은 경로의 프로젝트가 이 PC Codex에 있지만 폴더가 없습니다."
                    : string.Empty),
            ProjectTargetReason.AmbiguousRoot =>
                "이 폴더가 Codex에 여러 프로젝트로 등록되어 있어 자동으로 연결하지 않습니다. 다른 폴더를 고르거나 기타 대화로 가져옵니다.",
            ProjectTargetReason.LegacyOnlyProject =>
                "이 폴더는 Codex 데스크톱의 이전 형식 프로젝트로만 등록되어 있어 지금은 연결할 수 없습니다. 기타 대화로 들어갑니다.",
            ProjectTargetReason.CreationUnsupported =>
                "이 Codex 버전에서는 프로젝트 자동 생성을 지원하지 않습니다. 기타 대화로 들어갑니다.",
            _ => "이 PC의 프로젝트에 연결할 수 없습니다. 지금은 기타 대화로 들어갑니다.",
        };
    }

    /// <summary>목적지가 등록 프로젝트 연결인지(초록 표시용).</summary>
    public static bool IsLinked(ProjectTarget target) => target.Kind == ProjectTargetKind.LinkExisting;

    /// <summary>
    /// 원본 루트 중 이 PC Codex에 등록돼 있지만 폴더가 없는 것이 있는지. 등록 여부와 실존 여부는
    /// <see cref="KnownProjectRoot.ExistsOnDisk"/>(카탈로그 생성 시 <c>Directory.Exists</c>)로만 판단한다.
    /// </summary>
    internal static bool HasRegisteredButMissingRoot(ProjectDirectory directory, IReadOnlyList<string> originalRootPaths)
    {
        foreach (string root in originalRootPaths)
        {
            if (CanonicalPath.TryCreate(root, out CanonicalPath? canonical, out _) &&
                directory.FindByRoot(canonical!) is { Kind: ProjectLookupKind.Found } lookup &&
                lookup.Project!.Roots.Any(r => r.Canonical.Equals(canonical) && !r.ExistsOnDisk))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>하단 요약 첫 줄.</summary>
    public static string SummaryLine(ImportSelectionSummary summary)
        => $"가져오기: 새 대화 {summary.ImportCount} · 이어받기 {summary.UpdateCount} · 기타 대화로 {summary.UncategorizedImportCount}";

    /// <summary>
    /// 하단 요약 둘째 줄(안내 또는 적용 불가 사유 한 줄). 차단 사유의 thread ID는 대화 제목으로 바꾼다.
    /// </summary>
    public static string? SummaryHint(ImportSelectionSummary summary, IReadOnlyDictionary<string, string> titles)
    {
        if (summary.BlockingReasons.Count > 0)
        {
            string first = WithTitles(summary.BlockingReasons[0], titles);
            return summary.BlockingReasons.Count == 1 ? first : $"{first} (외 {summary.BlockingReasons.Count - 1}건)";
        }

        switch (summary.NothingToWriteReason)
        {
            case ImportNothingToWriteReason.AllAlreadyPresent:
                return "선택한 대화는 모두 이미 이 PC에 있습니다. 가져올 것이 없습니다.";
            case ImportNothingToWriteReason.NothingSelected:
                return "가져올 대화를 선택해 주세요.";
            case ImportNothingToWriteReason.NoneSelectable:
                return "이 백업에는 지금 가져올 수 있는 대화가 없습니다.";
        }

        return summary.UncategorizedImportCount > 0
            ? $"선택한 대화 중 \"기타 대화\"로 들어가는 것이 {summary.UncategorizedImportCount}개 있습니다."
            : null;
    }

    /// <summary>문장 안의 thread ID를 '제목'으로 바꾼다(없으면 짧은 대체 표기).</summary>
    public static string WithTitles(string text, IReadOnlyDictionary<string, string> titles)
    {
        var builder = new StringBuilder(text);
        foreach ((string threadId, string title) in titles.OrderByDescending(p => p.Key.Length))
        {
            builder.Replace(threadId, $"'{title}'");
        }

        return builder.ToString();
    }

    /// <summary>[가져오기] 버튼 문구.</summary>
    public static string ImportButton(ImportSelectionSummary summary)
        => $"대화 {summary.ImportCount + summary.UpdateCount}개 가져오기";

    /// <summary>확인 대화상자 문구(설계 §7.5 — 9_3/9_5 줄은 아직 없다).</summary>
    public static string ConfirmMessage(ImportSelectionSummary summary)
        => "다음 내용을 Codex에 적용합니다." + Environment.NewLine + Environment.NewLine +
           $"  새로 가져올 대화   {summary.ImportCount}개" + Environment.NewLine +
           $"  이어받을 대화      {summary.UpdateCount}개" + Environment.NewLine +
           $"  기타 대화로 들어갈 대화 {summary.UncategorizedImportCount}개" + Environment.NewLine + Environment.NewLine +
           "적용 전에 현재 상태의 복구 지점(Snapshot)을 만듭니다." + Environment.NewLine +
           "실패하면 자동으로 되돌립니다. Codex가 완전히 종료되어 있어야 합니다.";

    /// <summary>Codex 실행 중 안내(가져오기 시작 시, 설계 §9).</summary>
    public const string CodexRunningAtStart = "가져오기는 Codex를 종료한 상태에서만 할 수 있습니다. Codex를 완전히 종료한 뒤 [다시 확인]을 눌러 주세요.";

    /// <summary>Editing 중 Codex가 켜졌을 때(설계 §9).</summary>
    public const string CodexStartedWhileEditing = "Codex가 실행되어 분석 결과가 바뀌었을 수 있습니다. Codex를 종료한 뒤 [다시 분석]을 눌러 주세요.";

    /// <summary>Codex는 종료됐지만 분석이 낡았을 때.</summary>
    public const string AnalysisStale = "Codex가 실행된 동안 분석 결과가 바뀌었을 수 있습니다. [다시 분석]을 눌러 주세요.";

    /// <summary>내보내기 전 Codex 실행 중 안내(9_2-20, 차단하지 않는다).</summary>
    public const string ExportWhileCodexRunning = "Codex에서 사용 중인 대화는 내보내기 도중 바뀌면 실패할 수 있습니다. 실패하면 Codex를 종료하고 다시 내보내기";

    /// <summary>Plan을 만들 수 없을 때(백업이 미리보기 이후 바뀜, 설계 §9).</summary>
    public const string PlanUnavailable = "미리보기 이후 백업 파일이 바뀌었습니다. 다시 열어 주세요.";

    /// <summary>분석 실패 제목과 해결 방법.</summary>
    public static (string Message, string Hint) AnalysisFailure(IReadOnlyList<string> validationErrors)
    {
        string joined = string.Join(" ", validationErrors);
        if (joined.Contains("지원하지 않는", StringComparison.Ordinal) || joined.Contains("Version", StringComparison.OrdinalIgnoreCase))
        {
            return ("이 백업은 지원하지 않는 형식입니다.", "최신 버전 프로그램으로 열어 주세요.");
        }

        return ("백업 파일이 손상되었거나 읽을 수 없습니다.", "원본 PC에서 다시 내보내 주세요.");
    }

    /// <summary>결과 화면 제목과 해결 방법(설계 §7.6, §9).</summary>
    public static (string Title, string? Hint) ResultHeadline(RestoreResult result)
        => result.Outcome switch
        {
            RestoreOutcome.Succeeded => ("✅ 가져오기 완료", "Codex를 실행하면 가져온 대화가 표시됩니다."),
            RestoreOutcome.NothingToDo => ("적용할 변경이 없었습니다. 선택한 대화는 모두 이미 이 PC에 있습니다.", null),
            RestoreOutcome.Cancelled => ("적용을 취소하여 이전 상태로 되돌렸습니다. 기존 데이터는 그대로입니다.", "[다시 시도]로 다시 가져올 수 있습니다."),
            RestoreOutcome.RolledBack => ("적용 중 문제가 생겨 이전 상태로 되돌렸습니다. 기존 데이터는 그대로입니다.",
                $"자세한 내용은 로그 폴더에 있습니다: {Services.AppPaths.LogDirectory}"),
            RestoreOutcome.RollbackFailedCritical => ($"[치명] 자동 복구에 실패했습니다. 복구 지점 {result.SnapshotId ?? "(알 수 없음)"}로 수동 복구가 필요합니다.",
                "메인 화면의 [이전 상태로 복구]를 눌러 주세요."),
            _ => NotReadyHeadline(result),
        };

    private static (string Title, string? Hint) NotReadyHeadline(RestoreResult result) => result.PreflightStatus switch
    {
        ImportPlanPreflightStatus.BackupChanged => ("미리보기 이후 백업 파일이 바뀌었습니다.", "백업을 다시 열어 주세요."),
        ImportPlanPreflightStatus.LocalStateChanged => ("미리보기 이후 이 PC의 Codex 데이터가 바뀌었습니다.", "[다시 시도]를 누르면 다시 분석합니다."),
        ImportPlanPreflightStatus.TargetPathUnavailable => ("대상 작업 폴더를 더 이상 찾을 수 없습니다.", "[다시 시도] 후 폴더를 다시 지정해 주세요."),
        _ => (string.IsNullOrWhiteSpace(result.Message) ? "지금은 적용할 수 없습니다." : result.Message, "[다시 시도]로 다시 분석할 수 있습니다."),
    };

    /// <summary>
    /// 복구 지점 짧은 표기(Phase 9_2-29b). Snapshot ID는 <c>yyyyMMdd-HHmmss(UTC)-guid</c>이므로 앞부분을 이 PC 시각
    /// "2026-10-01 03:17"로 바꾼다. 형식이 다르면 ID 앞 15자를 그대로 쓴다(전체 ID는 툴팁/상세).
    /// </summary>
    public static string SnapshotLabel(string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        if (snapshotId.Length >= 15 &&
            DateTime.TryParseExact(snapshotId[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
        {
            return new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return snapshotId.Length > 15 ? snapshotId[..15] : snapshotId;
    }

    /// <summary>
    /// 백업을 만든 앱 버전 표기(Phase 9_2-28): "만든 앱 v0.1.1". 규칙: <c>+</c> 뒤(빌드 메타데이터)는 버리고,
    /// 숫자 버전이면 끝의 0인 네 번째 자리(revision)만 줄인다(세 자리는 유지: 0.1.0 → v0.1.0, 0.1.1.0 → v0.1.1, 0.1.1.2 → v0.1.1.2).
    /// 숫자 버전이 아니면(예: 0.2.0-beta) 그대로 쓴다. 비어 있으면 "만든 앱 버전 알 수 없음".
    /// </summary>
    public static string BackupAppVersion(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion))
        {
            return "만든 앱 버전 알 수 없음";
        }

        string core = appVersion.Trim();
        int plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        if (Version.TryParse(core, out Version? version))
        {
            string text = version.Build < 0
                ? $"{version.Major}.{version.Minor}"
                : version.Revision > 0
                    ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                    : $"{version.Major}.{version.Minor}.{version.Build}";
            return "만든 앱 v" + text;
        }

        return "만든 앱 v" + core.TrimStart('v', 'V');
    }

    /// <summary>
    /// (Phase 9_2-30) 사람이 읽는 경로 표기: <see cref="CanonicalPath.Display"/>(<c>\?\</c> 접두사 제거, 구분자 통일, 끝 구분자 제거, 대소문자 보존).
    /// 정규화할 수 없는 값(상대 경로 등)은 원문 그대로 둔다. 원문은 툴팁/상세 정보에 따로 남긴다.
    /// </summary>
    public static string DisplayPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CanonicalPath.TryCreate(path, out CanonicalPath? canonical, out _) ? canonical!.Display : path;
    }

    /// <summary>
    /// 트리 행 안의 "이 PC 위치" 한 줄(Phase 9_2-26). 이 PC에 이미 있는 대화(새 대화가 아닌 모든 경우)에만 보인다.
    /// </summary>
    public static string? RowLocation(ImportConversationPreview conversation)
        => conversation.Relation != RevisionRelation.New && LocalLocation(conversation.LocalLocation) is { } location
            ? "이 PC 위치: " + location
            : null;

    /// <summary>날짜 표시("만든 날 2026-06-29 · 마지막 2026-09-13").</summary>
    public static string? Dates(DateTimeOffset? created, DateTimeOffset? updated)
    {
        var parts = new List<string>();
        if (created is { } c)
        {
            parts.Add("만든 날 " + c.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (updated is { } u)
        {
            parts.Add("마지막 " + u.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
