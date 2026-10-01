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

    /// <summary>원본 작업 폴더(백업 manifest 원문). 없으면 <c>null</c>. 화면 본문은 <see cref="OriginalCwdText"/>, 원문은 툴팁이다.</summary>
    public string? OriginalCwd { get; }

    /// <summary>(Phase 9_2-30) 원본 작업 폴더의 사람이 읽는 표기(<c>\?\</c> 없음). 없으면 <c>null</c>.</summary>
    public string? OriginalCwdText => OriginalCwd is { } cwd ? ImportTexts.DisplayPath(cwd) : null;

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

    /// <summary>
    /// (Phase 9_2-26) 트리 행 안의 "이 PC 위치: …" 작은 줄. 이 PC에 이미 있는 대화(새 대화가 아닌 경우)에만 있다.
    /// </summary>
    public string? RowLocationText => ImportTexts.RowLocation(_result.Preview);

    /// <summary>(Phase 9_2-23) 화면 읽기 프로그램·UI 자동화 이름: "제목, 배지[, 이 PC 위치]".</summary>
    public string AutomationName => RowLocationText is { } location
        ? $"{Title}, {BadgeText}, {location}"
        : $"{Title}, {BadgeText}";

    /// <summary>
    /// 상세의 "이 PC 위치: …" 한 줄. 이 PC에 없으면 <c>null</c>. (Phase 9_2-30) 있음/같음 같은 상태 설명은 바로 윗줄
    /// <see cref="StatusSentence"/>가 이미 하므로 되풀이하지 않는다.
    /// </summary>
    public string? PresenceText => LocalLocationText is { } location ? "이 PC 위치: " + location : null;

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
        OnPropertyChanged(nameof(AutomationName));
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
        CreateFolderCommand = new RelayCommand(() => _owner.CreateNewFolder(this), () => CanChangeFolder && _owner.CanCreateFolders);
    }

    /// <summary>표시 이름.</summary>
    public string DisplayName { get; }

    /// <summary>(Phase 9_2-23) 화면 읽기 프로그램·UI 자동화 이름(프로젝트 이름).</summary>
    public string AutomationName => DisplayName;

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

    /// <summary>원본 경로(백업). (Phase 9_2-30) 사람이 읽는 표기이고, 원문은 <see cref="OriginalPathRaw"/>(툴팁)다.</summary>
    public string OriginalPathText => _project is { } p && p.Preview.PathMapping.OriginalRootPaths.Count > 0
        ? string.Join(Environment.NewLine, p.Preview.PathMapping.OriginalRootPaths.Select(ImportTexts.DisplayPath))
        : "없음";

    /// <summary>원본 경로 원문(백업 manifest 그대로, 툴팁용). 없으면 <c>null</c>.</summary>
    public string? OriginalPathRaw => _project is { } p && p.Preview.PathMapping.OriginalRootPaths.Count > 0
        ? string.Join(Environment.NewLine, p.Preview.PathMapping.OriginalRootPaths)
        : null;

    /// <summary>이 PC 경로(목적지 폴더, 사람이 읽는 표기). 없으면 "지정 안 함".</summary>
    public string LocalPathText => Target?.FolderPath is { } folder ? ImportTexts.DisplayPath(folder) : "지정 안 함";

    /// <summary>이 PC 경로 원문(툴팁용). 없으면 <c>null</c>.</summary>
    public string? LocalPathRaw => Target?.FolderPath;

    /// <summary>작업 폴더 상태 문구(ProjectTarget.Reason 기준).</summary>
    public string TargetStatusText => _project is { } p
        ? ImportTexts.TargetStatus(p.Target, _owner.LocalProjects, p.Preview.PathMapping.OriginalRootPaths, IsCreationDeclined)
        : "선택한 대화에 필요한 원본 대화입니다. 기타 대화로 함께 들어갑니다.";

    /// <summary>(Phase 9_5-05) 목적지가 새 프로젝트 만들기인지(이름 칸을 보여준다).</summary>
    public bool IsCreatingProject => Target?.Kind == ProjectTargetKind.CreateNew;

    /// <summary>
    /// (Phase 9_5-05) 이 폴더로 새 프로젝트를 만들 수 있는 상태인지(지금 만들기로 되어 있거나, 사용자가 "만들지 않기"를 골랐거나).
    /// "새 프로젝트를 만들지 않고 기타 대화로 가져오기" 선택을 보여준다.
    /// </summary>
    public bool IsCreationOffered => _project is { } p && _owner.IsCreationOffered(p.ProjectKey);

    /// <summary>(Phase 9_5-05) 사용자가 "새 프로젝트를 만들지 않고 기타 대화로"를 골랐는지.</summary>
    public bool IsCreationDeclined => _project is { } p && !_owner.DecisionFor(p.ProjectKey).CreateProject && IsCreationOffered;

    /// <summary>"새 프로젝트를 만들지 않고 기타 대화로 가져오기" 체크(양방향).</summary>
    public bool DeclineCreation
    {
        get => IsCreationDeclined;
        set
        {
            if (_project is { } p && value != IsCreationDeclined)
            {
                _owner.SetCreateProject(p.ProjectKey, !value);
            }
        }
    }

    /// <summary>
    /// (Phase 9_5-05) 새 프로젝트 이름 칸(양방향). 입력 중인 값(빈 값 포함)을 그대로 보여주고, 입력이 없으면 기본값(폴더 이름)이다.
    /// 빈 이름은 <see cref="DecisionError"/>로 드러나고 가져오기가 꺼진다.
    /// </summary>
    public string NewProjectName
    {
        get => _project is { } p
            ? _owner.DecisionFor(p.ProjectKey).NewProjectName ?? Target?.NewProjectName ?? string.Empty
            : string.Empty;
        set
        {
            if (_project is { } p && !string.Equals(value, NewProjectName, StringComparison.Ordinal))
            {
                _owner.SetNewProjectName(p.ProjectKey, value ?? string.Empty);
            }
        }
    }

    /// <summary>등록 프로젝트에 연결되는지(초록 표시).</summary>
    public bool IsLinked => Target is { } t && ImportTexts.IsLinked(t);

    /// <summary>결정 오류(예: 없는 폴더). 없으면 <c>null</c>.</summary>
    public string? DecisionError => _project?.Resolution.Error;

    /// <summary>
    /// 작업 폴더를 바꿀 수 있는 프로젝트인지. 조상 그룹만 불가다(Phase 9_5-05 — 백업의 "기타 대화" 그룹에도 폴더를 지정할 수 있다).
    /// </summary>
    public bool IsFolderEditable => _project is not null;

    /// <summary>사용자가 폴더를 직접 골랐는지([원래대로] 표시).</summary>
    public bool IsFolderUserSelected => _project is { } p && _owner.IsFolderDecision(p.ProjectKey);

    /// <summary>폴더 버튼 문구([폴더 선택…] / [다른 폴더…]).</summary>
    public string ChooseFolderText => Target?.Reason is ProjectTargetReason.OriginalRootMissing or ProjectTargetReason.NotApplicable
        ? "폴더 선택…"
        : "다른 폴더…";

    /// <summary>
    /// [새로고침] 안내가 필요한 상태인지: 미등록 폴더인데 새 프로젝트를 만들 수 없을 때만(기능 스위치 꺼짐, 9_2까지의 A안).
    /// 새 프로젝트를 만들 수 있으면(또는 사용자가 만들지 않기로 골랐으면) 보여주지 않는다.
    /// </summary>
    public bool ShowRefreshHint => Target is { Kind: ProjectTargetKind.Uncategorized } t &&
                                   t.Reason is ProjectTargetReason.UserSelectedUnregistered or ProjectTargetReason.OriginalRootExistsUnregistered &&
                                   !IsCreationOffered;

    private bool CanChangeFolder => IsFolderEditable && _owner.CanEditSelection;

    /// <summary>[다른 폴더…] / [폴더 선택…].</summary>
    public RelayCommand ChooseFolderCommand { get; }

    /// <summary>[원래대로].</summary>
    public RelayCommand ResetFolderCommand { get; }

    /// <summary>(Phase 9_5-07) [새 폴더 만들기].</summary>
    public RelayCommand CreateFolderCommand { get; }

    /// <summary>(Phase 9_5-07) [새 폴더 만들기] 버튼을 보여주는지(기능이 켜져 있고 폴더를 바꿀 수 있는 행).</summary>
    public bool ShowCreateFolder => IsFolderEditable && _owner.CanCreateFolders;

    /// <summary>(Phase 9_5-07) [새 폴더 만들기] 실패 안내(없으면 <c>null</c>).</summary>
    public string? FolderCreateError => _project is { } p ? _owner.FolderCreateErrorFor(p.ProjectKey) : null;

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
        CreateFolderCommand.RaiseCanExecuteChanged();
    }
}
