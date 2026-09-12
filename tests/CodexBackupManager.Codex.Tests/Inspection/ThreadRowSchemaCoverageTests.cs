using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// Phase 05_01 hardening — "38컬럼 전부 보존한다"는 문서(<c>docs/codexbackup-format-v1.md</c> §1)의
/// 주장을 코드로 강제한다. 실제 <c>state_5.sqlite.threads</c> 38컬럼을 하드코딩된 ground truth로
/// 두고, <see cref="ThreadRowReader.KnownColumns"/>/<see cref="ThreadRow"/>와 기계적으로 대조한다.
/// 컬럼 하나라도 빠지면(이번에 <c>source</c>가 그랬던 것처럼) 이 테스트가 실패한다.
/// </summary>
public sealed class ThreadRowSchemaCoverageTests
{
    /// <summary>
    /// 실제 <c>state_5.sqlite.threads</c>(migration 52, 38컬럼)를 <c>PRAGMA table_info</c>로 실측한
    /// 값(docs/codexbackup-format-v1.md §1.2). 새 컬럼이 실측되면 여기에 먼저 추가하고, 그 다음
    /// <see cref="ThreadRowReader.KnownColumns"/>/<see cref="ThreadRow"/>/manifest를 갱신한다 —
    /// 순서를 반대로 하지 않는다(실측이 항상 먼저).
    /// </summary>
    private static readonly string[] Real38Columns =
    [
        "id", "rollout_path", "created_at", "updated_at", "source", "model_provider", "cwd", "title",
        "sandbox_policy", "approval_mode", "tokens_used", "has_user_event", "archived", "archived_at",
        "git_sha", "git_branch", "git_origin_url", "cli_version", "first_user_message",
        "agent_nickname", "agent_role", "memory_mode", "model", "reasoning_effort", "agent_path",
        "created_at_ms", "updated_at_ms", "thread_source", "preview", "recency_at", "recency_at_ms",
        "history_mode", "name", "is_pinned", "thread_section_id", "section_position",
        "section_entered_at_ms", "project_id",
    ];

    /// <summary>컬럼(snake_case) → <see cref="ThreadRow"/> 속성 이름(PascalCase) 매핑.</summary>
    private static readonly Dictionary<string, string> ColumnToThreadRowProperty = new(StringComparer.Ordinal)
    {
        ["id"] = nameof(ThreadRow.Id),
        ["rollout_path"] = nameof(ThreadRow.RolloutPath),
        ["created_at"] = nameof(ThreadRow.CreatedAtSeconds),
        ["updated_at"] = nameof(ThreadRow.UpdatedAtSeconds),
        ["source"] = nameof(ThreadRow.Source),
        ["model_provider"] = nameof(ThreadRow.ModelProvider),
        ["cwd"] = nameof(ThreadRow.Cwd),
        ["title"] = nameof(ThreadRow.Title),
        ["sandbox_policy"] = nameof(ThreadRow.SandboxPolicy),
        ["approval_mode"] = nameof(ThreadRow.ApprovalMode),
        ["tokens_used"] = nameof(ThreadRow.TokensUsed),
        ["has_user_event"] = nameof(ThreadRow.HasUserEvent),
        ["archived"] = nameof(ThreadRow.Archived),
        ["archived_at"] = nameof(ThreadRow.ArchivedAtSeconds),
        ["git_sha"] = nameof(ThreadRow.GitSha),
        ["git_branch"] = nameof(ThreadRow.GitBranch),
        ["git_origin_url"] = nameof(ThreadRow.GitOriginUrl),
        ["cli_version"] = nameof(ThreadRow.CliVersion),
        ["first_user_message"] = nameof(ThreadRow.FirstUserMessage),
        ["agent_nickname"] = nameof(ThreadRow.AgentNickname),
        ["agent_role"] = nameof(ThreadRow.AgentRole),
        ["memory_mode"] = nameof(ThreadRow.MemoryMode),
        ["model"] = nameof(ThreadRow.Model),
        ["reasoning_effort"] = nameof(ThreadRow.ReasoningEffort),
        ["agent_path"] = nameof(ThreadRow.AgentPath),
        ["created_at_ms"] = nameof(ThreadRow.CreatedAtMs),
        ["updated_at_ms"] = nameof(ThreadRow.UpdatedAtMs),
        ["thread_source"] = nameof(ThreadRow.ThreadSource),
        ["preview"] = nameof(ThreadRow.Preview),
        ["recency_at"] = nameof(ThreadRow.RecencyAtSeconds),
        ["recency_at_ms"] = nameof(ThreadRow.RecencyAtMs),
        ["history_mode"] = nameof(ThreadRow.HistoryMode),
        ["name"] = nameof(ThreadRow.Name),
        ["is_pinned"] = nameof(ThreadRow.IsPinned),
        ["thread_section_id"] = nameof(ThreadRow.ThreadSectionId),
        ["section_position"] = nameof(ThreadRow.SectionPosition),
        ["section_entered_at_ms"] = nameof(ThreadRow.SectionEnteredAtMs),
        ["project_id"] = nameof(ThreadRow.ProjectId),
    };

    [Fact]
    public void ThreadRowReader_KnownColumns가_실제_38컬럼과_정확히_일치한다()
    {
        var expected = new HashSet<string>(Real38Columns, StringComparer.Ordinal);
        var actual = new HashSet<string>(ThreadRowReader.KnownColumns, StringComparer.Ordinal);

        var missingFromReader = expected.Except(actual).ToList();
        var extraInReader = actual.Except(expected).ToList();

        Assert.True(
            missingFromReader.Count == 0,
            $"ThreadRowReader.KnownColumns에 없는 실제 컬럼: {string.Join(", ", missingFromReader)}");
        Assert.True(
            extraInReader.Count == 0,
            $"ThreadRowReader.KnownColumns에만 있고 실제 스키마엔 없는 컬럼(오타 의심): {string.Join(", ", extraInReader)}");
    }

    [Fact]
    public void 실제_38컬럼_전부가_ThreadRow_속성에_매핑돼_있다()
    {
        var expected = new HashSet<string>(Real38Columns, StringComparer.Ordinal);
        var mappedColumns = new HashSet<string>(ColumnToThreadRowProperty.Keys, StringComparer.Ordinal);

        var unmapped = expected.Except(mappedColumns).ToList();
        Assert.True(unmapped.Count == 0, $"매핑표에 없는 컬럼(=ThreadRow에 보존되지 않을 위험): {string.Join(", ", unmapped)}");

        PropertyInfo[] threadRowProperties = typeof(ThreadRow).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var threadRowPropertyNames = new HashSet<string>(threadRowProperties.Select(p => p.Name), StringComparer.Ordinal);

        foreach ((string column, string propertyName) in ColumnToThreadRowProperty)
        {
            Assert.True(
                threadRowPropertyNames.Contains(propertyName),
                $"컬럼 '{column}'이 가리키는 ThreadRow.{propertyName} 속성이 존재하지 않습니다.");
        }

        // 반대 방향: ThreadRow에 매핑표에 없는 "여분의" 속성이 있으면(실수로 이름이 어긋났거나
        // 중복 추가된 경우) 여기서도 드러난다.
        var mappedPropertyNames = new HashSet<string>(ColumnToThreadRowProperty.Values, StringComparer.Ordinal);
        var unmappedProperties = threadRowPropertyNames.Except(mappedPropertyNames).ToList();
        Assert.True(
            unmappedProperties.Count == 0,
            $"ThreadRow에 매핑표와 대조되지 않은 속성이 있습니다(38컬럼 목록을 갱신했는지 확인): {string.Join(", ", unmappedProperties)}");
    }
}
