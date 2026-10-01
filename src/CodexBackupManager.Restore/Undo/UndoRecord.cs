using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBackupManager.Restore.Undo;

/// <summary>(Phase 9_4-01) 이 가져오기가 새로 만든 rollout 파일.</summary>
public sealed record UndoNewRolloutFile(string OwningThreadId, string Path, long Length, string Sha256);

/// <summary>(Phase 9_4-01) 이 가져오기가 뒤에 이어 붙인 rollout 파일(앞부분 = 이전 파일).</summary>
public sealed record UndoAppendedRollout(string ThreadId, string Path, long BeforeLength, string BeforeSha256, long AfterLength, string AfterSha256);

/// <summary>(Phase 9_4-01) 이 가져오기가 INSERT한 <c>threads</c> 행과 그 fingerprint(<see cref="ThreadRowFingerprint"/>).</summary>
public sealed record UndoInsertedThread(string ThreadId, IReadOnlyList<string> FingerprintColumns, string Fingerprint);

/// <summary>(Phase 9_4-01) 컬럼 값 하나(정수 또는 문자열, 둘 다 <c>null</c>이면 NULL).</summary>
public sealed record UndoColumnValue(string Column, string? Text, long? Integer);

/// <summary>
/// (Phase 9_4-01) 이 가져오기가 UPDATE한 기존 <c>threads</c> 행(이어받기의 rollout_path·메타데이터). 이전 값은 가져오기 Snapshot의 DB 사본에서 읽었다.
/// </summary>
/// <param name="ThreadId">thread.</param>
/// <param name="Before">바꾼 컬럼의 이전 값(되돌릴 때 이 값으로 쓴다).</param>
/// <param name="CheckedColumns">되돌리기 전 "이후 값 그대로인지" 볼 컬럼(Desktop이 켜지기만 해도 바꾸는 <c>updated_at</c>·<c>updated_at_ms</c> 제외).</param>
/// <param name="AfterFingerprint"><paramref name="CheckedColumns"/>의 이후 값 fingerprint.</param>
public sealed record UndoUpdatedThread(string ThreadId, IReadOnlyList<UndoColumnValue> Before, IReadOnlyList<string> CheckedColumns, string AfterFingerprint);

/// <summary>(Phase 9_4-01) 이 가져오기의 연결 변경(9_3).</summary>
public sealed record UndoThreadLink(string ThreadId, string? BeforeProjectId, string? BeforeCwd, string AfterProjectId, string AfterCwd);

/// <summary>(Phase 9_4-01) 이 가져오기가 새로 만든 프로젝트(멱등성 키로 재사용한 프로젝트는 넣지 않는다).</summary>
public sealed record UndoCreatedProject(string DbProjectId, string Name, IReadOnlyList<string> RootPaths, string IdempotencyKey);

/// <summary>(Phase 9_4-01) 이 가져오기가 global-state 레거시 저장소에 추가한 항목(9_5a).</summary>
public sealed record UndoGlobalStateEntry(string LegacyProjectId, string DbProjectId, string Name, IReadOnlyList<string> RootPaths, long CreatedAt);

/// <summary>
/// (Phase 9_4-06) 가져오기 직후 Codex 흔적 기준값(대화별). 되돌릴 때 지금 값이 이 값과 다르면 Codex에서 열어 본 것으로 본다.
/// <c>null</c>은 그때 확인하지 못했다는 뜻이다(되돌릴 때 보수적으로 거부).
/// </summary>
public sealed record UndoTraceBaseline(string ThreadId, long? HistoryRows, long? AtomStateMentions, long? SessionIndexLines);

/// <summary>
/// (Phase 9_4-01/08) 성공한 가져오기의 역연산 항목 목록. 가져오기 Snapshot 디렉터리 안의 <c>undo-record.json</c>에 저장하고, journal에는 그 해시만 둔다.
/// 새 쓰기 종류가 생기면 목록 하나와 <see cref="ImportUndoService"/>의 처리만 더한다.
/// </summary>
public sealed record UndoRecord(
    int Version,
    string ImportSnapshotId,
    IReadOnlyList<UndoNewRolloutFile> NewRolloutFiles,
    IReadOnlyList<UndoAppendedRollout> AppendedRollouts,
    IReadOnlyList<UndoInsertedThread> InsertedThreads,
    IReadOnlyList<UndoUpdatedThread> UpdatedThreads,
    IReadOnlyList<UndoThreadLink> ThreadLinks,
    IReadOnlyList<UndoCreatedProject> CreatedProjects,
    IReadOnlyList<UndoGlobalStateEntry> GlobalStateEntries,
    IReadOnlyList<UndoTraceBaseline> TraceBaselines)
{
    /// <summary>현재 형식 버전.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>(Phase 9_4-01) <see cref="UndoRecord"/> 파일 읽기·쓰기(temp + flush + move, 읽을 때 journal의 해시와 비교).</summary>
public static class UndoRecordStore
{
    /// <summary>파일 이름.</summary>
    public const string FileName = "undo-record.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>쓰고 그 바이트의 SHA-256(소문자)을 돌려준다.</summary>
    public static string Write(string snapshotDirectory, UndoRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(record);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions));
        string path = Path.Combine(snapshotDirectory, FileName);
        string temp = path + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>읽는다. 없거나, 해시가 <paramref name="expectedSha256"/>와 다르거나, 해석할 수 없으면 <c>null</c>.</summary>
    public static UndoRecord? TryRead(string snapshotDirectory, string expectedSha256)
    {
        string path = Path.Combine(snapshotDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            UndoRecord? record = JsonSerializer.Deserialize<UndoRecord>(bytes, JsonOptions);
            return record is { Version: UndoRecord.CurrentVersion, NewRolloutFiles: not null, AppendedRollouts: not null, InsertedThreads: not null,
                UpdatedThreads: not null, ThreadLinks: not null, CreatedProjects: not null, GlobalStateEntries: not null, TraceBaselines: not null }
                ? record
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
