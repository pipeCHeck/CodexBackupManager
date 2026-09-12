using System;
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

    /// <summary>
    /// 사용자가 직접 폴더를 선택해 재지정했다(Phase 06_01). <see cref="NotFound"/>였던 것을 사용자가
    /// 지정했거나, <see cref="AutoLinked"/> 자동 연결 결과를 사용자가 다른 경로로 덮어썼을 때 둘 다
    /// 이 상태가 된다 — Phase 7이 "이건 자동 추정이 아니라 사용자가 확정한 값"임을 구분할 수 있다.
    /// </summary>
    ManuallyLinked = 3,
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
    string? ResolvedLocalPath)
{
    /// <summary>
    /// <see cref="Status"/>가 사용자의 수동 재지정을 허용하는지. "기타 대화"(<see cref="ProjectPathMappingStatus.NotApplicable"/>)만
    /// 제외한다 — <see cref="ProjectPathMappingStatus.NotFound"/>는 반드시 가능해야 하고,
    /// <see cref="ProjectPathMappingStatus.AutoLinked"/>도 사용자가 원하면 덮어쓸 수 있다(요구사항).
    /// </summary>
    public bool CanManuallyOverride => Status != ProjectPathMappingStatus.NotApplicable;

    /// <summary>
    /// 사용자가 직접 고른 경로로 재지정한 복사본을 만든다. <see cref="ResolvedLocalPath"/>는 호출자가
    /// 이미 실제 디렉터리 존재 확인 + <c>CanonicalPath</c> 정규화를 마친 값이어야 한다(이 레코드
    /// 자체는 파일 시스템에 접근하지 않는다 — 순수 데이터).
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="CanManuallyOverride"/>가 <c>false</c>일 때.</exception>
    public ProjectPathMapping WithManualOverride(string resolvedLocalPath)
    {
        if (!CanManuallyOverride)
        {
            throw new InvalidOperationException("\"기타 대화\"(미분류) 그룹은 경로를 재지정할 수 없습니다.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedLocalPath);
        // LinkedLocalProjectId는 "AutoLinked가 어떤 로컬 프로젝트를 찾았는지"의 의미였다 — 사용자가
        // 직접 경로를 골랐다면 그 경로가 반드시 같은 로컬 프로젝트를 가리킨다는 보장이 없으므로 비운다.
        return this with { Status = ProjectPathMappingStatus.ManuallyLinked, ResolvedLocalPath = resolvedLocalPath, LinkedLocalProjectId = null };
    }
}
