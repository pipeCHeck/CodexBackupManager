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
    public MainViewModel(
        CodexDetectionService detection,
        SettingsStore settings,
        FileLogger logger,
        Func<string?> folderPicker,
        Func<string, string?>? exportFilePicker = null,
        Func<string?>? importFilePicker = null,
        Func<string?>? projectPathPicker = null)
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

        // UI 스레드에서 시작하고 결과를 기다리지 않는다. 예외는 각 메서드 내부에서 처리한다.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        ChangeFolderCommand = new RelayCommand(() => _ = ChangeFolderAsync(), () => !IsBusy);
        SelectAllConversationsCommand = new RelayCommand(SelectAllConversations, () => TotalConversationCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearAllSelections, () => HasSelection);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => HasSelection && !IsExporting);
        CancelExportCommand = new RelayCommand(CancelExport, () => IsExporting);
        ImportPreviewCommand = new RelayCommand(() => _ = ImportPreviewAsync(), () => !IsImportPreviewLoading);
        CloseImportPreviewCommand = new RelayCommand(CloseImportPreview);

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
            return;
        }

        ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
        _currentImportPlan = plan;

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
