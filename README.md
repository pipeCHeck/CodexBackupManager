# Codex Backup Manager

OpenAI Codex의 로컬 프로젝트/대화 데이터를 조회 · 선택 · 내보내기 · 불러오기 · 복원하는 **Windows 데스크톱 프로그램**.

> **현재 상태: Phase 1(Codex 탐색) + Phase 2(Read Model) + Phase 3(Conversation Viewer)**
> **+ Phase 4(Selection) + Phase 5(Export, `.codexbackup`) + Phase 05_01(Backup V1 Freeze)**
> **+ Phase 6~06_03(Import Preview/ImportPlan/Preflight) + Phase 7(Safe Restore) +**
> **Phase 07_01(Restore Hardening + Apply UI) + Phase 07_02(Release Safety Gate) +**
> **Phase 07_03(Final Restore Edge-Case Hardening) 완료.**
> Codex에 실제로 쓰는 첫 기능이 Phase 7에서 들어갔고, 07_01/07_02/07_03을 거치며 실제 원본
> `.codex` clone으로 재현한 crash/동시성 edge case를 포함해 Restore 안전성을 반복적으로
> 검증·강화했습니다 — New Import와 안전이 증명된 IncomingAhead fast-forward만 지원하고,
> Diverged 자동 merge 등 고위험 기능은 여전히 지원하지 않습니다. Restore Core는 이제 기능 변경
> 없이 Phase 8(Release/Packaging)로 넘어갑니다. 자세한 내용은 `docs/safe-restore-phase7.md` §10~11
> 참고.

---

## 이 프로그램의 최우선 원칙

> 사용자의 기존 Codex 데이터를 **절대 잃지 않으면서** 프로젝트와 대화를 안전하게 이동시킨다.

- **탐색/Viewer/Export/Import Preview는 전부 Read-Only.** 이 경로에서는 Codex Home 아래 어떤 파일도
  만들거나 바꾸거나 지우지 않고, SQLite도 `Mode=ReadOnly` + `Pooling=False`로만 연다.
- **실제 write는 [적용] 버튼(Apply, Phase 7/07_01/07_02)에서만, 그것도 제한적으로 일어난다** — Codex가
  완전히 종료되어 있는지 확인하고, 그 순간 다시 fresh preflight를 통과한 뒤, 반드시 복구용 Snapshot을
  먼저 만들고 나서야 rollout 파일/`threads` 테이블에 쓴다. 실패하면 Snapshot으로 자동 Rollback한다
  (상세: [`docs/safe-restore-phase7.md`](./docs/safe-restore-phase7.md)).
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
│  ├─ project-status-and-handoff.md              현재 상태/다음 작업 인계 문서 ★★ 세션 시작 시 가장 먼저 읽을 것
│  ├─ phase0-codex-investigation-2026-09-11.md   조사 원본 기록
│  ├─ codex-storage-format.md                    구현 기준 문서 ★ 먼저 읽을 것
│  ├─ codexbackup-format-v1.md                   Backup Format V1 스펙(FROZEN) + Restore Sufficiency Audit
│  └─ import-preview-phase6.md                   Phase 6 Import Preview 스펙(RevisionRelation/fast-forward/divergence/metadata diff/path remapping)
├─ scripts/verify-phase1.ps1
├─ src/
│  ├─ CodexBackupManager.Domain/     의존성 0. 값 객체와 모델만
│  │    Paths/CanonicalPath          Windows 경로 정규화 (\\?\ / NFC / 대소문자)
│  │    Codex/…                      CodexInstallationInfo, CodexHomeValidation, …
│  │    Codex/Rollout, Sessions, Threads, Titles, Projects, Catalog
│  │                                 Phase 2 Read Model 도메인(rollout 파일 참조,
│  │                                 세션 메타데이터, thread 체인, 제목/프로젝트 해결, 카탈로그)
│  │    Diagnostics/Redact           로그용 민감정보 마스킹
│  │    Codex/Selection/ConversationSelectionState
│  │                                 Phase 4 백업 선택의 단일 source of truth(ThreadId 기준,
│  │                                 WPF 의존성 없음 — Viewer 포커스와 완전히 분리된 상태)
│  │    Codex/Import/…                Phase 6 — RolloutSlice/ConversationRevision(revision
│  │                                 fingerprint), RevisionRelation, MetadataDifferences,
│  │                                 ProjectPathMapping. 전부 순수 데이터, I/O 없음
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
│  │    Threads/ThreadDependencyResolver
│  │                                 선택 대화를 완전히 재구성하는 데 필요한 rollout 파일 집합을
│  │                                 계산(Viewer의 ConversationTranscriptBuilder와 Export의
│  │                                 ExportPlanBuilder가 이 하나의 코드를 공유한다)
│  │    Attachments/LocalImageAttachmentScanner
│  │                                 rollout content가 참조하는 local_image 첨부 경로를 찾는다
│  │    Rollout/RolloutSliceHasher, IRolloutSliceReader
│  │                                 Phase 6 — rollout 슬라이스의 SHA-256/길이를 스트리밍으로 계산.
│  │                                 로컬 파일/backup ZIP entry를 같은 인터페이스로 추상화
│  │    Revisions/ConversationRevisionBuilder, ConversationRevisionComparer
│  │                                 Phase 6 — thread의 revision fingerprint를 만들고 두 revision을
│  │                                 비교해 RevisionRelation을 판정(timestamp 아닌 실제 내용 기준)
│  ├─ CodexBackupManager.Backup/     `.codexbackup` Export/검증 (Phase 5) + Import Preview (Phase 6)
│  │    Planning/ExportPlanBuilder   선택 ThreadId → 무엇을 내보낼지 계산(dedupe/dependency closure)
│  │    Manifest/…                  manifest.json 모델 + 직렬화
│  │    Checksums/…                 checksums.json 모델 + 직렬화
│  │    Writing/BackupWriter        스트리밍 ZIP 작성 + atomic publish + self-validation
│  │    Reading/BackupReader        `.codexbackup` 읽기 전용 리더
│  │    Validation/BackupValidator  manifest/체크섬/path traversal/JSONL 최소 parse 검증
│  │    Import/ImportPreviewBuilder Phase 6 진입점 — 검증 → backup lineage 재구성 → RevisionRelation
│  │                                 판정 → metadata diff → path mapping까지, Codex에는 쓰지 않는다
│  │    Import/BackupCatalogReader  backup의 payload/rollouts/만으로 로컬과 같은 lineage 재구성
│  │    Import/ImportConflictAnalyzer, MetadataDifferenceAnalyzer, ProjectPathMapper
│  └─ CodexBackupManager.App/        WPF (MVVM). 로직 없음
└─ tests/
   ├─ CodexBackupManager.Domain.Tests/
   ├─ CodexBackupManager.Codex.Tests/    Revisions/ConversationRevisionComparerTests(Phase 6) 포함
   ├─ CodexBackupManager.Backup.Tests/  Import/ImportPreviewBuilderTests 등(Phase 6) 포함
   ├─ CodexBackupManager.App.Tests/  ViewModel 단위 테스트(Selection tri-state, Viewer 독립성,
   │                                 MainViewModelImportPreviewTests 등)
   └─ Fixtures/CodexHome/            합성 가짜 Codex Home (실제 데이터 아님)
```

의존 방향은 단방향이다: `App → Backup → Codex → Domain`.

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
이 단계(Phase 2)에서는 대화 내용을 열람하는 Viewer는 아직 구현하지 않았다(Viewer는 Phase 3에서 완료 —
아래 "Phase 3가 표시하는 것" 참고). 선택 기능은 여전히 Phase 4 예정이다.

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

## Phase 4가 표시하는 것

프로젝트/대화 앞에 체크박스가 생겨, 백업 대상(프로젝트 전체 또는 개별 대화, 여러 개 동시)을 미리
선택해 둘 수 있다. 아직 Export 자체는 만들지 않는다 — 이 Phase는 "무엇을 백업할지 고르는" 상태만
다룬다.

- **선택과 Viewer 포커스는 완전히 별개다.** 왼쪽 트리에서 대화 제목을 클릭하면(기존과 동일하게)
  오른쪽에 그 대화가 열리지만 체크 상태는 바뀌지 않고, 체크박스를 누르면 백업 대상만 바뀔 뿐
  오른쪽 Viewer는 그대로다. 두 상태는 서로 다른 질문("지금 보고 있는 대화" vs "백업할 대화")이라
  구조적으로 분리했다(`ConversationNodeViewModel.IsSelected` ↔ `MainViewModel.SelectConversation`).
- **선택의 단일 source of truth**: `ConversationSelectionState`(Domain 계층, WPF 의존성 없음)가
  ThreadId 기준 `HashSet`으로 선택 여부를 관리한다. Project/Conversation ViewModel은 각자 상태를
  따로 저장하지 않고 전부 이 하나의 저장소를 그대로 읽고 쓰는 얇은 뷰다.
- **프로젝트 체크박스는 3상태**(`IsThreeState`): 전체 선택 / 전체 미선택 / 일부 선택(indeterminate)을
  자식들의 실제 선택 상태로부터 매번 다시 계산한다. 클릭하면 "이미 전체 선택 상태가 아니면 전체
  선택, 이미 전체 선택 상태면 전체 해제"로 동작한다 — WPF tri-state 체크박스가 클릭 시 전달하는
  원시값은 무시하고 클릭 직전 상태만으로 판단한다. "기타 대화" 그룹도 일반 프로젝트와 동일하다.
- **대량 선택도 안전하게**: 프로젝트 전체 선택/해제, 전체 선택/선택 해제 버튼은 중앙 저장소를 한
  번만 호출하고 자식마다 딱 한 번씩만 화면 갱신을 알린다 — 항목이 수천 개로 늘어도 재계산이
  연쇄되거나 반복되지 않는다.
- **카탈로그 refresh 시 선택 보존**: 같은 Codex Home을 "다시 확인"해서 카탈로그를 재구축하면,
  더 이상 존재하지 않는 대화만 선택에서 빠지고 나머지는 유지된다. 반대로 다른 Codex Home으로
  바꾸면 이전 선택은 전부 초기화된다(서로 다른 Home의 선택이 섞이지 않는다).
- **Phase 5 연동 API**: `MainViewModel.GetSelectedThreadIdsSnapshot()`이 현재 선택의 불변 스냅샷을
  돌려준다 — Export(Phase 5)가 UI ViewModel을 직접 해석할 필요가 없다.

---

## Phase 5가 표시하는 것

하단에 **[백업 내보내기]** 버튼이 생긴다. 선택한 대화 수가 0개면 비활성화되고, `SaveFileDialog`로
저장 위치를 고르면(기본 파일 이름은 대화 제목이 아니라 시각 기반) 백그라운드에서 Export가 진행되고
완료/실패 결과가 버튼 옆에 요약으로 뜬다(선택 대화 수/크기/소요 시간, 실패 시 안전한 오류 문구).

- **core(`CodexBackupManager.Backup`)와 UI가 분리돼 있다.** `MainViewModel`은
  `ExportPlanBuilder.Build()` → `ManifestBuilder.Build()` → `BackupWriter.Write()`를 그대로 호출할
  뿐, ZIP/체크섬/atomic publish 로직을 직접 갖고 있지 않다.
- **dependency closure는 Viewer와 같은 규칙을 쓴다.** 선택한 대화의 조상(분기/세그먼트) rollout
  파일까지 자동으로 포함하되, 그 조상 자신은 "선택한 대화 수"에 세지 않는다(dependency로 별도 집계).
- **원본 바이트를 그대로 보존한다.** rollout `.jsonl`/`.jsonl.zst`를 파싱해서 다시 쓰지 않고
  스트리밍으로 그대로 복사한다 — 대형 rollout(288MB 실측)에서도 메모리 사용량이 파일 크기에
  비례해서 늘지 않는다.
- **자기 자신을 다시 검증한 뒤에만 최종 파일이 된다.** temp 파일로 먼저 쓰고, `BackupValidator`로
  다시 열어 manifest/체크섬/path traversal 등을 전부 검사한 뒤에만 최종 `.codexbackup`으로 옮긴다.
  검증 실패·취소·예외 시 temp만 지우고 기존 파일은 건드리지 않는다.
- Import Preview는 Phase 6에서 완료했다(아래 참고) — `BackupReader`/`BackupValidator`(읽기/검증)를
  그대로 재사용한다. Codex에 실제로 적용하는 기능(Apply/Restore)은 Phase 7/07_01에서 추가됐다(아래
  "Phase 7/07_01이 표시하는 것" 참고).

자세한 포맷 스펙과 설계 근거는 [`docs/codexbackup-format-v1.md`](./docs/codexbackup-format-v1.md) 참고.

---

## Phase 6이 표시하는 것

하단에 **[백업 불러오기]** 버튼이 생긴다. `.codexbackup` 파일을 고르면(`OpenFileDialog`)
`BackupValidator`로 즉시 검증하고, 통과하면 카탈로그/뷰어 영역을 덮는 **Import Preview** 패널이
뜬다 — 프로젝트별로 대화 상태(신규/동일/업데이트 가능/현재 PC가 더 최신/분기 충돌/확인 불가)와
경로 재매핑 상태를 보여준다. **Preview만으로는 선택 즉시 적용하지 않는다** — Codex에는 어떤
write도 없다. 실제로 적용하려면 같은 화면의 **[적용]** 버튼을 따로 눌러야 한다(Phase 7/07_01, 아래
"Phase 7/07_01이 표시하는 것" 참고).

- **timestamp가 아니라 실제 rollout 내용으로 판정한다.** 같은 ThreadId를 로컬과 backup 양쪽이 갖고
  있으면 "중복"으로 건너뛰지 않고, 실제로 소비되는 rollout byte 구간(`ConversationRevision`/
  `RolloutSlice`)을 비교해 `RevisionRelation`(신규/동일/업데이트 가능/현재 PC가 더 최신/분기
  충돌/확인 불가)을 판정한다 — 두 PC를 오가며 같은 대화를 이어서 작업하는 시나리오를 지원하기
  위해서다.
- **lineage 판단은 Viewer/Export와 완전히 같은 코드를 쓴다.** `ThreadDependencyResolver`에
  파일별 컷오프 계산(`ResolveFileSlices`)을 추출해 Viewer(`ConversationTranscriptBuilder`)도 이걸
  쓰도록 리팩터링했다 — 세 곳(Viewer/Export/Import Preview)이 절대 서로 다른 lineage 규칙으로
  갈라지지 않는다.
- **분기(Diverged)는 자동 merge하지 않는다.** "같은 rollout id, 다른 길이"는 실제로 byte prefix인지
  다시 읽어서 확인한 뒤에만 fast-forward로 인정하고, 그렇지 않으면 분기 충돌로 표시할 뿐 Phase 6이
  임의로 합치지 않는다.
- **metadata 차이는 대화 내용 관계와 분리해서 보여준다.** cwd/프로젝트 연결/고정 여부/섹션/제목/
  최근 사용 시각이 달라도 대화 내용 자체는 완전히 같을 수 있다 — 이 둘을 섞지 않는다.
- **경로 재매핑은 제안만 한다.** backup 프로젝트의 원본 경로가 현재 PC의 로컬 프로젝트와 canonical
  path로 일치하면 자동 연결 표시를 하지만, 수동 재지정은 이 화면에서 바로 할 수 있고 실제 적용은
  Phase 7/07_01의 [적용] 버튼이 한다.
- **core와 UI가 분리돼 있다.** `MainViewModel.ImportPreviewCommand`가 `ImportPreviewBuilder.Build()`를
  그대로 호출할 뿐, ZIP/rollout 비교 로직을 직접 갖고 있지 않다.

자세한 스펙(RevisionRelation 정의, fast-forward/divergence 판정 알고리즘, metadata diff 정책,
경로 재매핑)은 [`docs/import-preview-phase6.md`](./docs/import-preview-phase6.md) 참고.

---

## Phase 7/07_01이 표시하는 것

Import Preview 패널 안에 **[적용]** 버튼이 생긴다. 누르면 확인 대화상자("백업 내용을 Codex에
적용합니다. 적용 전에 현재 상태의 복구용 Snapshot을 생성합니다. Codex가 완전히 종료되어 있어야
합니다. 계속하시겠습니까?")가 뜨고, 승인해야만 실제 적용이 시작된다.

- **버튼을 누른다고 바로 안전하다고 믿지 않는다.** 버튼의 활성 조건(Plan 존재 여부)은 1차 UI
  판단일 뿐이고, 실제 안전성(Codex 실행 여부, backup/로컬 상태가 Preview 때와 같은지, 물리적으로
  안전한 fast-forward인지)은 클릭한 바로 그 순간 `RestoreExecutor`가 처음부터 다시 확인한다 — UI는
  Preview를 다시 해석하거나 판단을 대신하지 않는다.
- **중간 실패가 나도 100% 원상복구.** 적용 직전에 항상 복구용 Snapshot을 만들고, Snapshot 이후
  어디서 실패하든(사용자 취소 포함) 자동으로 Rollback한다 — "취소해서 복원", "오류가 나서 복원",
  "복원 자체가 실패한 CRITICAL 상태"를 서로 다른 문구로 구분해서 보여준다.
- **적용 중에는 다른 조작을 막는다.** Export/새 Import Preview 시작/프로젝트 경로 재지정/Codex 폴더
  변경 버튼이 전부 비활성화된다.
- **알려진 제약을 항상 먼저 보여준다.** 이 backup에 실제로 해당하는지와 무관하게, Codex Desktop
  사이드바 반영 여부 미검증·`local_image` 미지원·새 프로젝트 자동 생성 미지원·`.jsonl.zst` 이어받기
  미지원·분기(Diverged) 자동 적용 미지원을 같은 화면에 항상 표시한다 — "성공했다"는 결과만 보고
  제약을 놓치는 일이 없게 하기 위해서다.
- **지원 범위는 그대로다.** New Import, 완전 동일(NoOp), 안전이 증명된 IncomingAhead fast-forward,
  현재 PC가 더 최신인 경우(Skip)만 실제로 적용하고, 분기(Diverged)나 확인 불가(Unverifiable)는
  적용 자체를 전체 차단한다.

자세한 스펙(Snapshot/Rollback 설계, 공식 `codex-rs` 소스 조사 결과, Phase 07_01에서 고친 안전성
문제 목록, 알려진 한계)은 [`docs/safe-restore-phase7.md`](./docs/safe-restore-phase7.md) 참고.

---

## UI/UX 개선 (Phase 4 사후)

기능은 그대로 두고 가독성 · 레이아웃 · 렌더링 품질만 다듬은 작업. 새 기능(Export/Import)은 없다.

- **색상 대비 정리**: 버튼 스타일이 배경/전경을 지정하지 않아 시스템 기본 크롬(밝은 배경에 가까움)을
  그대로 물려받았던 게 "전체 선택/선택 해제 버튼이 흰 배경/흰 글씨처럼 보이는" 원인이었다. 버튼에
  전용 `ControlTemplate`을 주고 Hover/Pressed/Disabled 상태별 배경·전경·테두리를 전부 `App.xaml`
  리소스(`ButtonBg`/`ButtonFg`/`ButtonBgDisabled`/`ButtonFgDisabled` 등)로 명시했다 — Disabled도
  "비활성처럼 보이되 글자는 읽을 수 있게" 만들었다. CheckBox도 라벨 글자색을 명시해 테마와 무관하게
  또렷이 보이게 했다. Assistant 말풍선 배경은 창 배경(`Bg`)과 구분되는 `PanelAlt`로 바꿔 대비를 줬다.
- **레이아웃**: 왼쪽 프로젝트/대화 트리 폭을 300→380(최소 260)으로 넓히고, 오른쪽 Viewer와의 경계에
  `GridSplitter`를 추가해 사용자가 직접 폭을 조절할 수 있다. 프로젝트/대화 이름은 `StackPanel` 대신
  `Grid`(Auto+`*`)로 감싸 실제로 폭이 제한되게 했다 — 전에는 `StackPanel`이 자식에게 무한 너비를 줘서
  `TextTrimming`이 사실상 동작하지 않았다. 너무 길면 말줄임(`…`) 처리되고, 마우스를 올리면 전체 이름이
  ToolTip으로 보인다.
- **Viewer 렌더링(Markdown-lite)**: 대화 본문을 더 이상 순수 텍스트로 보여주지 않는다.
  `CodexBackupManager.App.Rendering.MarkdownLiteParser`가 문단/줄바꿈/제목(`#`~`###`)/번호 목록/불릿
  목록/코드블록(펜스 ```` ``` ````)/인라인 코드(`` `code` ``)를 인식하고, `MarkdownLiteFlowDocumentRenderer`가
  이를 WPF `FlowDocument`로 그린다. 외부 markdown 패키지 대신 직접 만든 최소 subset 파서다(과설계 방지,
  CLAUDE.md의 최소 의존성 원칙). 렌더링은 `TextBlock`이 아니라 읽기 전용 `RichTextBox`에 붙이는데,
  `RichTextBox.Document`가 바인딩 불가능한 일반 CLR 속성이라 `FlowDocumentBinding` 첨부 속성을 거친다 —
  이 방식이라야 서식이 섞여도 기존처럼 텍스트 선택/복사가 유지된다. 실제 `.codex` 데이터(1,624개 실측
  메시지)로 확인한 결과 제목 1,296개·불릿 항목 4,291개·번호 항목 792개·코드블록 403개가 실제로
  파싱되었고 예외는 0건이었다.
- **스크롤 부드럽게**: 메시지 `ListBox`에 `VirtualizingPanel.ScrollUnit="Pixel"`을 추가했다. 기존
  기본값(Item 단위)은 마우스 휠 한 번에 "메시지 하나"(=여러 줄짜리 말풍선 전체) 단위로 건너뛰어
  스크롤이 딱딱했다. Pixel 단위로 바꾸면 가상화(성능)는 그대로 유지하면서 일반 문서처럼 부드럽게
  스크롤된다 — 실측 1,431개 메시지 대화에서도 전체 렌더링 894ms로 체감 지연이 없었다.

---

## 로드맵

| Phase | 내용 | 상태 |
|---|---|---|
| 1 | Codex Home 탐색 · 검증 · 설치 정보 조회 | **완료** |
| 2 | Read Model — rollout 파서, thread 체인, 프로젝트/제목 해결, 대화 목록 | **완료** |
| 3 | Conversation Viewer — User / Assistant 메시지 | **완료** |
| 4 | Selection — 프로젝트/대화 다중 선택 | **완료** |
| 5 | Export — `.codexbackup` (ZIP + Manifest + SHA-256) | **완료** |
| 05_01 | Backup V1 Freeze / Restore Sufficiency Hardening | **완료** |
| 6 | Import Preview — 검증, RevisionRelation 판정(timestamp 아닌 실제 내용 기준), 경로 재매핑 제안 | **완료** |
| 06_01~06_03 | Revision Relation Hardening / Apply Preconditions Freeze / Preview Source Identity Pinning | **완료** |
| 7 | Restore Core — Snapshot → Apply(New/IncomingAhead fast-forward만) → 검증 → Rollback | **완료** |
| 07_01 | Restore Hardening + Apply UI — 안전성/정합성 하드닝, Apply 버튼, 실제 `.codex` clone E2E | **완료** |
| 07_02 | Release Safety Gate — atomic append, crash recovery journal, WAL/SHM-safe rollback, cwd remap, 실제 rollout IncomingAhead clone E2E | **완료** |
| 07_03 | Final Restore Edge-Case Hardening — New rollout atomic/durability, Home별 incomplete-apply scope, Recover consistency gate, 프로세스 간 Restore lock | **완료** |
| 8 | Release / self-contained EXE / final QA | 예정 |

Export(`.codexbackup` V1) 포맷/설계 전체는 [`docs/codexbackup-format-v1.md`](./docs/codexbackup-format-v1.md)에
있다 — Restore Sufficiency Audit(어떤 thread metadata가 있어야 복원할 수 있는지), dependency closure
정책, 첨부 정책, 체크섬/atomic export 정책을 담고 있다. Import Preview/`ImportPlan`/Preflight 스펙은
[`docs/import-preview-phase6.md`](./docs/import-preview-phase6.md)에, Safe Restore(Phase 7/07_01) 스펙과
공식 `codex-rs` 소스 조사 결과는 [`docs/safe-restore-phase7.md`](./docs/safe-restore-phase7.md)에 있다.

---

## 알려진 정리 필요 항목

- `.zst` 압축 rollout은 우리가 직접 압축한 fixture로만 검증했다. 실제 Codex `.zst` 실물은 아직
  확인하지 못했다(조사 시점 이 PC에 0개). `docs/codex-storage-format.md` §9 참고.
- `ThreadChainResolver.ResolveAncestry`(분기 조상 체인 계산)는 `ConversationTranscriptBuilder`(Viewer)와
  `ExportPlanBuilder`(Export)/Import Preview(`ConversationRevisionBuilder`)가
  `ThreadDependencyResolver`를 통해 **똑같이** 호출한다 — 실제 segmented/forked 대화로 세 경로가
  일치함을 실측 확인했다.
- **(Phase 6)** 이 PC의 실제 `.codex` 데이터에는 진짜로 두 PC를 오간 `Diverged`/`LocalAhead` 사례가
  없어, 실제 rollout 파일을 복사·수정한 합성 스냅샷으로 재현·검증했다(`New`/`Identical`/
  `IncomingAhead`는 실제 Export→Import Preview 왕복으로 직접 확인).
- Export가 `attachments\`(붙여넣기 텍스트)/`visualizations\`/`generated_images\` 폴더의 실제 파일은
  아직 포함하지 않는다 — 구조적으로 안전하게 참조를 추적할 방법을 찾지 못했다(`docs/codexbackup-format-v1.md` §2 참고).
  `local_image` 참조(스크린샷 등)는 포함한다.
- **(Phase 07_01/07_02)** Import된 대화가 Codex Desktop 앱 사이드바에 올바른 프로젝트로 묶여
  보이는지는 아직 검증하지 못했다 — Core/CLI의 project 배정 authority(`threads.project_id`)는
  공식 소스로 확정했지만 Electron Desktop 소스는 조사하지 못했고, `CODEX_HOME`을 통한 안전한
  격리 실행도 CLI에서만 확인했다(`docs/safe-restore-phase7.md` §11.7).
- **(Phase 07_02)** 실제 원본 `.codex` clone으로 New/IncomingAhead(단일 segment fast-forward) E2E는
  직접 확인했지만, segment 전환이 포함된 IncomingAhead와 `Diverged`/`LocalAhead`의 실제 데이터
  사례는 이 PC에 없어 여전히 합성 데이터로만 검증했다(`docs/safe-restore-phase7.md` §11.2/11.9).
- **(Phase 07_03)** `RestoreProcessLock`은 named Mutex 기반이라 같은 Windows 로그인 세션 안에서만
  유효하다 — 서로 다른 사용자 세션/원격 세션 간 잠금은 범위 밖이다(단일 사용자 데스크톱 앱
  전제, `docs/safe-restore-phase7.md` §12.4).

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
