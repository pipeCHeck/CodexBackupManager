using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CodexBackupManager.Backup.Checksums;
using CodexBackupManager.Backup.Manifest;

namespace CodexBackupManager.Backup.Reading;

/// <summary>
/// <c>.codexbackup</c> 파일을 읽기 전용으로 연다. Phase 5는 "읽기/검증"까지만 한다 — Codex에
/// 적용(Import Apply/Restore)하지 않는다(Phase 6/7의 역할).
/// </summary>
public sealed class BackupReader : IDisposable
{
    private readonly ZipArchive _archive;

    private BackupReader(ZipArchive archive)
    {
        _archive = archive;
    }

    /// <summary>파일을 연다. ZIP 자체를 열 수 없으면 예외가 전파된다(호출자가 처리).</summary>
    public static BackupReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        return new BackupReader(archive);
    }

    /// <summary>
    /// 이미 열려 있는 스트림으로 연다(Phase 07_01 — 호출자가 identity를 먼저 확인한 뒤 같은 스트림을
    /// 계속 재사용하고 싶을 때 쓴다 — 예: Restore 계층이 backup 파일을 한 번만 열어 hash를 확인하고,
    /// 그 뒤의 계획 수립/실제 적용까지 같은 바이트를 계속 신뢰하기 위해). 스트림은 seek이 가능해야
    /// 한다(ZIP 중앙 디렉터리를 읽어야 하므로).
    /// </summary>
    /// <param name="stream">이미 연 스트림.</param>
    /// <param name="leaveOpen"><c>true</c>면 이 <see cref="BackupReader"/>를 Dispose해도 스트림은 닫지 않는다.</param>
    public static BackupReader OpenFromStream(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen);
        return new BackupReader(archive);
    }

    /// <summary>모든 entry 이름 목록(원본 순서).</summary>
    public IReadOnlyList<string> EntryNames => _archive.Entries.Select(e => e.FullName).ToList();

    /// <summary>entry가 존재하는지.</summary>
    public bool HasEntry(string entryPath) => _archive.GetEntry(entryPath) is not null;

    /// <summary>entry의 압축 해제 후 바이트 길이. 없으면 <c>null</c>.</summary>
    public long? GetEntryLength(string entryPath) => _archive.GetEntry(entryPath)?.Length;

    /// <summary>entry를 읽기 스트림으로 연다. 없으면 <c>null</c>.</summary>
    public Stream? OpenEntry(string entryPath) => _archive.GetEntry(entryPath)?.Open();

    /// <summary><c>manifest.json</c>을 읽고 파싱한다.</summary>
    public BackupManifest ReadManifest()
    {
        using Stream stream = OpenEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.json entry가 없습니다.");
        using var reader = new StreamReader(stream);
        return ManifestJson.Deserialize(reader.ReadToEnd());
    }

    /// <summary><c>checksums.json</c>을 읽고 파싱한다.</summary>
    public ChecksumManifest ReadChecksums()
    {
        using Stream stream = OpenEntry("checksums.json")
            ?? throw new InvalidDataException("checksums.json entry가 없습니다.");
        using var reader = new StreamReader(stream);
        return ChecksumJson.Deserialize(reader.ReadToEnd());
    }

    /// <inheritdoc />
    public void Dispose() => _archive.Dispose();
}
