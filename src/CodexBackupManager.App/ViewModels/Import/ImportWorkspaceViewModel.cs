using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>가져오기 화면 상태(설계 §7.1).</summary>
public enum ImportWorkspaceState
{
    /// <summary>닫힘.</summary>
    Closed,

    /// <summary>백업 파일 선택 중.</summary>
    Opening,

    /// <summary>Codex가 실행 중이라 분석을 시작하지 않고 종료를 기다린다.</summary>
    WaitingForCodexExit,

    /// <summary>백업 검증·비교 중.</summary>
    Analyzing,

    /// <summary>분석 실패.</summary>
    Failed,

    /// <summary>선택·폴더 지정.</summary>
    Editing,

    /// <summary>확인 대화상자.</summary>
    Confirming,

    /// <summary>적용 중.</summary>
    Applying,

    /// <summary>결과.</summary>
    Result,
}

/// <summary>가져오기 화면을 닫을 때 메인에 넘기는 정보.</summary>
/// <param name="CodexDataChanged">적용이 실제로 무언가를 썼는지(메인 목록을 새로 읽어야 하는지).</param>
/// <param name="ShowInList">[목록에서 보기]로 닫았는지.</param>
/// <param name="ThreadIdsToHighlight">목록에서 선택·강조할 대화.</param>
public sealed record ImportWorkspaceClosedEventArgs(bool CodexDataChanged, bool ShowInList, IReadOnlyList<string> ThreadIdsToHighlight);

/// <summary>
/// 가져오기 작업 공간(Phase 9_2-2, 설계 §7). 백업을 트리(체크 + 배지 + 프로젝트별 작업 폴더)로 보여주고, 고른 대화만 가져온다.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>판단은 Core에 있다. 선택 요약은 <see cref="ImportSelection.Compute"/>, 폴더 판정은 <see cref="ImportSelection.ResolveTarget"/>,
///     Plan은 [가져오기]를 누를 때 한 번만 <see cref="ImportPlanBuilder.Build(ImportPreview,string,ImportUserChoices,CancellationToken)"/>로 만든다.</item>
///   <item>체크·폴더 상태의 원천은 <see cref="ImportUserChoices"/> 하나(<see cref="Choices"/>)다. 노드는 계산 결과를 보여줄 뿐이다.</item>
///   <item>Codex가 실행 중이면 분석을 시작하지 않는다(CLAUDE.md §19, 설계 §7.1 B안). 편집 중에는 창 활성화와 5초 주기로 확인한다.
///     최종 안전장치는 여전히 <see cref="RestoreExecutor"/>의 이중 확인이다.</item>
///   <item>로그에는 개수와 enum만 남긴다(경로·제목·원문 없음).</item>
/// </list>
/// </remarks>
public sealed class ImportWorkspaceViewModel : ObservableObject, IDisposable
{
    /// <summary>편집 중 Codex 실행 여부 확인 주기(설계 §7.1).</summary>
    public static readonly TimeSpan CodexPollInterval = TimeSpan.FromSeconds(5);

    private readonly FileLogger _logger;
    private readonly Func<string?> _importFilePicker;
    private readonly Func<string?> _projectPathPicker;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string> _snapshotRootProvider;
    private readonly Func<string?> _codexHomeProvider;
    private readonly Func<bool> _hasIncompleteApply;

    private ImportWorkspaceState _state = ImportWorkspaceState.Closed;
    private string? _backupFilePath;
    private ImportPreview? _preview;
    private ImportUserChoices? _choices;
    private ImportSelectionSummary? _summary;
    private Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImportConversationNodeViewModel> _conversationNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImportProjectNodeViewModel> _projectNodes = new(StringComparer.Ordinal);
    private ImportProjectNodeViewModel? _dependencyGroup;

    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _applyCancellation;
    private IDisposable? _pollTimer;
    private bool _isCodexRunning;
    private bool _isAnalysisStale;
    private string _searchText = string.Empty;
    private object? _selectedNode;
    private string? _message;
    private string? _messageHint;
    private string? _editingError;
    private string? _applyStatusText;
    private string? _codexRecheckText;
    private bool _failureRetryable;
    private ImportResultViewModel? _result;
    private ImportPlan? _appliedPlan;

    // Phase 9_2b — 대화 내용 미리보기. 백업 파일마다 한 번만 열고(ImportConversationPreviewer) 화면을 닫거나 다른 파일을 열 때 닫는다.
    private ImportConversationPreviewer? _previewer;
    private string? _previewerKey;
    private CancellationTokenSource? _contentCancellation;
    private bool _isContentLoading;
    private string? _contentNotice;

    // Phase 9_5-07 ~ 09 — [새 폴더 만들기]. 이 화면에서 앱이 만든 폴더만 기억하고(기준 폴더는 넣지 않는다), 쓰이지 않으면 비어 있을 때만 지운다.
    private readonly NewProjectFolderOptions? _newFolders;
    private readonly List<string> _createdFolders = [];
    private readonly Dictionary<string, string> _createdFolderByProjectKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folderCreateErrors = new(StringComparer.Ordinal);
    private string? _newFolderBase;
    private string? _newFolderBaseError;

    /// <summary>생성자.</summary>
    /// <param name="logger">로거.</param>
    /// <param name="importFilePicker">백업 파일 선택(취소 시 <c>null</c>).</param>
    /// <param name="projectPathPicker">작업 폴더 선택(취소 시 <c>null</c>).</param>
    /// <param name="confirmDialog">(메시지, 제목) → 확인 여부.</param>
    /// <param name="snapshotRootProvider">Snapshot 루트.</param>
    /// <param name="codexHomeProvider">현재 Codex Home 경로(없으면 <c>null</c>).</param>
    /// <param name="hasIncompleteApply">완료되지 못한 이전 적용이 있는지(있으면 가져오기를 막는다).</param>
    /// <param name="newProjectFolders">(Phase 9_5-07) [새 폴더 만들기]의 기준 폴더·생성기. <c>null</c>이면 그 기능을 끈다.</param>
    public ImportWorkspaceViewModel(
        FileLogger logger,
        Func<string?> importFilePicker,
        Func<string?> projectPathPicker,
        Func<string, string, bool> confirmDialog,
        Func<string> snapshotRootProvider,
        Func<string?> codexHomeProvider,
        Func<bool> hasIncompleteApply,
        NewProjectFolderOptions? newProjectFolders = null)
    {
        _newFolders = newProjectFolders;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _importFilePicker = importFilePicker ?? throw new ArgumentNullException(nameof(importFilePicker));
        _projectPathPicker = projectPathPicker ?? throw new ArgumentNullException(nameof(projectPathPicker));
        _confirmDialog = confirmDialog ?? throw new ArgumentNullException(nameof(confirmDialog));
        _snapshotRootProvider = snapshotRootProvider ?? throw new ArgumentNullException(nameof(snapshotRootProvider));
        _codexHomeProvider = codexHomeProvider ?? throw new ArgumentNullException(nameof(codexHomeProvider));
        _hasIncompleteApply = hasIncompleteApply ?? throw new ArgumentNullException(nameof(hasIncompleteApply));

        // Phase 9_2-34 — 종료 대기 화면에서는 가운데 [다시 확인]과 우측 상단 [다시 분석]이 같은 메서드(ReanalyzeAsync)를 부른다.
        RecheckCodexCommand = new RelayCommand(() => _ = ReanalyzeAsync(), () => State == ImportWorkspaceState.WaitingForCodexExit);
        CancelAnalysisCommand = new RelayCommand(() => _analysisCancellation?.Cancel(), () => State == ImportWorkspaceState.Analyzing);
        ChooseOtherFileCommand = new RelayCommand(() => _ = OpenAsync(), () => State is ImportWorkspaceState.Failed);
        CloseCommand = new RelayCommand(() => Close(showInList: false), () => State is not (ImportWorkspaceState.Applying or ImportWorkspaceState.Analyzing or ImportWorkspaceState.Confirming or ImportWorkspaceState.Closed));
        SelectNewOnlyCommand = new RelayCommand(SelectNewOnly, () => CanEditSelection);
        SelectAllCommand = new RelayCommand(SelectAll, () => CanEditSelection);
        ClearAllCommand = new RelayCommand(ClearAll, () => CanEditSelection);
        ReanalyzeCommand = new RelayCommand(
            () => _ = ReanalyzeAsync(),
            () => State is ImportWorkspaceState.Editing or ImportWorkspaceState.Result or ImportWorkspaceState.WaitingForCodexExit || CanRetryAnalysis);
        ImportCommand = new RelayCommand(() => _ = ImportAsync(), () => CanImport);
        CancelApplyCommand = new RelayCommand(() => _applyCancellation?.Cancel(), () => State == ImportWorkspaceState.Applying);
        RetryCommand = new RelayCommand(() => _ = ReanalyzeAsync(), () => CanRetry);
        ShowInListCommand = new RelayCommand(() => Close(showInList: true), () => State == ImportWorkspaceState.Result && ImportedThreadIds.Count > 0);
        ChangeNewFolderBaseCommand = new RelayCommand(ChangeNewFolderBase, () => CanCreateFolders && CanEditSelection);
    }

    // ── 테스트용 seam(InternalsVisibleTo App.Tests). 제품 기본값은 실제 동작이다. ──────────────────

    /// <summary>Codex 실행 여부 판정에 쓰는 프로세스 목록(기본: 실제 목록 + <see cref="CodexProcessGuard"/>).</summary>
    internal CodexProcessGuard.RunningProcessLister ProcessLister { get; set; } = CodexProcessGuard.SystemRunningProcessLister;

    /// <summary>분석 시점의 fresh 로컬 카탈로그 생성기(기본: 탐지 + <see cref="CodexCatalogBuilder"/>).</summary>
    internal Func<string, CancellationToken, CodexCatalog> CatalogBuilder { get; set; } = DefaultCatalogBuilder;

    /// <summary>Plan 생성기(기본: 선택 오버로드). 테스트가 호출 횟수를 셀 수 있게 둔다.</summary>
    internal Func<ImportPreview, string, ImportUserChoices, ImportPlan?> PlanBuilder { get; set; }
        = static (preview, path, choices) => ImportPlanBuilder.Build(preview, path, choices);

    /// <summary>Restore 진입점(기본: production <see cref="RestoreExecutor.Apply(ImportPlan,string,string?,IRestoreFaultInjectionHook?,Action{string}?,CancellationToken)"/>).</summary>
    internal Func<ImportPlan, string, string, Action<string>, CancellationToken, RestoreResult> RestoreApply { get; set; }
        = static (plan, home, snapshotRoot, onStatus, token) => RestoreExecutor.Apply(
            plan, home, snapshotRoot: snapshotRoot, onStatusChanged: onStatus, cancellationToken: token);

    /// <summary>편집 중 Codex 감시 타이머 생성기(주기, 콜백) → 해제 핸들. 기본: UI 컨텍스트로 넘기는 스레드 타이머.</summary>
    internal Func<TimeSpan, Action, IDisposable> PollTimerFactory { get; set; } = DefaultPollTimer;

    /// <summary>미리보기 생성기 팩터리(테스트가 열기 횟수·캐시를 확인할 수 있게 둔다). 기본: 백업 파일을 공유 읽기로 여는 미리보기.</summary>
    internal Func<string, ImportConversationPreviewer> PreviewerFactory { get; set; } = static path => new ImportConversationPreviewer(path);

    /// <summary>현재 미리보기 생성기(테스트 확인용). 편집 화면이 아니면 <c>null</c>일 수 있다.</summary>
    internal ImportConversationPreviewer? Previewer => _previewer;

    /// <summary>마지막으로 시작한 대화 내용 불러오기 작업(테스트가 기다릴 수 있게 둔다).</summary>
    internal Task? ContentLoadTask { get; private set; }

    /// <summary>이 화면이 Plan을 만든 횟수(테스트 확인용 — 체크/폴더 변경으로는 늘지 않아야 한다).</summary>
    internal int PlanBuildCount { get; private set; }

    /// <summary>분석을 시작한 횟수(테스트 확인용 — Codex 실행 중에는 0이어야 한다).</summary>
    internal int AnalysisStartCount { get; private set; }

    /// <summary>마지막으로 적용에 넘긴 Plan(테스트 확인용).</summary>
    internal ImportPlan? LastAppliedPlan => _appliedPlan;

    /// <summary>현재 Preview(테스트 확인용).</summary>
    internal ImportPreview? CurrentPreview => _preview;

    /// <summary>이 PC의 지금 시각(9_2-36 확인 시각 표시). 테스트가 고정할 수 있게 둔다.</summary>
    internal Func<DateTime> Now { get; set; } = static () => DateTime.Now;

    /// <summary>이 화면에서 앱이 만든 폴더 중 아직 남아 있는 것(테스트 확인용).</summary>
    internal IReadOnlyList<string> CreatedFolders => _createdFolders;

    // ── 상태 ─────────────────────────────────────────────────────────────

    /// <summary>화면이 닫힐 때(메인이 새로고침·강조를 한다).</summary>
    public event EventHandler<ImportWorkspaceClosedEventArgs>? Closed;

    /// <summary>현재 상태.</summary>
    public ImportWorkspaceState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                foreach (string name in new[]
                {
                    nameof(IsOpen), nameof(IsWaitingForCodexExit), nameof(IsAnalyzing), nameof(IsFailed),
                    nameof(IsEditorVisible), nameof(IsApplying), nameof(IsResult), nameof(CanEditSelection), nameof(CanRetryAnalysis),
                })
                {
                    OnPropertyChanged(name);
                }

                RaiseCommands();
                foreach (ImportConversationNodeViewModel node in _conversationNodes.Values)
                {
                    node.RaiseEditability(); // Phase 9_3-01 — 옮기기 체크는 편집 중에만 바꿀 수 있다
                }

                _logger.Info($"가져오기 화면 상태={value}");
            }
        }
    }

    /// <summary>화면이 열려 있는지.</summary>
    public bool IsOpen => State != ImportWorkspaceState.Closed;

    /// <summary>Codex 종료 대기 화면.</summary>
    public bool IsWaitingForCodexExit => State == ImportWorkspaceState.WaitingForCodexExit;

    /// <summary>분석 중 화면.</summary>
    public bool IsAnalyzing => State == ImportWorkspaceState.Analyzing;

    /// <summary>분석 실패 화면.</summary>
    public bool IsFailed => State == ImportWorkspaceState.Failed;

    /// <summary>편집 화면(트리 + 상세 + 요약)이 보이는지(확인 대화상자 중에도 보인다).</summary>
    public bool IsEditorVisible => State is ImportWorkspaceState.Editing or ImportWorkspaceState.Confirming;

    /// <summary>적용 중 화면.</summary>
    public bool IsApplying => State == ImportWorkspaceState.Applying;

    /// <summary>결과 화면.</summary>
    public bool IsResult => State == ImportWorkspaceState.Result;

    /// <summary>체크·폴더를 바꿀 수 있는지(편집 중이고 분석이 낡지 않았을 때).</summary>
    public bool CanEditSelection => State == ImportWorkspaceState.Editing;

    /// <summary>안내/실패 문구(대기·분석·실패 화면).</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>해결 방법 문구.</summary>
    public string? MessageHint
    {
        get => _messageHint;
        private set => SetProperty(ref _messageHint, value);
    }

    /// <summary>편집 화면의 오류 한 줄(예: Plan을 만들 수 없음, 폴더 선택 실패).</summary>
    public string? EditingError
    {
        get => _editingError;
        private set => SetProperty(ref _editingError, value);
    }

    /// <summary>적용 진행 문구.</summary>
    public string? ApplyStatusText
    {
        get => _applyStatusText;
        private set => SetProperty(ref _applyStatusText, value);
    }

    /// <summary>편집 중 Codex가 켜져 있는지(배너).</summary>
    public bool IsCodexRunning
    {
        get => _isCodexRunning;
        private set
        {
            if (SetProperty(ref _isCodexRunning, value))
            {
                OnPropertyChanged(nameof(CodexBannerText));
                OnPropertyChanged(nameof(HasCodexBanner));
                RaiseCommands();
            }
        }
    }

    /// <summary>Codex가 편집 도중 켜진 적이 있어 분석을 다시 해야 하는지.</summary>
    public bool IsAnalysisStale
    {
        get => _isAnalysisStale;
        private set
        {
            if (SetProperty(ref _isAnalysisStale, value))
            {
                OnPropertyChanged(nameof(CodexBannerText));
                OnPropertyChanged(nameof(HasCodexBanner));
                RaiseCommands();
            }
        }
    }

    /// <summary>상단 배너를 보여주는지.</summary>
    public bool HasCodexBanner => IsCodexRunning || IsAnalysisStale;

    /// <summary>상단 배너 문구.</summary>
    public string? CodexBannerText => IsCodexRunning
        ? ImportTexts.CodexStartedWhileEditing
        : IsAnalysisStale ? ImportTexts.AnalysisStale : null;

    // ── 백업 정보 / 트리 ───────────────────────────────────────────────────

    /// <summary>헤더 한 줄(파일 이름 · 만든 시각 · 대화 수 · 앱 버전).</summary>
    public string? BackupHeaderText { get; private set; }

    /// <summary>"상세 정보 ▼" 안의 개발용 정보(검증 결과, 포맷 버전, Codex 버전, 경고).</summary>
    public ObservableCollection<InfoRow> DetailRows { get; } = [];

    /// <summary>트리의 프로젝트 노드(마지막에 조상 그룹이 있을 수 있다).</summary>
    public ObservableCollection<ImportProjectNodeViewModel> Projects { get; } = [];

    /// <summary>이 PC 프로젝트 목록(문구용).</summary>
    public ProjectDirectory LocalProjects => _preview?.LocalProjectDirectory ?? ProjectDirectory.Empty;

    /// <summary>현재 사용자 선택(체크·폴더의 유일한 원천). 분석 전에는 <c>null</c>.</summary>
    public ImportUserChoices? Choices => _choices;

    /// <summary>마지막 선택 요약.</summary>
    public ImportSelectionSummary? Summary => _summary;

    /// <summary>검색어(대화 제목 또는 프로젝트 이름 부분 일치). 표시만 바꾸고 선택은 유지한다.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>트리에서 고른 노드(대화 또는 프로젝트).</summary>
    public object? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(SelectedConversation));
                OnPropertyChanged(nameof(SelectedProject));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    /// <summary>상세 패널의 대화.</summary>
    public ImportConversationNodeViewModel? SelectedConversation => SelectedNode as ImportConversationNodeViewModel;

    /// <summary>상세 패널의 프로젝트.</summary>
    public ImportProjectNodeViewModel? SelectedProject => SelectedNode as ImportProjectNodeViewModel;

    /// <summary>아무것도 고르지 않았는지.</summary>
    public bool HasNoSelection => SelectedNode is null;

    /// <summary>대화 내용 미리보기 자리 표시(9_2b에서 채운다).</summary>
    public string ContentPreviewPlaceholder => "대화 내용 미리보기는 다음 버전에서 제공합니다.";

    /// <summary>하단 요약 첫 줄.</summary>
    public string? SummaryText => _summary is { } s ? ImportTexts.SummaryLine(s) : null;

    /// <summary>하단 요약 둘째 줄(안내 또는 적용 불가 사유).</summary>
    public string? SummaryHint
    {
        get
        {
            if (_hasIncompleteApply())
            {
                return "이전 복원 작업이 완료되지 않았습니다. 메인 화면에서 [이전 상태로 복구]를 먼저 해 주세요.";
            }

            return _summary is { } s ? ImportTexts.SummaryHint(s, _titles) : null;
        }
    }

    /// <summary>요약 둘째 줄이 적용 불가 사유인지(경고색).</summary>
    public bool IsSummaryHintWarning => _hasIncompleteApply() || _summary is { CanApply: false };

    /// <summary>[대화 N개 가져오기] 문구.</summary>
    public string ImportButtonText => _summary is { } s ? ImportTexts.ImportButton(s) : "가져오기";

    /// <summary>[가져오기]를 누를 수 있는지.</summary>
    public bool CanImport => State == ImportWorkspaceState.Editing && _summary is { CanApply: true } &&
                             !IsCodexRunning && !IsAnalysisStale && !_hasIncompleteApply();

    // ── 결과 ─────────────────────────────────────────────────────────────

    /// <summary>결과 화면(Phase 9_2-29a — <see cref="ImportResultViewModel"/>로 분리). 결과가 없으면 <c>null</c>.</summary>
    public ImportResultViewModel? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
            {
                OnPropertyChanged(nameof(ImportedThreadIds));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    /// <summary>목록에서 강조할 대화(결과 화면 기준).</summary>
    public IReadOnlyList<string> ImportedThreadIds => _result?.ImportedThreadIds ?? [];

    /// <summary>
    /// (Phase 9_2-36) 종료 대기 화면에서 다시 확인했는데 Codex가 아직 실행 중일 때 "아직 Codex가 실행 중입니다(확인 HH:mm:ss)"(이 PC 시각).
    /// 처음 대기 화면에 들어왔을 때와 대기 화면이 아닐 때는 <c>null</c>.
    /// </summary>
    public string? CodexRecheckText
    {
        get => _codexRecheckText;
        private set => SetProperty(ref _codexRecheckText, value);
    }

    /// <summary>
    /// (Phase 9_2-37) 실패 화면에서 [다시 분석]을 보여주는지. 다시 시도하면 될 수 있는 실패(Codex Home 확인 불가, 분석 중 예외)만 참이다.
    /// 백업 파일 자체의 문제(손상, 체크섬 불일치, 지원하지 않는 형식)는 거짓이다.
    /// </summary>
    public bool CanRetryAnalysis => State == ImportWorkspaceState.Failed && _failureRetryable;

    /// <summary>[다시 시도]를 보여주는지.</summary>
    public bool CanRetry => State == ImportWorkspaceState.Result && _result is { IsRetryable: true };

    // ── 대화 내용 미리보기(9_2b) ────────────────────────────────────────────────

    /// <summary>오른쪽 상세의 대화 내용(메인 Viewer와 같은 표시용 메시지).</summary>
    public ObservableCollection<ConversationMessageViewModel> ContentMessages { get; } = [];

    /// <summary>대화 내용을 불러오는 중인지.</summary>
    public bool IsContentLoading
    {
        get => _isContentLoading;
        private set => SetProperty(ref _isContentLoading, value);
    }

    /// <summary>대화 내용 안내 한 줄(일부만 보임, 읽을 수 없음 등). 없으면 <c>null</c>.</summary>
    public string? ContentNotice
    {
        get => _contentNotice;
        private set => SetProperty(ref _contentNotice, value);
    }

    // ── 명령 ─────────────────────────────────────────────────────────────

    /// <summary>[다시 확인](Codex 종료 대기).</summary>
    public RelayCommand RecheckCodexCommand { get; }

    /// <summary>분석 [취소].</summary>
    public RelayCommand CancelAnalysisCommand { get; }

    /// <summary>[다른 파일 선택](실패 화면).</summary>
    public RelayCommand ChooseOtherFileCommand { get; }

    /// <summary>[닫기] / [← 목록으로].</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>[새 대화만].</summary>
    public RelayCommand SelectNewOnlyCommand { get; }

    /// <summary>[전체 선택].</summary>
    public RelayCommand SelectAllCommand { get; }

    /// <summary>[모두 해제].</summary>
    public RelayCommand ClearAllCommand { get; }

    /// <summary>[다시 분석] / [새로고침] — 선택과 폴더를 유지하고 분석만 다시 한다.</summary>
    public RelayCommand ReanalyzeCommand { get; }

    /// <summary>[대화 N개 가져오기].</summary>
    public RelayCommand ImportCommand { get; }

    /// <summary>[적용 취소].</summary>
    public RelayCommand CancelApplyCommand { get; }

    /// <summary>[다시 시도](결과 화면) — 다시 분석해 선택을 유지한 채 편집으로 돌아간다(Plan은 다시 만든다).</summary>
    public RelayCommand RetryCommand { get; }

    /// <summary>[목록에서 보기].</summary>
    public RelayCommand ShowInListCommand { get; }

    // ── 열기 / 분석 ─────────────────────────────────────────────────────────

    /// <summary>[백업 가져오기]: 파일을 고르고, Codex가 꺼져 있으면 분석한다.</summary>
    public async Task OpenAsync()
    {
        // Phase 9_5-09 — 다른 백업 파일로 화면을 다시 시작하면, 이전 화면에서 만들고 쓰지 않은 빈 폴더를 정리한다.
        CleanupCreatedFolders(KeepFoldersUsedBySuccessfulApply());
        _newFolderBase = null;
        ResetAnalysis();
        State = ImportWorkspaceState.Opening;
        string? path = _importFilePicker();
        if (string.IsNullOrWhiteSpace(path))
        {
            Close(showInList: false);
            return;
        }

        _backupFilePath = path;
        _choices = null;
        await StartAnalysisIfCodexStoppedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 선택과 폴더 결정을 유지한 채 다시 분석한다([다시 분석], [새로고침], [다시 시도], 종료 대기 화면의 [다시 확인]).
    /// Codex가 아직 실행 중이면 종료 대기 화면에 남는다. 첫 열기에서 온 대기면 선택이 아직 없어(<see cref="Choices"/> = <c>null</c>) 기본 선택으로 시작한다.
    /// </summary>
    private async Task ReanalyzeAsync()
    {
        if (_backupFilePath is null)
        {
            return;
        }

        StopPolling();
        await StartAnalysisIfCodexStoppedAsync().ConfigureAwait(true);
    }

    private async Task StartAnalysisIfCodexStoppedAsync()
    {
        if (IsCodexRunningNow())
        {
            // Phase 9_2-36 — 이미 대기 화면에서 다시 확인한 것이면 확인했다는 사실을 시각으로 보여준다(화면 변화가 없어 반응이 없어 보이지 않게).
            CodexRecheckText = State == ImportWorkspaceState.WaitingForCodexExit
                ? ImportTexts.CodexStillRunning(Now())
                : null;
            Message = ImportTexts.CodexRunningAtStart;
            MessageHint = null;
            State = ImportWorkspaceState.WaitingForCodexExit;
            return;
        }

        CodexRecheckText = null;
        await AnalyzeAsync().ConfigureAwait(true);
    }

    private async Task AnalyzeAsync()
    {
        string path = _backupFilePath!;
        string? home = _codexHomeProvider();
        if (string.IsNullOrWhiteSpace(home))
        {
            Fail("Codex Home 경로를 확인할 수 없습니다.", "메인 화면에서 Codex 폴더를 먼저 선택해 주세요.", retryable: true);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        AnalysisStartCount++;
        _failureRetryable = false;
        Message = "백업을 확인하고 이 PC와 비교하는 중…";
        MessageHint = null;
        EditingError = null;
        State = ImportWorkspaceState.Analyzing;

        try
        {
            ImportPreview preview = await Task.Run(
                () =>
                {
                    CodexCatalog catalog = CatalogBuilder(home, cancellation.Token);
                    return ImportPreviewBuilder.Build(path, catalog, cancellation.Token);
                },
                cancellation.Token).ConfigureAwait(true);

            if (!ReferenceEquals(_analysisCancellation, cancellation) || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!preview.Success)
            {
                (string message, string hint) = ImportTexts.AnalysisFailure(preview.ValidationErrors);
                Fail(message, hint, retryable: false); // 백업 파일 자체의 문제 — 다시 해도 같다
                _logger.Warning($"가져오기 분석: 백업 검증 실패. errors={preview.ValidationErrors.Count}");
                return;
            }

            LoadPreview(preview);
            IsCodexRunning = false;
            IsAnalysisStale = false;
            State = ImportWorkspaceState.Editing;
            StartPolling();
            _logger.Info(
                $"가져오기 분석 완료. projects={preview.Projects.Count} conversations={preview.Projects.Sum(p => p.Conversations.Count)} " +
                $"dependencyOnly={preview.DependencyOnlyConversations.Count} warnings={preview.Warnings.Count}");
        }
        catch (OperationCanceledException)
        {
            Close(showInList: false);
        }
        catch (Exception ex)
        {
            _logger.Error("가져오기 분석 중 오류", ex);
            Fail($"백업을 확인하는 중 오류가 발생했습니다: {ex.GetType().Name}", "다른 파일을 선택하거나 [다시 분석]을 눌러 주세요.", retryable: true);
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancellation))
            {
                _analysisCancellation = null;
            }
        }
    }

    /// <param name="retryable">(Phase 9_2-37) 다시 시도하면 될 수 있는 실패인지([다시 분석]을 켠다).</param>
    private void Fail(string message, string hint, bool retryable)
    {
        Message = message;
        MessageHint = hint;
        _failureRetryable = retryable;
        State = ImportWorkspaceState.Failed;
        OnPropertyChanged(nameof(CanRetryAnalysis));
        RaiseCommands();
        _logger.Info($"가져오기 분석 실패. retryable={retryable}");
    }

    private void LoadPreview(ImportPreview preview)
    {
        string? previousSelection = SelectedConversation?.ThreadId;
        string? previousProjectKey = SelectedProject?.ProjectKey;
        _preview = preview;
        EnsurePreviewer(preview);
        BackupManifest manifest = preview.Manifest!;
        Dictionary<string, BackupConversationMetadata> metadata = manifest.Conversations
            .ToDictionary(c => c.ThreadId, StringComparer.OrdinalIgnoreCase);

        // 이전 선택 유지(다시 분석) 또는 기본 선택.
        _choices = _choices is null ? ImportUserChoices.CreateDefault(preview) : _choices;
        ImportSelectionSummary summary = ImportSelection.Compute(preview, _choices);
        _summary = summary;

        _titles = summary.Conversations
            .GroupBy(c => c.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ImportTexts.TitleOf(g.First().Preview), StringComparer.OrdinalIgnoreCase);

        _conversationNodes.Clear();
        _projectNodes.Clear();
        Projects.Clear();
        SelectedNode = null;

        foreach (ImportSelectionProject project in summary.Projects)
        {
            var conversations = project.Preview.Conversations
                .Select(c => CreateConversationNode(summary, c.ThreadId, metadata))
                .ToList();
            var node = new ImportProjectNodeViewModel(this, project, project.Preview.DisplayName, conversations);
            _projectNodes.TryAdd(project.ProjectKey, node);
            Projects.Add(node);
        }

        var dependencies = preview.DependencyOnlyConversations
            .Select(c => CreateConversationNode(summary, c.ThreadId, metadata))
            .ToList();
        _dependencyGroup = dependencies.Count > 0
            ? new ImportProjectNodeViewModel(this, null, "필요한 원본 대화(자동 포함)", dependencies)
            : null;
        if (_dependencyGroup is not null)
        {
            Projects.Add(_dependencyGroup);
        }

        BackupHeaderText =
            $"{Path.GetFileName(_backupFilePath)} · {manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · " +
            $"대화 {manifest.ConversationCount}개 · {ImportTexts.BackupAppVersion(manifest.AppVersion)}";
        OnPropertyChanged(nameof(BackupHeaderText));

        DetailRows.Clear();
        DetailRows.Add(new InfoRow("백업 검증", "통과(체크섬 일치)"));
        DetailRows.Add(new InfoRow("백업 형식 버전", manifest.BackupFormatVersion.ToString(CultureInfo.InvariantCulture)));
        DetailRows.Add(new InfoRow("만든 앱 버전", manifest.AppVersion));
        DetailRows.Add(new InfoRow("원본 Codex Desktop", manifest.SourceCodexDesktopVersion ?? "확인 불가"));
        DetailRows.Add(new InfoRow("원본 Codex CLI", manifest.SourceCodexCliVersion ?? "확인 불가"));
        DetailRows.Add(new InfoRow("선택 대화 / 원본 대화", $"{manifest.ConversationCount} / {manifest.DependencyConversationCount}"));
        DetailRows.Add(new InfoRow("백업 파일 SHA-256", preview.SourceBackupIdentity?.BackupFileSha256 ?? "확인 불가"));
        foreach (string warning in preview.Warnings)
        {
            DetailRows.Add(new InfoRow("경고", warning));
        }

        OnPropertyChanged(nameof(LocalProjects));
        OnPropertyChanged(nameof(Choices));
        ApplyFilter();
        RefreshSummaryBindings();
        AutoSelect(previousSelection, previousProjectKey);
    }

    /// <summary>
    /// Phase 9_2-27 — 편집 화면에 들어오면 오른쪽 상세가 비지 않게 한다: 이전 선택(다시 분석) → 첫 대화 → 첫 프로젝트 순.
    /// </summary>
    private void AutoSelect(string? previousThreadId, string? previousProjectKey)
    {
        object? target =
            (previousThreadId is not null && _conversationNodes.TryGetValue(previousThreadId, out ImportConversationNodeViewModel? previous)
                ? previous
                : null)
            ?? (previousProjectKey is not null && _projectNodes.TryGetValue(previousProjectKey, out ImportProjectNodeViewModel? previousProject)
                ? previousProject
                : null)
            ?? (object?)Projects.Where(p => !p.IsDependencyGroup).SelectMany(p => p.Conversations).FirstOrDefault(c => c.IsVisible)
            ?? Projects.FirstOrDefault();

        switch (target)
        {
            case ImportConversationNodeViewModel conversation:
                conversation.IsTreeSelected = true;
                break;
            case ImportProjectNodeViewModel project:
                project.IsTreeSelected = true;
                break;
        }

        SelectNode(target);
    }

    private void EnsurePreviewer(ImportPreview preview)
    {
        // 같은 파일이고 내용(SHA-256)도 같을 때만 이미 연 미리보기(와 캐시)를 그대로 쓴다. 다시 분석하기 전에 파일이 바뀌었으면
        // 이전 목록·캐시가 맞지 않으므로 새로 연다(Phase 9_2-32).
        string key = _backupFilePath + "|" + (preview.SourceBackupIdentity?.BackupFileSha256 ?? string.Empty);
        if (_previewer is not null && string.Equals(_previewerKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposePreviewer();
        _previewer = PreviewerFactory(_backupFilePath!);
        _previewerKey = key;
    }

    private void DisposePreviewer()
    {
        _contentCancellation?.Cancel();
        _contentCancellation = null;
        _previewer?.Dispose();
        _previewer = null;
        _previewerKey = null;
        ContentMessages.Clear();
        ContentNotice = null;
        IsContentLoading = false;
    }

    /// <summary>
    /// Phase 9_2b-04 — 고른 대화의 내용을 백그라운드에서 만든다. 선택이 바뀌면 이전 작업을 취소하고, 마지막 선택만 화면에 남긴다.
    /// 로그에는 개수와 짧은 해시만 남긴다(원문·제목 없음).
    /// </summary>
    private async Task LoadContentAsync(ImportConversationNodeViewModel node)
    {
        _contentCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _contentCancellation = cancellation;
        ContentMessages.Clear();
        ContentNotice = null;

        if (_previewer is not { } previewer)
        {
            return;
        }

        IsContentLoading = true;
        try
        {
            ImportConversationPreviewResult result = await previewer.LoadAsync(node.ThreadId, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(SelectedNode, node))
            {
                return;
            }

            foreach (ConversationMessageViewModel message in result.Messages)
            {
                ContentMessages.Add(message);
            }

            ContentNotice = result.IsPartial
                ? "일부 내용만 표시합니다. 백업에 없는 원본 대화가 있거나 읽을 수 없는 부분이 있습니다."
                : result.Messages.Count == 0 ? "표시할 대화 내용이 없습니다." : null;
            _logger.Info(
                $"가져오기 미리보기. thread={CodexBackupManager.Domain.Diagnostics.Redact.ShortHash(node.ThreadId)} " +
                $"messages={result.Messages.Count} warnings={result.WarningCount}");
        }
        catch (OperationCanceledException)
        {
            // 다른 대화를 골랐다 — 이전 결과를 남기지 않는다.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_contentCancellation, cancellation))
            {
                ContentNotice = $"대화 내용을 읽을 수 없습니다({ex.GetType().Name}).";
            }

            _logger.Warning($"가져오기 미리보기 실패. type={ex.GetType().Name}");
        }
        finally
        {
            if (ReferenceEquals(_contentCancellation, cancellation))
            {
                IsContentLoading = false;
            }
        }
    }

    private ImportConversationNodeViewModel CreateConversationNode(
        ImportSelectionSummary summary, string threadId, Dictionary<string, BackupConversationMetadata> metadata)
    {
        ImportSelectionConversation result = summary.Conversations.First(c => string.Equals(c.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
        metadata.TryGetValue(threadId, out BackupConversationMetadata? meta);
        var node = new ImportConversationNodeViewModel(this, result, meta);
        _conversationNodes.TryAdd(threadId, node);
        return node;
    }

    // ── 선택 변경(메모리 연산만, Plan 없음) ────────────────────────────────────

    /// <summary>대화들의 포함 여부를 바꾼다(노드와 빠른 선택 버튼이 부른다).</summary>
    internal void SetIncluded(IEnumerable<string> threadIds, bool include)
    {
        if (!CanEditSelection || _choices is null)
        {
            return;
        }

        var included = new HashSet<string>(_choices.IncludedThreadIds, StringComparer.OrdinalIgnoreCase);
        foreach (string id in threadIds)
        {
            if (include)
            {
                included.Add(id);
            }
            else
            {
                included.Remove(id);
            }
        }

        UpdateChoices(_choices with { IncludedThreadIds = included });
    }

    /// <summary>(Phase 9_3-01) "📁 이 프로젝트로 옮기기"를 바꾼다(메모리 연산만, Plan 없음).</summary>
    internal void SetRelink(string threadId, bool relink)
    {
        if (!CanEditSelection || _choices is null)
        {
            return;
        }

        var relinks = new HashSet<string>(_choices.RelinkThreadIds, StringComparer.OrdinalIgnoreCase);
        if (relink)
        {
            relinks.Add(threadId);
        }
        else
        {
            relinks.Remove(threadId);
        }

        UpdateChoices(_choices with { RelinkThreadIds = relinks });
        _logger.Info($"가져오기: 옮기기 {(relink ? "켬" : "끔")}. count={relinks.Count}");
    }

    private void SelectNewOnly()
    {
        if (_preview is null || _choices is null)
        {
            return;
        }

        UpdateChoices(_choices with { IncludedThreadIds = ImportUserChoices.CreateDefault(_preview).IncludedThreadIds });
    }

    private void SelectAll()
        => SetIncluded(_conversationNodes.Values.Where(n => n.Result.IsSelectable).Select(n => n.ThreadId), include: true);

    private void ClearAll()
    {
        if (_choices is null)
        {
            return;
        }

        UpdateChoices(_choices with { IncludedThreadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>프로젝트의 폴더 결정이 사용자가 고른 폴더인지.</summary>
    internal bool IsFolderDecision(string projectKey)
        => _choices is not null && _choices.ProjectDecisions.TryGetValue(projectKey, out ProjectTargetDecision? d) && !d.UseSuggestion;

    /// <summary>프로젝트의 현재 결정(없으면 제안 그대로).</summary>
    internal ProjectTargetDecision DecisionFor(string projectKey)
        => _choices?.DecisionFor(projectKey) ?? ProjectTargetDecision.Suggested;

    /// <summary>
    /// (Phase 9_5-05) 지금 결정(폴더)으로 새 프로젝트를 만들 수 있는지 — "만들지 않기"와 이름은 빼고 판정한다(메모리 연산).
    /// </summary>
    internal bool IsCreationOffered(string projectKey)
    {
        if (_summary?.Projects.FirstOrDefault(p => p.ProjectKey == projectKey) is not { } project)
        {
            return false;
        }

        ProjectTargetDecision probe = DecisionFor(projectKey) with { NewProjectName = null, CreateProject = true };
        return ImportSelection.ResolveTarget(project.Preview, probe, LocalProjects).Target.Kind == ProjectTargetKind.CreateNew;
    }

    /// <summary>(Phase 9_5-05) 새 프로젝트 이름을 바꾼다(폴더 결정은 그대로). 빈 이름은 결정 오류가 되어 가져오기가 꺼진다.</summary>
    internal void SetNewProjectName(string projectKey, string name)
    {
        if (!CanEditSelection || _choices is null)
        {
            return;
        }

        UpdateDecision(projectKey, DecisionFor(projectKey) with { NewProjectName = name });
    }

    /// <summary>(Phase 9_5-05) "새 프로젝트를 만들지 않고 기타 대화로 가져오기"(<paramref name="create"/> = <c>false</c>)를 바꾼다.</summary>
    internal void SetCreateProject(string projectKey, bool create)
    {
        if (!CanEditSelection || _choices is null)
        {
            return;
        }

        UpdateDecision(projectKey, DecisionFor(projectKey) with { CreateProject = create });
        _logger.Info($"가져오기: 새 프로젝트 만들기 {(create ? "켬" : "끔")}.");
    }

    private void UpdateDecision(string projectKey, ProjectTargetDecision decision)
    {
        var decisions = new Dictionary<string, ProjectTargetDecision>(_choices!.ProjectDecisions, StringComparer.Ordinal)
        {
            [projectKey] = decision,
        };
        UpdateChoices(_choices with { ProjectDecisions = decisions });
    }

    /// <summary>[다른 폴더…]: 폴더를 고르고 즉시 다시 판정한다(Plan은 만들지 않는다).</summary>
    internal void ChooseFolder(ImportProjectNodeViewModel node)
    {
        if (!CanEditSelection || !node.IsFolderEditable || node.ProjectKey is not { } key || _choices is null || _preview is null)
        {
            return;
        }

        string? folder = _projectPathPicker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        ApplyFolderDecision(key, folder, newProjectName: null);
    }

    /// <summary>
    /// 폴더 결정을 적용한다([다른 폴더…]와 [새 폴더 만들기]가 같은 경로를 쓴다). 이 행에 이 화면이 만든 폴더가 있었고 이번에 다른 폴더로
    /// 바뀌었으면, 바꾸기가 확정된 뒤 그 폴더가 비어 있을 때만 지운다(Phase 9_5-09).
    /// </summary>
    private void ApplyFolderDecision(string key, string folder, string? newProjectName)
    {
        _folderCreateErrors.Remove(key);
        UpdateChoices(_choices! with
        {
            ProjectDecisions = new Dictionary<string, ProjectTargetDecision>(_choices.ProjectDecisions, StringComparer.Ordinal)
            {
                [key] = ProjectTargetDecision.Folder(folder) with { NewProjectName = newProjectName },
            },
        });
        _logger.Info($"가져오기: 작업 폴더 지정. reason={_summary?.Projects.FirstOrDefault(p => p.ProjectKey == key)?.Target.Reason}");
        ReleaseCreatedFolderOf(key, unlessSameAs: folder);
    }

    /// <summary>[원래대로]: 제안 목적지로 되돌린다.</summary>
    internal void ResetFolder(ImportProjectNodeViewModel node)
    {
        if (!CanEditSelection || node.ProjectKey is not { } key || _choices is null)
        {
            return;
        }

        var decisions = new Dictionary<string, ProjectTargetDecision>(_choices.ProjectDecisions, StringComparer.Ordinal)
        {
            [key] = ProjectTargetDecision.Suggested,
        };
        _folderCreateErrors.Remove(key);
        UpdateChoices(_choices with { ProjectDecisions = decisions });
        ReleaseCreatedFolderOf(key, unlessSameAs: null);
    }

    // ── Phase 9_5-07 ~ 09 [새 폴더 만들기] ─────────────────────────────────────

    /// <summary>[새 폴더 만들기]를 쓸 수 있는지(기능이 켜져 있을 때).</summary>
    public bool CanCreateFolders => _newFolders is not null;

    /// <summary>지금 쓰는 새 폴더 기준 위치(설정값, 없으면 기본값 <c>문서\ChatGPT</c>).</summary>
    public string? NewFolderBase => _newFolders is null ? null : _newFolderBase ??= _newFolders.LoadSavedBase() ?? _newFolders.DefaultBase();

    /// <summary>"새 폴더 위치: …" 한 줄(사람이 읽는 경로 표기).</summary>
    public string? NewFolderBaseText => NewFolderBase is { } basePath ? "새 폴더 위치: " + ImportTexts.DisplayPath(basePath) : null;

    /// <summary>기준 폴더를 쓸 수 없을 때의 안내(없으면 <c>null</c>).</summary>
    public string? NewFolderBaseError
    {
        get => _newFolderBaseError ?? (NewFolderBase is { } basePath ? ValidateNewFolderBase(basePath) : null);
        private set => SetProperty(ref _newFolderBaseError, value);
    }

    /// <summary>[변경…]: 새 폴더 기준 위치를 고른다(기존 폴더 선택 대화상자). 쓸 수 없는 위치면 저장하지 않고 안내한다.</summary>
    public RelayCommand ChangeNewFolderBaseCommand { get; }

    private string? ValidateNewFolderBase(string basePath)
        => NewProjectFolderService.ValidateBase(basePath, _codexHomeProvider(), _newFolders!.ProtectedRoots());

    private void ChangeNewFolderBase()
    {
        if (_newFolders is null || !CanEditSelection)
        {
            return;
        }

        string? picked = _projectPathPicker();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        if (ValidateNewFolderBase(picked) is { } error)
        {
            NewFolderBaseError = error; // 기준 폴더는 바꾸지 않는다
            _logger.Info("가져오기: 새 폴더 위치 변경 거부.");
            return;
        }

        bool saved = _newFolders.SaveBase(picked);
        _newFolderBase = picked;
        NewFolderBaseError = saved ? null : "새 폴더 위치를 설정에 저장하지 못했습니다(이번 화면에서만 씁니다).";
        OnPropertyChanged(nameof(NewFolderBase));
        OnPropertyChanged(nameof(NewFolderBaseText));
        _logger.Info($"가져오기: 새 폴더 위치 변경. saved={saved}");
    }

    /// <summary>행의 [새 폴더 만들기] 실패 안내(없으면 <c>null</c>).</summary>
    internal string? FolderCreateErrorFor(string projectKey)
        => _folderCreateErrors.TryGetValue(projectKey, out string? error) ? error : null;

    /// <summary>
    /// [새 폴더 만들기]: 기준 폴더 아래 원래 이름(정리한 이름)으로 새 폴더를 즉시 만들고, 그 폴더를 고른 것과 똑같이 처리한다(→ CreateNew).
    /// 새 프로젝트 이름 칸에는 정리 전 원래 이름(앞뒤 공백 제거)을 넣는다. 실패하면 목적지를 바꾸지 않고 그 행에 안내한다.
    /// </summary>
    internal void CreateNewFolder(ImportProjectNodeViewModel node)
    {
        if (_newFolders is null || !CanEditSelection || !node.IsFolderEditable || node.ProjectKey is not { } key || _choices is null || _preview is null)
        {
            return;
        }

        string basePath = NewFolderBase!;
        string originalName = OriginalFolderName(node);
        NewProjectFolderResult created = ValidateNewFolderBase(basePath) is { } baseError
            ? NewProjectFolderResult.Failed(baseError.TrimEnd('.'))
            : _newFolders.CreateFolder(basePath, originalName);

        if (!created.Success)
        {
            _folderCreateErrors[key] = $"폴더를 만들지 못했습니다({created.FailureReason}). [폴더 선택…]으로 직접 골라 주세요.";
            node.Update(_summary!.Projects.First(p => p.ProjectKey == key));
            _logger.Warning("가져오기: 새 폴더 만들기 실패.");
            return;
        }

        string folder = created.FolderPath!;
        _createdFolders.Add(folder);
        ApplyFolderDecision(key, folder, originalName);
        _createdFolderByProjectKey[key] = folder;
        _logger.Info($"가져오기: 새 폴더 만듦. createdBase={created.CreatedBase} sessionFolders={_createdFolders.Count}");
    }

    /// <summary>
    /// 새 폴더의 원래 이름: 백업 프로젝트 표시 이름(앞뒤 공백 제거). (Phase 9_5-11) 백업 "기타 대화" 그룹에는 폴더를 만들지 않으므로
    /// 그 그룹의 이름 규칙은 없다.
    /// </summary>
    private static string OriginalFolderName(ImportProjectNodeViewModel node) => node.DisplayName.Trim();

    /// <summary>
    /// 행의 목적지가 이 화면이 만든 폴더에서 다른 곳으로 바뀌었으면, 다른 행도 쓰지 않을 때 그 폴더를 (비어 있을 때만) 지운다.
    /// </summary>
    private void ReleaseCreatedFolderOf(string key, string? unlessSameAs)
    {
        if (!_createdFolderByProjectKey.TryGetValue(key, out string? previous) ||
            (unlessSameAs is not null && string.Equals(previous, unlessSameAs, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _createdFolderByProjectKey.Remove(key);
        if (!_createdFolderByProjectKey.Values.Contains(previous, StringComparer.OrdinalIgnoreCase) && !IsFolderInAnyDecision(previous))
        {
            DeleteCreatedFolders([previous]);
        }
    }

    private bool IsFolderInAnyDecision(string folder)
        => _choices is not null && _choices.ProjectDecisions.Values.Any(d =>
            !d.UseSuggestion && d.FolderPath is { } path && CanonicalPath.AreSameLocation(path, folder));

    /// <summary>성공한 적용에 쓰인 폴더(새 프로젝트 루트 등). 성공이 아니면 빈 목록.</summary>
    private IReadOnlyList<string> KeepFoldersUsedBySuccessfulApply()
    {
        if (_result?.Outcome != RestoreOutcome.Succeeded || _appliedPlan is not { } plan)
        {
            return [];
        }

        return plan.Projects
            .Where(p => plan.UsesProjectTarget(p) && p.ResolvedTarget?.FolderPath is not null)
            .Select(p => p.ResolvedTarget!.FolderPath!)
            .ToList();
    }

    /// <summary>이 화면이 만든 폴더 중 <paramref name="keep"/>에 없는 것을 (비어 있을 때만) 지운다. 목록은 비운다.</summary>
    private void CleanupCreatedFolders(IReadOnlyList<string> keep)
    {
        List<string> candidates = _createdFolders
            .Where(folder => !keep.Any(k => CanonicalPath.AreSameLocation(k, folder)))
            .ToList();
        DeleteCreatedFolders(candidates);
        _createdFolders.Clear();
        _createdFolderByProjectKey.Clear();
        _folderCreateErrors.Clear();
    }

    private void DeleteCreatedFolders(IReadOnlyList<string> folders)
    {
        int deleted = 0, kept = 0, failed = 0;
        foreach (string folder in folders)
        {
            // 세 조건: 이 화면에서 앱이 만든 폴더(_createdFolders), 비재귀 삭제, 지우기 직전 비어 있음 재확인(TryDeleteIfEmpty).
            if (!_createdFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (NewProjectFolderService.TryDeleteIfEmpty(folder, out bool error))
            {
                deleted++;
            }
            else if (error)
            {
                failed++;
            }
            else
            {
                kept++;
            }

            _createdFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        }

        if (failed > 0)
        {
            _logger.Warning($"가져오기: 만든 빈 폴더 정리 실패. failed={failed}");
        }

        if (deleted + kept > 0)
        {
            _logger.Info($"가져오기: 만든 폴더 정리. deleted={deleted} keptNotEmpty={kept}");
        }
    }

    /// <summary>
    /// UI가 폴더를 바꿀 때 부르는 순수 판정(설계 9_2-06)을 노출한다 — Plan 없이 메모리와 <c>Directory.Exists</c>만 쓴다.
    /// </summary>
    internal ProjectTargetResolution PreviewTarget(ImportProjectNodeViewModel node, ProjectTargetDecision decision)
    {
        ImportSelectionProject project = _summary!.Projects.First(p => p.ProjectKey == node.ProjectKey);
        return ImportSelection.ResolveTarget(project.Preview, decision, LocalProjects);
    }

    private void UpdateChoices(ImportUserChoices choices)
    {
        _choices = choices;
        _summary = ImportSelection.Compute(_preview!, choices);
        EditingError = null;

        foreach (ImportSelectionConversation result in _summary.Conversations)
        {
            if (_conversationNodes.TryGetValue(result.ThreadId, out ImportConversationNodeViewModel? node))
            {
                node.Update(result);
            }
        }

        foreach (ImportSelectionProject project in _summary.Projects)
        {
            if (_projectNodes.TryGetValue(project.ProjectKey, out ImportProjectNodeViewModel? node))
            {
                node.Update(project);
            }
        }

        _dependencyGroup?.Update(null);
        OnPropertyChanged(nameof(Choices));
        ApplyFilter();
        RefreshSummaryBindings();
    }

    private void RefreshSummaryBindings()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryHint));
        OnPropertyChanged(nameof(IsSummaryHintWarning));
        OnPropertyChanged(nameof(ImportButtonText));
        OnPropertyChanged(nameof(CanImport));
        RaiseCommands();
    }

    private void ApplyFilter()
    {
        string query = _searchText.Trim();
        foreach (ImportProjectNodeViewModel project in Projects)
        {
            bool anyVisible = false;
            foreach (ImportConversationNodeViewModel conversation in project.Conversations)
            {
                bool shown = IsShownBySearch(query, project.DisplayName, project.IsDependencyGroup, conversation.Title, conversation.IsAutoIncluded);
                conversation.IsVisible = shown;
                anyVisible |= shown;
            }

            project.IsVisible = project.IsDependencyGroup ? anyVisible : anyVisible || query.Length == 0;
        }
    }

    /// <summary>
    /// 검색어(앞뒤 공백 제거됨)로 대화 한 줄을 보일지(표시 규칙만, 선택은 바꾸지 않는다). Phase 9_2-33 — 백업 프로젝트 그룹 이름이
    /// 맞으면 그 그룹의 대화를 모두 보인다. "필요한 원본 대화(자동 포함)" 그룹은 이름으로 찾지 않고, 실제로 자동 포함된 대화만 보인다.
    /// </summary>
    internal static bool IsShownBySearch(string query, string projectName, bool isDependencyGroup, string title, bool isAutoIncluded)
    {
        bool titleMatches = query.Length == 0 || title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        if (isDependencyGroup)
        {
            return titleMatches && isAutoIncluded;
        }

        return titleMatches || projectName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>트리 선택이 바뀌었을 때(View가 부른다).</summary>
    public void SelectNode(object? node)
    {
        SelectedNode = node;
        if (node is ImportConversationNodeViewModel conversation)
        {
            ContentLoadTask = LoadContentAsync(conversation);
        }
        else
        {
            _contentCancellation?.Cancel();
            ContentMessages.Clear();
            ContentNotice = null;
            IsContentLoading = false;
        }
    }

    // ── Codex 감시 ───────────────────────────────────────────────────────────

    private bool IsCodexRunningNow()
    {
        try
        {
            return CodexProcessGuard.Check(ProcessLister).IsRunning;
        }
        catch (Exception ex)
        {
            // 확인 자체가 실패하면 안전한 쪽(실행 중으로 본다)을 택한다.
            _logger.Warning($"Codex 실행 여부 확인 실패. type={ex.GetType().Name}");
            return true;
        }
    }

    /// <summary>창이 활성화될 때와 주기적으로 부른다. 편집 중 Codex가 켜지면 배너를 띄우고 가져오기를 막는다.</summary>
    public void CheckCodexRunning()
    {
        if (State is not (ImportWorkspaceState.Editing or ImportWorkspaceState.Confirming))
        {
            return;
        }

        bool running = IsCodexRunningNow();
        if (running && !IsCodexRunning)
        {
            _logger.Info("가져오기 편집 중 Codex 실행 감지.");
        }

        IsCodexRunning = running;
        if (running)
        {
            IsAnalysisStale = true;
        }

        OnPropertyChanged(nameof(CanImport));
    }

    private void StartPolling()
    {
        StopPolling();
        _pollTimer = PollTimerFactory(CodexPollInterval, CheckCodexRunning);
    }

    private void StopPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private static IDisposable DefaultPollTimer(TimeSpan interval, Action callback)
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        return new Timer(
            _ =>
            {
                if (context is null)
                {
                    callback();
                }
                else
                {
                    context.Post(_ => callback(), null);
                }
            },
            null, interval, interval);
    }

    private static CodexCatalog DefaultCatalogBuilder(string codexHomePath, CancellationToken cancellationToken)
    {
        CodexDetectionService.DetectionResult detection = new CodexDetectionService().DetectFromUserSelection(codexHomePath);
        if (detection.Installation is not { } installation)
        {
            throw new InvalidOperationException("Codex Home을 다시 확인할 수 없습니다.");
        }

        return CodexCatalogBuilder.Build(installation, cancellationToken);
    }

    // ── 가져오기 / 적용 ──────────────────────────────────────────────────────

    /// <summary>
    /// [가져오기]: 확인 → Plan 한 번 생성 → 적용 → 결과. <paramref name="bypassCanImport"/>는 테스트 전용이다
    /// (예: 쓸 것이 없는 선택으로 적용 경로를 태워 NothingToDo 결과 화면을 확인). Core 안전장치는 그대로 적용된다.
    /// </summary>
    internal async Task ImportAsync(bool bypassCanImport = false)
    {
        if ((!bypassCanImport && !CanImport) || _preview is null || _choices is null || _summary is null || _backupFilePath is null)
        {
            return;
        }

        State = ImportWorkspaceState.Confirming;
        bool confirmed = _confirmDialog(ImportTexts.ConfirmMessage(_summary, LocalProjects), "가져오기 확인");
        if (!confirmed)
        {
            State = ImportWorkspaceState.Editing;
            return;
        }

        // 적용 직전 한 번 더 확인한다(최종 판단은 RestoreExecutor의 이중 확인이다).
        if (IsCodexRunningNow())
        {
            IsCodexRunning = true;
            IsAnalysisStale = true;
            State = ImportWorkspaceState.Editing;
            return;
        }

        // Plan은 여기서 딱 한 번 만든다(backup 전체를 스트리밍 해시한다).
        PlanBuildCount++;
        ImportPlan? plan = PlanBuilder(_preview, _backupFilePath, _choices);
        if (plan is null)
        {
            EditingError = ImportTexts.PlanUnavailable;
            State = ImportWorkspaceState.Editing;
            _logger.Warning("가져오기: Plan을 만들 수 없음(백업 변경 또는 선택 오류).");
            return;
        }

        string? home = _codexHomeProvider();
        if (string.IsNullOrWhiteSpace(home))
        {
            EditingError = "Codex Home 경로를 확인할 수 없습니다.";
            State = ImportWorkspaceState.Editing;
            return;
        }

        StopPolling();
        _appliedPlan = plan;
        var cancellation = new CancellationTokenSource();
        _applyCancellation = cancellation;
        ApplyStatusText = "안전성 확인 중…";
        State = ImportWorkspaceState.Applying;
        SynchronizationContext? uiContext = SynchronizationContext.Current;

        RestoreResult result;
        try
        {
            result = await Task.Run(
                () => RestoreApply(plan, home, _snapshotRootProvider(), status => ReportApplyStatus(status, uiContext), cancellation.Token),
                cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = new RestoreResult(RestoreOutcome.Cancelled, "적용을 취소했습니다.", null, null);
        }
        catch (Exception ex)
        {
            _logger.Error("가져오기 적용 중 예기치 않은 오류", ex);
            result = new RestoreResult(RestoreOutcome.NotReady, $"적용 중 예기치 않은 오류가 발생했습니다: {ex.GetType().Name}", null, null);
        }
        finally
        {
            if (ReferenceEquals(_applyCancellation, cancellation))
            {
                _applyCancellation = null;
            }
        }

        _logger.Info(
            $"가져오기 적용 결과. outcome={result.Outcome} preflight={result.PreflightStatus?.ToString() ?? "-"} " +
            $"imports={_summary.ImportCount} updates={_summary.UpdateCount} relinks={_summary.RelinkCount} uncategorized={_summary.UncategorizedImportCount} " +
            $"newProjects={_summary.NewProjects.Count}"); // 프로젝트 이름·경로는 남기지 않는다
        ShowResult(result, plan);
    }

    private void ReportApplyStatus(string status, SynchronizationContext? uiContext)
    {
        if (uiContext is null)
        {
            ApplyStatusText = status;
            return;
        }

        uiContext.Post(_ => ApplyStatusText = status, null);
    }

    private void ShowResult(RestoreResult result, ImportPlan plan)
    {
        Result = new ImportResultViewModel(result, plan, _summary!, _titles, LocalProjects);
        State = ImportWorkspaceState.Result;
    }

    // ── 닫기 ────────────────────────────────────────────────────────────────

    private void Close(bool showInList)
    {
        bool changed = _result?.Outcome == RestoreOutcome.Succeeded;
        IReadOnlyList<string> highlight = showInList ? ImportedThreadIds : [];
        CleanupCreatedFolders(KeepFoldersUsedBySuccessfulApply()); // Phase 9_5-09
        StopPolling();
        _analysisCancellation?.Cancel();
        ResetAnalysis();
        State = ImportWorkspaceState.Closed;
        Closed?.Invoke(this, new ImportWorkspaceClosedEventArgs(changed, showInList, highlight));
    }

    private void ResetAnalysis()
    {
        _preview = null;
        _summary = null;
        Result = null;
        _appliedPlan = null;
        DisposePreviewer();
        Projects.Clear();
        DetailRows.Clear();
        _conversationNodes.Clear();
        _projectNodes.Clear();
        _dependencyGroup = null;
        SelectedNode = null;
        IsCodexRunning = false;
        IsAnalysisStale = false;
        Message = null;
        MessageHint = null;
        EditingError = null;
        ApplyStatusText = null;
        CodexRecheckText = null;
        _failureRetryable = false;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        RefreshSummaryBindings();
    }

    private void RaiseCommands()
    {
        foreach (RelayCommand command in new[]
        {
            RecheckCodexCommand, CancelAnalysisCommand, ChooseOtherFileCommand, CloseCommand, SelectNewOnlyCommand,
            SelectAllCommand, ClearAllCommand, ReanalyzeCommand, ImportCommand, CancelApplyCommand, RetryCommand, ShowInListCommand,
        })
        {
            command?.RaiseCanExecuteChanged();
        }

        foreach (ImportProjectNodeViewModel project in Projects)
        {
            project.ChooseFolderCommand.RaiseCanExecuteChanged();
            project.ResetFolderCommand.RaiseCanExecuteChanged();
            project.CreateFolderCommand.RaiseCanExecuteChanged();
        }

        ChangeNewFolderBaseCommand?.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 앱 종료 중: 최선만 다한다(예외를 밖으로 내지 않는다, 종료를 막지 않는다).
        try
        {
            CleanupCreatedFolders(KeepFoldersUsedBySuccessfulApply());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warning($"가져오기: 종료 중 폴더 정리 실패. type={ex.GetType().Name}");
        }

        DisposePreviewer();
        StopPolling();
        _analysisCancellation?.Cancel();
        _applyCancellation?.Cancel();
    }
}
