# Phase 7 — Safe Restore / Fast-forward Apply Core 스펙

이 문서는 Phase 7(frozen `ImportPlan`을 실제 Codex Home에 적용하는 최초의 write 단계)의 정식
스펙이다. Phase 6/06_01/06_02/06_03(`docs/import-preview-phase6.md`, FROZEN)이 "무엇을 할지"를
결정했다면, 이 문서는 "그것을 어떻게 안전하게 실제로 쓰는가"를 다룬다.

> **상태: Phase 7 + Phase 07_01 + Phase 07_02(Release Safety Gate / Full-Clone E2E / Crash
> Recovery) 완료.** `New`→Import, `Identical`→NoOp, `IncomingAhead`→안전이 증명되는 경우만
> Fast-forward, `LocalAhead`→Skip, `Diverged`/`Unverifiable`→Apply 전체 차단까지 실제로 구현하고
> 검증했다(§9, §10, §11). Diverged 자동 merge/backup으로 교체/복사본 가져오기,
> `.codex-global-state.json` write, 새 로컬 프로젝트 자동 생성, attachment 실제 파일 복원 같은
> 고위험/미확정 기능은 여전히 구현하지 않았다(§2/§9.2/§10/§11.7). Phase 07_01에서 GitHub 코드
> 리뷰로 발견된 실제 안전성/정합성 문제(§10.1)를 전부 고쳤고, Apply 버튼을 포함한 UI를
> 연결했다(§10.5). **Phase 07_02**는 배포 전 마지막 안전성 게이트다 — rollout append를 항상
> temp+atomic replace로만 하도록 바꿔 crash-safety를 확보했고, Rollback의 post-check가 실제 target
> 파일을 다시 열지 않도록 고쳤으며(WAL/`-shm` 재오염 방지), durable transaction journal +
> incomplete-apply 복구를 추가했고, New Import의 `threads.cwd`를 대상 PC 경로로 remap하도록
> 바꿨다(공식 소스 조사 근거, §11.4). 그리고 **실제 `.codex`를 clone한 진짜 rollout 파일로
> IncomingAhead(fast-forward) E2E를 처음으로 성공시켰다**(§11.2) — 이전까지는 합성 데이터로만
> 검증했었다.
>
> **Phase 07_03(Final Restore Edge-Case Hardening)**도 완료했다. GitHub 코드 리뷰에서 배포 전
> 고쳐야 할 Restore edge case 3개가 발견됐다: (1) New rollout도 IncomingAhead append와 같은 수준의
> temp/atomic move + durability를 갖추지 않아 temp 작성 중 크래시가 재시도를 막을 수 있었던 문제,
> (2) 완료되지 못한 이전 Apply(incomplete-apply) 판정이 Codex Home을 구분하지 않아 수동으로 여러
> Home을 오가며 쓰는 이 프로그램의 실제 사용 패턴과 맞지 않았던 문제, (3) 같은 EXE를 두 번 실행하면
> 두 프로세스가 같은 Codex Home에 동시에 Apply할 수 있었던 문제. 셋 다 고쳤고(§12), 추가로
> `IncompleteApplyRecoveryService.Recover` 자신도 journal/manifest 정합성을 스스로 재검증하도록
> 강화했다(§12.3). 이제 **Restore Core는 기능을 더 바꾸지 않고 Phase 8(Release/Packaging)로
> 넘어간다**.

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
- **Desktop 사이드바 반영 여부는 여전히 미검증이다(Phase 07_01, 요구사항 11 — "코스메틱일 뿐"이라는
  단정을 취소한다).** 확정된 것은 딱 하나, **Core/CLI 엔진 authority는 `threads.project_id`뿐이라는
  것**(§E, 공식 소스 확인)이다. 이 PC의 `.codex-global-state.json`은 `threadAssignmentsMigrated: false`
  상태이고 Electron Desktop 앱 자신의 사이드바 상태(`thread-project-assignments`)에는 실제로 60개
  항목이 있는데 SQL `threads.project_id`는 전부 NULL이다 — 이것만으로는 "Desktop이 project_id를
  안 본다"고 결론 내릴 수 없다. Electron 소스를 보지 못했으므로 Desktop이 `project_id`를 실제로
  읽는지, 읽더라도 기존 global-state 항목과 병행/우선 처리하는지는 **조사하지 않았다.** Phase 07_01도
  `.codex-global-state.json`을 여전히 전혀 쓰지 않는다(§C 결론 그대로 유지). Import된 대화가 Codex
  Desktop 사이드바에서 올바른 프로젝트 아래 보이는지는 **별도 통합 검증**(Desktop 앱을 실제로 띄워
  확인)이 필요하다 — 이번 Phase에서 안전하게 확인할 방법을 찾지 못해 검증하지 못했으므로, Phase 8
  진입 전 알려진 Release 한계로 명시한다(§9, README 참고).

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
60개 항목이 있다 — SQL 쪽 `projects`/`project_roots`는 이미 45행이 있는데도 `project_id`는 채워지지
않은 상태다.

**여기서 확정할 수 있는 것과 확정할 수 없는 것을 분리한다(Phase 07_01, 요구사항 11).**

- **확정**: `codex-rs` 코어/CLI 엔진 안에서 project/thread 배정의 유일한 authority는 SQLite
  `threads.project_id`다. `.codex-global-state.json`/`threadAssignmentsMigrated`는 코어 엔진에는
  아예 존재하지 않는다(공식 소스 전체 검색으로 확인).
- **미확정(Electron 소스 미조사)**: Codex **Desktop 앱의 사이드바 UI**가 `threads.project_id`를
  실제로 읽어 반영하는지, 아니면 여전히 자신의 global-state만 참고하는지는 검증하지 못했다. 위 실측
  (project_id 전부 NULL vs global-state 60개 항목)은 "이 PC에서 두 값이 지금 서로 다르다"는 사실만
  보여줄 뿐, "Desktop이 project_id를 무시한다"는 결론까지 정당화하지 않는다 — 그렇게 단정했던 이전
  서술은 취소한다.

**Phase 7/07_01 결론**: `threads.project_id`만 쓴다(§C). `.codex-global-state.json`은 건드리지 않는다
— 공식 소스 근거가 없는 파일을 추측으로 수정하지 않는다는 원칙(CLAUDE.md §33)을 그대로 따른 결과다.
Import된 대화가 Desktop 사이드바에서 올바른 프로젝트로 보이는지는 별도 통합 검증이 필요한 **미검증
항목**으로 남겨 둔다(§C, §9).

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

### 9.5 알려진 한계(Phase 7 시점 — Phase 07_01에서의 변화는 §10.6 참고)

- ~~실제 사용자 `.codex`를 clone한 진짜 데이터 기반 Restore E2E는 수행하지 않았다~~ →
  **Phase 07_01에서 New/archived/segmented-New 계열은 실제 clone으로 검증했다(§10.4). IncomingAhead
  계열은 여전히 합성 데이터로만 검증했다(§10.6).**
- ~~`RestoreExecutor.Apply`를 호출하는 UI(Apply 버튼/진행 상태/확인 dialog)는 만들지 않았다~~ →
  **Phase 07_01에서 연결했다(§10.5).**
- IncomingAhead의 물리 안전성 검사는 로컬 파일이 전용 파일 하나일 때(세그먼트 1개)를 확실히
  검증했다 — 세그먼트가 여러 개인 상태에서 fast-forward가 동시에 여러 새 segment를 만드는 복합
  케이스는 코드상 지원하지만(반복문으로 처리) 전용 테스트로 다중 신규 segment(2개 이상)까지는
  검증하지 못했다(신규 segment 1개는 실제로 검증함) — **Phase 07_01에서도 이 항목은 추가 검증하지
  않았다. 여전히 알려진 미검증 항목이다(§10.6).**

---

## 10. Phase 07_01 — Restore Hardening / Apply UI / Real-Clone E2E

Phase 7(HEAD `a3de8e8`)을 GitHub 코드 리뷰한 결과 실제 사용자 `.codex`에 Apply를 노출하기 전에
고쳐야 할 안전성/정합성 문제가 여러 건 발견됐다. Phase 7의 아키텍처 방향(계층 구조, Snapshot/
Rollback 기반 설계, RevisionRelation별 처리 정책)은 그대로 유지하고, 아래 문제만 하드닝했다 —
**이번 Phase가 PASS하기 전에는 실제 사용자 `.codex`에 자동 Restore를 수행하지 않는다**는 제약을
세션 내내 지켰다(§10.4에서도 clone만 사용).

### 10.1 GitHub 리뷰로 발견되어 고친 문제

| # | 문제 | 실제로 있었던 문제 | 수정 |
|---|---|---|---|
| 1 | `CodexProcessGuard.SystemRunningProcessLister` | `process.Dispose()` 후 그 다음 줄에서 `process.ProcessName`을 다시 읽는 구조 — 실제 spawn된 프로세스로 테스트하니 `InvalidOperationException("No process is associated with this object.")`가 실제로 발생함을 확인(RED). `KnownPathMarker`(`@"OpenAI\Codex"`)는 리뷰에서 이중 백슬래시 버그로 지목됐으나 실제 파일을 바이트 단위로 확인한 결과 이미 올바른 단일 백슬래시였다 — 존재하지 않는 문제라 고치지 않고, 대신 실제 프로세스로 이 경로를 검증하는 테스트를 새로 추가했다. | `ProcessName`/`MainModule.FileName`을 dispose 전에 캡처해 저장한 뒤 `finally`에서 dispose하도록 수정(`CodexProcessGuardTests.cs`, 실제 spawn된 `cmd.exe` 자식 프로세스로 검증). |
| 2 | Codex 실행 여부 재확인 시점(TOCTOU) | 첫 확인 이후 Snapshot 생성 도중 사용자가 Codex를 다시 켤 수 있다. | Snapshot 완료 직후 · 첫 mutation 직전에 한 번 더 확인. 아직 mutation 전이면 Rollback 없이 그냥 `NotReady`로 중단(안전), mutation 후라면 기존 Rollback 경로. |
| 3 | `RestoreExecutor.Apply`가 호출자가 만든 `freshLocalCatalog`를 신뢰 | App이 예전 `_lastCatalog`를 그대로 넘기면 "그 순간"이 아닌 카탈로그로 판정할 위험. | production 진입점은 `ImportPlan`+`CodexHome` 경로만 받고, `CodexDetectionService`/`CodexCatalogBuilder`로 내부에서 직접 fresh catalog를 만든다. 테스트 전용 `internal` 오버로드만 카탈로그 빌더를 주입받는다. |
| 4 | backup 파일 재오픈 TOCTOU(Apply 내부) | Preflight가 backup을 열어 확인 → 닫음 → Planner가 같은 경로를 다시 오픈 → 닫음 → Executor가 다시 오픈. Phase 06_03이 Preview/Plan 사이에서 고친 것과 같은 부류의 문제가 Apply 내부에 다시 있었다. | `PinnedBackupSource`가 Apply 시작 시 파일을 **한 번만** `FileShare.Read`로 열어 전체 해시를 확정하고, Planner/Executor가 그 reader를 그대로 재사용한다. Preflight 이후·Planner 이후 바꿔치기 테스트로 write 0건을 확인했다(Windows의 `FileShare.Read` 자체가 외부 쓰기를 막는 것도 함께 확인). |
| 5 | SQLite Snapshot이 `-wal`/`-shm`을 놓침 | 원래 `-wal`이 "이미 존재할 때만" 조건부로 스냅샷 대상에 포함됐고 `-shm`은 아예 없었다. SQLite를 WAL 모드로 열면 이 사이드카가 새로 생길 수 있다. | SQL write가 하나라도 있으면 `-wal`/`-shm`을 **항상** 스냅샷 대상으로 등록(존재 여부 자체는 `SnapshotService`가 `ExistedBefore`로 판정) — 커밋 직후 강제 실패 테스트로 원래(둘 다 없던) 상태로 정확히 되돌아감을 확인. Rollback 후 `PRAGMA quick_check`+`SELECT COUNT(*) FROM threads`로 무결성도 재확인한다. |
| 6 | `.jsonl.zst` 새 segment 해시 버그 | `PlannedNewRolloutFile`을 만들 때 **논리(압축 해제 후) 길이/해시**(`newSlice.LogicalByteLength`/`Sha256Hex`)를 썼는데, `RolloutRestoreService.CreateNewFile`은 **원본(압축된) 바이트**를 복사해 그 원본 바이트로 검증한다 — 그러면 유효한 `.zst` 파일도 항상 검증 실패로 롤백됐다. | backup entry의 물리(원본) 길이/해시(`reader.GetEntryLength`+직접 해시)로 교체. RED로 확인(되돌리면 전용 zst 테스트가 `RolledBack`으로 실패) 후 복원. |
| 7 | IncomingAhead 메타데이터 병합 정책 부재 | fast-forward는 rollout 파일만 갱신하고 `threads` 행의 `updated_at`/`tokens_used`/`name` 등은 그대로 둬서, 내용은 최신인데 목록 UI에는 오래된 메타데이터가 남을 위험이 있었다. | `project_id`/`cwd`(대상 PC 고유값)는 항상 보존, `updated_at`/`updated_at_ms`/`tokens_used`/`has_user_event`는 incoming이 더 클 때만 갱신, `name`/`model`/`cli_version`은 로컬이 비어 있을 때만 채움 — `PlannedThreadMetadataUpdate`+`StateDatabaseWriter.UpdateMetadata`(동적 SET, non-null 필드만)로 구현. |
| 8 | New Import의 `thread_source` 누락 허용 | 감사 문서 자체가 이미 `thread_source == NULL`이면 Codex UI 목록에서 안 보일 수 있다고 지적했는데, Planner는 `model_provider`만 확인했다. | 사용자가 실제 선택한 대화(`IsSelected`)인데 `thread_source`가 비어 있으면 New Import 자체를 Blocked 처리(추측으로 값을 채우지 않는다). |
| 9 | post-apply validation이 얕음 | revision fingerprint만 확인하고, project 불일치 등은 계산만 하고 실패 처리하지 않았다. | `threads` 행 존재, `rollout_path`가 실제 기대 leaf를 가리키는지+그 파일 존재, revision fingerprint, New의 `model_provider`/`thread_source`/`archived` 일관성, 메타데이터 병합 결과, `project_id`가 해석 가능했는데 실제로 반영 안 됐으면 실패 — 전부 검사하도록 강화. |
| 10 | Update 중 archived 상태 변경 미차단 | fast-forward 도중 로컬과 incoming의 `archived` 여부가 다르면(물리 위치까지 바뀌어야 하는 경우) 처리 정책이 없었다. | `archived` 값이 다르면 해당 대화의 Update를 거부(Unsupported) — Plan 생성 자체를 막는다(부분 성공 없음, §5 정책과 동일). |

### 10.2 Fault Injection — 6개 지점 전부 개별 검증(요구사항 12)

| 지점 | 전용 테스트 | 확인한 것 |
|---|---|---|
| `AfterSnapshot` | `Snapshot_직후_강제_실패해도_일관된_결과를_돌려준다` | 아직 mutation 전이라도 Rollback 경로를 타 일관된 `RestoreResult`를 반환(이전엔 try 블록 밖이라 예외가 그대로 새어 나갔던 버그를 여기서 고쳤다 — RED로 확인). |
| `AfterFirstRolloutCreate` | 전용 Throwing 테스트 + 취소 테스트 | 새로 만든 rollout 파일이 삭제되고 원 상태로 복구. |
| `AfterRolloutAppend` | `rollout_append_직후_강제_실패해도_Rollback된다` | append된 내용이 append 이전 바이트로 정확히 복구. |
| `BeforeSqliteTransaction` | 전용 Throwing 테스트 | SQL write 전 실패 — DB 자체가 원래 상태 그대로(트랜잭션 시작조차 안 함). |
| `AfterSqliteCommit` | `SQLite_커밋_직후_실패하면_WAL_SHM도_원래_없던_상태로_되돌아간다` 등 | 커밋된 DB 내용 + `-wal`/`-shm` 사이드카까지 원 상태로 복구. |
| `BeforePostValidation` | `post_validation_직전_강제_실패해도_Rollback된다` | write는 전부 끝났지만 검증 직전 실패해도 Rollback. |

취소(Cancellation)는 mutation 전(즉시 중단, Rollback 불필요) / rollout 생성 후 / append 후 / SQLite
커밋 후 각각 별도 테스트로 확인했고, 전부 `RestoreOutcome.Cancelled`(실제 오류로 인한
`RolledBack`과 메시지·enum 값 모두 구분)로 끝나며 Rollback도 동일하게 수행됨을 확인했다.

### 10.3 새/변경된 핵심 타입

- `PinnedBackupSource`(`CodexBackupManager.Restore`) — backup 파일을 Apply 전체에 걸쳐 한 번만 열어
  재사용(§10.1-4).
- `BackupReader.OpenFromStream(Stream, bool leaveOpen)`(`CodexBackupManager.Backup`, additive) —
  `PinnedBackupSource`가 이미 열어 해시까지 낸 스트림을 그대로 재사용하기 위한 진입점.
- `PlannedThreadMetadataUpdate` + `StateDatabaseWriter.UpdateMetadata` — IncomingAhead 메타데이터
  병합(§10.1-7).
- `RestoreOutcome.Cancelled`(신규 enum 값) — 사용자 취소와 실제 오류로 인한 `RolledBack`을
  분리(§10.1, 요구사항 13).
- `RestoreExecutor.Apply`에 선택적 `Action<string>? onStatusChanged` 콜백 추가(순수 진행 상태 알림
  용도, 안전성 판단에는 관여하지 않는다) — Apply UI가 "안전성 확인 중"/"Snapshot 생성 중"/"적용
  중"/"검증 중"/"Rollback 중" 단계를 보여주는 데 쓴다(§10.5).

### 10.4 실제 `.codex` clone 기반 E2E(요구사항 14)

세션 스크래치패드의 1회성 콘솔 하네스(`real-clone-e2e`, 커밋되지 않음)로 다음을 확인했다:

1. 실제 `.codex`에서 `state_5.sqlite`(+`-wal`/`-shm`, 있었다면)와 `config.toml`/
   `session_index.jsonl`/`.codex-global-state.json`만 임시 폴더로 복사했다(`sessions`/
   `archived_sessions`는 빈 폴더로 새로 만듦 — 실제 세션 파일 내용은 복사하지 않았다).
2. 이 clone(실제 스키마 그대로, 당시 357개의 실제 thread 행 포함)을 대상으로 **production**
   `RestoreExecutor.Apply(plan, cloneHome)`(internal 테스트 오버로드가 아니라 실제 진입점 —
   `InternalsVisibleTo`가 없는 별도 어셈블리라 자연히 production 경로로 검증됐다)를 세 번 호출:
   합성 New Import 1건, archived New Import 1건, segment/`history_base` 조상이 있는 New Import
   1건.
3. 결과: clone의 thread 행이 357 → 360(정확히 +3)으로 늘었고, 원래 있던 실제 thread 3개를
   무작위로 뽑아 해시 비교한 결과 변경 없음. archived New는 실제로 `archived_sessions\` 아래에
   생성됐다.
4. 하네스 실행 전/후 **실제 원본** `C:\Users\User\.codex`의 `state_5.sqlite`/`session_index.jsonl`/
   `.codex-global-state.json`/`config.toml` SHA-256을 `Get-FileHash`로 비교 — 이 세션 전체에서
   기록해 온 값과 완전히 동일함을 재확인했다(§10.7에도 최종 재확인 기록).

**의도적으로 검증하지 않은 것**: `IncomingAhead`(fast-forward) 계열은 clone에 실제 rollout 파일
내용을 넣어야 재현할 수 있는데, `sessions\`/`archived_sessions\`를 비운 채로 진행했으므로(사용자가
명시적으로 승인하지 않은, 실제 대화 원문을 스크래치 영역에 복사하는 작업을 시간/승인 범위 밖으로
판단해 하지 않았다) 이번 clone E2E는 New 계열만 검증한다. IncomingAhead의 안전성 자체는
`Restore.Tests`의 합성 fixture로 충분히(요구사항 5/6/7 각각 전용 테스트로) 검증했지만, **실제
프로덕션 rollout 파일 형태(줄 구분, 인코딩 등)와의 호환성**까지 clone으로 재확인하지는 못했다 —
알려진 미검증 항목으로 남긴다(§10.6).

### 10.5 Apply UI

`MainViewModel`에 `ApplyCommand`/`CancelApplyCommand`, `IsApplying`, `ApplyStatusText`,
`KnownLimitationsText`(정적, 항상 표시)를 추가했다. 흐름:

1. `ApplyCommand`의 `CanExecute`는 `_currentImportPlan is not null && !IsApplying`뿐이다 — **실제
   안전성 판단은 클릭 시점에 `RestoreExecutor.Apply`가 fresh preflight/backup pin/Operation Plan
   재검증으로 전부 다시 한다**(이 ViewModel은 Preview/Plan을 다시 해석하지 않는다).
2. 클릭하면 사용자가 지정한 확인 문구(`ConfirmDialog.Confirm`, WPF `MessageBox`)를 그대로 띄운다:
   "백업 내용을 Codex에 적용합니다. / 적용 전에 현재 상태의 복구용 Snapshot을 생성합니다. / Codex가
   완전히 종료되어 있어야 합니다. / 계속하시겠습니까?" — "예"가 아니면 아무 것도 하지 않는다.
3. 확인 후 `Export`/새 `Import Preview`/프로젝트 경로 재지정/Codex 폴더 변경을 전부 막고(각 커맨드의
   `CanExecute`에 `!IsApplying` 추가, 경로 재지정은 델리게이트 자체에서 직접 차단), `Task.Run`으로
   production `RestoreExecutor.Apply`를 호출한다. 진행 콜백(`onStatusChanged`)은 백그라운드 스레드에서
   호출되므로 호출 시점의 `SynchronizationContext`로 `Post`해 UI 스레드에서만 `ApplyStatusText`를
   갱신한다.
4. 성공(`Succeeded`)/변경 없음(`NothingToDo`)이면 frozen Plan을 무효화한다(다시 적용하려면 새
   Import Preview부터 다시 시작해야 한다) — 실패/취소/Rollback이면 Plan은 그대로 남아 사용자가 다시
   시도할 수 있다.
5. `KnownLimitationsText`는 이 backup에 실제로 해당하는지와 무관하게 Import Preview 화면에 항상
   표시한다(요구사항 16) — Desktop 사이드바 반영 미검증(§10.6), `local_image` 미지원, 새 프로젝트
   자동 생성 미지원, `.jsonl.zst` Update 미지원, Diverged 자동 적용 미지원.

`MainViewModelApplyTests.cs`(App.Tests, 신규)로 확인: Plan이 없으면 `ApplyCommand.CanExecute`가
`false`, 확인 대화상자에서 거부하면 아무 것도 호출되지 않음(RED→GREEN으로 확인 — 가드를 임시로
비활성화하니 실제로 실패), 완전히 동일한 두 Codex Home 사이의 Apply는 `NothingToDo`로 끝나고 Plan을
무효화함(이 경로는 `CodexProcessGuard.SystemRunningProcessLister`를 포함한 실제 production
진입점을 그대로 탄다 — 테스트 프로세스 이름이 `Codex`가 아니므로 통과한다).

### 10.6 알려진 한계(Phase 07_01 시점 최종)

- **Codex Desktop 사이드바에 프로젝트별로 정확히 표시되는지 미검증**(§1.C/§1.E, 요구사항 11) —
  Core/CLI의 `threads.project_id` authority는 공식 소스로 확정했지만, Electron Desktop이 그 값을
  실제로 반영하는지는 Electron 소스가 없어 조사하지 못했다. Phase 07_01도 `.codex-global-state.json`
  을 쓰지 않는다.
- **실제 `.codex` clone 기반 E2E는 New 계열만 검증**했다 — IncomingAhead(fast-forward)는 여전히
  합성 fixture로만 검증했다(§10.4).
- IncomingAhead가 한 번에 여러 개의 신규 segment를 만드는 복합 케이스(로컬 파일이 세그먼트
  2개 이상)는 코드는 지원하지만 전용 테스트로 검증하지 못했다(Phase 7 §9.5부터 이어지는 항목,
  Phase 07_01에서도 추가 검증 없음).
- `local_image` 첨부 실제 파일은 복원하지 않는다(레코드는 유지, §2).
- 새 로컬 프로젝트 자동 생성은 지원하지 않는다 — 해석 가능한 기존 프로젝트가 없으면 "기타 대화"로
  Import된다(§1.C).
- `Diverged`는 자동 merge/backup 교체를 지원하지 않는다 — Apply 자체가 전체 차단된다(전용 테스트로
  write 0건 확인).
- `.jsonl.zst`로 압축된 대화의 Update(이어받기)는 지원하지 않는다 — 항상 거부한다.

### 10.7 전체 테스트 수 / 실제 사용자 `.codex` 무변경(Phase 07_01 최종)

`Domain 64 + Codex 200 + Backup 88 + Restore 30 + App 94 = 476건 전부 통과`, `dotnet build` 경고/
오류 0. `Restore.Tests`의 자동화 테스트는 전부 `TestCodexHomeBuilder` 합성 fixture 또는 임시 temp
Codex Home에서만 동작한다 — §10.4의 clone 하네스만 실제 `.codex`의 상태 DB/global-state/config를
읽어(그리고 clone에) 썼을 뿐, **실제 원본 `C:\Users\User\.codex`에는 세션 전체를 통틀어 단 한 번도
쓰지 않았다** — Export/Preview 등 읽기 작업 전후, 그리고 이번 clone E2E 하네스 실행 전후 모두
`state_5.sqlite`/`session_index.jsonl`/`.codex-global-state.json`/`config.toml`의 SHA-256이
동일함을 `Get-FileHash`로 확인했다(최종 재확인:
`state_5.sqlite=57C75D6B58045E4DDB3EFD5B5696C120E653661A850C6BD4A7B5FAD4474908D6`,
`session_index.jsonl=050C3D8505735E6CD08BDB1650DE733B6A30B55EE4F5E75F9BEB4D99CC82993E`,
`.codex-global-state.json=707D63DFDC779E6A2324FDD602F71997CA14CC4583A7CFFCCADE3681DABA7C50`,
`config.toml=A081B92A5F4F099692B1AA6EA5154CA88A9E135644F1ACC0913CF2A9E8A6A10E`).

---

## 11. Phase 07_02 — Release Safety Gate / Full-Clone E2E / Crash Recovery

실제 다른 사용자에게 EXE를 배포하기 전 마지막 Restore 안전성 검증 단계. Phase 7/07_01의 아키텍처는
그대로 두고, GitHub 코드 리뷰/재검토로 나온 안전성 문제만 고쳤다. 이번 Phase가 PASS하면
Phase 8(self-contained EXE + Release QA)로 넘어간다.

### 11.1 In-place append → temp + atomic replace(요구사항 6)

기존 `RolloutRestoreService.AppendToFile`은 기존 rollout 파일에 `Seek(end)+CopyTo`로 **직접**
append했다. 정상 예외라면 Snapshot Rollback이 처리하지만, 프로세스 강제 종료/정전 중에는
catch/Rollback 자체가 실행되지 않아 원본이 반쯤 쓰인 상태로 남을 위험이 있었다.

고친 방식: (1) 원본을 같은 디렉터리의 temp로 streaming copy(전체 메모리 로드 없음) (2) incoming
delta를 그 temp 뒤에 이어붙임 (3) temp 전체 길이/해시 검증 (4) `Flush(flushToDisk: true)`로 디스크에
내림 (5) `File.Move(..., overwrite: true)`로 원본을 atomic 교체(Windows에서 같은 볼륨 내 이동은
원자적이다). 이 과정에 새 fault-injection 지점 3개(`DuringAppendTempWrite`/`BeforeAtomicReplace`/
`AfterAtomicReplace`)를 추가해 각 지점에서 강제 실패해도 원본이 안전한지(temp 작성 중/replace
직전) 또는 Rollback으로 정확히 복구되는지(replace 직후)를 각각 전용 테스트로 확인했다(RED→GREEN).
temp는 `FileMode.Create`로 열어 이전 크래시가 남긴 잔재가 있어도 재시도를 막지 않는다. 큰 파일에서도
스트리밍임을 확인하기 위한 수십 MB급 append 테스트도 추가했다(288MB 실측 사례의 축소판).

### 11.2 실제 rollout 파일 기반 IncomingAhead E2E(요구사항 2/3) — 이번 Phase의 핵심 검증

Phase 07_01의 "real clone E2E"는 상태 DB 등 4개 파일만 clone하고 `sessions`/`archived_sessions`는
비워 뒀다 — 그래서 New Import만 검증할 수 있었다. 이번에는 세션 스크래치패드 하네스(커밋되지
않음, 세션 종료 시 삭제)로 **`sessions`/`archived_sessions`를 포함한 `.codex` 전체를 read-only로
clone**했다.

절차:
1. 실제 `state_5.sqlite`(read-only)에서 안전한 후보 thread를 골랐다 — `thread_source='user'`,
   비archived, `.jsonl`(비압축), 6줄 이상, **다른 thread의 `history_base` 조상으로 참조되지 않음**을
   전부 만족하는 것만(대화 원문/제목/경로는 어떤 로그/보고서에도 남기지 않았다 — thread id만 내부
   진행 로그에 출력).
2. 실제 `.codex` 전체(`state_5.sqlite`(+`-wal`/`-shm`), `config.toml`, `session_index.jsonl`,
   `.codex-global-state.json`, `sessions/**`, `archived_sessions/**`)를 `newerHome`으로
   복사(read-only 원본 접근, 총 약 1.5GB)한 뒤, `newerHome`을 그대로 한 번 더 복사해 `olderHome`을
   만들었다(로컬→로컬 복사라 원본은 이 시점부터 전혀 관여하지 않는다).
3. `olderHome`에서만 후보 thread의 rollout 파일을 "실제 유효 record boundary"에서 정확히 잘랐다 —
   **처음에는 `File.ReadAllLines`+`File.WriteAllLines`로 잘랐다가 `Diverged`로 잘못 판정되는 것을
   발견했다**: `WriteAllLines`가 `Environment.NewLine`(Windows에서 `\r\n`)으로 다시 join해, 원본이
   `\n`만 쓰던 줄바꿈 바이트 자체를 바꿔버려 "남은 부분"조차 원본과 byte-for-byte 같지 않게 만든
   것이 원인이었다(RED). 원본 바이트를 그대로 두고 정확한 개행 바이트 위치에서만 자르도록
   고치자(byte-precise truncation) 올바르게 `IncomingAhead`로 판정됐다(GREEN) — 이 자체가 "rollout
   바이트를 조금이라도 바꾸면 안 된다"는 이 프로젝트의 핵심 원칙을 실제 데이터로 재확인한
   사례다.
4. `newerHome` 기준으로 실제 프로덕션 카탈로그(357개 대화 전체)를 빌드하고, 후보 thread **하나만**
   `.codexbackup`으로 Export했다.
5. `olderHome`(잘린 파일) 기준으로 `ImportPreviewBuilder.Build` → 후보 thread의 relation이 정확히
   **`IncomingAhead`**로 판정됨을 확인했다.
6. `olderHome`에 실제 production 진입점(`RestoreExecutor.Apply(plan, olderHome)` — internal 테스트
   오버로드가 아니라 실제 앱이 쓰는 것과 동일한 경로, 실제 `CodexProcessGuard.SystemRunningProcessLister`
   포함)으로 Apply했다. 결과: **`Succeeded`**(post-apply validation까지 통과). Apply 후
   `olderHome`의 rollout 파일이 `newerHome`(=원본 온전한 내용)과 **byte-for-byte 완전히 동일**함을
   확인했다. `olderHome`의 다른 thread 3건(무작위 표본)의 DB 행 해시도 Apply 전후 무변경.
7. 하네스 실행 전/후 **실제 원본** `C:\Users\User\.codex`의 4개 source-of-truth 파일 SHA-256이
   전부 동일함을 재확인했다(§11.6 최종 재확인 값과 일치).
8. 하네스와 그 안의 clone(약 3GB, 실제 대화 내용 포함) 전체를 세션 종료 전에 삭제했다.

**의도적으로 검증하지 않은 것**: segment-transition(여러 물리 segment 파일에 걸친) IncomingAhead는
이번에도 실제 clone으로 재현하지 않았다 — 안전하게 재현할 만한 실제 다중 segment 후보를 찾는
추가 조사가 필요해 이번 Phase 범위 밖으로 남긴다(합성 fixture로는 이미 검증됨, §9.2). 물리적으로
안전하지 않아 Blocked돼야 하는 실제 사례도 이번에는 만나지 못했다(고른 후보가 안전한 경우였다) —
Blocked 경로 자체는 합성 fixture로 이미 충분히 검증됐다(§9.2/`RestoreOperationPlanner`).

### 11.3 Rollback post-check가 더 이상 live target을 다시 열지 않는다(요구사항 5)

기존 `RollbackService`는 파일 복원 → 재검증 → **복원된 state DB를 다시 열어** `PRAGMA quick_check`를
확인하는 순서였다. 이 프로젝트는 실제 `.codex`에서 ReadOnly SQLite 연결도 WAL index 재구성으로
`-shm`을 다시 건드릴 수 있음을 실측했었다(Phase 07_01) — 그런데 "복구 결과를 확인하는 절차 자체가
그 결과를 또 바꿔버릴" 위험은 그때 미처 못 고쳤다.

고친 방식: 복원된 DB(+`-wal`/`-shm`, 있으면)를 **별도 임시 검증 폴더로 복사**한 뒤 그 복사본만 열어
`quick_check`+`threads` 조회를 확인하고, 검증이 끝나면 그 임시 폴더는 지운다 — target 파일은 이
과정에서 단 한 번도 다시 열리지 않는다. 그리고 이 확인이 끝난 뒤 target의 DB/WAL/SHM 해시/존재
상태를 **한 번 더** 재확인한다(무언가 target에 영향을 줬다면 여기서 잡힌다).

실제 WAL 있음+SHM 있음/WAL 있음+SHM 없음/둘 다 없음 3가지 fixture로 직접 테스트했다
(`RollbackServiceShmSafetyTests`). **RED로 실제 버그를 재현**했다 — 검증 코드를 예전 방식(live
target을 다시 여는 방식)으로 되돌리자, WAL 있음 케이스 2건 모두 실패했다(`복구 후 해시가
snapshot과 다릅니다(state-db-shm)` 등 — 실제로 `-shm`이 변경됨을 직접 관찰했다). 고친 코드로
되돌리자 재통과했다.

### 11.4 New Import의 `threads.cwd` remap(요구사항 4) — 공식 소스 조사로 확정

**조사 결과(공식 `openai/codex` `codex-rs` 소스, 실제 파일/함수 인용)**: `threads.cwd`는 단순
표시/필터용이 아니다. `tui/src/app/resume_config.rs::resume_config_for_target()`이 이 값(정확히는
`state/src/runtime/threads.rs`의 SQL SELECT를 거친 `ThreadMetadata.cwd`)을 thread를 다시 열 때의
작업 디렉터리 후보로 쓴다 — `tui.resume_cwd = "session"` 설정이면 **그대로, 존재 확인 없이**
채택되고(`resume_config.rs` L82-84), 기본 설정이면 `cwd_prompt.rs`의 선택 프롬프트가 이 값을
기본 강조 선택지로 제시한다(`CwdPromptScreen::new`). 즉 원본 PC 경로를 그대로 두면 대상 PC에
**존재하지 않는 폴더가 기본값으로 뜨거나 그대로 채택**될 수 있다. (반대로 turn replay 자체의
작업 디렉터리 폴백은 `codex_thread.rs`의 `config.cwd`이지 `threads.cwd`가 아니다 — 이 부분은
Phase 7의 기존 결론과 일치한다. 별개의 메커니즘이다.) `project_id`와의 SQL 상관관계는 찾지
못했다(project 배정은 순수 FK, cwd 매칭과 무관).

**정책**: New Import에서 `project_id`가 실제로 대상 PC의 로컬 프로젝트로 해석됐을 때만(=
`ResolvedProjectId`가 있을 때만) `threads.cwd`도 그 프로젝트의 대상 PC 경로로 remap한다
(`RestoreOperationPlanner`의 `resolvedTargetCwd`, `StateDatabaseWriter.InsertThread`). 해석
불가(기타 대화)면 원본 `OriginalCwd`를 그대로 쓴다 — 더 나은 값이 없다. **rollout JSONL 내부의
`session_meta.cwd`는 이번에도 절대 건드리지 않는다** — SQLite 컬럼에만 적용되는 정책이다(공식 조사
결과 향후 어떤 backfill이 SQLite `cwd`를 rollout의 `session_meta.cwd`에서 다시 파생시킬 수도
있다는 근거도 나왔다 — `state/src/extract.rs` — 그래서 이 remap이 "완벽히 영구적"이라고
주장하지는 않는다. 그럼에도 원본 PC의, 대상 PC에 존재하지 않는 경로를 그대로 두는 것보다는
이 remap이 안전하다). RED→GREEN으로 확인: remap 로직을 되돌리면 전용 테스트가 실패한다.

### 11.5 Durable transaction journal / incomplete-apply 복구(요구사항 7/8)

Snapshot만으로는 프로세스가 강제 종료됐을 때 "복원 도중 죽었다"는 사실 자체를 다음 실행이 알 방법이
없었다. `RestoreTransactionJournal`(`Prepared`/`Applying`/`Completed`/`RolledBack`)을 Snapshot
디렉터리 안 `restore-transaction.json`에 atomic write(temp+flush+move)로 기록한다.

- Snapshot 검증 완료 → `Prepared`.
- 첫 mutation 직전 → `Applying`(atomic write).
- post-apply validation PASS → `Completed`(best-effort — 이 기록 자체가 실패해도 이미 끝난 성공을
  뒤집지 않는다).
- 정상 Rollback PASS → `RolledBack`(best-effort, 이유는 위와 대칭).
- **`RestoreExecutor.Apply` 진입점 자신이** 시작하자마자 `IncompleteApplyRecoveryService.FindIncomplete`로
  `Applying` 상태 snapshot이 있는지 확인하고, 있으면 새 Apply를 거부한다(`NotReady`) — UI 가드가
  없거나 우회돼도 Core가 최종적으로 막는다.
- `IncompleteApplyRecoveryService.Recover`는 사용자가 명시적으로 승인했을 때만(App의 [이전
  상태로 복구] 버튼) 호출된다 — Codex 실행 중이면 거부(복구 자체를 시작하지 않는다, 강제
  종료하지 않는다). manifest를 다시 읽어 기존 `RollbackService.Rollback`으로 byte-for-byte 복구하고
  journal을 `RolledBack`으로 갱신한다.
- **또한 Snapshot 이전(=아직 아무것도 안 쓴 상태) 취소가 그대로 밖으로 새어 나가 "예기치 않은
  오류"처럼 보이던 버그를 고쳤다**(요구사항 8) — `RestoreExecutor.Apply`가 이제 이 구간 전체를
  `OperationCanceledException` 전용으로 잡아 깨끗한 `Cancelled`를 돌려준다(Rollback 불필요 — 아직
  아무것도 안 썼다). App의 확인 대화상자에서 취소하는 것과는 별개로, Preflight/hash 계산 도중의
  취소가 대상이다. RED로 확인: 이 catch를 비활성화하면 `OperationCanceledException`이 그대로
  테스트 밖으로 튀어나옴을 확인했다.

**실제 프로세스 강제 종료 재현(요구사항 7 "가능하면")**: 별도 헬퍼 프로젝트
`CodexBackupManager.Restore.CrashSim`(테스트 전용, 제품에는 포함 안 됨)을 새로 만들어, 자식
프로세스로 production `RestoreExecutor.Apply`를 실제로 실행시키고, 지정된 fault-injection
지점(`AfterAtomicReplace`/`AfterSqliteCommit`)에 도달하면 sentinel 파일로 신호만 남기고 블록하게
한 뒤, 부모 테스트가 **실제로 `Process.Kill(entireProcessTree: true)`** 호출한다 — .NET 예외가
아니라 진짜 OS 레벨 강제 종료라 `RestoreExecutor`의 catch/Rollback이 전혀 실행되지 않는다. 두
경우 모두 다음 "실행"(`IncompleteApplyRecoveryService.FindIncomplete`+`Recover`)이 정확히
`Applying` 상태를 찾아내 정상 복구함을 확인했다(`CrashRecoveryIntegrationTests`).

### 11.6 Apply UI 정리(요구사항 9) / README 정책 문구(요구사항 10)

`ApplyCommand`의 `CanExecute`를 `Plan 존재`에서 `Plan 존재 && IsApplyReady`로 강화했다 — Diverged/
Unverifiable이 섞인 Plan은 이제 버튼 자체가 비활성화된다(Core의 최종 Preflight 차단은 그대로
유지, 이건 1차 UI 판단일 뿐이다). 프로젝트 "폴더 선택" 버튼도 Apply 진행 중에는 비활성화되도록
`InverseBooleanConverter`를 추가해 바인딩했다. 완료되지 못한 이전 Apply가 있으면 새 Apply를
막고("이전 복원 작업이 완료되지 않았습니다") [이전 상태로 복구] 버튼을 제공하는 배너를 추가했다.

README의 "SQLite는 Mode=ReadOnly로만 연다"는 문구는 Phase 7 이후 실제 동작과 모순됐다 — "탐색/
Viewer/Export/Import Preview는 Read-Only, 실제 write는 Apply에서만(Codex 종료 확인 + fresh
preflight + Snapshot 이후 제한적으로) 일어나고 실패하면 Rollback한다"로 정정했다.

### 11.7 Codex Desktop 사이드바(요구사항 12) — 여전히 미검증, 시도하지 않음

Core/CLI의 `threads.project_id` authority는 그대로 유지한다(변경 없음). Electron Desktop이 이 값을
실제로 반영하는지는 여전히 확인하지 못했다. `CODEX_HOME` 환경변수는 Codex **CLI**가 인식하는
공식 오버라이드임을 실측으로 이미 확인했지만(`docs/codex-storage-format.md`), Codex **Desktop**
(Electron 셸)이 같은 변수를 존중하는지는 확인된 바 없다 — Electron 소스가 이 저장소에 없어 정적으로
확인할 수 없고, 실제로 Desktop 앱을 격리된 프로필로 띄워 실측하는 것은 이번 Phase의 안전 범위(실제
사용자 프로필/앱 상태를 건드리지 않는다) 밖이라고 판단해 시도하지 않았다. 추측으로
`.codex-global-state.json`을 수정하지 않는다는 원칙도 그대로 유지한다. **Release 시점의 알려진
한계로 남긴다** — 확실한 격리 방법이 공식적으로 확인되면 향후 Phase에서 재검토한다.

### 11.8 Phase 07_02 최종 테스트 수 / 실제 원본 무변경

`Domain 64 + Codex 200 + Backup 88 + Restore 47 + App 96 = 495건 전부 통과`, `dotnet build`
(Debug/Release 둘 다) 경고/오류 0. 새로 생긴 테스트: `RollbackServiceShmSafetyTests`(3),
`CrashRecoveryIntegrationTests`(2, 실제 자식 프로세스 강제 종료 포함), atomic append fault
injection 3건 + 대형 파일 스트리밍 1건 + durable journal/incomplete-apply 6건 + cwd remap 2건
(전부 `RestoreExecutorTests.cs`에 추가). 세션 스크래치패드의 IncomingAhead 하네스(§11.2, 커밋 안
됨)까지 포함해 이번 Phase 전체에서 **실제 원본 `C:\Users\User\.codex`에는 단 한 번도 쓰지
않았다** — 작업 전/중/후 반복 확인한 4개 source-of-truth 파일 SHA-256이 세션 전체에서 완전히
동일했다(`state_5.sqlite=57C75D6B58045E4DDB3EFD5B5696C120E653661A850C6BD4A7B5FAD4474908D6`,
`session_index.jsonl=050C3D8505735E6CD08BDB1650DE733B6A30B55EE4F5E75F9BEB4D99CC82993E`,
`.codex-global-state.json=707D63DFDC779E6A2324FDD602F71997CA14CC4583A7CFFCCADE3681DABA7C50`,
`config.toml=A081B92A5F4F099692B1AA6EA5154CA88A9E135644F1ACC0913CF2A9E8A6A10E`).

### 11.9 알려진 한계(Phase 07_02 시점 최종, 정직하게 남김)

- Desktop 사이드바 프로젝트 그룹핑 반영 여부(§11.7) — 미검증, 방법 없음.
- segment-transition IncomingAhead의 실제 clone 재현(§11.2) — 미검증, 합성 fixture로만 확인.
- 물리적으로 불안전해 Blocked돼야 하는 실제 사례(§11.2) — 이번 clone에서 만나지 못함, 합성
  fixture로만 확인.
- `threads.cwd` remap(§11.4)은 SQLite 값만 바꾼다 — rollout JSONL의 `session_meta.cwd`는 원본
  그대로이며, 향후 Codex 자체의 어떤 backfill 동작이 SQLite `cwd`를 다시 덮어쓸 가능성은 공식
  소스상 배제되지 않는다(빈도/조건 모두 불명).
- 다중 신규 segment(2개 이상)를 한 번에 만드는 복합 케이스 — Phase 7부터 이어지는 미검증 항목,
  이번에도 추가 검증 없음.
- `local_image`/새 프로젝트 자동 생성/`Diverged` 자동 merge/`.jsonl.zst` Update는 여전히 전부
  미지원(Unsupported).

## 12. Phase 07_03 — Final Restore Edge-Case Hardening

Phase 07_02를 사용자가 커밋한 뒤(HEAD `b298140`), 공유 가능한 EXE로 배포하기 전 마지막 GitHub
코드 리뷰에서 Restore edge case 3개가 발견됐다. 이 Phase는 그 3개만 고친다 — Restore Core의 기존
동작(§9~§11)은 전부 그대로 유지한다.

### 12.1 New rollout temp/atomic move + durability(요구사항 1/6)

`RolloutRestoreService.CreateNewFile`은 원래 temp를 `FileMode.CreateNew`로 열고 `File.Move(overwrite:
false)`로 target을 만들었다 — IncomingAhead append(§11.1)와 달리 durability/재시도 안전성을 갖추지
않았다. temp 작성 중 프로세스가 강제 종료되면 target은 아직 없고 temp만 남는데, 다음 Apply
시도가 같은 temp 경로에 `CreateNew`로 다시 쓰려다 `IOException`으로 실패할 수 있었다 — 실제
crash recovery hole이었다.

append와 동일한 패턴으로 고쳤다: temp를 `FileMode.Create`(덮어쓰기 허용)로 열어 이전 크래시의
잔재를 안전하게 재활용하고, 쓰기 후 `Flush(flushToDisk: true)`로 디스크에 내리고, 길이/해시를
검증한 뒤 `File.Move`로 target을 atomic하게 만들고, `finally`에서 temp를 정리한다. 새 fault
injection 지점 3개를 추가했다: `DuringNewRolloutTempWrite`(temp 작성 도중), `BeforeNewRolloutMove`
(검증까지 끝나고 이동 직전), `AfterNewRolloutMove`(이동 직후). `RestoreExecutor.ExecuteMutations`가
이 3개 지점 전부를 실제로 통과하도록 `faultInjection`을 `CreateNewFile`에도 넘긴다(이전에는 append
에만 넘겼었다).

검증:

- `New_rollout_temp_작성_중_강제_실패해도_target은_생성되지_않는다` / `New_rollout_move_직전_...`
  / `New_rollout_move_직후_...` — 3개 fault injection 지점 각각에서 target이 만들어지지 않거나
  (또는 만들어졌다면) Snapshot Rollback으로 정확히 지워지는지 확인.
- `stale_New_rollout_temp가_있어도_재시도가_성공한다` — 운영 코드와 동일한
  `RestoreOperationPlanner.Build`를 테스트에서 직접 호출해 실제 target 경로를 계산한 뒤, 그 자리에
  이전 크래시가 남긴 것과 같은 모양의 garbage temp 파일을 미리 만들어 두고 Apply가 그래도
  성공하는지 확인(RED로 먼저 확인: `FileMode.CreateNew`로 되돌리면 이 테스트가
  `IOException`으로 실패했다).
- `크래시_시뮬레이션_New_rollout_temp_생성_후_target_이동_전_강제_종료되면_다음_실행에서_복구되고_재시도가_성공한다`
  (`CrashRecoveryIntegrationTests`) — **진짜 자식 프로세스**를 `BeforeNewRolloutMove`에서
  `Process.Kill(entireProcessTree: true)`로 강제 종료한 뒤, target/thread가 전혀 생기지 않았음을
  확인하고, `IncompleteApplyRecoveryService.Recover`로 복구한 뒤, **같은 backup으로 다시 Apply해
  `Succeeded`까지 end-to-end로 확인했다**.

### 12.2 Incomplete Apply를 Codex Home별로 scope(요구사항 2)

이 프로그램은 수동 Codex Home 선택을 지원하므로, Home A에서 crash recovery가 필요한 상태로
남아 있어도 사용자가 Home B를 선택했다면 Home B의 Apply를 막으면 안 되고, [이전 상태로 복구]
배너도 Home B 화면에는 나타나면 안 된다 — 이전에는 `IncompleteApplyRecoveryService.FindIncomplete`가
`snapshotRoot` 아래 모든 Snapshot을 Home 구분 없이 반환해 이 문제가 있었다.

`IncompleteApplyRecoveryService.FindIncompleteForHome(snapshotRoot, codexHomePath)`를 추가했다 —
내부적으로 기존 `FindIncomplete`(전체 Home, 진단/테스트용으로 남겨 둠)를 호출한 뒤,
`CanonicalPath.AreSameLocation`(대소문자/`\\?\` prefix/trailing slash 차이를 정규화하는 기존
Phase 0 타입, `docs/codex-storage-format.md` §6)으로 journal(우선) 또는 manifest(journal이 손상돼
읽을 수 없을 때의 대체)의 `CodexHomePath`와 비교해 필터링한다. `RestoreExecutor.Apply`의 production
진입점과 `MainViewModel.RefreshIncompleteApplyState(codexHomePath)`(생성자 파라미터로 Home을
명시적으로 받도록 변경) 둘 다 이 scoped API로 갈아탔다.

Home도 manifest도 알 수 없는 극히 드문 경우(journal이 손상되고 manifest까지 없는 경우)는 조용히
무시하지 않고 안전 쪽으로 기울여 모든 Home에 노출한다 — 놓치는 것보다 과잉 경고가 낫다는
원칙이다.

검증: `FindIncompleteForHome은_다른_Home의_미완료_Apply를_돌려주지_않는다`,
`다른_Home을_겨냥한_미완료_Apply는_지금_Home의_Apply를_막지_않는다`(실제
`RestoreExecutor.Apply`가 Succeeded까지 끝나는 것으로 확인), App 레벨에서
`다른_Home의_미완료_Apply는_현재_Home에_나타나지_않고_그_Home을_다시_선택하면_나타난다`(두 개의
서로 다른 fixture Codex Home 사이를 오가며 배너 표시/은닉/재표시 확인, snapshot/journal 자체는
그대로 남아 있음도 함께 확인).

### 12.3 `IncompleteApplyRecoveryService.Recover` 자체의 consistency gate(요구사항 3)

이전 `Recover(snapshotDirectory)`는 호출자(UI)가 올바른 Applying snapshot만 넘긴다는 것을 사실상
신뢰했다 — journal 상태를 스스로 확인하지 않고 곧바로 `RollbackService.Rollback`을 실행했다.
즉 이미 `Completed`되거나 `RolledBack`된 snapshot을 실수로(또는 UI 버그로)다시 넘기면 **성공적으로
끝난 Apply 결과를 도로 되돌려 버리는** 위험이 있었다.

Core 레벨에서 스스로 재검증하도록 강화했다:

1. journal을 읽되 "없다"/"정상"/"손상됐다"를 구분하는
   `RestoreTransactionJournalStore.TryReadDetailed`(새로 추가, 기존 `TryRead`는 이를 감싼
   호환 유지용 wrapper)로 상태를 확인한다.
2. journal이 손상됐으면(`RestoreTransactionJournalReadStatus.Corrupt`) — Applying이었는지 이미
   끝난 뒤였는지 알 수 없으므로 "없는 것"처럼 조용히 넘어가지 않고 `RollbackFailedCritical`로
   보수적으로 거부한다(RecoveryStateUnknown 성격 — 사용자에게 수동 확인을 안내). journal 개념이
   아예 없던(파일 자체가 없는) 오래된 정상 Snapshot까지 전부 위험 상태로 오판하지는 않는다 —
   `Missing`은 별도로 취급한다.
3. journal이 있지만 `Applying`이 아니면(`Missing` 포함) — 이미 처리된 것으로 보고
   `RestoreOutcome.NotReady`로 조용히(비-CRITICAL) 거부한다. `Completed`/`RolledBack` snapshot을
   다시 Rollback하지 않는다.
4. journal의 `SnapshotId`/`CodexHomePath`가 manifest와 다르면(디렉터리가 잘못 합쳐졌거나 손으로
   편집된 경우) `RollbackFailedCritical`로 거부한다.
5. 호출자가 `expectedCodexHomePath`를 넘겼는데 manifest의 Home과 다르면(호출자 실수로 다른
   Home의 snapshot을 넘긴 경우) `NotReady`로 거부한다. `MainViewModel.RecoverIncompleteApplyAsync`는
   `FindIncompleteForHome`이 찾아 둔 Home을 항상 이 값으로 넘긴다(방어적 이중 확인).
6. 모든 검증을 통과했을 때만 실제 lock을 잡고(§12.4) Rollback을 실행한다.

검증: `Recover는_Completed_snapshot을_다시_되돌리지_않는다`,
`Recover는_RolledBack_snapshot을_다시_되돌리지_않는다`,
`journal과_manifest의_SnapshotId가_다르면_Recover를_거부한다`,
`journal과_manifest의_CodexHomePath가_다르면_Recover를_거부한다`,
`호출자가_기대한_Home과_manifest의_Home이_다르면_Recover를_거부한다`,
`journal_파일이_손상되어_있으면_Recover가_보수적으로_거부한다`(손상된 journal이
`FindIncomplete`에서도 `IncompleteApplyReason.JournalUnreadable`로 잡히는지 함께 확인) — 전부
"실제로 INSERT된 thread가 그대로 남아 있는지"로 되돌리지 않았음을 확인한다.

### 12.4 Restore 프로세스 간(inter-process) lock(요구사항 4/5)

한 앱 인스턴스 안에서는 `IsApplying`으로 막고 있었지만, 같은 EXE를 두 번 실행하면 서로 다른
프로세스가 같은 Codex Home에 동시에 Apply할 수 있었다 — 배포 전 반드시 막아야 하는 문제였다.

`RestoreProcessLock`(신규)을 추가했다 — Codex Home 경로를 `CanonicalPath`로 정규화한 뒤
SHA-256 해시로 `Local\CodexBackupManager.Restore.<hash32>` 형태의 named Mutex 이름을 만든다.
`RestoreExecutor.Apply`(production 진입점, 새로 분리한 내부 `ApplyLocked`가 실제 로직을 담당)와
`IncompleteApplyRecoveryService.Recover`(정합성 검증을 모두 통과한 뒤) 둘 다 이 lock을
`TimeSpan.Zero`(대기 없이 즉시 판정)로 시도하고, 이미 다른 프로세스가 잡고 있으면 즉시
`"다른 Codex Backup Manager 인스턴스가 이 Codex Home을 처리 중입니다."`로 `NotReady`(write
0건)를 돌려준다. 이전 소유자가 죽어서 lock이 `AbandonedMutexException`으로 넘어온 경우는 이번
호출이 정상적으로 획득한 것으로 처리하되, 바로 이어지는 incomplete-apply 검사(§12.2)가 그
이전 시도의 `Applying` journal을 그대로 잡아내 recovery를 요구한다 — lock 자체가 "이전 상태를
안전한 것으로 착각"하게 만들지 않는다.

검증:

- `RestoreProcessLockTests`(4건, 단일 프로세스·다중 스레드로 named Mutex 소유권 규칙을 빠르고
  결정적으로 검증 — 같은 Home은 한쪽만 획득, 다른 Home은 동시 획득 가능, 소유 스레드가 놓지 않고
  끝나면 다음 획득이 Abandoned로 처리됨, 대소문자/`\\?\` prefix가 달라도 같은 lock).
- `같은_Codex_Home에서_동시에_Apply를_시도하면_한쪽만_lock을_획득하고_다른_Home은_막히지_않는다`
  (`CrashRecoveryIntegrationTests`) — **진짜 두 프로세스**로 검증한다. `CodexBackupManager.Restore.CrashSim`에
  `lock-hold` 서브커맨드를 추가해 자식 프로세스가 지정된 Home의 lock을 실제로 잡고 sentinel
  파일로 신호를 남긴 뒤, 부모가 release 신호 파일을 만들 때까지 그대로 쥐고 있게 했다. 부모
  프로세스는 production `RestoreExecutor.Apply`로 (1) 같은 Home에 Apply를 시도해 `NotReady`임을,
  (2) 완전히 다른 Home에는 동시에 Apply해 `Succeeded`임을 확인한다.
- 기존 `크래시_시뮬레이션_...` 2건(§11.5)이 lock 도입 이후에도 그대로 GREEN이다 — 자식
  프로세스가 kill되면 그 프로세스가 쥐고 있던 lock도 OS가 abandon 처리하고, 부모의
  `Recover` 호출이 (같은 lock을 다시 잡고) 정상적으로 복구한다는 것을 방증한다.

### 12.5 기존 Phase 07_02 동작 회귀 없음

`PinnedBackupSource`/fresh catalog/atomic IncomingAhead replace/WAL·SHM Snapshot·Rollback/
disposable-copy quick_check/durable transaction journal/`threads.cwd` remap/`ApplyCommand`의
`IsApplyReady` 게이팅/`New`·`Identical`·`IncomingAhead`·`LocalAhead`·`Diverged`·`Unverifiable`
정책은 전부 코드 변경 없이 그대로다 — Phase 07_02의 기존 테스트 전체(§11.8의 495건)가 이번
Phase의 전체 테스트 실행에도 그대로 포함되어 GREEN이다.

### 12.6 Phase 07_03 최종 테스트 수 / 실제 원본 무변경

`Domain 64 + Codex 200 + Backup 88 + Restore 65 + App 97 = 514건 전부 통과`(Phase 07_02의
495건 + 이번 Phase 신규 19건), `dotnet build`(Debug/Release 둘 다) 경고/오류 0. 새로 생긴 테스트:
`RestoreExecutorTests.cs`에 New rollout 하드닝 4건 + Home-scope 2건 + Recover consistency 6건 =
12건, `CrashRecoveryIntegrationTests.cs`에 진짜 크래시/두 프로세스 통합 테스트 2건,
`RestoreProcessLockTests.cs`(신규 파일) 4건, `MainViewModelApplyTests.cs`에 Home 전환 시나리오
1건. 전체 실행을 연속 2회 그린으로 확인했다. 실제 원본 `C:\Users\User\.codex`의 4개
source-of-truth 파일 SHA-256은 이번 Phase 전/후로도 완전히 동일했다(§11.8과 같은 값 —
`state_5.sqlite=57C75D6B58045E4DDB3EFD5B5696C120E653661A850C6BD4A7B5FAD4474908D6`,
`session_index.jsonl=050C3D8505735E6CD08BDB1650DE733B6A30B55EE4F5E75F9BEB4D99CC82993E`,
`.codex-global-state.json=707D63DFDC779E6A2324FDD602F71997CA14CC4583A7CFFCCADE3681DABA7C50`,
`config.toml=A081B92A5F4F099692B1AA6EA5154CA88A9E135644F1ACC0913CF2A9E8A6A10E`) — 실제 rollout
clone도 이번 Phase에서는 별도로 만들지 않았다(순수 합성 fixture + 실제 자식 프로세스 crash
시뮬레이션만으로 전부 검증 가능했다).

### 12.7 알려진 한계(Phase 07_03 시점 최종)

- `RestoreProcessLock`은 named Mutex(`Local\` 네임스페이스) 기반이라 같은 Windows 로그인 세션
  안에서만 유효하다 — 여러 사용자 세션/원격 데스크톱 세션을 넘나드는 잠금은 범위 밖이다(단일
  사용자 데스크톱 앱을 전제하므로 의도적으로 범위를 좁혔다).
- lock 획득은 `TimeSpan.Zero`(즉시 판정)만 지원한다 — "잠깐 기다렸다가 자동 재시도"는 하지
  않는다(사용자에게 명확히 알리고 다시 시도하게 하는 편이 더 안전하다는 기존 UX 원칙과 일치).
- §11.9의 한계(Desktop 사이드바, segment-transition IncomingAhead 실제 clone, 물리적으로 불안전한
  실제 Blocked 사례, 다중 신규 segment 복합 케이스, `local_image`/새 프로젝트 자동 생성/`Diverged`
  자동 merge/`.jsonl.zst` Update 미지원)는 이번 Phase의 범위가 아니었으므로 그대로 남아 있다.
