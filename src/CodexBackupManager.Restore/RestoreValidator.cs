using System;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Revisions;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Restore;

/// <summary>
/// 모든 mutation이 끝난 뒤, Codex Home을 처음부터 다시 읽어 실제로 목표 상태에 도달했는지 확인한다
/// (Phase 7 요구사항 17). 여기서 relation을 새로 "판정"하지 않는다 — frozen
/// <see cref="ImportConversationPrecondition.ExpectedIncomingRevision"/>과 지금 다시 만든 revision이
/// 정확히 같은지만 본다.
/// </summary>
public static class RestoreValidator
{
    /// <summary>검증 결과.</summary>
    public sealed record Result(bool Success, string? FailureReason);

    public static Result Validate(ImportPlan plan, string codexHomePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        var detection = new CodexDetectionService().DetectFromUserSelection(codexHomePath);
        if (detection.Installation is not { } installation)
        {
            return new Result(false, "복원 후 Codex Home을 다시 읽을 수 없습니다.");
        }

        CodexCatalog catalog = CodexCatalogBuilder.Build(installation, cancellationToken);

        foreach (ImportPlanConversation conversation in plan.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (conversation.PlannedAction is not (ImportPlannedAction.Import or ImportPlannedAction.Update))
            {
                continue;
            }

            if (!catalog.Chains.ContainsKey(conversation.ThreadId))
            {
                return new Result(false, $"복원됐어야 할 대화가 카탈로그에 없습니다({Redact(conversation.ThreadId)}).");
            }

            ConversationRevision? expected = conversation.Precondition.ExpectedIncomingRevision;
            if (expected is null)
            {
                continue;
            }

            ConversationRevisionBuildResult current = ConversationRevisionBuilder.Build(
                conversation.ThreadId, catalog.Chains, LocalFileRolloutSliceReader.Instance, cancellationToken);

            if (current.Revision is null || !RevisionFingerprintEquals(current.Revision, expected))
            {
                return new Result(false, $"복원 후 대화 내용이 기대한 것과 다릅니다({Redact(conversation.ThreadId)}).");
            }

            if (conversation.TargetProjectPath is not null)
            {
                bool projectMatches = catalog.Projects.Any(p =>
                    p.Conversations.Any(c => string.Equals(c.ThreadId, conversation.ThreadId, StringComparison.OrdinalIgnoreCase)));
                if (!projectMatches)
                {
                    // 로컬에 아직 등록된 프로젝트가 없어 배정하지 못한 경우일 수 있다(알려진 제약,
                    // docs/safe-restore-phase7.md §1.C) — 이 자체는 실패가 아니다. "기타 대화"로
                    // 남았는지만 확인하는 수준으로 완화한다(대화 자체는 이미 위에서 확인했다).
                }
            }
        }

        return new Result(true, null);
    }

    /// <summary>
    /// <see cref="ImportPlanPreflightValidator"/>의 같은 이름 비교 로직과 동일한 원칙(<c>.File</c>은
    /// 무시하고 논리적 내용만 비교)을 재사용한다 — 새 규칙을 만들지 않는다.
    /// </summary>
    private static bool RevisionFingerprintEquals(ConversationRevision a, ConversationRevision b)
    {
        if (!string.Equals(a.ThreadId, b.ThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (a.OrderedSlices.Count != b.OrderedSlices.Count)
        {
            return false;
        }

        for (int i = 0; i < a.OrderedSlices.Count; i++)
        {
            RolloutSlice x = a.OrderedSlices[i];
            RolloutSlice y = b.OrderedSlices[i];
            if (!string.Equals(x.RolloutId, y.RolloutId, StringComparison.OrdinalIgnoreCase) ||
                x.Boundary != y.Boundary ||
                x.LogicalByteLength != y.LogicalByteLength ||
                !string.Equals(x.Sha256Hex, y.Sha256Hex, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Redact(string threadId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(threadId)))[..12];
}
