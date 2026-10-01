using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Restore;

/// <summary>(Phase 9_5a-02) 레거시 저장소에 추가할 DB 프로젝트 하나.</summary>
/// <param name="DbProjectId"><c>projects.id</c>.</param>
/// <param name="Name">이름(DB 값).</param>
/// <param name="RootPaths">루트 표시 경로(DB <c>project_roots</c> position 순).</param>
/// <param name="TimestampMs"><c>createdAt</c>·<c>updatedAt</c>에 쓸 ms(Apply는 DB 트랜잭션과 같은 nowMs, 보정은 DB <c>created_at_ms</c>).</param>
public sealed record GlobalStateProjectAddition(string DbProjectId, string Name, IReadOnlyList<string> RootPaths, long TimestampMs);

/// <summary>추가된 레거시 항목 하나.</summary>
/// <param name="DbProjectId">DB 프로젝트 ID.</param>
/// <param name="LegacyProjectId">새 레거시 ID(UUIDv4, 소문자 하이픈).</param>
/// <param name="Addition">입력 값.</param>
public sealed record GlobalStateAddedProject(string DbProjectId, string LegacyProjectId, GlobalStateProjectAddition Addition);

/// <summary>순수 변환 결과.</summary>
/// <param name="NewBytes">새 파일 바이트. 추가할 것이 없으면 원본과 같다.</param>
/// <param name="Added">실제로 추가한 항목(이미 매핑에 있는 DB ID는 빠진다 — 멱등).</param>
public sealed record GlobalStateTransformResult(byte[] NewBytes, IReadOnlyList<GlobalStateAddedProject> Added)
{
    /// <summary>바꿀 것이 없는지.</summary>
    public bool IsNoOp => Added.Count == 0;
}

/// <summary>실제 쓰기 결과(사후 검증에 쓴다).</summary>
/// <param name="OriginalBytes">쓰기 직전 원본 바이트.</param>
/// <param name="NewBytes">쓴 바이트(no-op이면 원본과 같고 파일은 그대로다).</param>
/// <param name="Added">추가한 항목.</param>
public sealed record GlobalStateWriteResult(byte[] OriginalBytes, byte[] NewBytes, IReadOnlyList<GlobalStateAddedProject> Added);

/// <summary>
/// (Phase 9_5a-02) Codex Desktop 레거시 프로젝트 저장소에 이 앱이 만든 DB 프로젝트를 추가한다 — 사이드바 프로젝트 목록은 이 저장소에서 온다
/// (9_0-B 실측). <b>쓰기는 <see cref="RestoreExecutor"/>와 <see cref="SidebarRepairService"/> 파이프라인 안에서만</b> 부른다(Snapshot·Journal·Rollback).
/// </summary>
/// <remarks>
/// <para>바꾸는 곳은 세 곳뿐이다. 다른 키·값은 원문 조각 그대로 쓴다(<see cref="GlobalStateJson.Serialize(GlobalStateJsonNode,string?)"/>).</para>
/// <list type="number">
///   <item><c>local-projects</c> 끝에 <c>{"id":L,"name":…,"rootPaths":[…],"createdAt":ms,"updatedAt":ms}</c></item>
///   <item><c>project-order</c> 끝에 L(사이드바 맨 아래 — DB position = MAX+1과 맞다)</item>
///   <item>레거시 ID 매핑의 현재 host 객체 끝에 <c>L: DB ID</c></item>
/// </list>
/// L은 새 UUIDv4(<see cref="Guid.NewGuid"/> 소문자 "D")다. 같은 DB ID가 이미 매핑에 있으면 추가하지 않는다(멱등).
/// <c>thread-project-assignments</c> 등 다른 키는 쓰지 않는다. 게이트(<see cref="GlobalStateProjectGate"/>)를 통과하지 못한 파일에는 쓰지 않는다.
/// </remarks>
public static class GlobalStateWriter
{
    /// <summary>쓰기용 temp 접미사(rollout 쓰기와 같은 규칙, Rollback이 이 이름만 정리한다).</summary>
    public const string TempSuffix = ".cbm-restore-tmp";

    /// <summary>Snapshot 라벨.</summary>
    public const string SnapshotLabel = "global-state";

    /// <summary>
    /// 순수 변환. 원본이 게이트를 통과하지 않으면 <see cref="InvalidOperationException"/>. 결과 바이트도 게이트를 다시 통과해야 한다.
    /// </summary>
    /// <param name="originalBytes">원본 파일 바이트.</param>
    /// <param name="codexHome">현재 Codex Home(host 키 선택).</param>
    /// <param name="additions">추가할 프로젝트(이 순서로 끝에 붙인다).</param>
    /// <param name="newLegacyId">레거시 ID 생성기(테스트 고정용). 기본은 <see cref="Guid.NewGuid"/> 소문자 "D".</param>
    public static GlobalStateTransformResult Transform(
        byte[] originalBytes,
        CanonicalPath codexHome,
        IReadOnlyList<GlobalStateProjectAddition> additions,
        Func<string>? newLegacyId = null)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(codexHome);
        ArgumentNullException.ThrowIfNull(additions);
        newLegacyId ??= static () => Guid.NewGuid().ToString("D");

        GlobalStateGateFailure failure = GlobalStateProjectGate.Evaluate(
            originalBytes, codexHome, out string? hostKey, out string? text, out GlobalStateJsonObject? root);
        if (failure != GlobalStateGateFailure.None)
        {
            throw new InvalidOperationException($"데스크톱 앱 상태 파일이 확인한 형태와 달라 쓰지 않습니다({failure}).");
        }

        var localProjects = (GlobalStateJsonObject)root!.Get(GlobalStateProjectGate.LocalProjectsKey)!;
        var order = (GlobalStateJsonArray)root.Get(GlobalStateProjectGate.ProjectOrderKey)!;
        var mappingByHost = (GlobalStateJsonObject)root.Get(Codex.Inspection.GlobalStateReader.LegacyProjectIdMappingKey)!;
        var mapping = (GlobalStateJsonObject)mappingByHost.Get(hostKey!)!;

        var mappedDbIds = new HashSet<string>(
            mapping.Members.Select(m => ((GlobalStateJsonString)m.Value).Value), StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        usedIds.UnionWith(localProjects.Members.Select(m => m.Key));
        usedIds.UnionWith(mapping.Members.Select(m => m.Key));
        usedIds.UnionWith(order.Items.Select(i => ((GlobalStateJsonString)i).Value));

        var added = new List<GlobalStateAddedProject>();
        foreach (GlobalStateProjectAddition addition in additions)
        {
            ValidateAddition(addition);
            if (!mappedDbIds.Add(addition.DbProjectId))
            {
                continue; // 이미 사이드바에 연결된 DB 프로젝트(또는 같은 목록 안 중복) — 멱등
            }

            string legacyId = NewUniqueLegacyId(newLegacyId, usedIds);
            var entry = new GlobalStateJsonObject();
            entry.Members.Add(new("id", new GlobalStateJsonString(legacyId)));
            entry.Members.Add(new("name", new GlobalStateJsonString(addition.Name)));
            var roots = new GlobalStateJsonArray();
            roots.Items.AddRange(addition.RootPaths.Select(r => (GlobalStateJsonNode)new GlobalStateJsonString(r)));
            entry.Members.Add(new("rootPaths", roots));
            entry.Members.Add(new("createdAt", GlobalStateJsonNumber.FromInteger(addition.TimestampMs)));
            entry.Members.Add(new("updatedAt", GlobalStateJsonNumber.FromInteger(addition.TimestampMs)));

            localProjects.Members.Add(new(legacyId, entry));
            order.Items.Add(new GlobalStateJsonString(legacyId));
            mapping.Members.Add(new(legacyId, new GlobalStateJsonString(addition.DbProjectId)));
            added.Add(new GlobalStateAddedProject(addition.DbProjectId, legacyId, addition));
        }

        if (added.Count == 0)
        {
            return new GlobalStateTransformResult(originalBytes, added);
        }

        foreach (GlobalStateJsonNode node in new GlobalStateJsonNode[] { localProjects, order, mapping, mappingByHost, root })
        {
            node.MarkModified();
        }

        byte[] newBytes = GlobalStateJson.StrictUtf8.GetBytes(GlobalStateJson.Serialize(root, text));
        if (!GlobalStateProjectGate.CheckBytes(newBytes, codexHome).IsSupported)
        {
            throw new InvalidOperationException("변환한 데스크톱 앱 상태 파일이 형식 확인을 통과하지 못했습니다.");
        }

        return new GlobalStateTransformResult(newBytes, added);
    }

    /// <summary>
    /// 파일을 다시 읽어(해시가 <paramref name="expectedOriginalSha256"/>와 같아야 한다) 변환하고, 같은 폴더의 temp에 쓰고 flush한 뒤
    /// 검증하고 원자적으로 교체한다. 추가할 것이 없으면 파일을 건드리지 않는다.
    /// </summary>
    /// <exception cref="InvalidOperationException">계획 뒤 파일이 바뀌었거나, 게이트 실패, temp 검증 실패.</exception>
    public static GlobalStateWriteResult Apply(
        string globalStatePath,
        CanonicalPath codexHome,
        string expectedOriginalSha256,
        IReadOnlyList<GlobalStateProjectAddition> additions,
        IRestoreFaultInjectionHook? faultInjection = null,
        Func<string>? newLegacyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalStatePath);
        ArgumentNullException.ThrowIfNull(codexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOriginalSha256);
        ArgumentNullException.ThrowIfNull(additions);
        faultInjection ??= NoOpRestoreFaultInjectionHook.Instance;

        byte[] original = File.ReadAllBytes(globalStatePath);
        if (!string.Equals(Sha256(original), expectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("데스크톱 앱 상태 파일이 계획 이후 바뀌었습니다.");
        }

        GlobalStateTransformResult transform = Transform(original, codexHome, additions, newLegacyId);
        if (transform.IsNoOp)
        {
            return new GlobalStateWriteResult(original, original, transform.Added);
        }

        string tempPath = globalStatePath + TempSuffix;
        try
        {
            // FileMode.Create: 이전 시도가 크래시로 남긴 temp는 순수 작업용이라 덮어써도 된다(rollout 쓰기와 같은 규칙).
            using (FileStream temp = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                temp.Write(transform.NewBytes, 0, transform.NewBytes.Length);
                faultInjection.Check(RestoreFaultInjectionPoint.DuringGlobalStateTempWrite);
                temp.Flush(flushToDisk: true);
            }

            if (!File.ReadAllBytes(tempPath).AsSpan().SequenceEqual(transform.NewBytes))
            {
                throw new InvalidOperationException("데스크톱 앱 상태 파일 temp 검증에 실패했습니다.");
            }

            File.Move(tempPath, globalStatePath, overwrite: true);
            faultInjection.Check(RestoreFaultInjectionPoint.AfterGlobalStateReplace);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return new GlobalStateWriteResult(original, transform.NewBytes, transform.Added);
    }

    /// <summary>
    /// 파일 수준 사후 검증. 지금 파일이 게이트를 통과하고, 바꾼 세 키를 뺀 모든 최상위 키가 원본과 바이트 조각까지 같고,
    /// 세 키에는 원본 항목이 그대로(같은 순서·같은 원문) 있고 그 뒤에 추가분만 정확히 있는지 본다. 문제가 없으면 <c>null</c>.
    /// </summary>
    public static string? Verify(byte[] originalBytes, byte[] currentBytes, CanonicalPath codexHome, IReadOnlyList<GlobalStateAddedProject> added)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(currentBytes);
        ArgumentNullException.ThrowIfNull(codexHome);
        ArgumentNullException.ThrowIfNull(added);

        if (added.Count == 0)
        {
            return currentBytes.AsSpan().SequenceEqual(originalBytes) ? null : "바꿀 것이 없었는데 데스크톱 앱 상태 파일이 바뀌었습니다.";
        }

        if (GlobalStateProjectGate.Evaluate(originalBytes, codexHome, out string? hostBefore, out string? textBefore, out GlobalStateJsonObject? before)
            != GlobalStateGateFailure.None)
        {
            return "원본 데스크톱 앱 상태 파일을 다시 해석할 수 없습니다.";
        }

        GlobalStateGateFailure failure = GlobalStateProjectGate.Evaluate(
            currentBytes, codexHome, out string? hostAfter, out string? textAfter, out GlobalStateJsonObject? after);
        if (failure != GlobalStateGateFailure.None)
        {
            return $"쓴 데스크톱 앱 상태 파일이 형식 확인을 통과하지 못했습니다({failure}).";
        }

        if (!string.Equals(hostBefore, hostAfter, StringComparison.Ordinal))
        {
            return "매핑 host 키가 바뀌었습니다.";
        }

        string mappingKey = Codex.Inspection.GlobalStateReader.LegacyProjectIdMappingKey;
        var changedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            GlobalStateProjectGate.LocalProjectsKey, GlobalStateProjectGate.ProjectOrderKey, mappingKey,
        };

        if (!before!.Members.Select(m => m.Key).SequenceEqual(after!.Members.Select(m => m.Key), StringComparer.Ordinal))
        {
            return "최상위 키 구성이나 순서가 바뀌었습니다.";
        }

        foreach (KeyValuePair<string, GlobalStateJsonNode> member in before.Members)
        {
            if (!changedKeys.Contains(member.Key) && !SameRaw(textBefore!, member.Value, textAfter!, after.Get(member.Key)!))
            {
                return "바꾸지 않아야 할 키가 바뀌었습니다.";
            }
        }

        // local-projects: 원본 항목 그대로 + 끝에 추가분.
        var lpBefore = (GlobalStateJsonObject)before.Get(GlobalStateProjectGate.LocalProjectsKey)!;
        var lpAfter = (GlobalStateJsonObject)after.Get(GlobalStateProjectGate.LocalProjectsKey)!;
        if (lpAfter.Members.Count != lpBefore.Members.Count + added.Count ||
            !PrefixSame(textBefore!, lpBefore.Members, textAfter!, lpAfter.Members))
        {
            return "local-projects에 추가분 외 변경이 있습니다.";
        }

        var orderBefore = (GlobalStateJsonArray)before.Get(GlobalStateProjectGate.ProjectOrderKey)!;
        var orderAfter = (GlobalStateJsonArray)after.Get(GlobalStateProjectGate.ProjectOrderKey)!;
        if (orderAfter.Items.Count != orderBefore.Items.Count + added.Count ||
            !orderBefore.Items.Select((n, i) => SameRaw(textBefore!, n, textAfter!, orderAfter.Items[i])).All(x => x))
        {
            return "project-order에 추가분 외 변경이 있습니다.";
        }

        var mapBefore = (GlobalStateJsonObject)before.Get(mappingKey)!;
        var mapAfter = (GlobalStateJsonObject)after.Get(mappingKey)!;
        if (!mapBefore.Members.Select(m => m.Key).SequenceEqual(mapAfter.Members.Select(m => m.Key), StringComparer.Ordinal))
        {
            return "매핑 host 구성이 바뀌었습니다.";
        }

        foreach (KeyValuePair<string, GlobalStateJsonNode> host in mapBefore.Members)
        {
            if (!string.Equals(host.Key, hostBefore, StringComparison.Ordinal) &&
                !SameRaw(textBefore!, host.Value, textAfter!, mapAfter.Get(host.Key)!))
            {
                return "다른 host의 매핑이 바뀌었습니다.";
            }
        }

        var hostMapBefore = (GlobalStateJsonObject)mapBefore.Get(hostBefore!)!;
        var hostMapAfter = (GlobalStateJsonObject)mapAfter.Get(hostAfter!)!;
        if (hostMapAfter.Members.Count != hostMapBefore.Members.Count + added.Count ||
            !PrefixSame(textBefore!, hostMapBefore.Members, textAfter!, hostMapAfter.Members))
        {
            return "현재 host 매핑에 추가분 외 변경이 있습니다.";
        }

        for (int i = 0; i < added.Count; i++)
        {
            GlobalStateAddedProject expected = added[i];
            KeyValuePair<string, GlobalStateJsonNode> lp = lpAfter.Members[lpBefore.Members.Count + i];
            KeyValuePair<string, GlobalStateJsonNode> map = hostMapAfter.Members[hostMapBefore.Members.Count + i];
            var orderItem = (GlobalStateJsonString)orderAfter.Items[orderBefore.Items.Count + i];

            if (!string.Equals(lp.Key, expected.LegacyProjectId, StringComparison.Ordinal) ||
                lp.Value is not GlobalStateJsonObject entry ||
                !LocalProjectEntryEquals(entry, expected))
            {
                return "새 local-projects 항목이 예상과 다릅니다.";
            }

            if (!string.Equals(orderItem.Value, expected.LegacyProjectId, StringComparison.Ordinal))
            {
                return "새 project-order 항목이 예상과 다릅니다.";
            }

            if (!string.Equals(map.Key, expected.LegacyProjectId, StringComparison.Ordinal) ||
                map.Value is not GlobalStateJsonString mapped ||
                !string.Equals(mapped.Value, expected.DbProjectId, StringComparison.Ordinal))
            {
                return "새 매핑 항목이 예상과 다릅니다.";
            }
        }

        return null;
    }

    /// <summary>SHA-256(소문자 hex).</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool LocalProjectEntryEquals(GlobalStateJsonObject entry, GlobalStateAddedProject expected)
    {
        string[] keys = entry.Members.Select(m => m.Key).ToArray();
        if (!keys.SequenceEqual(GlobalStateProjectGate.LocalProjectFields, StringComparer.Ordinal))
        {
            return false;
        }

        string ms = expected.Addition.TimestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return entry.Get("id") is GlobalStateJsonString id && id.Value == expected.LegacyProjectId
               && entry.Get("name") is GlobalStateJsonString name && string.Equals(name.Value, expected.Addition.Name, StringComparison.Ordinal)
               && entry.Get("rootPaths") is GlobalStateJsonArray roots
               && roots.Items.Select(r => ((GlobalStateJsonString)r).Value).SequenceEqual(expected.Addition.RootPaths, StringComparer.Ordinal)
               && entry.Get("createdAt") is GlobalStateJsonNumber created && created.Raw == ms
               && entry.Get("updatedAt") is GlobalStateJsonNumber updated && updated.Raw == ms;
    }

    private static bool PrefixSame(
        string textBefore, List<KeyValuePair<string, GlobalStateJsonNode>> before,
        string textAfter, List<KeyValuePair<string, GlobalStateJsonNode>> after)
    {
        for (int i = 0; i < before.Count; i++)
        {
            if (!string.Equals(before[i].Key, after[i].Key, StringComparison.Ordinal) ||
                !SameRaw(textBefore, before[i].Value, textAfter, after[i].Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRaw(string textA, GlobalStateJsonNode a, string textB, GlobalStateJsonNode b)
        => a.SourceEnd - a.SourceStart == b.SourceEnd - b.SourceStart &&
           string.CompareOrdinal(textA, a.SourceStart, textB, b.SourceStart, a.SourceEnd - a.SourceStart) == 0;

    private static void ValidateAddition(GlobalStateProjectAddition addition)
    {
        ArgumentNullException.ThrowIfNull(addition);
        if (string.IsNullOrWhiteSpace(addition.DbProjectId) || string.IsNullOrWhiteSpace(addition.Name) ||
            addition.RootPaths.Count == 0 || addition.RootPaths.Any(string.IsNullOrWhiteSpace) || addition.TimestampMs <= 0)
        {
            throw new InvalidOperationException("레거시 프로젝트 항목에 쓸 값이 비어 있습니다.");
        }
    }

    private static string NewUniqueLegacyId(Func<string> newLegacyId, HashSet<string> usedIds)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string id = newLegacyId();
            if (!string.IsNullOrWhiteSpace(id) && usedIds.Add(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("겹치지 않는 레거시 프로젝트 ID를 만들 수 없습니다.");
    }
}
