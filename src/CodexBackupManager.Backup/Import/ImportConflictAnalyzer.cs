using CodexBackupManager.Domain.Codex.Import;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <see cref="RevisionRelation"/>에서 Phase 7의 기본 계획(<see cref="ImportPlannedAction"/>)으로
/// 매핑한다. 요구사항 6/15/16/14의 기본 정책을 그대로 코드로 옮긴 것 — 여기서 새 정책을 만들지 않는다.
/// </summary>
public static class ImportConflictAnalyzer
{
    /// <summary>관계에서 기본 계획을 결정한다.</summary>
    public static ImportPlannedAction Decide(RevisionRelation relation) => relation switch
    {
        RevisionRelation.New => ImportPlannedAction.Import,
        RevisionRelation.Identical => ImportPlannedAction.NoOp,
        RevisionRelation.IncomingAhead => ImportPlannedAction.Update,
        RevisionRelation.LocalAhead => ImportPlannedAction.Skip,
        RevisionRelation.Diverged => ImportPlannedAction.RequiresDecision,
        RevisionRelation.Unverifiable => ImportPlannedAction.Blocked,
        _ => ImportPlannedAction.Blocked,
    };
}
