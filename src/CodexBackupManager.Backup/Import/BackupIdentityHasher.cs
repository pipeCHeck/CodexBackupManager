using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Container;
using CodexBackupManager.Backup.Manifest;

namespace CodexBackupManager.Backup.Import;

/// <summary>
/// <c>.codexbackup</c> 파일 전체의 identity(길이+streaming SHA-256)를 계산/비교하는 공용 로직
/// (Phase 06_03). <see cref="ImportPreviewBuilder"/>(Preview 시점 pin)와 <see cref="ImportPlanBuilder"/>
/// (Plan 생성 직전 재확인)가 같은 계산을 공유해서 "언제 계산했든 같은 정의의 identity"임을 보장한다.
/// </summary>
internal static class BackupIdentityHasher
{
    /// <summary>파일 전체를 스트리밍으로 다시 읽어 identity를 새로 만든다(메모리에 전체 로드 금지).</summary>
    public static ImportBackupIdentity Compute(string backupFilePath, BackupManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        (long length, string sha256) = Hash(backupFilePath, cancellationToken);

        return new ImportBackupIdentity(
            backupFilePath,
            length,
            sha256,
            manifest.BackupFormatVersion,
            manifest.CreatedAtUtc,
            manifest.AppVersion,
            manifest.ConversationCount);
    }

    /// <summary>
    /// <paramref name="currentFilePath"/>를 지금 다시 hash해서 <paramref name="expected"/>와 길이+SHA-256이
    /// 정확히 같은지 확인한다. source of truth는 hash뿐이다 — 경로 문자열이 다르더라도 내용이 같으면
    /// 일치로 본다(같은 파일을 다른 위치에서 다시 불러온 경우도 정당하게 허용하기 위함).
    /// </summary>
    public static bool Matches(ImportBackupIdentity expected, string currentFilePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        (long length, string sha256) = Hash(currentFilePath, cancellationToken);

        return length == expected.BackupFileLength &&
            string.Equals(sha256, expected.BackupFileSha256, StringComparison.Ordinal);
    }

    private static (long Length, string Sha256) Hash(string filePath, CancellationToken cancellationToken)
    {
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        StreamingHashCopy.Result hashed = StreamingHashCopy.HashOnly(stream, cancellationToken);
        return (hashed.ByteLength, hashed.Sha256Hex);
    }
}
