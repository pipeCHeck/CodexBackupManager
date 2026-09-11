using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Sessions;

public sealed class CodexSessionParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));

    public CodexSessionParserTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 정리 실패는 테스트 실패로 보지 않는다.
        }
    }

    private RolloutFileReference WriteFile(string content, string fileName = "a.jsonl", bool archived = false)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, content);
        return new RolloutFileReference(path, fileName, "thread-x", null, null, archived, RolloutFileKind.PlainJsonl);
    }

    [Fact]
    public void 정상_session_meta를_파싱한다()
    {
        string line = """
            {"timestamp":"2026-01-02T03:04:05.000Z","ordinal":0,"type":"session_meta","payload":{
              "session_id":"01a00000-0000-7000-8000-000000000001","cwd":"C:\\Fixture\\Alpha",
              "cli_version":"9.9.9","thread_source":"user","model_provider":"openai","model":"test-model",
              "history_mode":"paginated","source":"vscode","originator":"Codex Desktop",
              "git":{"commit_hash":"abc123","branch":"main","repository_url":"https://example.invalid/r.git"},
              "forked_from_id":"01a00000-0000-7000-8000-000000000000","forked_from_ordinal_exclusive":3,
              "history_base":{"thread_id":"01a00000-0000-7000-8000-000000000000","end_ordinal_exclusive":3,"end_byte_offset":120}
            }}
            """.ReplaceLineEndings(string.Empty);
        RolloutFileReference file = WriteFile(line, archived: true);

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Null(result.Warning);
        Assert.NotNull(result.Metadata);
        SessionMetadata meta = result.Metadata!;
        Assert.Equal("01a00000-0000-7000-8000-000000000001", meta.ThreadId);
        Assert.Equal(@"C:\Fixture\Alpha", meta.Cwd);
        Assert.Equal("9.9.9", meta.CliVersion);
        Assert.Equal("user", meta.ThreadSource);
        Assert.Equal("paginated", meta.HistoryMode);
        Assert.Equal("abc123", meta.Git?.CommitHash);
        Assert.Equal("01a00000-0000-7000-8000-000000000000", meta.ForkedFromId);
        Assert.Equal(3, meta.ForkedFromOrdinalExclusive);
        Assert.Equal("01a00000-0000-7000-8000-000000000000", meta.HistoryBase?.ThreadId);
        Assert.Equal(120, meta.HistoryBase?.EndByteOffset);
        Assert.True(meta.IsArchived);
    }

    [Fact]
    public void legacy_history_mode도_그대로_읽는다()
    {
        RolloutFileReference file = WriteFile(
            """{"type":"session_meta","payload":{"session_id":"01a00000-0000-7000-8000-000000000002","history_mode":"legacy"}}""");

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Equal("legacy", result.Metadata?.HistoryMode);
    }

    [Fact]
    public void 빈_JSONL은_null과_경고를_돌려준다()
    {
        RolloutFileReference file = WriteFile(string.Empty);

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Null(result.Metadata);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void 마지막_줄이_잘린_JSONL도_예외_없이_처리한다()
    {
        RolloutFileReference file = WriteFile("""{"type":"session_meta","payload":{"session_id":"01a0000""");

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Null(result.Metadata);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void 앞줄에_알수없는_type이_있어도_session_meta를_찾는다()
    {
        string content = string.Join(
            "\n",
            """{"type":"world_state","payload":{}}""",
            """{"type":"turn_context","payload":{}}""",
            """{"type":"session_meta","payload":{"session_id":"01a00000-0000-7000-8000-000000000009"}}""");
        RolloutFileReference file = WriteFile(content);

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Equal("01a00000-0000-7000-8000-000000000009", result.Metadata?.ThreadId);
    }

    [Fact]
    public void session_meta가_아예_없으면_경고와_함께_null이다()
    {
        string content = string.Join("\n", Enumerable(10));
        RolloutFileReference file = WriteFile(content);

        CodexSessionParser.ParseResult result = CodexSessionParser.ParseSessionMetadata(file);

        Assert.Null(result.Metadata);
        Assert.NotNull(result.Warning);

        static IEnumerable<string> Enumerable(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return """{"type":"event_msg","payload":{}}""";
            }
        }
    }
}
