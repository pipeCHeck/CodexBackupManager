using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Codex.Threads;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Projects;
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
    /// <summary>
    /// 계획을 만든다. Codex에는 어떤 것도 쓰지 않는다(읽기만 한다).
    /// </summary>
    /// <param name="pinnedBackup">
    /// (Phase 07_01) Apply 시작 시 한 번만 연 backup source — 이 메서드는 이 reader만 쓰고
    /// <paramref name="plan"/>의 경로로 파일을 새로 열지 않는다. 호출자(<see cref="RestoreExecutor"/>)가
    /// 이미 identity를 확인한 바로 그 바이트를 계속 신뢰하기 위함이다(Preview↔Plan TOCTOU를 막은
    /// Phase 06_03과 같은 원칙을 Apply 내부에도 적용).
    /// </param>
    public static RestoreOperationPlanResult Build(
        ImportPlan plan,
        CodexCatalog freshLocalCatalog,
        string codexHomePath,
        PinnedBackupSource pinnedBackup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(freshLocalCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(pinnedBackup);

        if (plan.HasBlockingIssues || plan.HasUnresolvedDivergence)
        {
            return Reject("Blocked 또는 분기 충돌(Diverged)이 있는 Plan은 계획을 만들지 않습니다.");
        }

        if (!pinnedBackup.MatchesExpected(plan.Backup))
        {
            return Reject("backup 파일이 Apply 시작 이후 바뀌었습니다.");
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

        // Phase 9_1-08 — Plan에 freeze된 프로젝트 목적지가 지금(fresh 카탈로그) 판정과 같은지 확인한다. 다르면
        // (연결 대상이 사라졌거나, 기타 대화로 계획했던 폴더가 그 사이 등록됐거나) 사용자가 본 미리보기와 다른
        // 결과가 되므로 계획 자체를 거부한다. ResolvedTarget이 없는 이전 방식 Plan은 이 확인을 하지 않는다.
        if (FindChangedProjectTarget(plan, freshLocalCatalog) is not null)
        {
            return Reject(ProjectTargetChangedMessage);
        }

        // Phase 9_5-01 — 새 프로젝트 계획. 위의 fresh 재판정을 통과했으므로 계획한 CreateNew 루트는 지금도 미등록이고
        // 이 PC가 생성을 지원한다(그 사이 등록됐거나 스키마가 바뀌었으면 이미 거부됐다 — 중복 생성 금지).
        var createRejections = new List<string>();
        (IReadOnlyList<PlannedProjectCreate> projectCreates, Dictionary<string, PlannedProjectCreate> createByProjectKey) =
            PlanProjectCreates(plan, createRejections);
        if (createRejections.Count > 0)
        {
            return new RestoreOperationPlanResult(null, createRejections);
        }

        if (projectCreates.Count > 0 && !SchemaCompatibilityChecker.CheckProjectCreationFile(stateDbPath).IsSupported)
        {
            return Reject("현재 state DB 프로젝트 스키마가 확인한 형태와 달라 새 프로젝트를 만들 수 없습니다.");
        }

        // Phase 9_5a-03 — 새 프로젝트는 Desktop 사이드바(global-state 레거시 저장소)에도 기록한다. 지금 파일이 게이트를 통과해야 하고,
        // 그 바이트의 해시를 계획에 고정한다(Snapshot·트랜잭션 안·쓰기 직전에 다시 비교해 계획 뒤 변경을 거부한다).
        // Phase 9_3-06 — 연결 변경이 있으면 프로젝트를 만들지 않아도 같은 게이트와 해시 고정을 한다(계획 뒤 Desktop 배정 변경 감지).
        string? globalStateSha256 = null;
        if (projectCreates.Count > 0 || plan.Relinks.Count > 0)
        {
            if (!CanonicalPath.TryCreate(codexHomePath, out CanonicalPath? home, out _))
            {
                return Reject("Codex Home 경로를 해석할 수 없습니다.");
            }

            GlobalStateProjectGate.Result desktop = GlobalStateProjectGate.Check(
                Path.Combine(codexHomePath, CodexHomeLayout.GlobalStateFileName), home!);
            if (!desktop.IsSupported)
            {
                return Reject($"Codex 데스크톱 앱 상태 파일이 확인한 형태와 달라 새 프로젝트를 만들거나 대화를 옮길 수 없습니다(사유: {desktop.Failure}).");
            }

            globalStateSha256 = desktop.Sha256Hex;
        }

        // Phase 9_3-02 — 연결 변경. fresh 카탈로그로 후보 조건을 다시 본다(배정에 들어감, 위치가 계획과 다름, 보관됨이면 거부 — 쓰기 0).
        // 내용이 바뀌었는지(다르게 이어졌는지)는 Preflight가 옮길 대화의 로컬 revision으로 이미 확인했다.
        var relinkRejections = new List<string>();
        List<PlannedThreadProjectLink> projectLinks = PlanRelinks(plan, freshLocalCatalog, createByProjectKey, relinkRejections);
        if (relinkRejections.Count > 0)
        {
            return new RestoreOperationPlanResult(null, relinkRejections);
        }

        BackupReader reader = pinnedBackup.Reader;
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
        var threadMetadataUpdates = new List<PlannedThreadMetadataUpdate>();
        var copiedEntryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejections = new List<string>();

        foreach (ImportPlanConversation conversation in plan.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Phase 9_2-21 — 사용자 선택으로 뺀 대화(UserExcluded/NotSelectedDependencyNotNeeded)는 쓰기 대상이 아니다.
            // PlannedAction과 무관하게 건너뛴다(충돌 대화를 빼도 "예상치 못한 PlannedAction"으로 거부하지 않는다).
            if (conversation.IsExcludedFromApply)
            {
                continue;
            }

            switch (conversation.PlannedAction)
            {
                case ImportPlannedAction.Import:
                    PlanNewImport(
                        conversation, metadataByThreadId, actionByThreadId, reader, codexHomePath,
                        newRolloutFiles, threadInserts, copiedEntryPaths, freshLocalCatalog, createByProjectKey, rejections);
                    break;

                case ImportPlannedAction.Update:
                    PlanFastForward(
                        conversation, freshLocalCatalog, backupCatalog, backupSliceReader, reader,
                        metadataByThreadId, codexHomePath,
                        newRolloutFiles, rolloutAppends, rolloutPathUpdates, threadMetadataUpdates,
                        rejections, cancellationToken);
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

        var restorePlan = new RestoreOperationPlan(
            newRolloutFiles, rolloutAppends, threadInserts, rolloutPathUpdates, threadMetadataUpdates)
        {
            ProjectCreates = projectCreates,
            GlobalStateExpectedSha256 = globalStateSha256,
            ThreadProjectLinks = projectLinks,
        };
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
        Dictionary<string, PlannedProjectCreate> createByProjectKey,
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

        // 요구사항 9 — thread_source가 NULL이면 Codex UI 목록에서 사라진다(공식 Issue #23979,
        // docs/codex-storage-format.md §7-5). 사용자가 실제로 선택한 대화(IsSelected)는 반드시
        // Desktop/CLI 목록에 보여야 하므로 값이 없으면 추측해서 채우지 않고 New Import 자체를
        // 거부한다. dependency-only(조상, IsSelected=false) thread는 독립적으로 목록에 보일 필요가
        // 없으므로 이 게이트를 적용하지 않는다.
        if (conversation.IsSelected && string.IsNullOrWhiteSpace(metadata.ThreadSource))
        {
            rejections.Add($"{Redact(conversation.ThreadId)}: thread_source가 비어 있어 New Import를 할 수 없습니다(Codex 목록에서 보이지 않을 위험).");
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

        // Phase 9_5-01 — 새로 만들 프로젝트로 가는 대화: project_id = 새 ID, cwd = 새 루트(같은 트랜잭션에서 먼저 만든다).
        if (conversation.TargetProjectKey is { } projectKey && createByProjectKey.TryGetValue(projectKey, out PlannedProjectCreate? create))
        {
            threadInserts.Add(new PlannedThreadInsert(metadata, leafTargetPath, create.NewProjectId, create.RootPathDisplay));
            return;
        }

        string? resolvedProjectId = ResolveLocalProjectId(conversation.TargetProjectPath, freshLocalCatalog);

        // 요구사항 4(Phase 07_02) — project_id가 실제로 해석됐을 때만(=대상 PC에 실존하는 프로젝트
        // 폴더일 때만) cwd도 그 경로로 remap한다. 해석 불가(기타 대화)면 원본 cwd를 그대로 둔다 —
        // 더 나은 값이 없고, rollout JSONL은 어차피 절대 건드리지 않는다.
        string? resolvedTargetCwd = resolvedProjectId is not null ? conversation.TargetProjectPath : null;

        threadInserts.Add(new PlannedThreadInsert(metadata, leafTargetPath, resolvedProjectId, resolvedTargetCwd));
    }

    private static void PlanFastForward(
        ImportPlanConversation conversation,
        CodexCatalog freshLocalCatalog,
        BackupCatalogReader.Result backupCatalog,
        IRolloutSliceReader backupSliceReader,
        BackupReader reader,
        Dictionary<string, BackupConversationMetadata> metadataByThreadId,
        string codexHomePath,
        List<PlannedNewRolloutFile> newRolloutFiles,
        List<PlannedRolloutAppend> rolloutAppends,
        List<PlannedThreadRolloutPathUpdate> rolloutPathUpdates,
        List<PlannedThreadMetadataUpdate> threadMetadataUpdates,
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

        // 요구사항 8 — archived 상태가 바뀌는 update는 이번 Phase에서 지원하지 않는다(파일을
        // sessions\↔archived_sessions\로 옮기는 것까지 포함해야 하는데, 그 물리적 이동은 검증하지
        // 않았다). local의 현재 archived 상태와 backup metadata의 archived가 다르면 전체 거부한다.
        if (metadataByThreadId.TryGetValue(threadId, out BackupConversationMetadata? incomingMetadata) &&
            incomingMetadata.Archived != currentFile.IsArchived)
        {
            rejections.Add($"{Redact(threadId)}: archived 상태가 바뀌는 update는 지원하지 않습니다(Unsupported).");
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

            // 요구사항 7 — 새 segment는 backup entry 바이트를 그대로 복사한다(New Import와 동일한
            // 연산). 따라서 검증 기준도 물리(physical) 길이/해시여야 한다 — newSlice.LogicalByteLength/
            // Sha256Hex는 압축 해제된 "논리" 값이라 .jsonl.zst 새 segment에서는 실제로 복사되는
            // 압축 바이트와 다르다(예전 버그, 정상 zst 파일도 검증에 실패했다). RolloutSlice.File은
            // backup ZIP entry를 가리키므로 여기서 물리 길이/해시를 다시 재는 것이 유일하게 안전하다.
            long physicalLength = reader.GetEntryLength(entryPath)
                ?? throw new InvalidOperationException($"backup entry를 찾을 수 없습니다: {Redact(threadId)}");
            string physicalHash = HashEntry(reader, entryPath);

            newRolloutFiles.Add(new PlannedNewRolloutFile(threadId, entryPath, targetPath, physicalLength, physicalHash));
            leafFileChanged = true;
            newLeafTargetPath = targetPath;
        }

        if (leafFileChanged && newLeafTargetPath is not null)
        {
            rolloutPathUpdates.Add(new PlannedThreadRolloutPathUpdate(threadId, newLeafTargetPath));
        }

        PlannedThreadMetadataUpdate? metadataUpdate = ComputeMetadataUpdate(threadId, incomingMetadata, freshLocalCatalog);
        if (metadataUpdate is not null)
        {
            threadMetadataUpdates.Add(metadataUpdate);
        }
    }

    /// <summary>
    /// 요구사항 8 — "대화 자체가 진행되면서 자연스럽게 갱신되는" metadata만 반영한다. target PC
    /// 고유 값(cwd/project_id/사이드바 배치 등)은 절대 건드리지 않는다. 아무 필드도 바뀌지 않으면
    /// <c>null</c>을 돌려줘 불필요한 UPDATE를 만들지 않는다.
    /// </summary>
    private static PlannedThreadMetadataUpdate? ComputeMetadataUpdate(
        string threadId, BackupConversationMetadata? incoming, CodexCatalog freshLocalCatalog)
    {
        if (incoming is null)
        {
            return null;
        }

        ThreadRow? localRow = freshLocalCatalog.AllConversations
            .FirstOrDefault(e => string.Equals(e.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))?.Row;
        if (localRow is null)
        {
            return null;
        }

        long? updatedAtSeconds = incoming.UpdatedAtSeconds is { } incomingUpdatedAt &&
            (localRow.UpdatedAtSeconds is not { } localUpdatedAt || incomingUpdatedAt > localUpdatedAt)
            ? incoming.UpdatedAtSeconds
            : null;
        long? updatedAtMs = updatedAtSeconds is not null ? incoming.UpdatedAtMs : null;

        long? tokensUsed = incoming.TokensUsed is { } incomingTokens &&
            (localRow.TokensUsed is not { } localTokens || incomingTokens > localTokens)
            ? incoming.TokensUsed
            : null;

        bool? hasUserEvent = incoming.HasUserEvent == true && localRow.HasUserEvent != true ? true : null;

        string? nameIfMissing = string.IsNullOrWhiteSpace(localRow.Name) && !string.IsNullOrWhiteSpace(incoming.Name) ? incoming.Name : null;
        string? modelIfMissing = string.IsNullOrWhiteSpace(localRow.Model) && !string.IsNullOrWhiteSpace(incoming.Model) ? incoming.Model : null;
        string? cliVersionIfMissing = string.IsNullOrWhiteSpace(localRow.CliVersion) && !string.IsNullOrWhiteSpace(incoming.CliVersion) ? incoming.CliVersion : null;

        if (updatedAtSeconds is null && tokensUsed is null && hasUserEvent is null &&
            nameIfMissing is null && modelIfMissing is null && cliVersionIfMissing is null)
        {
            return null;
        }

        return new PlannedThreadMetadataUpdate(
            threadId, updatedAtSeconds, updatedAtMs, tokensUsed, hasUserEvent,
            nameIfMissing, modelIfMissing, cliVersionIfMissing);
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

    /// <summary>idempotency key 앞부분(형식 버전 포함). 전체 = 접두 + backup SHA-256 + ":" + canonical 루트 SHA-256(소문자 hex).</summary>
    internal const string IdempotencyKeyPrefix = "codex-backup-manager:import:v1:";

    /// <summary>공식 <c>validate_idempotency_key</c> 상한(바이트).</summary>
    internal const int MaxIdempotencyKeyBytes = 512;

    /// <summary>
    /// (Phase 9_5-01) 새 프로젝트 계획. 새로 가져올 대화가 들어가는 CreateNew 목적지만, 같은 canonical 루트는 하나로 합친다
    /// (이름이 다르면 Plan 순서의 첫 번째). 이름(앞뒤 공백 제거 후 비어 있지 않음), 절대 경로, 키 길이를 확인한다.
    /// 선택 없는 이전 방식 Plan은 프로젝트를 만들지 않는다(0.1.3과 같은 결과).
    /// </summary>
    internal static (IReadOnlyList<PlannedProjectCreate> Creates, Dictionary<string, PlannedProjectCreate> ByProjectKey) PlanProjectCreates(
        ImportPlan plan, List<string> rejections)
    {
        var byKey = new Dictionary<string, PlannedProjectCreate>(StringComparer.Ordinal);
        var creates = new List<PlannedProjectCreate>();
        if (plan.UserChoices is null)
        {
            return (creates, byKey);
        }

        IEnumerable<(string, ProjectTarget)> targets = plan.Projects
            .Where(p => p.ResolvedTarget is { Kind: ProjectTargetKind.CreateNew } && plan.UsesProjectTarget(p))
            .Select(p => (ImportUserChoices.ProjectKeyOf(p.ProjectId), p.ResolvedTarget!));

        foreach (NewProjectGroup group in NewProjectGrouping.Group(targets))
        {
            // Phase 9_5-11 — 백업 "기타 대화" 그룹으로는 프로젝트를 만들지 않는다(Selection이 이미 막지만 Planner도 거부한다).
            if (group.ProjectKeys.Contains(ImportUserChoices.UncategorizedProjectKey))
            {
                rejections.Add("백업의 기타 대화로는 새 프로젝트를 만들지 않습니다.");
                continue;
            }

            if (group.Name.Length == 0)
            {
                rejections.Add("새 프로젝트 이름이 비어 있습니다.");
                continue;
            }

            if (!ProjectTargetResolver.IsAbsolute(group.Root))
            {
                rejections.Add("새 프로젝트 루트가 절대 경로가 아닙니다.");
                continue;
            }

            string key = IdempotencyKeyPrefix + plan.Backup.BackupFileSha256.ToLowerInvariant() + ":" + Sha256Hex(group.Root.Value);
            if (System.Text.Encoding.UTF8.GetByteCount(key) > MaxIdempotencyKeyBytes)
            {
                rejections.Add("새 프로젝트 멱등성 키가 너무 깁니다.");
                continue;
            }

            var create = new PlannedProjectCreate(Guid.CreateVersion7().ToString("D"), group.Name, group.FolderPath, key);
            creates.Add(create);
            foreach (string projectKey in group.ProjectKeys)
            {
                byKey[projectKey] = create;
            }
        }

        return (creates, byKey);
    }

    /// <summary>(Phase 9_3-02) 옮길 대화가 지금(fresh)도 옮길 수 있는 상태인지 다시 보고, 목적지 DB ID·cwd를 정한다.</summary>
    internal static List<PlannedThreadProjectLink> PlanRelinks(
        ImportPlan plan, CodexCatalog freshLocalCatalog, IReadOnlyDictionary<string, PlannedProjectCreate> createByProjectKey, List<string> rejections)
    {
        var links = new List<PlannedThreadProjectLink>();
        if (plan.Relinks.Count == 0)
        {
            return links;
        }

        Dictionary<string, ConversationEntry> localById = freshLocalCatalog.AllConversations
            .GroupBy(e => e.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        DesktopThreadPlacement placement = freshLocalCatalog.ThreadPlacement;

        foreach (ImportPlanRelink relink in plan.Relinks)
        {
            string label = Redact(relink.ThreadId);
            if (!localById.TryGetValue(relink.ThreadId, out ConversationEntry? local))
            {
                rejections.Add($"{label}: 옮길 대화가 이 PC에 없습니다.");
                continue;
            }

            if (local.Archived)
            {
                rejections.Add($"{label}: 옮길 대화가 보관되었습니다.");
                continue;
            }

            if (!placement.IsAvailable)
            {
                rejections.Add($"{label}: Codex 데스크톱 앱 상태 파일을 확인하지 못해 옮기지 않습니다.");
                continue;
            }

            if (placement.AssignedThreadIds.Contains(relink.ThreadId) || placement.ProjectlessThreadIds.Contains(relink.ThreadId))
            {
                rejections.Add($"{label}: Codex 데스크톱 앱이 이 대화의 위치를 따로 기록하게 되어 옮기지 않습니다.");
                continue;
            }

            string? currentProjectId = string.IsNullOrWhiteSpace(local.Row.ProjectId) ? null : local.Row.ProjectId;
            if (!string.Equals(currentProjectId, relink.ExpectedProjectId, StringComparison.Ordinal) ||
                !string.Equals(local.Row.Cwd, relink.ExpectedCwd, StringComparison.Ordinal))
            {
                rejections.Add($"{label}: 미리보기 이후 이 대화의 위치가 바뀌었습니다.");
                continue;
            }

            ImportPlanProject? project = plan.Projects.FirstOrDefault(p => ImportUserChoices.ProjectKeyOf(p.ProjectId) == relink.TargetProjectKey);
            switch (project?.ResolvedTarget)
            {
                case { Kind: ProjectTargetKind.CreateNew } when createByProjectKey.TryGetValue(relink.TargetProjectKey, out PlannedProjectCreate? create):
                    links.Add(new PlannedThreadProjectLink(relink.ThreadId, relink.ExpectedProjectId, relink.ExpectedCwd, create.NewProjectId, create.RootPathDisplay));
                    break;
                case { Kind: ProjectTargetKind.LinkExisting, LinkDbProjectId: { Length: > 0 } dbId, FolderPath: { Length: > 0 } folder }
                    when freshLocalCatalog.ProjectDirectory.FindById(dbId)?.DbProjectId == dbId:
                    if (string.Equals(currentProjectId, dbId, StringComparison.Ordinal))
                    {
                        rejections.Add($"{label}: 이미 그 프로젝트에 있습니다.");
                        break;
                    }

                    links.Add(new PlannedThreadProjectLink(relink.ThreadId, relink.ExpectedProjectId, relink.ExpectedCwd, dbId, folder));
                    break;
                default:
                    rejections.Add($"{label}: 옮길 목적지 프로젝트를 확정할 수 없습니다.");
                    break;
            }
        }

        return links;
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Plan 목적지와 fresh 판정이 다를 때의 거부 사유(사용자 원문 없음).</summary>
    internal const string ProjectTargetChangedMessage = "미리보기 이후 이 PC의 프로젝트 구성이 바뀌었습니다.";

    /// <summary>
    /// Phase 9_1-08 — freeze된 목적지(<see cref="ImportPlanProject.ResolvedTarget"/>)를 fresh 카탈로그의
    /// <see cref="CodexCatalog.ProjectDirectory"/>로 같은 규칙(<see cref="ProjectTargetResolver.ResolveFolder"/>)으로 다시 판정해
    /// <see cref="ProjectTarget.SameOutcomeAs"/>(Kind, LinkDbProjectId)가 아닌 첫 프로젝트를 돌려준다. 없으면 <c>null</c>.
    /// </summary>
    internal static ImportPlanProject? FindChangedProjectTarget(ImportPlan plan, CodexCatalog freshLocalCatalog)
    {
        foreach (ImportPlanProject project in plan.Projects)
        {
            // Phase 9_2-21 — 새로 가져올 대화가 없는 프로젝트의 목적지는 이번 적용에 쓰이지 않으므로 다시 판정하지 않는다.
            if (!plan.UsesProjectTarget(project))
            {
                continue;
            }

            if (project.ResolvedTarget is not { FolderPath: { } folder } frozen ||
                frozen.Reason == ProjectTargetReason.NotApplicable)
            {
                continue; // 이전 방식 Plan, 판정할 폴더가 없던 목적지(원본 없음), 기타 대화 그룹
            }

            ProjectTarget fresh = ProjectTargetResolver.ResolveFolder(
                folder, project.PathStatus == ProjectPathMappingStatus.ManuallyLinked, freshLocalCatalog.ProjectDirectory);

            // Phase 9_5 — 미등록 폴더를 "만들지 않고 기타 대화로"(사용자 선택 또는 선택 없는 이전 방식 Plan) freeze했다면, fresh 판정이
            // 새 프로젝트 제안이어도 "여전히 미등록"이라는 같은 상태다. 반대로 CreateNew로 freeze했는데 그 사이 등록됐거나(LinkExisting)
            // 생성을 지원하지 않게 됐으면(Uncategorized) 다르다 → 거부(중복 생성 금지).
            if (frozen.Kind == ProjectTargetKind.Uncategorized && fresh.Kind == ProjectTargetKind.CreateNew)
            {
                fresh = ProjectTarget.Uncategorized(fresh.Reason, fresh.FolderPath);
            }

            if (!fresh.SameOutcomeAs(frozen))
            {
                return project;
            }
        }

        return null;
    }

    /// <summary>
    /// Phase 9_1-08(결함 C) — <c>threads.project_id</c>에 쓸 ID. fresh 카탈로그의 <see cref="CodexCatalog.ProjectDirectory"/>에서
    /// 대상 폴더와 루트가 정확히 같은 프로젝트가 <b>하나</b>이고 그 프로젝트에 DB ID가 있을 때만 그 ID다
    /// (<c>threads.project_id REFERENCES projects(id)</c>). 레거시 전용/Ambiguous/없음이면 <c>null</c>(기타 대화).
    /// 대화가 0개인 등록 프로젝트도 여기서 찾는다(결함 A — 이전에는 대화가 있는 카탈로그 그룹만 봤다).
    /// </summary>
    private static string? ResolveLocalProjectId(string? targetProjectPath, CodexCatalog freshLocalCatalog)
    {
        if (targetProjectPath is null || !CanonicalPath.TryCreate(targetProjectPath, out CanonicalPath? canonicalTarget, out _))
        {
            return null;
        }

        ProjectLookupResult lookup = freshLocalCatalog.ProjectDirectory.FindByRoot(canonicalTarget!);
        return lookup.Kind == ProjectLookupKind.Found ? lookup.Project!.DbProjectId : null;
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
