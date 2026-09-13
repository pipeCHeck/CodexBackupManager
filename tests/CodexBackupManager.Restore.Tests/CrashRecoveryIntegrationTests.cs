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
        startInfo.ArgumentList.Add("apply");
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
        Assert.Equal(RestoreTransactionState.Applying, incomplete[0].Journal!.State);

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
        Assert.Equal(RestoreTransactionState.Applying, incomplete[0].Journal!.State);

        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(incomplete[0].SnapshotDirectory, () => []);

        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        Assert.Empty(IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot));
    }

    [Fact]
    public void 크래시_시뮬레이션_New_rollout_temp_생성_후_target_이동_전_강제_종료되면_다음_실행에서_복구되고_재시도가_성공한다()
    {
        // Phase 07_03 요구사항 1 — New Import 시나리오. temp 검증까지 끝나고 target으로 옮기기
        // 직전(BeforeNewRolloutMove)에서 실제로 프로세스를 죽인다("New rollout temp가 생긴 직후,
        // target으로 move되기 전").
        string threadId = NewId();
        DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        string aFile = WriteRollout(_pcADir, threadId, ts, Line(1, "hello"));
        CodexCatalog pcA = SourceCatalog(threadId, Ref(threadId, aFile, ts));
        string backupPath = ExportToBackup(pcA, threadId, "crashsim-new-rollout-move.codexbackup");

        RunAndKillAtCrashPoint(_pcBHome, backupPath, RestoreFaultInjectionPoint.BeforeNewRolloutMove, _snapshotRoot);

        // 크래시 직후: target rollout/thread는 아직 전혀 만들어지지 않았어야 한다(temp 검증까지만
        // 끝났다) — journal은 Applying으로 멈춰 있어야 한다.
        Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
        IReadOnlyList<IncompleteApply> incomplete = IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot);
        Assert.Single(incomplete);

        RestoreResult recovery = IncompleteApplyRecoveryService.Recover(incomplete[0].SnapshotDirectory, () => []);
        Assert.Equal(RestoreOutcome.RolledBack, recovery.Outcome);
        Assert.Empty(IncompleteApplyRecoveryService.FindIncomplete(_snapshotRoot));

        // "다음 실행"에서 같은 backup으로 다시 Apply한다 — 이전 시도가 남겼을 stale temp가 재시도
        // 자체를 막으면 안 된다(요구사항 1의 핵심).
        var detection = new CodexDetectionService().DetectFromUserSelection(_pcBHome);
        Assert.NotNull(detection.Installation);
        CodexCatalog freshCatalog = CodexCatalogBuilder.Build(detection.Installation!);
        ImportPreview retryPreview = ImportPreviewBuilder.Build(backupPath, freshCatalog);
        Assert.True(retryPreview.Success, string.Join(";", retryPreview.ValidationErrors));
        ImportPlan? retryPlan = ImportPlanBuilder.Build(retryPreview, backupPath);
        Assert.NotNull(retryPlan);

        RestoreResult retryResult = RestoreExecutor.Apply(retryPlan!, _pcBHome, snapshotRoot: _snapshotRoot);

        Assert.True(retryResult.Outcome == RestoreOutcome.Succeeded, $"{retryResult.Outcome}: {retryResult.Message}");
        Assert.Contains(threadId, TestCodexHomeBuilder.ReadThreadIds(_pcBHome));
    }

    [Fact]
    public void 같은_Codex_Home에서_동시에_Apply를_시도하면_한쪽만_lock을_획득하고_다른_Home은_막히지_않는다()
    {
        // Phase 07_03 요구사항 4/5 — 진짜 두 프로세스로 RestoreProcessLock을 검증한다.
        string exePath = FindOrBuildCrashSimExecutable();
        string readySentinel = Path.Combine(Path.GetTempPath(), $"cbm-lockhold-ready-{Guid.NewGuid():N}.txt");
        string releaseSignal = Path.Combine(Path.GetTempPath(), $"cbm-lockhold-release-{Guid.NewGuid():N}.txt");

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("lock-hold");
        startInfo.ArgumentList.Add(_pcBHome);
        startInfo.ArgumentList.Add(readySentinel);
        startInfo.ArgumentList.Add(releaseSignal);

        using Process? holder = Process.Start(startInfo);
        Assert.NotNull(holder);

        string otherHome = Path.Combine(_root, "pcC-home");
        TestCodexHomeBuilder.CreateEmpty(otherHome);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(readySentinel))
            {
                if (holder!.HasExited)
                {
                    Assert.Fail($"lock-hold 프로세스가 준비되기 전에 끝났습니다(exit={holder.ExitCode}): {holder.StandardError.ReadToEnd()}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("lock-hold 프로세스가 제한 시간 안에 준비되지 않았습니다.");
                }

                Thread.Sleep(20);
            }

            Assert.Equal("acquired", File.ReadAllText(readySentinel));

            // 같은 Home: 다른 프로세스가 이미 이 Home을 처리 중이므로 production RestoreExecutor.Apply가
            // 즉시 NotReady를 돌려줘야 한다 — write 0건.
            string threadId = NewId();
            DateTimeOffset ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            string aFile = WriteRollout(_pcADir, threadId, ts, Line(1, "hello"));
            CodexCatalog pcA = SourceCatalog(threadId, Ref(threadId, aFile, ts));
            string backupPath = ExportToBackup(pcA, threadId, "lock-same-home.codexbackup");

            var detection = new CodexDetectionService().DetectFromUserSelection(_pcBHome);
            Assert.NotNull(detection.Installation);
            CodexCatalog pcBCatalog = CodexCatalogBuilder.Build(detection.Installation!);
            ImportPreview preview = ImportPreviewBuilder.Build(backupPath, pcBCatalog);
            ImportPlan? plan = ImportPlanBuilder.Build(preview, backupPath);
            Assert.NotNull(plan);

            RestoreResult sameHomeResult = RestoreExecutor.Apply(plan!, _pcBHome, snapshotRoot: _snapshotRoot);
            Assert.Equal(RestoreOutcome.NotReady, sameHomeResult.Outcome);
            Assert.Contains("다른 Codex Backup Manager 인스턴스", sameHomeResult.Message);
            Assert.Empty(TestCodexHomeBuilder.ReadThreadIds(_pcBHome));

            // 다른 Home: 완전히 다른 이름의 lock이므로 동시에 획득할 수 있어야 한다.
            string threadId2 = NewId();
            string aFile2 = WriteRollout(_pcADir, threadId2, ts, Line(1, "hello-other-home"));
            CodexCatalog pcA2 = SourceCatalog(threadId2, Ref(threadId2, aFile2, ts));
            string backupPath2 = ExportToBackup(pcA2, threadId2, "lock-other-home.codexbackup");

            var detectionOther = new CodexDetectionService().DetectFromUserSelection(otherHome);
            Assert.NotNull(detectionOther.Installation);
            CodexCatalog pcCCatalog = CodexCatalogBuilder.Build(detectionOther.Installation!);
            ImportPreview previewOther = ImportPreviewBuilder.Build(backupPath2, pcCCatalog);
            ImportPlan? planOther = ImportPlanBuilder.Build(previewOther, backupPath2);
            Assert.NotNull(planOther);

            RestoreResult otherHomeResult = RestoreExecutor.Apply(planOther!, otherHome, snapshotRoot: _snapshotRoot);
            Assert.True(otherHomeResult.Outcome == RestoreOutcome.Succeeded, $"{otherHomeResult.Outcome}: {otherHomeResult.Message}");
        }
        finally
        {
            try
            {
                File.WriteAllText(releaseSignal, "release");
            }
            catch (IOException)
            {
            }

            holder!.WaitForExit(10000);

            try
            {
                File.Delete(readySentinel);
            }
            catch (IOException)
            {
            }

            try
            {
                File.Delete(releaseSignal);
            }
            catch (IOException)
            {
            }
        }
    }
}
