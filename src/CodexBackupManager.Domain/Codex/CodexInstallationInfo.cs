using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Domain.Codex;

/// <summary>
/// SQLite를 읽을 때 실제로 사용된 열기 방식.
/// </summary>
public enum SqliteOpenMode
{
    /// <summary>읽지 않았다.</summary>
    NotOpened = 0,

    /// <summary><c>mode=ro</c>. WAL에 커밋된 최신 내용까지 반영된다.</summary>
    ReadOnly,

    /// <summary>
    /// <c>mode=ro&amp;immutable=1</c> 폴백. <c>-wal</c>/<c>-shm</c>에 접근할 수 없어 사용했다.
    /// 이 경우 WAL에만 있는 최신 변경은 보이지 않을 수 있다.
    /// </summary>
    ReadOnlyImmutableFallback,

    /// <summary>열기 실패.</summary>
    Failed,
}

/// <summary>
/// Codex Home 한 곳을 Read-Only로 조사한 결과.
/// </summary>
/// <remarks>
/// <b>모든 값은 실제 파일/DB에서 읽은 결과다.</b> Phase 0에서 관측된 값(CLI 0.153.4, Desktop 26.903.61454,
/// state generation 5, migration 52)을 코드에 하드코딩하지 않는다. 확인할 수 없는 값은 <c>null</c>로 남기고
/// 억지로 만들어내지 않는다. (CLAUDE.md §12, §33)
/// </remarks>
public sealed record CodexInstallationInfo
{
    /// <summary>탐지된 Codex Home.</summary>
    public required CanonicalPath Home { get; init; }

    /// <summary>Codex Home을 찾은 경로(원본 표기). UI 표시용.</summary>
    public required string HomeDisplayPath { get; init; }

    /// <summary>Codex Home을 어떤 순위에서 찾았는지.</summary>
    public required CodexHomeSource Source { get; init; }

    /// <summary>검증 결과.</summary>
    public required CodexHomeValidation Validation { get; init; }

    /// <summary>
    /// Codex CLI(core) 버전. 가장 최근에 갱신된 thread의 <c>threads.cli_version</c>에서 읽는다.
    /// </summary>
    public string? CodexCliVersion { get; init; }

    /// <summary>
    /// Codex Desktop 버전. <c>config.toml</c>의 <c>BROWSER_USE_CODEX_APP_VERSION</c>에서 읽는다.
    /// </summary>
    public string? CodexDesktopVersion { get; init; }

    /// <summary>
    /// Codex CLI 실행 파일 경로. <c>config.toml</c>의 <c>CODEX_CLI_PATH</c>에서 읽는다.
    /// </summary>
    public string? CliExecutablePath { get; init; }

    /// <summary><see cref="CliExecutablePath"/>가 실제로 존재하는지.</summary>
    public bool? CliExecutableExists { get; init; }

    /// <summary>Codex Home 루트에서 발견된 모든 <c>state_*.sqlite</c>.</summary>
    public IReadOnlyList<StateDatabaseInfo> StateDatabases { get; init; } = [];

    /// <summary>
    /// 조사에 사용한 활성 state DB. generation이 가장 높은 파일을 선택한다.
    /// </summary>
    public StateDatabaseInfo? ActiveStateDatabase { get; init; }

    /// <summary>활성 state DB의 generation. 예: <c>state_5.sqlite</c> → 5.</summary>
    public int? StateGeneration => ActiveStateDatabase?.Generation;

    /// <summary>활성 state DB의 <c>_sqlx_migrations</c> 최신 version.</summary>
    public long? LatestMigrationVersion { get; init; }

    /// <summary><c>_sqlx_migrations</c> 최신 migration의 description.</summary>
    public string? LatestMigrationDescription { get; init; }

    /// <summary>state DB를 어떤 방식으로 열었는지.</summary>
    public SqliteOpenMode StateDatabaseOpenMode { get; init; } = SqliteOpenMode.NotOpened;

    /// <summary>state DB 읽기 중 발생한 오류(있는 경우).</summary>
    public string? StateDatabaseError { get; init; }

    /// <summary><c>threads</c> 테이블의 전체 행 수.</summary>
    public long? ThreadRowCount { get; init; }

    /// <summary><c>threads.archived = 1</c> 행 수.</summary>
    public long? ArchivedThreadRowCount { get; init; }

    /// <summary><c>sessions\**\*.jsonl</c> 파일 개수.</summary>
    public int SessionFileCount { get; init; }

    /// <summary>
    /// <c>sessions\**\*.jsonl.zst</c> 파일 개수.
    /// Phase 1에서는 개수만 센다. 압축 해제/파싱은 하지 않는다.
    /// </summary>
    public int CompressedSessionFileCount { get; init; }

    /// <summary><c>archived_sessions\*.jsonl</c> 파일 개수.</summary>
    public int ArchivedSessionFileCount { get; init; }

    /// <summary><c>archived_sessions\*.jsonl.zst</c> 파일 개수.</summary>
    public int CompressedArchivedSessionFileCount { get; init; }

    /// <summary>
    /// <c>.codex-global-state.json</c> → <c>app-server-projects-migration-by-host</c> →
    /// <c>threadAssignmentsMigrated</c>.
    /// </summary>
    /// <remarks>
    /// Phase 0의 핵심 발견: 이 값이 <c>false</c>이면 프로젝트↔대화 매핑이 아직
    /// <c>.codex-global-state.json</c>의 <c>thread-project-assignments</c>에 있고,
    /// <c>threads.project_id</c>는 전부 NULL이다. Phase 2의 CodexProjectResolver가
    /// 어느 경로를 읽어야 하는지 이 플래그로 결정한다.
    /// </remarks>
    public bool? ThreadAssignmentsMigrated { get; init; }

    /// <summary>같은 위치의 <c>projectsMigrated</c> 값.</summary>
    public bool? ProjectsMigrated { get; init; }

    /// <summary>위 두 플래그를 읽어온 host key. 예: <c>local:&lt;codex home&gt;</c></summary>
    public string? ProjectsMigrationHostKey { get; init; }

    /// <summary><c>session_index.jsonl</c>의 줄 수. 중복 포함(append-only 로그).</summary>
    public int? SessionIndexLineCount { get; init; }

    /// <summary>
    /// Codex가 현재 실행 중일 가능성을 나타내는 신호.
    /// <c>-wal</c>/<c>-shm</c> 동반 파일 존재, lock 디렉터리 등으로 추정한다.
    /// Phase 1에서는 표시만 하고 어떤 동작도 바꾸지 않는다.
    /// </summary>
    public IReadOnlyList<string> ActivitySignals { get; init; } = [];

    /// <summary>조사를 수행한 시각(UTC).</summary>
    public DateTime InspectedAtUtc { get; init; } = DateTime.UtcNow;
}
