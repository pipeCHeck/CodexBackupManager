# Phase 9 전체 설계 — 백업 가져오기 재설계 · 작업 폴더 연결 · 자동 프로젝트 생성

> **상태: 전체 설계(구현 전)** · 작성 2026-09-30 · 대상 버전 `0.2.0`
> 코드는 아직 바꾸지 않았다. 이 문서가 Phase 9 전체(9_0~9_5)의 기준 스펙이다.
> 구현 단계, 작업 ID, 진행 체크리스트는 [`docs/phase9-implementation-plan.md`](./phase9-implementation-plan.md)에 있다.
> 이 문서의 .html은 `python scripts/render-docs.py <md>`로 생성한다(직접 수정 금지).
>
> 근거 자료
> - 사용자 스크린샷(v0.1.1 "백업 불러오기 미리보기")
> - 실제 `C:\Users\User\.codex` **읽기 전용** 진단(작업 전후 해시 불변 확인)
> - `.codex` 복제본 E2E(PC A→B 42항목 PASS, 경로 재지정 Case 0~4)
> - 공식 `openai/codex` 소스 `bcd6d9ab`(2026-09-30): `codex-rs/state/src/runtime/projects.rs`,
>   `codex-rs/app-server/src/request_processors/projects.rs`, `app-server-protocol/src/protocol/v2/{project,thread}.rs`
>
> 확정된 사용자 결정(2026-09-30)
> 1. 최종 목표: **폴더를 고르면 그 경로를 작업 폴더로 하는 프로젝트가 자동으로 생기고, 가져온 대화가 거기 들어간다.**
>    9_2까지는 A안(미등록 폴더면 기타 대화 + 안내)으로 동작하고, 9_5에서 최종 동작으로 바꾼다.
> 2. Codex Desktop 반영 실험(9_0)은 추천안대로 사용자가 직접 진행한다.
> 3. 순서는 추천안대로 **9_1 → 9_2 → 9_2b → 9_5 → 9_3 → 9_4**(9_0은 병행).
> 4. 백업 속 대화 내용 미리보기(9_2b)는 **포함**한다.

---

## 목차

1. 배경과 진단
2. 목표 · 비목표 · 설계 원칙
3. 전체 구조(아키텍처와 데이터 흐름)
4. 핵심 개념: 프로젝트 식별(Project Identity)
5. 도메인 모델 변경
6. 단계별 상세 설계 (9_0 / 9_1 / 9_2 / 9_2b / 9_5 / 9_3 / 9_4)
7. 화면 설계(전체 UI 스펙)
8. 안전성 불변식
9. 사용자 메시지 카탈로그
10. 로깅
11. 테스트 계획
12. 문서 · 버전 · 인계
13. 위험 · 미해결 질문
14. 부록: 실측/공식 소스 근거

---

## 1. 배경과 진단

### 1.1 스크린샷 상황의 실제 원인

| 항목 | 확인 결과 |
|---|---|
| 백업 | `Downloads\codex-backup-20260913-211909.codexbackup`: 대화 1개("b버전 한글패치 제작"), 원본 경로 `C:\Users\ProController\Documents\한글패치` |
| 이 PC Apply 기록 | `%LOCALAPPDATA%\CodexBackupManager\Snapshots\20260913-122034-…`: 2026-09-13 21:20 **적용 성공(journal `Completed`)** |
| 현재 상태 | 이미 이 PC에 있다(비교 결과 "동일"). 원본 경로가 없어 **"기타 대화"**에 들어가 있다 |

첫 번째 불러오기는 성공했다. 대화가 프로젝트가 아닌 "기타 대화"로 들어가서 안 보였고, 다시 불러오면
"동일"이라 할 일이 없었다. 화면은 이 사실을 전달하지 못했다.

### 1.2 구조적 결함 목록

| ID | 결함 | 근거 | 영향 |
|---|---|---|---|
| **A** | 대화가 0개인 등록 프로젝트를 모른다. 재지정이 조용히 무시된다 | `CodexCatalog.Projects`가 대화 있는 그룹만 담는다. `ProjectPathMapper`/`RestoreOperationPlanner.ResolveLocalProjectId`가 이 목록만 본다. 복제본 Case 0/1/3 | "사용자가 지정함"으로 뜨지만 `project_id=null`, `cwd=원본` |
| **B** | 폴더 실존 여부를 안 본다 | `ProjectPathMapper`는 문자열 비교만 한다 | 원본 폴더가 같은 경로에 있어도 "원본 경로를 찾을 수 없습니다" |
| **C** | **레거시 프로젝트 ID를 `threads.project_id`에 쓰려다 외래키 위반 → Apply 전체 Rollback** | `threads.project_id REFERENCES projects(id)`. 카탈로그 프로젝트 ID가 global-state 레거시 ID(`0e4a4695-…`)이면 그 값이 그대로 INSERT된다. 복제본 Case 4: `RolledBack` | 이 PC에서 global-state로 배정된 대화 72개가 쓰는 프로젝트 26개가 **전부 레거시 ID**다. 이 폴더들로 가져오기를 지정하면 **항상 실패**한다 |
| **D** | 같은 폴더가 서로 다른 프로젝트 ID로 여러 번 표시된다 | 레거시 ID와 SQLite ID를 통합하지 않는다(`app-server-project-id-by-legacy-project-id-by-host` 미사용). 실측: 레거시/DB 중복 1건(9_1a에서 해소, 47→46). *(정정 2026-09-30: 처음 '묶음 2건'으로 적은 것은 Codex가 한 폴더를 서로 다른 DB 프로젝트 여러 개로 등록한 경우(4개·2개)이며 중복이 아니라 Ambiguous다.)* | 메인 목록 중복, 매핑 모호 |
| **E** | Codex Desktop의 연결 정보 위치가 불확실하다 | `threadAssignmentsMigrated=false`, `threads.project_id` 채워진 행 416개 중 0개, global-state 배정 72건 | DB에 연결해도 Desktop 사이드바 반영이 미검증이다 |
| **F** | 성공한 Import를 되돌릴 UI가 없다 | UI 명령은 `RecoverIncompleteApplyCommand`(중간 실패 복구)뿐이다 | CLAUDE.md §17 미충족 |
| **G** | 가져오기 화면이 내부 용어 나열이다. 결과·위치 안내가 없다 | 스크린샷 | 사용자가 성공/실패와 위치를 알 수 없다 |
| H | Snapshot이 계속 쌓인다 | 현재 339개(대부분 09-12) | 디스크 사용 증가 |
| I | 일부 테스트가 호스트 환경에 의존한다 | Codex가 켜져 있으면 7건 실패(실제 ProcessGuard 사용) | 개발 중 오탐 |

---

## 2. 목표 · 비목표 · 설계 원칙

### 2.1 목표
1. 가져오기 화면을 **메인 목록과 같은 프로젝트→대화 트리 + 체크박스**로 만든다.
2. 그 화면에서 **가져올 대화를 고르고**, **프로젝트별 작업 폴더를 지정**한다.
3. 폴더를 지정하면 **기존 프로젝트에 연결하거나, 없으면 새 프로젝트를 자동 생성**한다(9_5).
4. 각 대화가 적용 후 **어디에 어떤 상태로 들어가는지** 적용 전에 보여주고, 적용 후에는 결과를 보여준다.
5. 백업 속 **대화 내용을 미리 읽을 수** 있다.
6. 이미 있는 대화의 **프로젝트 연결만 고칠 수** 있다(9_3). 지금 한글패치 대화 같은 경우다.
7. 성공한 가져오기를 **안전하게 되돌릴 수** 있다(9_4).

### 2.2 비목표(V1 범위 유지)
Diverged 자동 병합, 백업으로 기존 대화 내용 교체, `.jsonl.zst` 이어받기, 첨부 파일 복원, global-state 쓰기(9_0 판정 전까지),
Cloud/계정 기능. **Backup Format V1은 바꾸지 않는다(FROZEN).** Export 쪽 변경은 없다.

### 2.3 설계 원칙
- **P1 원본 보호 우선**: 읽기·미리보기·분석은 전부 read-only다. 쓰기는 `RestoreExecutor` 파이프라인
  (ProcessGuard → fresh preflight → pinned backup → Operation Plan → Snapshot → write → post-validation → 실패 시 Rollback)
  **안에서만** 한다. 새 write 종류(프로젝트 생성, 연결 변경)도 이 파이프라인에 올린다.
- **P2 표시는 실제 동작과 일치해야 한다**: "연결됨"은 적용 시 실제로 연결될 때만 표시한다. 반영되지 않을 선택은 적용 전에 경고한다.
- **P3 공식 동작 정합**: 프로젝트 생성·연결은 공식 `codex-rs` `create_project`/`set_thread_project`와 같은 SQL 순서와 제약을 따르고,
  스키마 게이트로 버전 차이를 막는다.
- **P4 식별은 ID가 아니라 canonical 루트 경로 + 이 PC의 authoritative ID**(§4).
- **P5 frozen contract는 "추가"로만 확장한다**: `ImportPreview`/`ImportPlan`/`RevisionRelation` 판정 semantics는 바꾸지 않는다.
  새 필드와 오버로드만 추가하고 문서에 addendum으로 남긴다.
- **P6 UI와 로직 분리**: 판단은 전부 Core(Backup/Restore/Codex)에 두고, ViewModel은 표시와 사용자 선택 수집만 한다. 테스트 가능성을 확보한다.

---

## 3. 전체 구조

### 3.1 계층과 신규/변경 컴포넌트

```text
App (WPF, MVVM)
 ├─ ImportWorkspaceViewModel          [신규] 가져오기 화면 전체 상태 머신
 │   ├─ ImportProjectNodeViewModel    [신규] 트리 프로젝트 노드(3상태 체크 + 작업 폴더 편집)
 │   ├─ ImportConversationNodeViewModel [신규] 트리 대화 노드(배지 + 체크)
 │   ├─ ImportDetailViewModel         [신규] 오른쪽 상세 + 내용 미리보기(9_2b)
 │   └─ ImportResultViewModel         [신규] 결과 화면
 ├─ ImportHistoryViewModel            [신규, 9_4] 가져오기 기록/되돌리기
 └─ MainViewModel                     [변경] 가져오기 진입/복귀, 결과 강조 표시만 담당(기존 Import 코드는 Workspace로 이관)

Backup
 ├─ ImportPreviewBuilder              [변경] KnownProjects 기반 매핑, LocalLocation 채움
 ├─ ProjectPathMapper → ProjectTargetResolver [변경/확장] 목적지 판정(연결/생성/기타)
 ├─ ImportSelection                   [신규] 사용자 선택 + 의존성 closure 계산(순수 로직)
 ├─ ImportPlanBuilder                 [변경] 선택/목적지 결정을 반영하는 오버로드(기존 시그니처 유지)
 ├─ ImportPlanPreflightValidator      [변경] 목적지 결정 재검증(생성 대상 루트 충돌 등)
 └─ BackupTranscriptSource            [신규, 9_2b] backup entry를 스트림으로 읽는 transcript 입력

Restore
 ├─ RestoreOperationPlanner           [변경] PlannedProjectCreate/PlannedThreadProjectLink 생성, authoritative ID로만 연결
 ├─ StateDatabaseWriter               [변경] CreateProject / LinkThreadProject 추가
 ├─ SchemaCompatibilityChecker        [변경] projects/project_roots/project_idempotency_keys 게이트
 ├─ RestoreValidator                  [변경] 프로젝트 생성/연결 사후 검증
 ├─ RestoreTransactionJournal         [변경] Apply 직후 target 해시 기록(되돌리기 전제조건)
 ├─ ImportUndoService                 [신규, 9_4] 성공한 Apply 되돌리기
 └─ SnapshotRetentionService          [신규, 9_4] Snapshot 목록/정리

Codex
 ├─ ProjectDirectoryBuilder           [신규] "이 PC에 등록된 모든 프로젝트" + ID 통합(§4)
 ├─ CodexCatalogBuilder               [변경] KnownProjects/ProjectDirectory 노출, 트리 그룹 중복(결함 D) 제거
 └─ ConversationTranscriptBuilder     [변경, 9_2b] 파일 경로 대신 IRolloutContentSource로도 동작

Domain
 └─ 신규 레코드: KnownProject, ProjectDirectory, ProjectTarget, ConversationLocalLocation, ImportUserChoices …(§5)
```

의존 방향 `App → Restore → Backup → Codex → Domain`은 유지한다.

### 3.2 데이터 흐름

```text
[백업 파일 선택]
   │  (read-only)
   ▼
BackupValidator ──실패──▶ Workspace.Failed(원인 + 해결 방법)
   │
   ▼
ImportPreviewBuilder(backup, localCatalog{Projects, ProjectDirectory})
   │  → ImportPreview { 대화별 Relation/Action/LocalLocation, 프로젝트별 ProjectTarget 제안 }
   ▼
ImportWorkspaceViewModel  ◀── 사용자: 체크 변경 / 폴더 선택 / 새 프로젝트 이름 편집
   │  ImportUserChoices(선택 집합, 프로젝트별 목적지 결정)
   │  ImportSelection.Compute(preview, choices) → 요약(개수, 기타 대화 행 수, 생성될 프로젝트, 차단 사유)
   │        ※ 체크를 바꿀 때마다 즉시 재계산한다(순수 메모리 연산, 파일 I/O 없음)
   ▼
[N개 가져오기] 클릭
   │  ImportPlanBuilder.Build(preview, backupPath, choices)   ← 이 시점에 한 번만 backup 해시
   │  → ImportPlan(v2 addendum: UserExcluded, ProjectTarget, ProjectCreates, ThreadLinks)
   ▼
확인 대화상자(적용 요약)
   ▼
RestoreExecutor.Apply(plan)   (Codex 종료 확인 → fresh preflight → Operation Plan → Snapshot → write → validate)
   ▼
ImportResultViewModel(결과 + 어디로 들어갔는지 + 되돌리기 가능 여부)
   ▼
[목록에서 보기] → MainViewModel 새로고침 + 해당 대화 선택/강조
```

체크박스를 바꿀 때마다 Plan을 만들면 안 된다. `ImportPlanBuilder`가 backup 전체를 스트리밍 해시하기 때문이다
(128MB 백업이면 매번 수백 ms). Plan은 가져오기 버튼을 누를 때 한 번만 만든다. 그 전의 요약은 `ImportSelection`이
메모리에서 계산한다.

---

## 4. 핵심 개념: 프로젝트 식별(Project Identity)

결함 A·C·D의 공통 원인은 "프로젝트가 무엇인가"를 정의하지 않은 채 카탈로그 그룹 ID를 그대로 썼다는 점이다.

### 4.1 이 PC의 프로젝트 정보 출처(실측)

| 출처 | 내용 | 이 PC 실측 |
|---|---|---|
| `state_5.sqlite projects` + `project_roots` | 공식 코어 프로젝트. `threads.project_id`가 **외래키로 참조하는 유일한 대상** | 46개, 루트 53개 |
| global-state `local-projects` | Desktop 레거시 프로젝트(레거시 ID) | 46개 |
| global-state `app-server-project-id-by-legacy-project-id-by-host["local:<home>"]` | 레거시 ID → SQLite ID 매핑(Desktop이 migration 중에 기록) | 46개 전부 매핑됨 |
| global-state `thread-project-assignments` | thread → **레거시** 프로젝트 ID | 72건, 26개 프로젝트, 전부 레거시 ID |
| `threads.project_id` | thread → SQLite 프로젝트 ID | 416행 중 0개 |

### 4.2 정의

- **KnownProject**: 이 PC에 등록된 프로젝트 하나. 대화 유무와 관계없다.
  - `DbProjectId`: SQLite `projects.id`. 없으면 null(레거시만 있고 DB로 migration되지 않은 프로젝트).
  - `LegacyProjectIds`: 이 프로젝트를 가리키는 global-state 레거시 ID 집합(매핑 기반).
  - `Name`, `Roots`(원본 표기 + canonical), `RootsExistOnDisk`.
- **통합 규칙**:
  1. DB 프로젝트를 기준으로 KnownProject를 만든다.
  2. 레거시 ID는 매핑 테이블로 DB 프로젝트에 합친다.
  3. 매핑이 없는 레거시 프로젝트는 canonical 루트가 같은 DB 프로젝트가 **정확히 하나**일 때만 합친다.
     그렇지 않으면 레거시 전용 KnownProject(`DbProjectId=null`)로 둔다.
- **연결 가능한 ID(authoritative link id)**: `threads.project_id`에 쓸 수 있는 값은 **오직 `DbProjectId`**다.
  `DbProjectId`가 없는 프로젝트로는 "연결"하지 않는다. 9_5부터는 "생성" 대상이 된다. 결함 C의 근본 수정이다.
- **ProjectDirectory**: KnownProject 목록 + 조회 인덱스(canonical 루트 → KnownProject, 레거시 ID → KnownProject, DB ID → KnownProject).
  같은 canonical 루트가 여러 DB 프로젝트에 걸리면 **모호(Ambiguous)**로 표시하고 자동 연결하지 않는다. 사용자에게 선택을 맡긴다.

### 4.3 카탈로그 트리 그룹 키 변경(결함 D)

`CodexCatalog.Projects`의 그룹 키를 "대화가 가리키는 원시 ID"에서 **KnownProject 기준 키**로 바꾼다.
대화가 레거시 ID로 배정돼 있어도 매핑되는 DB 프로젝트와 같은 그룹에 모인다. 그룹 `ProjectId`는
`DbProjectId ?? 레거시 ID`로 둔다. 기존 ViewModel이 쓰는 속성 이름은 바꾸지 않는다.
(판정 로직 `CodexProjectResolver`의 authority 규칙은 그대로다. 결과 ID를 KnownProject로 정규화하는 단계만 추가한다.)

### 4.4 9_1a 구현으로 확정된 사항 (2026-09-30 점검)

- `ProjectDirectory`는 record가 아니라 sealed class다(생성자에서 만든 조회 인덱스를 `with` 복사로 어긋나게 하지 않기 위함).
  `FindByKey`, `KnownProject.PrimaryId`(`DbProjectId ?? 레거시 원시 ID`), `IsLegacyOnly`가 추가됐다.
- 카탈로그 그룹의 `ProjectEntry.ProjectId`는 `KnownProject.PrimaryId`다(`"legacy:"` 접두 Key는 밖으로 내보내지 않는다).
- 보강 규칙: 존재하지 않는 DB ID를 가리키는 매핑은 "매핑 없음"으로 취급해 루트 규칙으로 판단한다. `local-projects`에 없는
  레거시 ID라도 매핑 대상 DB가 실존하면 그 프로젝트의 레거시 ID로 등록한다. 레거시 ID 문자열이 DB ID와 같으면 같은 프로젝트다.
- **ID는 PC마다 다르다(원칙).** `ConversationEntry.Project.ProjectId`(원시 배정)와 그룹 `ProjectEntry.ProjectId`는 다를 수 있다.
  Export manifest에서 `projects[].projectId`(그룹 ID)와 대화의 `resolvedProjectId`(원시)가 다를 수 있다. 이 차이를 어디서도
  연결 판정에 쓰지 않는다. 대상 PC에서의 연결은 **항상 루트 경로 → 대상 PC의 ProjectDirectory**로만 판정한다.
  `MetadataDifferences.ProjectAssignmentDiffers`(원시 ID 비교)는 frozen이라 계산은 유지하되, 화면에서는 이 값 대신
  `LocalLocation`(이 PC의 현재 위치)과 목적지를 비교해 보여준다(9_1b/9_2).
- 구버전 백업(9_1a 이전 Export, manifest 프로젝트 ID가 레거시 ID)도 루트 기준 판정이라 그대로 동작해야 한다(9_1-T9).

### 4.5 9_1b 구현으로 확정된 사항 (2026-10-01 점검)

- `ProjectPathMapping.Status`의 Phase 6 의미는 그대로다. 자동 판정은 LinkExisting일 때만 `AutoLinked`, 나머지는 `NotFound`다.
  수동 재지정은 항상 `ManuallyLinked`(= 사용자가 폴더를 골랐다)다. **실제 연결 여부의 기준은 `ProjectTarget`**(`SuggestedTarget` / `ResolvedTarget`)이다.
- `ImportPreview.LocalProjectDirectory`(init)를 둔다. 수동 재지정도 같은 기준으로 다시 판정한다. `SuggestedTarget`/`ResolvedTarget`은 nullable이다(기존 생성자 호환).
- Plan의 목적지는 Apply 시점에 fresh `ProjectDirectory`로 다시 판정한다. Kind와 LinkDbProjectId가 다르면 Operation Plan을 거부한다(쓰기 0건).
  **예외: 판정할 폴더가 없던 목적지(OriginalRootMissing)는 다시 판정하지 않는다.** 그 사이 원본 폴더가 생겨도 미리보기대로 기타 대화로 들어간다.
- **등록된 프로젝트라도 루트 폴더가 이 PC에 없으면 OriginalRootMissing(기타 대화)이다.** 예전에는 AutoLinked 후 Preflight
  `TargetPathUnavailable`로 Apply 전체가 막혔다. 사용자는 9_2 화면에서 다른 폴더를 고를 수 있다.
- Preflight: LinkExisting 대상 DB 프로젝트가 사라지면 `LocalStateChanged`, 폴더가 사라지면 `TargetPathUnavailable`이다.
- Restore 테스트 fixture(`TestCodexHomeBuilder`)는 실측 스키마의 `projects`/`project_roots`/`project_idempotency_keys`와
  `threads.project_id` 외래키를 가진다. 번들 e_sqlite3는 `foreign_keys` 기본값이 1이다. 그래서 가짜 프로젝트 ID는 즉시 FK 위반으로 드러난다.

---

## 5. 도메인 모델 변경

모든 추가는 기존 레코드에 **옵션 필드 추가** 또는 **새 레코드**로 한다. 기존 생성자와 시그니처는 유지한다.

### 5.1 Codex/Domain

```csharp
// Domain.Codex.Projects
public sealed record KnownProjectRoot(string DisplayPath, CanonicalPath Canonical, bool ExistsOnDisk);

public sealed record KnownProject(
    string Key,                                  // DbProjectId ?? "legacy:" + 첫 레거시 ID
    string? DbProjectId,
    IReadOnlySet<string> LegacyProjectIds,
    string DisplayName,
    IReadOnlyList<KnownProjectRoot> Roots,
    int ConversationCount);

public sealed record ProjectDirectory(IReadOnlyList<KnownProject> Projects)
{
    public ProjectLookupResult FindByRoot(CanonicalPath root);   // Found / Ambiguous / None
    public KnownProject? FindById(string anyId);                 // DB 또는 레거시 ID
}

// CodexCatalog에 추가(기존 생성자 유지, init 속성)
public ProjectDirectory ProjectDirectory { get; init; } = ProjectDirectory.Empty;
```

### 5.2 Import(Backup)

```csharp
// 대화의 "이 PC에서의 현재 위치"(결함 G: 이미 있음 + 어디에 있는지)
public sealed record ConversationLocalLocation(
    bool ExistsLocally,
    string? KnownProjectKey,       // null이면 기타 대화
    string? ProjectDisplayName,
    bool Archived);

// ImportConversationPreview에 추가(옵션 init 속성)
public ConversationLocalLocation? LocalLocation { get; init; }
public bool IsCompressedRollout { get; init; }        // .zst → 이어받기 불가 안내용
public IReadOnlyList<string> RequiredAncestorThreadIds { get; init; } = [];

// 프로젝트 목적지(ProjectPathMapping을 대체하지 않고 옆에 추가)
public enum ProjectTargetKind { Uncategorized, LinkExisting, CreateNew }
public enum ProjectTargetReason
{
    OriginalRootRegistered,      // 원본 경로 = 등록 프로젝트 → 자동 연결
    UserSelectedRegistered,      // 사용자가 고른 폴더 = 등록 프로젝트
    OriginalRootExistsUnregistered, // 원본 경로 폴더는 있으나 미등록 → (9_5) 생성 제안
    UserSelectedUnregistered,    // 사용자가 고른 폴더가 미등록 → (9_5) 생성
    OriginalRootMissing,         // 원본 경로 없음, 지정 안 함 → 기타 대화
    AmbiguousRoot,               // 같은 루트가 여러 프로젝트 → 사용자 선택 필요
    LegacyOnlyProject,           // 레거시 전용(DB ID 없음) → (9_5) 생성, 그 전엔 기타 대화
    CreationUnsupported,         // 스키마 게이트 실패 → 기타 대화
    NotApplicable,               // 백업의 "기타 대화" 그룹(9_5 전)
}
public sealed record ProjectTarget(
    ProjectTargetKind Kind,
    ProjectTargetReason Reason,
    string? FolderPath,                 // 목적지 폴더(연결/생성 공통)
    string? LinkDbProjectId,            // Kind=LinkExisting일 때만
    string? NewProjectName);            // Kind=CreateNew일 때만(기본: 폴더 이름)

// ImportProjectPreview에 추가
public ProjectTarget SuggestedTarget { get; init; }

// 사용자 선택(ViewModel이 모아서 Core에 넘기는 유일한 형태)
public sealed record ImportUserChoices(
    IReadOnlySet<string> IncludedThreadIds,                       // 사용자가 체크한 대화(선택 대화만. 조상은 자동)
    IReadOnlyDictionary<string, ProjectTargetDecision> ProjectDecisions,  // key: 백업 프로젝트 키
    IReadOnlySet<string> RelinkThreadIds);                        // (9_3) 연결만 바꿀 "이미 있음" 대화

public sealed record ProjectTargetDecision(string? FolderPath, string? NewProjectName, bool UseSuggestion);
```

### 5.3 ImportPlan addendum(v2)

```csharp
public enum ImportSkipReason { None, LocalAhead, UserExcluded, NotSelectedDependencyNotNeeded }

// ImportPlanConversation에 추가
public ImportSkipReason SkipReason { get; init; }
public string? TargetProjectKey { get; init; }          // 계획상 목적지(백업 프로젝트 키 → ProjectTarget)
public bool RelinkOnly { get; init; }                   // (9_3) 내용은 그대로, 연결만

// ImportPlanProject에 추가
public ProjectTarget ResolvedTarget { get; init; }      // Plan 생성 시점에 확정된 목적지

// ImportPlan에 추가
public ImportUserChoices? UserChoices { get; init; }    // null = 기존 동작(전부 포함, 제안 목적지)
```

- 기존 `ImportPlanBuilder.Build(preview, path)`는 `UserChoices=null`로 **지금과 완전히 같은 결과**를 낸다. 회귀 테스트로 고정한다.
- 기존 `TargetProjectPath`는 호환용으로 유지하고 `ResolvedTarget.FolderPath`와 같은 값을 넣는다.

### 5.4 Restore

```csharp
public sealed record PlannedProjectCreate(
    string NewProjectId,           // UUIDv7, 계획 시점에 생성(재시도 시 idempotency로 재사용)
    string Name,
    string RootPathDisplay,        // \\?\ 없는 절대경로(공식 validate_roots 형식)
    string IdempotencyKey);        // "codex-backup-manager:import:v1:<backupSha256>:<canonicalRoot>"

public sealed record PlannedThreadProjectLink(   // 기존 thread의 연결만 변경(9_3)
    string ThreadId,
    string? ExpectedCurrentProjectId,  // TOCTOU: 계획 시점 값과 같을 때만
    string NewDbProjectId,
    string? NewCwd);

// PlannedThreadInsert.ResolvedProjectId는 이제 "DbProjectId 또는 같은 트랜잭션에서 만들 NewProjectId"만 허용
// RestoreOperationPlan에 추가
public IReadOnlyList<PlannedProjectCreate> ProjectCreates { get; init; } = [];
public IReadOnlyList<PlannedThreadProjectLink> ThreadProjectLinks { get; init; } = [];

// Journal에 추가(9_4)
public IReadOnlyList<AppliedTargetFingerprint>? AppliedFingerprints { get; init; } // Apply 직후 target 파일/행 해시
public ImportRecordSummary? Summary { get; init; }   // 기록 화면용(개수, 백업 이름, 생성 프로젝트 이름. 원문 없음)
```

---

## 6. 단계별 상세 설계

### 9_0 — Codex Desktop 반영 실험 (사용자 진행, 병행)

**목적**: DB(`threads.project_id`, `projects`)에만 쓴 연결과 프로젝트가 Desktop 사이드바에 나타나는지 판정한다(결함 E).

**공식 소스로 이미 확인한 것**
- `threads.project_id`는 공식 코어 필드이고 `projects(id)` 외래키다. `set_thread_project`, `create_project`가 쓴다.
- app-server에는 `project/create`, `project/import`, `thread/metadata/update.projectId`(모두 experimental)가 있다.
- global-state 플래그(`projectsMigrated=true`, `threadAssignmentsMigrated=false`, `pendingThreadAssignmentIds`)는
  Desktop이 연결 정보를 DB로 옮기는 중임을 시사한다. Electron 소스는 비공개라 동작은 **실측으로만 확정**한다.

**절차**
1. Codex Desktop/CLI 완전 종료(작업 관리자에서 `codex*` 프로세스 없음 확인).
2. `C:\Users\User\.codex` 폴더 전체를 다른 위치로 복사(원복용).
3. Claude가 읽기 전용으로 기준값을 기록한다: global-state 플래그, 배정 수, `threads.project_id` 분포.
4. **실험 1 (9_1 이후 버전 사용)**: 테스트 대화 1개를 "대화가 있는 등록 프로젝트" 폴더로 지정해 가져온다
   (`threads.project_id = DbProjectId`만 기록됨).
5. Desktop 실행 → 그 대화가 (a) 지정 프로젝트 아래 / (b) 프로젝트 없음 / (c) 안 보임 중 어디인지 확인하고 스크린샷을 남긴다.
6. **실험 2 (9_5 개발 빌드)**: 미등록 폴더로 새 프로젝트 자동 생성 → Desktop에 새 프로젝트와 대화가 보이는지 확인한다.
7. 종료 후 Claude가 global-state 변화(플래그, pending 목록, 배정)를 읽기 전용으로 비교한다.
8. 문제가 있으면 2번 복사본으로 폴더를 통째로 원복한다.

**판정과 영향**

| 결과 | 결론 | 후속 |
|---|---|---|
| 실험1 (a), 실험2 보임 | DB가 Desktop 기준으로도 authoritative | 9_5/9_3을 DB 방식 그대로 진행. UI의 ⓘ 경고 제거 |
| (b)/(c) | Desktop이 global-state를 본다 | 9_5 앞에 **9_5a "global-state 연결 기록"** 설계 추가(`local-projects`/`thread-project-assignments` 쓰기, `.bak` 포함 Snapshot 대상 확장, 원자적 교체). **별도 승인 후 진행** |
| Desktop 재시작 후 (a)로 바뀜 | Desktop이 시작 시 migration/backfill을 한다 | 결과 화면에 "Codex를 다시 시작하면 반영됩니다" 안내 추가 |

**금지**: Claude가 복제본을 `CODEX_HOME`으로 지정해 Codex를 실행하는 방식. 복제본 DB의 `rollout_path`가 원본 절대경로라
원본이 오염된다(2026-09-30 실제 발생).

---

### 9_1 — 프로젝트 식별 정리 + 결함 A·B·C·D 수정 (Core만, UI는 문구만)

**변경**
1. `ProjectDirectoryBuilder`(Codex): §4 통합 규칙 구현. 입력은 `ProjectTableReader` 결과, `GlobalStateReader.ProjectGraph`,
   레거시 매핑(`GlobalStateReader`에 `app-server-project-id-by-legacy-project-id-by-host[local:<home>]` 읽기 추가,
   host key는 현재 Home canonical로 선택)이다. `Directory.Exists`로 루트 실존을 판정한다(read-only).
2. `CodexCatalogBuilder`: `ProjectDirectory` 노출, 트리 그룹 키 정규화(§4.3).
3. `ProjectTargetResolver`(Backup, `ProjectPathMapper` 확장): 백업 프로젝트 + 사용자 결정 → `ProjectTarget`.

   ```text
   입력: 백업 프로젝트 원본 루트들, (선택) 사용자 폴더, ProjectDirectory, 스키마 게이트 결과(9_5)
   1) 폴더 = 사용자 폴더 ?? (원본 루트 중 이 PC에 실존하는 첫 번째)
   2) 폴더가 없으면 → Uncategorized/OriginalRootMissing
   3) FindByRoot(canonical(폴더)):
        Found(p) & p.DbProjectId != null → LinkExisting(p.DbProjectId)
        Found(p) & DbProjectId == null   → LegacyOnlyProject → (9_5) CreateNew / (9_1~9_2) Uncategorized
        Ambiguous                        → AmbiguousRoot(사용자 선택 필요, 선택 전 Uncategorized)
        None                             → (9_5) CreateNew(폴더 이름) / (9_1~9_2) Uncategorized + *Unregistered 사유
   ```
4. `ImportPreviewBuilder`: `SuggestedTarget`, `LocalLocation`, `IsCompressedRollout`, `RequiredAncestorThreadIds`를 채운다.
   `ProjectPathMapping`은 호환용으로 계속 채운다(Status는 새 판정에서 파생).
5. `RestoreOperationPlanner`: `ResolveLocalProjectId`를 **fresh ProjectDirectory 기준 `DbProjectId`만** 돌려주도록 교체한다(결함 C).
   계획 시점의 목적지와 fresh 판정이 다르면(그 사이 프로젝트 삭제 등) Plan 생성을 거부한다(`LocalStateChanged`).
6. `ImportPlanPreflightValidator`: LinkExisting 대상 `DbProjectId`가 fresh ProjectDirectory에 있는지, 폴더가 실존하는지 확인한다.
7. UI 문구 최소 수정(9_2 전 임시): "원본 경로를 찾을 수 없습니다" → 사유별 문구(§9). "사용자가 지정함"은 LinkExisting일 때만 쓴다.

**결과(9_1 완료 시점)**: 등록 프로젝트(대화 0개 포함, 레거시 배정 포함)로 연결이 **실제로** 된다. 레거시 ID 때문에
Rollback되던 문제(결함 C)가 사라진다. 미등록 폴더는 기타 대화로 들어가고 그 사실이 정확히 표시된다(A안).

**테스트(RED→GREEN)**: §11.1.

---

### 9_2 — 가져오기 작업 공간(새 화면) + 사용자 선택

화면 스펙은 §7. 여기서는 로직을 다룬다.

**`ImportSelection`(Backup, 순수 로직)**

```text
Compute(preview, choices) → SelectionSummary
  included = choices.IncludedThreadIds ∩ 선택 가능 대화
  closure  = included ∪ 각 included의 RequiredAncestorThreadIds(New/IncomingAhead인 조상만 실제 쓰기 대상)
  대화별 최종 동작:
    included & New           → Import(목적지 = 소속 백업 프로젝트의 결정)
    included & IncomingAhead → Update
    included & Identical     → NoOp (9_3: RelinkThreadIds에 있으면 RelinkOnly)
    not included             → Skip(UserExcluded)
    LocalAhead/Diverged/Unverifiable → 체크 불가(항상 제외)
  차단 판정:
    closure 안에 Blocked(Unverifiable) 또는 RequiresDecision(Diverged)가 있으면 → 이 선택으로는 적용 불가(그 대화 이름을 사유로)
  요약:
    writeCount(New+Update+Relink), 새로 만들 프로젝트 목록(9_5), 기타 대화로 들어갈 대화 수, 자동 포함 조상 수, 적용 가능 여부 + 사유
```

- 기본 선택: `New`와 `IncomingAhead`는 체크, 나머지는 해제(§7.4 표).
- 조상 자동 포함은 사용자가 끌 수 없다. 트리에는 "필요한 원본 대화(자동 포함)"로 흐리게 표시한다.
- Diverged·Unverifiable 대화가 있어도 **체크하지 않으면** 나머지는 적용할 수 있다. 지금은 Apply 전체가 막힌다.
  단 closure에 걸리면 계속 막는다.

**`ImportPlanBuilder.Build(preview, path, choices)`**: `ImportSelection`과 같은 규칙으로 Plan을 만든다.
`UserChoices`와 확정된 `ResolvedTarget`을 Plan에 freeze한다. backup identity pinning(06_03)은 그대로다.

**`RestoreExecutor`**: 로직 변경은 없다. Skip이 늘고 9_5에서 ProjectCreates가 추가될 뿐이다.

**`MainViewModel` 정리**: Import 관련 상태(`CurrentImportPreview`, `_currentImportPlan`, Apply 명령 등)를
`ImportWorkspaceViewModel`로 옮긴다. MainViewModel에는 진입(`OpenImportWorkspace`), 복귀, 결과 강조만 남긴다
(현재 1525줄 → 축소, CLAUDE.md §5 "거대 Manager 금지").

---

### 9_2b — 백업 속 대화 내용 미리보기

**구조**
- `IRolloutContentSource`(Codex): `Stream OpenRaw(RolloutFileReference file)`. 구현은 `LocalFileRolloutContentSource`(기존 동작)와
  `BackupRolloutContentSource`(Backup, `BackupReader.OpenEntry(file.FullPath)`, `BackupCatalogReader`가 이미 entry 경로를 FullPath로 둔다).
- `ConversationItemParser.ParseFile*`와 `ConversationTranscriptBuilder.Build`에 `IRolloutContentSource` 오버로드를 추가한다.
  기존 오버로드는 Local 구현에 위임해 동작을 그대로 둔다. 파서는 이미 `RolloutStreamReader.ReadLines(Stream, …)`를 지원한다.
- `.jsonl.zst`도 같은 스트림 경로(기존 관리형 zstd 디코더)로 읽는다.

**동작**
- 트리에서 대화를 클릭하면 백그라운드에서 `BackupReader`로 transcript를 만든다. 선택이 바뀌면 이전 작업을 취소한다.
- `BackupReader`는 Workspace 수명 동안 한 번 열어 재사용한다. 닫을 때 Dispose한다.
  Plan 생성 시 identity 검증은 별도로 하므로 미리보기용 reader가 열려 있어도 안전성에는 영향이 없다.
- 렌더링은 메인 Viewer와 같은 `ConversationMessageViewModel` + MarkdownLite + `FlowDocumentBinding`(04_04 재활용 버그 수정 포함)을 쓴다.
- 대형 대화: 메인 Viewer와 같은 가상화 목록을 쓰고, transcript는 한 번만 만든 뒤 캐시한다(LRU 3개).
- **임시 파일을 만들지 않는다. Codex Home에는 절대 쓰지 않는다.** 로그에 대화 원문을 남기지 않는다.

---

### 9_5 — 자동 프로젝트 생성 (최종 목표)

**트리거**: `ProjectTarget.Kind = CreateNew`. 사유는 `*Unregistered`, `LegacyOnlyProject`이거나, 백업 "기타 대화" 그룹에
사용자가 폴더를 지정한 경우다.

**계획(Planner)**
1. fresh ProjectDirectory로 대상 canonical 루트를 다시 조회한다. 그 사이 등록됐으면 **LinkExisting으로 전환**한다(중복 생성 금지, TOCTOU).
2. 같은 Plan 안에서 같은 canonical 루트를 가진 CreateNew가 여러 개면 **하나로 합친다**(백업 프로젝트 여러 개가 같은 폴더로 지정된 경우).
3. `PlannedProjectCreate(NewProjectId=UUIDv7, Name, RootPathDisplay, IdempotencyKey)`를 만든다.
   - `Name`: 사용자 입력 → 비면 폴더 이름. 앞뒤 공백을 제거하고 빈 값을 거부한다(공식 `validate_name`).
   - `RootPathDisplay`: 절대경로, `\\?\` 제거, 공식 `validate_roots`처럼 절대경로만 허용한다.
   - `IdempotencyKey`: `codex-backup-manager:import:v1:<backupSha256>:<canonicalRoot>`(512바이트 이하).
4. 그 프로젝트로 가는 모든 `PlannedThreadInsert.ResolvedProjectId = NewProjectId`, `ResolvedTargetCwd = RootPathDisplay`.

**실행(StateDatabaseWriter, 기존 단일 SQLite 트랜잭션 안에서 thread INSERT보다 먼저)** — 공식 `create_project`와 같은 순서:

```sql
-- 1) 멱등성
SELECT project_id FROM project_idempotency_keys WHERE key = @key;
--    있으면 그 project를 재사용(존재하지 않으면 실패 → Rollback)
-- 2) 루트 충돌 재확인(트랜잭션 안)
SELECT project_id FROM project_roots WHERE path = @root COLLATE NOCASE;   -- canonical 비교는 C#에서 전체 목록으로 수행
-- 3) 생성
INSERT INTO projects (id, name, metadata, position, created_at_ms, updated_at_ms)
  VALUES (@id, @name, '{}', (SELECT COALESCE(MAX(position), -1) + 1 FROM projects), @now, @now);
INSERT INTO project_roots (project_id, position, path) VALUES (@id, 0, @root);
-- 4) 대화 INSERT(기존 InsertThread, project_id=@id, cwd=@root)
-- 5) 멱등성 키
INSERT INTO project_idempotency_keys (key, project_id, created_at_ms) VALUES (@key, @id, @now);
```

**스키마 게이트(`SchemaCompatibilityChecker`)**: 아래 컬럼과 타입이 정확히 있어야 한다.
`projects(id TEXT PK, name TEXT NOT NULL, metadata TEXT NOT NULL, position INTEGER NOT NULL, created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL)`,
`project_roots(project_id, position, path; PK(project_id, position))`, `project_idempotency_keys(key, project_id, created_at_ms)`,
`threads.project_id` FK. 실패하면 **생성만 포기**한다. Preview에서 `CreationUnsupported`로 표시하고 그 대화들은 기타 대화로 들어간다.
Apply 전체를 막지는 않는다.

**사후 검증(`RestoreValidator`)**: 프로젝트 행, 루트 행, idempotency 행이 존재하는지 확인한다. 각 대화의
`threads.project_id`/`cwd`를 확인한다. fresh catalog의 ProjectDirectory에 새 프로젝트가 있고 가져온 대화가 그 그룹 아래 있는지 확인한다.

**Rollback**: state DB(+WAL/SHM)는 이미 Snapshot 대상이다. 실패 시 프로젝트 행까지 함께 원복된다. 새 파일은 없다.

**Desktop**: 9_0 판정 (a)면 이것으로 끝난다. (b)/(c)면 9_5a(global-state 기록)를 거친 뒤에만 "✨ 새 프로젝트" 기능을 켠다.
그 전에는 기능 플래그로 끈 상태로 배포한다.

**채택하지 않은 대안**: app-server `project/import` 호출. 다음 이유로 쓰지 않는다.
(1) Apply 중 Codex 프로세스를 직접 띄워야 해서 ProcessGuard·"종료 상태에서만 쓰기"와 충돌한다.
(2) Snapshot 트랜잭션 밖의 쓰기가 된다.
(3) experimental API와 `codex.exe` 위치·버전에 종속된다(CLAUDE.md §20).
공식 SQL 순서를 그대로 따르고 스키마 게이트로 드리프트를 막는 방식으로 정합성을 확보한다.

---

### 9_3 — 이미 있는 대화의 프로젝트 연결 변경

지금 한글패치 대화(기타 대화에 있음)를 프로젝트로 옮기는 기능이다.

- 대상: `Identical`(또는 IncomingAhead)이고 `LocalLocation`과 목적지가 다른 대화. 트리에 "📁 연결만 변경" 체크를 따로 둔다(기본 해제).
- 계획: `PlannedThreadProjectLink(ThreadId, ExpectedCurrentProjectId, NewDbProjectId or 같은 Plan의 NewProjectId, NewCwd)`.
- 실행: `UPDATE threads SET project_id=@p, cwd=@cwd WHERE id=@id AND project_id IS @expected`. 영향 행이 1이 아니면 실패 → Rollback(TOCTOU).
- rollout 파일은 건드리지 않는다. Snapshot은 state DB만 대상이다.
- 9_0이 (b)/(c)면 9_5a와 같은 global-state 기록이 필요하다(`thread-project-assignments`에서 제거/변경, `projectless-thread-ids`에서 제거).
- 메인 화면에도 같은 기능을 노출할지는 V1에서 하지 않는다. 가져오기 흐름 안에서만 제공한다(범위 통제).

---

### 9_4 — 가져오기 기록 · 되돌리기 · Snapshot 정리

**기록 화면**: 메인 하단 `[가져오기 기록]`. Snapshot 목록(현재 Home 기준)을 날짜, 백업 이름, 결과, 개수, 새 프로젝트와 함께 보여준다.
내용은 journal `Summary`에서 읽는다. 원문은 없다.

**되돌리기(`ImportUndoService`)**
1. 대상: journal `Completed`이고 `AppliedFingerprints`가 있는 Snapshot. 9_4 이전 Apply는 fingerprint가 없어 **되돌리기 불가**로 표시한다.
2. 사전 조건: Codex 종료, 같은 Home, 그 Apply 이후 **변경이 없어야 함**. target rollout 파일과 threads 행의 현재 해시가
   `AppliedFingerprints`와 같아야 한다. 사용자가 가져온 대화를 이어 썼으면 되돌리면 그 내용이 사라지므로 **거부**하고 이유를 보여준다.
   state DB는 다른 thread 변경이 섞일 수 있다. 그래서 **DB 파일 통째 복원을 하지 않는다.** 대신 "이 Apply가 만든 행과 파일만 역연산"한다.
   - 새로 만든 rollout 파일 삭제, append한 파일은 원래 길이로 truncate(앞부분 해시 확인 후)
   - INSERT한 threads 행 DELETE, UPDATE한 필드는 Snapshot의 이전 값으로 복원
   - 새로 만든 projects/project_roots/idempotency 행 DELETE(그 프로젝트에 다른 thread가 생겼으면 프로젝트는 남기고 알림)
   - 이 역연산 자체도 **새 Snapshot → 실행 → 검증 → 실패 시 Rollback** 파이프라인으로 수행한다.
3. 되돌린 뒤 journal에 `Undone` 상태를 기록한다.

**Snapshot 정리(`SnapshotRetentionService`)**: 사용자가 기록 화면에서 선택 삭제하거나 "30일 이상 + 되돌리기 불가 항목 정리"를 한다.
**자동 삭제는 하지 않는다.** 진행 중이거나 미완료(`Prepared`/`Applying`) Snapshot은 절대 삭제하지 않는다.

---

## 7. 화면 설계(전체 UI 스펙)

### 7.1 화면 상태 머신(`ImportWorkspaceViewModel.State`)

```text
Closed ──[백업 가져오기]──▶ Opening(파일 선택)
Opening ──취소──▶ Closed
Opening ──파일 선택──▶ WaitingForCodexExit(Codex 실행 중일 때만: 안내 + [다시 확인] [닫기])
WaitingForCodexExit ──Codex 종료 확인──▶ Analyzing
Opening ──파일 선택(Codex 꺼져 있음)──▶ Analyzing(검증·비교 중, 진행률 + [취소])
Analyzing ──실패──▶ Failed(원인·해결 방법, [다른 파일 선택] [닫기])
Analyzing ──성공──▶ Editing(트리/선택/폴더/미리보기)
Editing ──[N개 가져오기]──▶ Confirming(요약 대화상자)
Confirming ──취소──▶ Editing
Confirming ──확인──▶ Applying(단계 표시 + [적용 취소])
Applying ──▶ Result(Succeeded | NothingToDo | NotReady | Cancelled | RolledBack | Critical)
Result ──[목록에서 보기]/[닫기]──▶ Closed(메인 새로고침 + 강조)
Result(NotReady: Codex 실행 중 등) ──[다시 시도]──▶ Editing(선택 유지, Plan 재생성)
Editing ──Codex가 켜짐 감지──▶ Editing(잠김: 배너 + 가져오기 비활성, Codex 종료 후 [다시 분석])
```

**Codex 실행 정책(2026-09-30 결정, "B안")**

| 기능 | Codex 실행 중일 때 |
|---|---|
| 대화 목록 보기 · 대화 내용 보기 | 허용(읽기 전용, 기존과 같음) |
| 내보내기 | 허용. 시작 전에 "Codex에서 사용 중인 대화는 내보내기 도중 바뀌면 실패할 수 있습니다"라고 안내한다(차단하지 않음) |
| **가져오기(분석 · 미리보기 · 적용 전체)** | **시작하지 않는다.** 파일을 고른 직후 Codex 실행 여부를 확인한다. 실행 중이면 분석 없이 "Codex를 종료한 뒤 [다시 확인]" 화면을 보여준다 |
| 가져오기 기록 · 되돌리기(9_4) | 목록 보기는 허용, 되돌리기 실행은 종료 필요 |

이유: 켜진 채로 분석하면 그 사이 Codex가 대화를 이어 써서, [적용] 순간 "이 PC 데이터가 바뀌었습니다"(`LocalStateChanged`)로
거절되는 헷갈리는 실패가 생긴다. 분석 전부터 종료 상태를 요구하면 이런 실패가 원천적으로 줄어든다.
`RestoreExecutor`의 이중 확인(시작 시 + 첫 쓰기 직전)은 그대로 유지한다. 화면 정책은 편의를 위한 것이고 최종 안전장치는 여전히 Core가 맡는다.

Editing 상태에서도 Codex 실행 여부를 창 활성화 시점과 5초 주기(읽기 전용 프로세스 목록)로 계속 확인한다.
도중에 Codex가 켜지면 상단 배너를 띄우고 가져오기 버튼을 비활성화한다. 분석 결과가 낡았을 수 있으므로
Codex를 종료한 뒤 **[다시 분석]**을 누르게 한다. 사용자 선택과 폴더 지정은 유지하고 분석만 다시 한다.

### 7.2 레이아웃(Editing)

<!--html
<div class="mock" role="img" aria-label="가져오기 화면 예시: 왼쪽은 백업 속 프로젝트와 대화 트리, 오른쪽은 선택한 대화의 상세와 내용 미리보기, 아래는 요약과 가져오기 버튼">
  <div class="m-top"><span class="m-link">← 목록으로</span><b>백업 가져오기</b><span class="m-grow"></span><span class="m-btn">가져오기 기록</span></div>
  <div class="m-sub">codex-backup-20260913-211909.codexbackup · 2026-09-13 21:19 · 대화 1개 · 앱 0.1.1</div>
  <div class="m-banner">Codex가 실행 중입니다. 가져오기 전에 Codex를 종료해 주세요. <span class="m-dim">(실행 중일 때만 표시)</span></div>
  <div class="m-split">
    <div class="m-tree">
      <div class="m-tools"><span class="m-search">검색</span><span class="m-btn">새 대화만</span><span class="m-btn">전체 선택</span><span class="m-btn">모두 해제</span></div>
      <div class="m-proj"><span class="m-cb part"></span><b>한글패치</b><span class="m-grow"></span><span class="m-dim">1개 중 0</span></div>
      <div class="m-folder">
        <div class="m-cap">작업 폴더</div>
        <div class="m-kv"><span>원본</span><span class="m-path">C:\Users\ProController\…\한글패치</span></div>
        <div class="m-kv"><span>이 PC</span><span class="m-input">D:\Work\한글패치</span><span class="m-btn">…</span></div>
        <div class="m-create"><span class="m-spark">새 프로젝트</span><span class="m-input sm">한글패치</span><span>로 만들어 연결합니다</span><span class="m-tag">9_5</span></div>
      </div>
      <div class="m-conv"><span class="m-cb"></span><span>b버전 한글패치 제작</span><span class="m-grow"></span><span class="m-badge b-gray">이미 있음</span></div>
      <div class="m-conv m-child"><span class="m-cb"></span><span>이 대화를 위 폴더 프로젝트로 옮기기(연결만 변경)</span><span class="m-tag">9_3</span></div>
      <div class="m-proj"><span class="m-cb on"></span><b>기타 대화</b></div>
      <div class="m-folder"><div class="m-kv"><span>작업 폴더</span><span class="m-dim">지정 안 함 → 기타 대화</span><span class="m-btn">…</span></div></div>
      <div class="m-conv"><span class="m-cb on"></span><span>다른 대화</span><span class="m-grow"></span><span class="m-badge b-green">새 대화</span></div>
    </div>
    <div class="m-detail">
      <b class="m-title">b버전 한글패치 제작</b>
      <span class="m-badge b-gray">이미 이 PC에 있음 · 내용 동일</span>
      <div class="m-kv"><span>이 PC 위치</span><span>기타 대화</span></div>
      <div class="m-kv"><span>원본 작업 폴더</span><span class="m-path">C:\Users\ProController\…</span></div>
      <div class="m-kv"><span>날짜</span><span>만든 날 2026-06-29 · 마지막 2026-09-13</span></div>
      <div class="m-cap">대화 내용 <span class="m-tag">9_2b</span></div>
      <div class="m-msg m-you">You · …</div>
      <div class="m-msg">Codex · …</div>
    </div>
  </div>
  <div class="m-foot"><div><div>가져오기: 새 대화 1 · 이어받기 0 · 연결 변경 0 · 새 프로젝트 0 · 기타 대화로 1</div><div class="m-dim">선택한 대화 중 "기타 대화"로 들어가는 것이 1개 있습니다.</div></div><span class="m-grow"></span><span class="m-primary">대화 1개 가져오기</span></div>
</div>
-->

```text
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ← 목록으로   백업 가져오기                                                [가져오기 기록] │
│ codex-backup-20260913-211909.codexbackup · 2026-09-13 21:19 · 대화 1개 · 앱 0.1.1      │
│ ⚠ Codex가 실행 중입니다. 가져오기 전에 Codex를 종료해 주세요.        (실행 중일 때만)  │
├───────────────────────────────────────────┬──────────────────────────────────────────┤
│ 🔍 검색   [새 대화만] [전체 선택] [모두 해제] │  b버전 한글패치 제작                       │
│                                           │  ● 이미 이 PC에 있음 (내용 동일)            │
│ ▼ ▣ 한글패치                      1개 중 0 │  이 PC 위치: 기타 대화                      │
│    📁 작업 폴더                             │  원본 작업 폴더: C:\Users\ProController\…   │
│       원본  C:\Users\ProController\…\한글패치 │  만든 날 2026-06-29 · 마지막 2026-09-13     │
│       이 PC [ D:\Work\한글패치        ][…] │ ────────────────────────────────────────  │
│       ✨ 새 프로젝트 "[한글패치    ]"로 만들어 │  [대화 내용]                               │
│          연결합니다                (9_5)   │   You  ...                                 │
│    ☐ b버전 한글패치 제작        [이미 있음] │   Codex ...                                │
│       └ ☐ 📁 이 대화를 위 폴더 프로젝트로   │                                            │
│            옮기기 (연결만 변경, 9_3)        │                                            │
│                                           │                                            │
│ ▼ ☑ 기타 대화                             │                                            │
│    📁 작업 폴더  (지정 안 함 → 기타 대화) […]│                                            │
│    ☑ 다른 대화                   [새 대화] │                                            │
├───────────────────────────────────────────┴──────────────────────────────────────────┤
│ 가져오기: 새 대화 1 · 이어받기 0 · 연결 변경 0   새 프로젝트 0   기타 대화로 1           │
│ ⓘ 선택한 대화 중 "기타 대화"로 들어가는 것이 1개 있습니다.            [ 대화 1개 가져오기 ] │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

- 왼쪽 트리: 메인 목록과 같은 스타일(3상태 체크, 가상화, `GridSplitter`). 프로젝트 노드 아래 첫 줄은 항상 **작업 폴더 편집 영역**이다.
- 검색: 제목 부분 일치 필터(표시만 바꾸고 선택 상태는 유지).
- 빠른 선택: `[새 대화만]` = New + IncomingAhead만 체크.
- 오른쪽: 선택한 대화의 상태 설명, 이 PC 위치, 원본 정보, 대화 내용 미리보기(9_2b).
  프로젝트 노드를 클릭하면 프로젝트 요약(대화 수, 목적지 설명)을 보여준다.
- 하단 요약: `ImportSelection` 결과를 실시간으로 반영한다. 적용할 게 없으면 버튼을 비활성화하고 사유를 표시한다.
- "상세 정보 ▼"(접힘 기본): 백업 검증 결과, 체크섬, 포맷 버전, Codex 버전, 경고 목록(개발/문제 분석용).

### 7.3 작업 폴더 편집 영역(프로젝트 행)

| 목적지 상태 | 아이콘/문구 | 컨트롤 |
|---|---|---|
| LinkExisting(원본 자동) | ✅ "이 PC의 '○○' 프로젝트에 연결됩니다" | [다른 폴더…] |
| LinkExisting(사용자 지정) | ✅ "'○○' 프로젝트에 연결됩니다(직접 지정)" | [다른 폴더…] [원래대로] |
| CreateNew(9_5) | ✨ "새 프로젝트 [이름 입력칸]을 만들어 연결합니다" | [다른 폴더…] [원래대로] |
| Unregistered(9_2까지) | ⚠ "Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다. Codex에서 이 폴더를 한 번 연 뒤 [새로고침]하면 연결됩니다" | [다른 폴더…] [새로고침] |
| OriginalRootMissing | ⚠ "원본 폴더가 이 PC에 없습니다. 폴더를 지정하지 않으면 기타 대화로 들어갑니다" | [폴더 선택…] |
| AmbiguousRoot | ⚠ "이 폴더가 여러 프로젝트에 등록돼 있습니다. 연결할 프로젝트를 고르세요" | [프로젝트 선택 ▼] |
| CreationUnsupported | ⚠ "이 Codex 버전에서는 프로젝트 자동 생성을 지원하지 않습니다. 기타 대화로 들어갑니다" | — |

- 9_0 판정 전 "연결됨/새 프로젝트"에는 ⓘ 툴팁을 붙인다: "Codex Desktop 사이드바 반영은 확인 중입니다".
- 폴더를 바꾸면 Plan을 다시 만들지 않는다. 메모리에서 목적지만 다시 계산한다(`ProjectTargetResolver`, 파일 I/O는 `Directory.Exists`뿐).

### 7.4 대화 배지와 체크 규칙

| 배지(색) | 판정 | 기본 체크 | 체크 가능 | 오른쪽 설명 |
|---|---|---|---|---|
| 새 대화(초록) | New | ☑ | O | 이 PC에 없는 대화입니다. 가져옵니다 |
| 이어받기(파랑) | IncomingAhead | ☑ | O(`.zst`면 X) | 이 PC에 있지만 백업이 더 깁니다. 뒤에 이어붙입니다 |
| 이미 있음(회색) | Identical | ☐ | 연결 변경(9_3)만 | 내용이 같습니다. 이 PC 위치: ○○ / 기타 대화 |
| 이 PC가 최신(회색) | LocalAhead | ☐ | X | 이 PC 쪽이 더 깁니다. 건너뜁니다 |
| 충돌(주황) | Diverged | ☐ | X | 양쪽이 다르게 이어졌습니다. 이번 버전에서는 가져올 수 없습니다(다른 대화는 가져올 수 있음) |
| 확인 불가(빨강) | Unverifiable | ☐ | X | 이 PC 데이터가 불완전해 비교할 수 없습니다 |
| 자동 포함(흐림) | 선택 대화의 조상 | 자동 | X | 선택한 대화에 필요한 원본 대화라 함께 가져옵니다 |

### 7.5 확인 대화상자

```text
다음 내용을 Codex에 적용합니다.

  새로 가져올 대화   3개
  이어받을 대화      1개
  연결만 바꿀 대화   1개
  새로 만들 프로젝트 1개: "한글패치" (D:\Work\한글패치)
  기타 대화로 들어갈 대화 1개

적용 전에 현재 상태의 복구 지점(Snapshot)을 만듭니다.
실패하면 자동으로 되돌립니다. Codex가 완전히 종료되어 있어야 합니다.

                                   [취소]  [적용]
```

### 7.6 결과 화면

```text
✅ 가져오기 완료 · 2026-09-30 15:20
새로 가져옴 3 · 이어받음 1 · 연결 변경 1 · 건너뜀 2 · 새 프로젝트 1

한글패치  → D:\Work\한글패치  (새로 만듦)
  ✅ b버전 한글패치 제작              연결 변경(기타 대화 → 한글패치)
기타 대화
  ✅ 다른 대화                         새로 가져옴 · 원본 폴더 없음

복구 지점 20260930-062034-…    [이 가져오기 되돌리기](9_4)
ⓘ Codex를 실행하면 가져온 대화가 표시됩니다.(9_0 결과에 따라 문구 확정)

[목록에서 보기]  [닫기]
```

- `NothingToDo`: "적용할 변경이 없었습니다. 선택한 대화는 모두 이미 이 PC에 있습니다" + 각 대화의 현재 위치 목록.
  스크린샷 상황이 이 경우다.
- `NotReady`/`RolledBack`/`Cancelled`/`Critical`: §9 문구 + 해결 방법 + (가능하면) [다시 시도].

### 7.7 메인 화면 연동
- `[목록에서 보기]`를 누르면 카탈로그를 새로고침하고, 가져온 대화들을 선택·펼침·스크롤해 3초간 강조한다.
- 메인 하단 버튼: `[백업 내보내기] [백업 가져오기] [가져오기 기록]`. 문구를 "불러오기"에서 **"가져오기"**로 통일한다.

---

## 8. 안전성 불변식 (Phase 9 전체에서 깨지면 안 됨)

1. 분석, 미리보기, 선택 변경, 폴더 변경, 기록 조회는 **Codex Home에 쓰기 0건**이다(테스트로 해시 고정).
2. 모든 쓰기는 `RestoreExecutor` 파이프라인 안에서 한다. Snapshot 없이는 쓰지 않고, 실패하면 Rollback한다.
3. `threads.project_id`에는 **실존 `projects.id`(또는 같은 트랜잭션에서 만든 id)만** 쓴다.
4. 같은 canonical 루트로 프로젝트를 **중복 생성하지 않는다**(fresh 재조회 + idempotency key + 트랜잭션 내 재확인).
5. rollout 바이트는 기존 규칙(New는 원본 그대로 복사, Update는 검증된 append) 외에는 바꾸지 않는다. 연결 변경은 rollout을 건드리지 않는다.
6. global-state(`.codex-global-state.json`)는 9_0 판정과 별도 승인 전까지 **쓰지 않는다**.
7. Codex가 실행 중이면 어떤 쓰기도 시작하지 않는다(시작 시 + 첫 mutation 직전 이중 확인, 기존 규칙).
8. 되돌리기는 "이 Apply 이후 변경 없음"이 증명될 때만 한다. DB 파일 통째 복원을 하지 않는다.
9. frozen contract(Preview/Plan/Relation)의 기존 필드 의미는 바꾸지 않는다. `UserChoices=null`이면 0.1.3과 같은 결과가 나와야 한다.
10. 로그에 대화 원문, 제목, 전체 경로를 남기지 않는다(`Redact` 규칙 유지).

---

## 9. 사용자 메시지 카탈로그

| 상황 | 문구 | 해결 방법 안내 |
|---|---|---|
| 백업 손상 | 백업 파일이 손상되었습니다(체크섬 불일치: ○○). | 원본 PC에서 다시 내보내 주세요 |
| 지원 안 하는 포맷 | 이 백업은 지원하지 않는 형식(버전 N)입니다. | 최신 버전 프로그램으로 열어 주세요 |
| Codex 실행 중(가져오기 시작 시) | 가져오기는 Codex를 종료한 상태에서만 할 수 있습니다. | Codex를 완전히 종료한 뒤 [다시 확인] |
| Codex가 도중에 켜짐 | Codex가 실행되어 분석 결과가 바뀌었을 수 있습니다. | Codex를 종료한 뒤 [다시 분석] |
| Codex 실행 중(내보내기) | Codex에서 사용 중인 대화는 내보내기 도중 바뀌면 실패할 수 있습니다. | 실패하면 Codex를 종료하고 다시 내보내기 |
| 모두 이미 있음 | 선택한 대화는 모두 이미 이 PC에 있습니다. | 위치: 목록 표시. 연결을 바꾸려면 "연결만 변경"(9_3) |
| 원본 폴더 없음 | 원본 폴더가 이 PC에 없습니다. 지정하지 않으면 기타 대화로 들어갑니다. | [폴더 선택…] |
| 미등록 폴더(9_2까지) | Codex에 등록되지 않은 폴더입니다. 지금은 기타 대화로 들어갑니다. | Codex에서 폴더를 연 뒤 [새로고침] |
| 충돌 대화 포함 | '○○'는 양쪽이 다르게 이어져 가져올 수 없습니다. 체크를 해제하면 나머지를 가져올 수 있습니다. | — |
| 필수 조상 문제 | '○○'에 필요한 원본 대화 '△△'를 확인할 수 없어 함께 가져올 수 없습니다. | — |
| 백업이 그 사이 바뀜 | 미리보기 이후 백업 파일이 바뀌었습니다. | 다시 열어 주세요 |
| 이 PC 상태가 바뀜 | 미리보기 이후 이 PC의 Codex 데이터가 바뀌었습니다. | [다시 분석] |
| 자동 복구됨 | 적용 중 문제가 생겨 이전 상태로 되돌렸습니다. 기존 데이터는 그대로입니다. | 로그 위치 안내 |
| 자동 복구 실패 | [치명] 자동 복구에 실패했습니다. 복구 지점 ○○로 수동 복구가 필요합니다. | [이전 상태로 복구] |
| 되돌리기 불가(변경됨) | 가져온 뒤 이 대화가 Codex에서 계속 사용되어 되돌릴 수 없습니다. | — |
| 되돌리기 불가(구버전) | 이 기록은 이전 버전에서 만들어져 되돌리기를 지원하지 않습니다. | — |

---

## 10. 로깅

- 레벨/형식은 기존 `FileLogger`를 따른다.
- 새 이벤트: `ImportWorkspace.Opened/Analyzed(counts)/SelectionChanged(counts only)/TargetChanged(reason enum)/Apply.Started/Finished(outcome, snapshotId)/Undo.*`.
- 경로는 `Redact.Path`, thread/project ID는 해시 12자로 남긴다. 제목과 원문은 기록하지 않는다.

---

## 11. 테스트 계획

모든 Core 테스트는 합성 fixture(`TestCodexHomeBuilder`, `tests/Fixtures`)를 쓴다. 실제 사용자 데이터는 넣지 않는다.

### 11.1 9_1
- ProjectDirectory: DB 전용 / 레거시 전용 / 매핑으로 합쳐짐 / 매핑 없이 루트로 합쳐짐 / 같은 루트에 DB 2개 → Ambiguous / 루트 실존 판정.
- 카탈로그: 레거시 배정 대화와 cwd 폴백 대화가 **한 그룹**으로 합쳐짐(결함 D RED→GREEN).
- ProjectTargetResolver: 등록(대화 0개) → LinkExisting(결함 A RED), 원본 실존·미등록 → Unregistered 사유(결함 B), 원본 없음, 사용자 지정 각각.
- Planner/Writer: 레거시 ID 프로젝트로 지정 → `threads.project_id = DbProjectId`, **Rollback 없음**(결함 C RED: 현재 RolledBack 재현).
- Preflight: LinkExisting 대상 프로젝트가 Plan 이후 삭제됨 → LocalStateChanged.
- 회귀: 기존 531건 전부 GREEN.

### 11.2 9_2
- ImportSelection: 기본 체크, 제외 → Skip(UserExcluded), 조상 자동 포함, Diverged 제외 시 적용 가능, closure에 Blocked면 불가, 요약 개수.
- ImportPlanBuilder: `UserChoices=null`이면 기존 결과와 동일(스냅샷 비교), 선택 반영, identity pinning 유지.
- App: 3상태 체크, 검색 필터가 선택을 유지하는지, 0개 비활성 + 사유, 폴더 변경 후 요약 갱신, 상태 머신 전이, Codex 실행 중 배너,
  NothingToDo 결과 문구(스크린샷 시나리오 재현 테스트).

### 11.3 9_2b
- BackupRolloutContentSource로 만든 transcript = 같은 파일의 로컬 transcript(메시지 수와 해시 동일), 세그먼트/분기/`.zst` 포함.
- 미리보기 중 Codex Home 쓰기 0건, temp 파일 0건. 선택을 빠르게 바꿀 때 취소 처리. FlowDocument 재활용 스트레스(기존 테스트 패턴).

### 11.4 9_5
- 미등록 폴더 → 프로젝트 생성 + 연결 + cwd, 공식 SQL 불변식(position=MAX+1, 루트 position 0, idempotency 행).
- 같은 백업 재가져오기 → 프로젝트 재사용(중복 0). 계획 이후 누군가 같은 루트를 등록 → LinkExisting으로 전환.
- 같은 폴더로 지정된 백업 프로젝트 2개 → 생성 1회. fault injection(생성 직후, thread INSERT 후, 커밋 직후) → Rollback 후 프로젝트 행 없음.
- 스키마 게이트 실패 → 생성 생략 + 기타 대화 + 경고, Apply 자체는 성공.
- 크래시 시뮬레이션(CrashSim) 1건: 생성 트랜잭션 중 강제 종료 → 복구.

### 11.5 9_3 / 9_4
- 연결 변경: expected project 불일치 → Rollback, rollout 파일 무변경.
- 되돌리기: 변경 없음 → 성공(행/파일 원복, 다른 thread 무영향), 가져온 대화를 이어 씀 → 거부, fingerprint 없는 구버전 기록 → 불가 표시,
  되돌리기 도중 실패 → 되돌리기의 Rollback.
- Snapshot 정리: Prepared/Applying은 삭제 안 함.

### 11.6 E2E(스크래치패드 복제본, 원본 해시 불변 확인)
기존 PC A→B 하네스(42항목)에 다음 케이스를 추가한다: Case 0~4(재지정) + 선택 일부 제외 + 새 프로젝트 생성 + 연결 변경 + 되돌리기.
Codex app-server 검증은 **복제본 `rollout_path`를 복제본 경로로 먼저 바꾼 뒤** `thread/list`/`thread/read`만 쓴다(resume 금지).

### 11.7 테스트 환경(결함 I)
`MainViewModelApplyTests`는 `RestoreExecutor` 호출에 ProcessGuard lister를 주입받도록 바꾼다(internal 경로 노출).
`CrashRecoveryIntegrationTests`의 자식 프로세스에는 `--process-guard=none` 테스트 전용 인자를 추가한다.
실제 ProcessGuard 동작은 `CodexProcessGuardTests`가 계속 검증한다.

---

## 12. 문서 · 버전 · 인계

- 버전: 9_2 완료 시 `0.2.0`(UI와 기능 변화). 이후 단계마다 minor/patch를 올린다.
- 문서 갱신
  - `docs/import-preview-phase6.md`: **Phase 9 addendum**(ImportPreview/ImportPlan 추가 필드, `UserChoices`, `ProjectTarget` semantics, frozen 범위 명시).
  - `docs/safe-restore-phase7.md`: §13 Phase 9(프로젝트 생성/연결 변경/되돌리기, 새 불변식).
  - `docs/codex-storage-format.md`: §5 프로젝트 3중 구조에 레거시↔DB 매핑, 외래키, 9_0 실측 결과.
  - `docs/project-status-and-handoff.md`: 단계마다 갱신(CLAUDE.md §39).
  - `README.md`, `docs/dist-readme.txt`: 사용 방법(가져오기 화면) 갱신.
- 커밋: 단계별로 사용자가 직접 한다(CLAUDE.md §32).

---

## 13. 위험 · 미해결 질문

| 위험/질문 | 대응 |
|---|---|
| Desktop이 DB 연결을 무시할 수 있음(결함 E) | 9_0으로 판정. (b)/(c)면 9_5a 별도 설계·승인 |
| Codex 버전 업으로 projects 스키마 변경 | 스키마 게이트 → 생성만 포기(기타 대화) |
| Desktop migration(`pendingThreadAssignmentIds`)과 우리 쓰기의 경합 | Codex 종료 상태에서만 쓰기. 9_0 실험 7단계에서 migration 후 상태 확인 |
| 같은 폴더의 중복 프로젝트(Ambiguous)가 실제로 있음(실측 2건) | 자동 연결하지 않고 사용자 선택 |
| 대형 백업에서 분석 시간 | 기존 스트리밍 유지, 분석 중 진행률과 취소. 선택 변경은 메모리 연산만 |
| 되돌리기의 역연산 정확성 | fingerprint 기반 사전 검증 + 역연산 자체를 Snapshot/Rollback으로 보호 |
| 레거시 전용 프로젝트(DB 미migration)가 생길 수 있음 | 연결하지 않고 CreateNew(9_5)/기타 대화로 처리 |

---

## 14. 부록: 근거

### 14.1 실측(이 PC, 읽기 전용)
- `threads` 416행 중 `project_id` 채워진 행 0개. `projects` 46개, `project_roots` 53개.
- global-state: `projectsMigrated=true`, `threadAssignmentsMigrated=false`, `pendingThreadAssignmentIds` 12개,
  `thread-project-assignments` 72건(프로젝트 26개, 전부 레거시 ID, 전부 DB ID로 매핑됨).
- 카탈로그 배정 출처: GlobalStateAssignment 72 · CwdFallback 42 · Unassigned 12. 같은 루트를 공유하는 카탈로그 프로젝트 묶음 2건(→ 정정: Codex 자체의 다중 등록, Ambiguous. 레거시/DB 중복은 별도 1건).
- `threads.project_id REFERENCES projects(id) ON DELETE SET NULL`.

### 14.2 복제본 E2E
- 기본 시나리오 42항목 PASS(New 3 + IncomingAhead 1, 내용/바이트 동일, 재가져오기 NoOp, stale plan 거부, app-server 인식·resume).
- Case 0: 원본 경로가 실존하지만 대화 0개 프로젝트 → NotFound, 기타 대화.
- Case 1: 미등록 폴더 지정 → 무시됨.
- Case 2: 대화 있는 DB ID 프로젝트 → 정상.
- Case 3: 등록됐지만 대화 0개 프로젝트 지정 → 무시됨.
- **Case 4: 레거시 ID 프로젝트 지정 → RolledBack**. *(9_1a 이후: 그룹 ID가 DB ID로 정규화되어 Succeeded, `threads.project_id`=실존 DB ID 확인)*

### 14.3 공식 소스(`openai/codex` `bcd6d9ab`)
- `codex-rs/state/src/runtime/projects.rs` `create_project`: `BEGIN IMMEDIATE` → idempotency 조회 → thread 존재 확인 → `Uuid::now_v7()` →
  `MAX(position)+1` → `INSERT projects(metadata JSON)` → `replace_roots`(position 순서) → `UPDATE threads SET project_id` → `INSERT project_idempotency_keys`.
- `codex-rs/app-server/src/request_processors/projects.rs`: `validate_name`(trim, 비어 있으면 거부), `validate_idempotency_key`(≤512바이트),
  `validate_roots`(절대경로, 논리/canonical 중복 거부, `path.display()` 저장).
- `app-server-protocol/src/protocol/v2/project.rs`: `ProjectCreateParams{name, roots, metadata?, idempotencyKey}`, `ProjectImportParams{…, threads?}`.
- `app-server-protocol/src/protocol/v2/thread.rs`: `ThreadMetadataUpdateParams.projectId`(experimental: 빈 문자열이면 해제, 기존 프로젝트 ID면 배정).
