using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// Codex Home 하나를 <b>Read-Only</b>로 조사해 <see cref="CodexInstallationInfo"/>를 만든다.
/// </summary>
/// <remarks>
/// <para>이 클래스가 하는 일 (Phase 1 범위):</para>
/// <list type="bullet">
///   <item><c>state_*.sqlite</c> 목록과 generation 파싱, 활성 DB 선택</item>
///   <item>활성 DB에서 <c>_sqlx_migrations</c> 최신 version, <c>threads</c> 개수, <c>cli_version</c></item>
///   <item><c>config.toml</c>에서 Desktop 버전과 CLI 실행 파일 경로</item>
///   <item><c>.codex-global-state.json</c>에서 <c>threadAssignmentsMigrated</c></item>
///   <item><c>sessions\</c>, <c>archived_sessions\</c> 파일 개수</item>
///   <item>Codex 활동 신호(<c>-wal</c>/<c>-shm</c>, lock 디렉터리) — 표시만</item>
/// </list>
/// <para>이 클래스가 하지 않는 일:</para>
/// <list type="bullet">
///   <item>rollout JSONL을 열거나 파싱하지 않는다</item>
///   <item>대화 제목/미리보기/내용을 읽지 않는다</item>
///   <item>어떤 파일도 쓰거나 생성하거나 삭제하지 않는다</item>
/// </list>
/// </remarks>
public sealed class CodexInstallationInspector
{
    /// <summary>
    /// 탐색 결과를 받아 조사한다.
    /// </summary>
    /// <param name="located">
    /// <see cref="CodexLocator"/>의 결과. <see cref="CodexLocatorResult.Found"/>가 거짓이면 <c>null</c> 반환.
    /// </param>
    public CodexInstallationInfo? Inspect(CodexLocatorResult located)
    {
        ArgumentNullException.ThrowIfNull(located);

        if (!located.Found || located.Home is null || located.Source is null || located.Validation is null)
        {
            return null;
        }

        return Inspect(located.Home, located.HomeDisplayPath ?? located.Home.Display, located.Source.Value, located.Validation);
    }

    /// <summary>Codex Home을 직접 지정해 조사한다.</summary>
    public CodexInstallationInfo Inspect(
        CanonicalPath home,
        string homeDisplayPath,
        CodexHomeSource source,
        CodexHomeValidation validation)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(validation);

        string root = home.Display;
        var activity = new List<string>();

        // ── state DB 목록 ────────────────────────────────────────────────
        var stateDatabases = new List<StateDatabaseInfo>();
        foreach (string fileName in SafeFindStateDatabases(root))
        {
            string fullPath = Path.Combine(root, fileName);
            try
            {
                var file = new FileInfo(fullPath);
                bool hasWal = File.Exists(fullPath + "-wal");
                bool hasShm = File.Exists(fullPath + "-shm");

                stateDatabases.Add(new StateDatabaseInfo(
                    FileName: fileName,
                    Generation: CodexHomeLayout.TryParseGeneration(fileName),
                    SizeBytes: file.Exists ? file.Length : 0,
                    LastWriteTimeUtc: file.Exists ? file.LastWriteTimeUtc : default,
                    HasWriteAheadLog: hasWal,
                    HasSharedMemory: hasShm));

                if (hasWal)
                {
                    activity.Add($"{fileName}-wal 존재");
                }

                if (hasShm)
                {
                    activity.Add($"{fileName}-shm 존재");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 이 DB는 건너뛴다.
            }
        }

        // 활성 DB = generation 최대값. generation을 못 읽으면 최신 수정 시각.
        StateDatabaseInfo? active = null;
        foreach (StateDatabaseInfo candidate in stateDatabases)
        {
            if (active is null)
            {
                active = candidate;
                continue;
            }

            int candidateGeneration = candidate.Generation ?? -1;
            int activeGeneration = active.Generation ?? -1;

            if (candidateGeneration > activeGeneration ||
                (candidateGeneration == activeGeneration && candidate.LastWriteTimeUtc > active.LastWriteTimeUtc))
            {
                active = candidate;
            }
        }

        // ── 활성 state DB 읽기 ──────────────────────────────────────────
        StateDbReader.StateDbSnapshot? stateSnapshot = null;
        if (active is not null)
        {
            stateSnapshot = StateDbReader.Read(Path.Combine(root, active.FileName));
        }

        // ── config.toml ─────────────────────────────────────────────────
        IReadOnlyDictionary<string, string> configValues = ConfigTomlValueReader.ReadValues(
            Path.Combine(root, CodexHomeLayout.ConfigFileName),
            ConfigTomlValueReader.DesktopVersionKey,
            ConfigTomlValueReader.CliPathKey);

        configValues.TryGetValue(ConfigTomlValueReader.DesktopVersionKey, out string? desktopVersion);
        configValues.TryGetValue(ConfigTomlValueReader.CliPathKey, out string? cliPath);

        bool? cliExists = null;
        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            try
            {
                cliExists = File.Exists(cliPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cliExists = null;
            }
        }

        // ── .codex-global-state.json ────────────────────────────────────
        GlobalStateReader.ProjectsMigrationState? migration = GlobalStateReader.TryReadProjectsMigration(
            Path.Combine(root, CodexHomeLayout.GlobalStateFileName),
            home,
            out _);

        // ── 파일 개수 ───────────────────────────────────────────────────
        SessionFileCounter.Counts sessions = SessionFileCounter.Count(
            Path.Combine(root, CodexHomeLayout.SessionsDirectoryName), recursive: true);

        SessionFileCounter.Counts archived = SessionFileCounter.Count(
            Path.Combine(root, CodexHomeLayout.ArchivedSessionsDirectoryName), recursive: true);

        int? sessionIndexLines = SessionFileCounter.TryCountLines(
            Path.Combine(root, CodexHomeLayout.SessionIndexFileName));

        // ── 활동 신호 ───────────────────────────────────────────────────
        string lockDirectory = Path.Combine(root, CodexHomeLayout.ThreadWriterLocksDirectoryName);
        try
        {
            if (Directory.Exists(lockDirectory))
            {
                int lockCount = 0;
                foreach (string _ in Directory.EnumerateFileSystemEntries(lockDirectory))
                {
                    lockCount++;
                    if (lockCount >= 1)
                    {
                        break;
                    }
                }

                if (lockCount > 0)
                {
                    activity.Add($@"{CodexHomeLayout.ThreadWriterLocksDirectoryName}\ 에 항목 존재");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 무시
        }

        return new CodexInstallationInfo
        {
            Home = home,
            HomeDisplayPath = homeDisplayPath,
            Source = source,
            Validation = validation,
            CodexCliVersion = stateSnapshot?.LatestCliVersion,
            CodexDesktopVersion = desktopVersion,
            CliExecutablePath = cliPath,
            CliExecutableExists = cliExists,
            StateDatabases = stateDatabases,
            ActiveStateDatabase = active,
            LatestMigrationVersion = stateSnapshot?.LatestMigrationVersion,
            LatestMigrationDescription = stateSnapshot?.LatestMigrationDescription,
            StateDatabaseOpenMode = stateSnapshot?.OpenMode ?? SqliteOpenMode.NotOpened,
            StateDatabaseError = stateSnapshot?.Error,
            ThreadRowCount = stateSnapshot?.ThreadRowCount,
            ArchivedThreadRowCount = stateSnapshot?.ArchivedThreadRowCount,
            SessionFileCount = sessions.JsonlCount,
            CompressedSessionFileCount = sessions.CompressedCount,
            ArchivedSessionFileCount = archived.JsonlCount,
            CompressedArchivedSessionFileCount = archived.CompressedCount,
            ThreadAssignmentsMigrated = migration?.ThreadAssignmentsMigrated,
            ProjectsMigrated = migration?.ProjectsMigrated,
            ProjectsMigrationHostKey = migration?.HostKey,
            SessionIndexLineCount = sessionIndexLines,
            ActivitySignals = activity,
            InspectedAtUtc = DateTime.UtcNow,
        };
    }

    private static IReadOnlyList<string> SafeFindStateDatabases(string root)
    {
        try
        {
            return CodexHomeLayout.FindStateDatabaseFileNames(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
