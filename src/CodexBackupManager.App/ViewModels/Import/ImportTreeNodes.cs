using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>
/// 가져오기 트리의 대화 한 줄(Phase 9_2-09). 체크 상태를 따로 들고 있지 않는다 — 원천은
/// <see cref="ImportWorkspaceViewModel"/>의 <see cref="ImportUserChoices"/> 하나이고, 이 노드는 마지막
/// <see cref="ImportSelectionSummary"/>의 해당 대화 결과(<see cref="Result"/>)를 보여줄 뿐이다.
/// </summary>
public sealed class ImportConversationNodeViewModel : ObservableObject
{
    private readonly ImportWorkspaceViewModel _owner;
    private ImportSelectionConversation _result;
    private bool _isVisible = true;
    private bool _isTreeSelected;

    internal ImportConversationNodeViewModel(
        ImportWorkspaceViewModel owner, ImportSelectionConversation result, BackupConversationMetadata? metadata)
    {
        _owner = owner;
        _result = result;
        ThreadId = result.ThreadId;
        Title = ImportTexts.TitleOf(result.Preview);
        OriginalCwd = metadata?.OriginalCwd;
        DatesText = ImportTexts.Dates(metadata?.CreatedAtUtc, metadata?.UpdatedAtUtc);
        LimitationText = ImportTexts.Limitation(result.Preview, metadata?.PayloadAttachmentEntries.Count ?? 0);
    }

    /// <summary>thread ID.</summary>
    public string ThreadId { get; }

    /// <summary>표시 제목(없으면 짧은 대체 표기).</summary>
    public string Title { get; }

    /// <summary>원본 작업 폴더(백업 manifest). 없으면 <c>null</c>.</summary>
    public string? OriginalCwd { get; }

    /// <summary>날짜 문구. 없으면 <c>null</c>.</summary>
    public string? DatesText { get; }

    /// <summary>이 대화에 해당하는 알려진 제약. 없으면 <c>null</c>.</summary>
    public string? LimitationText { get; }

    /// <summary><see cref="LimitationText"/>가 있는지.</summary>
    public bool HasLimitation => LimitationText is not null;

    /// <summary>선택 계산 결과(체크/동작/목적지의 유일한 근거).</summary>
    public ImportSelectionConversation Result => _result;

    /// <summary>체크 표시. 사용자가 포함했거나 조상으로 자동 포함되면 체크로 보인다.</summary>
    public bool IsChecked
    {
        get => _result.IsInClosure;
        set
        {
            if (IsCheckable && value != _result.IsIncludedByUser)
            {
                _owner.SetIncluded([ThreadId], value);
            }
        }
    }

    /// <summary>사용자가 체크를 바꿀 수 있는지(선택 가능하고 자동 포함이 아닐 때).</summary>
    public bool IsCheckable => _result.IsSelectable && !_result.IsAutoIncludedAncestor;

    /// <summary>선택한 대화에 필요해 자동으로 포함된 원본 대화인지(흐리게 표시).</summary>
    public bool IsAutoIncluded => _result.IsAutoIncludedAncestor;

    /// <summary>배지 문구.</summary>
    public string BadgeText => ImportTexts.Badge(_result).Text;

    /// <summary>배지 종류(색).</summary>
    public ImportBadgeKind BadgeKind => ImportTexts.Badge(_result).Kind;

    /// <summary>체크할 수 없을 때의 이유(툴팁). 체크할 수 있으면 <c>null</c>.</summary>
    public string? DisabledReason => IsCheckable ? null : ImportTexts.UnselectableTooltip(_result);

    /// <summary>오른쪽 상세의 상태 설명.</summary>
    public string StatusSentence => ImportTexts.StatusSentence(_result);

    /// <summary>이 PC 위치 문구(예: "기타 대화"). 이 PC에 없으면 <c>null</c>.</summary>
    public string? LocalLocationText => ImportTexts.LocalLocation(_result.Preview.LocalLocation);

    /// <summary>상세의 "이미 이 PC에 있음 · 이 PC 위치: …" 한 줄. 이 PC에 없으면 <c>null</c>.</summary>
    public string? PresenceText => LocalLocationText is { } location
        ? (_result.Preview.Relation == RevisionRelation.Identical ? "이미 이 PC에 있음 · " : "이 PC에도 있음 · ") + "이 PC 위치: " + location
        : null;

    /// <summary>검색 필터로 보이는지(선택 상태와 무관).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    /// <summary>트리에서 선택(상세 패널 대상)됐는지.</summary>
    public bool IsTreeSelected
    {
        get => _isTreeSelected;
        set => SetProperty(ref _isTreeSelected, value);
    }

    internal void Update(ImportSelectionConversation result)
    {
        _result = result;
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsCheckable));
        OnPropertyChanged(nameof(IsAutoIncluded));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeKind));
        OnPropertyChanged(nameof(DisabledReason));
        OnPropertyChanged(nameof(StatusSentence));
    }
}

/// <summary>
/// 가져오기 트리의 프로젝트 노드(Phase 9_2-09/10): 3상태 체크, "N개 중 M개", 작업 폴더 영역.
/// 조상 전용 대화를 모아 보여주는 그룹(<see cref="IsDependencyGroup"/>)도 이 타입으로 표현한다(체크/폴더 없음).
/// </summary>
public sealed class ImportProjectNodeViewModel : ObservableObject
{
    private readonly ImportWorkspaceViewModel _owner;
    private ImportSelectionProject? _project;
    private bool _isVisible = true;
    private bool _isTreeSelected;

    internal ImportProjectNodeViewModel(
        ImportWorkspaceViewModel owner,
        ImportSelectionProject? project,
        string displayName,
        IReadOnlyList<ImportConversationNodeViewModel> conversations)
    {
        _owner = owner;
        _project = project;
        DisplayName = displayName;
        Conversations = conversations;
        ChooseFolderCommand = new RelayCommand(() => _owner.ChooseFolder(this), () => CanChangeFolder);
        ResetFolderCommand = new RelayCommand(() => _owner.ResetFolder(this), () => IsFolderUserSelected && CanChangeFolder);
    }

    /// <summary>표시 이름.</summary>
    public string DisplayName { get; }

    /// <summary>백업 프로젝트 키(<see cref="ImportUserChoices.ProjectKeyOf(string?)"/>). 조상 그룹은 <c>null</c>.</summary>
    public string? ProjectKey => _project?.ProjectKey;

    /// <summary>이 프로젝트의 대화.</summary>
    public IReadOnlyList<ImportConversationNodeViewModel> Conversations { get; }

    /// <summary>조상 전용 대화를 모은 그룹인지("필요한 원본 대화(자동 포함)").</summary>
    public bool IsDependencyGroup => _project is null;

    /// <summary>체크박스를 보여주는지(체크할 수 있는 대화가 하나라도 있는 프로젝트).</summary>
    public bool HasCheckbox => !IsDependencyGroup && Conversations.Any(c => c.IsCheckable || c.IsChecked);

    /// <summary>3상태 체크. <c>true</c> 전체, <c>false</c> 없음, <c>null</c> 일부(체크 가능한 대화 기준).</summary>
    public bool? IsChecked
    {
        get
        {
            List<ImportConversationNodeViewModel> checkable = Conversations.Where(c => c.IsCheckable).ToList();
            if (checkable.Count == 0)
            {
                return Conversations.Any(c => c.IsChecked) ? true : false;
            }

            int included = checkable.Count(c => c.Result.IsIncludedByUser);
            return included == 0 ? false : included == checkable.Count ? true : null;
        }
        set
        {
            if (IsDependencyGroup)
            {
                return;
            }

            // WPF 3상태 체크박스는 클릭 때 null을 거친다. "전체가 아니면 전체 선택, 전체면 해제"로 정규화한다.
            bool include = IsChecked != true;
            _owner.SetIncluded(Conversations.Where(c => c.IsCheckable).Select(c => c.ThreadId), include);
        }
    }

    /// <summary>"N개 중 M개"(M = 이번에 가져오기에 들어가는 대화 수).</summary>
    public string CountText => $"{Conversations.Count}개 중 {Conversations.Count(c => c.IsChecked)}";

    /// <summary>목적지. 조상 그룹은 <c>null</c>.</summary>
    public ProjectTarget? Target => _project?.Target;

    /// <summary>원본 경로(백업).</summary>
    public string OriginalPathText => _project is { } p && p.Preview.PathMapping.OriginalRootPaths.Count > 0
        ? string.Join(Environment.NewLine, p.Preview.PathMapping.OriginalRootPaths)
        : "없음";

    /// <summary>이 PC 경로(목적지 폴더). 없으면 "지정 안 함".</summary>
    public string LocalPathText => Target?.FolderPath ?? "지정 안 함";

    /// <summary>작업 폴더 상태 문구(ProjectTarget.Reason 기준).</summary>
    public string TargetStatusText => _project is { } p
        ? ImportTexts.TargetStatus(p.Target, _owner.LocalProjects, p.Preview.PathMapping.OriginalRootPaths)
        : "선택한 대화에 필요한 원본 대화입니다. 기타 대화로 함께 들어갑니다.";

    /// <summary>등록 프로젝트에 연결되는지(초록 표시).</summary>
    public bool IsLinked => Target is { } t && ImportTexts.IsLinked(t);

    /// <summary>결정 오류(예: 없는 폴더). 없으면 <c>null</c>.</summary>
    public string? DecisionError => _project?.Resolution.Error;

    /// <summary>작업 폴더를 바꿀 수 있는 프로젝트인지("기타 대화" 그룹과 조상 그룹은 불가).</summary>
    public bool IsFolderEditable => _project is { } p && p.Preview.PathMapping.CanManuallyOverride;

    /// <summary>사용자가 폴더를 직접 골랐는지([원래대로] 표시).</summary>
    public bool IsFolderUserSelected => _project is { } p && _owner.IsFolderDecision(p.ProjectKey);

    /// <summary>폴더 버튼 문구([폴더 선택…] / [다른 폴더…]).</summary>
    public string ChooseFolderText => Target?.Reason == ProjectTargetReason.OriginalRootMissing ? "폴더 선택…" : "다른 폴더…";

    /// <summary>[새로고침] 안내가 필요한 상태인지(미등록 폴더).</summary>
    public bool ShowRefreshHint => Target?.Reason is ProjectTargetReason.UserSelectedUnregistered or ProjectTargetReason.OriginalRootExistsUnregistered;

    private bool CanChangeFolder => IsFolderEditable && _owner.CanEditSelection;

    /// <summary>[다른 폴더…] / [폴더 선택…].</summary>
    public RelayCommand ChooseFolderCommand { get; }

    /// <summary>[원래대로].</summary>
    public RelayCommand ResetFolderCommand { get; }

    /// <summary>검색 필터로 보이는지.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    /// <summary>트리에서 선택(상세 패널 대상)됐는지.</summary>
    public bool IsTreeSelected
    {
        get => _isTreeSelected;
        set => SetProperty(ref _isTreeSelected, value);
    }

    /// <summary>상세 패널의 프로젝트 요약.</summary>
    public string DetailSummaryText => IsDependencyGroup
        ? $"필요한 원본 대화 {Conversations.Count(c => c.IsChecked)}개가 자동으로 함께 들어갑니다."
        : $"대화 {Conversations.Count}개 · 이번에 가져오기 {Conversations.Count(c => c.IsChecked && c.Result.FinalAction is ImportPlannedAction.Import or ImportPlannedAction.Update)}개";

    internal void Update(ImportSelectionProject? project)
    {
        _project = project;
        OnPropertyChanged(string.Empty); // 표시 속성 전부 다시 읽는다(값을 저장하지 않으므로 비용이 작다).
        ChooseFolderCommand.RaiseCanExecuteChanged();
        ResetFolderCommand.RaiseCanExecuteChanged();
    }
}
