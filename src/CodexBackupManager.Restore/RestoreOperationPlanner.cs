using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>
/// frozen <see cref="ImportPlan"/> + fresh 로컬 <see cref="CodexCatalog"/>로부터 실제 실행할
/// <see cref="RestoreOperationPlan"/>을 계산한다(Phase 7 요구사항 8). <b>relation/PlannedAction을
/// 다시 판정하지 않는다</b> — <see cref="ImportPlanConversation.Relation"/>을 그대로 신뢰하고,
/// 물리 바이트가 그 판정과 지금도 안전하게 들어맞는지만 재확인한다.
/// </summary>
/// <remarks>
/// 조금이라도 안전을 증명할 수 없는 항목이 있으면(스키마 비호환, New의 <c>model_provider</c> 누락,
/// IncomingAhead의 물리적 tail 위험/압축 append/다른 thread의 history_base 조상으로 참조되는 파일)
/// <b>계획 전체를 거부</b>한다(<c>null</c>) — 일부만 적용하는 부분 성공 상태를 만들지 않는다
/// (Phase 5 "부분 성공 금지" 정책과 동일).
/// </remarks>
public static class RestoreOperationPlanner
{
    /// <summary>계획을 만든다. Codex에는 어떤 것도 쓰지 않는다(읽기만 한다).</summary>
    public static RestoreOperationPlanResult Build(
        ImportPlan plan,
        CodexCatalog freshLocalCatalog,
        string codexHomePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(freshLocalCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        if (plan.HasBlockingIssues || plan.HasUnresolvedDivergence)
        {
            return Reject("Blocked 또는 분기 충돌(Diverged)이 있는 Plan은 계획을 만들지 않습니다.");
        }

        IReadOnlyList<string> stateDbNames = CodexHomeLayout.FindStateDatabaseFileNames(codexHomePath);
        if (stateDbNames.Count == 0)
        {
            return Reject("state DB 파일을 찾을 수 없습니다.");
        }

        string stateDbPath = Path.Combine(codexHomePath, stateDbNames[0]);
        SchemaCompatibilityChecker.Result schema = SchemaCompatibilityChecker.CheckFile(stateDbPath);
        if (!schema.IsCompatible)
        {
            return Reject($"현재 state DB 스키마와 호환되지 않습니다: {string.Join(", ", schema.MissingColumns)}");
        }

        using BackupReader reader = BackupReader.Open(plan.Backup.BackupFilePath);
        BackupManifest manifest = reader.ReadManifest();
        BackupCatalogReader.Result backupCatalog = BackupCatalogReader.Build(reader, cancellationToken);
        var backupSliceReader = new BackupRolloutSliceReader(reader);

        Dictionary<string, BackupConversationMetadata> metadataByThreadId = manifest.Conversations
            .ToDictionary(c => c.ThreadId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ImportPlannedAction> actionByThreadId = plan.Conversations
            .ToDictionary(c => c.ThreadId, c => c.PlannedAction, StringComparer.OrdinalIgnoreCase);

        var newRolloutFiles = new List<PlannedNewRolloutFile>();
        var rolloutAppends = new List<PlannedRolloutAppend>();
        var threadInserts = new List<PlannedThreadInsert>();
        var rolloutPathUpdates = new List<PlannedThreadRolloutPathUpdate>();
        var copiedEntryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejections = new List<string>();

        foreach (ImportPlanConversation conversation in plan.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (conversation.PlannedAction)
            {
                case ImportPlannedAction.Import:
                    PlanNewImport(
                        conversation, metadataByThreadId, actionByThreadId, reader, codexHomePath,
                        newRolloutFiles, threadInserts, copiedEntryPaths, freshLocalCatalog, rejections);
                    break;

                case ImportPlannedAction.Update:
                    PlanFastForward(
                        conversation, freshLocalCatalog, backupCatalog, backupSliceReader, codexHomePath,
                        newRolloutFiles, rolloutAppends, rolloutPathUpdates, rejections, cancellationToken);
                    break;

                case ImportPlannedAction.NoOp:
                case ImportPlannedAction.Skip:
                    break;

                case ImportPlannedAction.RequiresDecision:
                case ImportPlannedAction.Blocked:
                default:
                    rejections.Add($"{Redact(conversation.ThreadId)}: 예상치 못한 PlannedAction({conversation.PlannedAction})입니다.");
                    break;
            }
        }

        if (rejections.Count > 0)
        {
            return new RestoreOperationPlanResult(null, rejections);
        }

        var restorePlan = new RestoreOperationPlan(newRolloutFiles, rolloutAppends, threadInserts, rolloutPathUpdates);
        return new RestoreOperationPlanResult(restorePlan, []);
    }

    private static void PlanNewImport(
        ImportPlanConversation conversation,
        Dictionary<string, BackupConversationMetadata> metadataByThreadId,
        Dictionary<string, ImportPlannedAction> actionByThreadId,
        BackupReader reader,
        string codexHomePath,
        List<PlannedNewRolloutFile> newRolloutFiles,
        List<PlannedThreadInsert> threadInserts,
        HashSet<string> copiedEntryPaths,
        CodexCatalog freshLocalCatalog,
        List<string> rejections)
    {
        if (!metadataByThreadId.TryGetValue(conversation.ThreadId, out BackupConversationMetadata? metadata))
        {
            rejections.Add($"{Redact(conversation.ThreadId)}: backup manifest에서 metadata를 찾을 수 없습니다.");
            return;
        }

        if (string.IsNullOrWhiteSpace(metadata.ModelProvider))
        {
            rejections.Add($"{Redact(conversation.ThreadId)}: model_provider가 비어 있어 New Import를 할 수 없습니다(resume 실패 위험).");
            return;
        }

        // New existence precondition은 이미 Preflight가 확인했다 — 여기서는 다시 판정하지 않는다.
        // 로컬에 이미 같은 threadId 프로젝트 항목이 있으면(방어적), 물리 파일 위치 충돌을 피하기 위해
        // 중복 INSERT를 만들지 않는다.
        if (freshLocalCatalog.AllConversations.Any(e => string.Equals(e.ThreadId, conversation.ThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            rejections.Add($"{Redact(conversation.ThreadId)}: 로컬에 이미 같은 thread가 존재합니다(New precondition 재확인 실패).");
            return;
        }

        string? leafTargetPath = null;
        foreach (string entryPath in metadata.PayloadRolloutEntries)
        {
            string fileName = Path.GetFileName(entryPath);
            RolloutFileNamePattern.ParsedRolloutFileName? parsed = RolloutFileNamePattern.TryParse(fileName);
            if (parsed is null)
            {
                rejections.Add($"{Redact(conversation.ThreadId)}: backup entry 파일명을 해석할 수 없습니다.");
                return;
            }

            // 이 entry의 실제 소유 thread(파일명 앞부분)가 New가 아니면(이미 로컬에 존재하므로) 다시
            // 복사하지 않는다 — 그 thread의 물리 파일이 이미 로컬에 있다고 신뢰한다(Preflight가 그
            // precondition을 이미 확인했다).
            bool owningThreadIsNew = actionByThreadId.TryGetValue(parsed.ThreadId, out ImportPlannedAction owningAction)
                ? owningAction == ImportPlannedAction.Import
                : false;

            bool isLeafEntry = entryPath == metadata.PayloadRolloutEntries[^1];
            if (isLeafEntry)
            {
                string leafDir = ResolveTargetDirectory(codexHomePath, metadata.Archived, parsed.Timestamp);
                leafTargetPath = Path.Combine(leafDir, fileName);
            }

            if (!owningThreadIsNew)
            {
                continue;
            }

            if (!copiedEntryPaths.Add(entryPath))
            {
                continue;
            }

            long? entryLength = reader.GetEntryLength(entryPath);
            if (entryLength is null)
            {
                rejections.Add($"{Redact(conversation.ThreadId)}: backup entry를 찾을 수 없습니다.");
                return;
            }

            string entryHash = HashEntry(reader, entryPath);
            bool ownerArchived = metadataByThreadId.TryGetValue(parsed.ThreadId, out BackupConversationMetadata? owner) && owner.Archived;
            string targetDir = ResolveTargetDirectory(codexHomePath, ownerArchived, parsed.Timestamp);

            newRolloutFiles.Add(new PlannedNewRolloutFile(
                parsed.ThreadId, entryPath, Path.Combine(targetDir, fileName), entryLength.Value, entryHash));
        }

        if (leafTargetPath is null)
        {
            rejections.Add($"{Redact(conversation.ThreadId)}: leaf rollout entry를 확정할 수 없습니다.");
            return;
        }

        string? resolvedProjectId = ResolveLocalProjectId(conversation.TargetProjectPath, freshLocalCatalog);
        threadInserts.Add(new PlannedThreadInsert(metadata, leafTargetPath, resolvedProjectId));
    }

    private static void PlanFastForward(
        ImportPlanConversation conversation,
        CodexCatalog freshLocalCatalog,
        BackupCatalogReader.Result backupCatalog,
        IRolloutSliceReader backupSliceReader,
        string codexHomePath,
        List<PlannedNewRolloutFile> newRolloutFiles,
        List<PlannedRolloutAppend> rolloutAppends,
        List<PlannedThreadRolloutPathUpdate> rolloutPathUpdates,
        List<string> rejections,
        CancellationToken cancellationToken)
    {
        string threadId = conversation.ThreadId;
        ConversationRevision? expectedLocal = conversation.Precondition.ExpectedLocalRevision;
        ConversationRevision? expectedIncoming = conversation.Precondition.ExpectedIncomingRevision;

        if (expectedLocal is null || expectedIncoming is null || expectedLocal.OrderedSlices.Count == 0)
        {
            rejections.Add($"{Redact(threadId)}: IncomingAhead인데 frozen revision 정보가 없습니다.");
            return;
        }

        if (!freshLocalCatalog.Chains.TryGetValue(threadId, out ThreadChain? localChain))
        {
            rejections.Add($"{Redact(threadId)}: 로컬에서 이 thread의 rollout 체인을 다시 찾을 수 없습니다.");
            return;
        }

        RolloutSlice leafLocalSlice = expectedLocal.OrderedSlices[^1];
        RolloutFileReference? currentFile = localChain.Files
            .FirstOrDefault(f => string.Equals(f.OwnRolloutId, leafLocalSlice.RolloutId, StringComparison.OrdinalIgnoreCase));
        if (currentFile is null)
        {
            rejections.Add($"{Redact(threadId)}: 로컬 leaf 물리 파일을 찾을 수 없습니다.");
            return;
        }

        if (currentFile.Kind == RolloutFileKind.ZstdCompressed)
        {
            rejections.Add($"{Redact(threadId)}: 압축(.jsonl.zst) 파일에는 append를 시도하지 않습니다.");
            return;
        }

        // 물리적으로 지금 파일 전체가 frozen local 기대값과 정확히 같은지 재확인한다(cutoff 없음 —
        // leaf는 항상 Full이어야 한다).
        RolloutSliceHasher.Result actualLocal = LocalFileRolloutSliceReader.Instance
            .Hash(currentFile, null, null, cancellationToken);
        if (actualLocal.LogicalByteLength != leafLocalSlice.LogicalByteLength ||
            !string.Equals(actualLocal.Sha256Hex, leafLocalSlice.Sha256Hex, StringComparison.Ordinal))
        {
            rejections.Add($"{Redact(threadId)}: 로컬 파일에 논리적 cutoff 이후 여분 바이트가 있거나 내용이 달라 안전하게 append할 수 없습니다.");
            return;
        }

        // 이 물리 파일이 다른 어떤 thread의 history_base 조상으로도 참조되고 있지 않아야 한다
        // (공식 소스: 조상 rollout은 "immutable"로 취급된다 — docs/safe-restore-phase7.md §1.B).
        bool referencedAsAncestor = freshLocalCatalog.Chains.Values.Any(other =>
            !string.Equals(other.ThreadId, threadId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(other.ParentThreadId, leafLocalSlice.RolloutId, StringComparison.OrdinalIgnoreCase) ||
             other.FileHistoryBases.Any(h => h is not null && string.Equals(h.ThreadId, leafLocalSlice.RolloutId, StringComparison.OrdinalIgnoreCase))));
        if (referencedAsAncestor)
        {
            rejections.Add($"{Redact(threadId)}: 이 파일이 다른 대화의 조상(history_base)으로 참조되고 있어 append하지 않습니다.");
            return;
        }

        int leafIndexInIncoming = -1;
        for (int i = 0; i < expectedIncoming.OrderedSlices.Count; i++)
        {
            if (string.Equals(expectedIncoming.OrderedSlices[i].RolloutId, leafLocalSlice.RolloutId, StringComparison.OrdinalIgnoreCase))
            {
                leafIndexInIncoming = i;
                break;
            }
        }

        if (leafIndexInIncoming < 0)
        {
            rejections.Add($"{Redact(threadId)}: incoming revision에서 로컬 leaf와 대응하는 slice를 찾을 수 없습니다.");
            return;
        }

        RolloutSlice incomingLeafSlice = expectedIncoming.OrderedSlices[leafIndexInIncoming];
        if (incomingLeafSlice.LogicalByteLength > leafLocalSlice.LogicalByteLength)
        {
            if (incomingLeafSlice.File.Kind == RolloutFileKind.ZstdCompressed)
            {
                rejections.Add($"{Redact(threadId)}: 압축된 incoming slice에는 append를 시도하지 않습니다.");
                return;
            }

            string entryPath = incomingLeafSlice.File.FullPath;
            string incomingFullHash = HashBackupEntryFull(backupSliceReader, incomingLeafSlice);
            if (!string.Equals(incomingFullHash, incomingLeafSlice.Sha256Hex, StringComparison.Ordinal))
            {
                // 방어적 재확인 — Phase 06_02/06_03이 이미 backup identity로 확인했어야 한다.
                rejections.Add($"{Redact(threadId)}: incoming slice 재해시가 frozen 값과 다릅니다.");
                return;
            }

            rolloutAppends.Add(new PlannedRolloutAppend(
                threadId, currentFile.FullPath,
                leafLocalSlice.LogicalByteLength, leafLocalSlice.Sha256Hex,
                entryPath, leafLocalSlice.LogicalByteLength,
                incomingLeafSlice.LogicalByteLength, incomingLeafSlice.Sha256Hex));
        }

        bool leafFileChanged = false;
        string? newLeafTargetPath = null;
        for (int i = leafIndexInIncoming + 1; i < expectedIncoming.OrderedSlices.Count; i++)
        {
            RolloutSlice newSlice = expectedIncoming.OrderedSlices[i];
            string fileName = newSlice.File.FileName;
            string targetDir = ResolveTargetDirectory(codexHomePath, currentFile.IsArchived, RolloutFileNamePattern.TryParse(fileName)?.Timestamp);
            string targetPath = Path.Combine(targetDir, fileName);
            string entryPath = newSlice.File.FullPath;

            newRolloutFiles.Add(new PlannedNewRolloutFile(threadId, entryPath, targetPath, newSlice.LogicalByteLength, newSlice.Sha256Hex));
            leafFileChanged = true;
            newLeafTargetPath = targetPath;
        }

        if (leafFileChanged && newLeafTargetPath is not null)
        {
            rolloutPathUpdates.Add(new PlannedThreadRolloutPathUpdate(threadId, newLeafTargetPath));
        }
    }

    private static string ResolveTargetDirectory(string codexHomePath, bool archived, DateTimeOffset? timestamp)
    {
        if (archived)
        {
            return Path.Combine(codexHomePath, CodexHomeLayout.ArchivedSessionsDirectoryName);
        }

        DateTimeOffset ts = timestamp ?? DateTimeOffset.UtcNow;
        return Path.Combine(
            codexHomePath, CodexHomeLayout.SessionsDirectoryName,
            ts.Year.ToString("D4"), ts.Month.ToString("D2"), ts.Day.ToString("D2"));
    }

    private static string? ResolveLocalProjectId(string? targetProjectPath, CodexCatalog freshLocalCatalog)
    {
        if (targetProjectPath is null || !CanonicalPath.TryCreate(targetProjectPath, out CanonicalPath? canonicalTarget, out _))
        {
            return null;
        }

        foreach (ProjectEntry project in freshLocalCatalog.Projects)
        {
            foreach (string rootPath in project.RootPaths)
            {
                if (CanonicalPath.TryCreate(rootPath, out CanonicalPath? canonicalRoot, out _) &&
                    canonicalRoot!.Equals(canonicalTarget!))
                {
                    return project.ProjectId;
                }
            }
        }

        return null;
    }

    private static string HashEntry(BackupReader reader, string entryPath)
    {
        using Stream stream = reader.OpenEntry(entryPath)
            ?? throw new InvalidDataException($"backup entry를 열 수 없습니다: {entryPath}");
        return CodexBackupManager.Backup.Container.StreamingHashCopy.HashOnly(stream).Sha256Hex;
    }

    private static string HashBackupEntryFull(IRolloutSliceReader backupSliceReader, RolloutSlice slice)
        => backupSliceReader.Hash(slice.File, null, null).Sha256Hex;

    private static RestoreOperationPlanResult Reject(string reason) => new(null, [reason]);

    private static string Redact(string threadId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(threadId)))[..12];
}
