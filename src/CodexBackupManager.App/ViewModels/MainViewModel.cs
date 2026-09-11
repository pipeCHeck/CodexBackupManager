using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Diagnostics;

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

    private bool _isBusy;
    private bool _isConnected;
    private string _statusText = "Codex 확인 중…";
    private string _statusGlyph = "…";
    private string _homePath = string.Empty;
    private string? _warningText;
    private string? _detailText;

    private bool _isCatalogLoading;
    private string? _catalogSummaryText;
    private CancellationTokenSource? _catalogCancellation;

    /// <summary>생성자.</summary>
    /// <param name="detection">탐지 서비스.</param>
    /// <param name="settings">설정 저장소.</param>
    /// <param name="logger">로거.</param>
    /// <param name="folderPicker">폴더 선택 대화상자. 취소 시 <c>null</c>을 반환해야 한다.</param>
    public MainViewModel(
        CodexDetectionService detection,
        SettingsStore settings,
        FileLogger logger,
        Func<string?> folderPicker)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(folderPicker);

        _detection = detection;
        _settings = settings;
        _logger = logger;
        _folderPicker = folderPicker;

        // UI 스레드에서 시작하고 결과를 기다리지 않는다. 예외는 각 메서드 내부에서 처리한다.
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        ChangeFolderCommand = new RelayCommand(() => _ = ChangeFolderAsync(), () => !IsBusy);
    }

    /// <summary>다시 탐지.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Codex 폴더 직접 선택.</summary>
    public RelayCommand ChangeFolderCommand { get; }

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
        ProjectNodes.Clear();
        foreach (ProjectEntry project in catalog.Projects)
        {
            ProjectNodes.Add(new ProjectNodeViewModel
            {
                DisplayName = project.DisplayName,
                IsUncategorized = project.IsUncategorized,
                Conversations = project.Conversations
                    .Select(c => new ConversationNodeViewModel { Title = c.Title.Text, ThreadId = c.ThreadId })
                    .ToList(),
            });
        }

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
    }

    private void ClearCatalog()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation = null;
        ProjectNodes.Clear();
        IsCatalogLoading = false;
        CatalogSummaryText = null;
    }

    private void ShowFailure(string detail)
    {
        IsConnected = false;
        StatusGlyph = "○";
        StatusText = "Codex를 찾을 수 없습니다.";
        HomePath = string.Empty;
        WarningText = null;
        DetailText = detail;
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
