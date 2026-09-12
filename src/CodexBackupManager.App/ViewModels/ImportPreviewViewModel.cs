using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.App.ViewModels;

/// <summary>
/// 대화 하나의 Import Preview 표시용 행. 내부 enum 영어명을 그대로 보여주지 않고 한국어 라벨로
/// 바꾼다(요구사항 11).
/// </summary>
public sealed class ImportConversationRowViewModel(ImportConversationPreview preview)
{
    /// <summary>thread ID.</summary>
    public string ThreadId { get; } = preview.ThreadId;

    /// <summary>backup의 표시용 제목(가공값). 없으면 thread ID 일부로 대체.</summary>
    public string Title { get; } = string.IsNullOrWhiteSpace(preview.ResolvedTitle)
        ? preview.ThreadId
        : preview.ResolvedTitle;

    /// <summary>사용자에게 보여줄 한국어 상태 라벨.</summary>
    public string StatusLabel { get; } = DescribeRelation(preview.Relation);

    /// <summary>metadata 차이 요약(있으면). 없으면 <c>null</c>.</summary>
    public string? MetadataDiffSummary { get; } = DescribeMetadataDiff(preview.Metadata);

    /// <summary>metadata 차이가 있는지(UI 강조 표시용).</summary>
    public bool HasMetadataDiff { get; } = preview.Metadata.HasAny;

    private static string DescribeRelation(RevisionRelation relation) => relation switch
    {
        RevisionRelation.New => "신규",
        RevisionRelation.Identical => "동일",
        RevisionRelation.IncomingAhead => "업데이트 가능",
        RevisionRelation.LocalAhead => "현재 PC가 더 최신",
        RevisionRelation.Diverged => "분기 충돌",
        RevisionRelation.Unverifiable => "확인 불가",
        _ => relation.ToString(),
    };

    private static string? DescribeMetadataDiff(MetadataDifferences diff)
    {
        if (!diff.HasAny)
        {
            return null;
        }

        var parts = new List<string>();
        if (diff.CwdDiffers)
        {
            parts.Add("작업 폴더");
        }

        if (diff.ProjectAssignmentDiffers)
        {
            parts.Add("프로젝트 연결");
        }

        if (diff.PinnedDiffers)
        {
            parts.Add("고정 여부");
        }

        if (diff.SectionDiffers)
        {
            parts.Add("섹션");
        }

        if (diff.TitleDiffers)
        {
            parts.Add("제목");
        }

        if (diff.RecencyDiffers)
        {
            parts.Add("최근 사용 시각");
        }

        return string.Join(", ", parts) + " 다름";
    }
}

/// <summary>프로젝트 하나의 Import Preview 표시용 행.</summary>
public sealed class ImportProjectRowViewModel
{
    /// <summary>
    /// <paramref name="requestPathOverride"/>가 주어지면(즉 <see cref="CanOverridePath"/>가 참일
    /// 프로젝트라면) "폴더 선택" 버튼을 눌렀을 때 이 프로젝트의 <see cref="ProjectId"/>를 인자로
    /// 호출한다 — 실제 폴더 선택/재지정 로직은 <see cref="MainViewModel"/>이 갖고 있다(요구사항:
    /// View code-behind에 경로 상태를 두지 않는다).
    /// </summary>
    internal ImportProjectRowViewModel(ImportProjectPreview preview, Action<string?>? requestPathOverride)
    {
        ProjectId = preview.ProjectId;
        DisplayName = preview.DisplayName;
        Conversations = preview.Conversations.Select(c => new ImportConversationRowViewModel(c)).ToList();
        PathMappingStatusText = DescribePathMapping(preview.PathMapping);
        CanOverridePath = preview.PathMapping.CanManuallyOverride;
        SummaryText =
            $"신규 {preview.CountOf(RevisionRelation.New)} · 동일 {preview.CountOf(RevisionRelation.Identical)} · " +
            $"업데이트 가능 {preview.CountOf(RevisionRelation.IncomingAhead)} · 최신 유지 {preview.CountOf(RevisionRelation.LocalAhead)} · " +
            $"분기 충돌 {preview.CountOf(RevisionRelation.Diverged)} · 확인 불가 {preview.CountOf(RevisionRelation.Unverifiable)}";

        OverridePathCommand = CanOverridePath && requestPathOverride is not null
            ? new RelayCommand(() => requestPathOverride(ProjectId))
            : null;
    }

    /// <summary>backup 쪽 원본 프로젝트 ID. "기타 대화"면 <c>null</c>.</summary>
    public string? ProjectId { get; }

    /// <summary>표시 이름.</summary>
    public string DisplayName { get; }

    /// <summary>경로 재매핑 상태 문구.</summary>
    public string PathMappingStatusText { get; }

    /// <summary>
    /// "폴더 선택" 버튼을 보여줘도 되는지("기타 대화"만 제외 — <see cref="ProjectPathMapping.CanManuallyOverride"/> 그대로).
    /// </summary>
    public bool CanOverridePath { get; }

    /// <summary><see cref="CanOverridePath"/>가 참일 때만 값이 있다.</summary>
    public RelayCommand? OverridePathCommand { get; }

    /// <summary>이 프로젝트의 대화별 상태 요약(개수).</summary>
    public string SummaryText { get; }

    /// <summary>선택된 대화 목록(요구사항 11 — dependency-only 조상은 여기 포함되지 않는다).</summary>
    public IReadOnlyList<ImportConversationRowViewModel> Conversations { get; }

    private static string DescribePathMapping(ProjectPathMapping mapping) => mapping.Status switch
    {
        ProjectPathMappingStatus.NotApplicable => string.Empty,
        ProjectPathMappingStatus.AutoLinked => $"자동 연결됨 → {mapping.ResolvedLocalPath}",
        ProjectPathMappingStatus.NotFound => "원본 경로를 찾을 수 없습니다 — 폴더를 다시 지정해야 합니다.",
        ProjectPathMappingStatus.ManuallyLinked => $"사용자가 지정함 → {mapping.ResolvedLocalPath}",
        _ => string.Empty,
    };
}

/// <summary>
/// <see cref="ImportPreview"/>를 화면에 바인딩하기 좋은 형태로 감싼다. Phase 6 — 판정 결과를 그대로
/// 보여주기만 한다(Phase 7 전까지 아무것도 적용하지 않는다).
/// </summary>
public sealed class ImportPreviewViewModel
{
    /// <summary>빌드에 성공했는지(<see cref="ImportPreview.Success"/> 그대로).</summary>
    public bool Success { get; }

    /// <summary>검증 실패 사유(실패했을 때만).</summary>
    public IReadOnlyList<string> ValidationErrors { get; }

    /// <summary>프로젝트별 Preview.</summary>
    public ObservableCollection<ImportProjectRowViewModel> Projects { get; } = [];

    /// <summary>전체 요약 문구(상단에 표시).</summary>
    public string SummaryText { get; }

    /// <summary>경고(누락된 attachment 등). 사용자 원문 없음.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary><see cref="Success"/>가 <c>false</c>일 때만 채워지는 실패 사유 문구. 성공이면 <c>null</c>.</summary>
    public string? FailureMessage { get; }

    /// <param name="preview">감쌀 Preview.</param>
    /// <param name="requestPathOverride">
    /// "폴더 선택" 버튼을 눌렀을 때 호출할 콜백(인자: 그 프로젝트의 backup 쪽 원본 ID). 생략하면
    /// 모든 프로젝트 행의 <see cref="ImportProjectRowViewModel.OverridePathCommand"/>가 <c>null</c>이
    /// 된다(버튼 자체를 숨길 때 등).
    /// </param>
    public ImportPreviewViewModel(ImportPreview preview, Action<string?>? requestPathOverride = null)
    {
        Success = preview.Success;
        ValidationErrors = preview.ValidationErrors;
        Warnings = preview.Warnings;

        foreach (ImportProjectPreview project in preview.Projects)
        {
            Projects.Add(new ImportProjectRowViewModel(project, requestPathOverride));
        }

        int totalSelected = preview.Projects.Sum(p => p.Conversations.Count);
        SummaryText = preview.Success
            ? $"프로젝트 {preview.Projects.Count}개 · 대화 {totalSelected}개" +
              (preview.DependencyOnlyConversations.Count > 0 ? $" (조상 전용 {preview.DependencyOnlyConversations.Count}개 별도)" : string.Empty)
            : "이 백업 파일을 사용할 수 없습니다.";

        FailureMessage = preview.Success ? null : string.Join(" ", preview.ValidationErrors);
    }
}
