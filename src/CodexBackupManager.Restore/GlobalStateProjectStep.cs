using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>
/// (Phase 9_5a-03/04) global-state 레거시 프로젝트 기록의 공통 단계. <see cref="RestoreExecutor"/>와 <see cref="SidebarRepairService"/>가
/// 같은 확인·쓰기·검증을 쓴다(새 메커니즘을 따로 만들지 않는다). 메시지에는 경로·이름·ID를 넣지 않는다.
/// </summary>
internal static class GlobalStateProjectStep
{
    /// <summary>계획(또는 감지) 뒤 global-state가 바뀌었을 때.</summary>
    public const string ChangedAfterPlanMessage = "Codex 데스크톱 앱 상태 파일이 확인 이후 바뀌었습니다. 다시 시도해 주세요.";

    /// <summary>global-state 파일 경로.</summary>
    public static string PathFor(string codexHomePath) => Path.Combine(codexHomePath, CodexHomeLayout.GlobalStateFileName);

    /// <summary>Codex Home canonical(해석할 수 없으면 예외).</summary>
    public static CanonicalPath HomeOf(string codexHomePath)
        => CanonicalPath.TryCreate(codexHomePath, out CanonicalPath? home, out _)
            ? home!
            : throw new InvalidOperationException("Codex Home 경로를 해석할 수 없습니다.");

    /// <summary>Snapshot에 담긴 global-state가 기대한 바이트(해시)인지.</summary>
    public static bool SnapshotMatches(SnapshotManifest manifest, string expectedSha256)
    {
        SnapshotFileEntry? entry = manifest.Files.FirstOrDefault(f => f.RelativeLabel == GlobalStateWriter.SnapshotLabel);
        return entry is { ExistedBefore: true } &&
               string.Equals(entry.Sha256Hex, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>지금 파일이 게이트를 통과하고 해시가 기대값과 같은지. 문제가 없으면 <c>null</c>.</summary>
    public static string? CheckUnchanged(string codexHomePath, string? expectedSha256)
    {
        if (expectedSha256 is null)
        {
            return "Codex 데스크톱 앱 상태 파일을 확인하지 않은 계획이라 새 프로젝트를 만들지 않습니다.";
        }

        GlobalStateProjectGate.Result gate = GlobalStateProjectGate.Check(PathFor(codexHomePath), HomeOf(codexHomePath));
        if (!gate.IsSupported)
        {
            return $"Codex 데스크톱 앱 상태 파일이 확인한 형태와 달라 새 프로젝트를 만들지 않습니다(사유: {gate.Failure}).";
        }

        return string.Equals(gate.Sha256Hex, expectedSha256, StringComparison.OrdinalIgnoreCase) ? null : ChangedAfterPlanMessage;
    }

    /// <summary>쓴다(<see cref="GlobalStateWriter.Apply"/>). 계획 뒤 바뀌었으면 예외.</summary>
    public static GlobalStateWriteResult Write(
        string codexHomePath, string? expectedSha256, IReadOnlyList<GlobalStateProjectAddition> additions, IRestoreFaultInjectionHook faultInjection)
    {
        if (expectedSha256 is null)
        {
            throw new InvalidOperationException(CheckUnchanged(codexHomePath, null));
        }

        return GlobalStateWriter.Apply(PathFor(codexHomePath), HomeOf(codexHomePath), expectedSha256, additions, faultInjection);
    }

    /// <summary>파일 수준 사후 검증. 문제가 없으면 <c>null</c>.</summary>
    public static string? VerifyFile(string codexHomePath, GlobalStateWriteResult write)
    {
        byte[] current;
        try
        {
            current = File.ReadAllBytes(PathFor(codexHomePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Codex 데스크톱 앱 상태 파일을 다시 읽을 수 없습니다({ex.GetType().Name}).";
        }

        if (!current.AsSpan().SequenceEqual(write.NewBytes))
        {
            return "Codex 데스크톱 앱 상태 파일이 쓴 내용과 다릅니다.";
        }

        return GlobalStateWriter.Verify(write.OriginalBytes, current, HomeOf(codexHomePath), write.Added);
    }

    /// <summary>
    /// fresh 카탈로그의 프로젝트 목록에서 새 레거시 ID가 DB 프로젝트와 같은 프로젝트로 합쳐져 보이는지. 문제가 없으면 <c>null</c>.
    /// </summary>
    public static string? VerifyMerged(ProjectDirectory directory, IReadOnlyList<GlobalStateAddedProject> added)
    {
        foreach (GlobalStateAddedProject project in added)
        {
            KnownProject? byDb = directory.FindById(project.DbProjectId);
            KnownProject? byLegacy = directory.FindById(project.LegacyProjectId);
            if (byDb is null || byLegacy is null || !ReferenceEquals(byDb, byLegacy) ||
                !string.Equals(byDb.DbProjectId, project.DbProjectId, StringComparison.Ordinal) ||
                !byDb.LegacyProjectIds.Contains(project.LegacyProjectId, StringComparer.Ordinal))
            {
                return "새 사이드바 항목이 프로젝트 목록에서 DB 프로젝트와 합쳐지지 않습니다.";
            }
        }

        return null;
    }
}
