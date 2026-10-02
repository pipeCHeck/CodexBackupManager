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
                "백업에 이 PC보다 뒤의 내용이 있지만, 이 PC 대화가 압축 파일로 저장돼 있어 이어 붙일 수 없습니다.",
            RevisionRelation.IncomingAhead => "백업에 이 PC보다 뒤의 내용이 있습니다. 이 PC 대화에 이어 붙입니다.",
            RevisionRelation.Identical => "이미 이 PC에 있습니다. 내용이 같습니다.",
            RevisionRelation.LocalAhead => "이 PC에서 더 이어서 쓴 대화입니다. 그대로 두고 건너뜁니다.",
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
            ImportUnselectableReason.LocalAhead => "이 PC에서 더 이어서 쓴 대화라 건너뜁니다.",
            ImportUnselectableReason.Diverged => "양쪽이 다르게 이어져 이번 버전에서는 가져올 수 없습니다.",
            ImportUnselectableReason.Unverifiable => "이 PC 데이터가 불완전해 비교할 수 없습니다.",
            ImportUnselectableReason.CompressedUpdate => "이 PC 대화가 압축 파일(.jsonl.zst)로 저장돼 있어 이어 붙일 수 없습니다.",
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
            parts.Add("이 PC 대화가 압축 파일(.jsonl.zst)로 저장돼 있어 백업의 뒤 내용을 이어 붙일 수 없습니다.");
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
    /// 결과 화면의 이어받은 대화 한 줄. 괄호는 위치 안의 "(보관됨)" 한 번만 쓰고 나머지는 " · "로 잇는다(Phase 9_2-35).
    /// 예: "이어받음 · 이 PC 위치: 기타 대화 (보관됨) · 기존 위치 유지".
    /// </summary>
    public static string UpdatedResultText(ConversationLocalLocation? location)
        => LocalLocation(location) is { } text
            ? $"이어받음 · 이 PC 위치: {text} · 기존 위치 유지"
            : "이어받음 · 기존 위치 유지";

    /// <summary>
    /// 작업 폴더 상태 문구(설계 §7.3, 9_2는 CreateNew 대신 A안 문구). "사용자가 지정함"은 실제로 연결될 때만 쓴다.
    /// </summary>
    /// <param name="target">목적지.</param>
    /// <param name="directory">이 PC 프로젝트 목록(연결 대상 이름, 폴더 없는 등록 프로젝트 판정에 쓴다).</param>
    /// <param name="originalRootPaths">백업의 원본 루트(OriginalRootMissing 보충 문구 판정용).</param>
    /// <param name="creationDeclined">(Phase 9_5) 사용자가 "새 프로젝트를 만들지 않고 기타 대화로"를 골랐는지.</param>
    public static string TargetStatus(
        ProjectTarget target, ProjectDirectory directory, IReadOnlyList<string> originalRootPaths, bool creationDeclined = false)
    {
        if (target.Kind == ProjectTargetKind.CreateNew)
        {
            return $"✨ 이 폴더로 새 프로젝트 '{target.NewProjectName}'을(를) 만들어 연결합니다.";
        }

        if (creationDeclined && target.Kind == ProjectTargetKind.Uncategorized)
        {
            return "새 프로젝트를 만들지 않고 기타 대화로 가져옵니다.";
        }

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
            // Phase 9_5a-05 — Desktop 상태 파일 게이트 실패는 전용 문구(state DB 스키마가 다른 경우는 기존 문구 그대로).
            ProjectTargetReason.CreationUnsupported when directory.ProjectCreation == ProjectCreationSupport.DesktopStateUnsupported =>
                DesktopStateUnsupportedStatus,
            ProjectTargetReason.CreationUnsupported =>
                "이 Codex 버전에서는 프로젝트 자동 생성을 지원하지 않습니다. 기타 대화로 들어갑니다.",
            _ => "이 PC의 프로젝트에 연결할 수 없습니다. 지금은 기타 대화로 들어갑니다.",
        };
    }

    /// <summary>(Phase 9_5-11) 백업의 "기타 대화" 그룹 작업 폴더 행 문구.</summary>
    public const string BackupUncategorizedGroupStatus =
        "다른 PC에서 프로젝트 없이 쓰던 채팅입니다. 이 PC에서도 프로젝트 없는 채팅으로 가져옵니다.";

    /// <summary>(Phase 9_5a-05) Desktop 상태 파일 형식을 확인하지 못해 새 프로젝트를 만들지 않을 때의 작업 폴더 행 문구.</summary>
    public const string DesktopStateUnsupportedStatus =
        "Codex 데스크톱 앱의 프로젝트 목록 형식을 확인하지 못해 새 프로젝트를 만들지 않습니다. 기타 대화로 가져옵니다.";

    /// <summary>목적지가 등록 프로젝트 연결인지(초록 표시용).</summary>
    public static bool IsLinked(ProjectTarget target) => target.Kind == ProjectTargetKind.LinkExisting;

    /// <summary>(Phase 9_5) 새 프로젝트 하나의 표기: "'이름'(폴더)".</summary>
    public static string NewProjectLabel(NewProjectGroup project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $"'{project.Name}'({DisplayPath(project.FolderPath)})";
    }

    /// <summary>(Phase 9_5-05) "새로 만들 프로젝트 N개: '이름'(폴더), …". 없으면 <c>null</c>.</summary>
    public static string? NewProjectsLine(ImportSelectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.NewProjects.Count == 0
            ? null
            : $"새로 만들 프로젝트 {summary.NewProjects.Count}개: {string.Join(", ", summary.NewProjects.Select(NewProjectLabel))}";
    }

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
        => $"가져오기: 새 대화 {summary.ImportCount} · 이어받기 {summary.UpdateCount} · 기타 대화로 {summary.UncategorizedImportCount}" +
           (summary.NewProjects.Count > 0 ? $" · 새 프로젝트 {summary.NewProjects.Count}" : string.Empty) +
           (summary.RelinkCount > 0 ? $" · 연결 변경 {summary.RelinkCount}" : string.Empty);

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

        string? uncategorized = summary.UncategorizedImportCount > 0
            ? $"선택한 대화 중 \"기타 대화\"로 들어가는 것이 {summary.UncategorizedImportCount}개 있습니다."
            : null;
        string? newProjects = NewProjectsLine(summary);
        return newProjects is null ? uncategorized : uncategorized is null ? newProjects : $"{newProjects} · {uncategorized}";
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
    /// <remarks>(Phase 9_3-01) 옮기기가 있으면 "대화 N개 가져오기 · M개 옮기기", 옮기기만 있으면 "대화 M개 옮기기".</remarks>
    public static string ImportButton(ImportSelectionSummary summary)
    {
        int writes = summary.ImportCount + summary.UpdateCount;
        if (summary.RelinkCount == 0)
        {
            return $"대화 {writes}개 가져오기";
        }

        return writes == 0 ? $"대화 {summary.RelinkCount}개 옮기기" : $"대화 {writes}개 가져오기 · {summary.RelinkCount}개 옮기기";
    }

    /// <summary>확인 대화상자 문구(설계 §7.5).</summary>
    /// <param name="summary">선택 요약.</param>
    /// <param name="directory">(Phase 9_3-01) 이 PC 프로젝트 목록(옮길 목적지 이름). 없으면 목적지 이름 대신 "등록된 프로젝트".</param>
    public static string ConfirmMessage(ImportSelectionSummary summary, ProjectDirectory? directory = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string relinks = string.Empty;
        if (summary.RelinkCount > 0)
        {
            List<string> moves = summary.Conversations.Where(c => c.IsRelinkSelected).Select(c => "    " + RelinkLine(summary, c, directory)).ToList();
            const int Shown = 5;
            relinks = $"  프로젝트 옮기기 {summary.RelinkCount}개:" + Environment.NewLine +
                      string.Join(Environment.NewLine, moves.Take(Shown)) + Environment.NewLine +
                      (moves.Count > Shown ? $"    … 외 {moves.Count - Shown}개" + Environment.NewLine : string.Empty);
        }

        return "다음 내용을 Codex에 적용합니다." + Environment.NewLine + Environment.NewLine +
               $"  새로 가져올 대화   {summary.ImportCount}개" + Environment.NewLine +
               $"  이어받을 대화      {summary.UpdateCount}개" + Environment.NewLine +
               relinks +
               (summary.NewProjects.Count > 0
                   ? $"  새로 만들 프로젝트 {summary.NewProjects.Count}개: {string.Join(", ", summary.NewProjects.Select(NewProjectLabel))}" + Environment.NewLine
                   : string.Empty) +
               $"  기타 대화로 들어갈 대화 {summary.UncategorizedImportCount}개" + Environment.NewLine + Environment.NewLine +
               "적용 전에 현재 상태의 복구 지점(Snapshot)을 만듭니다." + Environment.NewLine +
               "실패하면 자동으로 되돌립니다. Codex가 완전히 종료되어 있어야 합니다.";
    }

    /// <summary>(Phase 9_3-01) "'제목' 기타 대화 → 'Alpha'" 또는 "→ 새 프로젝트 '이름'".</summary>
    public static string RelinkLine(ImportSelectionSummary summary, ImportSelectionConversation conversation, ProjectDirectory? directory)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(conversation);
        string from = LocalLocation(conversation.Preview.LocalLocation) ?? "기타 대화";
        return $"'{TitleOf(conversation.Preview)}' {from} → {RelinkDestination(summary, conversation.ProjectKey, directory, created: false)}";
    }

    /// <summary>(Phase 9_3-01/04) 옮길 목적지 표기: 등록 프로젝트 "'Alpha'", 새 프로젝트 "새 프로젝트 '이름'"(결과 화면은 "새로 만든 프로젝트").</summary>
    public static string RelinkDestination(ImportSelectionSummary summary, string? projectKey, ProjectDirectory? directory, bool created)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ImportSelectionProject? project = summary.Projects.FirstOrDefault(p => p.ProjectKey == projectKey);
        if (project?.Target is { Kind: ProjectTargetKind.CreateNew })
        {
            NewProjectGroup? group = summary.NewProjects.FirstOrDefault(g => projectKey is not null && g.ProjectKeys.Contains(projectKey));
            string name = group?.Name ?? project.Target.NewProjectName ?? string.Empty;
            return (created ? "새로 만든 프로젝트 '" : "새 프로젝트 '") + name + "'";
        }

        string? known = project?.Target.LinkDbProjectId is { } id ? directory?.FindById(id)?.DisplayName : null;
        return known is not null ? $"'{known}'" : "등록된 프로젝트";
    }

    /// <summary>(Phase 9_3-00) 옮길 수 없는 이유(행 안 한 줄). 옮길 수 있거나 체크를 숨기는 경우는 <c>null</c>.</summary>
    public static string? RelinkUnavailableReason(RelinkStatus status) => status switch
    {
        RelinkStatus.DesktopAssigned or RelinkStatus.DesktopProjectless => RelinkDesktopRecorded,
        RelinkStatus.DesktopStateUnavailable => RelinkDesktopStateUnavailable,
        _ => null,
    };

    // ── Phase 9_4 되돌리기 문구 ─────────────────────────────────────────────────

    /// <summary>(Phase 9_4-04) 기록 단위로 되돌릴 수 없는 이유.</summary>
    public static string UndoUnavailableText(CodexBackupManager.Restore.Undo.UndoUnavailableReason reason) => reason switch
    {
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.OldFormat => "이 기록은 이전 버전에서 만들어져 되돌리기를 지원하지 않습니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.SidebarRepair => "사이드바 보정 기록은 되돌리기를 지원하지 않습니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.UndoRecord => "되돌리기 작업의 기록입니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.NotCompleted => "완료되지 않은 기록이라 되돌릴 수 없습니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.AlreadyUndone => "이미 되돌린 기록입니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.OtherHome => "다른 Codex 폴더의 기록입니다.",
        CodexBackupManager.Restore.Undo.UndoUnavailableReason.RecordCorrupt => "기록이 손상되어 되돌릴 수 없습니다.",
        _ => "되돌릴 수 있습니다.",
    };

    /// <summary>(Phase 9_4-03) 대화가 되돌리기를 막는 이유.</summary>
    public static string UndoBlockText(CodexBackupManager.Restore.Undo.UndoBlockReason reason) => reason switch
    {
        CodexBackupManager.Restore.Undo.UndoBlockReason.OpenedInCodex => "Codex에서 이미 열어 본 대화라 되돌릴 수 없습니다.",
        CodexBackupManager.Restore.Undo.UndoBlockReason.TraceUnknown => "Codex에서 열어 봤는지 확인할 수 없어 되돌리지 않습니다.",
        CodexBackupManager.Restore.Undo.UndoBlockReason.RolloutChanged => "가져온 뒤 이어서 쓴 대화라 되돌릴 수 없습니다.",
        CodexBackupManager.Restore.Undo.UndoBlockReason.RowChanged => "가져온 뒤 바뀐 대화라 되돌릴 수 없습니다.",
        CodexBackupManager.Restore.Undo.UndoBlockReason.LinkChanged => "옮긴 뒤 위치가 바뀐 대화라 되돌릴 수 없습니다.",
        _ => "가져온 대화가 이 PC에서 지워져 되돌릴 수 없습니다.",
    };

    /// <summary>(Phase 9_4-03) 새 프로젝트를 남겨 둔 이유.</summary>
    public static string ProjectKeepText(CodexBackupManager.Restore.Undo.ProjectKeepReason? reason) => reason switch
    {
        CodexBackupManager.Restore.Undo.ProjectKeepReason.ProjectChanged or CodexBackupManager.Restore.Undo.ProjectKeepReason.SidebarEntryChanged =>
            "만든 뒤 프로젝트가 바뀌어 프로젝트는 남겨 두었습니다.",
        CodexBackupManager.Restore.Undo.ProjectKeepReason.DesktopStateUnavailable => "Codex 데스크톱 앱 상태 파일을 확인하지 못해 프로젝트는 남겨 두었습니다.",
        _ => "Codex에서 이 프로젝트를 사용 중이라 프로젝트는 남겨 두었습니다.",
    };

    /// <summary>(Phase 9_4-07) 되돌리기 가능 여부 한 줄(막는 대화는 최대 3개 제목과 이유).</summary>
    public static string UndoAssessmentText(CodexBackupManager.Restore.Undo.UndoAssessment assessment, IReadOnlyDictionary<string, string> titles)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(titles);
        if (assessment.Unavailable != CodexBackupManager.Restore.Undo.UndoUnavailableReason.None)
        {
            return UndoUnavailableText(assessment.Unavailable);
        }

        if (assessment.Blockers.Count > 0)
        {
            List<string> lines = assessment.Blockers
                .GroupBy(b => b.ThreadId, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"'{(titles.TryGetValue(g.Key, out string? t) ? t : FallbackTitle(g.Key))}' — {UndoBlockText(g.First().Reason)}")
                .ToList();
            return "되돌릴 수 없습니다(부분 되돌리기는 지원하지 않습니다): " + string.Join(" / ", lines.Take(3)) +
                   (lines.Count > 3 ? $" 외 {lines.Count - 3}개" : string.Empty);
        }

        int deletes = assessment.Projects.Count(p => p.Delete);
        int keeps = assessment.Projects.Count - deletes;
        return "되돌릴 수 있습니다." +
               (deletes > 0 ? $" 새로 만든 프로젝트 {deletes}개도 지웁니다." : string.Empty) +
               (keeps > 0 ? $" 새로 만든 프로젝트 {keeps}개는 사용 중이라 남겨 둡니다." : string.Empty);
    }

    /// <summary>(Phase 9_4-09) 되돌리기 결과 문구.</summary>
    public static string UndoResultText(CodexBackupManager.Restore.Undo.UndoResult result, IReadOnlyDictionary<string, string> titles)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(titles);
        switch (result.Outcome)
        {
            case RestoreOutcome.Succeeded:
                string text = $"가져오기를 되돌렸습니다. 대화 {result.UndoneConversationCount}개 · 지운 프로젝트 {result.DeletedProjectCount}개";
                foreach (CodexBackupManager.Restore.Undo.UndoProjectDecision kept in result.KeptProjects)
                {
                    text += $"{Environment.NewLine}'{kept.Name}': {ProjectKeepText(kept.KeepReason)}";
                }

                return text;
            case RestoreOutcome.NotReady when result.Assessment is { } assessment && !assessment.CanUndo:
                return UndoAssessmentText(assessment, titles);
            case RestoreOutcome.RollbackFailedCritical:
                return $"CRITICAL: {result.Message}";
            default:
                return result.Message;
        }
    }

    /// <summary>(Phase 9_4-07) 되돌리기 확인 문구.</summary>
    public const string UndoConfirmMessage =
        "이 가져오기가 만든 대화·파일·프로젝트를 지우고, 이어받은 대화와 옮긴 대화를 가져오기 전 상태로 되돌립니다." + "\n" +
        "되돌리기 전에 복구 지점(Snapshot)을 만듭니다. Codex가 완전히 종료되어 있어야 합니다. 계속하시겠습니까?";

    /// <summary>(Phase 9_3-00) Desktop이 위치를 따로 기록한 대화.</summary>
    public const string RelinkDesktopRecorded =
        "Codex 데스크톱 앱이 이 대화의 위치를 따로 기록하고 있어 이 앱에서 옮기지 않습니다. Codex에서 직접 옮겨 주세요.";

    /// <summary>(Phase 9_3-00) Desktop 상태 파일을 확인하지 못했을 때.</summary>
    public const string RelinkDesktopStateUnavailable = "Codex 데스크톱 앱 상태 파일을 확인하지 못해 옮기지 않습니다.";

    /// <summary>Codex 실행 중 안내(가져오기 시작 시, 설계 §9).</summary>
    public const string CodexRunningAtStart = "가져오기는 Codex를 종료한 상태에서만 할 수 있습니다. Codex를 완전히 종료한 뒤 [다시 확인]을 눌러 주세요.";

    /// <summary>(Phase 9_2-36) 대기 화면에서 다시 확인했는데 아직 실행 중일 때(이 PC 시각).</summary>
    public static string CodexStillRunning(DateTime checkedAtLocal)
        => $"아직 Codex가 실행 중입니다(확인 {checkedAtLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";

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
