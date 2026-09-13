using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Writing;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using CodexBackupManager.Restore.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>
/// Phase 07_02 요구사항 7("가능하면 child-process/helper를 이용해 Apply 중간에 프로세스를 강제
/// 종료한 것과 같은 상태를 만들어") — 별도 자식 프로세스(<c>CodexBackupManager.Restore.CrashSim</c>)를
/// 실제로 띄우고, 그 프로세스가 지정된 지점에 도달한 뒤 실제로 <c>Process.Kill()</c>로 강제
/// 종료한다. 이렇게 만들어진 "Applying 상태로 멈춘 snapshot"을
/// <see cref="IncompleteApplyRecoveryService"/>가 다음 실행에서 정확히 찾아 복구하는지 확인한다.
/// 실제 사용자 <c>.codex</c>는 전혀 관여하지 않는다 — 전부 이 테스트가 만든 합성 temp Codex Home이다.
/// </summary>
public sealed class CrashRecoveryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbm-crashsim-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pcADir;
    private readonly string _pcBHome;
    private readonly string _outDir;
    private readonly string _snapshotRoot;

    public CrashRecoveryIntegrationTests()
    {
        _pcADir = Path.Combine(_root, "pcA-rollouts");
        _pcBHome = Path.Combine(_root, "pcB-home");
        _outDir = Path.Combine(_root, "out");
        _snapshotRoot = Path.Combine(_root, "snapshots");
        Directory.CreateDirectory(_pcADir);
        Directory.CreateDirectory(_outDir);
        TestCodexHomeBuilder.CreateEmpty(_pcBHome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string NewId() => Guid.NewGuid().ToString();

    private static string Line(long ordinal, string text)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\"," +
           "\"id\":\"i" + ordinal + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private static string SessionMetaLine(string threadId, DateTimeOffset ts)
        => "{\"timestamp\":\"" + ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"ordinal\":0,\"type\":\"session_meta\"," +
           "\"payload\":{\"id\":\"" + threadId + "\",\"session_id\":\"" + threadId + "\"," +
           "\"timestamp\":\"" + ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"cwd\":\"C:\\\\Fixture\\\\Proj\"," +
           "\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.1.0\",\"source\":\"vscode\"," +
           "\"model_provider\":\"openai\"}}";

    private string WriteRollout(string dir, string threadId, DateTimeOffset ts, params string[] extraLines)
    {
        string path = Path.Combine(dir, $"rollout-{ts:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl");
        var lines = new List<string> { SessionMetaLine(threadId, ts) };
        lines.AddRange(extraLines);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static RolloutFileReference Ref(string threadId, string fullPath, DateTimeOffset ts)
        => new(fullPath, Path.GetFileName(fullPath), threadId, null, ts, IsArchived: false, RolloutFileKind.PlainJsonl);

    private static ConversationEntry MakeEntry(string threadId) => new()
    {
        ThreadId = threadId,
        Row = new ThreadRow
        {
            Id = threadId,
            ModelProvider = "openai",
            Source = "vscode",
            Cwd = @"C:\Fixture\Proj",
            Title = "test title",
            SandboxPolicy = "{}",
            ApprovalMode = "on-request",
            ThreadSource = "user",
            CreatedAtSeconds = 1_770_000_000,
            UpdatedAtSeconds = 1_770_000_100,
        },
        Title = new ThreadTitle(threadId, ThreadTitleSource.StateTitle),
        Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned),
    };

    private static CodexCatalog SourceCatalog(string threadId, RolloutFileReference file)
    {
        ConversationEntry entry = MakeEntry(threadId);
        var chain = new ThreadChain(threadId, [file], new HistoryBaseReference?[1], null, null, null, []);
        return new CodexCatalog(
            [], [entry], new Dictionary<string, ThreadChain> { [threadId] = chain },
            [], DateTimeOffset.UtcNow, new CodexCatalogStats(1, 1, 0, TimeSpan.Zero, TimeSpan.Zero));
    }

    private string ExportToBackup(CodexCatalog catalog, string threadId, string fileName)
    {
        ExportPlan plan = ExportPlanBuilder.Build(catalog, new HashSet<string> { threadId });
        Assert.Empty(plan.FatalErrors);
        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        string dest = Path.Combine(_outDir, fileName);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest);
        Assert.True(result.Success, result.FailureReason);
        return dest;
    }

    /// <summary>
    /// 리포지토리 안에서 <c>CodexBackupManager.Restore.CrashSim</c>의 빌드된 실행 파일을 찾는다.
    /// 아직 빌드되지 않았으면(예: Restore.Tests만 단독으로 빌드/실행한 경우) 이 자리에서 한 번
    /// 빌드한다 — 이 통합 테스트가 어떤 순서로 실행돼도 스스로 준비를 끝낼 수 있게 한다.
    /// </summary>
    private static string FindOrBuildCrashSimExecutable()
    {
        string repoRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(repoRoot, "tests", "CodexBackupManager.Restore.CrashSim", "CodexBackupManager.Restore.CrashSim.csproj");
        string projectDir = Path.GetDirectoryName(projectPath)!;

        string? existing = FindBuiltExecutable(projectDir);
        if (existing is not null)
        {
            return existing;
        }

        var build = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" -v quiet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? buildProcess = Process.Start(build);
        Assert.NotNull(buildProcess);
        string stdout = buildProcess!.StandardOutput.ReadToEnd();
        string stderr = buildProcess.StandardError.ReadToEnd();
        buildProcess.WaitForExit();
        Assert.True(buildProcess.ExitCode == 0, $"CrashSim 빌드 실패: {stdout}\n{stderr}");

        string? built = FindBuiltExecutable(projectDir);
        Assert.NotNull(built);
        return built!;
    }

    private static string FindRepositoryRoot()
    {
        const string SolutionFileName = "CodexBackupManager.sln";
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException($"{SolutionFileName}을 찾을 수 없어 Repository 루트를 특정할 수 없습니다.");
    }

    private static string? FindBuiltExecutable(string projectDir)
    {
        string binDir = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(binDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(binDir, "CodexBackupManager.Restore.CrashSim.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// CrashSim을 자식 프로세스로 띄우고, 지정된 지점에 도달했다는 sentinel 파일이 나타나면 실제로
    /// <see cref="Process.Kill(bool)"/>로 강제 종료한다.
    /// </summary>
    private static void RunAndKillAtCrashPoint(string codexHomePath, string backupFilePath, RestoreFaultInjectionPoint crashPoint, string snapshotRoot)
    {
        string exePath = FindOrBuildCrashSimExecutable();
        string sentinelPath = Path.Combine(Path.GetTempPath(), $"cbm-crashsim-sentinel-{Guid.NewGuid():N}.txt");

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(codexHomePath);
        startInfo.ArgumentList.Add(backupFilePath);
        startInfo.ArgumentList.Add(crashPoint.ToString());
        startInfo.ArgumentList.Add(sentinelPath);
        startInfo.ArgumentList.Add(snapshotRoot);

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(sentinelPath))
            {
                if (process!.HasExited)
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    Assert.Fail($"CrashSim이 크래시 지점에 도달하기 전에 스스로 끝났습니다(exit={process.ExitCode}): {stdout}\n{stderr}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("CrashSim이 제한 시간 안에 크래시 지점에 도달하지 못했습니다.");
                }

                Thread.Sleep(20);
            }

            // 진짜 "강제 종료"다 — RestoreExecutor의 catch/Rollback이 실행될 기회조차 주지 않는다.
            process!.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        finally
        {
            try
            {
                File.Delete(sentinelPath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void 크래시_시뮬레이션_atomic_replace_직후_강제_종료되면_다음_실행에서_복구된다()
    {
        // IncomingAhead append 시나리오 — AfterAtomicReplace(요구사항 6의 원본 교체 직후)에서
        // 실제로 프로세스를 죽인다.
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, ts, Line(1, "hello"), Line(2, "world"));
        CodexCatalog pcA = SourceCatalog(threadId, Ref(threadId, aFile, ts));
        string backupPath = ExportToBackup(pcA, threadId, "crashsim-atomic-replace.codexbackup");

        string localContent = SessionMetaLine(threadId, ts) + "\n" + Line(1, "hello") + "\n";
        string bPath = TestCodexHomeBuilder.WriteRolloutFile(_pcBHome, Path.GetFileName(aFile), localContent, archived: false, ts);
        TestCodexHomeBuilder.InsertExistingThread(_pcBHome, threadId, bPath, @"C:\Fixture\Proj");

        string originalContent = File.ReadAllText(bPath);

        RunAndKillAtCrashPoint(_pcBHome, backupPath, RestoreFaultInjectionPoint.AfterAtomicReplace, _snapshotRoot);

        // 크래시 직후: 원본 rollout은 이미 교체된 뒤였을 수 있고(replace 자체는 atomic이라 성공했다),
        // journal은 Applying으로 멈춰 있어야 한다 — Rollback이 전혀 실행되지 않았기 때문이다.
        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot);
        Assert.Single(incomplete);
        Assert.Equal(RestoreTransactionState.Applying, incomplete[0].Journal.State);

        // "다음 실행"에서 사용자가 복구를 승인한다.
        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(incomplete[0].SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Equal(originalContent, File.ReadAllText(bPath));
        Assert.Empty(IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot));
    }

    [Fact]
    public void 크래시_시뮬레이션_SQLite_커밋_직후_강제_종료되면_다음_실행에서_복구된다()
    {
        // New Import 시나리오 — AfterSqliteCommit(커밋 직후)에서 실제로 프로세스를 죽인다.
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, Ref(threadId, aFile, ts));
        string backupPath = ExportToBackup(pcA, threadId, "crashsim-sqlite-commit.codexbackup");

        RunAndKillAtCrashPoint(_pcBHome, backupPath, RestoreFaultInjectionPoint.AfterSqliteCommit, _snapshotRoot);

        // 크래시 직후: SQLite commit까지는 끝났으니 thread 행은 이미 생겼을 수 있고, journal은
        // Applying으로 멈춰 있어야 한다.
        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot);
        Assert.Single(incomplete);
        Assert.Equal(RestoreTransactionState.Applying, incomplete[0].Journal.State);

        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(incomplete[0].SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Empty(IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot));
    }
}
