using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Conversation;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Selection;
using CodexBackupManager.Domain.Diagnostics;
using CodexBackupManager.Domain.Paths;
using CodexBackupManager.Restore;

namespace CodexBackupManager.App.ViewModels;

/// <summary>한 줄 표시 항목.</summary>
/// <param name="Label">항목 이름.</param>
/// <param name="Value">항목 값.</param>
public sealed record InfoRow(string Label, string Value);

/// <summary>메인 창 ViewModel. Phase 1은 "탐지 결과 표시"까지다.</summary>
public sealed class MainViewModel : ObservableObject
{
    private const string NotAvailable = "확인 불가";

    private readonly CodexDetectionService _detection;
    private readonly SettingsStore _settings;
    private readonly FileLogger _logger;
    private readonly Func<string?> _folderPicker;
    private readonly Func<string, string?> _exportFilePicker;
    private readonly Func<string?> _importFilePicker;
    private readonly Func<string?> _projectPathPicker;
    private readonly Func<string, string, bool> _confirmDialog;

    private bool _isBusy;
    private bool _isConnected;
    private string _statusText = "Codex 확인 중…";
    private string _statusGlyph = "…";
    private string _homePath = string.Empty;
    private string? _warningText;
    private string? _detailText;

    private bool _isCatalogLoading;
    private string? _catalogSummaryText;
    private string? _compactSummaryText;
    private CancellationTokenSource? _catalogCancellation;
    private CodexCatalog? _lastCatalog;

    // Phase 4 — 백업 대상 선택의 단일 source of truth(ThreadId 기준). Viewer 포커스와는 완전히 분리된
    // 상태다(클래스 remarks 없음, ConversationSelectionState의 remarks 참고).
    private readonly ConversationSelectionState _selection = new();
    private CanonicalPath? _lastHome;

    private string? _selectedConversationTitle;
    private bool _isConversationLoading;
    private string? _conversationErrorText;
    private CancellationTokenSource? _conversationCancellation;

    // Phase 5 — Export. Codex Desktop/CLI 버전은 Apply()에서 저장해 두고 manifest에 그대로 쓴다
    // (매번 다시 조사하지 않는다 — 이미 Rows를 채울 때 읽은 값이다).
    private string? _lastCodexDesktopVersion;
    private string? _lastCodexCliVersion;
    private bool _isExporting;
    private string? _exportStatusText;
    private CancellationTokenSource? _exportCancellation;

    // Phase 6 — Import Preview. Codex에는 아무것도 쓰지 않는다(판정/미리보기까지만).
    private bool _isImportPreviewLoading;
    private string? _importStatusText;
    private ImportPreviewViewModel? _currentImportPreview;
    private CancellationTokenSource? _importPreviewCancellation;
    private string? _lastImportBackupFilePath;

    // Phase 06_01 — override 상태는 View code-behind가 아니라 여기(도메인 ImportPreview 그 자체)에
    // 저장한다. ImportPreviewBuilder.ApplyManualProjectPathOverride가 이 값만 갱신하고,
    // ImportPreviewViewModel은 매번 이 값으로부터 다시 만든다.
    private ImportPreview? _lastImportPreviewDomain;
    private string? _importPlanSummaryText;

    // Phase 06_03 — freeze된 ImportPlan은 여기 한 곳에만 보관한다. Preview가 성공하거나 경로를
    // 재지정할 때 딱 한 번만 ImportPlanBuilder.Build를 부르고, 그 결과를 이 필드에 저장한다 — Phase 7이
    // Apply 직전에 이 인스턴스를 그대로 받아 preflight만 다시 돌리면 되고, Preview를 다시 해석하거나
    // Plan을 다시 만들 필요가 없다.
    private ImportPlan? _currentImportPlan;

    // Phase 07_01 — Apply. RestoreExecutor가 유일한 안전성 판단 주체다: 여기서는 확인 대화상자를
    // 띄우고, 진행 중 다른 조작(Export/새 Import Preview/경로 재지정/폴더 변경)을 막고, 결과 문구를
    // 보여줄 뿐 Plan/Preflight를 다시 해석하지 않는다.
    private bool _isApplying;
    private string? _applyStatusText;
    private CancellationTokenSource? _applyCancellation;

    /// <summary>생성자.</summary>
    /// <param name="detection">탐지 서비스.</param>
    /// <param name="settings">설정 저장소.</param>
    /// <param name="logger">로거.</param>
    /// <param name="folderPicker">폴더 선택 대화상자. 취소 시 <c>null</c>을 반환해야 한다.</param>
    /// <param name="exportFilePicker">
    /// <c>.codexbackup</c> 저장 위치 선택 대화상자(입력: 기본 파일 이름, 출력: 선택한 경로 또는 취소 시
    /// <c>null</c>). 생략하면 <see cref="BackupFilePicker.PickSaveLocation"/>을 쓴다.
    /// </param>
    /// <param name="importFilePicker">
    /// 불러올 <c>.codexbackup</c> 선택 대화상자(Phase 6, 출력: 선택한 경로 또는 취소 시 <c>null</c>).
    /// 생략하면 <see cref="BackupFilePicker.PickOpenLocation"/>을 쓴다.
    /// </param>
    /// <param name="projectPathPicker">
    /// Import Preview에서 프로젝트 경로를 수동으로 재지정할 때 쓰는 폴더 선택 대화상자(Phase 06_01,
    /// 출력: 선택한 경로 또는 취소 시 <c>null</c>). 생략하면 <see cref="FolderPicker.PickProjectFolder"/>를 쓴다.
    /// </param>
    /// <param name="confirmDialog">
    /// Apply(Phase 07_01) 직전 확인 대화상자. (메시지, 제목) → 사용자가 "예"를 눌렀는지. 생략하면
    /// <see cref="Services.ConfirmDialog.Confirm"/>을 쓴다.
    /// </param>
    public MainViewModel(
        CodexDetectionService detection,
        SettingsStore settings,
        FileLogger logger,
        Func<string?> folderPicker,
        Func<string, string?>? exportFilePicker = null,
        Func<string?>? importFilePicker = null,
        Func<string?>? projectPathPicker = null,
        Func<string, string, bool>? confirmDialog = null)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(folderPicker);

        _detection = detection;
        _settings = settings;
        _logger = logger;
        _folderPicker = folderPicker;
        _exportFilePicker = exportFilePicker ?? BackupFilePicker.PickSaveLocation;
        _importFilePicker = importFilePicker ?? BackupFilePicker.PickOpenLocation;
        _projectPathPicker = projectPathPicker ?? FolderPicker.PickProjectFolder;
        _confirmDialog = confirmDialog ?? Services.ConfirmDialog.Confirm;

        // UI 스레드에서 시작하고 결과를 기다리지 않는다. 예외는 각 메서드 내부에서 처리한다.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy && !IsApplying);
        ChangeFolderCommand = new RelayCommand(() => _ = ChangeFolderAsync(), () => !IsBusy && !IsApplying);
        SelectAllConversationsCommand = new RelayCommand(SelectAllConversations, () => TotalConversationCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearAllSelections, () => HasSelection);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => HasSelection && !IsExporting && !IsApplying);
        CancelExportCommand = new RelayCommand(CancelExport, () => IsExporting);
        ImportPreviewCommand = new RelayCommand(() => _ = ImportPreviewAsync(), () => !IsImportPreviewLoading && !IsApplying);
        CloseImportPreviewCommand = new RelayCommand(CloseImportPreview, () => !IsApplying);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => _currentImportPlan is not null && !IsApplying);
        CancelApplyCommand = new RelayCommand(CancelApply, () => IsApplying);

        // 선택 상태 변경은 한 곳에서만 구독한다 — 대량 선택이어도 이 핸들러는 딱 한 번만 불려서
        // O(1) 작업(개수 갱신)만 한다(요구사항 10: 수천 개에서도 재계산이 폭증하지 않아야 한다).
        _selection.Changed += OnSelectionChanged;
    }

    /// <summary>다시 탐지.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Codex 폴더 직접 선택.</summary>
    public RelayCommand ChangeFolderCommand { get; }

    /// <summary>현재 카탈로그의 모든 대화를 백업 대상으로 선택한다.</summary>
    public RelayCommand SelectAllConversationsCommand { get; }

    /// <summary>백업 선택을 전부 해제한다.</summary>
    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>선택한 대화를 <c>.codexbackup</c> 파일로 내보낸다(Phase 5).</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>진행 중인 Export를 취소한다(Phase 05_01). Export 중일 때만 활성화된다.</summary>
    public RelayCommand CancelExportCommand { get; }

    /// <summary><c>.codexbackup</c> 파일을 선택해 Import Preview를 만든다(Phase 6). Codex에는 아무것도 쓰지 않는다.</summary>
    public RelayCommand ImportPreviewCommand { get; }

    /// <summary>현재 표시 중인 Import Preview를 닫는다(판정 결과를 버릴 뿐, Codex에는 아무 영향 없다).</summary>
    public RelayCommand CloseImportPreviewCommand { get; }

    /// <summary>
    /// 현재 frozen된 <see cref="_currentImportPlan"/>을 실제 Codex에 적용한다(Phase 07_01). 이
    /// <c>CanExecute</c>는 1차 UI 조건일 뿐이다 — 실제 안전성 판단은 클릭 시점에
    /// <see cref="RestoreExecutor"/>가 직접 fresh preflight/backup pin/Operation Plan 재검증으로
    /// 수행한다.
    /// </summary>
    public RelayCommand ApplyCommand { get; }

    /// <summary>진행 중인 Apply를 취소한다. Snapshot 이후라면 취소도 Rollback으로 처리된다.</summary>
    public RelayCommand CancelApplyCommand { get; }

    /// <summary>Import Preview를 만드는 중인지.</summary>
    public bool IsImportPreviewLoading
    {
        get => _isImportPreviewLoading;
        private set
        {
            if (SetProperty(ref _isImportPreviewLoading, value))
            {
                ImportPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Import Preview 진행/결과 문구. 대화 원문이나 개인 절대경로는 담지 않는다.</summary>
    public string? ImportStatusText
    {
        get => _importStatusText;
        private set => SetProperty(ref _importStatusText, value);
    }

    /// <summary>현재 만들어진 Import Preview. 아직 없으면 <c>null</c>.</summary>
    public ImportPreviewViewModel? CurrentImportPreview
    {
        get => _currentImportPreview;
        private set
        {
            if (SetProperty(ref _currentImportPreview, value))
            {
                OnPropertyChanged(nameof(HasImportPreview));
            }
        }
    }

    /// <summary>Import Preview 결과가 있어 화면에 보여줄 수 있는지.</summary>
    public bool HasImportPreview => CurrentImportPreview is not null;

    /// <summary>
    /// Preview를 <see cref="ImportPlan"/>으로 freeze한 결과에 대한 안내 문구(Phase 06_01, 문구는
    /// Phase 06_03에서 정정). <b>이 문구는 "지금 바로 Apply해도 안전하다"는 뜻이 아니다</b> —
    /// <see cref="ImportPlan.IsApplyReady"/>는 Diverged/Unverifiable이 없다는 것만 말해줄 뿐, backup
    /// 파일이나 로컬 Codex가 Preview 이후 바뀌었는지는 전혀 모른다(그건 Apply 직전 fresh
    /// <see cref="ImportPlanPreflightValidator"/>만 알 수 있다). Phase 7 이전까지는 "실제 적용
    /// 가능"이라는 오해를 주지 않는 중립적인 문구만 보여준다.
    /// </summary>
    public string? ImportPlanSummaryText
    {
        get => _importPlanSummaryText;
        private set => SetProperty(ref _importPlanSummaryText, value);
    }

    /// <summary>
    /// 테스트 전용 접근자(<c>InternalsVisibleTo</c>로 App.Tests에만 노출). 현재 freeze된
    /// <see cref="ImportPlan"/> — <see cref="UpdateImportPlanSummary"/>가 딱 한 곳에서만 만들고
    /// 저장한다. Phase 7은 이 인스턴스를 그대로 받아 Apply 직전 preflight만 다시 돌리면 된다(Preview
    /// 재해석/Plan 재생성 금지).
    /// </summary>
    internal ImportPlan? CurrentImportPlan => _currentImportPlan;

    /// <summary>Export가 진행 중인지. 재실행을 막고 취소 버튼 표시 여부를 결정하는 데 쓴다.</summary>
    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                ExportCommand.RaiseCanExecuteChanged();
                CancelExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Export 진행/결과 문구. 대화 원문이나 개인 절대경로는 담지 않는다.</summary>
    public string? ExportStatusText
    {
        get => _exportStatusText;
        private set => SetProperty(ref _exportStatusText, value);
    }

    /// <summary>
    /// Apply가 진행 중인지(Phase 07_01). 진행 중에는 Export/새 Import Preview/경로 재지정/Codex
    /// Home 변경을 모두 막는다.
    /// </summary>
    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                CancelApplyCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
                ChangeFolderCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
                ImportPreviewCommand.RaiseCanExecuteChanged();
                CloseImportPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Apply 진행/결과 문구("안전성 확인 중" 등). 대화 원문이나 개인 절대경로는 담지 않는다.
    /// </summary>
    public string? ApplyStatusText
    {
        get => _applyStatusText;
        private set => SetProperty(ref _applyStatusText, value);
    }

    /// <summary>탐지 결과 표. 라벨/값 쌍.</summary>
    public ObservableCollection<InfoRow> Rows { get; } = [];

    /// <summary>탐색 후보별 기록. 실패 원인을 보여준다.</summary>
    public ObservableCollection<InfoRow> ProbeRows { get; } = [];

    /// <summary>작업 중인지.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                ChangeFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Codex를 찾았는지.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>상단 상태 문구.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>상태 표시 문자.</summary>
    public string StatusGlyph
    {
        get => _statusGlyph;
        private set => SetProperty(ref _statusGlyph, value);
    }

    /// <summary>표시용 Codex Home 경로.</summary>
    public string HomePath
    {
        get => _homePath;
        private set => SetProperty(ref _homePath, value);
    }

    /// <summary>
    /// Phase 04_07 — 상단 진단 영역이 접혀 있어도 항상 보이는 한 줄 요약
    /// (<c>"Desktop {버전} · CLI {버전} · Sessions {개수} · Threads {개수}"</c>). 연결에 실패하면
    /// <c>null</c>이다(그 상태에선 실패 안내 문구가 대신 보인다). <see cref="Rows"/>에 이미 있는
    /// 값들을 그대로 재사용해 만든다 — 별도 진단 로직을 새로 만들지 않는다.
    /// </summary>
    public string? CompactSummaryText
    {
        get => _compactSummaryText;
        private set => SetProperty(ref _compactSummaryText, value);
    }

    /// <summary>경고 문구. 없으면 <c>null</c>.</summary>
    public string? WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    /// <summary>경고가 있는지.</summary>
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    /// <summary>실패 시 상세 사유.</summary>
    public string? DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    /// <summary>Phase 2 — 왼쪽 트리에 표시할 프로젝트/대화 카탈로그.</summary>
    public ObservableCollection<ProjectNodeViewModel> ProjectNodes { get; } = [];

    /// <summary>카탈로그를 만드는 중인지. <c>.codex</c> 탐색이 UI 스레드를 막지 않는다.</summary>
    public bool IsCatalogLoading
    {
        get => _isCatalogLoading;
        private set => SetProperty(ref _isCatalogLoading, value);
    }

    /// <summary>카탈로그 요약(프로젝트/대화/파일 개수, 소요 시간). 아직 없으면 <c>null</c>.</summary>
    public string? CatalogSummaryText
    {
        get => _catalogSummaryText;
        private set => SetProperty(ref _catalogSummaryText, value);
    }

    /// <summary>Phase 4 — 현재 백업 대상으로 선택된 대화 개수.</summary>
    public int SelectedConversationCount => _selection.Count;

    /// <summary>현재 카탈로그에 있는 사용자 대화 총 개수(선택 요약의 분모).</summary>
    public int TotalConversationCount => ProjectNodes.Sum(p => p.Conversations.Count);

    /// <summary>하나 이상 선택되어 있는지. Phase 5의 Export 버튼 활성화 조건으로 그대로 쓸 수 있다.</summary>
    public bool HasSelection => SelectedConversationCount > 0;

    /// <summary>"선택한 대화 N / M" 요약 문구.</summary>
    public string SelectionSummaryText => $"선택한 대화 {SelectedConversationCount} / {TotalConversationCount}";

    /// <summary>Phase 3 — 오른쪽 Conversation Viewer에 표시할 메시지 목록.</summary>
    public ObservableCollection<ConversationMessageViewModel> ConversationMessages { get; } = [];

    /// <summary>선택된 대화의 제목. 선택된 대화가 없으면 <c>null</c>.</summary>
    public string? SelectedConversationTitle
    {
        get => _selectedConversationTitle;
        private set
        {
            if (SetProperty(ref _selectedConversationTitle, value))
            {
                OnPropertyChanged(nameof(HasSelectedConversation));
                OnPropertyChanged(nameof(NoConversationSelected));
            }
        }
    }

    /// <summary>대화가 선택되어 있는지(오른쪽 영역 placeholder ↔ 실제 뷰어 전환용).</summary>
    public bool HasSelectedConversation => SelectedConversationTitle is not null;

    /// <summary>대화 내용을 읽는 중인지.</summary>
    public bool IsConversationLoading
    {
        get => _isConversationLoading;
        private set => SetProperty(ref _isConversationLoading, value);
    }

    /// <summary>대화 읽기 실패 사유. 없으면 <c>null</c>.</summary>
    public string? ConversationErrorText
    {
        get => _conversationErrorText;
        private set
        {
            if (SetProperty(ref _conversationErrorText, value))
            {
                OnPropertyChanged(nameof(HasConversationError));
            }
        }
    }

    /// <summary>대화 읽기 오류 문구가 있는지.</summary>
    public bool HasConversationError => !string.IsNullOrWhiteSpace(ConversationErrorText);

    /// <summary>오른쪽 영역에 placeholder를 보여줘야 하는지(아직 대화를 선택하지 않았을 때).</summary>
    public bool NoConversationSelected => !HasSelectedConversation;

    /// <summary>
    /// 왼쪽 트리에서 대화를 선택했을 때 호출한다(<c>null</c>이면 선택 해제).
    /// 이전 로딩이 진행 중이었다면 취소하고 새 로딩만 화면에 반영한다(race condition 방지).
    /// </summary>
    public void SelectConversation(ConversationNodeViewModel? node)
    {
        _conversationCancellation?.Cancel();

        if (node is null)
        {
            SelectedConversationTitle = null;
            ConversationMessages.Clear();
            ConversationErrorText = null;
            IsConversationLoading = false;
            return;
        }

        _ = LoadConversationAsync(node);
    }

    private async Task LoadConversationAsync(ConversationNodeViewModel node)
    {
        var cancellation = new CancellationTokenSource();
        _conversationCancellation = cancellation;

        SelectedConversationTitle = node.Title;
        ConversationMessages.Clear();
        ConversationErrorText = null;
        IsConversationLoading = true;

        try
        {
            IReadOnlyDictionary<string, Domain.Codex.Threads.ThreadChain>? chains = _lastCatalog?.Chains;
            if (chains is null)
            {
                ConversationErrorText = "카탈로그가 아직 준비되지 않았습니다.";
                return;
            }

            // transcript 재구성과 ConversationMessageViewModel 생성(Markdown-lite 파싱까지)을 전부
            // 백그라운드에서 끝낸다. FromDomain은 여기서 Blocks(WPF 비의존 순수 데이터)만 만들고
            // FlowDocument는 절대 만들지 않으므로, worker 스레드에서 WPF 객체를 생성하는 일은 없다
            // (ConversationMessageViewModel 클래스 remarks 참고) — Body는 각 아이템이 실제로
            // virtualize되어 화면에 바인딩될 때 UI 스레드에서 지연 생성된다.
            (ConversationTranscript transcript, List<ConversationMessageViewModel> messages) = await Task.Run(
                () =>
                {
                    ConversationTranscript t = ConversationTranscriptBuilder.Build(node.ThreadId, chains, cancellation.Token);
                    List<ConversationMessageViewModel> vms = t.Messages
                        .Select(ConversationMessageViewModel.FromDomain)
                        .ToList();
                    return (t, vms);
                },
                cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            foreach (ConversationMessageViewModel message in messages)
            {
                ConversationMessages.Add(message);
            }

            if (transcript.Messages.Count == 0 && transcript.Warnings.Count > 0)
            {
                ConversationErrorText = "이 대화를 읽을 수 없습니다.";
            }

            // 경고에는 사용자 원문이 없다(파일명 수준 진단 문구뿐이다). thread ID도 해시로만 남긴다.
            _logger.Info(
                $"대화 로딩 완료. thread={Redact.ShortHash(node.ThreadId)} messages={transcript.Messages.Count} " +
                $"warnings={transcript.Warnings.Count} loadMs={transcript.BuildDuration.TotalMilliseconds:F0}");
        }
        catch (OperationCanceledException)
        {
            // 사용자가 다른 대화를 선택해 취소됨. 이전 결과를 화면에 남기지 않는다.
        }
        catch (Exception ex)
        {
            _logger.Error("대화 로딩 중 오류", ex);
            ConversationErrorText = $"대화를 읽는 중 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            if (ReferenceEquals(_conversationCancellation, cancellation))
            {
                IsConversationLoading = false;
            }
        }
    }

    /// <summary>시작 시 한 번 호출한다.</summary>
    public async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Codex 확인 중…";
        StatusGlyph = "…";

        try
        {
            AppSettings settings = _settings.Load();
            CodexDetectionService.DetectionResult result =
                await Task.Run(() => _detection.Detect(settings.ManualCodexHomePath)).ConfigureAwait(true);
            Apply(result);
        }
        catch (Exception ex)
        {
            _logger.Error("자동 탐지 중 오류", ex);
            ShowFailure($"탐지 중 오류가 발생했습니다: {ex.GetType().Name}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeFolderAsync()
    {
        string? selected = _folderPicker();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        IsBusy = true;

        try
        {
            CodexDetectionService.DetectionResult result =
                await Task.Run(() => _detection.DetectFromUserSelection(selected)).ConfigureAwait(true);

            if (result.Found)
            {
                AppSettings settings = _settings.Load();
                settings.ManualCodexHomePath = selected;
                if (!_settings.Save(settings))
                {
                    _logger.Warning("설정 저장에 실패했습니다.");
                }

                _logger.Info($"사용자가 Codex Home을 지정했습니다. path={Redact.Path(selected)}");
            }
            else
            {
                _logger.Warning($"사용자가 지정한 폴더가 Codex Home이 아닙니다. path={Redact.Path(selected)}");
            }

            Apply(result);
        }
        catch (Exception ex)
        {
            _logger.Error("사용자 지정 폴더 검사 중 오류", ex);
            ShowFailure($"폴더 검사 중 오류가 발생했습니다: {ex.GetType().Name}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(CodexDetectionService.DetectionResult result)
    {
        Rows.Clear();
        ProbeRows.Clear();

        foreach (CodexHomeProbe probe in result.Located.Probes)
        {
            string status = probe.Skipped
                ? "건너뜀"
                : probe.Validation is null
                    ? "검사 불가"
                    : $"{probe.Validation.Status} ({probe.Validation.Score}/{CodexHomeValidation.MaxScore})";

            ProbeRows.Add(new InfoRow(
                $"{(int)probe.Source}. {DescribeSource(probe.Source)}",
                $"{status} — {probe.Note}"));
        }

        if (!result.Found || result.Installation is null)
        {
            var reasons = new List<string>();
            foreach (CodexHomeProbe probe in result.Located.Probes)
            {
                if (probe.Validation is null)
                {
                    reasons.Add($"· {DescribeSource(probe.Source)}: {probe.Note}");
                    continue;
                }

                foreach (string reason in probe.Validation.Reasons)
                {
                    reasons.Add($"· {DescribeSource(probe.Source)}: {reason}");
                }
            }

            ShowFailure(reasons.Count == 0
                ? "확인할 수 있는 후보 경로가 없었습니다."
                : string.Join(Environment.NewLine, reasons));

            _logger.Info($"Codex Home 탐지 실패. 후보 {result.Located.Probes.Count}건 검사.");
            ClearCatalog();
            return;
        }

        CodexInstallationInfo info = result.Installation;

        // Phase 4: 다른 Codex Home으로 바뀌었으면 이전 선택을 섞지 않는다. 같은 Home을 "다시 확인"한
        // 것이면 여기서는 아무것도 하지 않고, 아래 ApplyCatalog에서 사라진 ThreadId만 정리한다.
        _selection.ClearIfDifferentHome(_lastHome, info.Home);
        _lastHome = info.Home;

        IsConnected = true;
        StatusGlyph = "●";
        StatusText = info.Validation.Status == CodexHomeStatus.Valid
            ? "Codex 연결됨"
            : "Codex 연결됨 (일부 구성 요소 없음)";
        HomePath = info.HomeDisplayPath;
        DetailText = null;

        WarningText = info.Validation.Status == CodexHomeStatus.Valid
            ? null
            : string.Join(" ", info.Validation.Reasons);

        if (info.StateDatabaseOpenMode == SqliteOpenMode.ReadOnlyImmutableFallback)
        {
            WarningText = Combine(
                WarningText,
                "state DB를 immutable 모드로 읽었습니다. Codex가 실행 중이면 최신 변경이 반영되지 않을 수 있습니다.");
        }

        if (info.StateDatabaseError is not null)
        {
            WarningText = Combine(WarningText, $"state DB 경고: {info.StateDatabaseError}");
        }

        Rows.Add(new InfoRow("탐지 경로", DescribeSource(info.Source)));
        Rows.Add(new InfoRow("Codex Desktop", info.CodexDesktopVersion ?? NotAvailable));
        Rows.Add(new InfoRow("Codex CLI", info.CodexCliVersion ?? NotAvailable));
        Rows.Add(new InfoRow("CLI 실행 파일", DescribeCli(info)));
        Rows.Add(new InfoRow("State DB", DescribeStateDb(info)));
        Rows.Add(new InfoRow("Migration", info.LatestMigrationVersion?.ToString(CultureInfo.InvariantCulture) ?? NotAvailable));
        Rows.Add(new InfoRow("Sessions", DescribeCount(info.SessionFileCount, info.CompressedSessionFileCount)));
        Rows.Add(new InfoRow("Archived", DescribeCount(info.ArchivedSessionFileCount, info.CompressedArchivedSessionFileCount)));
        Rows.Add(new InfoRow("Threads (state DB)", DescribeThreads(info)));
        Rows.Add(new InfoRow("session_index.jsonl", info.SessionIndexLineCount is { } lines ? $"{lines}줄" : NotAvailable));
        Rows.Add(new InfoRow("projectsMigrated", DescribeFlag(info.ProjectsMigrated)));
        Rows.Add(new InfoRow("threadAssignmentsMigrated", DescribeFlag(info.ThreadAssignmentsMigrated)));
        Rows.Add(new InfoRow("검증 점수", $"{info.Validation.Status} ({info.Validation.Score}/{CodexHomeValidation.MaxScore})"));
        Rows.Add(new InfoRow("SQLite 열기 모드", DescribeOpenMode(info.StateDatabaseOpenMode)));
        Rows.Add(new InfoRow("Codex 활동 신호", info.ActivitySignals.Count == 0
            ? "없음"
            : string.Join(", ", info.ActivitySignals)));

        CompactSummaryText =
            $"Desktop {info.CodexDesktopVersion ?? NotAvailable} · CLI {info.CodexCliVersion ?? NotAvailable} · " +
            $"Sessions {DescribeCount(info.SessionFileCount, info.CompressedSessionFileCount)} · " +
            $"Threads {DescribeThreads(info)}";

        // Phase 5(Export) manifest에 그대로 쓴다 — 다시 조사하지 않고 여기서 읽은 값을 재사용한다.
        _lastCodexDesktopVersion = info.CodexDesktopVersion;
        _lastCodexCliVersion = info.CodexCliVersion;

        // 로그에는 경로 원문과 개수 이외의 사용자 데이터를 남기지 않는다.
        _logger.Info(
            $"Codex 탐지 성공. source={info.Source} status={info.Validation.Status} " +
            $"score={info.Validation.Score} home={Redact.Path(info.HomeDisplayPath)} " +
            $"stateGen={info.StateGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"migration={info.LatestMigrationVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"cli={info.CodexCliVersion ?? "-"} desktop={info.CodexDesktopVersion ?? "-"} " +
            $"sessions={info.SessionFileCount} zst={info.CompressedSessionFileCount} " +
            $"archived={info.ArchivedSessionFileCount} threads={info.ThreadRowCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"openMode={info.StateDatabaseOpenMode} tam={DescribeFlag(info.ThreadAssignmentsMigrated)}");

        _ = LoadCatalogAsync(info);
    }

    private async Task LoadCatalogAsync(CodexInstallationInfo installation)
    {
        _catalogCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _catalogCancellation = cancellation;

        ProjectNodes.Clear();
        IsCatalogLoading = true;
        CatalogSummaryText = null;

        try
        {
            CodexCatalog catalog = await Task.Run(
                () => CodexCatalogBuilder.Build(installation, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            ApplyCatalog(catalog);
        }
        catch (OperationCanceledException)
        {
            // 새 탐지/폴더 변경으로 취소됨. 이전 결과를 화면에 남기지 않는다.
        }
        catch (Exception ex)
        {
            _logger.Error("대화 카탈로그 생성 중 오류", ex);
            CatalogSummaryText = $"대화 목록을 만드는 중 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            if (ReferenceEquals(_catalogCancellation, cancellation))
            {
                IsCatalogLoading = false;
            }
        }
    }

    private void ApplyCatalog(CodexCatalog catalog)
    {
        _lastCatalog = catalog;
        SelectConversation(null);

        ProjectNodes.Clear();
        foreach (ProjectEntry project in catalog.Projects)
        {
            IReadOnlyList<ConversationNodeViewModel> conversations = project.Conversations
                .Select(c => new ConversationNodeViewModel(c.Title.Text, c.ThreadId, _selection))
                .ToList();

            ProjectNodes.Add(new ProjectNodeViewModel(project.DisplayName, project.IsUncategorized, conversations, _selection));
        }

        // Phase 4: 같은 Codex Home을 "다시 확인"해서 카탈로그를 재구축한 경우, 더 이상 존재하지 않게 된
        // ThreadId만 선택에서 제거하고 나머지는 그대로 둔다(다른 Home으로 바뀌었을 때의 전체 초기화는
        // 위 Apply()의 ClearIfDifferentHome이 이미 처리했다).
        _selection.RetainOnly(ProjectNodes.SelectMany(p => p.Conversations).Select(c => c.ThreadId));

        CatalogSummaryText =
            $"프로젝트 {catalog.Projects.Count}개 · 사용자 대화 {catalog.UserConversationCount}개 · " +
            $"rollout 파일 {catalog.Stats.RolloutFileCount}개 · " +
            $"스캔 {catalog.Stats.JsonlScanDuration.TotalMilliseconds:F0}ms / " +
            $"전체 {catalog.Stats.TotalBuildDuration.TotalMilliseconds:F0}ms";

        // 카탈로그 경고에는 사용자 원문이 없다(파일명/thread ID 수준의 진단 문구뿐이다). 개수만 로그에 남긴다.
        _logger.Info(
            $"카탈로그 생성 완료. projects={catalog.Projects.Count} userThreads={catalog.UserConversationCount} " +
            $"allThreads={catalog.AllConversations.Count} rolloutFiles={catalog.Stats.RolloutFileCount} " +
            $"scanMs={catalog.Stats.JsonlScanDuration.TotalMilliseconds:F0} " +
            $"totalMs={catalog.Stats.TotalBuildDuration.TotalMilliseconds:F0} warnings={catalog.Warnings.Count}");

        OnSelectionChanged(); // TotalConversationCount가 바뀌었을 수 있으므로 요약/커맨드 상태를 갱신한다.
    }

    private void ClearCatalog()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation = null;
        _lastCatalog = null;
        SelectConversation(null);
        ProjectNodes.Clear();
        IsCatalogLoading = false;
        CatalogSummaryText = null;

        // Phase 4: 탐지 자체가 실패한 상태다. 유효한 카탈로그가 없으므로 선택도 비워 둔다. 다음 성공적인
        // 탐지는(설령 같은 Home이라도) ClearIfDifferentHome이 "이전 Home 없음"으로 보고 다시 초기화한다.
        _lastHome = null;
        _selection.Clear();
        OnSelectionChanged();
    }

    /// <summary>
    /// Phase 5(Export)가 UI ViewModel을 직접 해석하지 않고도 쓸 수 있는, 현재 선택의 불변 스냅샷.
    /// </summary>
    public IReadOnlySet<string> GetSelectedThreadIdsSnapshot() => _selection.Snapshot();

    /// <summary>
    /// 선택된 대화를 <c>.codexbackup</c>으로 내보낸다. core 파이프라인(<see cref="ExportPlanBuilder"/> →
    /// <see cref="ManifestBuilder"/> → <see cref="BackupWriter"/>)을 그대로 호출할 뿐, UI는 결과를
    /// 요약해서 보여주기만 한다 — 대화 원문이나 개인 절대경로는 화면/로그에 남기지 않는다.
    /// </summary>
    private async Task ExportAsync()
    {
        if (_lastCatalog is not { } catalog)
        {
            ExportStatusText = "카탈로그가 아직 준비되지 않았습니다.";
            return;
        }

        IReadOnlySet<string> selectedThreadIds = GetSelectedThreadIdsSnapshot();
        if (selectedThreadIds.Count == 0)
        {
            return;
        }

        // 백업 파일 이름에 대화 제목 원문을 쓰지 않는다(요구사항 15) — 시각만으로 만든다.
        string defaultName = $"codex-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        string? destination = _exportFilePicker(defaultName);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        IsExporting = true;
        ExportStatusText = "내보내는 중…";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            BackupWriter.WriteResult result = await Task.Run(
                () =>
                {
                    ExportPlan plan = ExportPlanBuilder.Build(catalog, selectedThreadIds, cancellation.Token);
                    BackupManifest manifest = ManifestBuilder.Build(
                        plan, _lastCodexDesktopVersion, _lastCodexCliVersion, DateTimeOffset.UtcNow);
                    return BackupWriter.Write(plan, manifest, destination, overwrite: true, cancellationToken: cancellation.Token);
                },
                cancellation.Token).ConfigureAwait(true);

            stopwatch.Stop();

            if (result.Success)
            {
                long sizeBytes = new FileInfo(destination).Length;
                ExportStatusText =
                    $"내보내기 완료 — 선택 대화 {selectedThreadIds.Count}개, {sizeBytes / 1024.0 / 1024.0:F1} MB, " +
                    $"{stopwatch.ElapsedMilliseconds}ms" +
                    (result.Warnings.Count > 0 ? $" (경고 {result.Warnings.Count}건)" : string.Empty);
                _logger.Info(
                    $"Export 완료. selected={selectedThreadIds.Count} sizeBytes={sizeBytes} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} warnings={result.Warnings.Count}");
            }
            else
            {
                ExportStatusText = $"내보내기 실패: {result.FailureReason}";
                _logger.Warning($"Export 실패. reason={result.FailureReason}");
            }
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "내보내기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            _logger.Error("Export 중 오류", ex);
            ExportStatusText = $"내보내는 중 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                IsExporting = false;
                _exportCancellation = null;
            }
        }
    }

    /// <summary>
    /// 진행 중인 Export를 취소한다. 실제 취소 처리(temp 삭제 등)는 core
    /// (<see cref="BackupWriter.Write"/>)가 <see cref="CancellationToken"/>을 보고 직접 한다 —
    /// 여기서는 신호만 보낸다.
    /// </summary>
    private void CancelExport() => _exportCancellation?.Cancel();

    /// <summary>Import Preview를 닫고 관련 상태를 전부 비운다(override 상태 포함).</summary>
    private void CloseImportPreview()
    {
        CurrentImportPreview = null;
        _lastImportPreviewDomain = null;
        _lastImportBackupFilePath = null;
        _currentImportPlan = null;
        ImportPlanSummaryText = null;
        ApplyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// <c>.codexbackup</c> 파일을 선택해 Import Preview를 만든다(Phase 6). core
    /// (<see cref="ImportPreviewBuilder"/>)를 그대로 호출할 뿐이다 — ZIP/rollout 비교 로직을 여기서
    /// 직접 만들지 않는다. Codex 파일에는 어떤 것도 쓰지 않는다(판정/미리보기까지만).
    /// </summary>
    private async Task ImportPreviewAsync()
    {
        string? path = _importFilePicker();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_lastCatalog is not { } catalog)
        {
            ImportStatusText = "카탈로그가 아직 준비되지 않았습니다.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _importPreviewCancellation = cancellation;
        IsImportPreviewLoading = true;
        ImportStatusText = "백업 파일을 확인하는 중…";
        CurrentImportPreview = null;
        _lastImportPreviewDomain = null;
        _lastImportBackupFilePath = path;

        // 새 Preview를 "실제로 시작하는" 이 시점에 예전 frozen Plan을 즉시 무효화한다 — 새 backup을
        // 선택한 순간 기존 Plan은 더 이상 Apply 후보가 아니다. 이후 빌드가 취소/실패/예외로 끝나도
        // 예전 Plan이 남아있으면 안 되므로, 여기서 미리 비워 두고 UpdateImportPlanSummary가 성공
        // 시에만 다시 채운다(OpenFileDialog 자체를 취소한 경우는 위에서 이미 return해 여기 도달하지
        // 않으므로 기존 상태가 그대로 유지된다).
        // 새 Preview를 "실제로 시작하는" 이 시점에 예전 frozen Plan을 즉시 무효화한다 — 새 backup을
        // 선택한 순간 기존 Plan은 더 이상 Apply 후보가 아니다. 이후 빌드가 취소/실패/예외로 끝나도
        // 예전 Plan이 남아있으면 안 되므로, 여기서 미리 비워 두고 UpdateImportPlanSummary가 성공
        // 시에만 다시 채운다(OpenFileDialog 자체를 취소한 경우는 위에서 이미 return해 여기 도달하지
        // 않으므로 기존 상태가 그대로 유지된다).
        _currentImportPlan = null;
        ImportPlanSummaryText = null;
        ApplyCommand.RaiseCanExecuteChanged();

        try
        {
            ImportPreview preview = await Task.Run(
                () => ImportPreviewBuilder.Build(path, catalog, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _lastImportPreviewDomain = preview;
            CurrentImportPreview = new ImportPreviewViewModel(preview, RequestProjectPathOverride);
            UpdateImportPlanSummary(preview);

            if (preview.Success)
            {
                int total = preview.Projects.Sum(p => p.Conversations.Count);
                ImportStatusText = $"백업 확인 완료 — 대화 {total}개.";
                _logger.Info(
                    $"Import Preview 완료. projects={preview.Projects.Count} conversations={total} " +
                    $"dependencyOnly={preview.DependencyOnlyConversations.Count} warnings={preview.Warnings.Count}");
            }
            else
            {
                ImportStatusText = "이 백업 파일을 사용할 수 없습니다.";
                _logger.Warning($"Import Preview 검증 실패. errors={preview.ValidationErrors.Count}");
            }
        }
        catch (OperationCanceledException)
        {
            ImportStatusText = "Import Preview를 취소했습니다.";
        }
        catch (Exception ex)
        {
            _logger.Error("Import Preview 중 오류", ex);
            ImportStatusText = $"백업을 확인하는 중 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            if (ReferenceEquals(_importPreviewCancellation, cancellation))
            {
                IsImportPreviewLoading = false;
                _importPreviewCancellation = null;
            }
        }
    }

    /// <summary>
    /// 사용자가 프로젝트 폴더를 직접 재지정한다(Phase 06_01, 요구사항 3). 실제 재지정 상태는
    /// <see cref="ImportPreviewBuilder.ApplyManualProjectPathOverride"/>가 <see cref="_lastImportPreviewDomain"/>에
    /// 만든 새 <see cref="ImportPreview"/>로 저장된다 — View code-behind에는 아무 상태도 두지 않는다.
    /// Codex에는 여전히 아무것도 쓰지 않는다.
    /// </summary>
    private void RequestProjectPathOverride(string? projectId)
    {
        if (_isApplying)
        {
            // Apply 진행 중에는 경로 재지정을 막는다(요구사항 15) — pin된 ImportPlan을 흔들 수 있는
            // 조작이므로 버튼 자체가 항상 보이더라도(자식 ViewModel의 RelayCommand는 CanExecute를
            // 다시 묻지 않는 고정 델리게이트라 여기서 직접 막는다) 여기서 차단한다.
            return;
        }

        if (_lastImportPreviewDomain is not { } currentPreview)
        {
            return;
        }

        string? selected = _projectPathPicker();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        try
        {
            ImportPreview updated = ImportPreviewBuilder.ApplyManualProjectPathOverride(currentPreview, projectId, selected);
            _lastImportPreviewDomain = updated;
            CurrentImportPreview = new ImportPreviewViewModel(updated, RequestProjectPathOverride);
            UpdateImportPlanSummary(updated);
            ImportStatusText = "프로젝트 경로를 재지정했습니다.";
            _logger.Info("Import Preview 프로젝트 경로 수동 재지정 완료.");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or InvalidOperationException)
        {
            // 사용자가 잘못된 경로를 고르거나(존재하지 않는 폴더), 재지정할 수 없는 프로젝트("기타
            // 대화")를 시도한 경우. Codex에는 아무 영향이 없으므로 문구만 보여주고 되돌린다.
            ImportStatusText = $"경로를 재지정할 수 없습니다: {ex.Message}";
            _logger.Warning($"Import Preview 프로젝트 경로 재지정 실패. reason={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Preview를 <see cref="ImportPlan"/>으로 freeze해 <see cref="_currentImportPlan"/>에 보존하고,
    /// 문구를 갱신한다(요구사항 4/5). <b>이 메서드가 <see cref="ImportPlanBuilder.Build"/>를 부르는
    /// 유일한 곳이어야 한다</b> — 다른 곳(예: 문구를 다시 보여줘야 할 때)에서 Plan을 다시 만들지
    /// 않고 항상 <see cref="_currentImportPlan"/>을 그대로 읽어야 한다. Plan을 "만들기"만 할 뿐
    /// 여기서도 아무것도 적용하지 않는다.
    /// </summary>
    /// <remarks>
    /// (Phase 06_03) <see cref="ImportPlanBuilder.Build"/>가 <c>null</c>을 돌려주는 경우는 두 가지를
    /// 구분하지 않는다 — Preview 검증 실패, 그리고 Preview 이후 backup 파일이 바뀐 경우
    /// (<see cref="ImportPreview.SourceBackupIdentity"/> 불일치) 전부 "Plan을 지금 신뢰할 수 없다"는
    /// 같은 결론이므로, 문구도 재-Preview를 안내하는 것으로 충분하다.
    /// </remarks>
    private void UpdateImportPlanSummary(ImportPreview preview)
    {
        if (!preview.Success || _lastImportBackupFilePath is not { } backupPath)
        {
            _currentImportPlan = null;
            ImportPlanSummaryText = null;
            ApplyCommand.RaiseCanExecuteChanged();
            return;
        }

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        _currentImportPlan = plan;
        ApplyCommand.RaiseCanExecuteChanged();

        if (plan is null)
        {
            ImportPlanSummaryText = "가져오기 계획을 만들 수 없습니다 — 백업 파일이 미리보기 이후 변경되었을 수 있습니다. 다시 불러와 주세요.";
            return;
        }

        if (!plan.IsApplyReady)
        {
            int blockedCount = plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.Blocked);
            int divergedCount = plan.Conversations.Count(c => c.PlannedAction == ImportPlannedAction.RequiresDecision);
            ImportPlanSummaryText = $"충돌 있음 — 확인 불가 {blockedCount}건, 분기 충돌 {divergedCount}건(사용자 결정 필요).";
            return;
        }

        // "Apply 준비 완료"라고 말하지 않는다 — Diverged/Unverifiable이 없다는 뜻일 뿐, backup/로컬
        // Codex가 지금(Apply 직전) 이 상태와 같은지는 Phase 7의 fresh preflight만 알 수 있다.
        ImportPlanSummaryText = "가져오기 계획 생성 완료 — 충돌 없음(적용 전 최종 검사가 필요합니다).";
    }

    /// <summary>
    /// Apply 전 항상 보여줘야 하는 알려진 제약 사항(Phase 07_01 요구사항 16). 이 backup/현재 상태에
    /// 실제로 해당하는지와 무관하게, 사용자가 "성공했다"고 오해하지 않도록 항상 함께 표시한다.
    /// </summary>
    public static string KnownLimitationsText =>
        "알려진 제약: Codex Desktop 사이드바에 프로젝트별로 정확히 표시되는지는 아직 별도 검증되지 " +
        "않았습니다. local_image 첨부는 복원되지 않습니다. 새 프로젝트 자동 생성은 지원하지 않습니다. " +
        ".jsonl.zst로 압축된 대화의 이어받기(Update)는 지원하지 않습니다. 분기(Diverged)된 대화는 자동" +
        "적용하지 않습니다.";

    /// <summary>
    /// frozen된 <see cref="_currentImportPlan"/>을 실제로 적용한다(Phase 07_01). 여기서는
    /// <see cref="ImportPlan"/>을 다시 해석하거나 Preflight를 다시 판단하지 않는다 — 확인 대화상자를
    /// 띄우고, 다른 조작을 막고, <see cref="RestoreExecutor"/>의 결과를 그대로 보여줄 뿐이다.
    /// </summary>
    private async Task ApplyAsync()
    {
        if (_currentImportPlan is not { } plan)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HomePath))
        {
            ApplyStatusText = "Codex Home 경로를 확인할 수 없습니다.";
            return;
        }

        bool confirmed = _confirmDialog(
            "백업 내용을 Codex에 적용합니다.\n적용 전에 현재 상태의 복구용 Snapshot을 생성합니다.\n" +
            "Codex가 완전히 종료되어 있어야 합니다.\n계속하시겠습니까?",
            "적용 확인");
        if (!confirmed)
        {
            return;
        }

        string codexHomePath = HomePath;
        var cancellation = new CancellationTokenSource();
        _applyCancellation = cancellation;
        IsApplying = true;
        ApplyStatusText = "안전성 확인 중…";

        // RestoreExecutor.Apply의 onStatusChanged 콜백은 Task.Run 내부(백그라운드 스레드)에서
        // 그대로 호출된다 — WPF 바인딩 대상 속성은 UI 스레드에서만 갱신해야 하므로, 호출 스레드(UI
        // 스레드)의 SynchronizationContext로 다시 넘겨서(Post) 반영한다. ViewModel 자체는 WPF를
        // 직접 참조하지 않는다(System.Threading만 사용).
        SynchronizationContext? uiContext = SynchronizationContext.Current;

        try
        {
            RestoreResult result = await Task.Run(
                () => RestoreExecutor.Apply(
                    plan,
                    codexHomePath,
                    onStatusChanged: status => ReportApplyStatus(status, uiContext),
                    cancellationToken: cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            ApplyStatusText = DescribeApplyResult(result);

            switch (result.Outcome)
            {
                case RestoreOutcome.Succeeded:
                case RestoreOutcome.NothingToDo:
                    // 적용된(또는 더 이상 적용할 것이 없는) Plan을 다시 Apply할 수 없게 무효화한다 —
                    // 다시 적용하려면 새 Import Preview부터 시작해야 한다.
                    _currentImportPlan = null;
                    ApplyCommand.RaiseCanExecuteChanged();
                    _logger.Info($"Apply 완료. outcome={result.Outcome} snapshotId={result.SnapshotId}");
                    break;
                default:
                    _logger.Warning($"Apply 실패/중단. outcome={result.Outcome} snapshotId={result.SnapshotId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            // RestoreExecutor.Apply는 Snapshot 이후의 모든 예외(취소 포함)를 스스로 Rollback 처리해
            // RestoreResult로 돌려준다 — 여기까지 예외가 올라온다면 Rollback 경로 자체에 들어가기
            // 전(예: fresh catalog 생성 실패)의 예기치 않은 오류다.
            _logger.Error("Apply 중 예기치 않은 오류", ex);
            ApplyStatusText = $"적용 중 예기치 않은 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            if (ReferenceEquals(_applyCancellation, cancellation))
            {
                IsApplying = false;
                _applyCancellation = null;
            }
        }
    }

    /// <summary>
    /// RestoreExecutor의 진행 콜백(백그라운드 스레드에서 호출됨)을 UI 스레드로 옮겨 반영한다.
    /// <paramref name="uiContext"/>가 없으면(예: 테스트에서 동기 컨텍스트 없이 실행) 그냥 직접 쓴다.
    /// </summary>
    private void ReportApplyStatus(string status, SynchronizationContext? uiContext)
    {
        if (uiContext is null)
        {
            ApplyStatusText = status;
            return;
        }

        uiContext.Post(_ => ApplyStatusText = status, null);
    }

    /// <summary>진행 중인 Apply를 취소한다. Snapshot 이전이면 그냥 중단되고, 이후면 Rollback된다.</summary>
    private void CancelApply() => _applyCancellation?.Cancel();

    private static string DescribeApplyResult(RestoreResult result) => result.Outcome switch
    {
        RestoreOutcome.Succeeded => "적용 완료.",
        RestoreOutcome.NothingToDo => "적용할 변경 사항이 없습니다(이미 최신 상태입니다).",
        RestoreOutcome.Cancelled => "적용을 취소하여 이전 상태로 복원했습니다.",
        RestoreOutcome.RolledBack => "적용 중 오류가 발생해 이전 상태로 복원했습니다.",
        RestoreOutcome.RollbackFailedCritical => $"CRITICAL: {result.Message}",
        RestoreOutcome.NotReady => result.Message,
        _ => result.Message,
    };

    /// <summary>
    /// 테스트 전용 접근자(<c>InternalsVisibleTo</c>로 App.Tests에만 노출). 대량 선택/해제 시
    /// <see cref="ConversationSelectionState.Changed"/> 발생 횟수를 직접 검증하기 위해 쓴다.
    /// 공개 API 표면을 넓히지 않으면서도 핵심 selection 로직을 WPF 없이 테스트할 수 있게 한다.
    /// </summary>
    internal ConversationSelectionState Selection => _selection;

    /// <summary>
    /// 카탈로그 전체 선택. <see cref="ProjectNodeViewModel.SetAllSelected"/>를 프로젝트마다 호출하지
    /// 않는다 — 그러면 프로젝트 수(N)만큼 <c>_selection</c>에 별도 bulk mutation이 걸려
    /// <see cref="ConversationSelectionState.Changed"/>도 최대 N번 발생할 수 있다. 대신 전체
    /// ThreadId를 한 번에 모아 <c>_selection</c>은 딱 한 번만 바꾸고, 화면 갱신 알림만 노드마다
    /// 한 번씩 보낸다(요구사항 10: 대규모 카탈로그에서도 재계산이 반복되지 않아야 한다).
    /// </summary>
    private void SelectAllConversations()
    {
        _selection.SelectMany(ProjectNodes.SelectMany(p => p.Conversations).Select(c => c.ThreadId));
        NotifyAllNodesSelectionChanged();
    }

    /// <summary>카탈로그 전체 선택 해제. 위와 같은 이유로 <c>_selection.Clear()</c>를 한 번만 호출한다.</summary>
    private void ClearAllSelections()
    {
        _selection.Clear();
        NotifyAllNodesSelectionChanged();
    }

    /// <summary>모든 노드의 <c>IsSelected</c> 바인딩을 다시 읽으라고 알린다(값 자체는 저장하지 않는다).</summary>
    private void NotifyAllNodesSelectionChanged()
    {
        foreach (ProjectNodeViewModel project in ProjectNodes)
        {
            foreach (ConversationNodeViewModel conversation in project.Conversations)
            {
                conversation.NotifySelectionChanged();
            }

            project.NotifySelectionChanged();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedConversationCount));
        OnPropertyChanged(nameof(TotalConversationCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        SelectAllConversationsCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void ShowFailure(string detail)
    {
        IsConnected = false;
        StatusGlyph = "○";
        StatusText = "Codex를 찾을 수 없습니다.";
        HomePath = string.Empty;
        WarningText = null;
        DetailText = detail;
        CompactSummaryText = null;
    }

    private static string Combine(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing + " " + addition;

    private static string DescribeSource(CodexHomeSource source) => source switch
    {
        CodexHomeSource.CodexHomeEnvironmentVariable => "CODEX_HOME 환경변수",
        CodexHomeSource.UserProfileDotCodex => @"%USERPROFILE%\.codex",
        CodexHomeSource.SavedManualPath => "저장된 수동 경로",
        CodexHomeSource.UserSelected => "사용자 지정 폴더",
        _ => source.ToString(),
    };

    private static string DescribeStateDb(CodexInstallationInfo info)
    {
        if (info.ActiveStateDatabase is null)
        {
            return NotAvailable;
        }

        string generation = info.StateGeneration is { } g
            ? $"Generation {g.ToString(CultureInfo.InvariantCulture)}"
            : "Generation 미확인";

        string others = info.StateDatabases.Count > 1
            ? $" (총 {info.StateDatabases.Count}개 발견)"
            : string.Empty;

        return $"{generation} — {info.ActiveStateDatabase.FileName}{others}";
    }

    private static string DescribeCount(int jsonl, int compressed)
        => compressed == 0
            ? jsonl.ToString(CultureInfo.InvariantCulture)
            : $"{jsonl.ToString(CultureInfo.InvariantCulture)} (+ 압축 {compressed.ToString(CultureInfo.InvariantCulture)}개)";

    private static string DescribeThreads(CodexInstallationInfo info)
    {
        if (info.ThreadRowCount is not { } total)
        {
            return NotAvailable;
        }

        string archived = info.ArchivedThreadRowCount is { } a
            ? $", archived {a.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"{total.ToString(CultureInfo.InvariantCulture)}행{archived}";
    }

    private static string DescribeCli(CodexInstallationInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.CliExecutablePath))
        {
            return NotAvailable;
        }

        string existence = info.CliExecutableExists switch
        {
            true => "존재",
            false => "경로에 파일 없음",
            null => "확인 불가",
        };

        return $"{info.CliExecutablePath} ({existence})";
    }

    private static string DescribeFlag(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => NotAvailable,
    };

    private static string DescribeOpenMode(SqliteOpenMode mode) => mode switch
    {
        SqliteOpenMode.ReadOnly => "Mode=ReadOnly, Pooling=False",
        SqliteOpenMode.ReadOnlyImmutableFallback => "Mode=ReadOnly + immutable=1 (폴백)",
        SqliteOpenMode.Failed => "열기 실패",
        _ => "열지 않음",
    };
}
