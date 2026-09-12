using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Domain.Codex.Threads;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Manifest;

/// <summary>
/// Phase 05_01 hardening — "38컬럼을 lossless하게 보존한다"는 주장이 <see cref="ThreadRow"/>에서
/// 끝나지 않고 <see cref="BackupConversationMetadata"/>(실제 manifest.json에 쓰이는 모델)까지
/// 이어지는지 기계적으로 확인한다. <see cref="ThreadRow"/>에 새 원본 컬럼 속성을 추가하고 이 표를
/// 갱신하지 않으면 이 테스트가 실패한다 — "가공값(ResolvedTitle 등)이 원본을 대체하지 않는다"는
/// 정책도 함께 지킨다(원본 필드가 실제로 존재해야 하므로).
/// </summary>
public sealed class BackupConversationMetadataCoverageTests
{
    /// <summary>
    /// <see cref="ThreadRow"/> 속성 이름 → <see cref="BackupConversationMetadata"/> 속성 이름.
    /// <see cref="ThreadRow.Id"/>는 <see cref="BackupConversationMetadata.ThreadId"/>로,
    /// <see cref="ThreadRow.SandboxPolicy"/>는 opaque임을 강조하는 이름인
    /// <see cref="BackupConversationMetadata.SandboxPolicyRaw"/>로 대응된다 — 그 외에는 같은 이름이다.
    /// </summary>
    private static readonly Dictionary<string, string> ThreadRowToManifestProperty = new(StringComparer.Ordinal)
    {
        [nameof(ThreadRow.Id)] = nameof(BackupConversationMetadata.ThreadId),
        [nameof(ThreadRow.RolloutPath)] = nameof(BackupConversationMetadata.OriginalRolloutPath),
        [nameof(ThreadRow.Cwd)] = nameof(BackupConversationMetadata.OriginalCwd),
        [nameof(ThreadRow.Title)] = nameof(BackupConversationMetadata.Title),
        [nameof(ThreadRow.Name)] = nameof(BackupConversationMetadata.Name),
        [nameof(ThreadRow.FirstUserMessage)] = nameof(BackupConversationMetadata.FirstUserMessage),
        [nameof(ThreadRow.Preview)] = nameof(BackupConversationMetadata.Preview),
        [nameof(ThreadRow.ThreadSource)] = nameof(BackupConversationMetadata.ThreadSource),
        [nameof(ThreadRow.Source)] = nameof(BackupConversationMetadata.Source),
        [nameof(ThreadRow.ProjectId)] = nameof(BackupConversationMetadata.ProjectId),
        [nameof(ThreadRow.Archived)] = nameof(BackupConversationMetadata.Archived),
        [nameof(ThreadRow.ArchivedAtSeconds)] = nameof(BackupConversationMetadata.ArchivedAtSeconds),
        [nameof(ThreadRow.CreatedAtMs)] = nameof(BackupConversationMetadata.CreatedAtMs),
        [nameof(ThreadRow.UpdatedAtMs)] = nameof(BackupConversationMetadata.UpdatedAtMs),
        [nameof(ThreadRow.CreatedAtSeconds)] = nameof(BackupConversationMetadata.CreatedAtSeconds),
        [nameof(ThreadRow.UpdatedAtSeconds)] = nameof(BackupConversationMetadata.UpdatedAtSeconds),
        [nameof(ThreadRow.HistoryMode)] = nameof(BackupConversationMetadata.HistoryMode),
        [nameof(ThreadRow.CliVersion)] = nameof(BackupConversationMetadata.CliVersion),
        [nameof(ThreadRow.ModelProvider)] = nameof(BackupConversationMetadata.ModelProvider),
        [nameof(ThreadRow.Model)] = nameof(BackupConversationMetadata.Model),
        [nameof(ThreadRow.GitSha)] = nameof(BackupConversationMetadata.GitSha),
        [nameof(ThreadRow.GitBranch)] = nameof(BackupConversationMetadata.GitBranch),
        [nameof(ThreadRow.GitOriginUrl)] = nameof(BackupConversationMetadata.GitOriginUrl),
        [nameof(ThreadRow.SandboxPolicy)] = nameof(BackupConversationMetadata.SandboxPolicyRaw),
        [nameof(ThreadRow.ApprovalMode)] = nameof(BackupConversationMetadata.ApprovalMode),
        [nameof(ThreadRow.TokensUsed)] = nameof(BackupConversationMetadata.TokensUsed),
        [nameof(ThreadRow.HasUserEvent)] = nameof(BackupConversationMetadata.HasUserEvent),
        [nameof(ThreadRow.AgentNickname)] = nameof(BackupConversationMetadata.AgentNickname),
        [nameof(ThreadRow.AgentRole)] = nameof(BackupConversationMetadata.AgentRole),
        [nameof(ThreadRow.AgentPath)] = nameof(BackupConversationMetadata.AgentPath),
        [nameof(ThreadRow.MemoryMode)] = nameof(BackupConversationMetadata.MemoryMode),
        [nameof(ThreadRow.ReasoningEffort)] = nameof(BackupConversationMetadata.ReasoningEffort),
        [nameof(ThreadRow.IsPinned)] = nameof(BackupConversationMetadata.IsPinned),
        [nameof(ThreadRow.ThreadSectionId)] = nameof(BackupConversationMetadata.ThreadSectionId),
        [nameof(ThreadRow.SectionPosition)] = nameof(BackupConversationMetadata.SectionPosition),
        [nameof(ThreadRow.SectionEnteredAtMs)] = nameof(BackupConversationMetadata.SectionEnteredAtMs),
        [nameof(ThreadRow.RecencyAtSeconds)] = nameof(BackupConversationMetadata.RecencyAtSeconds),
        [nameof(ThreadRow.RecencyAtMs)] = nameof(BackupConversationMetadata.RecencyAtMs),
    };

    [Fact]
    public void ThreadRow의_모든_속성이_BackupConversationMetadata에_보존된다()
    {
        PropertyInfo[] threadRowProperties = typeof(ThreadRow).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var threadRowPropertyNames = new HashSet<string>(threadRowProperties.Select(p => p.Name), StringComparer.Ordinal);

        // 매핑표가 ThreadRow의 실제 속성 전부를 다루는지(=새 컬럼을 ThreadRow에 추가하고 이 표를
        // 잊었는지) 먼저 확인한다.
        var unmappedThreadRowProperties = threadRowPropertyNames.Except(ThreadRowToManifestProperty.Keys).ToList();
        Assert.True(
            unmappedThreadRowProperties.Count == 0,
            $"ThreadRow에 있지만 manifest 매핑표에 없는 속성(38컬럼 보존 정책 위반 가능): {string.Join(", ", unmappedThreadRowProperties)}");

        PropertyInfo[] manifestProperties = typeof(BackupConversationMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var manifestPropertyNames = new HashSet<string>(manifestProperties.Select(p => p.Name), StringComparer.Ordinal);

        foreach ((string threadRowProperty, string manifestProperty) in ThreadRowToManifestProperty)
        {
            Assert.True(
                manifestPropertyNames.Contains(manifestProperty),
                $"ThreadRow.{threadRowProperty}에 대응하는 BackupConversationMetadata.{manifestProperty}가 없습니다.");
        }
    }
}
