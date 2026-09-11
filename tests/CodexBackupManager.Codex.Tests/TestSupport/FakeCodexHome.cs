using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexBackupManager.Codex.Tests.TestSupport;

/// <summary>
/// 임시 폴더에 작은 가짜 Codex Home을 만든다.
/// </summary>
/// <remarks>
/// <b>실제 사용자의 <c>.codex</c> 데이터를 복사하지 않는다.</b> (CLAUDE.md §31)
/// 필요한 구성 요소만 골라 만들 수 있어 Validator의 각 판정 분기를 그대로 검증할 수 있다.
/// </remarks>
public sealed class FakeCodexHome : IDisposable
{
    private FakeCodexHome(string path) => Path = path;

    /// <summary>만들어진 가짜 Codex Home 경로.</summary>
    public string Path { get; }

    /// <summary>빈 폴더만 만든다.</summary>
    public static FakeCodexHome CreateEmpty()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cbm-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new FakeCodexHome(root);
    }

    /// <summary><c>sessions\</c> 폴더를 만든다.</summary>
    public FakeCodexHome WithSessionsDirectory()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "sessions"));
        return this;
    }

    /// <summary><c>archived_sessions\</c> 폴더를 만든다.</summary>
    public FakeCodexHome WithArchivedSessionsDirectory()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "archived_sessions"));
        return this;
    }

    /// <summary>
    /// <c>sessions\YYYY\MM\DD\</c> 아래에 rollout 파일을 만든다. 내용은 최소한의 한 줄.
    /// </summary>
    public FakeCodexHome WithSessionFile(string relativeDayPath, string fileName)
    {
        string directory = System.IO.Path.Combine(Path, "sessions", relativeDayPath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            System.IO.Path.Combine(directory, fileName),
            "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{}}" + Environment.NewLine,
            Encoding.UTF8);
        return this;
    }

    /// <summary><c>archived_sessions\</c> 아래에 rollout 파일을 만든다.</summary>
    public FakeCodexHome WithArchivedSessionFile(string fileName)
    {
        WithArchivedSessionsDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(Path, "archived_sessions", fileName),
            "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{}}" + Environment.NewLine,
            Encoding.UTF8);
        return this;
    }

    /// <summary>
    /// state DB 자리표시 파일을 만든다. Validator는 파일 존재만 보므로 내용은 SQLite가 아니어도 된다.
    /// </summary>
    public FakeCodexHome WithStateDatabaseFile(string fileName)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, fileName), "not-a-real-sqlite", Encoding.UTF8);
        return this;
    }

    /// <summary><c>session_index.jsonl</c>을 만든다.</summary>
    public FakeCodexHome WithSessionIndex(int lines = 1)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            builder.Append("{\"id\":\"01a00000-0000-7000-8000-00000000000")
                   .Append(i % 10)
                   .AppendLine("\",\"thread_name\":\"fixture\",\"updated_at\":\"2026-01-02T03:04:05.0000000Z\"}");
        }

        File.WriteAllText(System.IO.Path.Combine(Path, "session_index.jsonl"), builder.ToString(), Encoding.UTF8);
        return this;
    }

    /// <summary><c>config.toml</c>을 만든다.</summary>
    public FakeCodexHome WithConfigToml(string? content = null)
    {
        File.WriteAllText(
            System.IO.Path.Combine(Path, "config.toml"),
            content ?? "model = \"fixture\"" + Environment.NewLine,
            Encoding.UTF8);
        return this;
    }

    /// <summary><c>.codex-global-state.json</c>을 만든다.</summary>
    public FakeCodexHome WithGlobalState(string? content = null)
    {
        File.WriteAllText(
            System.IO.Path.Combine(Path, ".codex-global-state.json"),
            content ?? "{}",
            Encoding.UTF8);
        return this;
    }

    /// <summary>모든 신호를 갖춘 완전한 구조를 만든다.</summary>
    public static FakeCodexHome CreateComplete(string stateDatabaseFileName = "state_5.sqlite")
        => CreateEmpty()
            .WithSessionsDirectory()
            .WithArchivedSessionsDirectory()
            .WithStateDatabaseFile(stateDatabaseFileName)
            .WithSessionIndex()
            .WithConfigToml()
            .WithGlobalState();

    /// <summary>
    /// 현재 폴더 전체의 스냅샷을 만든다. 쓰기 방지 검증에 사용한다.
    /// </summary>
    /// <returns>상대 경로 → (크기, 마지막 수정 시각 ticks)</returns>
    public IReadOnlyDictionary<string, (long Length, long Ticks)> Snapshot()
        => SnapshotDirectory(Path);

    /// <summary>임의 디렉터리의 스냅샷을 만든다.</summary>
    public static IReadOnlyDictionary<string, (long Length, long Ticks)> SnapshotDirectory(string root)
    {
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            snapshot[System.IO.Path.GetRelativePath(root, file)] =
                (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 임시 폴더 정리 실패는 테스트 실패로 보지 않는다.
        }
    }
}
