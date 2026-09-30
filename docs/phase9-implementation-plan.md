# Phase 9 구현 계획 · 체크리스트

> 설계 원본: [`docs/import-ux-redesign-phase9.md`](./import-ux-redesign-phase9.md) (HTML: `import-ux-redesign-phase9.html`)
> 이 문서가 **구현 진행 상태의 기준**이다. HTML(`phase9-implementation-plan.html`)은 이 문서에서 생성한다.
> 최종 갱신: 2026-09-30 · 관리 대화방

---

## 0. 이 문서 사용법

### 0.1 체크 규칙

- `[ ]` 미완료 · `[x]` **관리 대화방이 점검해서 확인한 완료**.
- 구현 대화방은 이 문서의 체크박스를 **직접 바꾸지 않는다.** 완료 보고만 한다. 체크는 관리 대화방이 코드와 테스트를
  직접 확인한 뒤에만 한다. 보고만 있고 확인하지 못한 항목은 체크하지 않고 §13 점검 기록에 사유를 남긴다.
- 체크를 바꾸면 HTML을 다시 생성한다: `python scripts/render-docs.py docs/phase9-implementation-plan.md`.
- 구현 대화방에 보낸 프롬프트는 `docs/phase9-prompts/<단계>.md`에 원문 그대로 남긴다.
- 작업 ID(`9_1-03` 등)는 바꾸지 않는다. 작업이 추가되면 다음 번호를 붙이고, 없어진 작업은 ~~취소선~~과 사유를 남긴다.

### 0.2 단계 상태 표기

| 표기 | 의미 |
|---|---|
| ⬜ 대기 | 아직 프롬프트를 내보내지 않음 |
| 🟦 구현 중 | 구현 대화방에 프롬프트 전달됨 |
| 🟨 점검 중 | 구현 보고를 받아 관리 대화방이 확인 중 |
| 🟩 완료 | 모든 체크 완료 + 사용자 커밋 |
| ⛔ 보류 | 선행 조건 미충족 또는 사용자 결정 대기 |

---

## 1. 워크플로

<!--html
<div class="flow" role="img" aria-label="관리 대화방이 프롬프트를 쓰고, 사용자가 구현 대화방에 붙여넣어 구현한 뒤, 보고서를 관리 대화방이 점검하는 순환">
  <div class="room"><div class="room-h">관리 대화방</div><ol><li><b>①</b> 다음 단계 프롬프트 작성</li><li><b>④</b> 코드·테스트·실측 점검 → 체크 갱신</li><li><b>⑤</b> 보완 프롬프트 또는 다음 단계로</li></ol></div>
  <div class="flow-links"><div class="flow-arrow">프롬프트 붙여넣기 <span>→</span></div><div class="flow-arrow back"><span>←</span> 구현 보고서</div></div>
  <div class="room"><div class="room-h">구현 대화방</div><ol><li><b>②</b> 구현 · 테스트 · 보고서 작성</li><li><b>③</b> 커밋은 사용자가 직접</li></ol></div>
</div>
-->

```text
 ┌─────────────── 관리 대화방 ───────────────┐          ┌──────── 구현 대화방 ────────┐
 │ ① 다음 단계 프롬프트 작성                  │ ──붙여넣기──▶ │ ② 구현 · 테스트 · 보고서 작성 │
 │ ④ 점검(코드/테스트/실측) → 체크 갱신        │ ◀──보고서──── │ ③ (커밋은 사용자가 직접)      │
 │ ⑤ 수정 필요 → 보완 프롬프트 / 통과 → 다음    │          └─────────────────────────────┘
 └───────────────────────────────────────────┘
```

1. 관리 대화방이 이 문서의 **한 단계(또는 그 일부)**만 담은 프롬프트를 만든다. 프롬프트는 자기완결적이어야 한다.
   구현 대화방은 이 대화를 모른다.
2. 사용자가 프롬프트를 구현 대화방에 그대로 붙여넣는다.
3. 구현 대화방은 §2 공통 규칙을 지키며 구현하고, §1.1 형식으로 보고한다.
4. 사용자가 보고서를 관리 대화방에 붙여넣는다. 필요하면 커밋도 한다.
5. 관리 대화방은 실제 코드, diff, 테스트를 직접 확인하고 체크박스를 갱신한다. 부족하면 보완 프롬프트를 만든다.

### 1.1 구현 대화방 보고서 형식 (프롬프트마다 요구)

```text
## 구현 보고 — <단계> (<작업 ID 목록>)
1. 완료한 작업 ID와 한 줄 요약
2. 변경 파일 목록(신규/수정/삭제)
3. 추가/수정한 테스트 이름과 RED→GREEN 여부(RED를 실제로 봤는지)
4. 실행한 명령과 결과(전체 테스트 통과 수/실패 수)
5. 원본 .codex 무변경 확인 방법과 결과(해당 시)
6. 하지 않은 것 / 범위 밖으로 남긴 것 / 설계와 다르게 한 것과 이유
7. 남은 위험 · 관리 대화방에 확인받고 싶은 질문
```

---

## 2. 모든 단계 공통 규칙 (프롬프트에 항상 포함)

- **읽기 순서**: 시작 전에 `CLAUDE.md` → `docs/project-status-and-handoff.md` → `docs/import-ux-redesign-phase9.md`의 해당 절을 읽는다.
- **원본 보호**: 실제 `C:\Users\User\.codex`에는 절대 쓰지 않는다. 자동화 테스트는 합성 fixture만 쓴다. 실제 데이터 검증은
  읽기 전용이나 스크래치 복제본으로만 하고, 작업 전후 원본 해시를 비교한다
  (`state_5.sqlite`, `session_index.jsonl`, `.codex-global-state.json`, `config.toml`).
- **복제본 주의**: 복제본을 `CODEX_HOME`으로 지정해 Codex 바이너리를 실행하지 않는다. 복제본 DB의 `rollout_path`가 원본을 가리킨다.
  꼭 필요하면 `rollout_path`를 복제본 경로로 먼저 바꾸고 `thread/list`, `thread/read`만 쓴다.
- **TDD**: 결함 수정은 RED를 실제로 확인한 뒤 GREEN으로 만든다. 새 기능은 테스트와 함께 만든다.
- **범위 통제**: 프롬프트에 적힌 작업 ID만 한다. 관련 없는 리팩터링이나 기능 추가는 하지 않는다.
- **frozen contract**: `ImportPreview`/`ImportPlan`/`RevisionRelation`의 기존 필드 의미는 바꾸지 않는다. 추가만 한다.
  `UserChoices=null` 경로는 기존 결과와 같아야 한다.
- **쓰기 경로**: Codex Home 쓰기는 `RestoreExecutor` 파이프라인(Snapshot → write → validate → Rollback) 안에서만 한다.
- **global-state 금지**: `.codex-global-state.json` 쓰기는 9_5a 승인 전까지 금지다.
- **커밋/푸시 금지**: 사용자가 직접 한다.
- **보고**: 한국어로, §1.1 형식을 따른다. 테스트하지 않은 것을 "동작한다"고 쓰지 않는다.
- **빌드 기준**: `dotnet build -c Release` 경고와 오류 0, `dotnet test CodexBackupManager.sln -c Release` 전체 통과.
  단 Codex가 실행 중이면 호스트 의존 테스트가 실패할 수 있다(9_1-01 전까지).

---

## 3. 진행 현황 요약

| 단계 | 내용 | 상태 | 선행 조건 | 담당 |
|---|---|---|---|---|
| 9_P | 준비 작업 | 🟨 점검 중 | — | 사용자 + 관리 |
| 9_1 | 프로젝트 식별 정리 · 결함 A~D, I 수정 | 🟦 구현 중 | — | 구현 |
| 9_0-A | Desktop 반영 실험 1 (DB 연결만) | ⛔ 보류 | 9_1 | 사용자 + 관리 |
| 9_2 | 가져오기 작업 공간(새 화면) + 사용자 선택 | ⬜ 대기 | 9_1 | 구현 |
| 9_2b | 백업 속 대화 내용 미리보기 | ⬜ 대기 | 9_2 | 구현 |
| 9_5 | 자동 프로젝트 생성(최종 목표) | ⛔ 보류 | 9_2, 9_0-A 판정 | 구현 |
| 9_5a | (조건부) global-state 연결 기록 | ⛔ 보류 | 9_0 판정 (b)/(c)일 때만 + 별도 승인 | 관리 → 구현 |
| 9_0-B | Desktop 반영 실험 2 (자동 생성 프로젝트) | ⛔ 보류 | 9_5 개발 빌드 | 사용자 + 관리 |
| 9_3 | 이미 있는 대화의 프로젝트 연결 변경 | ⬜ 대기 | 9_5 | 구현 |
| 9_4 | 가져오기 기록 · 되돌리기 · Snapshot 정리 | ⬜ 대기 | 9_2 (9_5 이후 권장) | 구현 |
| 9_F | 마무리(문서 · 버전 0.2.0 · E2E · 릴리스 준비) | ⬜ 대기 | 전 단계 | 구현 + 관리 |

---

## 4. 9_P — 준비 작업

**목표**: 구현을 시작하기 전에 기준 상태를 정리한다.

- [x] **9_P-01** 설계 문서 3종(`import-ux-redesign-phase9.md/.html`, `phase9-implementation-plan.md/.html`)과 `scripts/render-docs.py`를 사용자가 커밋 — `a0eab0c`
- [x] **9_P-02** 관리 대화방 검증 중 원본 rollout 3개에 덧붙은 `thread_settings_applied` 이벤트 처리 결정(복원 / 유지). 복원이면 관리 대화방이 사본을 백업한 뒤 원래 길이로 truncate하고 해시 확인 — **복원 완료**(2026-09-30): 3개 모두 사고 전 사본과 SHA-256 일치, 복원 전 파일은 `%LOCALAPPDATA%\CodexBackupManager\manual-restore-20260930\` 보관, 핵심 파일 4종 해시 불변
- [x] **9_P-03** 기준선 기록: 현재 `main` 커밋, 전체 테스트 531건 통과(Codex 종료 상태), 앱 버전 0.1.3 — `54904e8`, 531/531(2026-09-30, Codex 종료 후 재실행), `Directory.Build.props` 0.1.3 확인
- [ ] **9_P-04** 사용자가 이후 확인에 쓸 앱을 최신 빌드(0.1.3 이상)로 교체(현재 실행 중인 앱은 0.1.1)

**완료 조건**: 4개 모두 체크.

---

## 5. 9_1 — 프로젝트 식별 정리 · 결함 A·B·C·D·I 수정

**목표**: 이 PC에 등록된 모든 프로젝트를 하나의 식별 규칙(`ProjectDirectory`)으로 다룬다. 폴더 재지정과 자동 연결이
**실제로** 반영되게 하고, 레거시 ID 때문에 Rollback되는 문제를 없앤다. UI는 문구만 고친다.
**설계 참조**: 설계 §1.2, §4, §5.1, §6 "9_1".
**범위 밖**: 새 화면, 사용자 선택, 프로젝트 생성, global-state 쓰기.
**주요 파일(예상)**: `Codex/Projects/ProjectDirectoryBuilder.cs`(신규), `Codex/Inspection/GlobalStateReader.cs`,
`Codex/Catalog/CodexCatalogBuilder.cs`, `Domain/Codex/Projects/*`, `Backup/Import/ProjectPathMapper.cs` → `ProjectTargetResolver.cs`,
`Backup/Import/ImportPreviewBuilder.cs`, `Backup/Import/ImportPlanPreflightValidator.cs`, `Restore/RestoreOperationPlanner.cs`,
`App/ViewModels/ImportPreviewViewModel.cs`, 관련 테스트.

> **프롬프트 분할**: 9_1a = 9_1-01~05 + T1, T2, T7(부분) (`docs/phase9-prompts/9_1a.md`, **점검 완료**) · 9_1b = 9_1-06~11 + T3~T6, T9, T7 (`docs/phase9-prompts/9_1b.md`, **점검 완료**) · 후속 9_1-12~14는 9_2-1 프롬프트에 포함 · 9_1-T8(복제본 E2E)은 관리 대화방이 점검 때 수행.

### 5.1 작업

- [x] **9_1-01** (결함 I) 호스트의 Codex 실행 여부에 의존하는 테스트 7건 격리. `MainViewModelApplyTests`는 ProcessGuard lister 주입, `CrashRecoveryIntegrationTests` 자식 프로세스는 테스트 전용 `--process-guard=none`. 실제 가드는 `CodexProcessGuardTests`로 계속 검증
- [x] **9_1-02** `GlobalStateReader`에 `app-server-project-id-by-legacy-project-id-by-host["local:<현재 Home>"]` 레거시→DB 매핑 읽기 추가(host key는 현재 Home canonical로 선택)
- [x] **9_1-03** Domain에 `KnownProject`, `KnownProjectRoot`, `ProjectDirectory`, `ProjectLookupResult(Found/Ambiguous/None)` 추가
- [x] **9_1-04** `ProjectDirectoryBuilder`: DB 프로젝트 기준 생성 → 레거시 ID를 매핑으로 합침 → 매핑 없으면 canonical 루트가 같은 DB 프로젝트가 정확히 1개일 때만 합침 → 루트 실존(`Directory.Exists`) 판정
- [x] **9_1-05** `CodexCatalog.ProjectDirectory` 노출(기존 생성자 유지). 트리 그룹 키를 KnownProject 기준으로 정규화해 같은 폴더의 중복 그룹 제거(결함 D). `CodexProjectResolver`의 authority 규칙은 바꾸지 않음
- [x] **9_1-06** `ProjectTarget`/`ProjectTargetKind`/`ProjectTargetReason` 추가, `ProjectTargetResolver` 구현(설계 §6 9_1 의사코드). 9_1에서는 CreateNew 대상도 `Uncategorized` + `*Unregistered`/`LegacyOnlyProject` 사유
- [x] **9_1-07** `ImportPreviewBuilder`가 `SuggestedTarget`, `LocalLocation`, `IsCompressedRollout`, `RequiredAncestorThreadIds`를 채움. `ProjectPathMapping`은 호환용으로 계속 채우되 새 판정에서 파생
- [x] **9_1-08** (결함 C) `RestoreOperationPlanner.ResolveLocalProjectId`를 fresh `ProjectDirectory` 기준 **`DbProjectId`만** 반환하도록 교체. 계획 목적지와 fresh 판정이 다르면 Plan 거부(`LocalStateChanged`)
- [x] **9_1-09** `ImportPlanPreflightValidator`: LinkExisting 대상 `DbProjectId` 존재와 폴더 실존 확인
- [x] **9_1-10** 임시 UI 문구 수정: 사유별 문구(설계 §9). "사용자가 지정함 → …"은 실제 LinkExisting일 때만. 미등록 폴더는 "지금은 기타 대화로 들어갑니다" 명시
- [x] **9_1-11** (9_1a 점검에서 추가) `ProjectDirectory`/`ProjectDirectoryBuilder` 방어: 비정상 데이터(ID 충돌 등)로 생성이 실패해도 카탈로그 전체가 실패하지 않게 한다(경고 + 문제 항목 제외), 테스트 포함
- [x] **9_1-12** (9_1b 점검 후속) `RestoreValidator`: New Import에서 `ResolvedProjectId`가 null이면 실제 행의 `project_id`도 null인지, 값이 있으면 `cwd`가 `ResolvedTargetCwd`와 같은지 사후 검증
- [x] **9_1-13** (9_1b 점검 후속) 더 이상 제품 코드에서 쓰지 않는 `ProjectPathMapper`와 그 테스트 제거(고유한 검증은 `ProjectTargetResolverTests`로 이전)
- [x] **9_1-14** (9_1b 점검 후속) 자동 LinkExisting의 `FolderPath`/`TargetProjectPath`(→ cwd)를 백업 원본 문자열이 아니라 이 PC `project_roots`에 저장된 루트 표기로 쓴다(canonical은 같아도 표기가 다를 수 있음)
- [ ] **9_1-15** (9_2-1 점검에서 발견) `CodexProcessGuardTests.실제로_띄운_자식_프로세스의_ProcessName과_경로를_정확히_캡처한다`가 목록의 **첫** `cmd`를 검사해 병렬 실행 시 간헐 실패(07_01부터 존재) → 띄운 자식 프로세스를 특정해 검사하도록 수정 — *9_2-2에서 PID로 특정하도록 고쳤으나 다시 간헐 실패. 관리 대화방 재현(부하 중 150회 → 5회): **방금 띄운 자식의 `MainModule` 경로를 아직 읽을 수 없어 null**인 타이밍 문제가 실제 원인. 9_2-3에서 재수정*
- [x] **9_1-16** (9_2-1 점검 결정) 수동 지정(UserSelectedRegistered)도 cwd/ResolvedLocalPath를 등록 루트 표기로 통일

### 5.2 테스트

- [x] **9_1-T1** ProjectDirectory: DB 전용 / 레거시 전용 / 매핑 합침 / 루트로 합침 / 같은 루트 DB 2개 → Ambiguous / 루트 실존
- [x] **9_1-T2** (결함 D, RED→GREEN) 레거시 배정 대화와 cwd 폴백 대화가 같은 폴더면 카탈로그 한 그룹
- [x] **9_1-T3** (결함 A, RED→GREEN) 대화 0개 등록 프로젝트로 자동 연결과 수동 지정 → Apply 후 `threads.project_id = DbProjectId`, `cwd` remap
- [x] **9_1-T4** (결함 B) 원본 폴더가 실존하고 미등록 → `OriginalRootExistsUnregistered`, 원본 없음 → `OriginalRootMissing`
- [x] **9_1-T5** (결함 C, RED→GREEN) 레거시 ID로만 알려진 프로젝트 폴더로 지정 → **RolledBack이 아니라 Succeeded**, `project_id`는 매핑된 DB ID
- [x] **9_1-T6** Preflight: Plan 이후 대상 프로젝트 삭제 → `LocalStateChanged`
- [x] **9_1-T7** 회귀: 기존 테스트 전체 GREEN(Codex 실행 중이어도 GREEN — 9_1-01 효과 확인)
- [x] **9_1-T8** 복제본 E2E: 관리 대화방 하네스 Case 0~4 재실행. Case 0/1/3은 사유 표시가 정확하고, Case 3/4는 실제 연결되며, 원본 해시 불변
- [x] **9_1-T9** (9_1a 점검에서 추가) 9_1a 이전에 만든 백업(manifest 프로젝트 ID가 레거시 ID) → 새 코드의 Preview가 루트 기준으로 올바르게 매핑, 원시 ID 불일치로 실패하지 않음

### 5.3 완료 조건
위 작업과 테스트가 모두 체크되고, 설계와 다른 결정이 있으면 설계 문서에 반영됐다.

---

## 6. 9_0-A — Codex Desktop 반영 실험 1 (사용자 진행)

**목표**: DB(`threads.project_id`)에만 기록한 연결이 Desktop 사이드바에 나타나는지 판정한다. **설계 참조**: §6 "9_0".

- [ ] **9_0-A1** Codex 완전 종료 확인 → `C:\Users\User\.codex` 전체 폴더 복사(원복용) — 사용자
- [ ] **9_0-A2** 관리 대화방이 기준값을 읽기 전용으로 기록(global-state 플래그, pending 목록, 배정 수, `project_id` 분포)
- [ ] **9_0-A3** 9_1 빌드로 테스트 대화 1개를 "대화가 있는 등록 프로젝트" 폴더로 가져오기 — 사용자
- [ ] **9_0-A4** Desktop에서 위치 확인 (a) 프로젝트 아래 / (b) 프로젝트 없음 / (c) 안 보임 + 스크린샷 — 사용자
- [ ] **9_0-A5** Desktop 재시작 후 다시 확인(시작 시 migration 여부) — 사용자
- [ ] **9_0-A6** 관리 대화방이 global-state 변화를 읽기 전용으로 비교하고 판정을 기록(§13 점검 기록)
- [ ] **9_0-A7** 판정에 따라 9_5/9_5a 상태 갱신(a → 9_5 진행, b/c → 9_5a 설계)

---

## 7. 9_2 — 가져오기 작업 공간(새 화면) + 사용자 선택

**목표**: 백업을 메인 목록과 같은 트리(체크박스, 배지, 프로젝트별 작업 폴더)로 보여준다. 고른 대화만 가져온다.
적용 전 요약과 적용 후 결과를 명확히 보여준다. 미등록 폴더는 A안(기타 대화 + 안내).
**설계 참조**: §3, §5.2~5.3, §6 "9_2", §7 전체, §9.
**범위 밖**: 대화 내용 미리보기(9_2b), 프로젝트 생성(9_5), 연결 변경(9_3), 되돌리기(9_4).

> **프롬프트 분할**: 9_2-1 = Core(9_2-01~06, 9_2-21, T1, T2, T7) + 9_1-12~14 (`docs/phase9-prompts/9_2-1.md`) · 9_2-2 = 화면(9_2-07~20, T3~T6) + 9_1-15·16 (`docs/phase9-prompts/9_2-2.md`, **점검 완료**) · 9_2-1 **점검 완료** · 9_2-3 = GUI 다듬기(9_2-23~29) + 9_1-15 재수정 + 9_2b 미리보기 (`docs/phase9-prompts/9_2-3.md`)

### 7.1 Core

- [x] **9_2-01** `ImportUserChoices`/`ProjectTargetDecision` 추가
- [x] **9_2-02** `ImportSelection.Compute(preview, choices)`: 선택 가능 여부, 조상 closure, 대화별 최종 동작, 차단 사유, 요약 개수(순수 로직, I/O 없음)
- [x] **9_2-03** `ImportPlan` addendum: `ImportSkipReason`, `TargetProjectKey`, `ResolvedTarget`, `UserChoices`(설계 §5.3) — 단 `ImportPlanProject.ResolvedTarget`은 9_1b(9_1-08)에서 먼저 추가
- [x] **9_2-04** `ImportPlanBuilder.Build(preview, path, choices)` 오버로드. 기존 시그니처는 `UserChoices=null`로 기존과 같은 결과
- [x] **9_2-05** Diverged/Unverifiable을 체크 해제하면 나머지는 적용 가능, closure에 걸리면 계속 차단
- [x] **9_2-06** `ProjectTargetResolver`를 사용자 폴더 변경에 즉시 재계산(메모리 + `Directory.Exists`만)
- [x] **9_2-21** (9_1b 점검에서 추가) 사용자가 제외한 대화(`UserExcluded`)는 Preflight 사전조건 검사와 Planner 쓰기 대상에서 모두 빠진다(제외 대화의 로컬 변화가 나머지 적용을 막지 않음, 제외 대화의 rollout·행은 쓰기 0건)
- [ ] **9_2-22** (보류, 9_5 이후 후보) Ambiguous 루트에서 연결할 프로젝트를 사용자가 고르기(`[프로젝트 선택 ▼]`) — Planner의 fresh 재판정과 충돌하지 않는 결정 모델 필요. 9_2-2에서는 안내 문구만
- [ ] **9_2-23** (9_2-2 GUI 점검) 트리 항목(가져오기 화면·메인 화면)의 접근성 이름이 ViewModel 클래스 이름으로 노출됨 → 표시 이름(프로젝트 이름/대화 제목)으로 지정
- [ ] **9_2-24** (9_2-2 GUI 점검) 검색 칸에 보이는 자리 표시 문구(예: "대화 제목 검색")가 없음 → 추가
- [ ] **9_2-25** (9_2-2 GUI 점검) 프로젝트 펼침 삼각형이 작업 폴더 상자 중간("이 PC" 줄 옆)에 떠 있음 → 프로젝트 이름 줄에 맞춤
- [ ] **9_2-26** (9_2-2 GUI 점검) 선택할 수 없는 대화 행: 체크박스가 흰색이라 선택 가능해 보임 → 비활성으로 보이게. "이미 있음" 행에 이 PC 위치(예: "이 PC 위치: 기타 대화")를 행 안에 바로 표시(사용자의 원래 혼란 지점)
- [ ] **9_2-27** (9_2-2 GUI 점검) 편집 화면 진입 시 오른쪽 상세가 비어 있음 → 첫 대화(없으면 첫 프로젝트)를 자동 선택
- [ ] **9_2-28** (9_2-2 GUI 점검) 머리 정보의 "앱 0.1.1.0"이 현재 앱 버전처럼 읽힘 → "만든 앱 v0.1.1" 등으로 의미를 밝힘
- [ ] **9_2-29** (9_2-2 보고 Q2~Q4) 결과 화면 로직을 `ImportResultViewModel`로 분리(설계 §3.1), 복구 지점 ID는 날짜·시각으로 짧게 보이고 전체 ID는 상세/툴팁, `HighlightDuration` 정적 속성 → 주입 가능한 인스턴스 값

### 7.2 App

- [x] **9_2-07** `ImportWorkspaceViewModel` 상태 머신(Closed/Opening/**WaitingForCodexExit**/Analyzing/Failed/Editing/Confirming/Applying/Result, 설계 §7.1)
- [x] **9_2-08** 기존 Import 상태와 명령을 `MainViewModel`에서 Workspace로 이관. MainViewModel은 진입/복귀/강조만
- [x] **9_2-09** 트리: `ImportProjectNodeViewModel`(3상태 체크 + 작업 폴더 영역), `ImportConversationNodeViewModel`(배지 + 체크), 조상 자동 포함 흐림 표시
- [x] **9_2-10** 작업 폴더 영역: 목적지 상태 7종 표시와 컨트롤(설계 §7.3, 9_2에서는 CreateNew 행 대신 A안 문구)
- [x] **9_2-11** 배지와 기본 체크 규칙(설계 §7.4), 검색 필터(선택 유지), `[새 대화만] [전체 선택] [모두 해제]`
- [x] **9_2-12** 오른쪽 상세 패널(상태 설명, 이 PC 위치, 원본 정보). 내용 미리보기 자리만 확보
- [x] **9_2-13** 하단 실시간 요약 + `[대화 N개 가져오기]` 버튼(0개면 비활성 + 사유)
- [x] **9_2-14** Codex 실행 정책 B(설계 §7.1): 파일 선택 직후 실행 중이면 분석하지 않고 종료 안내 + [다시 확인], Editing 중 감시(활성화 시점과 5초 주기) → 배너 + 버튼 비활성 + [다시 분석](선택 유지)
- [x] **9_2-15** 확인 대화상자(설계 §7.5)
- [x] **9_2-16** 결과 화면(설계 §7.6). `NothingToDo`면 각 대화의 현재 위치 목록, 실패 유형별 문구와 [다시 시도]
- [x] **9_2-17** `[목록에서 보기]`: 카탈로그 새로고침 + 가져온 대화 선택, 펼침, 강조
- [x] **9_2-18** 문구 통일: "불러오기" → "가져오기", 내부 용어 나열 제거, "상세 정보 ▼"에 개발용 정보
- [x] **9_2-19** 메시지 카탈로그(설계 §9) 적용
- [x] **9_2-20** 내보내기 시작 전 Codex 실행 중이면 안내 문구(차단하지 않음, 설계 §7.1 정책 표)

### 7.3 테스트

- [x] **9_2-T1** ImportSelection: 기본 체크, 제외 → Skip(UserExcluded), 조상 자동 포함, Diverged 제외 시 적용 가능, closure Blocked면 불가, 요약 개수
- [x] **9_2-T2** ImportPlanBuilder: `UserChoices=null` 결과가 기존과 동일(스냅샷 비교), 선택 반영, identity pinning 유지
- [x] **9_2-T3** App: 3상태 체크, 검색 중 선택 유지, 0개 비활성과 사유, 폴더 변경 후 요약 갱신, 상태 전이, Codex 실행 배너, **Codex 실행 중이면 분석 자체를 시작하지 않음(Analyzing 진입 0회)**, [다시 분석] 후 선택 유지
- [x] **9_2-T4** 스크린샷 시나리오 재현: 이미 있는(기타 대화) 대화 1개짜리 백업 → 버튼 비활성 + "이미 이 PC에 있음 · 위치: 기타 대화" 표시
- [x] **9_2-T5** 분석, 선택 변경, 폴더 변경 중 Codex Home 쓰기 0건(해시 고정 테스트)
- [ ] **9_2-T6** 전체 테스트 GREEN + 사용자 GUI 수동 확인(관리 대화방이 확인 목록 제공) — *자동 부분(667/667, fixture 2개 E2E, 렌더 바인딩 오류 0)은 9_2-2에서 확인. 사용자 GUI 수동 확인은 9_2-3 이후*
- [x] **9_2-T7** (Core E2E, Restore.Tests) Diverged/Unverifiable 대화를 제외한 선택 → 나머지 Apply 성공 + 제외 대화의 파일·행 무변경, 제외한 New 대화의 rollout 미생성, 필수 조상 자동 포함 후 적용 성공

---

## 8. 9_2b — 백업 속 대화 내용 미리보기

**목표**: 가져오기 화면 오른쪽에서 백업 안의 대화를 메인 Viewer와 같은 모양으로 읽는다. 임시 파일은 만들지 않는다.
**설계 참조**: §6 "9_2b".

- [ ] **9_2b-01** `IRolloutContentSource` + `LocalFileRolloutContentSource`(Codex), `BackupRolloutContentSource`(Backup)
- [ ] **9_2b-02** `ConversationItemParser`/`ConversationTranscriptBuilder`에 content source 오버로드. 기존 오버로드는 Local에 위임(동작 불변)
- [ ] **9_2b-03** Workspace 수명 동안 `BackupReader` 1회 열기와 재사용, 닫을 때 Dispose
- [ ] **9_2b-04** 선택 시 백그라운드 생성, 선택 변경 시 취소, LRU 3개 캐시
- [ ] **9_2b-05** 메인 Viewer 렌더링 재사용(MarkdownLite, `FlowDocumentBinding`, 가상화)
- [ ] **9_2b-T1** 백업 transcript = 같은 파일의 로컬 transcript(메시지 수와 해시), 세그먼트/분기/`.zst` 포함
- [ ] **9_2b-T2** 미리보기 중 Codex Home 쓰기 0건, temp 파일 0건
- [ ] **9_2b-T3** 빠른 선택 전환 취소 + FlowDocument 재활용 스트레스

---

## 9. 9_5 — 자동 프로젝트 생성 (최종 목표)

**선행 조건**: 9_2 완료 + 9_0-A 판정 (a). (b)/(c)면 9_5a 이후에만 기능 활성화.
**목표**: 폴더를 고르면(또는 원본 폴더가 실존하는데 미등록이면) 그 경로를 루트로 하는 프로젝트를 만들고 가져온 대화를 연결한다.
**설계 참조**: §6 "9_5", §7.3, §8.

- [ ] **9_5-01** `PlannedProjectCreate` + Planner: fresh 재조회(그 사이 등록되면 LinkExisting 전환), 같은 루트 CreateNew 합치기, 이름/루트/idempotency key 규칙
- [ ] **9_5-02** `StateDatabaseWriter.CreateProject`: 공식 `create_project`와 같은 순서(idempotency 조회 → 트랜잭션 내 루트 충돌 재확인 → projects → project_roots → thread INSERT → idempotency 키), thread INSERT보다 먼저
- [ ] **9_5-03** `SchemaCompatibilityChecker`에 projects/project_roots/project_idempotency_keys와 threads.project_id FK 게이트. 실패 시 `CreationUnsupported`(생성만 포기)
- [ ] **9_5-04** `RestoreValidator` 사후 검증(프로젝트/루트/키 행, thread `project_id`/`cwd`, fresh ProjectDirectory 반영)
- [ ] **9_5-05** Preview/화면: CreateNew 목적지와 이름 편집 칸, 백업 "기타 대화" 그룹에도 폴더 지정 허용, 확인과 결과 화면에 "새로 만들 프로젝트" 표시
- [ ] **9_5-06** 기능 플래그(9_0 판정 전에는 끌 수 있게)
- [ ] **9_5-T1** 미등록 폴더 → 생성 + 연결 + cwd, 공식 SQL 불변식(position=MAX+1, 루트 position 0, 키 행)
- [ ] **9_5-T2** 같은 백업 재가져오기 → 재사용(중복 0), 계획 후 같은 루트가 등록되면 LinkExisting 전환, 같은 폴더 백업 프로젝트 2개 → 생성 1회
- [ ] **9_5-T3** fault injection 3지점 → Rollback 후 프로젝트 행 없음, CrashSim 강제 종료 → 복구
- [ ] **9_5-T4** 스키마 게이트 실패 → 생성 생략 + 기타 대화 + 경고, Apply 성공
- [ ] **9_5-T5** 복제본 E2E(원본 해시 불변)

### 9.1 9_5a — (조건부) global-state 연결 기록
9_0 판정이 (b)/(c)일 때만 연다. 관리 대화방이 별도 설계를 작성하고 사용자 승인을 받은 뒤 작업 목록을 추가한다.

- [ ] **9_5a-00** 필요 여부 판정(9_0-A7 결과로 체크하거나 취소)

### 9.2 9_0-B — Desktop 반영 실험 2 (사용자 진행)
- [ ] **9_0-B1** 전체 백업 → 9_5 빌드로 미등록 폴더 가져오기(새 프로젝트 생성) → Desktop에서 새 프로젝트와 대화 확인 → 필요 시 원복
- [ ] **9_0-B2** 관리 대화방 판정 기록 + UI의 ⓘ 경고 문구 확정 또는 제거

---

## 10. 9_3 — 이미 있는 대화의 프로젝트 연결 변경

**선행 조건**: 9_5. **목표**: 기타 대화 등 다른 위치에 이미 있는 대화의 프로젝트 연결만 바꾼다. 지금 한글패치 대화 같은 경우다.
**설계 참조**: §6 "9_3".

- [ ] **9_3-01** 트리에 "📁 연결만 변경" 체크(Identical/IncomingAhead + 위치가 목적지와 다를 때, 기본 해제)
- [ ] **9_3-02** `PlannedThreadProjectLink` + Planner(목적지 = 기존 DB 프로젝트 또는 같은 Plan의 새 프로젝트)
- [ ] **9_3-03** Writer: `UPDATE … WHERE id=@id AND project_id IS @expected`, 영향 행 1이 아니면 실패 → Rollback
- [ ] **9_3-04** 결과 화면에 "연결 변경(기타 대화 → ○○)" 표시
- [ ] **9_3-T1** 연결 변경 성공, rollout 파일 무변경
- [ ] **9_3-T2** expected 불일치(TOCTOU) → Rollback
- [ ] **9_3-T3** 복제본 E2E: 기타 대화에 있는 대화 → 폴더 지정 → 새 프로젝트 생성 + 연결

---

## 11. 9_4 — 가져오기 기록 · 되돌리기 · Snapshot 정리

**목표**: 성공한 가져오기를 안전하게 되돌리고(CLAUDE.md §17), 쌓인 Snapshot을 관리한다. **설계 참조**: §6 "9_4".

- [ ] **9_4-01** Journal에 `AppliedFingerprints`(Apply 직후 target 파일과 행 해시)와 `Summary`(원문 없음) 기록
- [ ] **9_4-02** `ImportHistoryViewModel` + 메인 `[가져오기 기록]`(현재 Home 기준 목록)
- [ ] **9_4-03** `ImportUndoService`: 전제 조건(Codex 종료, 같은 Home, fingerprint 일치) → 역연산(새 파일 삭제, append truncate, INSERT 행 삭제, UPDATE 필드 복원, 새 프로젝트 삭제. 다른 thread가 쓰면 유지) → 역연산 자체도 Snapshot/Rollback
- [ ] **9_4-04** 구버전 기록(fingerprint 없음)은 "되돌리기 불가"로 표시
- [ ] **9_4-05** `SnapshotRetentionService`: 선택 삭제, "30일 이상 + 되돌리기 불가" 정리. Prepared/Applying은 삭제 금지. 자동 삭제 없음
- [ ] **9_4-T1** 변경 없음 → 되돌리기 성공, 다른 thread 무영향
- [ ] **9_4-T2** 가져온 대화를 이어 씀 → 거부
- [ ] **9_4-T3** 되돌리기 도중 실패 → 되돌리기의 Rollback
- [ ] **9_4-T4** Snapshot 정리 안전 규칙

---

## 12. 9_F — 마무리

- [ ] **9_F-01** `docs/import-preview-phase6.md` Phase 9 addendum(추가 필드, `UserChoices`, `ProjectTarget` semantics, frozen 범위)
- [ ] **9_F-02** `docs/safe-restore-phase7.md` §13 Phase 9(프로젝트 생성, 연결 변경, 되돌리기, 불변식)
- [ ] **9_F-03** `docs/codex-storage-format.md` §5에 레거시↔DB 매핑, 외래키, 9_0 실측 결과
- [ ] **9_F-04** `README.md`, `docs/dist-readme.txt` 사용 방법 갱신
- [ ] **9_F-05** 버전 `0.2.0`(`Directory.Build.props`) + release notes
- [ ] **9_F-06** 전체 E2E(PC A→B 기본 42항목 + Phase 9 케이스) 관리 대화방 재실행, 원본 해시 불변
- [ ] **9_F-07** `docs/project-status-and-handoff.md` 갱신
- [ ] **9_F-08** `scripts/publish-release.ps1`로 릴리스 산출물 생성 확인(Tag/Release는 사용자)

---

## 13. 점검 기록 (관리 대화방 작성)

| 날짜 | 단계/작업 ID | 결과 | 메모 |
|---|---|---|---|
| 2026-09-30 | — | 계획 작성 | 설계 확정, 9_P부터 시작 |
| 2026-09-30 | 9_P-03 | 확인 | main `54904e8`, 테스트 531/531, 버전 0.1.3 |
| 2026-09-30 | 9_P-01/02/04 | 사용자 대기 | 문서 미커밋, 원본 rollout 3개 여분(+3628/+2618/+2853 B) 그대로, 앱 교체 전 |
| 2026-09-30 | 9_1a | 프롬프트 발행 | `docs/phase9-prompts/9_1a.md` |
| 2026-09-30 | 9_P-02 | 완료 | 원본 rollout 3개 복원(사고 전 사본과 해시 일치, 복원 전 파일 보관) |
| 2026-09-30 | 설계 | 정책 결정 | Codex 실행 정책 B: 가져오기는 Codex 종료 상태에서 시작, 보기와 내보내기는 허용(설계 §7.1) → 9_2-07/14/T3 갱신, 9_2-20 추가 |
| 2026-09-30 | 9_P-01 | 완료 | 설계 문서 커밋 `a0eab0c` |
| 2026-09-30 | 9_1a 점검 | **통과** | 빌드 경고 0 · 테스트 559/559(Codex 없음), 가짜 `codex` 실행 중에도 Restore 79/79 · App 99/99 직접 재현. 실측(읽기 전용, 원본 해시 불변): 카탈로그 47→46, KnownProject 46(DB 46 · 레거시 전용 0), 대화 0개 1, Ambiguous 루트 2. 복제본 Case: 0(원본 경로 실존·0대화) NotFound 유지, 1(미등록) 표시 오해 유지, 2 정상, **3(등록·0대화) 여전히 무시 → 9_1b**, **4(레거시 배정 프로젝트) RolledBack→Succeeded**(9_1a 부수 효과로 결함 C 주경로 해소, DB ID 기록 확인). 관리 대화방 정정: 설계 §1.2 D의 '중복 2건'은 Codex가 한 폴더를 여러 프로젝트로 등록한 Ambiguous였고 실제 레거시/DB 중복은 1건. 후속 추가: 9_1-11(방어), 9_1-T9(구버전 백업 호환) |
| 2026-09-30 | 9_1b | 프롬프트 발행 | `docs/phase9-prompts/9_1b.md` |
| 2026-10-01 | 9_1b 점검 | **통과** | 빌드 경고 0 · 테스트 599/599(가짜 `codex` 실행 중/없이 각각). 복제본 Case 0~4 전부 기대대로(0 자동 연결, 1 기타 대화+정확한 사유, 2 정상, **3 연결됨**, 4 실존 DB ID), 9_1a 이전 형식 백업으로 수행 → T9 실데이터 확인. 기본 E2E 42/42(하네스의 원시 ID 비교를 그룹 소속 판정으로 수정 후), 원본 해시 불변, 줄바꿈 혼합 없음. 결정: Q1 루트 없는 등록 프로젝트→OriginalRootMissing(기타 대화) 수용, Q2 원본 없음 Plan은 Apply 때 재판정 안 함 수용, Q3 사후 검증 강화(9_1-12), Q4 `ProjectPathMapper` 제거(9_1-13), Q5 수용(T9는 관리 대화방이 실데이터로 확인). 관리 대화방 발견: 자동 연결 cwd가 백업 원본 표기를 씀 → 9_1-14. 9_2-21·9_2-T7 추가 |
| 2026-10-01 | 9_2-1 | 프롬프트 발행 | `docs/phase9-prompts/9_2-1.md` |
| 2026-10-01 | 9_2-1 점검 | **통과** | 빌드 경고 0 · 테스트 636/636(가짜 `codex` 실행 중). 없이 1회차에 `CodexProcessGuardTests` 1건 간헐 실패 → 단독 12회 통과, 원인은 목록의 첫 `cmd`를 검사하는 테스트 결함(07_01부터, 이번 변경 무관) → 9_1-15. 복제본 E2E에 Diverged 대화 1개 추가: 기존 Build는 전체 차단, 기본 선택 Plan은 3 Import + 1 Update 적용·Diverged 파일 바이트 불변·재가져오기 NoOp·낡은 계획 거부, 실패 0건, 원본 해시 불변. 결정: Q1 선택 안 한 Identical=UserExcluded 수용, Q2 수동 지정 cwd도 등록 루트 표기(9_1-16), Q3 화면은 thread ID 대신 제목(9_2-2), Q4 수용, Q5 9_2-2에서 해소. Ambiguous 사용자 선택은 9_2-22로 보류. `docs/import-preview-phase6.md` §10 addendum 작성 |
| 2026-10-01 | 9_2-2 | 프롬프트 발행 | `docs/phase9-prompts/9_2-2.md` |
| 2026-10-01 | 9_2-2 점검 | **통과(다듬기 후속)** | 빌드 경고 0 · 테스트 667/667(Codex 없이). 가짜 `codex` 실행 중 1회차에 9_1-15 테스트가 다시 간헐 실패 → 부하 중 150회 재현으로 실제 원인은 `MainModule` 초기화 타이밍임을 확인(제품 판정은 이름으로도 잡으므로 영향 없음). **실제 앱을 UI 자동화로 띄워 캡처**: 메인 화면 정상·버튼 "백업 가져오기", 사용자 스크린샷과 같은 백업을 열면 버튼 "대화 0개 가져오기" 비활성 + "선택한 대화는 모두 이미 이 PC에 있습니다", 원본 폴더 없음 안내, 820px 폭 겹침 없음. 원본 해시 불변, 적용 누르지 않음. 발견: 트리 접근성 이름이 클래스 이름, 검색 자리 표시 없음, 펼침 삼각형 위치, 비활성 체크박스 흰색·행 안 위치 표시 없음, 상세 빈 화면, 백업 앱 버전 표기 → 9_2-23~28. 보고 Q2~Q4 → 9_2-29 |
| 2026-10-01 | 9_2-3 | 프롬프트 발행 | `docs/phase9-prompts/9_2-3.md` |

## 14. 변경 이력

| 날짜 | 변경 |
|---|---|
| 2026-09-30 | 최초 작성(관리 대화방) |
| 2026-09-30 | Codex 실행 정책 B 반영(9_2-07, 9_2-14, 9_2-T3 수정, 9_2-20 추가), 9_P-02/03 완료 |
| 2026-09-30 | CLAUDE.md §19에 가져오기 Codex 종료 전제 정책 추가(사용자 승인) |
| 2026-09-30 | 9_1a 점검 반영: 9_1-01~05·T1·T2 체크, 9_1-11·9_1-T9 추가, 9_2-03 일부를 9_1b로 이동 |
| 2026-10-01 | 9_1b 점검 반영: 9_1-06~11·T3~T9 체크, 9_1-12~14·9_2-21·9_2-T7 추가, 9_2 프롬프트 분할(9_2-1 Core / 9_2-2 화면) |
| 2026-10-01 | 9_2-1 점검 반영: 9_1-12~14·9_2-01~06·21·T1·T2·T7 체크, 9_1-15·16·9_2-22 추가, Phase 6 계약 문서 §10 addendum |
| 2026-10-01 | 9_2-2 점검 반영: 9_1-16·9_2-07~20·T3~T5 체크, 9_1-15 원인 정정(재오픈), 9_2-23~29 추가 |
