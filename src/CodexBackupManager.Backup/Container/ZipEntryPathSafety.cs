using System;

namespace CodexBackupManager.Backup.Container;

/// <summary>
/// ZIP entry 이름이 안전한 상대 경로인지 확인한다(path traversal 방지).
/// </summary>
/// <remarks>
/// 우리가 직접 만드는 entry 이름은 항상 안전하지만(이중 방어선), 이 검사의 진짜 목적은
/// <see cref="BackupValidator"/>가 "우리 것인 척하는" 조작된 ZIP을 열었을 때다 — 절대경로나
/// <c>..</c> 세그먼트가 있는 entry는 ZIP 밖의 임의 경로에 파일을 쓰거나 덮어쓸 수 있다.
/// </remarks>
public static class ZipEntryPathSafety
{
    /// <summary>entry 이름이 안전한 상대 경로인지 확인한다.</summary>
    public static bool IsSafe(string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return false;
        }

        // 정규화된 구분자만 다룬다 — ZIP 표준은 '/'를 쓰지만 방어적으로 '\'도 함께 막는다.
        if (entryName.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (entryName.StartsWith('/'))
        {
            return false; // 절대 경로.
        }

        if (entryName.Length >= 2 && entryName[1] == ':')
        {
            return false; // "C:..." 같은 드라이브 지정 경로.
        }

        string[] segments = entryName.Split('/');
        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                return false;
            }

            if (segment.Length == 0)
            {
                return false; // "a//b" 같은 빈 세그먼트.
            }
        }

        return true;
    }
}
