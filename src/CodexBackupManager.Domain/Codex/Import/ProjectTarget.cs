namespace CodexBackupManager.Domain.Codex.Import;

/// <summary>가져온 대화가 대상 PC에서 들어갈 곳의 종류(Phase 9_1, docs/import-ux-redesign-phase9.md §5.2).</summary>
public enum ProjectTargetKind
{
    /// <summary>"기타 대화"로 들어간다(<c>threads.project_id</c> = NULL, cwd는 원본 그대로).</summary>
    Uncategorized = 0,

    /// <summary>대상 PC에 이미 등록된 DB 프로젝트(<see cref="ProjectTarget.LinkDbProjectId"/>)에 연결한다.</summary>
    LinkExisting = 1,

    /// <summary>
    /// (Phase 9_5) <see cref="ProjectTarget.FolderPath"/>를 루트로 하는 Codex 프로젝트를 새로 만들어 연결한다. 사유는
    /// <c>*Unregistered</c> 또는 <see cref="ProjectTargetReason.LegacyOnlyProject"/>다. 이 PC가 프로젝트 생성을 지원할 때만
    /// (<see cref="Projects.ProjectDirectory.ProjectCreation"/> = Supported) 만들어진다.
    /// </summary>
    CreateNew = 2,
}

/// <summary><see cref="ProjectTarget"/>을 그렇게 판정한 이유(화면 문구의 근거).</summary>
public enum ProjectTargetReason
{
    /// <summary>원본 경로 폴더가 이 PC에 있고 등록 프로젝트다 → 자동 연결.</summary>
    OriginalRootRegistered = 0,

    /// <summary>사용자가 고른 폴더가 등록 프로젝트다.</summary>
    UserSelectedRegistered = 1,

    /// <summary>원본 경로 폴더는 이 PC에 있지만 Codex에 등록되지 않았다.</summary>
    OriginalRootExistsUnregistered = 2,

    /// <summary>사용자가 고른 폴더가 Codex에 등록되지 않았다.</summary>
    UserSelectedUnregistered = 3,

    /// <summary>원본 경로 폴더가 이 PC에 없고 사용자가 지정하지도 않았다.</summary>
    OriginalRootMissing = 4,

    /// <summary>같은 루트가 여러 프로젝트에 등록돼 있다 — 자동으로 연결하지 않는다.</summary>
    AmbiguousRoot = 5,

    /// <summary>레거시(Desktop 이전 형식) 프로젝트로만 등록돼 있고 DB 프로젝트가 없다 — 연결할 ID가 없다.</summary>
    LegacyOnlyProject = 6,

    /// <summary>
    /// (Phase 9_5) 새 프로젝트를 만들어야 하는 폴더지만 이 PC state DB 스키마가 확인한 형태와 달라 만들지 않는다(기타 대화).
    /// </summary>
    CreationUnsupported = 7,

    /// <summary>백업의 "기타 대화" 그룹(또는 원본 루트가 없는 그룹) — 목적지 판정 대상이 아니다.</summary>
    NotApplicable = 8,
}

/// <summary>
/// 백업 프로젝트 하나의 대상 PC 목적지(Phase 9_1). 판정은 항상 "루트 경로 → 대상 PC의 ProjectDirectory"로만 한다
/// (설계 §4.4 — 프로젝트 ID는 PC마다 다르다).
/// </summary>
/// <param name="Kind">목적지 종류.</param>
/// <param name="Reason">그렇게 판정한 이유.</param>
/// <param name="FolderPath">
/// 목적지 판정에 쓴 폴더(원본 루트 또는 사용자가 고른 폴더). 판정할 폴더가 없으면 <c>null</c>.
/// <see cref="ProjectTargetKind.LinkExisting"/>이면 cwd remap에 쓰는 대상 루트다.
/// </param>
/// <param name="LinkDbProjectId"><see cref="ProjectTargetKind.LinkExisting"/>일 때만: 실존 <c>projects.id</c>.</param>
/// <param name="NewProjectName">
/// <see cref="ProjectTargetKind.CreateNew"/>일 때만: 만들 프로젝트 이름(기본값은 폴더 이름). 그 밖에는 <c>null</c>.
/// <see cref="ProjectTargetKind.CreateNew"/>이면 <see cref="FolderPath"/>는 <c>\\?\</c>와 끝 구분자가 없는 절대 경로(표시 표기)다.
/// </param>
public sealed record ProjectTarget(
    ProjectTargetKind Kind,
    ProjectTargetReason Reason,
    string? FolderPath,
    string? LinkDbProjectId,
    string? NewProjectName)
{
    /// <summary>"기타 대화"로 들어가는 목적지를 만든다.</summary>
    public static ProjectTarget Uncategorized(ProjectTargetReason reason, string? folderPath)
        => new(ProjectTargetKind.Uncategorized, reason, folderPath, null, null);

    /// <summary>등록 DB 프로젝트에 연결하는 목적지를 만든다.</summary>
    public static ProjectTarget Link(ProjectTargetReason reason, string folderPath, string dbProjectId)
        => new(ProjectTargetKind.LinkExisting, reason, folderPath, dbProjectId, null);

    /// <summary>(Phase 9_5) 폴더를 루트로 하는 새 프로젝트를 만드는 목적지를 만든다.</summary>
    public static ProjectTarget Create(ProjectTargetReason reason, string folderPath, string newProjectName)
        => new(ProjectTargetKind.CreateNew, reason, folderPath, null, newProjectName);

    /// <summary>
    /// 두 목적지가 실제 적용 결과로 같은지(<see cref="Kind"/>와 <see cref="LinkDbProjectId"/>만 비교). Plan에 freeze된
    /// 목적지와 Apply 시점의 fresh 판정을 비교할 때 쓴다.
    /// </summary>
    public bool SameOutcomeAs(ProjectTarget other)
        => other is not null &&
           Kind == other.Kind &&
           string.Equals(LinkDbProjectId, other.LinkDbProjectId, System.StringComparison.Ordinal);
}
