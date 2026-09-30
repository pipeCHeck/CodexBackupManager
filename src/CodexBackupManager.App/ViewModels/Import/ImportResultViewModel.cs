using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Restore;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>결과 화면의 대화 한 줄.</summary>
/// <param name="Title">대화 제목.</param>
/// <param name="Text">무엇을 했는지 / 지금 어디 있는지.</param>
public sealed record ImportResultItem(string Title, string Text)
{
    /// <summary>화면 읽기 프로그램·UI 자동화가 읽는 이름(Phase 9_2-23).</summary>
    public string AutomationName => $"{Title}, {Text}";
}

/// <summary>결과 화면의 묶음(프로젝트 → 어디로).</summary>
/// <param name="Header">묶음 이름.</param>
/// <param name="Destination">어디로 들어갔는지. 없으면 <c>null</c>.</param>
/// <param name="Items">대화들.</param>
public sealed record ImportResultGroup(string Header, string? Destination, IReadOnlyList<ImportResultItem> Items)
{
    /// <summary>화면 읽기 프로그램·UI 자동화가 읽는 이름(Phase 9_2-23).</summary>
    public string AutomationName => Destination is null ? Header : $"{Header} {Destination}";
}

/// <summary>
/// 가져오기 결과 화면(Phase 9_2-16 → 9_2-29a에서 <see cref="ImportWorkspaceViewModel"/>에서 분리, 설계 §3.1·§7.6).
/// 적용 결과(<see cref="RestoreResult"/>)와 적용한 Plan을 평범한 말로 바꾼다. 판정은 하지 않는다.
/// </summary>
public sealed class ImportResultViewModel
{
    /// <summary>결과를 만든다.</summary>
    /// <param name="result">적용 결과.</param>
    /// <param name="plan">적용한 Plan.</param>
    /// <param name="summary">적용 당시 선택 요약(NothingToDo일 때 현재 위치 목록).</param>
    /// <param name="titles">thread ID → 표시 제목.</param>
    /// <param name="localProjects">이 PC 프로젝트 목록(연결 대상 이름).</param>
    public ImportResultViewModel(
        RestoreResult result,
        ImportPlan plan,
        ImportSelectionSummary summary,
        IReadOnlyDictionary<string, string> titles,
        ProjectDirectory localProjects)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(localProjects);

        Outcome = result.Outcome;
        (Title, Hint) = ImportTexts.ResultHeadline(result);
        SnapshotId = result.Outcome == RestoreOutcome.Succeeded ? result.SnapshotId : null;
        SnapshotText = SnapshotId is { } id ? "복구 지점 " + ImportTexts.SnapshotLabel(id) : null;

        int imported = plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.Import);
        int updated = plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.Update);
        int skipped = plan.Conversations.Count(c => c.IsSelected && c.PlannedAction == ImportPlannedAction.Skip);

        var groups = new List<ImportResultGroup>();
        switch (result.Outcome)
        {
            case RestoreOutcome.Succeeded:
                CountsText = $"새로 가져옴 {imported} · 이어받음 {updated} · 건너뜀 {skipped}" +
                             (summary.NewProjects.Count > 0 ? $" · 새 프로젝트 {summary.NewProjects.Count}" : string.Empty);
                groups.AddRange(BuildSucceededGroups(plan, titles, localProjects));
                ImportedThreadIds = plan.Conversations
                    .Where(c => c.IsSelected && c.PlannedAction is ImportPlannedAction.Import or ImportPlannedAction.Update)
                    .Select(c => c.ThreadId)
                    .ToList();
                break;

            case RestoreOutcome.NothingToDo:
                groups.Add(BuildCurrentLocationGroup(summary));
                ImportedThreadIds = summary.Conversations
                    .Where(c => c.Preview.IsSelected && c.Preview.LocalLocation is { ExistsLocally: true })
                    .Select(c => c.ThreadId)
                    .ToList();
                break;

            default:
                ImportedThreadIds = [];
                break;
        }

        Groups = groups;
    }

    /// <summary>적용 결과 종류.</summary>
    public RestoreOutcome Outcome { get; }

    /// <summary>결과가 성공 계열인지(초록).</summary>
    public bool IsSuccess => Outcome is RestoreOutcome.Succeeded or RestoreOutcome.NothingToDo;

    /// <summary>[다시 시도]가 의미 있는 결과인지.</summary>
    public bool IsRetryable => Outcome is RestoreOutcome.NotReady or RestoreOutcome.Cancelled or RestoreOutcome.RolledBack;

    /// <summary>제목.</summary>
    public string Title { get; }

    /// <summary>해결 방법/안내. 없으면 <c>null</c>.</summary>
    public string? Hint { get; }

    /// <summary>개수 한 줄(성공일 때만).</summary>
    public string? CountsText { get; }

    /// <summary>복구 지점 짧은 표기("복구 지점 2026-10-01 03:17"). 성공이 아니면 <c>null</c>.</summary>
    public string? SnapshotText { get; }

    /// <summary>복구 지점 전체 ID(툴팁·상세용).</summary>
    public string? SnapshotId { get; }

    /// <summary>결과 묶음.</summary>
    public IReadOnlyList<ImportResultGroup> Groups { get; }

    /// <summary>목록에서 강조할 대화.</summary>
    public IReadOnlyList<string> ImportedThreadIds { get; }

    private static IEnumerable<ImportResultGroup> BuildSucceededGroups(
        ImportPlan plan, IReadOnlyDictionary<string, string> titles, ProjectDirectory localProjects)
    {
        // Phase 9_5-05 — 새로 만든 프로젝트는 합쳐진 이름(같은 루트면 첫 번째)으로 보여준다(Planner와 같은 규칙).
        IReadOnlyList<NewProjectGroup> created = NewProjectGrouping.Group(plan.Projects
            .Where(p => p.ResolvedTarget is { Kind: ProjectTargetKind.CreateNew } && plan.UsesProjectTarget(p))
            .Select(p => (ImportUserChoices.ProjectKeyOf(p.ProjectId), p.ResolvedTarget!)));

        foreach (IGrouping<string?, ImportPlanConversation> group in plan.Conversations
                     .Where(c => c.PlannedAction is ImportPlannedAction.Import or ImportPlannedAction.Update)
                     .GroupBy(c => c.TargetProjectKey))
        {
            ImportPlanProject? project = group.Key is null
                ? null
                : plan.Projects.FirstOrDefault(p => ImportUserChoices.ProjectKeyOf(p.ProjectId) == group.Key);
            string header = project?.DisplayName ?? "필요한 원본 대화";
            NewProjectGroup? newProject = group.Key is { } key ? created.FirstOrDefault(g => g.ProjectKeys.Contains(key)) : null;
            string destination = newProject is not null
                ? $"→ 새로 만든 프로젝트 {ImportTexts.NewProjectLabel(newProject)}"
                : project?.ResolvedTarget is { Kind: ProjectTargetKind.LinkExisting } target
                    ? $"→ '{localProjects.FindById(target.LinkDbProjectId)?.DisplayName ?? project.DisplayName}' 프로젝트 ({(target.FolderPath is { } folder ? ImportTexts.DisplayPath(folder) : string.Empty)})"
                    : "→ 기타 대화";

            var items = group
                .Select(c => new ImportResultItem(
                    titles.TryGetValue(c.ThreadId, out string? t) ? t : ImportTexts.FallbackTitle(c.ThreadId),
                    c.PlannedAction == ImportPlannedAction.Import ? "새로 가져옴" : "이어받음"))
                .ToList();
            yield return new ImportResultGroup(header, destination, items);
        }
    }

    private static ImportResultGroup BuildCurrentLocationGroup(ImportSelectionSummary summary)
    {
        var items = summary.Conversations
            .Where(c => c.Preview.IsSelected)
            .Select(c => new ImportResultItem(
                ImportTexts.TitleOf(c.Preview),
                ImportTexts.LocalLocation(c.Preview.LocalLocation) is { } location ? $"이 PC 위치: {location}" : "이 PC에 없음"))
            .ToList();
        return new ImportResultGroup("선택한 대화의 현재 위치", null, items);
    }
}
