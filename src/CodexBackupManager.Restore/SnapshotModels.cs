using System;
using System.Collections.Generic;

namespace CodexBackupManager.Restore;

/// <summary>Snapshot에 담긴 파일 하나(요구사항 7).</summary>
/// <param name="RelativeLabel">
/// Codex Home 기준 상대 라벨(로그/보고에 안전하게 쓸 수 있는 식별자 — 실제 절대경로가 아니다).
/// </param>
/// <param name="OriginalAbsolutePath">복원 시 실제로 되돌려 쓸 원본 절대경로.</param>
/// <param name="SnapshotFileName">snapshot 디렉터리 안에 저장된 파일명(충돌 방지를 위해 라벨과 다를 수 있다).</param>
/// <param name="ExistedBefore">
/// Snapshot 시점에 이 파일이 이미 존재했는지. <c>false</c>면 이번 Restore가 새로 만들 파일이라는
/// 뜻이고, Rollback 시 원본 대신 <b>삭제</b>해야 한다.
/// </param>
/// <param name="ByteLength">원본 파일 길이(<see cref="ExistedBefore"/>가 <c>true</c>일 때만 의미 있음).</param>
/// <param name="Sha256Hex">원본 파일의 SHA-256(<see cref="ExistedBefore"/>가 <c>true</c>일 때만).</param>
public sealed record SnapshotFileEntry(
    string RelativeLabel,
    string OriginalAbsolutePath,
    string SnapshotFileName,
    bool ExistedBefore,
    long ByteLength,
    string? Sha256Hex);

/// <summary>Snapshot 전체의 manifest(요구사항 7).</summary>
/// <param name="SnapshotId">이 snapshot의 식별자(디렉터리 이름과 같다).</param>
/// <param name="CreatedAtUtc">생성 시각.</param>
/// <param name="CodexHomePath">대상 Codex Home 경로(진단용 — 원문 그대로, 로그에는 별도 redact 적용).</param>
/// <param name="ImportPlanBackupSha256">
/// 이 snapshot이 어떤 <c>ImportPlan</c>의 Apply를 위한 것인지(<c>plan.Backup.BackupFileSha256</c>).
/// </param>
/// <param name="Files">snapshot에 포함된 파일 목록.</param>
public sealed record SnapshotManifest(
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    string CodexHomePath,
    string ImportPlanBackupSha256,
    IReadOnlyList<SnapshotFileEntry> Files);

/// <summary>Snapshot 생성 결과.</summary>
/// <param name="Success">성공 여부.</param>
/// <param name="SnapshotDirectory">성공했으면 snapshot 디렉터리 전체 경로.</param>
/// <param name="Manifest">성공했으면 manifest.</param>
/// <param name="FailureReason">실패했으면 이유(원문 경로/내용 없음).</param>
public sealed record SnapshotCreateResult(
    bool Success,
    string? SnapshotDirectory,
    SnapshotManifest? Manifest,
    string? FailureReason);
