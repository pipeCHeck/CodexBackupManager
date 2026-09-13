using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexBackupManager.Backup.Container;

namespace CodexBackupManager.Restore;

/// <summary>
/// write 시작 전에 이번 Restore가 실제로 건드릴 모든 파일을 복사해 둔다(Phase 7 요구사항 7).
/// <c>.codex</c> 내부가 아니라 <c>%LOCALAPPDATA%\CodexBackupManager\Snapshots\</c>에 만든다.
/// </summary>
/// <remarks>
/// temp 하위 디렉터리에 전부 복사 + 재해시로 검증한 뒤, 마지막에 <c>manifest.json</c>을 쓰는 것으로
/// "publish"를 표시한다 — 그 전에 프로세스가 죽으면 manifest가 없는 미완성 snapshot으로 남는다(자동
/// 정리는 하지 않는다, 사용자가 나중에 직접 확인할 수 있게 남겨 둔다). Snapshot 생성 중 실패하면
/// write는 0건이어야 한다 — 이 서비스 자체가 Codex 파일을 전혀 수정하지 않는다(읽기만 한다).
/// </remarks>
public static class SnapshotService
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>Snapshot 저장 루트. 기본값은 <c>%LOCALAPPDATA%\CodexBackupManager\Snapshots</c>.</summary>
    public static string DefaultSnapshotRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBackupManager", "Snapshots");

    /// <summary>
    /// <paramref name="targetAbsolutePaths"/>로 지정된 파일들을 snapshot한다. 존재하지 않는 파일은
    /// <see cref="SnapshotFileEntry.ExistedBefore"/>가 <c>false</c>로 기록된다(=이번 Restore가 새로
    /// 만들 파일이라는 뜻 — Rollback 시 삭제 대상).
    /// </summary>
    /// <param name="snapshotRoot">Snapshot 루트 디렉터리(없으면 만든다).</param>
    /// <param name="codexHomePath">진단용 원본 Codex Home 경로.</param>
    /// <param name="importPlanBackupSha256">이 Restore가 어떤 backup 기준인지(<c>plan.Backup.BackupFileSha256</c>).</param>
    /// <param name="targetAbsolutePaths">(라벨, 절대경로) 쌍 목록 — 이번 Restore가 실제로 건드릴 파일 전부.</param>
    public static SnapshotCreateResult Create(
        string snapshotRoot,
        string codexHomePath,
        string importPlanBackupSha256,
        IReadOnlyList<(string Label, string AbsolutePath)> targetAbsolutePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(targetAbsolutePaths);

        string snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        string snapshotDir = Path.Combine(snapshotRoot, snapshotId);

        try
        {
            Directory.CreateDirectory(snapshotDir);

            var entries = new List<SnapshotFileEntry>();
            int index = 0;
            foreach ((string label, string absolutePath) in targetAbsolutePaths)
            {
                index++;
                string snapshotFileName = $"{index:D4}.bin";

                if (!File.Exists(absolutePath))
                {
                    entries.Add(new SnapshotFileEntry(label, absolutePath, snapshotFileName, ExistedBefore: false, 0, null));
                    continue;
                }

                string snapshotFilePath = Path.Combine(snapshotDir, snapshotFileName);
                StreamingHashCopy.Result copied;
                using (FileStream source = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream dest = new(snapshotFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    copied = StreamingHashCopy.CopyWithHash(source, dest);
                    // Phase 8 release-blocker C — mutation 전에 존재하는 유일한 복구 수단이므로
                    // rollout append/journal과 같은 durability 수준(디스크에 실제로 내려감)을 갖게
                    // 한다. dest를 닫기 전에 명시적으로 flushToDisk한다.
                    dest.Flush(flushToDisk: true);
                }

                // 복사 직후 재해시로 검증한다 — 쓰기 도중 손상되지 않았는지 확인한다.
                using (FileStream verify = new(snapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    StreamingHashCopy.Result verified = StreamingHashCopy.HashOnly(verify);
                    if (verified.ByteLength != copied.ByteLength ||
                        !string.Equals(verified.Sha256Hex, copied.Sha256Hex, StringComparison.Ordinal))
                    {
                        return new SnapshotCreateResult(false, null, null, $"Snapshot 파일 검증 실패: {label}");
                    }
                }

                entries.Add(new SnapshotFileEntry(
                    label, absolutePath, snapshotFileName, ExistedBefore: true, copied.ByteLength, copied.Sha256Hex));
            }

            var manifest = new SnapshotManifest(
                snapshotId, DateTimeOffset.UtcNow, codexHomePath, importPlanBackupSha256, entries);

            string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            string manifestPath = Path.Combine(snapshotDir, ManifestFileName);
            string tempManifestPath = manifestPath + ".tmp";

            // Phase 8 release-blocker C — RestoreTransactionJournalStore.Write와 같은 패턴(temp
            // FileStream + flushToDisk + atomic move)으로 바꿔, 이 manifest도 rollout/journal과
            // 같은 durability 수준을 갖게 한다. manifest가 실제로 diskomit 되기 전까지는 아직
            // "publish"된 것으로 보지 않는다 — 요구사항 7의 원래 의도(manifest.json 존재 = 이
            // Snapshot이 완성됐다는 표시)와 일치한다.
            using (FileStream manifestStream = new(tempManifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                manifestStream.Write(manifestBytes, 0, manifestBytes.Length);
                manifestStream.Flush(flushToDisk: true);
            }

            File.Move(tempManifestPath, manifestPath, overwrite: false);

            return new SnapshotCreateResult(true, snapshotDir, manifest, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SnapshotCreateResult(false, null, null, $"Snapshot 생성 중 오류: {ex.GetType().Name}");
        }
    }

    /// <summary>이미 만들어진 snapshot 디렉터리에서 manifest를 읽는다(Rollback 등에 쓴다).</summary>
    public static SnapshotManifest? ReadManifest(string snapshotDirectory)
    {
        string manifestPath = Path.Combine(snapshotDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<SnapshotManifest>(json, ManifestJsonOptions);
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
}
