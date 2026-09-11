using System;
using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Catalog;

/// <summary>
/// Phase 2의 최종 산출물: "Codex Project → User Conversation 목록".
/// </summary>
/// <param name="Projects">
/// 프로젝트 그룹 목록. "기타 대화"(<see cref="ProjectEntry.IsUncategorized"/>)도 그룹 하나로 포함된다.
/// </param>
/// <param name="AllConversations">
/// <b>모든</b> thread(<c>subagent</c>/<c>guardian_review</c> 포함)를 담은 전체 목록.
/// 기본 UI는 <see cref="Projects"/>만 쓰지만, 이 목록은 버리지 않고 내부 관계 확인/향후 Export에 쓴다.
/// </param>
/// <param name="Warnings">카탈로그를 만드는 동안 발견한 이상 징후(사용자 원문 없이).</param>
/// <param name="BuiltAtUtc">카탈로그를 만든 시각.</param>
/// <param name="Stats">성능/규모 측정값.</param>
public sealed record CodexCatalog(
    IReadOnlyList<ProjectEntry> Projects,
    IReadOnlyList<ConversationEntry> AllConversations,
    IReadOnlyList<string> Warnings,
    DateTimeOffset BuiltAtUtc,
    CodexCatalogStats Stats)
{
    /// <summary><c>thread_source == "user"</c>인 전체 대화 수. 프로젝트 그룹 합계와 같다.</summary>
    public int UserConversationCount { get; } = CountUser(AllConversations);

    private static int CountUser(IReadOnlyList<ConversationEntry> all)
    {
        int count = 0;
        foreach (ConversationEntry entry in all)
        {
            if (entry.IsUserConversation)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>카탈로그 구축 성능/규모 측정값. 완료 보고에 그대로 쓴다.</summary>
/// <param name="RolloutFileCount">발견한 rollout 파일 수(세그먼트 포함).</param>
/// <param name="ThreadRowCount">state DB에서 읽은 전체 thread 행 수.</param>
/// <param name="ProjectCount"><see cref="CodexCatalog.Projects"/>의 개수("기타 대화" 포함).</param>
/// <param name="JsonlScanDuration">rollout 파일들의 <c>session_meta</c>를 스캔하는 데 걸린 시간.</param>
/// <param name="TotalBuildDuration">카탈로그 전체를 만드는 데 걸린 시간.</param>
public sealed record CodexCatalogStats(
    int RolloutFileCount,
    int ThreadRowCount,
    int ProjectCount,
    TimeSpan JsonlScanDuration,
    TimeSpan TotalBuildDuration);
