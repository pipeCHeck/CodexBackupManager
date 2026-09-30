using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Domain.Codex.Import;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// (Phase 9_5-01) 같은 canonical 루트로 새 프로젝트를 만들려는 백업 프로젝트 여러 개를 하나로 합친 것.
/// </summary>
/// <param name="Root">canonical 루트(비교 기준).</param>
/// <param name="FolderPath">저장할 루트 표기(<see cref="CanonicalPath.Display"/> — <c>\\?\</c>·끝 구분자 없는 절대 경로).</param>
/// <param name="Name">만들 이름(앞뒤 공백 제거). 여러 프로젝트의 이름이 다르면 첫 번째 이름이다.</param>
/// <param name="ProjectKeys">이 새 프로젝트로 들어가는 백업 프로젝트 키(원래 순서).</param>
/// <param name="HasConflictingNames">합친 프로젝트들의 이름이 서로 달랐는지.</param>
public sealed record NewProjectGroup(
    CanonicalPath Root,
    string FolderPath,
    string Name,
    IReadOnlyList<string> ProjectKeys,
    bool HasConflictingNames);

/// <summary>
/// 새 프로젝트 목적지를 canonical 루트 기준으로 합친다. 화면 요약(<see cref="ImportSelection"/>)과 Restore Planner가 같은 규칙을 쓴다.
/// </summary>
public static class NewProjectGrouping
{
    /// <summary>
    /// <paramref name="targets"/>(백업 프로젝트 키, 목적지) 중 <see cref="ProjectTargetKind.CreateNew"/>만 루트별로 합친다. 순서는 첫 등장 순서다.
    /// 루트를 정규화할 수 없는 목적지는 건너뛴다(Resolver가 그런 목적지를 만들지 않는다).
    /// </summary>
    public static IReadOnlyList<NewProjectGroup> Group(IEnumerable<(string ProjectKey, ProjectTarget Target)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var order = new List<CanonicalPath>();
        var byRoot = new Dictionary<CanonicalPath, (string Folder, string Name, List<string> Keys, bool Conflict)>(CanonicalPath.Comparer);
        foreach ((string key, ProjectTarget target) in targets)
        {
            if (target is not { Kind: ProjectTargetKind.CreateNew, FolderPath: { } folder } ||
                !CanonicalPath.TryCreate(folder, out CanonicalPath? root, out _))
            {
                continue;
            }

            string name = (target.NewProjectName ?? string.Empty).Trim();
            if (byRoot.TryGetValue(root!, out var existing))
            {
                existing.Keys.Add(key);
                byRoot[root!] = (existing.Folder, existing.Name, existing.Keys,
                    existing.Conflict || !string.Equals(existing.Name, name, StringComparison.Ordinal));
                continue;
            }

            order.Add(root!);
            byRoot[root!] = (root!.Display, name, [key], false);
        }

        return order.Select(root =>
        {
            var group = byRoot[root];
            return new NewProjectGroup(root, group.Folder, group.Name, group.Keys, group.Conflict);
        }).ToList();
    }
}
