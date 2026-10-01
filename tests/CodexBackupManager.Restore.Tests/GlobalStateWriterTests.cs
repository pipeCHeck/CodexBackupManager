using System;
using System.Text;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Restore.Tests;

/// <summary>Phase 9_5a-T1 — <see cref="GlobalStateWriter"/>의 순수 변환과 파일 수준 검증.</summary>
public sealed class GlobalStateWriterTests
{
    private const string Home = @"C:\Users\Test\.codex";
    private static readonly CanonicalPath HomePath = CanonicalPath.Create(Home);
    private const string Host = "\"local:C:\\\\Users\\\\Test\\\\.codex\"";

    private static readonly string Original =
        "{\"a\":{\"ratio\":0.5,\"s\":\"\\\\/ 한\"},\"local-projects\":{},\"project-order\":[]," +
        "\"app-server-project-id-by-legacy-project-id-by-host\":{\"local:D:\\\\X\":{\"k\":\"v\"}," + Host + ":{}}," +
        "\"app-server-projects-migration-by-host\":{" + Host + ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}},\"z\":1e+21}";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static GlobalStateProjectAddition Addition(string dbId, string name = "새 프로젝트")
        => new(dbId, name, [@"C:\Work\" + name], 1_759_300_000_000);

    [Fact]
    public void 추가하면_정확한_바이트가_나온다()
    {
        int n = 0;
        GlobalStateTransformResult result = GlobalStateWriter.Transform(
            Bytes(Original), HomePath, [Addition("db-1"), Addition("db-2", "둘")], () => $"0000000{++n}-0000-4000-8000-000000000000");

        string expected =
            "{\"a\":{\"ratio\":0.5,\"s\":\"\\\\/ 한\"}," +
            "\"local-projects\":{\"00000001-0000-4000-8000-000000000000\":{\"id\":\"00000001-0000-4000-8000-000000000000\",\"name\":\"새 프로젝트\",\"rootPaths\":[\"C:\\\\Work\\\\새 프로젝트\"],\"createdAt\":1759300000000,\"updatedAt\":1759300000000}," +
            "\"00000002-0000-4000-8000-000000000000\":{\"id\":\"00000002-0000-4000-8000-000000000000\",\"name\":\"둘\",\"rootPaths\":[\"C:\\\\Work\\\\둘\"],\"createdAt\":1759300000000,\"updatedAt\":1759300000000}}," +
            "\"project-order\":[\"00000001-0000-4000-8000-000000000000\",\"00000002-0000-4000-8000-000000000000\"]," +
            "\"app-server-project-id-by-legacy-project-id-by-host\":{\"local:D:\\\\X\":{\"k\":\"v\"}," + Host +
            ":{\"00000001-0000-4000-8000-000000000000\":\"db-1\",\"00000002-0000-4000-8000-000000000000\":\"db-2\"}}," +
            "\"app-server-projects-migration-by-host\":{" + Host + ":{\"version\":1,\"projectsMigrated\":true,\"threadAssignmentsMigrated\":false}},\"z\":1e+21}";
        Assert.Equal(expected, Encoding.UTF8.GetString(result.NewBytes));
        Assert.Equal(2, result.Added.Count);
        Assert.Null(GlobalStateWriter.Verify(Bytes(Original), result.NewBytes, HomePath, result.Added));
    }

    [Fact]
    public void 이미_매핑된_DB_ID는_추가하지_않는다()
    {
        GlobalStateTransformResult first = GlobalStateWriter.Transform(Bytes(Original), HomePath, [Addition("db-1")]);
        GlobalStateTransformResult second = GlobalStateWriter.Transform(first.NewBytes, HomePath, [Addition("db-1"), Addition("db-1")]);

        Assert.True(second.IsNoOp);
        Assert.Equal(first.NewBytes, second.NewBytes);
        Assert.Single(GlobalStateWriter.Transform(Bytes(Original), HomePath, [Addition("db-9"), Addition("db-9")]).Added);
    }

    [Fact]
    public void 기본_레거시_ID는_UUIDv4_소문자다()
    {
        GlobalStateTransformResult result = GlobalStateWriter.Transform(Bytes(Original), HomePath, [Addition("db-1")]);
        string id = result.Added[0].LegacyProjectId;
        Assert.Equal(4, Guid.Parse(id).Version);
        Assert.Equal(id.ToLowerInvariant(), id);
    }

    [Fact]
    public void 게이트를_통과하지_못한_원본은_변환하지_않는다()
        => Assert.Throws<InvalidOperationException>(() => GlobalStateWriter.Transform(Bytes(Original.Replace(",", ", ", StringComparison.Ordinal)), HomePath, [Addition("db-1")]));

    [Theory]
    [InlineData("\"ratio\":0.5", "\"ratio\":0.6")]                      // 다른 키 변경
    [InlineData("\"local:D:\\\\X\":{\"k\":\"v\"}", "\"local:D:\\\\X\":{\"k\":\"w\"}")] // 다른 host 매핑 변경
    [InlineData(",\"z\":1e+21", ",\"z\":1E+21")]                       // 숫자 원문 변경
    public void 검증은_추가분_외_변경을_찾는다(string from, string to)
    {
        GlobalStateTransformResult result = GlobalStateWriter.Transform(Bytes(Original), HomePath, [Addition("db-1")]);
        byte[] tampered = Bytes(Encoding.UTF8.GetString(result.NewBytes).Replace(from, to, StringComparison.Ordinal));

        Assert.NotNull(GlobalStateWriter.Verify(Bytes(Original), tampered, HomePath, result.Added));
    }

    [Fact]
    public void 검증은_추가분_누락이나_다른_이름을_찾는다()
    {
        GlobalStateTransformResult result = GlobalStateWriter.Transform(Bytes(Original), HomePath, [Addition("db-1")]);
        Assert.NotNull(GlobalStateWriter.Verify(Bytes(Original), Bytes(Original), HomePath, result.Added));
        byte[] renamed = Bytes(Encoding.UTF8.GetString(result.NewBytes).Replace("\"name\":\"새 프로젝트\"", "\"name\":\"다른\"", StringComparison.Ordinal));
        Assert.NotNull(GlobalStateWriter.Verify(Bytes(Original), renamed, HomePath, result.Added));
    }
}
