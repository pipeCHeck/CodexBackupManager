using System;
using System.Security.Cryptography;
using System.Text;

namespace CodexBackupManager.Domain.Diagnostics;

/// <summary>
/// 로그/진단 출력에서 민감 정보를 가리기 위한 헬퍼.
/// </summary>
/// <remarks>
/// CLAUDE.md §29 및 Phase 0 조사 결론에 따라 다음은 로그에 원문으로 남기지 않는다.
/// <list type="bullet">
///   <item>대화 내용</item>
///   <item><c>threads.first_user_message</c> / <c>preview</c> / <c>title</c> / <c>name</c></item>
///   <item>개인 프로젝트 경로 전문</item>
/// </list>
/// 대신 길이, 개수, 마지막 구성요소, SHA-256 단축 해시로만 기록한다.
/// </remarks>
public static class Redact
{
    /// <summary>
    /// 경로를 로그 안전한 형태로 축약한다.
    /// 루트 형태와 깊이, 마지막 폴더명 일부, 해시만 남긴다.
    /// </summary>
    /// <example><c>&lt;drive&gt;/…(4)/…Project#3f9a11c2</c></example>
    public static string Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<empty>";
        }

        string unified = path.Replace('/', '\\').TrimEnd('\\');
        string[] parts = unified.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string root = unified.Length >= 2 && unified[1] == ':'
            ? "<drive>"
            : unified.StartsWith(@"\\", StringComparison.Ordinal) ? "<unc>" : "<rel>";

        string leaf = parts.Length > 0 ? parts[^1] : string.Empty;
        string leafHint = leaf.Length <= 4 ? new string('*', leaf.Length) : leaf[^4..];

        return $"{root}/…({parts.Length})/…{leafHint}#{ShortHash(path)}";
    }

    /// <summary>사용자 텍스트(제목/미리보기/대화)를 길이와 해시로만 표현한다.</summary>
    public static string Text(string? text)
        => text is null ? "<null>" : $"<text len={text.Length} #{ShortHash(text)}>";

    /// <summary>SHA-256 앞 8자리 소문자 hex.</summary>
    public static string ShortHash(string? value)
    {
        if (value is null)
        {
            return "00000000";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..8];
    }
}
