using System.Collections.Generic;

namespace CodexBackupManager.Domain.Codex.Catalog;

/// <summary>카탈로그의 프로젝트 그룹 하나(왼쪽 트리의 최상위 노드).</summary>
/// <param name="ProjectId">프로젝트 ID. "기타 대화" 그룹이면 <c>null</c>.</param>
/// <param name="DisplayName">표시 이름. "기타 대화" 그룹이면 고정 문구.</param>
/// <param name="RootPaths">이 프로젝트에 연결된 알려진 루트 경로(원본 표기). 없으면 빈 목록.</param>
/// <param name="Conversations">
/// 이 프로젝트에 속한 사용자 대화(<c>thread_source == "user"</c>)만. 시간순으로 정렬되어 있다.
/// </param>
public sealed record ProjectEntry(
    string? ProjectId,
    string DisplayName,
    IReadOnlyList<string> RootPaths,
    IReadOnlyList<ConversationEntry> Conversations)
{
    /// <summary>"기타 대화"(프로젝트 미연결) 그룹인지.</summary>
    public bool IsUncategorized => ProjectId is null;

    /// <summary>이 그룹에 속한 대화 개수.</summary>
    public int ConversationCount => Conversations.Count;
}
