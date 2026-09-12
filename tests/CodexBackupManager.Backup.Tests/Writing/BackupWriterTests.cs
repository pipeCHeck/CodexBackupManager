using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.Backup.Manifest;
using CodexBackupManager.Backup.Planning;
using CodexBackupManager.Backup.Reading;
using CodexBackupManager.Backup.Validation;
using CodexBackupManager.Backup.Writing;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Writing;

/// <summary>
/// <see cref="BackupWriter"/>의 atomic publish/self-validation/원본 변경 감지/스트리밍을 실제 파일로
/// 검증한다. 실제 사용자 데이터는 쓰지 않는다 — 전부 이 테스트가 만든 합성 임시 파일이다.
/// </summary>
public sealed class BackupWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-backup-writer-tests", Guid.NewGuid().ToString("N"));

    public BackupWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteSourceFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ExportPlan SinglePayloadPlan(string sourcePath, string entryPath = "payload/rollouts/a.jsonl")
        => new(
            Conversations: [],
            RolloutFiles: [new PlannedPayloadFile(sourcePath, entryPath)],
            Attachments: [],
            Projects: [],
            Warnings: []);

    private static BackupManifest EmptyManifest(ExportPlan plan) =>
        ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void 정상_Export는_원본과_동일한_SHA256을_갖는_payload를_만든다()
    {
        string source = WriteSourceFile("a.jsonl", "{\"hello\":\"world\"}\n");
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));

        ExportPlan plan = SinglePayloadPlan(source);
        string dest = Path.Combine(_directory, "out.codexbackup");

        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(File.Exists(dest));

        using BackupReader reader = BackupReader.Open(dest);
        Checksums.ChecksumManifest checksums = reader.ReadChecksums();
        Checksums.ChecksumEntry entry = Assert.Single(checksums.Entries);
        Assert.Equal("payload/rollouts/a.jsonl", entry.Path);
        Assert.Equal(expectedHash, entry.Sha256);

        BackupManifest manifest = reader.ReadManifest();
        Assert.Equal(1, manifest.BackupFormatVersion);
    }

    [Fact]
    public void 대상_파일이_이미_있고_overwrite가_false면_시작_전에_실패한다()
    {
        string source = WriteSourceFile("a.jsonl", "content");
        string dest = Path.Combine(_directory, "out.codexbackup");
        File.WriteAllText(dest, "existing backup — must not be touched");
        string originalContent = File.ReadAllText(dest);

        ExportPlan plan = SinglePayloadPlan(source);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest, overwrite: false);

        Assert.False(result.Success);
        Assert.Equal(originalContent, File.ReadAllText(dest)); // 기존 파일이 훼손되지 않았다.
    }

    [Fact]
    public void 취소하면_temp_파일을_지우고_목적지도_만들지_않는다()
    {
        string source = WriteSourceFile("a.jsonl", new string('x', 1024));
        string dest = Path.Combine(_directory, "out.codexbackup");

        ExportPlan plan = SinglePayloadPlan(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => BackupWriter.Write(plan, EmptyManifest(plan), dest, cancellationToken: cts.Token));

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".*.tmp-*"));
    }

    [Fact]
    public async Task Export_도중_원본_파일이_바뀌면_실패하고_temp를_지운다()
    {
        // 실제 레이스를 재현하기 위해 스트리밍 복사가 눈에 띄는 시간이 걸리도록 큰 파일을 쓰고,
        // 복사 도중 백그라운드에서 파일을 수정한다(FileShare.ReadWrite라 실제로 가능하다 —
        // Codex가 실행 중일 때 rollout이 append되는 상황과 같다).
        string source = Path.Combine(_directory, "big.jsonl");
        File.WriteAllText(source, new string('a', 150 * 1024 * 1024));
        string dest = Path.Combine(_directory, "out.codexbackup");

        ExportPlan plan = SinglePayloadPlan(source);

        Task mutator = Task.Run(async () =>
        {
            await Task.Delay(30);
            using var fs = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            byte[] extra = "more"u8.ToArray();
            fs.Write(extra, 0, extra.Length);
            fs.Flush();
        });

        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest);
        await mutator;

        Assert.False(result.Success);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".*.tmp-*"));
    }

    [Fact]
    public void 큰_파일도_경계_있는_메모리로_스트리밍한다()
    {
        string source = Path.Combine(_directory, "huge.jsonl");
        const int sizeBytes = 100 * 1024 * 1024;
        using (FileStream fs = new(source, FileMode.Create, FileAccess.Write))
        {
            byte[] chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (int i = 0; i < sizeBytes / chunk.Length; i++)
            {
                fs.Write(chunk, 0, chunk.Length);
            }
        }

        string dest = Path.Combine(_directory, "out.codexbackup");
        // "payload/attachments/…" 경로를 쓴다 — 이 테스트의 목적은 스트리밍/메모리 검증이지
        // JSONL 유효성이 아니다("payload/rollouts/…"는 Validator가 최소 JSON parse를 확인한다).
        ExportPlan plan = SinglePayloadPlan(source, "payload/attachments/0/huge.bin");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest);

        long after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.True(result.Success, result.FailureReason);
        // 파일 크기(100MB)에 비례해서 메모리가 늘지 않아야 한다(버퍼 크기는 80KB 고정) —
        // 여유를 넉넉히 둬도 파일 크기의 1/4을 넘으면 전체를 메모리에 올렸다는 뜻이다.
        Assert.True(after - before < sizeBytes / 4, $"메모리 증가량이 너무 큽니다: {after - before} bytes");
    }

    [Fact]
    public void checksums_json의_payload_1바이트가_변조되면_Validator가_실패시킨다()
    {
        string source = WriteSourceFile("a.jsonl", "{\"hello\":\"world\"}\n");
        string dest = Path.Combine(_directory, "out.codexbackup");
        ExportPlan plan = SinglePayloadPlan(source);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest);
        Assert.True(result.Success, result.FailureReason);

        TamperEntry(dest, "payload/rollouts/a.jsonl");

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("SHA-256"));
    }

    [Fact]
    public void manifest가_없으면_Validator가_실패시킨다()
    {
        string dest = Path.Combine(_directory, "no-manifest.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("checksums.json");
            using Stream s = entry.Open();
            byte[] bytes = "{\"entries\":[]}"u8.ToArray();
            s.Write(bytes);
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("manifest.json"));
    }

    [Fact]
    public void 지원하지_않는_버전이면_Validator가_실패시킨다()
    {
        string dest = Path.Combine(_directory, "bad-version.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", "{\"backupFormatVersion\":99,\"appVersion\":\"0\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\",\"sourceOS\":\"test\",\"conversationCount\":0,\"dependencyConversationCount\":0,\"projectCount\":0,\"payloadCount\":0,\"projects\":[],\"conversations\":[],\"warnings\":[]}");
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("backupFormatVersion"));
    }

    [Fact]
    public void 중복된_ZIP_entry가_있으면_Validator가_실패시킨다()
    {
        string dest = Path.Combine(_directory, "dup.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", "{}");
            WriteEntry(archive, "manifest.json", "{}"); // 중복.
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("중복"));
    }

    [Fact]
    public void path_traversal_entry가_있으면_Validator가_실패시킨다()
    {
        string dest = Path.Combine(_directory, "traversal.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", "{}");
            WriteEntry(archive, "../../evil.txt", "gotcha");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("path traversal"));
    }

    [Fact]
    public void 참조된_payload가_ZIP에_없으면_Validator가_실패시킨다()
    {
        string dest = Path.Combine(_directory, "missing-ref.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            string manifestJson = ManifestJson.Serialize(new BackupManifest
            {
                BackupFormatVersion = 1,
                AppVersion = "0",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceOS = "test",
                ConversationCount = 1,
                DependencyConversationCount = 0,
                ProjectCount = 0,
                PayloadCount = 1,
                Projects = [],
                Conversations =
                [
                    new BackupConversationMetadata
                    {
                        ThreadId = "t1",
                        IsSelected = true,
                        PayloadRolloutEntries = ["payload/rollouts/missing.jsonl"],
                        PayloadAttachmentEntries = [],
                    },
                ],
                Warnings = [],
            });
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}"); // payload entry 자체가 없다.
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("checksums.json에 없습니다") || e.Contains("ZIP에 없습니다"));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream s = entry.Open();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void TamperEntry(string zipPath, string entryName)
    {
        using FileStream fs = new(zipPath, FileMode.Open, FileAccess.ReadWrite);
        using ZipArchive archive = new(fs, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException("entry not found");
        using Stream s = entry.Open();
        s.Position = 0;
        int b = s.ReadByte();
        s.Position = 0;
        s.WriteByte((byte)(b ^ 0xFF));
    }
}
