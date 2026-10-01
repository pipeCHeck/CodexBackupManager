using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Codex.Revisions;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.Restore;

/// <summary>
/// 모든 mutation이 끝난 뒤, Codex Home을 처음부터 다시 읽어 실제로 목표 상태에 도달했는지 확인한다
/// (Phase 7/07_01 요구사항 17/10). 여기서 relation을 새로 "판정"하지 않는다 — frozen
/// <see cref="ImportConversationPrecondition.ExpectedIncomingRevision"/>과 실행된
/// <see cref="RestoreOperationPlan"/>이 실제로 만들어 낸 결과가 정확히 일치하는지만 본다.
/// </summary>
public static class RestoreValidator
{
    /// <summary>검증 결과.</summary>
    public sealed record Result(bool Success, string? FailureReason);

    /// <param name="plan">frozen ImportPlan.</param>
    /// <param name="executedPlan">
    /// 실제로 실행한 <see cref="RestoreOperationPlan"/>(Phase 07_01 신규) — 기대한 rollout_path/
    /// project_id/metadata 갱신 결과가 실제로 반영됐는지 이 값과 대조한다. relation을 다시 판정하지
    /// 않는다는 원칙은 그대로 유지한다 — 이미 실행하기로 확정된 계획과 결과만 비교한다.
    /// </param>
    /// <param name="legacyProjectsAdded">
    /// (Phase 9_5a-03) global-state 레거시 저장소에 추가한 항목. 있으면 fresh 카탈로그에서 그 레거시 ID가 DB 프로젝트와 한 프로젝트로 보이는지 본다.
    /// </param>
    public static Result Validate(
        ImportPlan plan, RestoreOperationPlan executedPlan, string codexHomePath, CancellationToken cancellationToken = default,
        IReadOnlyList<GlobalStateAddedProject>? legacyProjectsAdded = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        var detection = new CodexDetectionService().DetectFromUserSelection(codexHomePath);
        if (detection.Installation is not { } installation)
        {
            return new Result(false, "복원 후 Codex Home을 다시 읽을 수 없습니다.");
        }

        CodexCatalog catalog = CodexCatalogBuilder.Build(installation, cancellationToken);

        Dictionary<string, PlannedThreadInsert> insertsByThreadId = executedPlan.ThreadInserts
            .ToDictionary(i => i.Source.ThreadId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PlannedThreadRolloutPathUpdate> pathUpdatesByThreadId = executedPlan.ThreadRolloutPathUpdates
            .ToDictionary(u => u.ThreadId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PlannedThreadMetadataUpdate> metadataUpdatesByThreadId = executedPlan.ThreadMetadataUpdates
            .ToDictionary(u => u.ThreadId, StringComparer.OrdinalIgnoreCase);

        foreach (ImportPlanConversation conversation in plan.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Phase 9_2-21 — 사용자 선택으로 뺀 대화는 이번에 쓰지 않았으므로 사후 검증 대상이 아니다(Skip이라 원래도 빠진다).
            if (conversation.IsExcludedFromApply ||
                conversation.PlannedAction is not (ImportPlannedAction.Import or ImportPlannedAction.Update))
            {
                continue;
            }

            ConversationEntry? localEntry = catalog.AllConversations
                .FirstOrDefault(e => string.Equals(e.ThreadId, conversation.ThreadId, StringComparison.OrdinalIgnoreCase));
            if (localEntry is null)
            {
                return Fail(conversation.ThreadId, "복원됐어야 할 대화가 카탈로그(DB 행)에 없습니다.");
            }

            if (!catalog.Chains.ContainsKey(conversation.ThreadId))
            {
                return Fail(conversation.ThreadId, "복원됐어야 할 대화의 rollout 체인을 찾을 수 없습니다.");
            }

            // ── rollout_path: 기대한 leaf 파일을 실제로 가리키고, 그 파일이 존재하는지 ──
            string? expectedLeafPath = insertsByThreadId.TryGetValue(conversation.ThreadId, out PlannedThreadInsert? insert)
                ? insert.ResolvedRolloutPathAbsolute
                : pathUpdatesByThreadId.TryGetValue(conversation.ThreadId, out PlannedThreadRolloutPathUpdate? pathUpdate)
                    ? pathUpdate.NewRolloutPathAbsolute
                    : null;

            if (expectedLeafPath is not null)
            {
                if (!string.Equals(localEntry.Row.RolloutPath, expectedLeafPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(conversation.ThreadId, "rollout_path가 기대한 leaf 파일을 가리키지 않습니다.");
                }

                if (!File.Exists(expectedLeafPath))
                {
                    return Fail(conversation.ThreadId, "rollout_path가 가리키는 파일이 존재하지 않습니다.");
                }
            }
            else if (localEntry.Row.RolloutPath is null || !File.Exists(localEntry.Row.RolloutPath))
            {
                return Fail(conversation.ThreadId, "rollout_path 파일이 존재하지 않습니다.");
            }

            // ── revision fingerprint: 논리적 내용이 frozen ExpectedIncomingRevision과 정확히 같은지 ──
            ConversationRevision? expectedRevision = conversation.Precondition.ExpectedIncomingRevision;
            if (expectedRevision is not null)
            {
                ConversationRevisionBuildResult current = ConversationRevisionBuilder.Build(
                    conversation.ThreadId, catalog.Chains, LocalFileRolloutSliceReader.Instance, cancellationToken);

                if (current.Revision is null || !RevisionFingerprintEquals(current.Revision, expectedRevision))
                {
                    return Fail(conversation.ThreadId, "복원 후 대화 내용이 기대한 것과 다릅니다.");
                }
            }

            // ── New Import 전용: 필수 metadata gate가 실제로 지켜졌는지 ──
            if (insert is not null)
            {
                if (string.IsNullOrWhiteSpace(localEntry.Row.ModelProvider))
                {
                    return Fail(conversation.ThreadId, "model_provider가 비어 있습니다(resume 실패 위험).");
                }

                if (conversation.IsSelected && string.IsNullOrWhiteSpace(localEntry.Row.ThreadSource))
                {
                    return Fail(conversation.ThreadId, "thread_source가 비어 있습니다(목록 노출 실패 위험).");
                }

                if (localEntry.Row.Archived != insert.Source.Archived)
                {
                    return Fail(conversation.ThreadId, "archived 상태가 기대한 값과 다릅니다.");
                }
            }

            // ── metadata 병합 결과가 실제로 반영됐는지 ──
            if (metadataUpdatesByThreadId.TryGetValue(conversation.ThreadId, out PlannedThreadMetadataUpdate? metadataUpdate))
            {
                if (metadataUpdate.UpdatedAtSeconds is { } expectedUpdatedAt && localEntry.Row.UpdatedAtSeconds != expectedUpdatedAt)
                {
                    return Fail(conversation.ThreadId, "updated_at 병합 결과가 기대와 다릅니다.");
                }

                if (metadataUpdate.TokensUsed is { } expectedTokens && localEntry.Row.TokensUsed != expectedTokens)
                {
                    return Fail(conversation.ThreadId, "tokens_used 병합 결과가 기대와 다릅니다.");
                }

                if (metadataUpdate.HasUserEvent is { } expectedHasUserEvent && localEntry.Row.HasUserEvent != expectedHasUserEvent)
                {
                    return Fail(conversation.ThreadId, "has_user_event 병합 결과가 기대와 다릅니다.");
                }

                if (metadataUpdate.NameIfLocalMissing is { } expectedName &&
                    !string.Equals(localEntry.Row.Name, expectedName, StringComparison.Ordinal))
                {
                    return Fail(conversation.ThreadId, "name 병합 결과가 기대와 다릅니다.");
                }
            }

            // ── project_id: TargetProjectPath가 있고 로컬 프로젝트로 실제 해결됐다면, 그 배정이
            //    조용히 무시되지 않고 정확히 반영됐는지 확인한다. 로컬에 대응하는 프로젝트가 아예
            //    없어서 배정 자체를 못 한 경우(§1.C 알려진 제약)는 실패로 보지 않는다 — Preview/Plan
            //    단계에서 이미 Warning으로 드러나야 하는 것이지 여기서 처음 발견해 조용히 넘기지
            //    않는다는 것이 요구사항의 취지이므로, "배정 못 함" 자체는 검증 대상에서 제외한다.
            if (insert?.ResolvedProjectId is { } expectedProjectId &&
                !string.Equals(localEntry.Row.ProjectId, expectedProjectId, StringComparison.Ordinal))
            {
                return Fail(conversation.ThreadId, "project_id가 기대한 프로젝트로 배정되지 않았습니다.");
            }

            // ── Phase 9_1-12: New Import 행의 project_id/cwd가 계획과 정확히 같은지 양방향으로 확인한다.
            //    기타 대화로 계획했으면(ResolvedProjectId=null) 실제 행도 NULL이어야 하고, cwd는 계획한 값
            //    (연결 시 대상 루트, 아니면 백업 원본 cwd — StateDatabaseWriter와 같은 규칙)이어야 한다.
            if (insert is not null)
            {
                if (insert.ResolvedProjectId is null && localEntry.Row.ProjectId is not null)
                {
                    return Fail(conversation.ThreadId, "기타 대화로 계획한 대화에 project_id가 기록되어 있습니다.");
                }

                string? expectedCwd = insert.ResolvedTargetCwd ?? insert.Source.OriginalCwd;
                if (expectedCwd is not null && !string.Equals(localEntry.Row.Cwd, expectedCwd, StringComparison.Ordinal))
                {
                    return Fail(conversation.ThreadId, "cwd가 계획한 값과 다릅니다.");
                }
            }
        }

        // Phase 9_5-04 — 새로 만든 프로젝트: 행·루트·키가 계획대로 있고, fresh 카탈로그의 프로젝트 목록에 그 ID와 루트로 나타나는지.
        if (executedPlan.ProjectCreates.Count > 0)
        {
            Result? projectResult = ValidateProjectCreates(executedPlan, codexHomePath, catalog);
            if (projectResult is not null)
            {
                return projectResult;
            }
        }

        // Phase 9_3-04 — 옮긴 대화: 행의 project_id·cwd가 새 값이고, fresh 카탈로그에서 그 대화가 목적지 프로젝트 그룹 아래 있다.
        foreach (PlannedThreadProjectLink link in executedPlan.ThreadProjectLinks)
        {
            ConversationEntry? entry = catalog.AllConversations
                .FirstOrDefault(e => string.Equals(e.ThreadId, link.ThreadId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return Fail(link.ThreadId, "옮긴 대화를 다시 읽을 수 없습니다.");
            }

            if (!string.Equals(entry.Row.ProjectId, link.NewProjectId, StringComparison.Ordinal) ||
                !string.Equals(entry.Row.Cwd, link.NewCwd, StringComparison.Ordinal))
            {
                return Fail(link.ThreadId, "옮긴 대화의 project_id 또는 cwd가 계획과 다릅니다.");
            }

            string? expectedGroupId = catalog.ProjectDirectory.FindById(link.NewProjectId)?.PrimaryId;
            ProjectEntry? group = catalog.Projects.FirstOrDefault(p => p.Conversations.Any(c => string.Equals(c.ThreadId, link.ThreadId, StringComparison.OrdinalIgnoreCase)));
            if (expectedGroupId is null || group is null || !string.Equals(group.ProjectId, expectedGroupId, StringComparison.Ordinal))
            {
                return Fail(link.ThreadId, "옮긴 대화가 목적지 프로젝트 그룹 아래에 나타나지 않습니다.");
            }
        }

        if (legacyProjectsAdded is { Count: > 0 } &&
            GlobalStateProjectStep.VerifyMerged(catalog.ProjectDirectory, legacyProjectsAdded) is { } mergeError)
        {
            return new Result(false, mergeError);
        }

        return new Result(true, null);
    }

    private static Result? ValidateProjectCreates(RestoreOperationPlan executedPlan, string codexHomePath, CodexCatalog catalog)
    {
        IReadOnlyList<string> stateDbNames = CodexBackupManager.Codex.Locating.CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath);
        if (stateDbNames.Count == 0)
        {
            return new Result(false, "복원 후 state DB를 찾을 수 없습니다.");
        }

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(codexHomePath, stateDbNames[0]),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
        };

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();

        foreach (PlannedProjectCreate create in executedPlan.ProjectCreates)
        {
            string label = Redact(create.NewProjectId);

            using (Microsoft.Data.Sqlite.SqliteCommand project = connection.CreateCommand())
            {
                project.CommandText = "SELECT name FROM projects WHERE id = $id";
                project.Parameters.AddWithValue("$id", create.NewProjectId);
                if (project.ExecuteScalar() is not string name || !string.Equals(name, create.Name, StringComparison.Ordinal))
                {
                    return new Result(false, $"{label}: 새 프로젝트 행이 없거나 이름이 다릅니다.");
                }
            }

            using (Microsoft.Data.Sqlite.SqliteCommand root = connection.CreateCommand())
            {
                root.CommandText = "SELECT path FROM project_roots WHERE project_id = $id AND position = 0";
                root.Parameters.AddWithValue("$id", create.NewProjectId);
                if (root.ExecuteScalar() is not string path || !string.Equals(path, create.RootPathDisplay, StringComparison.Ordinal))
                {
                    return new Result(false, $"{label}: 새 프로젝트 루트 행이 없거나 경로가 다릅니다.");
                }
            }

            using (Microsoft.Data.Sqlite.SqliteCommand key = connection.CreateCommand())
            {
                key.CommandText = "SELECT project_id FROM project_idempotency_keys WHERE key = $key";
                key.Parameters.AddWithValue("$key", create.IdempotencyKey);
                if (key.ExecuteScalar() is not string keyProjectId || !string.Equals(keyProjectId, create.NewProjectId, StringComparison.Ordinal))
                {
                    return new Result(false, $"{label}: 새 프로젝트 멱등성 키 행이 없거나 다른 프로젝트를 가리킵니다.");
                }
            }

            KnownProjectMatch match = MatchInDirectory(catalog, create);
            if (match != KnownProjectMatch.Found)
            {
                return new Result(false, $"{label}: 새 프로젝트가 프로젝트 목록에 그 루트로 나타나지 않습니다({match}).");
            }
        }

        return null;
    }

    private enum KnownProjectMatch
    {
        Found,
        MissingById,
        RootNotFound,
        RootPointsElsewhere,
    }

    private static KnownProjectMatch MatchInDirectory(CodexCatalog catalog, PlannedProjectCreate create)
    {
        Domain.Codex.Projects.KnownProject? known = catalog.ProjectDirectory.FindById(create.NewProjectId);
        if (known is null || !string.Equals(known.DbProjectId, create.NewProjectId, StringComparison.Ordinal))
        {
            return KnownProjectMatch.MissingById;
        }

        if (!Domain.Paths.CanonicalPath.TryCreate(create.RootPathDisplay, out Domain.Paths.CanonicalPath? root, out _))
        {
            return KnownProjectMatch.RootNotFound;
        }

        Domain.Codex.Projects.ProjectLookupResult lookup = catalog.ProjectDirectory.FindByRoot(root!);
        return lookup.Kind switch
        {
            Domain.Codex.Projects.ProjectLookupKind.Found when ReferenceEquals(lookup.Project, known) => KnownProjectMatch.Found,
            Domain.Codex.Projects.ProjectLookupKind.None => KnownProjectMatch.RootNotFound,
            _ => KnownProjectMatch.RootPointsElsewhere,
        };
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

    private static Result Fail(string threadId, string reason) => new(false, $"{Redact(threadId)}: {reason}");

    private static string Redact(string threadId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(threadId)))[..12];
}
