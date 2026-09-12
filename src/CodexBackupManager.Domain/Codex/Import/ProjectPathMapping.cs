using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Import;

/// <summary>backup 프로젝트의 원본 경로를 현재 PC에 연결할 수 있었는지.</summary>
public enum ProjectPathMappingStatus
{
    /// <summary>"기타 대화"(미분류) 그룹 — 경로 매핑 대상이 아니다.</summary>
    NotApplicable = 0,

    /// <summary>현재 로컬 카탈로그의 어떤 프로젝트와 canonical path가 일치해 자동으로 연결됐다.</summary>
    AutoLinked = 1,

    /// <summary>일치하는 로컬 프로젝트를 찾지 못했다 — 사용자가 폴더를 다시 지정해야 한다.</summary>
    NotFound = 2,
}

/// <summary>
/// backup 프로젝트 하나의 경로 재매핑 Preview. Phase 6은 판정/제안만 하고 어떤 Codex 파일도 쓰지
/// 않는다 — 실제 적용은 Phase 7의 역할이다.
/// </summary>
/// <param name="ProjectId">backup 쪽 원본 프로젝트 ID. 미분류면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름.</param>
/// <param name="OriginalRootPaths">backup manifest에 기록된 원본 루트 경로(참고용).</param>
/// <param name="Status">매핑 상태.</param>
/// <param name="LinkedLocalProjectId"><see cref="ProjectPathMappingStatus.AutoLinked"/>일 때 연결된 로컬 프로젝트 ID.</param>
/// <param name="ResolvedLocalPath">자동 연결됐거나 사용자가 재지정한 현재 PC 기준 경로. 없으면 <c>null</c>.</param>
public sealed record ProjectPathMapping(
    string? ProjectId,
    string DisplayName,
    IReadOnlyList<string> OriginalRootPaths,
    ProjectPathMappingStatus Status,
    string? LinkedLocalProjectId,
    string? ResolvedLocalPath);
