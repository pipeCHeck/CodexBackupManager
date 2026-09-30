using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// 백업 프로젝트 하나의 목적지에 대한 사용자 결정(Phase 9_2-01, 설계 §5.2).
/// </summary>
/// <param name="FolderPath">
/// 사용자가 고른 폴더. <paramref name="UseSuggestion"/>이 <c>false</c>일 때만 쓴다.
/// <see cref="ProjectTargetResolver.ResolveFolder"/>(userSelected: true)로 판정한다.
/// </param>
/// <param name="NewProjectName">새 프로젝트 이름(Phase 9_5용). 지금은 값이 있으면 선택 오류(적용 불가)다.</param>
/// <param name="UseSuggestion"><c>true</c>면 <see cref="ImportProjectPreview.SuggestedTarget"/>을 그대로 쓴다.</param>
public sealed record ProjectTargetDecision(string? FolderPath, string? NewProjectName, bool UseSuggestion)
{
    /// <summary>제안된 목적지를 그대로 쓰는 결정.</summary>
    public static ProjectTargetDecision Suggested { get; } = new(null, null, UseSuggestion: true);

    /// <summary>사용자가 고른 폴더로 판정하는 결정.</summary>
    public static ProjectTargetDecision Folder(string folderPath) => new(folderPath, null, UseSuggestion: false);
}

/// <summary>
/// 사용자가 가져오기 화면에서 고른 것 전부(Phase 9_2-01, 설계 §5.2). ViewModel이 모아서 Core에 넘기는 유일한 형태다.
/// </summary>
/// <remarks>
/// <para>
/// 이 값은 판정을 하지 않는다. 선택 가능 여부, 조상 자동 포함, 최종 동작은 <see cref="ImportSelection.Compute"/>가
/// 정하고, <see cref="ImportPlanBuilder.Build(ImportPreview,string,ImportUserChoices,System.Threading.CancellationToken)"/>가
/// 같은 규칙으로 Plan을 만든다.
/// </para>
/// <para>설계 §5.2의 <c>RelinkThreadIds</c>는 Phase 9_3에서 추가한다.</para>
/// </remarks>
/// <param name="IncludedThreadIds">
/// 사용자가 체크한 대화(백업 manifest에서 선택된 대화만 의미가 있다). 조상은 넣지 않아도 자동 포함된다.
/// 선택 불가 대화가 들어 있으면 무시하고 경고를 남긴다.
/// </param>
/// <param name="ProjectDecisions">
/// 백업 프로젝트 키(<see cref="ProjectKeyOf(string?)"/>) → 목적지 결정. 없는 키는 <see cref="ProjectTargetDecision.Suggested"/>로 본다.
/// </param>
public sealed record ImportUserChoices(
    IReadOnlySet<string> IncludedThreadIds,
    IReadOnlyDictionary<string, ProjectTargetDecision> ProjectDecisions)
{
    /// <summary>
    /// 백업의 "기타 대화" 그룹(<see cref="ImportProjectPreview.ProjectId"/> = <c>null</c>)의 프로젝트 키.
    /// Codex 프로젝트 ID(UUID 형식)와 겹치지 않도록 공백과 괄호를 넣은 고정 문자열이다.
    /// </summary>
    public const string UncategorizedProjectKey = "(uncategorized)";

    /// <summary>백업 프로젝트 ID → 프로젝트 키. "기타 대화"(<c>null</c>)는 <see cref="UncategorizedProjectKey"/>.</summary>
    public static string ProjectKeyOf(string? backupProjectId) => backupProjectId ?? UncategorizedProjectKey;

    /// <summary>백업 프로젝트 → 프로젝트 키.</summary>
    public static string ProjectKeyOf(ImportProjectPreview project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ProjectKeyOf(project.ProjectId);
    }

    /// <summary>이 프로젝트의 결정. 없으면 제안 그대로.</summary>
    public ProjectTargetDecision DecisionFor(string projectKey)
        => ProjectDecisions.TryGetValue(projectKey, out ProjectTargetDecision? decision) ? decision : ProjectTargetDecision.Suggested;

    /// <summary>
    /// 기본 선택(설계 §7.4): 선택 가능한 New와 IncomingAhead(<c>.jsonl.zst</c>가 아닌 것)만 체크, 모든 프로젝트는 제안 목적지.
    /// </summary>
    public static ImportUserChoices CreateDefault(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var decisions = new Dictionary<string, ProjectTargetDecision>(StringComparer.Ordinal);
        foreach (ImportProjectPreview project in preview.Projects)
        {
            decisions[ProjectKeyOf(project)] = ProjectTargetDecision.Suggested;
            foreach (ImportConversationPreview conversation in project.Conversations.Where(c => c.IsSelected))
            {
                if (ImportSelection.GetUnselectableReason(conversation) is null)
                {
                    included.Add(conversation.ThreadId);
                }
            }
        }

        return new ImportUserChoices(included, decisions);
    }
}
