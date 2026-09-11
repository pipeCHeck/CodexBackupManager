# Codex Backup Manager

OpenAI Codex의 로컬 프로젝트/대화 데이터를 조회 · 선택 · 내보내기 · 불러오기 · 복원하는 **Windows 데스크톱 프로그램**.

> **현재 상태: Phase 1 — Codex 탐색까지만 구현되어 있습니다.**
> 대화 목록 / 뷰어 / Export / Import / Restore는 아직 없습니다.

---

## 이 프로그램의 최우선 원칙

> 사용자의 기존 Codex 데이터를 **절대 잃지 않으면서** 프로젝트와 대화를 안전하게 이동시킨다.

- 조회 기능은 전부 **Read-Only**. Codex Home 아래에 어떤 파일도 만들거나 바꾸거나 지우지 않는다.
- SQLite는 `Mode=ReadOnly` + `Pooling=False`로만 연다. `INSERT`/`UPDATE`/`DELETE`/`CREATE`/쓰기 PRAGMA 없음.
- 프로그램 설정과 로그는 `%APPDATA%\CodexBackupManager\`에만 둔다.
- 확인되지 않은 값을 추측해서 채우지 않는다. 모르면 "확인 불가"로 표시한다.

자세한 개발 지침은 루트의 [`CLAUDE.md`](./CLAUDE.md)를 따른다.

---

## 요구 사항

| 항목 | 값 |
|---|---|
| OS | Windows 10 / 11 (x64) |
| .NET SDK | **10.x (LTS)** — `net10.0` / `net10.0-windows` |
| 런타임 | `Microsoft.WindowsDesktop.App` 10.x |

.NET 10을 고른 이유: 조사 시점(2026-09) 기준 .NET 10이 현재 LTS이고,
.NET 9는 STS로 지원이 끝났으며 .NET 8 LTS는 2026-11에 종료된다.

---

## 빌드 · 테스트 · 실행

```powershell
dotnet restore CodexBackupManager.sln
dotnet build   CodexBackupManager.sln -c Debug
dotnet test    CodexBackupManager.sln -c Debug
dotnet run --project src\CodexBackupManager.App
```

한 번에 전부 검증하려면:

```powershell
# 0. 환경 확인 → 1. restore → 2. build → 3. test
# → 4. .codex 스냅샷 → 5. 앱 실행 → 6. 스냅샷 비교(쓰기 발생 여부)
powershell -ExecutionPolicy Bypass -File scripts\verify-phase1.ps1

# GUI 없이 자동 검증만
powershell -ExecutionPolicy Bypass -File scripts\verify-phase1.ps1 -SkipRun
```

> 6단계 비교를 의미 있게 하려면 **Codex를 완전히 종료한 상태**로 실행하세요.
> Codex가 켜져 있으면 Codex 자신이 파일을 갱신하므로 우리 프로그램의 쓰기와 구분할 수 없습니다.

---

## 구조

```
CodexBackupManager/
├─ CLAUDE.md                         개발 지침 (최우선)
├─ docs/
│  ├─ phase0-codex-investigation-2026-09-11.md   조사 원본 기록
│  └─ codex-storage-format.md                    구현 기준 문서 ★ 먼저 읽을 것
├─ scripts/verify-phase1.ps1
├─ src/
│  ├─ CodexBackupManager.Domain/     의존성 0. 값 객체와 모델만
│  │    Paths/CanonicalPath          Windows 경로 정규화 (\\?\ / NFC / 대소문자)
│  │    Codex/…                      CodexInstallationInfo, CodexHomeValidation, …
│  │    Diagnostics/Redact           로그용 민감정보 마스킹
│  ├─ CodexBackupManager.Codex/      Codex 데이터 접근 (Read-Only)
│  │    Locating/CodexLocator        CODEX_HOME → %USERPROFILE%\.codex → 저장된 경로 → 사용자 선택
│  │    Locating/CodexHomeValidator  Valid / Probable / Invalid + 사유
│  │    Locating/CodexHomeLayout     state_*.sqlite 등 파일명 패턴 한 곳에 모음
│  │    Inspection/…                 StateDbReader, ConfigTomlValueReader,
│  │                                 GlobalStateReader, SessionFileCounter,
│  │                                 CodexInstallationInspector
│  │    Sqlite/ReadOnlySqlite        읽기 전용 SQLite 연결의 유일한 통로
│  │    CodexDetectionService        탐색 + 검증 + 조사 파사드
│  └─ CodexBackupManager.App/        WPF (MVVM). 로직 없음
└─ tests/
   ├─ CodexBackupManager.Domain.Tests/
   ├─ CodexBackupManager.Codex.Tests/
   └─ Fixtures/CodexHome/            합성 가짜 Codex Home (실제 데이터 아님)
```

의존 방향은 단방향이다: `App → Codex → Domain`.

---

## Phase 1이 표시하는 것

```
Codex Backup Manager                              ● Codex 연결됨

Codex Home
C:\Users\User\.codex

탐지 경로                    CODEX_HOME 환경변수
Codex Desktop               26.903.61454
Codex CLI                   0.153.4
CLI 실행 파일                ...\codex.exe (존재)
State DB                    Generation 5 — state_5.sqlite
Migration                   52
Sessions                    365
Archived                    1
Threads (state DB)          352행, archived 1
session_index.jsonl         136줄
projectsMigrated            true
threadAssignmentsMigrated   false
검증 점수                    Valid (5/5)
SQLite 열기 모드              Mode=ReadOnly, Pooling=False
```

위 값은 **전부 런타임에 읽은 결과**다. 코드에 하드코딩된 값은 하나도 없다.
그것을 증명하기 위해 테스트 픽스처는 일부러 다른 값을 쓴다
(Desktop `99.123.45678`, CLI `9.9.9`, state generation `4`, migration `7`).

Codex를 찾지 못하면 후보 경로별 탈락 사유를 함께 보여주고 `[Codex 폴더 선택]`을 제공한다.

---

## 로드맵

| Phase | 내용 | 상태 |
|---|---|---|
| 1 | Codex Home 탐색 · 검증 · 설치 정보 조회 | **완료** |
| 2 | Read Model — JSONL Parser, 세션 메타데이터, 프로젝트 그룹화, 대화 목록 | 예정 |
| 3 | Conversation Viewer — User / Assistant 메시지 | 예정 |
| 4 | Selection — 프로젝트/대화 다중 선택 | 예정 |
| 5 | Export — `.codexbackup` (ZIP + Manifest + SHA-256) | 예정 |
| 6 | Import Preview — Manifest/체크섬 검사, 충돌 검사, 경로 재매핑 | 예정 |
| 7 | Safe Restore — Snapshot → Apply → 검증 → Rollback | 예정 |

---

## 알려진 정리 필요 항목

- **NuGet 패키지 버전이 floating(`10.0.*`, `2.*`, `17.*`, `3.*`)이다.**
  첫 `dotnet restore` 성공 후 `scripts\verify-phase1.ps1`이 출력하는 실제 해석 버전으로 고정할 것.
- `.zst` 압축 rollout은 개수만 세고 처리하지 않는다. (Phase 2 이후)
- `CODEX_HOME` 환경변수의 런타임 실측값은 `verify-phase1.ps1`이 출력한다.

---

## 외부 의존성

| 패키지 | 용도 |
|---|---|
| `Microsoft.Data.Sqlite` | `state_*.sqlite` 읽기 전용 접근 |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | 테스트 |

그 밖에는 전부 BCL만 쓴다. 로거, TOML 스칼라 리더, MVVM 베이스, 폴더 선택 대화상자 모두 직접 구현했다.
**외부 프로그램을 실행하거나 필수 의존성으로 삼지 않는다** (CLAUDE.md §2.1).
