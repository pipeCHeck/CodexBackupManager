# Codex Backup Manager

OpenAI Codex의 로컬 프로젝트/대화 데이터를 조회 · 선택 · 내보내기 · 불러오기 · 복원하는 **Windows 데스크톱 프로그램**.

> **현재 상태: Phase 1(Codex 탐색) + Phase 2(Read Model) + Phase 3(Conversation Viewer) 완료.**
> 선택 / Export / Import / Restore는 아직 없습니다.

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

위 `dotnet build`/`dotnet run`은 개발용 framework-dependent 빌드다(대상 PC에 .NET 10 Desktop 런타임 필요).
CLAUDE.md §38이 정한 최종 배포 형태(런타임 설치 불필요)는 배포할 때 다음처럼 self-contained로 publish한다.

```powershell
dotnet publish src\CodexBackupManager.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

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
│  │    Codex/Rollout, Sessions, Threads, Titles, Projects, Catalog
│  │                                 Phase 2 Read Model 도메인(rollout 파일 참조,
│  │                                 세션 메타데이터, thread 체인, 제목/프로젝트 해결, 카탈로그)
│  │    Diagnostics/Redact           로그용 민감정보 마스킹
│  ├─ CodexBackupManager.Codex/      Codex 데이터 접근 (Read-Only)
│  │    Locating/CodexLocator        CODEX_HOME → %USERPROFILE%\.codex → 저장된 경로 → 사용자 선택
│  │    Locating/CodexHomeValidator  Valid / Probable / Invalid + 사유
│  │    Locating/CodexHomeLayout     state_*.sqlite 등 파일명 패턴 한 곳에 모음
│  │    Inspection/…                 StateDbReader, ThreadRowReader, ProjectTableReader,
│  │                                 SessionIndexReader, ConfigTomlValueReader,
│  │                                 GlobalStateReader, SessionFileCounter,
│  │                                 CodexInstallationInspector
│  │    Rollout/…                    RolloutFileLocator, RolloutStreamReader(.jsonl/.jsonl.zst)
│  │    Sessions/CodexSessionParser  rollout의 session_meta 한 줄 파싱(스트리밍)
│  │    Threads/ThreadChainResolver  세그먼트/분기 thread를 하나의 체인으로
│  │    Titles/ThreadTitleResolver   제목 우선순위 결정
│  │    Projects/CodexProjectResolver  프로젝트↔대화 연결(마이그레이션 상태별 authoritative 정책)
│  │    Sqlite/ReadOnlySqlite        읽기 전용 SQLite 연결의 유일한 통로
│  │    CodexDetectionService        탐색 + 검증 + 조사 파사드 (Phase 1)
│  │    Catalog/CodexCatalogBuilder  "Project → User Conversation 목록" 오케스트레이터 (Phase 2)
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

탐지 경로                    %USERPROFILE%\.codex
Codex Desktop               26.903.61454
Codex CLI                   0.153.4
CLI 실행 파일                ...\codex.exe (존재)
State DB                    Generation 5 — state_5.sqlite
Migration                   52
Sessions                    370
Archived                    1
Threads (state DB)          357행, archived 1
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

> **`CODEX_HOME` 환경변수에 대한 실측 결과 (Phase 1 검증, 2026-09-11):** 이 PC에서는
> `Process`/`User`/`Machine` 세 스코프 모두 `CODEX_HOME`이 설정되어 있지 않았다.
> Phase 0 조사 당시 "config.toml이 주입하는 값으로 추정 확정"이라고 적었던 것은 틀린 추정이었다 —
> 실제로는 `CodexLocator`가 2순위 후보인 `%USERPROFILE%\.codex`로 정상 폴백해 Codex Home을 찾았다
> (`source=UserProfileDotCodex`). 다른 PC/다른 실행 환경에서는 실제로 설정되어 있을 수 있으므로
> 하드코딩하지 않고 매번 런타임에 읽는다. 상세: `docs/codex-storage-format.md` §9.

---

## Phase 2가 표시하는 것

Phase 1의 탐지 정보 아래에, 실제 Codex 대화를 **프로젝트 → 사용자 대화** 트리로 보여준다.
`thread_source == "user"`인 대화만 그룹에 노출하고(`subagent`/`guardian_review`는 숨기되 버리지 않는다),
아직 대화 내용을 열람하거나 선택하는 기능은 없다(오른쪽은 Phase 3 예정 placeholder).

```
▼ Balhwajeom_Project (17)
    Gameplay Tag 시스템 구조
    조사 시스템 구현
    ...

▼ UProjectHub (12)
    Column Width 수정
    ...

▼ 기타 대화 (11)
    ...

프로젝트 46개 · 사용자 대화 112개 · rollout 파일 371개 · 스캔 1716ms / 전체 1795ms
```

이 목록은 다음 순서로 만든다(전부 Read-Only, 파일 전체를 메모리에 올리지 않는 스트리밍 방식):

1. `sessions\`/`archived_sessions\`에서 rollout 파일을 찾는다(`RolloutFileLocator`).
2. 각 파일의 `session_meta` 한 줄만 스트리밍으로 읽는다(`RolloutStreamReader` + `CodexSessionParser`,
   `.jsonl.zst`도 순수 관리형 라이브러리로 지원).
3. 세그먼트/분기로 나뉜 파일을 하나의 대화로 묶는다(`ThreadChainResolver`).
4. `state_*.sqlite`의 `threads`를 읽는다(`ThreadRowReader`).
5. 제목을 우선순위대로 결정한다(`ThreadTitleResolver`: `name` → `session_index` → `title` →
   `first_user_message` → `preview` → thread ID 폴백).
6. 프로젝트를 연결한다(`CodexProjectResolver`: `threadAssignmentsMigrated` 값에 따라
   `.codex-global-state.json`과 `threads.project_id` 중 어느 쪽이 authoritative인지 결정 —
   값이 있다고 migration 완료를 추측하지 않는다).

실제 PC 실측(2026-09-11): 프로젝트 46개, 사용자 대화 112개(Phase 0 조사값과 일치), rollout 파일 371개,
JSONL 스캔 1.7초, 전체 빌드 1.8초, WPF 앱 WorkingSet 약 205MB(1.45GB 원본을 메모리에 올리지 않음).

---

## Phase 3가 표시하는 것

프로젝트 트리에서 대화를 선택하면 오른쪽에 실제 User / Assistant 메시지를 보여준다.

- **User / Assistant 메시지 Viewer**: `event_msg`/`item_completed`(UI 레벨)를 우선 사용하고,
  그것이 하나도 없는 파일에서만 `response_item`(API wire 포맷)로 폴백한다(`ConversationItemParser`).
  Assistant 메시지는 `commentary`/`final` phase를 구분해서 보존한다.
- **segmented/history_base 재구성**: 하나의 대화가 여러 rollout 파일(세그먼트, 분기)에 걸쳐
  있을 수 있다. `history_base`로 이어진 부모 파일들을 실제 순서대로 이어붙여 하나의 transcript로
  만든다(`ConversationTranscriptBuilder`, `ThreadChainResolver`).
- **rollout ID 기반 경계 처리**: `history_base.thread_id`는 실제로는 **rollout ID**이지 안정적인
  thread ID가 아니다(공식 `HistoryPosition` 구조체로 확인). 파일 자신의 rollout ID
  (`RolloutFileReference.OwnRolloutId`)로 색인을 만들어(`ThreadChainResolver.BuildRolloutIdIndex`)
  분기/세그먼트가 걸쳐 있는 원본 파일을 정확히 찾고, `end_ordinal_exclusive`로 상속 범위를 자른다.
- **response_item fallback**: `event_msg`가 없는 파일에서는 `response_item`으로 대체하되,
  `role:"developer"`는 항상 숨기고, `role:"user"`라도 확인된 주입 마커(`<recommended_plugins>` 등)와
  정확히 일치하는 조각은 제외한다 — 상세 정책은 아래 "내부 주입 콘텐츠 필터" 참고.
- **가상화 WPF message list**: 메시지 수백~수천 개도 부드럽게 스크롤되도록 `ListBox` +
  `VirtualizingPanel`(Recycling 모드)로 렌더링한다.

### 내부 주입 콘텐츠 필터

`response_item` 폴백 경로에서 실제 사용자가 타이핑하지 않은 시스템 주입 콘텐츠(예:
`<environment_context>`, `<recommended_plugins>`)를 걸러낸다. OpenAI Codex 공식 소스
(`codex-rs/context-fragments/src/fragment.rs`의 `ContextualUserFragment::matches_marked_text`)와
동일하게, **확인된 마커의 시작 태그와 종료 태그가 정확히 양 끝에서 일치할 때만** 숨긴다.
"소문자 태그로 시작하면 숨긴다" 같은 일반 구조 규칙은 쓰지 않는다 — 그런 규칙은 사용자가 실제로
`<code>`/`<summary>`/`<xml>` 같은 정상 HTML/코드 조각으로 메시지를 시작했을 때도 오탐으로 삭제해버리기
때문이다. 내부 메시지 하나를 잘못 보여주는 것보다 실제 사용자 메시지를 누락하는 쪽이 더 심각한
오류라는 원칙에 따라, 확인되지 않은 새 마커는 목록에 추가되기 전까지 숨기지 않는다
(`ConversationItemParser.IsInjectedContent`).

---

## 로드맵

| Phase | 내용 | 상태 |
|---|---|---|
| 1 | Codex Home 탐색 · 검증 · 설치 정보 조회 | **완료** |
| 2 | Read Model — rollout 파서, thread 체인, 프로젝트/제목 해결, 대화 목록 | **완료** |
| 3 | Conversation Viewer — User / Assistant 메시지 | **완료** |
| 4 | Selection — 프로젝트/대화 다중 선택 | 예정 |
| 5 | Export — `.codexbackup` (ZIP + Manifest + SHA-256) | 예정 |
| 6 | Import Preview — Manifest/체크섬 검사, 충돌 검사, 경로 재매핑 | 예정 |
| 7 | Safe Restore — Snapshot → Apply → 검증 → Rollback | 예정 |

---

## 알려진 정리 필요 항목

- `.zst` 압축 rollout은 우리가 직접 압축한 fixture로만 검증했다. 실제 Codex `.zst` 실물은 아직
  확인하지 못했다(조사 시점 이 PC에 0개). `docs/codex-storage-format.md` §9 참고.
- `ThreadChainResolver.ResolveAncestry`(분기 조상 체인 계산)는 Phase 3의 `ConversationTranscriptBuilder`가
  실제로 호출한다 — 다만 Export(Phase 5)에서 백업 대상 파일 범위를 정할 때는 별도로 다시 검증이 필요하다.

---

## 외부 의존성

| 패키지 | 버전 | 용도 |
|---|---|---|
| `Microsoft.Data.Sqlite` | `10.0.12` | `state_*.sqlite` 읽기 전용 접근 |
| `ZstdSharp.Port` | `0.8.8` | `.jsonl.zst` 압축 해제(순수 관리형, 외부 exe 없음) |
| `xunit` | `2.9.3` | 테스트 |
| `xunit.runner.visualstudio` | `3.1.5` | 테스트 러너 |
| `Microsoft.NET.Test.Sdk` | `17.14.1` | 테스트 |

전부 정확한 버전으로 고정되어 있다(floating 버전 없음). 그 밖에는 전부 BCL만 쓴다.
로거, TOML 스칼라 리더, MVVM 베이스, 폴더 선택 대화상자 모두 직접 구현했다.
**외부 프로그램을 실행하거나 필수 의존성으로 삼지 않는다** (CLAUDE.md §2.1).
