# Codex Backup Manager — Phase 0 조사 보고서

조사일: 2026-09-11
조사 대상 PC: `k-2301160127` (Windows, x64)
조사 방식: **Read-Only**. `~/.codex` 폴더에 읽기 전용 권한만 받아 목록 조회 + 파일 복사(컨테이너로 스테이징) 후 분석.
**Codex 원본 파일은 단 한 건도 수정/삭제하지 않았음. Commit / Push 없음.**

---

## 1. 현재 Repository 상태

- **Codex Backup Manager 소스 Repository는 이 PC에 존재하지 않는다.** (사용자 확인: "아직 없음 / 새로 만들 예정")
- 홈 디렉터리 하위 후보 폴더를 전수 확인했으나 이 프로젝트에 해당하는 폴더 없음:
  - `~/source/repos` — 학습용 콘솔 프로젝트들 (`260408_BullsAndCows`, `ConsoleApp1` …)
  - `~/Documents/GitHub` — `CoinBox`, `D2D_Engine_Project`, `u3d260730_Chess`, `ue260802_aircraft` …
  - `~/Documents/Codex` — 날짜별 Codex 작업 폴더(`2026-07-01` … `2026-08-31`) + `mcp`
  - `~/Desktop` — 과제/빌드 폴더들
- 따라서 **Git 상태 / 기존 코드 / 기존 테스트 / 기존 기술 스택 결정 사항 모두 없음** (백지 상태).
- 개발 지침 `CLAUDE.md`는 Claude Project 문서(`CLAUDE (2).md`)로만 존재. → Repo 생성 시 루트에 `CLAUDE.md`로 복사할 것을 권장.

### 참고: 사용자의 기존 도구 스택 흔적
- `~/Documents/ChatGPT/260827_UProjectHub` → `.git / docs / scripts / src / tests` 구조
- `~/Desktop/UProjectHub-0.6.18-win-x64/UProjectHub-0.6.18-win-x64-self-contained`
  → **.NET `self-contained` publish 네이밍 규칙**. 이미 .NET 기반 win-x64 단일 배포 도구를 만들어 본 이력으로 판단됨.

---

## 2. 발견한 Codex 설치 / 데이터 경로

| 항목 | 값 | 근거 |
|---|---|---|
| **CODEX_HOME (실사용)** | `C:\Users\User\.codex` | `config.toml` → `[mcp_servers.node_repl.env] CODEX_HOME`, 그리고 global-state 키 `"local:C:\\Users\\User\\.codex"` |
| `%USERPROFILE%\.codex` | 존재 O — 위와 동일 경로 | 디렉터리 실측 |
| Codex CLI 실행 파일 | `C:\Users\User\AppData\Local\OpenAI\Codex\bin\fd4c151a749f3ab4\codex.exe` | `config.toml` → `CODEX_CLI_PATH` |
| Codex 런타임 | `C:\Users\User\AppData\Local\OpenAI\Codex\runtimes\cua_node\b58ca2eaa616c2da\bin\` | `config.toml` → `notify`, `node_repl` |
| 플러그인 런타임 캐시 | `C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\` | `config.toml` marketplaces |
| **Codex Desktop 버전** | **26.903.61454** | `config.toml` → `BROWSER_USE_CODEX_APP_VERSION` |
| **Codex CLI(core) 버전** | **0.153.4** (최신 세션 82건) | `session_meta.cli_version`, `threads.cli_version` |

> `CODEX_HOME` **환경변수 실제 값**은 이번 세션에 이 PC의 셸 도구(`device_bash`)가 없어 직접 읽지 못했다.
> config.toml이 자식 프로세스에 주입하는 값과 global-state의 host key가 모두 `C:\Users\User\.codex`이므로 실질적으로 확정이지만,
> **Phase 1 CodexLocator는 반드시 런타임에 환경변수를 직접 읽는 코드로 구현해야 한다** (하드코딩 금지).

### `cli_version` 이력 (버전 어댑터 필요성의 실증)
0.136 → 0.137 → 0.138 → 0.140 → 0.142 → 0.144 → 0.145 → 0.146 → 0.147 → 0.148 → 0.149 → 0.150 → 0.151 → 0.153
총 28개 버전이 한 PC의 데이터에 섞여 있다. 단일 포맷 가정은 즉시 깨진다.

---

## 3. 실제 Codex 저장 구조 (`C:\Users\User\.codex`)

### 3.1 대화 원본 (Source of Truth)
```
sessions\YYYY\MM\DD\rollout-<ISO8601(초,하이픈)>-<threadId>.jsonl
sessions\YYYY\MM\DD\rollout-<ISO8601>-<threadId>_<segmentId>.jsonl   ← 페이지네이션 세그먼트
archived_sessions\rollout-<ISO8601>-<threadId>.jsonl                 ← 아카이브 시 물리적 이동
```
- 실측: `sessions` 365개 파일 / **1,558,104,474 bytes (약 1.45 GB)**, 2026/06/02 ~ 2026/09/11
- 실측: `archived_sessions` 1개 파일 (74,855 bytes)
- 파일명 `_` 뒤 두 번째 UUID가 붙은 파일 14개 = **하나의 thread가 여러 rollout 파일로 분할 저장**됨
- `<threadId>`는 **UUIDv7** → 문자열 정렬 = 생성 시간 정렬
- ⚠️ 공식 소스 기준: **오래된 rollout 파일은 Zstandard(`.zst`)로 자동 압축**되고 resume 시 `.jsonl`로 되돌려진다.
  이 PC에는 현재 `.zst`가 0개지만, 다른 PC/미래 버전에서는 반드시 나타난다.

### 3.2 인덱스 / 상태 DB
| 파일 | 크기 | 역할 |
|---|---|---|
| `state_5.sqlite` | 33.1 MB | **대화/프로젝트 카탈로그 (Codex가 목록을 그리는 실제 인덱스)** |
| `thread_history_1.sqlite` | 325.3 MB | rollout JSONL → UI 아이템 **projection 캐시** (재생성 가능) |
| `session_index.jsonl` | 18.8 KB | `{id, thread_name, updated_at}` 만 담은 append 로그. 136줄 / 고유 129건 |
| `logs_2.sqlite` | 118.2 MB | 앱 로그 (백업 대상 아님) |
| `goals_1.sqlite` / `memories_1.sqlite` / `queue_1.sqlite` | 32~40 KB | 목표/메모리/큐 (현재 모두 0 rows) |
| `.codex-global-state.json` | 732.7 KB | **Electron 데스크톱 앱 상태 = 프로젝트↔대화 매핑의 현재 실소유자** |
| `.codex-global-state.json.bak` + `*.tmp-*` 잔여물 11개 | — | Codex 자체 atomic-write 흔적 (0바이트 tmp 다수) |
| `config.toml` | 8.2 KB | 모델/플러그인/MCP + **`[projects.'<lowercased path>'] trust_level`** |
| `sqlite\` 하위 | — | 구버전 DB 세트 + 별도 `codex-dev.db`(2.8 MB, 현재도 갱신 중), `codex-thread-summaries-dev.db` |
| `attachments\<uuid>\pasted-text.txt` + `pasted-text-attachments.json` | 41.6 KB + 16파일 | 붙여넣기 첨부 원본 |
| 기타 디렉터리 | — | `visualizations`, `worktrees`, `generated_images`, `computer-use`, `dictation-history`, `plugins`, `skills`, `rules`, `browser`, `cache`, `.sandbox*`, `rollout-migrations`(빈 폴더), `thread-writer-locks`, `process_manager`, `mcp-oauth-locks`, `ambient-suggestions`, `pets`, `node_repl`, `vendor_imports`, `tmp`, `.tmp` |

### 3.3 `state_5.sqlite` 스키마 (핵심)

`threads` — **352 rows**, 38 컬럼
```
id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
sandbox_policy, approval_mode, tokens_used, has_user_event, archived, archived_at,
git_sha, git_branch, git_origin_url, cli_version, first_user_message,
agent_nickname, agent_role, memory_mode, model, reasoning_effort, agent_path,
created_at_ms, updated_at_ms, thread_source, preview, recency_at, recency_at_ms,
history_mode, name, is_pinned, thread_section_id, section_position,
section_entered_at_ms, project_id
```
- `projects` (45 rows): `id, name, metadata, position, created_at_ms, updated_at_ms`
- `project_roots` (52 rows): `project_id, position, path`
- `thread_spawn_edges` (27 rows): `parent_thread_id, child_thread_id, status`
- `thread_sections` (1 row: "Pinned"), `thread_artifacts`(0), `thread_dynamic_tools`(0)
- `rollout_migration_state` (1 row): `migration_id='legacy_to_paginated_v1'`
- `rollout_migration_skipped_rollouts`(0), `backfill_state`(status=complete), `external_agent_config_imports`(0), `remote_control_enrollments`(0)
- `_sqlx_migrations` **52 rows** (최신: 52 `projects recency`, 51 `thread artifacts`, 49 `projects`)
- `created_at_ms` / `updated_at_ms` / `recency_at_ms`를 채우는 **AFTER INSERT/UPDATE 트리거 4개 존재** → 직접 INSERT 시 동작함

### 3.4 실측 분포 (매우 중요)
| 구분 | 값 |
|---|---|
| `thread_source='user'` | **112** ← 사람이 실제로 한 대화 |
| `thread_source='subagent'` | 95 |
| `thread_source='guardian_review'` | **145** ← Codex 내부 리뷰 스레드 |
| `history_mode='paginated'` / `'legacy'` | 333 / 19 |
| `archived=1` | 1 |
| `cwd`에 `\\?\` prefix | **346 / 352** |
| `threads.project_id IS NOT NULL` | **0 / 352** |
| `thread_history_1.sqlite`에 아이템이 있는 thread | **134 / 352** (lazy projection) |
| rollout 파일은 있는데 `session_index.jsonl`에 없음 | 223건 |

### 3.5 `thread_history_1.sqlite` 스키마
```
thread_items (33,999 rows)  : thread_id, turn_id, item_id, rollout_ordinal,
                              created_at_ms, item_json, item_type, updated_at_ordinal
thread_turns (1,585 rows)   : thread_id, turn_id, rollout_ordinal, status, error_json,
                              started_at, completed_at, duration_ms,
                              first_user_item_id, final_agent_item_id,
                              rollout_byte_offset, rollout_end_ordinal, rollout_end_byte_offset
thread_history_projection_state (347 rows): thread_id, next_rollout_byte_offset, next_rollout_ordinal
thread_realtime_items (0)
```
`item_type` 분포: `reasoning` 12,376 / `commandExecution` 7,420 / `agentMessage` 7,237 / `fileChange` 2,776 / `mcpToolCall` 1,923 / **`userMessage` 1,611** / `webSearch` 242 / `contextCompaction` 176 / `imageView` 163 / `collabAgentToolCall` 40 / `imageGeneration` 22 / `subAgentActivity` 10 / `dynamicToolCall` 3

**결정적 증거**: thread `01a08e64-…0755`의 `next_rollout_byte_offset = 120580`이
rollout 파일 `rollout-2026-09-11T11-55-17-01a08e64-…0755.jsonl`의 **파일 크기 120,580 bytes와 정확히 일치**.
→ `thread_history_1.sqlite`는 **rollout JSONL을 byte offset 기준으로 따라가며 만든 파생 캐시**이며, JSONL이 원본이다.

---

## 4. 프로젝트와 대화를 식별할 수 있는 방법

**현재 이 PC의 Codex는 "프로젝트 ↔ 대화" 연결이 마이그레이션 도중 상태다.** 3중 구조를 모두 읽어야 한다.

### (A) 정식(신규) 경로 — `state_5.sqlite`, 아직 비어 있음
- `projects` 45건 + `project_roots` 52건은 채워져 있음
  - 예: `01a07b96-c191-7732-9b08-134558856389` / `ueGI8_LoginServer` / roots=`['C:\_UserProjects\Unreal\L20260713_Day03']`
- 그러나 `threads.project_id`는 **352건 전부 NULL**
- 원인 확정: `.codex-global-state.json`
  ```json
  "app-server-projects-migration-by-host": {
    "local:C:\\Users\\User\\.codex": { "version":1, "projectsMigrated": true, "threadAssignmentsMigrated": false }
  }
  ```
  → 프로젝트는 SQLite로 옮겨졌지만 **스레드 할당은 아직 안 옮겨졌다.**

### (B) 현재 실제로 유효한 경로 — `.codex-global-state.json` (Electron 상태)
| 키 | 내용 |
|---|---|
| `local-projects` (45) | `{ id, name, rootPaths[], createdAt, updatedAt }`<br>id 형식 2종: `local-<md5>` 와 순수 UUIDv4 |
| **`thread-project-assignments` (60)** | `threadId → { projectKind:"local", projectId }` ← **직접적인 프로젝트↔대화 링크** |
| `app-server-project-id-by-legacy-project-id-by-host` | legacy UUID → `state_5.projects.id`(UUIDv7) **변환 맵** |
| `projectless-thread-ids` (11) | 프로젝트 미지정 대화 = "기타 대화" |
| `thread-workspace-root-hints` (11) | threadId → 작업 루트 힌트 |
| `thread-projectless-output-directories` (11) | threadId → `…\outputs` |
| `pinned-project-ids` (5) / `pinned-thread-ids` (1) / `project-order` (45) / `selected-project` | UI 상태 |
| `thread-writable-roots` (66) | threadId → 쓰기 허용 루트 배열 |

### (C) 폴백 경로 — `cwd` 정규화 (CLAUDE.md §6의 방식)
- `threads.cwd` 는 **346/352가 `\\?\C:\...` 형태**, `session_meta.cwd`는 **prefix 없는 `C:\...`**
- `config.toml`의 `[projects.'...']` 키는 **전부 소문자**
- 따라서 3종 표기가 동시에 존재:
  ```
  C:\_UserProjects\Unreal\Balhwajeom_Project      (session_meta)
  \\?\C:\_UserProjects\Unreal\Balhwajeom_Project  (state_5.threads.cwd)
  c:\_userprojects\unreal\balhwajeom_project      (config.toml key)
  ```
- Canonical 규칙 제안: `\\?\` / `\\?\UNC\` prefix 제거 → 구분자 `\` 통일 → trailing `\` 제거 → **`ToLowerInvariant()` + Unicode NFC 정규화** (한글 폴더명이 매우 많음: `기획`, `펫 만들기`, `미디어 분석`, `ㅁㄴㄻㄴㄹ`)
- 원본 문자열은 그대로 보존하고 비교용 키만 별도 보관 (CLAUDE.md §6 준수)
- 실측 상위: `Balhwajeom_Project` 84 threads (user만 17), `260827_UProjectHub` 80, `펫 만들기` 19

### (D) 부모/자식 관계
- `thread_spawn_edges(parent_thread_id, child_thread_id, status)` 27건
- `threads.source`가 JSON일 때: `{"subagent":{"thread_spawn":{"parent_thread_id":…,"depth":1,"agent_nickname":"Sagan",…}}}` (240건)
- `session_meta.forked_from_id` / `forked_from_ordinal_exclusive` / `history_base{thread_id, end_ordinal_exclusive, end_byte_offset}`

### 제목(title) 출처 3가지 — 우선순위 제안
1. `state_5.threads.name` (LLM 생성 짧은 제목, 129건 존재) ← **표시용 1순위**
2. `session_index.jsonl.thread_name` (동일 값, 129건) ← state DB 손상 시 폴백
3. `state_5.threads.title` / `first_user_message` / `preview` (= 첫 user 메시지 원문 절단) ← 최종 폴백
※ **rollout JSONL의 `session_meta`에는 제목 필드가 없다.** 제목은 JSONL 밖에 있다 → 백업 포맷에 반드시 별도 저장해야 한다.

---

## 5. 대화 내용을 읽을 수 있는 방법

rollout JSONL 한 줄 = `{ timestamp, ordinal, type, payload }`

### 최상위 `type` (실측)
| type | 의미 |
|---|---|
| `session_meta` | ordinal 0, 세션 식별/메타 |
| `response_item` | 모델 API wire 포맷 (`message`/`reasoning`/`custom_tool_call`/`custom_tool_call_output`) |
| `event_msg` | 프로토콜 이벤트 (`item_completed`, `token_count`, `task_started`, `task_complete`, `turn_aborted`, `thread_settings_applied`) |
| `turn_context` | 턴별 cwd/workspace_roots/approval_policy/sandbox_policy/model |
| `world_state` | 환경 스냅샷 (environments, filesystem, collaboration_mode, agents_md) |
| `compacted` | 컨텍스트 압축 요약 |
| `token_usage_record` | 토큰 사용량 |

### `session_meta.payload` 실측 필드
```
session_id, id, timestamp(생성시각), cwd, originator("Codex Desktop"|"codex_work_desktop"),
cli_version, source("vscode"), thread_source("user"), model_provider, base_instructions(18,068자),
history_mode("paginated"|"legacy"), context_window{window_id},
git{commit_hash, branch, repository_url},
forked_from_id, forked_from_ordinal_exclusive,
history_base{thread_id, end_ordinal_exclusive, end_byte_offset},
multi_agent_version, dynamic_tools[]
```

### 메시지 읽는 두 갈래
**① UI 레벨 (권장 — 뷰어용)**: `type="event_msg"` & `payload.type="item_completed"`
```json
{"type":"item_completed","thread_id":"…","turn_id":"…",
 "item":{"type":"UserMessage","id":"…","content":[{"type":"text","text":"…"}]},
 "started_at_ms":…, "completed_at_ms":…}
```
`item.type` ∈ `UserMessage` / `AgentMessage`(+`phase`:`commentary`|`final`) / `Reasoning` / `CommandExecution`(command[], cwd, parsed_cmd) / …
→ `thread_history_1.sqlite`의 `thread_items.item_json`과 **동일한 camelCase 스키마**(`userMessage`, `agentMessage`, …). 파서 하나로 양쪽 커버 가능.

**② API wire 레벨 (재개/정합성 검증용)**: `type="response_item"`
- user: `role:"user"`, `content[].type="input_text"`
- assistant: `role:"assistant"`, `content[].type="output_text"`
- developer: `role:"developer"` ← 앱 컨텍스트/멀티에이전트 지침 등 **시스템 주입분. UI에는 숨겨야 함**
- reasoning: `encrypted_content` (암호화 문자열, 복호화 불가 — 그대로 보존만)

### V1 뷰어 최소 구현
`session_meta` 1줄 파싱 → `event_msg/item_completed` 중 `UserMessage`/`AgentMessage`만 시간순 렌더.
`role:"developer"`와 `<recommended_plugins>` / `<app-context>` / `<turn_aborted>` 류는 기본 숨김.

### 첨부
`attachments\<uuid>\pasted-text.txt` + `attachments\pasted-text-attachments.json` → 대화 본문에서 uuid로 참조. Export 완전성 옵션으로 고려.

---

## 6. SQLite 및 Index 파일의 역할 정리

| 파일 | 권위 | 재생성 가능? | 백업 포함? | 비고 |
|---|---|---|---|---|
| `sessions\**\*.jsonl` | **원본 (Source of Truth)** | ✕ | **필수** | 유일한 진짜 데이터 |
| `state_5.sqlite` | **발견/목록 인덱스 (권위 아님, 하지만 없으면 Codex가 대화를 못 찾음)** | 이론상 rollout 재스캔 가능 | 행 데이터만 manifest에 기록 | Import 시 여기 등록 필요 |
| `thread_history_1.sqlite` | 파생 캐시 | **O** (byte offset projection) | **미포함 권장** | 347행 projection_state가 진행 위치 |
| `session_index.jsonl` | 제목 보조 로그 | O | 제목 폴백용으로 값만 기록 | append-only, 중복 존재(136줄/129고유) |
| `.codex-global-state.json` | **프로젝트↔대화 매핑의 현재 실소유자** | ✕ | **매핑 값만 필수** | Electron 앱 상태. 파일 통째 덮어쓰기 절대 금지 |
| `config.toml` | 신뢰 경로 등록(`trust_level`) | ✕ | 참조만 | 사용자 설정, 수정 지양 |
| `logs_2.sqlite` | 로그 | O | ✕ | 118 MB, 불필요 |
| `goals_/memories_/queue_*.sqlite` | 부가 기능 | — | ✕ | 현재 0 rows |

**핵심 결론**: JSONL = 원본, SQLite = 인덱스/캐시.
→ Import의 정석은 **"JSONL을 올바른 경로에 놓고, state_5에 thread 행을 등록하고, global-state에 프로젝트 할당을 추가한다"**. `thread_history_1.sqlite`는 **건드리지 않는다** (Codex가 다시 projection 함).

---

## 7. Import / Restore 구현 시 예상되는 위험 요소

### 🔴 치명 (설계에 반드시 반영)
1. **한 대화 = 여러 파일 (체인)**
   `forked_from_id` + `history_base{thread_id, end_ordinal_exclusive, end_byte_offset}`로 부모 파일을 참조한다.
   `threads.rollout_path`는 **마지막 세그먼트**를 가리킨다(예: `…_01a08e7d-…jsonl`).
   → **체인 전체를 함께 Export/Import 하지 않으면 대화 앞부분이 통째로 사라진다.** Export 시 조상 파일 강제 포함 + Manifest에 체인 명시.

2. **프로젝트↔대화 매핑이 마이그레이션 중간 상태**
   `threadAssignmentsMigrated: false`. 지금은 `.codex-global-state.json`이 정답, 다음 버전은 `threads.project_id`가 정답.
   → 두 경로를 모두 쓰는 Adapter 필수. 어느 쪽이 활성인지 `app-server-projects-migration-by-host`로 판정.

3. **`.codex-global-state.json` 쓰기 = Electron 앱과의 경합**
   Codex 실행 중 이 파일을 앱이 계속 덮어쓴다(`.bak` + 다수의 0바이트 `.tmp-*` 잔여물이 증거).
   → 반드시 Codex 종료 상태에서만, **읽기 → 특정 키만 병합 → atomic replace(temp+move)**. 전체 덮어쓰기 금지.

4. **SQLite WAL 동시 접근**
   `state_5.sqlite-wal`(4.1 MB), `logs_2.sqlite-wal`, `queue_1.sqlite-wal`(1.6 MB) 활성.
   → 읽기는 `Mode=ReadOnly` + **`-wal`/`-shm` 동반 처리**. 쓰기는 Codex 완전 종료 확인 후. 백업 스냅샷은 `-wal`/`-shm`까지 같이 떠야 정합.
   (참고: 조사 시 `immutable=1`로 열어 원본 무영향 확인)

5. **`state_5` 행이 불완전하면 Codex가 대화를 못 열거나 아예 안 보임** (공식 Issue로 확인)
   - `model_provider`가 빈 문자열 → resume 시 "Model provider `` not found" (Issue #29083)
   - `thread_source`가 NULL → UI 목록에서 사라짐 (Issue #23979)
   → Import 시 `id, rollout_path, created_at(_ms), updated_at(_ms), source, model_provider, cwd, title, sandbox_policy, approval_mode, thread_source, history_mode, model, cli_version, preview, recency_at(_ms)`를 **JSONL에서 실제로 읽어** 채운다. 없는 값은 만들어내지 않는다(CLAUDE.md §12).

6. **경로 표기 3종 불일치** — `\\?\` prefix(346/352) / 소문자 config 키 / 한글 NFC. 재매핑 시 Canonical 규칙 통일 실패하면 "기타 대화"로 전부 흘러간다.

### 🟠 높음
7. **`.zst` 압축 rollout** — 공식적으로 오래된 파일은 Zstandard 압축된다. 이 PC엔 아직 없으나 다른 PC엔 있을 수 있다. Reader/Exporter가 `.jsonl.zst`를 지원해야 한다(직접 구현 = ZstdSharp 등 라이브러리 링크, 외부 exe 호출 금지).
8. **Thread ID 충돌** — UUIDv7이라 실제 충돌은 희박하지만, "같은 PC에서 Export→같은 PC로 Import" 시 100% 충돌. 기본 `건너뛰기`(CLAUDE.md §16). "복사본으로 가져오기"는 새 UUIDv7 발급 + JSONL 내부 `session_id`/`id`/`thread_id`/`turn_id` 참조 전부 재작성이 필요 → **V1에서는 Experimental로 두는 것을 권장**.
9. **`guardian_review` 145건 / `subagent` 95건 노이즈** — 전체 352건 중 사용자 대화는 112건뿐. 필터 없으면 UI가 쓸모없어진다.
10. **아카이브는 물리적 이동** — `archived=1` + `archived_at` + `rollout_path`가 `archived_sessions\`로 변경됨. 복원 시 아카이브 상태를 보존해야 하고, 대상 폴더를 잘못 고르면 Codex가 못 찾는다.
11. **대용량** — 최대 단일 세션 12.8 MB, 총 1.45 GB. 스트리밍 파서 필수(전체 로드 금지), Export 시 진행률/취소 필요.
12. **`base_instructions` 18 KB × 세션 수** — 백업 용량의 상당 부분. 중복 제거(해시 참조) 고려.

### 🟡 중간
13. `session_index.jsonl` append-only 중복 (136줄 / 129 고유) → "마지막 승리" 규칙으로 읽어야 함. 쓰기는 append만, 재작성 금지.
14. `sqlite\` 하위에 구버전 DB 세트 + 현재도 갱신되는 `codex-dev.db`(2.8 MB) 존재 → **어느 DB가 활성인지 mtime으로 판단하지 말고, 루트의 `state_5.sqlite`를 기준으로** 하되 Locator가 검증할 것.
15. 트리거 4개 + 인덱스 20여 개 → 직접 INSERT 시 트리거 동작(정상). 단 `_sqlx_migrations` 버전(현재 52)이 다른 PC와 다르면 컬럼 자체가 다를 수 있다 → **Import 전 `_sqlx_migrations` 최신 version + 실제 `PRAGMA table_info(threads)` 비교 검증 필수.**
16. Codex 실행 감지 — `thread-writer-locks\`, `mcp-oauth-locks\`, `process_manager\`, `*-shm/-wal` 존재 + `codex.exe`/Electron 프로세스 확인 병행.
17. 로그에 대화 원문 금지 (CLAUDE.md §29) — `first_user_message`/`preview`/`title` 모두 사용자 원문이다. 로그·파일명·에러 메시지에 그대로 노출하지 말 것.

---

## 8. 추천 기술 스택과 이유

### 결론: **C# / .NET 9 + WPF (MVVM), win-x64 self-contained single-file 배포**

| 요구사항 | 충족 방식 |
|---|---|
| Windows 전용 GUI, 일반 사용자 실행 | WPF. `dotnet publish -r win-x64 --self-contained /p:PublishSingleFile=true` → **런타임 설치 불필요한 exe 1개** |
| 3-pane + 트리 + 체크박스 + 대화 뷰어 | WPF `TreeView` + `HierarchicalDataTemplate` + Virtualization. CLAUDE.md §24 레이아웃과 정확히 일치 |
| Codex 데이터 탐색 / 파일 처리 | `System.IO` 스트리밍, `System.Text.Json` `Utf8JsonReader`(1.45 GB도 저메모리 처리), **`\\?\` 롱패스 native 지원** |
| SQLite Read-Only | `Microsoft.Data.Sqlite` — `Mode=ReadOnly`, `Pooling=false` 로 원본 안전 |
| ZIP 컨테이너 (`.codexbackup`) | `System.IO.Compression.ZipArchive` (BCL 내장) |
| SHA-256 | `System.Security.Cryptography.SHA256` (BCL 내장) |
| `.zst` rollout | `ZstdSharp.Port` (순수 C#, 네이티브 DLL 불필요) |
| Snapshot / Rollback | `System.IO` + atomic `File.Replace` (NTFS 트랜잭션적 교체) |
| Codex 프로세스 감지 | `System.Diagnostics.Process` |
| 유지보수성 | Interface + DI(`Microsoft.Extensions.DependencyInjection`) → CLAUDE.md §5 계층/§23 `ICodexStorageAdapter` 그대로 구현 |
| 단위 테스트 | xUnit + FluentAssertions. UI 무관 로직 분리(CLAUDE.md §30) |
| **외부 프로그램 의존 없음** | 전부 BCL + 순수 관리형 NuGet. 외부 exe 호출 0건 (CLAUDE.md §2.1) |
| **사용자 친화성** | UProjectHub를 이미 .NET self-contained로 배포한 이력 + Unity/C# 경험 |

### 탈락 후보 비교
| 후보 | 장점 | 탈락 이유 |
|---|---|---|
| **Python + PySide6 / PyInstaller** | 개발 속도 | 배포물 100 MB+ / 시작 지연 / **백신 오탐 빈발** / 자식 프로세스·롱패스 취급 불편 / 일반 사용자 배포 난이도 |
| **Rust + Tauri or egui** | Codex core와 동일 언어 → 공식 구조체 참조 이점, 성능 | GUI 생태계(트리+체크박스+복잡 레이아웃) 구현 비용 큼, Tauri는 WebView2 의존, **장기 유지보수 난이도가 사용자 배경과 불일치** |
| **Electron / TypeScript** | UI 자유도 최고 | 200 MB+ 배포, Codex와 같은 Electron 이중 설치, 파일/SQLite 처리에 네이티브 모듈 필요 |
| **WinUI 3 / MAUI** | 최신 Fluent UI | 배포/패키징 이슈, 데스크톱 트리 UI 성숙도가 WPF보다 낮음, .NET 버전 민감 |
| **Avalonia** | 크로스플랫폼 대비 | V1은 Windows 전용이라 이점 없음. WPF보다 도구/자료 적음 |

> Rust의 "공식 소스 동형" 이점은 **포맷 문서화로 대체**한다: 공식 Rust 소스의 `RolloutItem` / `ThreadMetadata` 정의를 읽어 C# DTO에 1:1로 옮기고, 그 매핑표를 `docs/`에 남긴다. 이게 CLAUDE.md §33의 "추측 금지" 원칙을 만족하는 현실적 방법이다.

### NuGet 최소 세트
`Microsoft.Data.Sqlite` · `ZstdSharp.Port` · `Microsoft.Extensions.DependencyInjection` · `Microsoft.Extensions.Logging` + `Serilog.Sinks.File` · `Tomlyn`(config.toml 읽기) · xUnit + FluentAssertions

---

## 9. 전체 프로그램의 추천 아키텍처

```
CodexBackupManager.sln
├─ src/
│  ├─ CodexBackupManager.Domain/            (의존성 0)
│  │    CodexHome, CodexThread, CodexProject, ConversationItem,
│  │    CanonicalPath, ThreadChain, BackupManifest, RestorePlan,
│  │    ConflictPolicy, ValidationResult
│  │
│  ├─ CodexBackupManager.Codex/             (Codex 데이터 접근 — Read-Only 기본)
│  │    CodexLocator                 : CODEX_HOME → %USERPROFILE%\.codex → 설정 → 수동, + 검증
│  │    CodexHomeValidator           : sessions/ + state_*.sqlite + session_index.jsonl 조합 확인
│  │    ICodexStorageAdapter         : 버전 어댑터 인터페이스
│  │      ├ CodexStorageAdapterV153  : state_5 + paginated + global-state 매핑 (현재 PC)
│  │      └ CodexStorageAdapterLegacy: history_mode=legacy / project_id 사용 버전
│  │    StateDbReader                : state_5.sqlite Read-Only (+ PRAGMA/_sqlx_migrations 검증)
│  │    GlobalStateReader            : .codex-global-state.json (thread-project-assignments 등)
│  │    SessionIndexReader           : session_index.jsonl (last-wins)
│  │    ConfigTomlReader             : trust_level 프로젝트 목록
│  │    RolloutFileLocator           : sessions/archived_sessions 스캔, .jsonl + .jsonl.zst
│  │    RolloutStreamReader          : Utf8JsonReader 스트리밍 (+ Zstd decompress)
│  │    CodexSessionParser           : session_meta / item_completed / response_item → Domain
│  │    ThreadChainResolver          : forked_from_id · history_base · _<segment> 체인 해석
│  │    CodexProjectResolver         : (A)state_5.project_id → (B)global-state → (C)cwd canonical
│  │    CodexProcessDetector         : codex.exe / Electron / lock 디렉터리 / -wal -shm
│  │
│  ├─ CodexBackupManager.Backup/            (독립 포맷 — Codex 버전 비의존)
│  │    BackupWriter / BackupReader  : .codexbackup (ZIP)
│  │    ManifestSerializer           : backupFormatVersion = 1
│  │    ChecksumService              : SHA-256
│  │    BackupValidator              : manifest·version·필수파일·checksum·parse·ID충돌
│  │
│  ├─ CodexBackupManager.Restore/
│  │    SnapshotService              : Snapshots/yyyy-MM-dd_HHmmss/ (변경 대상 + -wal/-shm)
│  │    RestorePlanner               : 충돌 판정 · 경로 재매핑 · 적용 순서 결정
│  │    RestoreExecutor              : ①JSONL 배치 → ②state_5 등록 → ③global-state 매핑 병합
│  │    RollbackService              : 단계별 역순 복구
│  │    IntegrityValidator           : 적용 후 존재·parse·metadata·(가능시 app-server 인식) 검증
│  │
│  ├─ CodexBackupManager.App/               (WPF, MVVM — 로직 없음)
│  │    Views: MainWindow, ProjectTreeView, ConversationView, ImportPreviewDialog,
│  │           PathRemapDialog, ConflictDialog, SnapshotManagerView
│  │    ViewModels / Services(DialogService, SettingsStore, ProgressReporter)
│  │
│  └─ CodexBackupManager.AppServer/         (Phase 7+ 선택, 격리)
│       ICodexAppServerClient        : thread/list · thread/read 등. 실패해도 전체 기능 유지
└─ tests/
   ├─ …Domain.Tests / …Codex.Tests / …Backup.Tests / …Restore.Tests
   └─ Fixtures/CodexHome (실데이터 아닌 모방 픽스처), Fixtures/BackupV1
```

**의존 방향**: `App → Restore → Backup → Codex → Domain` (역방향 없음)
**원칙**: `Codex` 어셈블리는 기본 Read-Only. 쓰기는 `Restore`만 수행하고, 반드시 Snapshot을 선행한다.

---

## 10. Phase 1에서 실제로 구현할 범위

**목표: "Codex 연결됨 + 경로 표시"까지. 대화 파싱/Export는 하지 않는다.** (CLAUDE.md §25 Phase 1, §35 범위 통제)

### 산출물
1. **Repository 초기화**
   - `.gitignore`(.NET), `README.md`, 루트에 `CLAUDE.md` 배치
   - `CodexBackupManager.sln` + `Domain` / `Codex` / `App` / 테스트 4개 프로젝트 골격
   - `docs/codex-storage-format.md` ← **본 보고서 3~6장을 정식 문서로 고정**
2. **`CanonicalPath`** (Domain)
   - `\\?\` / `\\?\UNC\` 제거, 구분자 통일, trailing 제거, `ToLowerInvariant`, Unicode NFC
   - 원본 문자열 보존
3. **`CodexLocator`** (Codex)
   - 탐색 순서: ① `CODEX_HOME` 환경변수 → ② `%USERPROFILE%\.codex` → ③ 저장된 수동 경로 → ④ 사용자 직접 선택
4. **`CodexHomeValidator`**
   - 필수: `sessions\` 존재
   - 강한 신호: `state_*.sqlite` (glob, `state_5` 하드코딩 금지) / `session_index.jsonl` / `config.toml`
   - 스코어링 후 `Valid / Probable / Invalid` + 사유 반환. "폴더가 존재함"만으로 유효 판정 금지 (CLAUDE.md §4)
5. **`CodexInstallationInfo`** (읽기만)
   - `state_*.sqlite` 파일명에서 state 스키마 세대, `_sqlx_migrations` 최신 version
   - `threads.cli_version` 최신값 → Codex CLI 버전
   - `config.toml` → `BROWSER_USE_CODEX_APP_VERSION`, `CODEX_CLI_PATH`
   - `app-server-projects-migration-by-host.threadAssignmentsMigrated` → **활성 프로젝트 매핑 경로 판정**
6. **`SettingsStore`**
   - `%APPDATA%\CodexBackupManager\settings.json` (Codex 폴더 안에 쓰지 않는다)
7. **최소 UI (WPF)**
   - 상단: `Codex 연결됨 ●` + 경로 + 버전 배지 / 미발견 시 `[폴더 선택]`
   - 하단: 탐지 요약 (세션 파일 수, state DB 세대, migration 상태)
   - 목록/뷰어/Export 버튼은 **비활성 placeholder**
8. **로깅** — Serilog, `%APPDATA%\CodexBackupManager\logs\`. 대화 원문·제목·경로 원문 미기록(해시/카운트만)
9. **단위 테스트** (CLAUDE.md §30 CodexLocator / Path Normalization 항목)
   - CanonicalPath: 대소문자 / `\\?\` / trailing / 상대경로 / 한글 NFC·NFD / UNC
   - CodexLocator: CODEX_HOME 우선 / 없을 때 fallback / 잘못된 경로 / 수동 지정
   - Validator: 빈 폴더 거부 / sessions만 있을 때 / 전체 구비 / state_*.sqlite 이름 변형

### Phase 1에서 하지 않을 것
JSONL 파싱, 대화 목록, 프로젝트 그룹화, 뷰어, Export, Import, Snapshot, SQLite 쓰기, app-server 호출.

### Phase 1 완료 기준
- 이 PC에서 실행 → `C:\Users\User\.codex` 자동 탐지 + CLI 0.153.4 / Desktop 26.903.61454 / state gen 5 / migration 52 표시
- 잘못된 폴더 수동 지정 시 명확한 사유와 함께 거부
- 전체 테스트 green
- **Codex 데이터에 쓰기 0건** (테스트로 보장: Codex 경로 쓰기 시도 시 실패하는 가드)

---

## 11. 다음에 Claude에게 보내면 좋은 작업 지시 (그대로 복사 사용)

```text
Phase 1을 시작해줘. CLAUDE.md와 docs/codex-storage-format.md를 최우선 지침으로 사용해.

작업 위치: C:\Users\User\Documents\GitHub\CodexBackupManager (없으면 생성)

1. Repository 초기화
   - .NET 9 솔루션 CodexBackupManager.sln
   - src: CodexBackupManager.Domain / .Codex / .App(WPF)
   - tests: CodexBackupManager.Domain.Tests / .Codex.Tests (xUnit + FluentAssertions)
   - 루트에 CLAUDE.md 배치, .gitignore(.NET), README.md
   - docs/codex-storage-format.md 에 조사 결과를 정식 문서로 작성

2. 구현 (Phase 1 범위만)
   - Domain: CanonicalPath (\\?\ 제거, 구분자 통일, trailing 제거, ToLowerInvariant, Unicode NFC, 원본 보존)
   - Codex: CodexLocator (CODEX_HOME → %USERPROFILE%\.codex → 저장된 수동 경로 → 사용자 선택)
   - Codex: CodexHomeValidator (sessions 필수 + state_*.sqlite glob / session_index.jsonl / config.toml 스코어링,
            Valid / Probable / Invalid + 사유. state_5 하드코딩 금지)
   - Codex: CodexInstallationInfo (Read-Only: _sqlx_migrations 최신 version, threads.cli_version 최신값,
            config.toml의 BROWSER_USE_CODEX_APP_VERSION / CODEX_CLI_PATH,
            app-server-projects-migration-by-host.threadAssignmentsMigrated)
   - App: 상단 "Codex 연결됨 ● / 경로 / 버전" + 미발견 시 [폴더 선택] + 탐지 요약. 나머지 UI는 비활성 placeholder
   - SettingsStore: %APPDATA%\CodexBackupManager\settings.json
   - Serilog 파일 로깅 (대화 원문·제목·경로 원문 기록 금지)

3. 반드시 지킬 것
   - Codex 데이터는 Read-Only. SQLite는 Mode=ReadOnly, Pooling=false 로만 연다
   - .codex 안에 어떤 파일도 쓰지 않는다 (설정은 %APPDATA%)
   - 확인되지 않은 필드를 추측해 채우지 않는다
   - 테스트하지 않은 기능을 동작한다고 말하지 않는다

4. 테스트
   - CanonicalPath: 대소문자 / \\?\ / \\?\UNC\ / trailing slash / 상대경로 / 한글 NFC·NFD
   - CodexLocator: CODEX_HOME 우선, fallback, 잘못된 경로, 수동 지정
   - CodexHomeValidator: 빈 폴더 거부 / sessions만 / 전체 구비 / state_*.sqlite 이름 변형
   - 실제 사용자 .codex 데이터를 테스트에 넣지 말고 tests/Fixtures/CodexHome 모방 픽스처를 만들어 사용

5. 완료 후 보고
   변경 내용 / 변경 파일 / 실행한 테스트 / 테스트 결과 / 남은 위험 요소 / 다음 추천 단계

Commit과 Push는 내가 따로 요청할 때까지 하지 마.
```

### 이어지는 단계용 지시 (참고)
- **Phase 2**: `RolloutStreamReader`(Utf8JsonReader 스트리밍 + `.jsonl.zst`) → `CodexSessionParser`(`session_meta` + `event_msg/item_completed`) → `ThreadChainResolver`(forked_from_id / history_base / `_<segment>`) → `CodexProjectResolver` 3단 폴백 → `thread_source='user'` 필터 + `guardian_review`/`subagent` 접기. **1.45 GB / 12.8 MB 단일 파일에서 메모리 사용량을 측정해 보고할 것.**
- **Phase 3**: 뷰어. `role:"developer"`·`<app-context>`·`<recommended_plugins>` 숨김. `reasoning.encrypted_content`는 렌더하지 않고 보존만.
- **Phase 5 전에 반드시**: 공식 Codex Rust 소스의 `RolloutItem` / `SessionMeta` / `ThreadMetadata` 정의를 읽어 C# DTO 매핑표를 `docs/`에 고정. (추측 금지 원칙)

---

## 부록: 이번 조사에서 확정하지 못한 항목 (다음 단계에서 반드시 확인)

1. ~~**`CODEX_HOME` 환경변수의 실제 런타임 값**~~ — **Phase 1 검증(2026-09-11)에서 확정.**
   실제 셸로 `Process`/`User`/`Machine` 세 스코프를 모두 확인한 결과 **전부 미설정**이었다.
   이 부록에 적었던 "config.toml 주입값으로 추정 확정"은 **틀린 추정이었음이 실측으로 드러났다.**
   `CodexLocator`는 우선순위 2순위인 `%USERPROFILE%\.codex`로 정상 폴백해 Codex Home을 찾았다
   (`source=UserProfileDotCodex`, 검증 점수 5/5 Valid). 상세는 `docs/codex-storage-format.md` §9 참고.
2. **`.zst` 압축 rollout 실물 샘플** — 이 PC에는 0건. 실제 바이트 레이아웃 검증 필요.
3. **Import 후 Codex가 새 thread를 인식하는지** — 실제 적용 실험은 하지 않았다(원본 보호 우선). 반드시 **격리된 임시 CODEX_HOME**에서 먼저 재현 실험할 것.
4. **app-server(`thread/list`, `thread/read`) 실사용 가능성** — 미확인. Phase 7에서 격리 모듈로만 조사.
5. **`sqlite\codex-dev.db`(2.8 MB, 현재도 갱신 중)의 역할** — 스키마 미확인. 루트 `state_5.sqlite`와의 관계 확인 필요.
6. **`worktrees` / `visualizations` / `generated_images`** — 대화가 참조하는 산출물. Export 완전성 범위에 포함할지 결정 필요(CLAUDE.md §27과의 경계).

---

## 부록 2: Phase 1 검증 중 새로 실측된 사실 (2026-09-11, Phase 1 완료 검증)

Phase 1 구현을 실제 Windows PC의 실제 `.codex`에 대해 실행/검증하는 과정에서 이 보고서 작성 시점에는
확인하지 못했던 사실이 추가로 드러났다. Phase 0 조사 시점에는 이 PC에 대한 셸 접근이 없어 직접 실측하지
못했던 항목들이다.

1. **`CODEX_HOME` 환경변수는 실제로 설정되어 있지 않았다.** (부록 1번 참고) Phase 0의 추정은 틀렸다.
2. **Read-Only SQLite 접속 과정에서 `state_5.sqlite-shm`이 최초 1회 재구성되는 것을 관찰했다.**
   Codex를 종료한 직후 `Mode=ReadOnly` 연결로 처음 접속하면 SQLite가 WAL 공유 메모리 인덱스(`-shm`)를
   재구성하는데, 이는 읽기 전용 연결에서도 발생하는 SQLite 자체의 정상 동작이다(같은 프로세스로 재접속하면
   더 이상 바뀌지 않았다). `state_5.sqlite` 본체와 `-wal`(실제 데이터/보류 커밋)은 모든 시도에서 바이트
   단위로 동일했다.
3. **이 발견에 따라 이 프로젝트의 "Codex 원본 데이터 변경 없음" 판정 기준을 명문화했다**:
   `state_*.sqlite` 본체 · `-wal` · rollout JSONL · `session_index.jsonl` · `.codex-global-state.json` ·
   `config.toml` 같은 영속 데이터의 해시가 변하지 않는 것을 기준으로 하고, `-shm`처럼 SQLite 프로토콜상
   재생성 가능한 임시 sidecar 파일은 이 기준에서 제외한다(변화가 관찰되면 별도로 기록한다).
   상세 근거는 `docs/codex-storage-format.md` §4, §7(위험 요소 표 4번)에 있다.

---

## 조사 무결성 선언

- Codex 원본 파일 **수정 0건 / 삭제 0건 / 생성 0건**
- SQLite는 전부 `file:…?mode=ro&immutable=1` 로 열어 WAL 복구·체크포인트조차 발생시키지 않음
- 조사에 사용한 복사본은 이 세션의 격리된 컨테이너에만 존재
- Git commit / push **하지 않음** (Repository 자체가 없음)
