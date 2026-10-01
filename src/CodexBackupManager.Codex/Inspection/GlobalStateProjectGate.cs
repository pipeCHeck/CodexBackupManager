using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>(Phase 9_5a-01) <see cref="GlobalStateProjectGate"/> 실패 사유. 사용자 데이터는 담지 않는다.</summary>
public enum GlobalStateGateFailure
{
    /// <summary>통과.</summary>
    None = 0,

    /// <summary>파일이 없다.</summary>
    FileMissing,

    /// <summary>파일이 상한(<see cref="GlobalStateReader.MaxFileSizeBytes"/>)보다 크다.</summary>
    TooLarge,

    /// <summary>파일을 읽을 수 없다(잠김·권한).</summary>
    Unreadable,

    /// <summary>UTF-8 BOM으로 시작한다(Desktop은 BOM 없이 쓴다).</summary>
    ByteOrderMark,

    /// <summary>올바른 UTF-8이 아니다.</summary>
    InvalidUtf8,

    /// <summary>JSON 문법 오류 또는 중복 키.</summary>
    InvalidJson,

    /// <summary>최상위가 객체가 아니다.</summary>
    NotObject,

    /// <summary>JS <c>JSON.stringify</c> 형식으로 다시 쓰면 원래 바이트와 다르다(예: 들여쓰기, 다른 이스케이프).</summary>
    RoundTripMismatch,

    /// <summary><c>local-projects</c>가 없거나 객체가 아니다.</summary>
    LocalProjectsInvalid,

    /// <summary><c>local-projects</c> 항목 중 확인한 5필드 형태가 아닌 것이 있다(필드 추가·누락·타입, id ≠ 키).</summary>
    LocalProjectEntryInvalid,

    /// <summary><c>project-order</c>가 없거나 문자열 배열이 아니다.</summary>
    ProjectOrderInvalid,

    /// <summary>레거시 ID 매핑(<see cref="GlobalStateReader.LegacyProjectIdMappingKey"/>)이 없거나 형태가 다르다.</summary>
    MappingInvalid,

    /// <summary>매핑에 현재 Codex Home과 맞는 host 키가 없다.</summary>
    HostKeyMissing,

    /// <summary>현재 Codex Home과 맞는 host 키가 둘 이상이다.</summary>
    HostKeyAmbiguous,

    /// <summary>마이그레이션 상태(<see cref="GlobalStateReader.ProjectsMigrationKey"/>)에 그 host가 없거나 형태가 다르다.</summary>
    MigrationStateInvalid,

    /// <summary>그 host의 <c>projectsMigrated</c>가 <c>true</c>가 아니다.</summary>
    ProjectsNotMigrated,
}

/// <summary>
/// (Phase 9_5a-01) 이 앱이 Codex Desktop 레거시 프로젝트 저장소(<c>local-projects</c>·<c>project-order</c>·레거시 ID 매핑)에
/// 기록해도 되는 <c>.codex-global-state.json</c>인지 판정한다. <b>읽기 전용</b>이다.
/// </summary>
/// <remarks>
/// <para>모두 맞을 때만 통과한다(docs/import-ux-redesign-phase9.md §6 "9_0-B 결과", 2026-10-01 실측):</para>
/// <list type="number">
///   <item>파일이 있고 UTF-8(BOM 없음) JSON 객체다(중복 키 없음).</item>
///   <item>왕복 바이트 동일: <see cref="GlobalStateJson"/>으로 전체를 다시 쓰면 원래 바이트와 정확히 같다.</item>
///   <item><c>local-projects</c>가 객체이고, 모든 값이 <c>{id, name, rootPaths, createdAt, updatedAt}</c> 5필드 객체다
///     (<c>id</c> = 키, name 문자열, rootPaths 문자열 배열, 시각 정수).</item>
///   <item><c>project-order</c>가 문자열 배열이다.</item>
///   <item>매핑 객체에 현재 Codex Home과 맞는 host 키(<see cref="GlobalStateReader.HostKeyMatches"/>)가 정확히 하나 있고 그 값이
///     문자열 값만 가진 객체다. 마이그레이션 상태에도 그 host가 정확히 하나 있고 <c>projectsMigrated == true</c>다.</item>
/// </list>
/// 실패하면 새 프로젝트를 만들지 않는다(사이드바에 보이지 않는 프로젝트를 만들지 않는다, CLAUDE.md §33).
/// </remarks>
public static class GlobalStateProjectGate
{
    /// <summary>레거시 프로젝트 객체 키.</summary>
    public const string LocalProjectsKey = "local-projects";

    /// <summary>사이드바 순서 키.</summary>
    public const string ProjectOrderKey = "project-order";

    /// <summary>레거시 프로젝트 항목의 필드(실측 순서).</summary>
    public static readonly IReadOnlyList<string> LocalProjectFields = ["id", "name", "rootPaths", "createdAt", "updatedAt"];

    /// <summary>판정 결과.</summary>
    /// <param name="Failure">실패 사유(<see cref="GlobalStateGateFailure.None"/>이면 통과).</param>
    /// <param name="HostKey">통과했으면 매핑의 host 키(원문).</param>
    /// <param name="OriginalBytes">읽은 바이트(파일을 읽었으면).</param>
    /// <param name="Sha256Hex">읽은 바이트의 SHA-256(소문자, 파일을 읽었으면).</param>
    public sealed record Result(GlobalStateGateFailure Failure, string? HostKey, byte[]? OriginalBytes, string? Sha256Hex)
    {
        /// <summary>통과했는지.</summary>
        public bool IsSupported => Failure == GlobalStateGateFailure.None;
    }

    /// <summary>파일을 읽기 전용(공유 읽기)으로 열어 판정한다.</summary>
    public static Result Check(string globalStateFilePath, CanonicalPath codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalStateFilePath);
        ArgumentNullException.ThrowIfNull(codexHome);

        byte[] bytes;
        try
        {
            var file = new FileInfo(globalStateFilePath);
            if (!file.Exists)
            {
                return new Result(GlobalStateGateFailure.FileMissing, null, null, null);
            }

            if (file.Length > GlobalStateReader.MaxFileSizeBytes)
            {
                return new Result(GlobalStateGateFailure.TooLarge, null, null, null);
            }

            using FileStream stream = new(globalStateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(GlobalStateGateFailure.Unreadable, null, null, null);
        }

        return CheckBytes(bytes, codexHome);
    }

    /// <summary>이미 읽은 바이트로 판정한다(파일을 다시 열지 않는다).</summary>
    public static Result CheckBytes(byte[] bytes, CanonicalPath codexHome)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(codexHome);
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        GlobalStateGateFailure failure = Evaluate(bytes, codexHome, out string? hostKey, out _, out _);
        return new Result(failure, failure == GlobalStateGateFailure.None ? hostKey : null, bytes, sha);
    }

    /// <summary>
    /// 판정하고, 통과하면 파싱한 문서(원문 텍스트와 루트 객체)를 돌려준다. 쓰기 쪽(<c>GlobalStateWriter</c>)이 같은 규칙으로 문서를 얻을 때 쓴다.
    /// </summary>
    public static GlobalStateGateFailure Evaluate(
        byte[] bytes, CanonicalPath codexHome, out string? hostKey, out string? text, out GlobalStateJsonObject? root)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(codexHome);
        hostKey = null;
        text = null;
        root = null;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return GlobalStateGateFailure.ByteOrderMark;
        }

        string decoded;
        try
        {
            decoded = GlobalStateJson.StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return GlobalStateGateFailure.InvalidUtf8;
        }

        GlobalStateJsonNode parsed;
        try
        {
            parsed = GlobalStateJson.Parse(decoded);
        }
        catch (FormatException)
        {
            return GlobalStateGateFailure.InvalidJson;
        }

        if (parsed is not GlobalStateJsonObject obj)
        {
            return GlobalStateGateFailure.NotObject;
        }

        // 왕복: 원문 조각을 쓰지 않고 전체를 JS 규칙으로 다시 쓴 결과가 원래 바이트와 같아야 한다.
        byte[] reserialized;
        try
        {
            reserialized = GlobalStateJson.StrictUtf8.GetBytes(GlobalStateJson.Serialize(obj));
        }
        catch (ArgumentException)
        {
            return GlobalStateGateFailure.RoundTripMismatch;
        }

        if (!reserialized.AsSpan().SequenceEqual(bytes))
        {
            return GlobalStateGateFailure.RoundTripMismatch;
        }

        GlobalStateGateFailure structure = CheckStructure(obj, codexHome, out hostKey);
        if (structure != GlobalStateGateFailure.None)
        {
            hostKey = null;
            return structure;
        }

        text = decoded;
        root = obj;
        return GlobalStateGateFailure.None;
    }

    private static GlobalStateGateFailure CheckStructure(GlobalStateJsonObject root, CanonicalPath codexHome, out string? hostKey)
    {
        hostKey = null;

        if (root.Get(LocalProjectsKey) is not GlobalStateJsonObject localProjects)
        {
            return GlobalStateGateFailure.LocalProjectsInvalid;
        }

        foreach (KeyValuePair<string, GlobalStateJsonNode> entry in localProjects.Members)
        {
            if (!IsLocalProjectEntry(entry.Key, entry.Value))
            {
                return GlobalStateGateFailure.LocalProjectEntryInvalid;
            }
        }

        if (root.Get(ProjectOrderKey) is not GlobalStateJsonArray order || !order.Items.TrueForAll(i => i is GlobalStateJsonString))
        {
            return GlobalStateGateFailure.ProjectOrderInvalid;
        }

        if (root.Get(GlobalStateReader.LegacyProjectIdMappingKey) is not GlobalStateJsonObject mappingByHost)
        {
            return GlobalStateGateFailure.MappingInvalid;
        }

        GlobalStateGateFailure hostResult = FindSingleHost(mappingByHost, codexHome, out KeyValuePair<string, GlobalStateJsonNode> host);
        if (hostResult != GlobalStateGateFailure.None)
        {
            return hostResult;
        }

        if (host.Value is not GlobalStateJsonObject mapping ||
            !mapping.Members.TrueForAll(m => m.Key.Length > 0 && m.Value is GlobalStateJsonString { Value.Length: > 0 }))
        {
            return GlobalStateGateFailure.MappingInvalid;
        }

        if (root.Get(GlobalStateReader.ProjectsMigrationKey) is not GlobalStateJsonObject migrationByHost)
        {
            return GlobalStateGateFailure.MigrationStateInvalid;
        }

        GlobalStateGateFailure migrationHost = FindSingleHost(migrationByHost, codexHome, out KeyValuePair<string, GlobalStateJsonNode> migration);
        if (migrationHost != GlobalStateGateFailure.None)
        {
            return migrationHost == GlobalStateGateFailure.HostKeyMissing ? GlobalStateGateFailure.MigrationStateInvalid : migrationHost;
        }

        if (migration.Value is not GlobalStateJsonObject migrationState)
        {
            return GlobalStateGateFailure.MigrationStateInvalid;
        }

        if (migrationState.Get("projectsMigrated") is not GlobalStateJsonLiteral { IsTrue: true })
        {
            return GlobalStateGateFailure.ProjectsNotMigrated;
        }

        hostKey = host.Key;
        return GlobalStateGateFailure.None;
    }

    private static GlobalStateGateFailure FindSingleHost(
        GlobalStateJsonObject byHost, CanonicalPath codexHome, out KeyValuePair<string, GlobalStateJsonNode> found)
    {
        found = default;
        int count = 0;
        foreach (KeyValuePair<string, GlobalStateJsonNode> member in byHost.Members)
        {
            if (member.Key.StartsWith("local:", StringComparison.Ordinal) && GlobalStateReader.HostKeyMatches(member.Key, codexHome))
            {
                found = member;
                count++;
            }
        }

        return count switch
        {
            0 => GlobalStateGateFailure.HostKeyMissing,
            1 => GlobalStateGateFailure.None,
            _ => GlobalStateGateFailure.HostKeyAmbiguous,
        };
    }

    private static bool IsLocalProjectEntry(string key, GlobalStateJsonNode value)
    {
        if (value is not GlobalStateJsonObject entry || entry.Members.Count != LocalProjectFields.Count)
        {
            return false;
        }

        foreach (string field in LocalProjectFields)
        {
            if (!entry.Contains(field))
            {
                return false;
            }
        }

        return entry.Get("id") is GlobalStateJsonString id && string.Equals(id.Value, key, StringComparison.Ordinal)
               && entry.Get("name") is GlobalStateJsonString
               && entry.Get("rootPaths") is GlobalStateJsonArray roots && roots.Items.TrueForAll(r => r is GlobalStateJsonString)
               && entry.Get("createdAt") is GlobalStateJsonNumber { IsInteger: true }
               && entry.Get("updatedAt") is GlobalStateJsonNumber { IsInteger: true };
    }
}
