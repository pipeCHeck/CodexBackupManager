using System;
using System.IO;

namespace CodexBackupManager.App.Tests.TestSupport;

/// <summary>Repository에 커밋된 <c>tests/Fixtures/</c> 경로를 찾는다(Codex.Tests의 동명 헬퍼와 동일한 규칙).</summary>
public static class RepositoryFixtures
{
    private const string SolutionFileName = "CodexBackupManager.sln";

    /// <summary>Repository 루트.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>커밋된 가짜 Codex Home 픽스처 경로.</summary>
    public static string CodexHomeFixture => Path.Combine(RepositoryRoot, "tests", "Fixtures", "CodexHome");

    /// <summary>픽스처를 임시 폴더로 복사한다. 원본 픽스처를 건드리지 않기 위해 사용한다.</summary>
    /// <returns>복사된 임시 폴더 경로. 호출자가 정리해야 한다.</returns>
    public static string CopyCodexHomeFixtureToTemp()
    {
        string destination = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
        CopyDirectory(CodexHomeFixture, destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"{SolutionFileName}을 찾을 수 없어 Repository 루트를 특정할 수 없습니다. 탐색 시작: {AppContext.BaseDirectory}");
    }
}
