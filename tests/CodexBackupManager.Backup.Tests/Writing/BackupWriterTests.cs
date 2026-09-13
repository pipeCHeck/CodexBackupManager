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
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Projects;
using CodexBackupManager.Domain.Codex.Rollout;
using CodexBackupManager.Domain.Codex.Sessions;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
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
            Warnings: [],
            FatalErrors: []);

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
        Checksums.ChecksumEntry entry = Assert.Single(checksums.Entries, e => e.Path == "payload/rollouts/a.jsonl");
        Assert.Equal(expectedHash, entry.Sha256);

        // Phase 05_01: manifest.json 자신도 이제 체크섬으로 보호된다(순환 없이 — manifest는
        // checksums.json 내용을 참조하지 않는다).
        Checksums.ChecksumEntry manifestEntry = Assert.Single(checksums.Entries, e => e.Path == "manifest.json");
        Assert.NotEmpty(manifestEntry.Sha256);

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

    /// <summary>
    /// <paramref name="cancelAfterReadCount"/>번째 <see cref="Read"/> 호출까지는 더미 바이트를
    /// 돌려주고, 그 이후부터는(반환하기 직전) <paramref name="cts"/>를 취소한다 — 실제 스트리밍
    /// 복사가 "진행 중일 때" 취소되는 상황을 wall-clock 없이 완전히 결정적으로 재현한다(Phase
    /// 08_01 — 예전에는 "300MB 파일 + 30ms 뒤 취소"라는 race를 썼는데 GitHub Actions CI에서
    /// 병렬 부하에 따라 타이밍이 어긋나 실패했다).
    /// </summary>
    private sealed class CancelAfterNReadsStream(CancellationTokenSource cts, int cancelAfterReadCount, long totalLength) : Stream
    {
        private long _position;
        private int _readCount;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _readCount++;
            if (_readCount > cancelAfterReadCount)
            {
                cts.Cancel();
            }

            long remaining = totalLength - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, remaining);
            Array.Fill(buffer, (byte)'a', offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void 스트리밍_복사_도중_취소되면_temp를_지우고_목적지를_만들지_않는다()
    {
        // source는 실제로 존재하기만 하면 된다(내용은 CopyPayload의 변경-감지용 FileInfo 확인에만
        // 쓰인다) — 실제로 스트리밍되는 바이트는 아래 sourceFileOpener가 주입하는
        // CancelAfterNReadsStream에서 나온다. 내부 테스트 전용 오버로드(BackupWriter.Write의
        // sourceFileOpener 매개변수)로 파일 크기/실행 시간에 좌우되지 않는 결정적 취소를 만든다.
        string source = WriteSourceFile("for-cancel.jsonl", "placeholder");
        string dest = Path.Combine(_directory, "out2.codexbackup");
        ExportPlan plan = SinglePayloadPlan(source, "payload/attachments/0/huge.bin");

        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => BackupWriter.Write(
            plan, EmptyManifest(plan), dest, overwrite: false, cancellationToken: cts.Token,
            sourceFileOpener: _ => new CancelAfterNReadsStream(cts, cancelAfterReadCount: 3, totalLength: 50L * 1024 * 1024)));

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
    public void manifest_json이_1바이트_변조돼도_여전히_JSON이면_체크섬으로_잡아낸다()
    {
        string source = WriteSourceFile("a.jsonl", "{\"hello\":\"world\"}\n");
        string dest = Path.Combine(_directory, "out.codexbackup");
        ExportPlan plan = SinglePayloadPlan(source);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, EmptyManifest(plan), dest);
        Assert.True(result.Success, result.FailureReason);

        TamperManifestKeepingJsonValid(dest);

        // 변조 후에도 manifest.json은 여전히 정상적으로 파싱된다(요구사항의 "여전히 JSON parse
        // 가능" 시나리오) — 그래도 체크섬 정책(Phase 05_01: manifest.json도 체크섬에 포함)이
        // 이 변조를 잡아내야 한다.
        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("manifest.json") && e.Contains("SHA-256"));
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
            WriteEntry(archive, "manifest.json", "{\"backupFormatVersion\":99,\"appVersion\":\"0\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\",\"sourceOS\":\"test\",\"conversationCount\":0,\"dependencyConversationCount\":0,\"projectCount\":0,\"payloadCount\":0,\"projects\":[],\"conversations\":[],\"attachments\":[],\"warnings\":[]}");
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
                Attachments = [],
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

    // ── Phase 05_01 hardening: malformed 입력에서도 예외가 아니라 항상 Fail을 돌려줘야 한다 ──

    [Fact]
    public void checksums_json에_중복된_경로가_있어도_예외_없이_Fail한다()
    {
        string dest = Path.Combine(_directory, "dup-checksum.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", MinimalManifestJson());
            // 같은 경로가 두 번 — 이걸로 Dictionary를 만들면 ArgumentException이 나야 정상인데,
            // Validator는 예외 대신 Fail을 돌려줘야 한다.
            WriteEntry(archive, "checksums.json",
                "{\"entries\":[" +
                "{\"path\":\"payload/rollouts/a.jsonl\",\"byteLength\":1,\"sha256\":\"aa\"}," +
                "{\"path\":\"payload/rollouts/a.jsonl\",\"byteLength\":2,\"sha256\":\"bb\"}]}");
            WriteEntry(archive, "payload/rollouts/a.jsonl", "{}");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest); // 예외를 던지면 테스트 자체가 실패한다.
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("중복"));
    }

    [Fact]
    public void Windows_기준_대소문자만_다른_entry는_충돌로_Fail한다()
    {
        string dest = Path.Combine(_directory, "case-collision.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", MinimalManifestJson());
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}");
            WriteEntry(archive, "payload/rollouts/A.jsonl", "{}");
            WriteEntry(archive, "payload/rollouts/a.jsonl", "{}"); // Windows에서는 위와 같은 파일.
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("대소문자"));
    }

    [Fact]
    public void 허용되지_않은_위치의_entry가_있으면_Fail한다()
    {
        string dest = Path.Combine(_directory, "unexpected-entry.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", MinimalManifestJson());
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}");
            WriteEntry(archive, "unexpected-top-level-file.txt", "surprise");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("허용되지 않은 위치"));
    }

    [Fact]
    public void conversationCount이_실제_선택된_대화_수와_다르면_Fail한다()
    {
        string dest = Path.Combine(_directory, "count-mismatch.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            string manifestJson = ManifestJson.Serialize(new BackupManifest
            {
                BackupFormatVersion = 1,
                AppVersion = "0",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceOS = "test",
                ConversationCount = 5, // 실제로는 아래에 선택된 대화가 1개뿐이다.
                DependencyConversationCount = 0,
                ProjectCount = 0,
                PayloadCount = 0,
                Projects = [],
                Attachments = [],
                Conversations = [new BackupConversationMetadata { ThreadId = "t1", IsSelected = true, PayloadRolloutEntries = [], PayloadAttachmentEntries = [] }],
                Warnings = [],
            });
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("conversationCount"));
    }

    [Fact]
    public void project가_선택되지_않은_thread를_참조하면_Fail한다()
    {
        string dest = Path.Combine(_directory, "bad-project-ref.codexbackup");
        using (FileStream fs = new(dest, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            string manifestJson = ManifestJson.Serialize(new BackupManifest
            {
                BackupFormatVersion = 1,
                AppVersion = "0",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceOS = "test",
                ConversationCount = 0,
                DependencyConversationCount = 0,
                ProjectCount = 1,
                PayloadCount = 0,
                Projects = [new BackupProjectMetadata { DisplayName = "P", OriginalRootPaths = [], ConversationThreadIds = ["does-not-exist"] }],
                Attachments = [],
                Conversations = [],
                Warnings = [],
            });
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(archive, "checksums.json", "{\"entries\":[]}");
        }

        BackupValidationResult validation = BackupValidator.Validate(dest);
        Assert.False(validation.Success);
        Assert.Contains(validation.Errors, e => e.Contains("존재하지 않는"));
    }

    private static string MinimalManifestJson() => ManifestJson.Serialize(new BackupManifest
    {
        BackupFormatVersion = 1,
        AppVersion = "0",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        SourceOS = "test",
        ConversationCount = 0,
        DependencyConversationCount = 0,
        ProjectCount = 0,
        PayloadCount = 0,
        Projects = [],
        Attachments = [],
        Conversations = [],
        Warnings = [],
    });

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

    /// <summary>
    /// manifest.json의 <c>createdAtUtc</c> 타임스탬프 문자열 안의 숫자 한 자리를 바꾼다 — JSON
    /// 구조/길이는 그대로 유지되므로(여전히 유효한 JSON) "파싱 자체는 되지만 내용이 변조된" 상황을
    /// 재현할 수 있다(backupFormatVersion 등 다른 필드를 건드리면 그 필드 자체의 검증에 걸려
    /// 체크섬 불일치를 순수하게 확인할 수 없다).
    /// </summary>
    private static void TamperManifestKeepingJsonValid(string zipPath)
    {
        using FileStream fs = new(zipPath, FileMode.Open, FileAccess.ReadWrite);
        using ZipArchive archive = new(fs, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.GetEntry("manifest.json") ?? throw new InvalidOperationException("manifest.json entry not found");

        string original;
        using (Stream s = entry.Open())
        using (var reader = new StreamReader(s))
        {
            original = reader.ReadToEnd();
        }

        int marker = original.IndexOf("createdAtUtc", StringComparison.Ordinal);
        int digitIndex = original.IndexOfAny("0123456789".ToCharArray(), marker);
        char originalDigit = original[digitIndex];
        char replacement = originalDigit == '9' ? '8' : (char)(originalDigit + 1);
        string tampered = string.Concat(original.AsSpan(0, digitIndex), replacement.ToString(), original.AsSpan(digitIndex + 1));

        entry.Delete();
        ZipArchiveEntry newEntry = archive.CreateEntry("manifest.json");
        using Stream destination = newEntry.Open();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(tampered);
        destination.Write(bytes, 0, bytes.Length);
    }

    // ── Phase 05_01 hardening: 선택 대화 일부를 완전하게 백업할 수 없으면 Export 전체가 FAIL해야 한다 ──

    [Fact]
    public void 선택_3개_중_1개의_체인이_없으면_최종_backup을_만들지_않고_기존_파일도_보존한다()
    {
        string okPath1 = WriteSourceFile("ok1.jsonl", "{\"a\":1}\n");
        string okPath2 = WriteSourceFile("ok2.jsonl", "{\"b\":2}\n");
        var file1 = new RolloutFileReference(okPath1, "ok1.jsonl", "ok1", null, DateTimeOffset.UnixEpoch, false, RolloutFileKind.PlainJsonl);
        var file2 = new RolloutFileReference(okPath2, "ok2.jsonl", "ok2", null, DateTimeOffset.UnixEpoch, false, RolloutFileKind.PlainJsonl);

        var chains = new Dictionary<string, ThreadChain>
        {
            ["ok1"] = new ThreadChain("ok1", [file1], new HistoryBaseReference?[1], null, null, null, []),
            ["ok2"] = new ThreadChain("ok2", [file2], new HistoryBaseReference?[1], null, null, null, []),
            // "missing"은 의도적으로 체인이 없다 — 선택 3개 중 1개가 완전히 백업 불가능한 상황.
        };

        var entries = new List<ConversationEntry>
        {
            new() { ThreadId = "ok1", Row = new ThreadRow { Id = "ok1" }, Title = new ThreadTitle("ok1", ThreadTitleSource.StateTitle), Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned) },
            new() { ThreadId = "ok2", Row = new ThreadRow { Id = "ok2" }, Title = new ThreadTitle("ok2", ThreadTitleSource.StateTitle), Project = new ProjectAssignment(null, ProjectAssignmentSource.Unassigned) },
        };

        var catalog = new CodexCatalog([], entries, chains, [], DateTimeOffset.UtcNow, new CodexCatalogStats(2, 2, 0, TimeSpan.Zero, TimeSpan.Zero));

        ExportPlan plan = ExportPlanBuilder.Build(catalog, new HashSet<string> { "ok1", "ok2", "missing" });
        Assert.NotEmpty(plan.FatalErrors);

        string dest = Path.Combine(_directory, "out.codexbackup");
        File.WriteAllText(dest, "existing good backup — must survive"); // 기존 정상 backup 시뮬레이션.
        string before = File.ReadAllText(dest);

        BackupManifest manifest = ManifestBuilder.Build(plan, sourceCodexDesktopVersion: null, sourceCodexCliVersion: null, createdAtUtc: DateTimeOffset.UtcNow);
        BackupWriter.WriteResult result = BackupWriter.Write(plan, manifest, dest, overwrite: true);

        Assert.False(result.Success); // "부분 성공"을 성공으로 보여주지 않는다.
        Assert.Equal(before, File.ReadAllText(dest)); // 기존 파일이 훼손되지 않았다.
        Assert.Empty(Directory.EnumerateFiles(_directory, ".*.tmp-*")); // FatalErrors면 temp조차 만들지 않는다.
    }
}
