using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Backup.Validation;
using CodexBackupManager.Codex.Revisions;
using CodexBackupManager.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// Phase 7이 실제 write를 시작하기 직전에 <see cref="ImportPlan"/>이 여전히 유효한지 확인하는
/// read-only validator(Phase 06_02). Codex에는 어떤 것도 쓰지 않는다 — 이 클래스가 하는 일은
/// "지금 다시 읽은 상태가 Plan에 frozen된 기대값과 정확히 같은가"뿐이고, 무엇을 어떻게 적용할지는
/// 전혀 모른다(그건 Phase 7의 영역).
/// </summary>
/// <remarks>
/// <para>
/// <b>relation을 다시 판정하지 않는다.</b> <see cref="ConversationRevisionComparer"/>를 다시 부르지
/// 않고, 지금 다시 만든 <see cref="ConversationRevision"/>이 Plan에 frozen된
/// <see cref="ImportConversationPrecondition.ExpectedLocalRevision"/>/<see cref="ImportConversationPrecondition.ExpectedIncomingRevision"/>과
/// 완전히 같은지(RolloutId/Boundary/길이/해시 시퀀스)만 비교한다 — "판정"이 아니라 "일치 확인"이다.
/// </para>
/// <para>
/// 실패 우선순위: backup identity → Blocked(Unverifiable) → UnresolvedDivergence(Diverged) →
/// LocalStateChanged → TargetPathUnavailable → Ready. backup 자체가 Preview 때와 다르면 그 아래
/// 어떤 것도 신뢰할 수 없으므로 항상 가장 먼저 확인한다.
/// </para>
/// </remarks>
public static class ImportPlanPreflightValidator
{
    /// <summary>preflight 결과.</summary>
    public sealed record Result(ImportPlanPreflightStatus Status, IReadOnlyList<string> Issues)
    {
        /// <summary><see cref="Status"/>가 <see cref="ImportPlanPreflightStatus.Ready"/>인지.</summary>
        public bool IsReady => Status == ImportPlanPreflightStatus.Ready;

        internal static Result Of(ImportPlanPreflightStatus status, string issue) => new(status, [issue]);

        internal static readonly Result Ready = new(ImportPlanPreflightStatus.Ready, []);
    }

    /// <summary>
    /// <paramref name="plan"/>이 지금 바로 적용해도 되는 상태인지 확인한다. Codex에는 쓰지 않는다 —
    /// backup 파일과 <paramref name="currentLocalCatalog"/>(호출자가 이미 만들어 둔, "지금" 시점의
    /// 로컬 카탈로그)만 다시 읽는다.
    /// </summary>
    /// <param name="plan">확인할 Plan(Preview 시점에 frozen됨).</param>
    /// <param name="currentLocalCatalog">
    /// 지금(Apply 직전) 시점의 로컬 카탈로그. 호출자가 <c>CodexCatalogBuilder.Build</c>로 새로
    /// 만들어서 넘긴다 — 이 메서드는 그 카탈로그를 다시 만들지 않는다.
    /// </param>
    public static Result Validate(ImportPlan plan, CodexCatalog currentLocalCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentLocalCatalog);

        Result backupCheck = CheckBackupIdentity(plan.Backup, cancellationToken);
        if (!backupCheck.IsReady)
        {
            return backupCheck;
        }

        if (plan.HasBlockingIssues)
        {
            return Result.Of(ImportPlanPreflightStatus.Blocked, "확인할 수 없는(Unverifiable) 대화가 있어 Apply할 수 없습니다.");
        }

        if (plan.HasUnresolvedDivergence)
        {
            return Result.Of(ImportPlanPreflightStatus.UnresolvedDivergence, "분기 충돌(Diverged) 대화가 있어 사용자 결정 없이는 Apply할 수 없습니다.");
        }

        // 로컬/incoming precondition 확인 — backup을 다시 열어야 하는 건 IncomingRevision 재검증
        // (요구사항 4의 방어적 이중 확인, §8.1 참고)뿐이다. backup identity가 이미 일치했으므로
        // 이론상 항상 통과해야 하지만, "판정 로직 자체가 재현 가능한가"까지 확인하는 값이다.
        using BackupReader reader = BackupReader.Open(plan.Backup.BackupFilePath);
        BackupCatalogReader.Result backupCatalog = BackupCatalogReader.Build(reader, cancellationToken);
        var incomingSliceReader = new BackupRolloutSliceReader(reader);
        IRolloutSliceReader localSliceReader = LocalFileRolloutSliceReader.Instance;

        var currentLocalByThreadId = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ConversationEntry entry in currentLocalCatalog.AllConversations)
        {
            currentLocalByThreadId[entry.ThreadId] = true;
        }

        foreach (ImportPlanConversation conversation in plan.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result? localStateResult = CheckConversationPrecondition(
                conversation, currentLocalCatalog, currentLocalByThreadId, localSliceReader, backupCatalog, incomingSliceReader, cancellationToken);
            if (localStateResult is not null)
            {
                return localStateResult;
            }
        }

        foreach (ImportPlanProject project in plan.Projects)
        {
            Result? pathResult = CheckTargetPath(project);
            if (pathResult is not null)
            {
                return pathResult;
            }
        }

        return Result.Ready;
    }

    private static Result CheckBackupIdentity(ImportBackupIdentity identity, CancellationToken cancellationToken)
    {
        if (!File.Exists(identity.BackupFilePath))
        {
            return Result.Of(ImportPlanPreflightStatus.BackupChanged, "백업 파일이 미리보기 이후 변경되었습니다. 다시 불러와 주세요.");
        }

        BackupValidationResult validation = BackupValidator.Validate(identity.BackupFilePath, cancellationToken);
        if (!validation.Success)
        {
            return Result.Of(ImportPlanPreflightStatus.BackupChanged, "백업 파일이 미리보기 이후 변경되었습니다. 다시 불러와 주세요.");
        }

        using FileStream stream = new(identity.BackupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        StreamingHashCopy.Result hashed = StreamingHashCopy.HashOnly(stream, cancellationToken);

        bool matches = hashed.ByteLength == identity.BackupFileLength &&
            string.Equals(hashed.Sha256Hex, identity.BackupFileSha256, StringComparison.Ordinal);

        return matches
            ? Result.Ready
            : Result.Of(ImportPlanPreflightStatus.BackupChanged, "백업 파일이 미리보기 이후 변경되었습니다. 다시 불러와 주세요.");
    }

    private static Result? CheckConversationPrecondition(
        ImportPlanConversation conversation,
        CodexCatalog currentLocalCatalog,
        Dictionary<string, bool> currentLocalByThreadId,
        IRolloutSliceReader localSliceReader,
        BackupCatalogReader.Result backupCatalog,
        IRolloutSliceReader incomingSliceReader,
        CancellationToken cancellationToken)
    {
        ImportConversationPrecondition precondition = conversation.Precondition;

        if (precondition.ExpectedPresence == ExpectedLocalPresence.MustNotExist)
        {
            bool existsNow = currentLocalCatalog.Chains.ContainsKey(conversation.ThreadId) ||
                currentLocalByThreadId.ContainsKey(conversation.ThreadId);
            if (existsNow)
            {
                return Result.Of(
                    ImportPlanPreflightStatus.LocalStateChanged,
                    "미리보기 이후 로컬 Codex에 같은 대화가 새로 생겨 더 이상 신규 대화로 가져올 수 없습니다.");
            }

            return null;
        }

        // MustExist — Preview 당시 로컬 revision을 만들 수 있었을 때만(Unverifiable이 아니었을 때만)
        // 다시 계산해서 정확히 같은지 비교한다. relation을 다시 판정하지 않는다 — fingerprint가
        // 완전히 같은지만 본다.
        if (precondition.ExpectedLocalRevision is not null)
        {
            ConversationRevisionBuildResult currentLocalResult = ConversationRevisionBuilder.Build(
                conversation.ThreadId, currentLocalCatalog.Chains, localSliceReader, cancellationToken);

            if (currentLocalResult.Revision is null ||
                !RevisionFingerprintEquals(currentLocalResult.Revision, precondition.ExpectedLocalRevision))
            {
                return Result.Of(
                    ImportPlanPreflightStatus.LocalStateChanged,
                    "미리보기 이후 로컬 대화 내용이 바뀌어 더 이상 미리보기 때와 같지 않습니다. 다시 불러와 주세요.");
            }
        }

        // incoming revision 방어적 재확인(요구사항 4/8) — backup identity가 이미 일치했으므로
        // 이론상 항상 통과해야 하지만, lineage 재구성 로직 자체가 그때와 똑같이 재현되는지까지
        // 확인한다.
        if (precondition.ExpectedIncomingRevision is not null)
        {
            ConversationRevisionBuildResult currentIncomingResult = ConversationRevisionBuilder.Build(
                conversation.ThreadId, backupCatalog.Chains, incomingSliceReader, cancellationToken);

            if (currentIncomingResult.Revision is null ||
                !RevisionFingerprintEquals(currentIncomingResult.Revision, precondition.ExpectedIncomingRevision))
            {
                return Result.Of(
                    ImportPlanPreflightStatus.BackupChanged,
                    "백업 파일이 미리보기 이후 변경되었습니다. 다시 불러와 주세요.");
            }
        }

        return null;
    }

    private static Result? CheckTargetPath(ImportPlanProject project)
    {
        if (project.TargetProjectPath is null)
        {
            // 미분류("기타 대화")이거나 아직 경로를 해결하지 못한 프로젝트 — 경로 확인 대상이 아니다.
            return null;
        }

        if (!Directory.Exists(project.TargetProjectPath))
        {
            return Result.Of(
                ImportPlanPreflightStatus.TargetPathUnavailable,
                $"프로젝트 '{project.DisplayName}'의 대상 폴더를 더 이상 찾을 수 없습니다.");
        }

        if (!CanonicalPath.TryCreate(project.TargetProjectPath, out _, out string? error))
        {
            return Result.Of(
                ImportPlanPreflightStatus.TargetPathUnavailable,
                $"프로젝트 '{project.DisplayName}'의 대상 폴더 경로가 더 이상 유효하지 않습니다: {error}");
        }

        return null;
    }

    /// <summary>
    /// 두 <see cref="ConversationRevision"/>의 내용이 완전히 같은지 비교한다. <see cref="RolloutSlice.File"/>
    /// (로컬 파일 경로/backup ZIP entry 경로)은 비교하지 않는다 — 파일이 물리적으로 어디 있는지가
    /// 아니라 논리적 내용(RolloutId/Boundary/길이/해시)만 같으면 된다.
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
}

/// <summary><see cref="ImportPlanPreflightValidator.Validate"/>의 판정 결과.</summary>
public enum ImportPlanPreflightStatus
{
    /// <summary>지금 바로 Apply를 시작해도 안전하다.</summary>
    Ready = 0,

    /// <summary>backup 파일이 Preview 이후 바뀌었다(경로가 같아도 내용이 다르거나, 더 이상 유효하지 않다).</summary>
    BackupChanged = 1,

    /// <summary>로컬 Codex 상태가 Preview 이후 바뀌었다(같은 ThreadId가 새로 생겼거나, 내용이 달라졌다).</summary>
    LocalStateChanged = 2,

    /// <summary>대상 프로젝트 경로를 더 이상 찾을 수 없다.</summary>
    TargetPathUnavailable = 3,

    /// <summary>Unverifiable 대화가 있어 항상 적용 금지.</summary>
    Blocked = 4,

    /// <summary>Diverged 대화가 있어 사용자 결정 없이는 적용 금지.</summary>
    UnresolvedDivergence = 5,
}
