using System;
using System.Collections.Generic;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Domain.Codex.Projects;

/// <summary><see cref="ProjectDirectory.FindByRoot"/> 결과 종류.</summary>
public enum ProjectLookupKind
{
    /// <summary>그 루트를 가진 프로젝트가 없다.</summary>
    None = 0,

    /// <summary>정확히 한 프로젝트가 그 루트를 가진다.</summary>
    Found = 1,

    /// <summary>
    /// 서로 다른 프로젝트 둘 이상이 같은 루트를 가진다. 자동으로 연결하지 않는다(설계 §4.2).
    /// </summary>
    Ambiguous = 2,
}

/// <summary>루트 기준 조회 결과.</summary>
/// <param name="Kind">결과 종류.</param>
/// <param name="Project"><see cref="ProjectLookupKind.Found"/>일 때만 채워진다.</param>
/// <param name="Candidates">
/// 그 루트를 가진 프로젝트 전부(<see cref="ProjectLookupKind.Found"/>면 1개, <see cref="ProjectLookupKind.Ambiguous"/>면 2개 이상,
/// <see cref="ProjectLookupKind.None"/>이면 빈 목록).
/// </param>
public sealed record ProjectLookupResult(
    ProjectLookupKind Kind,
    KnownProject? Project,
    IReadOnlyList<KnownProject> Candidates)
{
    /// <summary>찾지 못함.</summary>
    public static ProjectLookupResult None { get; } = new(ProjectLookupKind.None, null, []);
}

/// <summary>
/// 이 PC에 등록된 프로젝트(<see cref="KnownProject"/>) 목록과 조회 인덱스(Phase 9_1, 설계 §4.2).
/// </summary>
/// <remarks>
/// <para>
/// 인덱스: canonical 루트 → 프로젝트(들), DB ID → 프로젝트, 레거시 ID → 프로젝트.
/// 한 ID가 두 프로젝트에 걸리는 목록은 만들 수 없다(생성자에서 거부) — <see cref="FindById"/>의 답은 항상 하나다.
/// 같은 canonical 루트가 여러 프로젝트에 걸리는 것은 허용하고 <see cref="FindByRoot"/>가
/// <see cref="ProjectLookupKind.Ambiguous"/>로 알려준다.
/// </para>
/// <para>이 타입은 파일을 읽지 않는다. 만드는 쪽은 <c>CodexBackupManager.Codex.Projects.ProjectDirectoryBuilder</c>다.</para>
/// </remarks>
public sealed class ProjectDirectory
{
    private readonly Dictionary<string, KnownProject> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<CanonicalPath, List<KnownProject>> _byRoot = new(CanonicalPath.Comparer);
    private readonly Dictionary<string, KnownProject> _byKey = new(StringComparer.Ordinal);

    /// <summary>생성자. 인덱스를 만든다.</summary>
    /// <exception cref="ArgumentException">같은 Key 또는 같은 DB/레거시 ID가 두 프로젝트에 있으면.</exception>
    public ProjectDirectory(IReadOnlyList<KnownProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        Projects = projects;

        foreach (KnownProject project in projects)
        {
            if (!_byKey.TryAdd(project.Key, project))
            {
                throw new ArgumentException("같은 Key를 가진 프로젝트가 둘 이상입니다.", nameof(projects));
            }

            if (project.DbProjectId is { } dbId)
            {
                AddId(dbId, project);
            }

            foreach (string legacyId in project.LegacyProjectIds)
            {
                AddId(legacyId, project);
            }

            foreach (KnownProjectRoot root in project.Roots)
            {
                if (!_byRoot.TryGetValue(root.Canonical, out List<KnownProject>? list))
                {
                    list = [];
                    _byRoot[root.Canonical] = list;
                }

                if (!list.Contains(project))
                {
                    list.Add(project);
                }
            }
        }
    }

    /// <summary>프로젝트가 하나도 없는 디렉터리.</summary>
    public static ProjectDirectory Empty { get; } = new([]);

    /// <summary>전체 프로젝트(대화가 0개인 프로젝트 포함).</summary>
    public IReadOnlyList<KnownProject> Projects { get; }

    /// <summary>canonical 루트로 찾는다. 루트가 정확히 같아야 한다(하위 폴더는 일치로 보지 않는다).</summary>
    public ProjectLookupResult FindByRoot(CanonicalPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!_byRoot.TryGetValue(root, out List<KnownProject>? list) || list.Count == 0)
        {
            return ProjectLookupResult.None;
        }

        return list.Count == 1
            ? new ProjectLookupResult(ProjectLookupKind.Found, list[0], [list[0]])
            : new ProjectLookupResult(ProjectLookupKind.Ambiguous, null, list.ToArray());
    }

    /// <summary>DB 프로젝트 ID 또는 레거시 ID로 찾는다. 모르면 <c>null</c>.</summary>
    public KnownProject? FindById(string? anyId)
        => anyId is not null && _byId.TryGetValue(anyId, out KnownProject? project) ? project : null;

    /// <summary><see cref="KnownProject.Key"/>로 찾는다. 모르면 <c>null</c>.</summary>
    public KnownProject? FindByKey(string? key)
        => key is not null && _byKey.TryGetValue(key, out KnownProject? project) ? project : null;

    private void AddId(string id, KnownProject project)
    {
        if (_byId.TryGetValue(id, out KnownProject? existing))
        {
            if (!ReferenceEquals(existing, project))
            {
                throw new ArgumentException("같은 프로젝트 ID가 서로 다른 두 프로젝트에 있습니다.", nameof(Projects));
            }

            return;
        }

        _byId[id] = project;
    }
}
