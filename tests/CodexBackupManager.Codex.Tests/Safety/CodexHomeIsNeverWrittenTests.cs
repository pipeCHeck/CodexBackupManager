using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Sqlite;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Safety;

/// <summary>
/// <b>Phase 1에서 가장 중요한 테스트.</b>
/// 조사 코드가 Codex Home에 어떤 파일도 만들거나 바꾸거나 지우지 않음을 검증한다.
/// </summary>
/// <remarks>
/// <para>검증 방법:</para>
/// <list type="number">
///   <item>커밋된 합성 픽스처를 임시 폴더로 복사한다.</item>
///   <item>파일 목록 + 크기 + 마지막 수정 시각 + SHA-256을 스냅샷한다.</item>
///   <item>탐색 → 검증 → 조사 전체를 여러 번 실행한다.</item>
///   <item>스냅샷을 다시 만들어 완전히 동일한지 확인한다.</item>
/// </list>
/// <para>
/// <c>-wal</c> / <c>-shm</c> 파일이 새로 생기는지도 함께 본다.
/// 쓰기 가능 연결로 SQLite를 열면 이 파일들이 생기므로, 없어야 읽기 전용이 지켜진 것이다.
/// </para>
/// </remarks>
public sealed class CodexHomeIsNeverWrittenTests
{
    private sealed record FileState(long Length, long Ticks, string Sha256);

    [Fact]
    public void 조사_전후로_Codex_Home의_파일이_하나도_바뀌지_않는다()
    {
        string home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();

        try
        {
            IReadOnlyDictionary<string, FileState> before = Snapshot(home);
            Assert.NotEmpty(before);

            // 전체 파이프라인을 3회 반복 실행한다.
            for (int i = 0; i < 3; i++)
            {
                var service = new CodexDetectionService(
                    new CodexLocator(new FakeCodexEnvironment().WithCodexHome(home), new CodexHomeValidator()),
                    new CodexInstallationInspector());

                CodexDetectionService.DetectionResult result = service.Detect(savedManualPath: null);

                Assert.True(result.Found);
                Assert.NotNull(result.Installation);
                Assert.Equal(SqliteOpenMode.ReadOnly, result.Installation!.StateDatabaseOpenMode);
            }

            IReadOnlyDictionary<string, FileState> after = Snapshot(home);

            // 1) 새 파일이 생기지 않았다.
            foreach (string path in after.Keys)
            {
                Assert.True(before.ContainsKey(path), $"조사 중에 파일이 새로 생겼습니다: {path}");
            }

            // 2) 사라진 파일이 없다.
            foreach (string path in before.Keys)
            {
                Assert.True(after.ContainsKey(path), $"조사 중에 파일이 사라졌습니다: {path}");
            }

            // 3) 내용/크기/수정 시각이 모두 그대로다.
            foreach ((string path, FileState expected) in before)
            {
                FileState actual = after[path];
                Assert.Equal(expected.Sha256, actual.Sha256);
                Assert.Equal(expected.Length, actual.Length);
                Assert.Equal(expected.Ticks, actual.Ticks);
            }
        }
        finally
        {
            TryDelete(home);
        }
    }

    [Fact]
    public void state_DB를_읽어도_wal과_shm_파일이_생기지_않는다()
    {
        string home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();

        try
        {
            string databasePath = Path.Combine(home, "state_4.sqlite");
            Assert.True(File.Exists(databasePath));

            StateDbReader.StateDbSnapshot snapshot = StateDbReader.Read(databasePath);
            Assert.Equal(SqliteOpenMode.ReadOnly, snapshot.OpenMode);
            Assert.Equal(7L, snapshot.LatestMigrationVersion);

            Assert.False(File.Exists(databasePath + "-wal"), "읽기 전용 연결이 -wal 파일을 만들었습니다.");
            Assert.False(File.Exists(databasePath + "-shm"), "읽기 전용 연결이 -shm 파일을 만들었습니다.");
            Assert.False(File.Exists(databasePath + "-journal"), "읽기 전용 연결이 -journal 파일을 만들었습니다.");
        }
        finally
        {
            TryDelete(home);
        }
    }

    [Fact]
    public void 존재하지_않는_state_DB_경로를_읽어도_파일이_생기지_않는다()
    {
        // Mode=ReadOnly 는 파일을 새로 만들지 않아야 한다. (기본 모드는 만든다)
        string directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string missing = Path.Combine(directory, "state_7.sqlite");

        try
        {
            StateDbReader.StateDbSnapshot snapshot = StateDbReader.Read(missing);

            Assert.Equal(SqliteOpenMode.Failed, snapshot.OpenMode);
            Assert.False(File.Exists(missing), "읽기 전용으로 열었는데 DB 파일이 생성되었습니다.");
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void 쓰기_계열_SQL은_거부된다()
    {
        string home = RepositoryFixtures.CopyCodexHomeFixtureToTemp();

        try
        {
            using ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(
                Path.Combine(home, "state_4.sqlite"), out string? error);

            Assert.NotNull(database);
            Assert.Null(error);

            foreach (string sql in new[]
                     {
                         "INSERT INTO threads (id) VALUES ('x')",
                         "UPDATE threads SET title = 'x'",
                         "DELETE FROM threads",
                         "CREATE TABLE evil (a INTEGER)",
                         "DROP TABLE threads",
                         "PRAGMA journal_mode = DELETE",
                         "PRAGMA wal_checkpoint(TRUNCATE)",
                         "VACUUM",
                         "ATTACH DATABASE 'other.sqlite' AS other",
                     })
            {
                Assert.Throws<InvalidOperationException>(() => { database!.ReadScalar(sql); });
            }

            // SELECT 는 통과해야 한다.
            Assert.NotNull(database!.ReadScalar("SELECT COUNT(*) FROM threads"));
        }
        finally
        {
            TryDelete(home);
        }
    }

    [Fact]
    public void 우리가_쓰는_설정_경로는_Codex_Home_밖이다()
    {
        // App의 AppPaths는 App 어셈블리에 있으므로, 여기서는 규칙 자체를 문서화·고정한다.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.False(string.IsNullOrWhiteSpace(appData));
        Assert.DoesNotContain(".codex", appData, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, FileState> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            using FileStream stream = File.OpenRead(file);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));

            snapshot[Path.GetRelativePath(root, file)] = new FileState(
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                hash);
        }

        return snapshot;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 정리 실패는 테스트 실패로 보지 않는다.
        }
    }
}
