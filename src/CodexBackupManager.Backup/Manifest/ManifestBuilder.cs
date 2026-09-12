using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Backup.Manifest;

/// <summary><see cref="ExportPlan"/>을 <see cref="BackupManifest"/>(순수 데이터, I/O 없음)로 변환한다.</summary>
public static class ManifestBuilder
{
    /// <summary>Manifest를 만든다.</summary>
    /// <param name="plan">계획.</param>
    /// <param name="sourceCodexDesktopVersion">Export 시점의 Codex Desktop 버전.</param>
    /// <param name="sourceCodexCliVersion">Export 시점의 Codex CLI 버전.</param>
    /// <param name="createdAtUtc">Export 시각.</param>
    public static BackupManifest Build(
        ExportPlan plan,
        string? sourceCodexDesktopVersion,
        string? sourceCodexCliVersion,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<string, string> rolloutEntryByPath = plan.RolloutFiles
            .ToDictionary(f => f.SourceFullPath, f => f.EntryPath, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> attachmentEntryByPath = plan.Attachments
            .ToDictionary(a => a.SourceFullPath, a => a.EntryPath, StringComparer.OrdinalIgnoreCase);

        // Phase 05_01: 원본 절대경로 ↔ entry 경로의 명시적 역매핑을 top-level에 별도로 남긴다 —
        // conversation의 PayloadAttachmentEntries만으로는 이 관계를 되짚을 수 없다(같은 basename이
        // 다른 디렉터리에 있었을 수 있다).
        List<BackupAttachmentMetadata> attachments = plan.Attachments
            .Select(a => new BackupAttachmentMetadata { EntryPath = a.EntryPath, OriginalAbsolutePath = a.SourceFullPath })
            .ToList();

        var conversations = new List<BackupConversationMetadata>(plan.Conversations.Count);
        foreach (PlannedConversation c in plan.Conversations)
        {
            ThreadRow? row = c.Entry?.Row;
            List<string> rolloutEntries = c.RolloutFiles
                .Select(f => rolloutEntryByPath[f.FullPath])
                .ToList();
            List<string> attachmentEntries = c.AttachmentAbsolutePaths
                .Where(attachmentEntryByPath.ContainsKey)
                .Select(p => attachmentEntryByPath[p])
                .ToList();

            conversations.Add(new BackupConversationMetadata
            {
                ThreadId = c.ThreadId,
                IsSelected = c.IsSelected,
                ResolvedTitle = c.Entry?.Title.Text,
                TitleSource = c.Entry?.Title.Source.ToString(),
                ProjectId = row?.ProjectId,
                ResolvedProjectId = c.Entry?.Project.ProjectId,
                OriginalCwd = row?.Cwd,
                OriginalRolloutPath = row?.RolloutPath,
                Source = row?.Source,
                Title = row?.Title,
                Name = row?.Name,
                FirstUserMessage = row?.FirstUserMessage,
                Preview = row?.Preview,
                CreatedAtSeconds = row?.CreatedAtSeconds,
                CreatedAtMs = row?.CreatedAtMs,
                CreatedAtUtc = c.Entry?.CreatedAtUtc,
                UpdatedAtSeconds = row?.UpdatedAtSeconds,
                UpdatedAtMs = row?.UpdatedAtMs,
                UpdatedAtUtc = c.Entry?.UpdatedAtUtc,
                RecencyAtSeconds = row?.RecencyAtSeconds,
                RecencyAtMs = row?.RecencyAtMs,
                Archived = row?.Archived ?? false,
                ArchivedAtSeconds = row?.ArchivedAtSeconds,
                ArchivedAtUtc = row?.ArchivedAtSeconds is { } archivedAt
                    ? DateTimeOffset.FromUnixTimeSeconds(archivedAt)
                    : null,
                HistoryMode = row?.HistoryMode,
                ThreadSource = row?.ThreadSource,
                CliVersion = row?.CliVersion,
                ModelProvider = row?.ModelProvider,
                Model = row?.Model,
                ReasoningEffort = row?.ReasoningEffort,
                MemoryMode = row?.MemoryMode,
                SandboxPolicyRaw = row?.SandboxPolicy,
                ApprovalMode = row?.ApprovalMode,
                TokensUsed = row?.TokensUsed,
                HasUserEvent = row?.HasUserEvent,
                GitSha = row?.GitSha,
                GitBranch = row?.GitBranch,
                GitOriginUrl = row?.GitOriginUrl,
                AgentNickname = row?.AgentNickname,
                AgentRole = row?.AgentRole,
                AgentPath = row?.AgentPath,
                IsPinned = row?.IsPinned,
                ThreadSectionId = row?.ThreadSectionId,
                SectionPosition = row?.SectionPosition,
                SectionEnteredAtMs = row?.SectionEnteredAtMs,
                PayloadRolloutEntries = rolloutEntries,
                PayloadAttachmentEntries = attachmentEntries,
            });
        }

        var projects = plan.Projects
            .Select(p => new BackupProjectMetadata
            {
                ProjectId = p.ProjectId,
                DisplayName = p.DisplayName,
                OriginalRootPaths = p.OriginalRootPaths,
                ConversationThreadIds = p.SelectedThreadIds,
            })
            .ToList();

        return new BackupManifest
        {
            BackupFormatVersion = BackupManifest.CurrentFormatVersion,
            AppVersion = GetAppVersion(),
            CreatedAtUtc = createdAtUtc,
            SourceOS = RuntimeInformation.OSDescription,
            SourceCodexDesktopVersion = sourceCodexDesktopVersion,
            SourceCodexCliVersion = sourceCodexCliVersion,
            ConversationCount = plan.SelectedConversationCount,
            DependencyConversationCount = plan.DependencyConversationCount,
            ProjectCount = projects.Count,
            PayloadCount = plan.RolloutFiles.Count + plan.Attachments.Count,
            Projects = projects,
            Conversations = conversations,
            Attachments = attachments,
            Warnings = plan.Warnings,
        };
    }

    private static string GetAppVersion()
        => typeof(ManifestBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
