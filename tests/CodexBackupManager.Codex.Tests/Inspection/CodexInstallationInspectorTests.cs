using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Tests.TestSupport;
using CodexBackupManager.Domain.Codex;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// <see cref="CodexInstallationInspector"/> 통합 테스트.
/// </summary>
/// <remarks>
/// 커밋된 합성 픽스처(<c>tests/Fixtures/CodexHome</c>)를 사용한다.
/// 픽스처의 값은 실제 이 PC의 값과 일부러 다르게 만들어서
/// (Desktop <c>99.123.45678</c>, CLI <c>9.9.9</c>, state generation 4, migration 7)
/// <b>코드에 Phase 0 관측값이 하드코딩되어 있지 않음</b>을 증명한다.
/// </remarks>
public sealed class CodexInstallationInspectorTests
{
    private static CodexInstallationInfo InspectFixture()
    {
        var locator = new CodexLocator(
            new FakeCodexEnvironment().WithCodexHome(RepositoryFixtures.CodexHomeFixture),
            new CodexHomeValidator());

        CodexLocatorResult located = locator.Locate();
        Assert.True(located.Found);

        CodexInstallationInfo? info = new CodexInstallationInspector().Inspect(located);
        Assert.NotNull(info);
        return info!;
    }

    [Fact]
    public void 픽스처의_state_DB_generation을_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(4, info.StateGeneration);
        Assert.NotNull(info.ActiveStateDatabase);
        Assert.Equal("state_4.sqlite", info.ActiveStateDatabase!.FileName);
        Assert.Single(info.StateDatabases);
    }

    [Fact]
    public void 픽스처의_migration_최신_version을_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(7L, info.LatestMigrationVersion);
        Assert.Equal("fixture latest", info.LatestMigrationDescription);
    }

    [Fact]
    public void 픽스처의_thread_개수를_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(4L, info.ThreadRowCount);
        Assert.Equal(1L, info.ArchivedThreadRowCount);
    }

    [Fact]
    public void 가장_최근_thread의_cli_version을_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        // 픽스처의 updated_at_ms 최대 행은 cli_version = 9.9.9
        Assert.Equal("9.9.9", info.CodexCliVersion);
    }

    [Fact]
    public void config_toml에서_Desktop_버전과_CLI_경로를_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal("99.123.45678", info.CodexDesktopVersion);
        Assert.Equal(@"C:\Fixture\bin\codex.exe", info.CliExecutablePath);

        // 픽스처의 CLI 경로는 실제로 존재하지 않는다. 그 사실도 정확히 보고해야 한다.
        Assert.False(info.CliExecutableExists);
    }

    [Fact]
    public void global_state에서_마이그레이션_플래그를_읽는다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.True(info.ProjectsMigrated);
        Assert.False(info.ThreadAssignmentsMigrated);
        Assert.Equal(@"local:C:\Fixture\.codex", info.ProjectsMigrationHostKey);
    }

    [Fact]
    public void 세션_파일_개수를_센다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(2, info.SessionFileCount);
        Assert.Equal(0, info.CompressedSessionFileCount);
        Assert.Equal(1, info.ArchivedSessionFileCount);
        Assert.Equal(0, info.CompressedArchivedSessionFileCount);
    }

    [Fact]
    public void session_index_줄_수를_센다()
    {
        CodexInstallationInfo info = InspectFixture();

        // 픽스처는 3줄(고유 2건). 중복 제거는 Phase 2의 일이다.
        Assert.Equal(3, info.SessionIndexLineCount);
    }

    [Fact]
    public void state_DB를_읽기_전용으로_연다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(SqliteOpenMode.ReadOnly, info.StateDatabaseOpenMode);
        Assert.Null(info.StateDatabaseError);
    }

    [Fact]
    public void 탐지_경로와_검증_결과를_함께_보고한다()
    {
        CodexInstallationInfo info = InspectFixture();

        Assert.Equal(CodexHomeSource.CodexHomeEnvironmentVariable, info.Source);
        Assert.Equal(CodexHomeStatus.Valid, info.Validation.Status);
        Assert.True(CanonicalPath.AreSameLocation(RepositoryFixtures.CodexHomeFixture, info.HomeDisplayPath));
    }

    [Fact]
    public void 탐지에_실패하면_null을_돌려준다()
    {
        var locator = new CodexLocator(
            new FakeCodexEnvironment().WithCodexHome(null).WithUserProfile(null),
            new CodexHomeValidator());

        CodexLocatorResult located = locator.Locate();

        Assert.False(located.Found);
        Assert.Null(new CodexInstallationInspector().Inspect(located));
    }

    [Fact]
    public void state_DB가_SQLite가_아니면_오류를_보고하고_나머지는_계속_읽는다()
    {
        // Validator는 파일 존재만 보므로 Valid가 되지만, 실제로 열면 실패해야 한다.
        using FakeCodexHome home = FakeCodexHome.CreateComplete("state_9.sqlite")
            .WithSessionFile(System.IO.Path.Combine("2026", "01", "02"), "rollout-a.jsonl");

        var locator = new CodexLocator(
            new FakeCodexEnvironment().WithCodexHome(home.Path),
            new CodexHomeValidator());

        CodexInstallationInfo? info = new CodexInstallationInspector().Inspect(locator.Locate());

        Assert.NotNull(info);
        Assert.Equal(9, info!.StateGeneration);

        // 열기 실패 또는 테이블 없음 중 하나로 보고되어야 하며, 어느 쪽이든 예외를 던지지 않는다.
        Assert.True(
            info.StateDatabaseOpenMode == SqliteOpenMode.Failed || info.StateDatabaseError is not null,
            $"열기 모드={info.StateDatabaseOpenMode}, 오류={info.StateDatabaseError ?? "(없음)"}");

        Assert.Null(info.CodexCliVersion);
        Assert.Equal(1, info.SessionFileCount);
    }

    [Fact]
    public void 압축된_rollout_파일도_따로_센다()
    {
        using FakeCodexHome home = FakeCodexHome.CreateComplete()
            .WithSessionFile(System.IO.Path.Combine("2026", "01", "02"), "rollout-a.jsonl")
            .WithSessionFile(System.IO.Path.Combine("2026", "01", "02"), "rollout-b.jsonl.zst")
            .WithSessionFile(System.IO.Path.Combine("2026", "01", "03"), "rollout-c.jsonl.tmp");

        var locator = new CodexLocator(
            new FakeCodexEnvironment().WithCodexHome(home.Path),
            new CodexHomeValidator());

        CodexInstallationInfo? info = new CodexInstallationInspector().Inspect(locator.Locate());

        Assert.NotNull(info);
        Assert.Equal(1, info!.SessionFileCount);            // .jsonl 만
        Assert.Equal(1, info.CompressedSessionFileCount);   // .jsonl.zst
        // .jsonl.tmp 는 어느 쪽에도 세지 않는다.
    }
}
