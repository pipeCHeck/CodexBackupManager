using System;

namespace CodexBackupManager.App.ViewModels;

/// <summary>
/// (Phase 9_2-33 → 9_U-01) 목록 검색 규칙 — 메인 화면과 가져오기 화면이 같이 쓴다. 표시 여부만 정하고 선택은 바꾸지 않는다.
/// 대화 제목이 맞으면 그 대화(와 부모 프로젝트)를, 프로젝트 이름이 맞으면 그 프로젝트의 대화를 모두 보인다.
/// 앞뒤 공백은 빼고 대소문자는 무시한다.
/// </summary>
internal static class TreeSearch
{
    /// <summary>검색어 정리(앞뒤 공백 제거, <c>null</c>은 빈 문자열).</summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim();

    /// <summary>대화 제목이 검색어를 포함하는지(검색어가 비었으면 참).</summary>
    public static bool TitleMatches(string query, string title) => query.Length == 0 || Contains(title, query);

    /// <summary>대화 한 줄을 보일지 — 제목 또는 프로젝트 이름 일치(검색어가 비었으면 참).</summary>
    public static bool IsShown(string query, string projectName, string title)
        => TitleMatches(query, title) || Contains(projectName, query);

    private static bool Contains(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
