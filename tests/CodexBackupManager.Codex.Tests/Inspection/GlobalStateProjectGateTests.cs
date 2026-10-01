using System;
using System.IO;
using System.Linq;
using System.Text;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// Phase 9_5a-T1 — <see cref="GlobalStateProjectGate"/>: 확인한 Desktop 형태일 때만 통과하고, 형태가 다르면 사유를 알려 준다(읽기 전용).
/// </summary>
public sealed class GlobalStateProjectGateTests
{
    private const string Home = @"C:\Users\Test\.codex";
    private static readonly CanonicalPath HomePath = CanonicalPath.Create(Home);
    private const string HostKeyJson = "\"local:C:\\\\Users\\\\Test\\\\.codex\"";

    private const string Entry =
        "{\"id\":\"11111111-1111-4111-8111-111111111111\",\"name\":\"알파\",\"rootPaths\":[\"C:\\\\Work\\\\Alpha\"],\"createdAt\":1759300000000,\"updatedAt\":1759300000001}";

    /// <summary>실측 형태(키 순서도 실측처럼 다른 키 사이에 섞여 있다).</summary>
    internal static string Valid(
        string? entry = null, string? order = null, string? mapping = null, string? migration = null, string extra = "")
        => "{\"electron-persisted-atom-state\":{\"x\":0.5},\"project-order\":" + (order ?? "[\"11111111-1111-4111-8111-111111111111\"]") +
           ",\"local-projects\":{\"11111111-1111-4111-8111-111111111111\":" + (entry ?? Entry) + "}" +
           ",\"app-server-project-id-by-legacy-project-id-by-host\":" + (mapping ?? "{" + HostKeyJson + ":{\"11111111-1111-4111-8111-111111111111\":\"019a0000-0000-7000-8000-000000000001\"}}") +
           ",\"app-server-projects-migration-by-host\":" + (migration ?? "{" + HostKeyJson + ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}}") +
           extra + "}";

    private static GlobalStateGateFailure Gate(string json) => GlobalStateProjectGate.CheckBytes(Encoding.UTF8.GetBytes(json), HomePath).Failure;

    [Fact]
    public void 실측_형태는_통과하고_host_키와_해시를_돌려준다()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Valid());
        GlobalStateProjectGate.Result result = GlobalStateProjectGate.CheckBytes(bytes, HomePath);

        Assert.True(result.IsSupported, result.Failure.ToString());
        Assert.Equal(@"local:C:\Users\Test\.codex", result.HostKey);
        Assert.Equal(64, result.Sha256Hex!.Length);
    }

    [Fact]
    public void 빈_레거시_저장소도_통과한다()
        => Assert.Equal(GlobalStateGateFailure.None, Gate(
            "{\"local-projects\":{},\"project-order\":[],\"app-server-project-id-by-legacy-project-id-by-host\":{" + HostKeyJson + ":{}}," +
            "\"app-server-projects-migration-by-host\":{" + HostKeyJson + ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}}}"));

    [Fact]
    public void BOM이_있으면_실패()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Valid())];
        Assert.Equal(GlobalStateGateFailure.ByteOrderMark, GlobalStateProjectGate.CheckBytes(bytes, HomePath).Failure);
    }

    [Fact]
    public void 잘못된_UTF8이면_실패()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("{\"a\":\""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}")];
        Assert.Equal(GlobalStateGateFailure.InvalidUtf8, GlobalStateProjectGate.CheckBytes(bytes, HomePath).Failure);
    }

    [Fact]
    public void 들여쓰기된_파일은_왕복이_달라_실패()
        => Assert.Equal(GlobalStateGateFailure.RoundTripMismatch, Gate(Valid().Replace(",\"", ",\n  \"", StringComparison.Ordinal)));

    [Fact]
    public void 다른_이스케이프를_쓴_파일은_왕복이_달라_실패()
        => Assert.Equal(GlobalStateGateFailure.RoundTripMismatch, Gate(Valid().Replace("알파", "\\uC54C\\uD30C", StringComparison.Ordinal)));

    [Theory]
    [InlineData("[]", GlobalStateGateFailure.NotObject)]
    [InlineData("{\"a\":1,\"a\":1}", GlobalStateGateFailure.InvalidJson)]
    [InlineData("{\"project-order\":[]}", GlobalStateGateFailure.LocalProjectsInvalid)]
    [InlineData("{\"local-projects\":[]}", GlobalStateGateFailure.LocalProjectsInvalid)]
    public void 최상위_구조가_다르면_실패(string json, GlobalStateGateFailure expected) => Assert.Equal(expected, Gate(json));

    [Theory]
    [InlineData(",\"extra\":1}", "field-added")]
    [InlineData("}", "field-missing")]
    public void 레거시_항목_필드가_추가되거나_빠지면_실패(string tail, string label)
    {
        string entry = tail == "}"
            ? Entry.Replace(",\"updatedAt\":1759300000001", string.Empty, StringComparison.Ordinal)
            : Entry[..^1] + tail;
        Assert.True(Gate(Valid(entry: entry)) == GlobalStateGateFailure.LocalProjectEntryInvalid, label);
    }

    [Theory]
    [InlineData("\"name\":\"알파\"", "\"name\":null")]
    [InlineData("\"createdAt\":1759300000000", "\"createdAt\":1759300000000.5")]
    [InlineData("\"rootPaths\":[\"C:\\\\Work\\\\Alpha\"]", "\"rootPaths\":\"C:\\\\Work\"")]
    [InlineData("\"id\":\"11111111-1111-4111-8111-111111111111\"", "\"id\":\"other\"")]
    public void 레거시_항목_타입이나_id가_다르면_실패(string from, string to)
        => Assert.Equal(GlobalStateGateFailure.LocalProjectEntryInvalid, Gate(Valid(entry: Entry.Replace(from, to, StringComparison.Ordinal))));

    [Fact]
    public void project_order가_문자열_배열이_아니면_실패()
    {
        Assert.Equal(GlobalStateGateFailure.ProjectOrderInvalid, Gate(Valid(order: "[1]")));
        Assert.Equal(GlobalStateGateFailure.ProjectOrderInvalid, Gate(Valid(order: "{}")));
    }

    [Fact]
    public void 매핑이_없거나_형태가_다르면_실패()
    {
        Assert.Equal(GlobalStateGateFailure.MappingInvalid, Gate(Valid(mapping: "[]")));
        Assert.Equal(GlobalStateGateFailure.MappingInvalid, Gate(Valid(mapping: "{" + HostKeyJson + ":{\"a\":1}}")));
        Assert.Equal(GlobalStateGateFailure.MappingInvalid, Gate(Valid(mapping: "{" + HostKeyJson + ":[]}")));
    }

    [Fact]
    public void 현재_Home의_host가_0개면_실패()
        => Assert.Equal(GlobalStateGateFailure.HostKeyMissing, Gate(Valid(mapping: "{\"local:D:\\\\Other\\\\.codex\":{}}")));

    [Fact]
    public void 현재_Home과_맞는_host가_2개면_실패()
        => Assert.Equal(GlobalStateGateFailure.HostKeyAmbiguous, Gate(Valid(
            mapping: "{" + HostKeyJson + ":{},\"local:c:\\\\users\\\\test\\\\.codex\\\\\":{}}")));

    [Fact]
    public void projectsMigrated가_true가_아니면_실패()
    {
        Assert.Equal(GlobalStateGateFailure.ProjectsNotMigrated, Gate(Valid(
            migration: "{" + HostKeyJson + ":{\"version\":1,\"projectsMigrated\":false,\"threadAssignmentsMigrated\":false}}")));
        Assert.Equal(GlobalStateGateFailure.ProjectsNotMigrated, Gate(Valid(migration: "{" + HostKeyJson + ":{\"version\":1}}")));
        Assert.Equal(GlobalStateGateFailure.MigrationStateInvalid, Gate(Valid(migration: "{\"local:D:\\\\Other\":{\"projectsMigrated\":true}}")));
        Assert.Equal(GlobalStateGateFailure.MigrationStateInvalid, Gate(Valid(migration: "[]")));
    }

    [Fact]
    public void 파일이_없으면_실패()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cbm-gate-missing-{Guid.NewGuid():N}.json");
        Assert.Equal(GlobalStateGateFailure.FileMissing, GlobalStateProjectGate.Check(path, HomePath).Failure);
    }

    [Fact]
    public void 파일로도_같은_판정을_하고_파일을_바꾸지_않는다()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cbm-gate-{Guid.NewGuid():N}.json");
        byte[] bytes = Encoding.UTF8.GetBytes(Valid());
        File.WriteAllBytes(path, bytes);
        try
        {
            DateTime before = File.GetLastWriteTimeUtc(path);
            Assert.True(GlobalStateProjectGate.Check(path, HomePath).IsSupported);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 레거시_필드_목록은_실측_5개다()
        => Assert.Equal(["id", "name", "rootPaths", "createdAt", "updatedAt"], GlobalStateProjectGate.LocalProjectFields.ToArray());
}
