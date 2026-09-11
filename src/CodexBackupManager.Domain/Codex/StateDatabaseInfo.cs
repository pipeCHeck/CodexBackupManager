using System;

namespace CodexBackupManager.Domain.Codex;

/// <summary>
/// Codex Home에서 발견된 <c>state_*.sqlite</c> 파일 하나에 대한 정보.
/// </summary>
/// <remarks>
/// Phase 0 실측: 이 PC에는 <c>state_5.sqlite</c>(33.1 MB) + <c>-wal</c>(4.1 MB) + <c>-shm</c>이 존재했고,
/// <c>sqlite\</c> 하위에 구버전 세트가 별도로 남아 있었다.
/// 파일명은 절대 하드코딩하지 않고 <c>state_*.sqlite</c> 패턴으로 찾은 뒤 generation을 파싱한다.
/// </remarks>
/// <param name="FileName">파일명만 (경로 제외).</param>
/// <param name="Generation">
/// <c>state_&lt;N&gt;.sqlite</c>의 N. 패턴에 맞지 않으면 <c>null</c>.
/// </param>
/// <param name="SizeBytes">파일 크기.</param>
/// <param name="LastWriteTimeUtc">마지막 수정 시각(UTC).</param>
/// <param name="HasWriteAheadLog"><c>-wal</c> 동반 파일 존재 여부. 존재하면 Codex가 최근에 열었다는 신호.</param>
/// <param name="HasSharedMemory"><c>-shm</c> 동반 파일 존재 여부.</param>
public sealed record StateDatabaseInfo(
    string FileName,
    int? Generation,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    bool HasWriteAheadLog,
    bool HasSharedMemory);
