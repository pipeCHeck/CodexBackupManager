# Phase 7 — Safe Restore / Fast-forward Apply Core 스펙

이 문서는 Phase 7(frozen `ImportPlan`을 실제 Codex Home에 적용하는 최초의 write 단계)의 정식
스펙이다. Phase 6/06_01/06_02/06_03(`docs/import-preview-phase6.md`, FROZEN)이 "무엇을 할지"를
결정했다면, 이 문서는 "그것을 어떻게 안전하게 실제로 쓰는가"를 다룬다.

> **상태: Phase 7 완료(구현+테스트 검증 기준).** `New`→Import, `Identical`→NoOp, `IncomingAhead`→
> 안전이 증명되는 경우만 Fast-forward, `LocalAhead`→Skip, `Diverged`/`Unverifiable`→Apply 전체
> 차단까지 실제로 구현하고 합성 temp Codex Home으로 검증했다(§9). Diverged 자동 merge/backup으로
> 교체/복사본 가져오기, `.codex-global-state.json` write, 새 로컬 프로젝트 자동 생성,
> attachment 실제 파일 복원 같은 고위험/미확정 기능은 이번 Phase에서 구현하지 않았다(§2/§9.2 —
> Phase 07_01에서 필요성 재검토). Apply 버튼 등 UI는 아직 없다.

---

## 1. Restore Write Strategy Audit — 실측/공식 소스 기반 결론

### A. New thread를 Codex가 인식하는 최소 write set

실제 `state_5.sqlite`를 read-only로 다시 열어 `sqlite_master`/`PRAGMA table_info(threads)`를
직접 확인했다(이 세션에서, 원본 무변경 — §8 참고). 결과:

```
threads (38컬럼, 실측)
  NOT NULL, 기본값 없음 → INSERT 시 반드시 값을 채워야 한다:
    rollout_path, created_at, updated_at, source, model_provider, cwd, title,
    sandbox_policy, approval_mode

  NOT NULL, 기본값 있음(생략 가능, 있으면 원본 metadata로 채운다):
    tokens_used(0), has_user_event(0), archived(0), cli_version(''),
    first_user_message(''), memory_mode('enabled'), preview(''),
    recency_at(0), recency_at_ms(0), history_mode('legacy'), is_pinned(0)

  NULL 허용:
    archived_at, git_sha, git_branch, git_origin_url, agent_nickname, agent_role,
    model, reasoning_effort, agent_path, created_at_ms, updated_at_ms, thread_source,
    name, thread_section_id, section_position, section_entered_at_ms, project_id
```

트리거 3개(`threads_created_at_ms_after_insert`류 + `threads_updated_at_ms_after_update` +
`threads_recency_at_after_insert`)가 `*_ms`/`recency_at(_ms)`를 `created_at`/`updated_at`으로부터
자동으로 채운다 — **직접 계산해서 넣지 않는다.** `BackupConversationMetadata`가 이미 원본
`CreatedAtMs`/`UpdatedAtMs`를 갖고 있으면 그 값을 그대로 넣고(트리거는 `_ms`가 이미 0이 아니면
건드리지 않는다), 없으면 컬럼을 비워 트리거가 초 단위 값에서 파생하게 둔다.

**`model_provider`가 비어 있으면 resume이 실패한다**(공식 Issue #29083, `docs/codex-storage-format.md`
§7-5) — `BackupConversationMetadata.ModelProvider`가 없으면 New Import 자체를 거부한다(추측으로
채우지 않는다). **`thread_source`가 NULL이면 UI 목록에서 사라진다**(Issue #23979) — nullable
컬럼이지만 원본 metadata에 값이 있으면 반드시 그대로 넣는다.

**공식 소스로 확인(`codex-rs/state/migrations/0001_threads.sql` 최초 스키마 +
`0002`~`0055`의 후속 `ALTER TABLE`)**: 위 컬럼 목록/제약은 실측(이 세션 read-only 재확인)과 정확히
일치한다. 최소 요구되는 rollout 내용도 확인했다(`rollout/src/metadata.rs`
`extract_metadata_from_rollout`/`builder_from_session_meta`) — **`session_meta` 한 줄이 있고, 그
`payload.id`가 파일명의 thread id와 일치해야** 정상적으로 metadata를 추출한다(불일치하는
`session_meta`는 무시된다 — "forked rollouts that embed the source session metadata" 대응).
Backup V1의 `BackupConversationMetadata`는 이미 원본 rollout에서 이 값들을 그대로 읽어 보존하고
있으므로 새로 채워 넣을 필요가 없다.

`_sqlx_migrations` 최신 version은 이 조사 시점(실측) **52**이고, 공식 소스 클론에는 최신
**55**까지 있다(소스가 설치본보다 앞서 있다 — 정상이다, `runtime_state_migrator`가
`ignore_missing: true`로 이런 차이를 허용하도록 설계돼 있다, `state/src/migrations.rs`). **`PRAGMA
user_version`은 공식 소스 어디에서도 쓰이지 않는다**(실측도 `0`) — 그래서 Phase 7의 스키마
호환성 게이트는 버전 숫자가 아니라(§1.H) **위 "기본값 없는 NOT NULL 컬럼 집합"이 실제로
존재하는지**를 `PRAGMA table_info(threads)`로 매번 다시 확인한다. 또한 Codex 자신의 SQLite 연결
설정(`journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5s`, `state/src/sqlite.rs`)을 우리
쪽 write 연결에도 그대로 맞춘다 — 다른 설정을 쓰면 잔존 핸들과 충돌하거나 손상 위험이 있다.

### B. IncomingAhead thread를 안전하게 이어붙이는 write set

**공식 소스로 확인(`codex-rs/rollout/src/recorder.rs`)**: 일반적인 resume-and-continue는 실제로
기존 물리 파일에 `File::options().read(true).append(true).open(...)`로 열어 **그대로 append**한다
(`open_rollout_for_append`) — 여는 즉시 파일 끝이 개행으로 끝나는지 확인하고(`ensure_rollout_is_
newline_terminated`), ordinal 상태는 파일을 **뒤에서부터** 스캔해 복구한다(`ordinal_state_for_
rollout`, `rollout/src/ordinal.rs`). 즉 **"완결된 JSONL 줄을 파일 끝에 추가하는 것" 자체는 Codex
자신의 정상 동작과 정확히 같은 연산**이다 — 이번 Phase가 하려는 일이 특별하거나 비공식적인 방식이
아니라는 근거다. 새 segment(새 물리 파일)는 `thread/revert`나 fork처럼 새 `rollout_id`가 필요할
때만 만들어진다(파일명이 `..._<새 rollout_id>.jsonl`이 된다).

다만 공식 소스는 **한 가지 정합성 위험을 명시적으로 경고한다**: `RolloutLineage`(`thread-store/src/
local/rollout_lineage.rs`)는 조상 파일들을 "immutable rollout range"로 취급하고, `history_base.
end_byte_offset`는 `rollout_contains_prefix`(`rollout/src/seekable_reader.rs`)로 **길이만** 검증한다
(그 위치까지의 바이트 내용을 다시 해시하지 않는다). 즉 **"다른 thread가 이미 `history_base`로
참조하고 있는(=그 thread의 조상 역할을 하는) 파일에 추가로 append하면", Codex 자신의 무결성
검증(길이 검사)은 통과하지만 "조상은 불변"이라는 자체 불변식과 모순되는 상태가 될 수 있다.**
Phase 06_01 실측(3-segment thread에서 segment1이 공식 cutoff 이후에도 여분 바이트를 갖고 있던
사례)도 바로 이 종류의 상황이었을 가능성이 있다.

**따라서 이번 Phase의 fast-forward 안전 조건(§4 자세히)**:
1. 지금 물리 파일의 바이트가 frozen `ExpectedLocalRevision`의 마지막 slice와 **정확히** 일치해야
   한다(길이+해시 재확인 — Phase 06_02/06_03의 precondition을 Apply 시점에 다시 물리적으로
   검증).
2. 물리 파일에 그 논리적 cutoff **이후**의 바이트가 남아있으면(Phase 06_01이 발견한 바로 그
   위험) — 안전하게 append할 수 없다(어디까지가 "우리가 이어붙일 지점"인지 물리적으로 확정할 수
   없다). 이 경우 **전체 Apply를 거부**한다(Unsupported).
3. **append하려는 그 물리 rollout id가, 로컬 카탈로그의 다른 어떤 thread에서도 `history_base`
   조상으로 참조되고 있지 않아야 한다**(공식 소스가 "조상은 불변"이라고 전제하는 것과 충돌하지
   않기 위한 방어적 확인 — 위 경고 반영). 참조되고 있으면 전체 Apply를 거부한다.
4. `.jsonl.zst`(압축) 파일에는 **압축 append를 절대 시도하지 않는다** — 그 세그먼트가 대상이면
   전체 Apply를 거부한다.
5. incoming에만 있는 **새 segment 파일**(로컬에는 아직 없는 뒤쪽 파일)은 안전하다 — 기존 바이트를
   전혀 건드리지 않는 순수 파일 생성이기 때문이다. `.jsonl`이든 `.jsonl.zst`든 byte-for-byte로 새로
   만든다.

### C. Project path remapping 시 state DB/global-state만 바꿔도 되는가

**(공식 소스 조사로 최초 가설을 뒤집음 — 아래 §E 참고)** `threads.project_id`(SQLite)만 쓰면 된다.
`.codex-global-state.json`은 이번 Phase에서 **전혀 건드리지 않는다.**

- `ImportPlanConversation.TargetProjectPath`를 Apply 시점 fresh 로컬 카탈로그의
  `ProjectEntry.RootPaths`와 canonical 비교해서 일치하는 로컬 프로젝트를 찾으면, 그
  `ProjectEntry.ProjectId`를 `threads.project_id`에 직접 쓴다.
- 일치하는 로컬 프로젝트를 못 찾으면(예: 사용자가 수동으로 고른 폴더가 아직 Codex에 프로젝트로
  등록돼 있지 않은 경우) 프로젝트 배정 자체를 하지 않는다 — "기타 대화"로 Import된다. **새
  프로젝트를 `projects`/`project_roots` 테이블에 생성하는 것은 이번 Phase에서 지원하지 않는다**(§2).
- **알려진 코스메틱 한계**: 이 PC의 `.codex-global-state.json`은 `threadAssignmentsMigrated: false`
  상태이고 Electron Desktop 앱 자신의 사이드바 상태(`thread-project-assignments`)는 SQL
  `threads.project_id`와 아직 동기화되지 않은 것으로 보인다(§E). 즉 `threads.project_id`를
  정확히 설정해도, **Codex Desktop 앱의 사이드바 UI**에는 그 프로젝트로 묶여 보이지 않고
  "기타 대화"로 보일 수 있다 — 이건 실제 CLI 엔진 동작(목록/resume/필터링)에는 영향이 없는
  Electron 자체의 별도 상태 문제다. Phase 07_01에서 추가 증거가 생기면 재검토한다.

### D. rollout JSONL 내부 cwd를 수정해야 하는가

**하지 않는다.** 공식 소스로도 확인했다 — `session_meta.cwd`는 SQLite `threads.cwd`로 흘러가
목록/필터링(정렬 인덱스)에만 쓰이고, 재개(resume) 시 실제 작업 디렉터리는 그 값이 아니라 매
turn마다 별도로 기록되는 `TurnContextItem.cwd`와 그 순간의 런타임 `Config`에서 온다
(`codex-rs/core/src/thread_manager.rs`) — replay가 rollout의 `session_meta.cwd`를 다시 읽어
적용하는 코드 경로를 찾지 못했다. 이번 Phase는 rollout JSONL의 바이트를 절대 수정하지 않는다(New는 원본 그대로
복사, IncomingAhead는 완결된 새 줄만 append 또는 새 파일 생성 — 기존 바이트는 절대 변경하지
않는다). `session_meta.cwd`는 프로젝트 연결의 authority가 아니다(§C — authority는 global-state
또는 `threads.project_id`다, cwd는 그 두 경로 모두 실패했을 때의 마지막 폴백일 뿐이다,
`CodexProjectResolver.cs` 참고). `history_base.end_byte_offset`이 실제 바이트 오프셋을 가리키므로
JSONL 내부 문자열 하나라도 바꾸면(cwd 포함) 그 오프셋 semantics가 깨질 수 있다는 사용자 지적이
정확하다 — 그래서 건드리지 않는 것으로 확정한다. `threads.cwd`(DB 컬럼)에는 원본
`BackupConversationMetadata.OriginalCwd`를 그대로 넣는다(가공하지 않는다).

### E. `threadAssignmentsMigrated` 상태에 따른 authority — **최초 가설이 틀렸다, 공식 소스로 정정**

공식 `codex-rs` 소스 전체(코어 엔진 + CLI)를 조사한 결과, **`.codex-global-state.json`이라는
파일 자체도, `threadAssignmentsMigrated`라는 플래그도 `codex-rs`에는 전혀 존재하지 않는다.**
project/thread 배정은 `codex-rs` 안에서 **SQLite `threads.project_id`(FK → `projects.id`,
`state/migrations/0049_projects.sql`)가 유일하고 무조건적인 authority**다 — flag로 갈라지는
dual-authority 구조가 아니다.

`docs/codex-storage-format.md` §5(B)가 문서화한 `.codex-global-state.json`은 **Codex Desktop(Electron)
앱 자신의 상태 파일**이며(그 문서에도 이미 "Electron 앱 상태"라고 적혀 있다), `codex-rs` 코어 엔진의
일부가 아니다 — Electron 셸이 자신의 사이드바 UI를 위해 별도로 유지하는 상태로 보이며, 이 저장소에는
그 Electron 소스가 없어 그 파일의 정확한 소비 방식은 조사할 수 없었다. 실측 결과(§1 앞부분) 이
PC에서는 `threads.project_id`가 전부 NULL인 반면 global-state의 `thread-project-assignments`에는
60개 항목이 있다 — 이는 **Electron 사이드바가 아직 SQL 스키마의 project 기능과 동기화되지 않은
상태**임을 보여준다(SQL 쪽 `projects`/`project_roots`는 이미 45행이 있는데도).

**Phase 7 결론**: `threads.project_id`만 쓴다(§C). `.codex-global-state.json`은 건드리지 않는다 —
공식 소스 근거가 없는 파일을 추측으로 수정하지 않는다는 원칙(CLAUDE.md §33)을 그대로 따른 결과다.

### F. archived conversation의 실제 물리 경로/DB 필드

`docs/codex-storage-format.md` §2 디렉터리 레이아웃과 공식 소스(`codex-rs/thread-store/src/local/
archive_thread.rs`, `helpers.rs::rollout_path_is_archived`)가 정확히 일치한다:
`archived_sessions\rollout-<ISO8601>-<threadId>.jsonl`(날짜 하위 폴더 없이 flat, `sessions\`와
형제 디렉터리). 공식 archive 구현은 파일 rename과 `archived`/`archived_at` DB 갱신을 한 묶음으로
수행하고 실패 시 rename을 되돌린다 — **두 신호(물리 위치 ↔ DB 플래그)는 항상 같이 바뀌어야 한다**는
뜻이다. New Import 시 `BackupConversationMetadata.Archived`가 `true`면 대상 경로를
`archived_sessions\<파일명>`으로, `false`면 `sessions\{yyyy}\{MM}\{dd}\<파일명>`(파일명 자체에
이미 박혀 있는 타임스탬프를 `RolloutFileNamePattern.TryParse`로 다시 파싱해서 얻는다 — 새로
추측하지 않는다)으로 계산하고, `threads.archived`/`archived_at`을 **반드시 같은 값으로** 맞춘다.
(참고: 공식 소스는 파일이 `sessions\`/`archived_sessions\` 루트 아래 정확한 파일명만 유지하면 정확한
YYYY/MM/DD 하위 폴더가 아니어도 재귀 탐색으로 찾아 `rollout_path`를 self-heal한다 —
`rollout/src/list.rs` `find_thread_path_by_id_str`. 그래도 이번 Phase는 공식 배치 규칙을 정확히
따르는 쪽을 택한다 — 이 폴백에 의존하지 않는다.)

### G. local_image attachment 경로 처리

**이번 Phase에서는 지원하지 않는다(Unsupported/Warning).** `local_image.path`가 rollout JSONL
content 내부에 있고, 그 절대경로를 다른 PC의 실제 파일 위치로 바꾸려면 JSONL 내부를 수정해야
하는데, 이는 §D와 같은 이유로 `history_base` byte-offset semantics를 깨뜨릴 위험이 있다 —
안전성을 증명하기 전까지 금지(사용자 지시 §13과 동일). 대화 텍스트/rollout 무결성이 attachment
복원보다 우선한다. Backup V1이 의도적으로 제외한 `attachments\`/`visualizations\`/
`generated_images\` 범위를 이번 Phase에서 넓히지 않는다.

### H. schema/migration 호환성 게이트

`_sqlx_migrations`의 `MAX(version)`은 "스키마 세대"를 나타내는 값이지 그 자체로 컬럼 목록을
말해주지 않는다(공식 소스에 `PRAGMA user_version`은 실측 결과 `0`으로 전혀 쓰이지 않았다 —
sqlx 자체 migrations 테이블이 유일한 버전 신호다). 따라서 게이트는 버전 **숫자 비교가 아니라
구조 비교**로 한다: `PRAGMA table_info(threads)`를 읽어 §A에서 확정한 "기본값 없는 NOT NULL
컬럼 9개"가 전부 존재하는지 확인하고, **하나라도 없으면 즉시 Apply 전체 거부**(그 컬럼 없이는
INSERT 자체가 실패하거나 우리가 모르는 새 필수 컬럼이 있다는 뜻이므로 추측하지 않는다). 위
9개가 전부 있고 그 밖에 우리가 모르는 컬럼이 추가돼 있어도(nullable이거나 기본값이 있으면)
차단하지 않는다 — 모르는 컬럼에 값을 채우려 하지 않고 SQLite의 기본값에 맡긴다.

---

## 2. 확정된 제한 사항 / 이번 Phase가 지원하지 않는 것 (추측 대신 명시적 차단)

| # | 항목 | 처리 |
|---|---|---|
| 1 | `Diverged`/`Unverifiable` | Apply 전체 차단(Phase 07_01에서 결정 메커니즘 도입 예정) |
| 2 | `.jsonl.zst`로의 append(기존 압축 파일 이어쓰기) | 전체 Apply 거부 — 압축 append는 시도하지 않는다 |
| 3 | 물리 파일에 논리 cutoff 이후 여분 바이트가 있는 IncomingAhead | 전체 Apply 거부(§1.B) |
| 4 | `local_image` 첨부 실제 파일 복원 | Unsupported — rollout JSONL 내부 경로는 그대로 두고 첨부 파일 자체는 옮기지 않는다 |
| 5 | 로컬에 아직 없는 프로젝트로의 신규 프로젝트 생성(`projects`/`project_roots` INSERT) | 미지원 — "기타 대화"로 Import(§1.C) |
| 6 | app-server(`thread/list` 등) 경유 write | 조사 안 함(app-server 자체를 이번 Phase 범위에 포함하지 않는다) |
| 7 | `thread_history_<N>.sqlite` | 절대 쓰지 않는다 — Codex가 lazy하게 재생성하는 파생 캐시 |

---

## 3. 계층 구조

```
CodexBackupManager.Restore              (신규 프로젝트, .NET 라이브러리)
  CodexProcessGuard                     Codex 실행 여부 판정
  SchemaCompatibilityChecker            PRAGMA table_info(threads) 구조 검증
  SnapshotService                       변경 대상 파일을 %LOCALAPPDATA%로 snapshot
  RestoreOperationPlanner               frozen ImportPlan + fresh catalog → RestoreOperationPlan(순수 데이터)
  RestoreOperationPlan (+ 하위 레코드)   구체적 file/db operation 목록
  RestoreExecutor                       Plan을 실제로 실행(Preflight→ProcessGuard→Schema→Snapshot→Write→Validate)
  StateDatabaseWriter                   threads INSERT/UPDATE(단일 트랜잭션)
  GlobalStateWriter                     .codex-global-state.json 병합 쓰기(atomic replace)
  RolloutRestoreService                 rollout 파일 생성/append(temp+atomic move, append 전후 hash 검증)
  RestoreValidator                      post-apply 검증(카탈로그 재구축 + fingerprint 비교)
  RollbackService                       snapshot 기반 원상복구 + 재검증
  IFaultInjectionHook                   테스트 전용 실패 지점 주입(운영 코드에는 no-op)

tests/CodexBackupManager.Restore.Tests
```

의존 방향은 그대로 유지한다: `App → Restore → Backup → Codex → Domain`(Restore는 Backup의
`ImportPlan`/`ImportPlanPreflightValidator`와 Codex의 카탈로그 빌더를 그대로 재사용한다 — 새
판정 로직을 만들지 않는다).

## 4. IncomingAhead Fast-forward — 물리 안전성 검증 절차

Apply 직전, `RestoreOperationPlanner`가 각 `IncomingAhead` 대화에 대해(relation을 다시 판정하는
게 아니라 물리 바이트만 재확인하는 것임에 주의):

1. `conversation.Precondition.ExpectedLocalRevision`의 마지막 slice(leaf)를 가져온다.
2. 그 slice의 `RolloutId`에 해당하는 **지금** 물리 파일을 다시 스트리밍 해시한다.
3. 파일 전체 길이가 `ExpectedLocalRevision`의 그 slice `LogicalByteLength`와 **정확히 같고** 해시도
   같아야 한다. 파일이 더 길면(=논리 cutoff 이후 여분 바이트, Phase 06_01의 실측 위험) → Unsupported.
   더 짧거나 내용이 다르면 → 애초에 06_03 preflight가 `LocalStateChanged`로 이미 잡았어야 하므로
   여기 도달했다면 방어적으로 다시 거부한다.
4. 그 leaf가 `.jsonl.zst`이면(`RolloutSlice`가 아니라 실제 파일 확장자로 재확인) → Unsupported.
5. `ExpectedIncomingRevision`의 슬라이스 중 로컬에 없는(뒤쪽) 슬라이스는 전부 "새 파일 생성"
   대상으로 만든다 — 기존 파일은 건드리지 않는다.
6. 로컬의 leaf와 incoming의 대응 슬라이스가 같은 `RolloutId`인데 `LogicalByteLength`가 다르면(=
   그 파일 자체가 이어써진 경우) — incoming의 해당 slice 바이트 중 로컬 길이 **이후의 부분만**을
   backup payload에서 잘라 append 대상으로 삼는다. append 전 로컬 파일을 다시 읽어 길이/해시를
   한 번 더 확인(방금 2~3단계와 동일 재확인, TOCTOU 최소화)하고, append 후 즉시 다시 읽어 전체
   길이/해시가 incoming의 그 slice와 일치하는지 확인한다.

하나라도 이 조건을 만족하지 못하면 **그 대화 하나만 건너뛰는 것이 아니라 Plan 생성 자체를
거부한다**(§5) — 부분 성공 상태를 만들지 않기 위해서다(Phase 5의 "부분 성공 금지" 정책을 Phase 7
write에도 그대로 적용).

## 5. Operation Plan 생성 실패 = 전체 write 0건

`RestoreOperationPlanner.Build`는 다음 중 하나라도 있으면 `null`을 돌려주고(=Snapshot조차 만들지
않는다) 이유 목록만 보고한다:
- `ImportPlan.HasBlockingIssues`/`HasUnresolvedDivergence`(이미 있으면 안 됨 — Preflight가 먼저
  걸러야 하지만 방어적으로 재확인)
- §4에서 하나라도 물리적으로 안전하지 않은 IncomingAhead
- 스키마 호환성 게이트 실패
- New 대화 중 `ModelProvider`가 비어 있는 경우(§1.A)

전부 통과해야 실제 mutation 목록(`RestoreOperationPlan`)이 만들어지고, 그 뒤에야 Snapshot이
시작된다.

## 6. Snapshot

`%LOCALAPPDATA%\CodexBackupManager\Snapshots\<yyyyMMdd-HHmmss>-<guid>\`에 만든다(`.codex` 내부가
아니다). `manifest.json`:

```
SnapshotManifest
  CodexHomeIdentity        (경로, 이 세션의 identity 정보)
  CreatedAtUtc
  ImportPlanBackupSha256   (plan.Backup.BackupFileSha256 — 이 Snapshot이 어떤 Apply를 위한 것인지)
  Files: [{ RelativeLabel, ExistedBefore, ByteLength, Sha256 }]  // ExistedBefore=false면 새로 생긴 파일 → rollback 시 삭제
```

대상(구현 확정, §9 참고): SQLite write가 하나라도 있을 때만 `state_<N>.sqlite`(+ `-wal` — 존재하면),
그리고 `RestoreOperationPlan`이 실제로 append/생성하기로 한 rollout 파일들. `session_index.jsonl`/
`.codex-global-state.json`은 snapshot 대상이 아니다 — 이번 Phase가 그 두 파일을 전혀 쓰지 않기로
확정했으므로(§1.E, §9) 보호할 대상 자체가 없다. temp 폴더에 전부 복사한 뒤 각 파일 재해시로
검증하고, 마지막에 manifest.json을 쓰는 것으로 "publish"를 표시한다(그 전에 프로세스가 죽으면
manifest가 없으므로 미완성 snapshot으로 간주).

## 7. Rollback

Snapshot manifest 기준으로 `ExistedBefore=true`인 파일은 원래 바이트로 복원, `false`인 파일(새로
생성됐던 것)은 삭제한다. 복원 후 각 파일을 다시 해시해 manifest의 `Sha256`과 비교 — 여기서도
실패하면 "복구 완료"라고 말하지 않고 `RestoreResult.Status = RollbackFailedCritical`로 보고한다.

## 8. 이 문서 작성 중 실제 `.codex` 접근 기록(read-only)

이 세션에서 `state_5.sqlite`(threads 스키마/트리거/인덱스/`_sqlx_migrations`)와
`.codex-global-state.json`(`threadAssignmentsMigrated` 등 특정 키만)을 `Mode=ReadOnly`로 다시
읽었다. 스크래치패드의 1회성 콘솔 도구로 조회했고, 조회 전후 `state_5.sqlite`/
`session_index.jsonl`/`.codex-global-state.json`/`config.toml`의 SHA-256이 동일함을 확인했다
(완료 보고에서 재확인).

---

## 9. 구현/검증 결과 요약

### 9.1 계층 구조(실제로 만든 것)

```
CodexBackupManager.Restore
  CodexProcessGuard             프로세스 이름(codex/Codex)/실행 파일 경로("OpenAI\Codex" 포함) 기반 판정
  SchemaCompatibilityChecker    PRAGMA table_info(threads)로 §1.A의 9개 컬럼 존재만 확인(버전 숫자 아님)
  SnapshotService               temp 복사 → 재해시 검증 → manifest.json 마지막에 write("publish")
  RestoreOperationPlanner       frozen ImportPlan + fresh 카탈로그 → RestoreOperationPlan(안전하지 않으면 전체 거부)
  RestoreOperationPlan          PlannedNewRolloutFile/PlannedRolloutAppend/PlannedThreadInsert/PlannedThreadRolloutPathUpdate
  StateDatabaseWriter           threads INSERT/UPDATE(트랜잭션은 RestoreExecutor가 소유), 컬럼 존재 여부를 다시 확인 후 바인딩
  RolloutRestoreService         새 파일: temp+검증+atomic move / append: 전후 hash 재확인
  RestoreValidator              post-apply — CodexDetectionService.DetectFromUserSelection + CodexCatalogBuilder로 재구축해 ExpectedIncomingRevision과 fingerprint 비교
  RollbackService                snapshot manifest 기준 byte-level 복구 + 재해시 검증
  RestoreExecutor                위 전부를 Codex 실행 확인 → fresh preflight → planning → snapshot → write → validation 순서로 오케스트레이션, 어디서 실패하든(취소 포함) snapshot 이후는 항상 rollback
  IRestoreFaultInjectionHook      테스트 전용 6개 지점(AfterSnapshot/AfterFirstRolloutCreate/AfterRolloutAppend/BeforeSqliteTransaction/AfterSqliteCommit/BeforePostValidation), 운영 코드는 NoOp
```

의존 방향: `App → Restore → Backup → Codex → Domain`(기존 방향 유지, Restore는 Backup/Codex의 기존
클래스를 그대로 재사용하고 새 판정 로직을 만들지 않는다).

### 9.2 실제로 지원하는 것 / 이번 Phase에서 명시적으로 막은 것

| 관계 | 처리 |
|---|---|
| `New` | rollout 전체(조상 포함, New인 소유자 것만) byte-for-byte 복사 + `threads` INSERT(원본 38컬럼 그대로, 가공값 안 씀) |
| `Identical` | write 0건 |
| `IncomingAhead` | 물리 파일이 frozen 기대값과 정확히 일치 + 논리 cutoff 이후 여분 바이트 없음 + 다른 thread의 history_base 조상으로 참조되지 않음 + plain `.jsonl`일 때만 append(zst는 항상 거부) — 하나라도 어긋나면 Apply 전체 거부(write 0건), 부분 성공 없음 |
| `LocalAhead` | write 0건(Skip) |
| `Diverged`/`Unverifiable` | `ImportPlan.HasUnresolvedDivergence`/`HasBlockingIssues`가 Preflight 단계에서 이미 차단 — Apply 시도 자체가 안 됨 |

이번 Phase가 하지 않은 것(§2/§4-17~20 handoff 문서 참고): `.codex-global-state.json` write(공식
소스에 이 개념 자체가 없음을 확인), 새 로컬 프로젝트 자동 생성, attachment 실제 파일 복원,
Diverged 자동 merge/backup 교체.

### 9.3 RED → GREEN

- `RestoreOperationPlanner`의 "다른 thread의 history_base 조상으로 참조되는 파일에는 append하지
  않는다" 검사를 일시적으로 비활성화 → 전용 테스트가 정확히 실패(적용이 `Succeeded`로 잘못
  통과)함을 확인 → 복원 → 재통과.
- 압축(`.jsonl.zst`) append 차단은 로컬 kind 체크와 incoming kind 체크 두 곳에 중복 방어로 존재한다
  — 로컬 쪽 체크를 비활성화해도 incoming 쪽 체크가 독립적으로 막아 `NotReady`를 유지함을 실제로
  확인했다(둘 다 있어야 안전하다는 뜻이 아니라, 최소 하나는 항상 작동한다는 defense-in-depth 확인).
- Phase 06_03과 동일하게, MainViewModel의 "새 Preview 시작 시 기존 frozen Plan 즉시 무효화" 수정도
  RED(무효화 코드를 제거하면 두 번째 Preview 완료를 기다리기도 전에 이전 Plan이 남아 있음)→GREEN으로
  확인했다.

### 9.4 전체 테스트 수 / 실제 사용자 `.codex` 무변경

`Domain 64 + Codex 200 + Backup 88 + Restore 15 + App 90 = 457건 전부 통과`, `dotnet build` 경고/
오류 0. `CodexBackupManager.Restore.Tests`의 모든 테스트는 `TestCodexHomeBuilder`가 실측 스키마
그대로 만든 합성 temp Codex Home에서만 동작한다 — 실제 사용자 `.codex`는 Phase 7 자동화 테스트
어디에서도 열리지 않았다(§8의 read-only 조사만 예외, 그마저도 SHA-256 무변경 확인).

### 9.5 알려진 한계(정직하게 남김)

- 실제 사용자 `.codex`를 clone한 진짜 데이터 기반 Restore E2E는 수행하지 않았다(§4-20 handoff
  문서, 용량/시간 제약 + "자동 테스트로 실제 `.codex`를 절대 건드리지 말라"는 지시를 안전하게
  지키는 쪽을 택함).
- `RestoreExecutor.Apply`를 호출하는 UI(Apply 버튼/진행 상태/확인 dialog)는 만들지 않았다 — 이번
  Phase는 write 엔진 자체의 안전성 확정을 최우선으로 했다.
- IncomingAhead의 물리 안전성 검사는 로컬 파일이 전용 파일 하나일 때(세그먼트 1개)를 확실히
  검증했다 — 세그먼트가 여러 개인 상태에서 fast-forward가 동시에 여러 새 segment를 만드는 복합
  케이스는 코드상 지원하지만(반복문으로 처리) 전용 테스트로 다중 신규 segment(2개 이상)까지는
  검증하지 못했다(신규 segment 1개는 실제로 검증함).
