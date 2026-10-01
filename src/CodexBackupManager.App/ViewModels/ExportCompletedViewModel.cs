using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodexBackupManager.App.Services;
using CodexBackupManager.Backup.Summary;

namespace CodexBackupManager.App.ViewModels;

/// <summary>(Phase 9_6-01) 완료 대화상자의 작업 폴더 한 줄.</summary>
/// <param name="Path">표시 경로.</param>
/// <param name="IsMissing">이 PC에 그 폴더가 없는지(대화상자에서만 보여 준다, 텍스트 파일에는 적지 않는다).</param>
public sealed record ExportFolderRow(string Path, bool IsMissing)
{
    /// <summary>화면 문구("경로" 또는 "경로 (이 PC에 없음)").</summary>
    public string Text => IsMissing ? Path + " (이 PC에 없음)" : Path;
}

/// <summary>(Phase 9_6-01) 완료 대화상자의 프로젝트(또는 기타 대화) 하나.</summary>
/// <param name="Name">프로젝트 이름(기타 대화는 "기타 대화").</param>
/// <param name="ConversationCountText">"대화 N개".</param>
/// <param name="Folders">작업 폴더.</param>
public sealed record ExportFolderGroupRow(string Name, string ConversationCountText, IReadOnlyList<ExportFolderRow> Folders)
{
    /// <summary>작업 폴더 정보가 없는지(Manifest에 루트·cwd가 없음).</summary>
    public bool HasNoFolders => Folders.Count == 0;
}

/// <summary>
/// (Phase 9_6-01/02) "내보내기 완료" 대화상자. 백업에 작업 폴더 파일이 들어 있지 않다는 사실(CLAUDE.md §27)과 옮겨야 할 폴더를 보여 주고,
/// 목록을 텍스트 파일(UTF-8 BOM, CRLF)로 저장한다. 내용은 <see cref="ExportSummaryBuilder"/>(Backup 층 순수 함수)가 만든다.
/// 로그에는 개수와 성공·실패만 남긴다(제목·경로·파일 이름 없음).
/// </summary>
public sealed class ExportCompletedViewModel : ObservableObject
{
    /// <summary>강조 안내.</summary>
    public const string NoticeText =
        "백업 파일에는 Codex 대화 기록만 들어 있습니다. 작업 폴더의 파일(소스 코드, 문서, 에셋 등)은 들어 있지 않습니다. " +
        "다른 PC에서 이어서 작업하려면 아래 작업 폴더를 Git, USB, 클라우드 드라이브 등으로 따로 옮겨 주세요.";

    /// <summary>팁.</summary>
    public const string TipText = "다른 PC에서 가져올 때, 옮긴 폴더를 작업 폴더로 지정하면 그 프로젝트에 연결됩니다.";

    /// <summary>저장 버튼 아래 작은 글씨.</summary>
    public const string TextFileNoteText = "텍스트 파일에는 대화 제목과 폴더 경로가 그대로 적힙니다.";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly ExportSummary _summary;
    private readonly string _backupFilePath;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<string, string, string?> _textFilePicker;
    private readonly FileLogger _logger;
    private string? _saveStatusText;
    private bool _isSaveError;

    /// <summary>생성자.</summary>
    /// <param name="summary">이번 내보내기 요약.</param>
    /// <param name="backupFilePath">백업 파일 경로(저장 기본 위치·이름).</param>
    /// <param name="directoryExists">폴더가 이 PC에 있는지(App 층에서 확인, 테스트는 주입).</param>
    /// <param name="textFilePicker">(기본 폴더, 기본 파일 이름) → 저장 경로(취소 시 <c>null</c>).</param>
    /// <param name="timeZone">텍스트 시각의 시간대(이 PC).</param>
    /// <param name="logger">로거.</param>
    public ExportCompletedViewModel(
        ExportSummary summary,
        string backupFilePath,
        Func<string, bool> directoryExists,
        Func<string, string, string?> textFilePicker,
        TimeZoneInfo timeZone,
        FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _backupFilePath = backupFilePath ?? throw new ArgumentNullException(nameof(backupFilePath));
        _textFilePicker = textFilePicker ?? throw new ArgumentNullException(nameof(textFilePicker));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var groups = summary.Projects.Select(p => ToRow(p, directoryExists)).ToList();
        if (summary.Uncategorized is { } other)
        {
            groups.Add(ToRow(other, directoryExists));
        }

        Groups = groups;
        SaveTextCommand = new RelayCommand(SaveText);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>창을 닫아 달라는 요청(창이 구독한다).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>백업 파일 줄("이름 (크기)").</summary>
    public string BackupFileText => $"{_summary.BackupFileName} ({ExportSummaryBuilder.FormatSize(_summary.BackupSizeBytes)})";

    /// <summary>개수 줄("대화 N개 · 프로젝트 M개 · 기타 대화 K개").</summary>
    public string CountsText => ExportSummaryBuilder.CountsLine(_summary);

    /// <summary>강조 안내.</summary>
    public string Notice => NoticeText;

    /// <summary>팁.</summary>
    public string Tip => TipText;

    /// <summary>저장 버튼 아래 안내.</summary>
    public string TextFileNote => TextFileNoteText;

    /// <summary>작업 폴더 목록(프로젝트 이름 순, 기타 대화는 마지막).</summary>
    public IReadOnlyList<ExportFolderGroupRow> Groups { get; }

    /// <summary>"이 PC에 없음" 폴더 수(로그·테스트용).</summary>
    public int MissingFolderCount => Groups.Sum(g => g.Folders.Count(f => f.IsMissing));

    /// <summary>저장 기본 폴더(백업과 같은 폴더).</summary>
    public string DefaultTextFileDirectory => Path.GetDirectoryName(Path.GetFullPath(_backupFilePath)) ?? string.Empty;

    /// <summary>저장 기본 이름(<c>&lt;백업 파일 이름(확장자 제외)&gt;.txt</c>).</summary>
    public string DefaultTextFileName => Path.GetFileNameWithoutExtension(_backupFilePath) + ".txt";

    /// <summary>[목록을 텍스트 파일로 저장…].</summary>
    public RelayCommand SaveTextCommand { get; }

    /// <summary>[닫기].</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>저장 결과 문구("저장했습니다: 파일 이름" 또는 실패 사유). 없으면 <c>null</c>.</summary>
    public string? SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    /// <summary>저장 결과가 실패인지(경고색).</summary>
    public bool IsSaveError
    {
        get => _isSaveError;
        private set => SetProperty(ref _isSaveError, value);
    }

    /// <summary>텍스트 파일 내용(CRLF, BOM은 저장할 때 붙인다).</summary>
    public string BuildText() => ExportSummaryBuilder.ToText(_summary, _timeZone);

    private void SaveText()
    {
        string? path = _textFilePicker(DefaultTextFileDirectory, DefaultTextFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, BuildText(), Utf8WithBom);
            IsSaveError = false;
            SaveStatusText = "저장했습니다: " + Path.GetFileName(path);
            _logger.Info($"내보내기 목록 텍스트 저장. result=ok groups={Groups.Count}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            IsSaveError = true;
            SaveStatusText = $"저장하지 못했습니다({ex.GetType().Name}). 다른 위치를 골라 다시 시도해 주세요.";
            _logger.Warning($"내보내기 목록 텍스트 저장 실패. type={ex.GetType().Name}");
        }
    }

    private static ExportFolderGroupRow ToRow(ExportSummaryGroup group, Func<string, bool> directoryExists)
        => new(
            group.Name,
            $"대화 {group.Conversations.Count}개",
            group.Folders.Select(f => new ExportFolderRow(f, !SafeExists(directoryExists, f))).ToList());

    private static bool SafeExists(Func<string, bool> directoryExists, string path)
    {
        try
        {
            return directoryExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
