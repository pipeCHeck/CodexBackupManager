using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.App.ViewModels.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Restore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackupManager.App.Tests.TestSupport;

/// <summary>
/// Phase 9_2-2 — 가져오기 화면 테스트용 준비물. 커밋된 fixture Codex Home을 temp로 두 번 복사해(원본 PC / 대상 PC)
/// 실제 Export → 가져오기 화면 → Apply까지 돌린다. 실제 사용자 <c>.codex</c>는 전혀 쓰지 않는다.
/// </summary>
/// <remarks>
/// Codex 실행 여부는 주입한다(<see cref="CodexRunning"/> = 화면의 확인, <see cref="RestoreSeesCodex"/> = Restore의 확인).
/// 편집 중 5초 감시 타이머는 콜백만 잡아 두고(<see cref="PollCallback"/>) 테스트가 직접 부른다.
/// </remarks>
internal sealed class ImportWorkspaceHarness : IDisposable
{
    /// <summary>fixture의 사용자 대화 1(Alpha 프로젝트, 레거시 배정).</summary>
    public const string Thread1 = "01a00000-0000-7000-8000-000000000001";

    /// <summary>fixture의 사용자 대화 2(기타 대화).</summary>
    public const string Thread2 = "01a00000-0000-7000-8000-000000000002";

    /// <summary>fixture의 사용자 대화 3(보관됨, 기타 대화).</summary>
    public const string Thread3 = "01a00000-0000-7000-8000-000000000003";

    public ImportWorkspaceHarness()
    {
        SourceHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        TargetHome = RepositoryFixtures.CopyCodexHomeFixtureToTemp();
        TestDir = Path.Combine(Path.GetTempPath(), "cbm-app-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestDir);
        SnapshotRoot = Path.Combine(TestDir, "snapshots");
    }

    public string SourceHome { get; }

    public string TargetHome { get; }

    public string TestDir { get; }

    public string SnapshotRoot { get; }

    /// <summary>가져오기 화면이 보는 "Codex 실행 중" 여부.</summary>
    public bool CodexRunning { get; set; }

    /// <summary>Restore(적용 시점)가 보는 "Codex 실행 중" 여부.</summary>
    public bool RestoreSeesCodex { get; set; }

    /// <summary>편집 중 감시 타이머 콜백(테스트가 직접 부른다).</summary>
    public Action? PollCallback { get; private set; }

    public void Dispose()
    {
        foreach (string dir in new[] { SourceHome, TargetHome, TestDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public MainViewModel CreateMainViewModel(
        string home, Func<string, string?>? exportFilePicker = null, Func<string?>? importFilePicker = null)
    {
        var viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(TestDir, $"settings-{Guid.NewGuid():N}.json")),
            new FileLogger(Path.Combine(TestDir, "logs")),
            folderPicker: () => home,
            exportFilePicker: exportFilePicker,
            importFilePicker: importFilePicker,
            confirmDialog: (_, _) => true,
            snapshotRootProvider: () => SnapshotRoot);
        viewModel.ProcessLister = () => [];
        Configure(viewModel.ImportWorkspace);
        return viewModel;
    }

    public static async Task ConnectAsync(MainViewModel viewModel)
    {
        viewModel.ChangeFolderCommand.Execute(null);
        await WaitUntil(() => !viewModel.IsBusy, "탐지");
        await WaitUntil(() => !viewModel.IsCatalogLoading, "카탈로그");
    }

    /// <summary>원본 PC(fixture 복사본)에서 대화를 내보낸다. 지정하지 않으면 전체.</summary>
    public async Task<string> ExportAsync(params string[] threadIds)
    {
        string backupPath = Path.Combine(TestDir, $"export-{Guid.NewGuid():N}.codexbackup");
        MainViewModel exporter = CreateMainViewModel(SourceHome, _ => backupPath);
        await ConnectAsync(exporter);
        if (threadIds.Length == 0)
        {
            exporter.SelectAllConversationsCommand.Execute(null);
        }
        else
        {
            exporter.Selection.SelectMany(threadIds);
        }

        exporter.ExportCommand.Execute(null);
        await WaitUntil(() => !exporter.IsExporting, "Export");
        Assert.True(File.Exists(backupPath), exporter.ExportStatusText);
        return backupPath;
    }

    /// <summary>대상 PC에서 대화를 지운다(행 + rollout 파일) — 가져오기에서 "새 대화"가 된다.</summary>
    public void RemoveFromTarget(params string[] threadIds)
    {
        using (SqliteConnection connection = Open(TargetHome))
        {
            foreach (string id in threadIds)
            {
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM threads WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        foreach (string file in Directory.EnumerateFiles(TargetHome, "rollout-*.jsonl*", SearchOption.AllDirectories)
                     .Where(f => threadIds.Any(id => Path.GetFileName(f).Contains(id, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            File.Delete(file);
        }
    }

    /// <summary>대상 PC에 등록 프로젝트를 만든다(실측 스키마의 projects/project_roots).</summary>
    public void RegisterTargetProject(string projectId, string name, string root)
    {
        using SqliteConnection connection = Open(TargetHome);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, metadata, position, created_at_ms, updated_at_ms)
            VALUES ($id, $name, '{}', (SELECT COALESCE(MAX(position), -1) + 1 FROM projects), 1, 1);
            INSERT INTO project_roots (project_id, position, path) VALUES ($id, 0, $root);
            """;
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$root", root);
        cmd.ExecuteNonQuery();
    }

    public static object? ReadThreadColumn(string home, string threadId, string column)
    {
        using SqliteConnection connection = Open(home);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM threads WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", threadId);
        return cmd.ExecuteScalar();
    }

    public static bool ThreadExists(string home, string threadId)
    {
        using SqliteConnection connection = Open(home);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM threads WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", threadId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>폴더 안 모든 파일의 SHA-256(상대 경로 → 해시). 쓰기 0건 확인용.</summary>
    public static Dictionary<string, string> HashTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.OrdinalIgnoreCase);

    public string NewFolder(string name)
    {
        string path = Path.Combine(TestDir, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    public ImportWorkspaceViewModel CreateWorkspace(
        Func<string?> filePicker,
        Func<string?>? folderPicker = null,
        Func<string, string, bool>? confirm = null,
        Func<bool>? hasIncompleteApply = null)
    {
        var workspace = new ImportWorkspaceViewModel(
            new FileLogger(Path.Combine(TestDir, "logs")),
            filePicker,
            folderPicker ?? (() => null),
            confirm ?? ((_, _) => true),
            () => SnapshotRoot,
            () => TargetHome,
            hasIncompleteApply ?? (() => false));
        Configure(workspace);
        return workspace;
    }

    public void Configure(ImportWorkspaceViewModel workspace)
    {
        workspace.ProcessLister = () => CodexRunning ? [new RunningProcessInfo("Codex", null)] : [];
        workspace.PollTimerFactory = (_, callback) =>
        {
            PollCallback = callback;
            return new Disposable(() => PollCallback = null);
        };
        workspace.RestoreApply = (plan, home, snapshotRoot, onStatus, token) => RestoreExecutor.Apply(
            plan, home,
            () => RestoreSeesCodex ? [new RunningProcessInfo("Codex", null)] : [],
            RestoreExecutor.BuildFreshCatalog,
            snapshotRoot, faultInjection: null, onStatusChanged: onStatus, cancellationToken: token);
    }

    /// <summary>가져오기 화면을 열어 편집 상태까지 간다.</summary>
    public async Task<ImportWorkspaceViewModel> OpenEditingAsync(
        string backupPath, Func<string?>? folderPicker = null, Func<string, string, bool>? confirm = null)
    {
        ImportWorkspaceViewModel workspace = CreateWorkspace(() => backupPath, folderPicker, confirm);
        await workspace.OpenAsync();
        Assert.True(workspace.State == ImportWorkspaceState.Editing, $"{workspace.State}: {workspace.Message}");
        return workspace;
    }

    public static async Task WaitUntil(Func<bool> condition, string label)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{label}이(가) 제한 시간 안에 끝나지 않았습니다.");
            }

            await Task.Delay(10);
        }
    }

    private static SqliteConnection Open(string home)
    {
        string db = Directory.EnumerateFiles(home, "state_*.sqlite").First();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private sealed class Disposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
