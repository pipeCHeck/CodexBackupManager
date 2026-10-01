using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Restore;
using CodexBackupManager.Restore.Undo;

namespace CodexBackupManager.App.ViewModels;

/// <summary>(Phase 9_4-02) 기록 목록의 한 줄.</summary>
public sealed class ImportHistoryRowViewModel
{
    internal ImportHistoryRowViewModel(ImportHistoryEntry entry)
    {
        Entry = entry;
    }

    /// <summary>기록.</summary>
    public ImportHistoryEntry Entry { get; }

    /// <summary>날짜·시각(이 PC 시간).</summary>
    public string DateText => Entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>종류.</summary>
    public string KindText => Entry.Kind switch
    {
        ImportHistoryKind.SidebarRepair => "사이드바 보정",
        ImportHistoryKind.Undo => "되돌리기",
        _ => "가져오기",
    };

    /// <summary>백업 이름(9_4 이전 기록은 알 수 없음).</summary>
    public string BackupText => Entry.Summary?.BackupFileName ?? "—";

    /// <summary>결과.</summary>
    public string ResultText => Entry.JournalUnreadable
        ? "기록 손상"
        : Entry.State switch
        {
            RestoreTransactionState.Completed => "완료",
            RestoreTransactionState.Undone => "되돌림",
            RestoreTransactionState.RolledBack => "실패(원래대로 둠)",
            RestoreTransactionState.Prepared => "시작 전 중단",
            RestoreTransactionState.Applying or RestoreTransactionState.Undoing => "미완료",
            _ => "알 수 없음",
        };

    /// <summary>개수(9_4 이후 가져오기만).</summary>
    public string? CountsText => Entry.Summary is { } s
        ? $"새로 {s.ImportedCount} · 이어받음 {s.UpdatedCount} · 옮김 {s.RelinkedCount} · 새 프로젝트 {s.CreatedProjectCount}"
        : null;

    /// <summary>되돌리기 가능 여부(기록 단위).</summary>
    public string AvailabilityText => Entry.Availability == UndoUnavailableReason.None
        ? "되돌리기 가능(실행 전에 다시 확인합니다)"
        : ImportTexts.UndoUnavailableText(Entry.Availability);

    /// <summary>화면 읽기 프로그램용 이름.</summary>
    public string AutomationName => $"{DateText} {KindText} {BackupText} {ResultText}";
}

/// <summary>
/// (Phase 9_4-02/05) "가져오기 기록" 화면: 현재 Codex Home의 기록(최신순), 고른 기록의 대상 대화·새 프로젝트, [되돌리기], [선택 삭제], [오래된 기록 정리].
/// 목록을 만드는 동안 Codex Home과 Snapshot 폴더에 쓰지 않는다. 쓰기는 확인을 받은 뒤 Restore 계층 서비스가 한다.
/// 로그에는 개수와 결과 enum만 남긴다.
/// </summary>
public sealed class ImportHistoryViewModel : ObservableObject
{
    private readonly string _codexHomePath;
    private readonly string _snapshotRoot;
    private readonly FileLogger _logger;
    private readonly Func<string, string, bool> _confirm;
    private readonly Func<string, string?> _titleOf;
    private readonly CodexProcessGuard.RunningProcessLister _processLister;
    private readonly Action _onDataChanged;
    private readonly Func<DateTimeOffset> _now;
    private ImportHistoryRowViewModel? _selected;
    private UndoAssessment? _assessment;
    private string? _detailText;
    private string? _statusText;
    private bool _isBusy;

    /// <summary>생성자.</summary>
    /// <param name="codexHomePath">현재 Codex Home.</param>
    /// <param name="snapshotRoot">Snapshot 루트.</param>
    /// <param name="logger">로거.</param>
    /// <param name="confirm">(메시지, 제목) → 확인.</param>
    /// <param name="titleOf">thread ID → 현재 카탈로그의 제목(모르면 <c>null</c>).</param>
    /// <param name="processLister">Codex 실행 확인용 프로세스 목록.</param>
    /// <param name="onDataChanged">되돌리기로 Codex 데이터가 바뀌었을 때(메인 목록 새로 읽기).</param>
    /// <param name="now">지금 시각(정리 기준).</param>
    public ImportHistoryViewModel(
        string codexHomePath,
        string snapshotRoot,
        FileLogger logger,
        Func<string, string, bool> confirm,
        Func<string, string?> titleOf,
        CodexProcessGuard.RunningProcessLister processLister,
        Action onDataChanged,
        Func<DateTimeOffset>? now = null)
    {
        _codexHomePath = codexHomePath;
        _snapshotRoot = snapshotRoot;
        _logger = logger;
        _confirm = confirm;
        _titleOf = titleOf;
        _processLister = processLister;
        _onDataChanged = onDataChanged;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        UndoCommand = new RelayCommand(() => _ = UndoSelectedAsync(), () => CanUndo);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => _selected is { Entry.IsInProgress: false } && !_isBusy);
        CleanupCommand = new RelayCommand(Cleanup, () => !_isBusy);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        Refresh();
    }

    /// <summary>창을 닫아 달라는 요청.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>기록(최신순).</summary>
    public ObservableCollection<ImportHistoryRowViewModel> Entries { get; } = [];

    /// <summary>기록이 없는지.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>고른 기록.</summary>
    public ImportHistoryRowViewModel? SelectedEntry
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                SelectionTask = LoadDetailAsync();
            }
        }
    }

    /// <summary>마지막 상세 불러오기(테스트가 기다릴 수 있게 둔다).</summary>
    internal Task? SelectionTask { get; private set; }

    /// <summary>고른 기록의 대상 대화(현재 카탈로그의 제목).</summary>
    public ObservableCollection<string> DetailConversations { get; } = [];

    /// <summary>고른 기록이 새로 만든 프로젝트.</summary>
    public ObservableCollection<string> DetailProjects { get; } = [];

    /// <summary>고른 기록의 되돌리기 판정·사유.</summary>
    public string? DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    /// <summary>작업 결과 문구.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>[되돌리기]를 누를 수 있는지.</summary>
    public bool CanUndo => !_isBusy && _assessment is { CanUndo: true };

    /// <summary>[되돌리기].</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>[선택 삭제].</summary>
    public RelayCommand DeleteSelectedCommand { get; }

    /// <summary>[30일 지난 되돌리기 불가 기록 정리].</summary>
    public RelayCommand CleanupCommand { get; }

    /// <summary>[닫기].</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>목록을 다시 읽는다(읽기 전용).</summary>
    public void Refresh()
    {
        string? selectedId = _selected?.Entry.SnapshotId;
        Entries.Clear();
        try
        {
            foreach (ImportHistoryEntry entry in ImportHistoryService.List(_snapshotRoot, _codexHomePath))
            {
                Entries.Add(new ImportHistoryRowViewModel(entry));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"기록을 읽지 못했습니다({ex.GetType().Name}).";
        }

        OnPropertyChanged(nameof(IsEmpty));
        SelectedEntry = Entries.FirstOrDefault(e => e.Entry.SnapshotId == selectedId);
        if (SelectedEntry is null)
        {
            ClearDetail();
        }

        _logger.Info($"가져오기 기록 목록. count={Entries.Count}");
    }

    private void ClearDetail()
    {
        _assessment = null;
        DetailConversations.Clear();
        DetailProjects.Clear();
        DetailText = null;
        RaiseCommands();
    }

    private async Task LoadDetailAsync()
    {
        ClearDetail();
        if (_selected is not { } row)
        {
            return;
        }

        foreach (string id in row.Entry.Summary?.ThreadIds ?? [])
        {
            DetailConversations.Add(_titleOf(id) ?? ImportTexts.FallbackTitle(id));
        }

        if (row.Entry.Availability != UndoUnavailableReason.None)
        {
            DetailText = ImportTexts.UndoUnavailableText(row.Entry.Availability);
            return;
        }

        DetailText = "되돌릴 수 있는지 확인하는 중…";
        string dir = row.Entry.SnapshotDirectory;
        UndoAssessment assessment = await Task.Run(() => ImportUndoService.Assess(_codexHomePath, dir, _snapshotRoot)).ConfigureAwait(true);
        if (!ReferenceEquals(_selected, row))
        {
            return;
        }

        _assessment = assessment;
        foreach (UndoCreatedProject project in assessment.Record?.CreatedProjects ?? [])
        {
            UndoProjectDecision? decision = assessment.Projects.FirstOrDefault(p => p.DbProjectId == project.DbProjectId);
            DetailProjects.Add(decision is { Delete: false }
                ? $"{project.Name} — {ImportTexts.ProjectKeepText(decision.KeepReason)}"
                : project.Name);
        }

        DetailText = ImportTexts.UndoAssessmentText(assessment, Titles(row));
        RaiseCommands();
    }

    private Dictionary<string, string> Titles(ImportHistoryRowViewModel row)
        => (row.Entry.Summary?.ThreadIds ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id => _titleOf(id) ?? ImportTexts.FallbackTitle(id), StringComparer.OrdinalIgnoreCase);

    /// <summary>[되돌리기]: 확인 → 되돌리기 → 결과 문구 → 목록 새로 읽기.</summary>
    internal async Task UndoSelectedAsync()
    {
        if (!CanUndo || _selected is not { } row)
        {
            return;
        }

        if (!_confirm(ImportTexts.UndoConfirmMessage, "가져오기 되돌리기"))
        {
            return;
        }

        SetBusy(true);
        StatusText = "되돌리는 중…";
        string dir = row.Entry.SnapshotDirectory;
        Dictionary<string, string> titles = Titles(row);
        try
        {
            UndoResult result = await Task.Run(() => ImportUndoService.Undo(_codexHomePath, dir, _snapshotRoot, _processLister)).ConfigureAwait(true);
            StatusText = ImportTexts.UndoResultText(result, titles);
            _logger.Info($"기록 화면 되돌리기. outcome={result.Outcome} conversations={result.UndoneConversationCount} deletedProjects={result.DeletedProjectCount} keptProjects={result.KeptProjects.Count}");
            if (result.Outcome == RestoreOutcome.Succeeded)
            {
                _onDataChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("기록 화면 되돌리기 중 예기치 않은 오류", ex);
            StatusText = $"되돌리는 중 예기치 않은 오류가 발생했습니다: {ex.GetType().Name}";
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void DeleteSelected()
    {
        if (_selected is not { } row)
        {
            return;
        }

        if (!_confirm("고른 기록 1개를 지웁니다. 지운 기록은 되돌리기에 쓸 수 없습니다. 계속하시겠습니까?", "기록 삭제"))
        {
            return;
        }

        RunDelete([row.Entry.SnapshotId]);
    }

    private void Cleanup()
    {
        IReadOnlyList<ImportHistoryEntry> candidates = SnapshotRetentionService.FindCleanupCandidates(_snapshotRoot, _codexHomePath, _now());
        if (candidates.Count == 0)
        {
            StatusText = "정리할 기록이 없습니다(30일 이상 지났고 되돌리기를 할 수 없는 기록만 정리합니다).";
            return;
        }

        if (!_confirm($"30일 이상 지났고 되돌리기를 할 수 없는 기록 {candidates.Count}개를 지웁니다. 계속하시겠습니까?", "오래된 기록 정리"))
        {
            return;
        }

        RunDelete(candidates.Select(c => c.SnapshotId).ToList());
    }

    private void RunDelete(IReadOnlyCollection<string> ids)
    {
        SetBusy(true);
        try
        {
            RetentionResult result = SnapshotRetentionService.Delete(_snapshotRoot, _codexHomePath, ids, allowUndoable: false);
            if (result.NeedsUndoableConfirmation)
            {
                // 되돌리기 가능한 기록: 경고 후 다시 확인을 받으면 지운다(정책 9_4-05).
                if (!_confirm("되돌리기를 할 수 있는 기록이 들어 있습니다. 지우면 그 가져오기를 되돌리기를 할 수 없게 됩니다. 그래도 지우시겠습니까?", "되돌리기 가능 기록 삭제"))
                {
                    StatusText = "지우지 않았습니다.";
                    return;
                }

                result = SnapshotRetentionService.Delete(_snapshotRoot, _codexHomePath, ids, allowUndoable: true);
            }

            StatusText = result.Refused.Count == 0
                ? $"기록 {result.DeletedCount}개를 지웠습니다."
                : $"기록 {result.DeletedCount}개를 지웠습니다. {result.Refused.Count}개는 지우지 않았습니다(진행 중이거나 안전하지 않은 경로).";
            _logger.Info($"기록 삭제. deleted={result.DeletedCount} refused={result.Refused.Count}");
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand?.RaiseCanExecuteChanged();
        DeleteSelectedCommand?.RaiseCanExecuteChanged();
        CleanupCommand?.RaiseCanExecuteChanged();
    }
}
