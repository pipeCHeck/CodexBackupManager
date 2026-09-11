# Codex 로컬 저장 구조 — 구현 기준 문서

이 문서는 **구현에 필요한 사실만** 정리한 작업 기준이다.
조사 과정과 전체 맥락은 [`phase0-codex-investigation-2026-09-11.md`](./phase0-codex-investigation-2026-09-11.md)에 있다.

| 항목 | 값 |
|---|---|
| 조사 기준일 | 2026-09-11 |
| 조사 대상 | Windows x64, Codex Desktop `26.903.61454`, Codex CLI `0.153.4` |
| 조사 방식 | Read-Only (원본 수정 0건). SQLite는 `mode=ro&immutable=1`로만 열었다. |
| 교차 검증 | 공식 Codex Issue #23979 / #29083 / #27363, DeepWiki "Rollout Persistence and Replay" |

> **이 문서의 값을 코드에 하드코딩하지 않는다.** 버전·generation·개수는 전부 런타임에 읽는다.
> 이 문서는 "무엇을 어디서 읽어야 하는가"를 고정할 뿐이다. (CLAUDE.md §33)

---

## 1. Codex Home

탐색 우선순위 (구현: `CodexLocator`)

1. 런타임 `CODEX_HOME` 환경변수
2. `%USERPROFILE%\.codex`
3. 프로그램 Settings에 저장된 수동 경로
4. 사용자가 직접 선택한 폴더

조사 대상 PC에서 확인된 부수 정보 (`config.toml`에서 읽음):

```
CODEX_HOME     = C:\Users\User\.codex
CODEX_CLI_PATH = C:\Users\User\AppData\Local\OpenAI\Codex\bin\<hash>\codex.exe
런타임          = C:\Users\User\AppData\Local\OpenAI\Codex\runtimes\...
플러그인 캐시    = C:\Users\User\.cache\codex-runtimes\...
```

### Codex Home 검증 규칙 (구현: `CodexHomeValidator`)

게이트 2개 + 가중 점수. 폴더가 존재한다는 이유만으로 Valid로 판정하지 않는다.

| 단계 | 규칙 |
|---|---|
| 게이트 1 | 폴더가 존재하지 않으면 `Invalid` |
| 게이트 2 | `sessions\` 하위 폴더가 없으면 `Invalid` |
| 점수 | `2 × state_*.sqlite` + `1 × session_index.jsonl` + `1 × config.toml` + `1 × .codex-global-state.json` (최대 5) |
| 판정 | `≥3` → Valid / `1~2` → Probable / `0` → Invalid |

- `state_*.sqlite`에 가중치 2를 주는 이유: 이 파일이 Codex의 대화 목록을 실제로 만들어내는 인덱스다.
- 점수 0(= `sessions\`만 있는 폴더)을 Invalid로 두는 이유: "sessions"라는 폴더명은 Codex와 무관한 프로젝트에도 흔하다.
- `archived_sessions\`는 정보용 신호(가중치 0)다. 실제로 아카이브가 없는 설치도 정상이다.

---

## 2. 디렉터리 / 파일 레이아웃

```
<CODEX_HOME>\
├─ sessions\YYYY\MM\DD\rollout-<ISO8601>-<threadId>.jsonl              ← 대화 원본
├─ sessions\YYYY\MM\DD\rollout-<ISO8601>-<threadId>_<segmentId>.jsonl  ← 페이지네이션 세그먼트
├─ archived_sessions\rollout-<ISO8601>-<threadId>.jsonl                ← 아카이브 시 물리적 이동
├─ state_<N>.sqlite  (+ -wal, -shm)                                    ← 대화/프로젝트 카탈로그
├─ thread_history_<N>.sqlite                                           ← UI 아이템 projection 캐시
├─ session_index.jsonl                                                 ← 제목 보조 로그 (append-only)
├─ .codex-global-state.json  (+ .bak, .tmp-*)                          ← Electron 앱 상태
├─ config.toml                                                         ← 모델/플러그인/MCP/trust_level
├─ attachments\<uuid>\pasted-text.txt  +  pasted-text-attachments.json ← 붙여넣기 첨부
├─ logs_<N>.sqlite / goals_<N>.sqlite / memories_<N>.sqlite / queue_<N>.sqlite
├─ sqlite\                                                             ← 구버전 DB 세트 + codex-dev.db
└─ (그 외) visualizations\ worktrees\ generated_images\ plugins\ skills\
           rules\ browser\ cache\ computer-use\ dictation-history\
           thread-writer-locks\ process_manager\ mcp-oauth-locks\ ...
```

### 파일명 규칙

```
rollout-2026-09-11T11-55-17-01a08e64-0ead-7a80-a5a5-a762540c0755.jsonl
        └── ISO8601, ':' → '-' ──┘ └────────── threadId (UUIDv7) ──────────┘

rollout-2026-09-11T12-22-35-01a08e64-...-a762540c0755_01a08e7d-...-55505359447b.jsonl
                            └── 부모 threadId ──┘ └──── 세그먼트 id ────┘
```

- `threadId`는 **UUIDv7** → 문자열 정렬 = 생성 시간 정렬.
- `_` 가 포함된 파일명은 **같은 thread의 이어지는 세그먼트**다. 실측 365개 중 14개.
- 오래된 rollout은 공식적으로 **Zstandard(`.jsonl.zst`)로 압축**되고 resume 시 되돌려진다.
  조사 시점 이 PC에는 0개였으나 **Reader는 반드시 `.jsonl.zst`를 지원해야 한다.**
  (Phase 1은 개수만 센다)

### 규모 (참고용, 하드코딩 금지)

| 항목 | 실측 |
|---|---|
| `sessions\**\*.jsonl` | 365개 / 약 1.45 GB |
| 단일 최대 세션 파일 | 12.8 MB |
| `archived_sessions` | 1개 |
| `state_5.sqlite` | 33.1 MB (+ `-wal` 4.1 MB) |
| `thread_history_1.sqlite` | 325.3 MB |
| `.codex-global-state.json` | 732.7 KB |

---

## 3. rollout JSONL 포맷

한 줄 = 하나의 JSON 객체.

```json
{ "timestamp": "2026-09-11T02:55:18.534Z", "ordinal": 0, "type": "...", "payload": { ... } }
```

### 최상위 `type`

| type | 내용 |
|---|---|
| `session_meta` | `ordinal: 0`. 세션 식별/메타. 파일당 1개 |
| `response_item` | 모델 API wire 포맷 |
| `event_msg` | 프로토콜 이벤트 |
| `turn_context` | 턴별 cwd / workspace_roots / approval_policy / sandbox_policy / model |
| `world_state` | 환경 스냅샷 (environments, filesystem, collaboration_mode, agents_md) |
| `compacted` | 컨텍스트 압축 요약 |
| `token_usage_record` | 토큰 사용량 |

### `session_meta.payload` 필드 (실측)

```
session_id, id                          동일 값 (threadId)
timestamp                               생성 시각
cwd                                     prefix 없는 형태:  C:\_UserProjects\Unreal\Balhwajeom_Project
originator                              "Codex Desktop" | "codex_work_desktop"
cli_version                             예: "0.153.4"
source                                  예: "vscode"
thread_source                           "user" | "subagent" | ...
model_provider                          "openai"
base_instructions                       약 18 KB (세션마다 반복 → 백업 시 중복 제거 고려)
history_mode                            "paginated" | "legacy"
context_window { window_id }
git { commit_hash, branch, repository_url }

# 분기/이어붙이기 관련 (Export/Import에 결정적)
forked_from_id                          부모 threadId
forked_from_ordinal_exclusive           부모에서 분기한 ordinal
history_base { thread_id, end_ordinal_exclusive, end_byte_offset }
multi_agent_version
dynamic_tools[]
```

**`session_meta`에는 대화 제목 필드가 없다.** 제목은 §5에서 따로 읽어야 한다.

### 메시지를 읽는 두 갈래

**① UI 레벨 — 뷰어에 쓸 것 (`event_msg` + `payload.type == "item_completed"`)**

```json
{
  "type": "item_completed",
  "thread_id": "...", "turn_id": "...",
  "item": { "type": "UserMessage", "id": "...", "content": [ { "type": "text", "text": "..." } ] },
  "started_at_ms": 0, "completed_at_ms": 0
}
```

`item.type`: `UserMessage` / `AgentMessage`(+`phase`: `commentary`\|`final`) / `Reasoning` /
`CommandExecution`(`command[]`, `cwd`, `parsed_cmd`) / `FileChange` / `McpToolCall` / `WebSearch` / …

> 이 `item` 스키마는 `thread_history_<N>.sqlite`의 `thread_items.item_json`과 **동일한 camelCase 형태**다
> (`userMessage`, `agentMessage`, …). **파서 하나로 양쪽을 처리할 수 있다.**

**② API wire 레벨 — 정합성 검증/재개용 (`response_item`)**

| `payload.role` | 내용 |
|---|---|
| `user` | `content[].type == "input_text"` |
| `assistant` | `content[].type == "output_text"`, `phase` 포함 |
| `developer` | **시스템 주입분 — UI에서 숨겨야 한다** (`<app-context>`, `<recommended_plugins>`, `<multi_agent_mode>`, `<turn_aborted>` 등) |

`payload.type == "reasoning"`은 `encrypted_content`(암호화 문자열)만 담는다. **복호화 불가 — 보존만 한다.**

---

## 4. `state_<N>.sqlite` — 대화/프로젝트 카탈로그

권위: **원본이 아니다.** rollout JSONL이 원본이고 이것은 발견/목록 인덱스다.
다만 **이 인덱스에 등록되지 않으면 Codex가 대화를 목록에 표시하지 못한다.**

### `threads` (실측 352행, 38컬럼)

```
id                      TEXT PK      threadId (UUIDv7)
rollout_path            TEXT         절대 경로. 세그먼트가 있으면 마지막 세그먼트를 가리킨다
created_at, updated_at  INTEGER      초 단위
created_at_ms, updated_at_ms         밀리초 (트리거가 초 단위에서 자동 채움)
recency_at, recency_at_ms
source                  TEXT         "vscode" 또는 subagent JSON (아래 참고)
model_provider          TEXT         비어 있으면 resume 실패 (Issue #29083)
cwd                     TEXT         346/352가 \\?\ prefix 형태
title                   TEXT         첫 user 메시지 절단
first_user_message      TEXT         첫 user 메시지 절단
preview                 TEXT         첫 user 메시지 절단
name                    TEXT         LLM 생성 짧은 제목 (실측 129건 존재) ← 표시용 1순위
sandbox_policy          TEXT         JSON
approval_mode           TEXT
tokens_used             INTEGER
has_user_event          INTEGER
archived                INTEGER      1이면 rollout_path가 archived_sessions\ 를 가리킨다
archived_at             INTEGER
git_sha, git_branch, git_origin_url
cli_version             TEXT         ← Codex CLI 버전의 출처
agent_nickname, agent_role, agent_path
memory_mode, model, reasoning_effort
thread_source           TEXT         NULL이면 UI 목록에서 사라진다 (Issue #23979)
history_mode            TEXT         "paginated" | "legacy"
is_pinned, thread_section_id, section_position, section_entered_at_ms
project_id              TEXT         ★ 실측 352행 전부 NULL — §5 참고
```

트리거 4개가 `created_at_ms` / `updated_at_ms` / `recency_at(_ms)`를 자동으로 채운다.
직접 INSERT할 때 이 트리거가 동작한다. (Phase 7 이전에는 INSERT하지 않는다)

### 그 외 테이블

| 테이블 | 실측 | 내용 |
|---|---|---|
| `projects` | 45행 | `id, name, metadata, position, created_at_ms, updated_at_ms` |
| `project_roots` | 52행 | `project_id, position, path` |
| `thread_spawn_edges` | 27행 | `parent_thread_id, child_thread_id, status` |
| `thread_sections` | 1행 | `Pinned` |
| `rollout_migration_state` | 1행 | `migration_id = 'legacy_to_paginated_v1'` |
| `_sqlx_migrations` | 52행 | 최신 version이 스키마 세대. 최신: 52 `projects recency` |
| `thread_artifacts` / `thread_dynamic_tools` | 0행 | — |

### 실측 분포 — UI 설계에 직접 영향

| 구분 | 값 |
|---|---|
| `thread_source = 'user'` | **112** ← 사람이 실제로 한 대화 |
| `thread_source = 'subagent'` | 95 |
| `thread_source = 'guardian_review'` | **145** ← Codex 내부 리뷰 스레드 |
| `history_mode` paginated / legacy | 333 / 19 |
| `archived = 1` | 1 |
| `cwd`에 `\\?\` prefix | 346 / 352 |

→ **필터 없이 352건을 전부 보여주면 UI가 쓸모없어진다.** 기본은 `thread_source = 'user'`.

`threads.source`가 JSON인 경우(240건):

```json
{"subagent":{"thread_spawn":{"parent_thread_id":"...","depth":1,"agent_nickname":"Sagan","agent_role":null}}}
```

### 읽기 규칙

```
Mode=ReadOnly            쓰기 불가. 읽기 전용 연결은 WAL checkpoint를 수행하지 않는다
Pooling=False            파일 핸들을 즉시 놓는다
Cache=Private            페이지 캐시를 공유하지 않는다
```

`-shm`을 만들 수 없어 열기가 실패하는 경우(`SQLITE_READONLY_CANTINIT` 등)에만
`file:///...?mode=ro&immutable=1` URI로 재시도한다.
이 폴백은 원본을 건드리지 않지만 **WAL에만 있는 최신 변경을 보지 못할 수 있으므로**
사용한 모드를 호출자에게 반드시 알린다.

**컬럼 구성은 버전마다 달라진다.** 쿼리를 조립하기 전에 항상 `PRAGMA table_info(threads)`로 실제 컬럼을 확인한다.

---

## 5. 프로젝트 ↔ 대화 연결 — 3중 구조

조사 시점 이 PC는 **마이그레이션 중간 상태**였다. 세 경로를 모두 읽을 수 있어야 한다.

### (A) 정식 경로 — `state_<N>.sqlite`

`projects`(45) + `project_roots`(52)는 채워져 있으나 `threads.project_id`는 **전부 NULL**.

### (B) 현재 유효한 경로 — `.codex-global-state.json`

```json
"app-server-projects-migration-by-host": {
  "local:C:\\Users\\User\\.codex": {
    "version": 1,
    "projectsMigrated": true,
    "threadAssignmentsMigrated": false     ← 이 플래그가 어느 경로가 유효한지 알려준다
  }
}
```

| 키 | 실측 | 내용 |
|---|---|---|
| `local-projects` | 45 | `{ id, name, rootPaths[], createdAt, updatedAt }`. id는 `local-<md5>` 또는 UUIDv4 |
| **`thread-project-assignments`** | **60** | `threadId → { projectKind:"local", projectId }` ← **직접 링크** |
| `app-server-project-id-by-legacy-project-id-by-host` | — | legacy UUID → `state.projects.id`(UUIDv7) 변환 맵 |
| `projectless-thread-ids` | 11 | 프로젝트 미지정 = "기타 대화" |
| `thread-workspace-root-hints` | 11 | `threadId → 작업 루트` |
| `thread-projectless-output-directories` | 11 | `threadId → ...\outputs` |
| `thread-writable-roots` | 66 | `threadId → 쓰기 허용 루트[]` |
| `pinned-project-ids` / `pinned-thread-ids` / `project-order` / `selected-project` | — | UI 상태 |

**쓰기 금지.** Codex 실행 중 앱이 이 파일을 계속 덮어쓴다 (`.bak` + 0바이트 `.tmp-*` 잔여물 11개가 증거).
Phase 7에서 쓰게 되더라도 반드시 `읽기 → 특정 키만 병합 → atomic replace`이며, 전체 덮어쓰기는 절대 금지다.

### (C) 폴백 — `cwd` 정규화

같은 폴더가 3가지 표기로 존재한다.

```
C:\_UserProjects\Unreal\Balhwajeom_Project        session_meta.cwd
\\?\C:\_UserProjects\Unreal\Balhwajeom_Project    state.threads.cwd
c:\_userprojects\unreal\balhwajeom_project        config.toml [projects.'...'] 키 (전부 소문자)
```

정규화 순서 (구현: `Domain.Paths.CanonicalPath`)

1. Unicode **NFC** ← 한글 폴더명이 다수이므로 필수 (`기획`, `펫 만들기`, `미디어 분석`)
2. `/` → `\`
3. `\\?\UNC\` → `\\`, `\\?\` → 제거
4. 루트 판별 (드라이브 / UNC / 디바이스 / 드라이브 상대 / 상대)
5. 세그먼트 분해: 빈 세그먼트·`.` 제거, `..` 어휘적 해소, trailing separator 제거
6. `Display` 조립 (대소문자 보존)
7. `Value = Display.ToLowerInvariant()` ← 비교 전용 키

**원본 문자열(`Original`)은 절대 변형하지 않고 보존한다.**

### 제목의 출처 — 우선순위

1. `state.threads.name` (LLM 생성 짧은 제목, 129건)
2. `session_index.jsonl`의 `thread_name` (같은 값)
3. `state.threads.title` / `first_user_message` / `preview` (첫 user 메시지 원문 절단)

**rollout JSONL 안에는 제목이 없다.** 백업 포맷에 별도 필드로 저장해야 한다.

---

## 6. `thread_history_<N>.sqlite` — 파생 캐시

```
thread_items (33,999행)   thread_id, turn_id, item_id, rollout_ordinal,
                          created_at_ms, item_json, item_type, updated_at_ordinal
thread_turns (1,585행)    thread_id, turn_id, rollout_ordinal, status, error_json,
                          started_at, completed_at, duration_ms,
                          first_user_item_id, final_agent_item_id,
                          rollout_byte_offset, rollout_end_ordinal, rollout_end_byte_offset
thread_history_projection_state (347행)
                          thread_id, next_rollout_byte_offset, next_rollout_ordinal
thread_realtime_items (0행)
```

`item_type` 분포: `reasoning` 12,376 / `commandExecution` 7,420 / `agentMessage` 7,237 /
`fileChange` 2,776 / `mcpToolCall` 1,923 / **`userMessage` 1,611** / `webSearch` 242 /
`contextCompaction` 176 / `imageView` 163 / `collabAgentToolCall` 40 / `imageGeneration` 22 /
`subAgentActivity` 10 / `dynamicToolCall` 3

**이것이 파생 캐시라는 증거:** thread `01a08e64-…0755`의
`next_rollout_byte_offset = 120580`이 해당 rollout 파일 크기 **120,580 바이트와 정확히 일치**했다.
또한 352 thread 중 **134개만** 아이템을 가지고 있다(= 열어본 thread만 lazy projection).

→ **백업에 포함하지 않는다. Import 시에도 쓰지 않는다.** Codex가 rollout에서 다시 만든다.

---

## 7. 백업/복원 설계에 직결되는 위험 요소

| # | 위험 | 대응 |
|---|---|---|
| 1 | **한 대화 = 여러 파일 체인**. `threads.rollout_path`는 마지막 세그먼트를 가리키고 `history_base`로 부모를 참조 | Export 시 조상 파일 강제 포함 + Manifest에 체인 명시. 안 하면 대화 앞부분이 사라진다 |
| 2 | 프로젝트↔대화 매핑이 마이그레이션 중간 상태 | `threadAssignmentsMigrated`로 판정하는 Adapter |
| 3 | `.codex-global-state.json` 쓰기 경합 | Codex 종료 후, 특정 키만 병합, atomic replace |
| 4 | SQLite WAL 동시 접근 | 읽기는 `Mode=ReadOnly`, 스냅샷은 `-wal`/`-shm`까지 함께 |
| 5 | `state` 행이 불완전하면 Codex가 대화를 못 열거나 안 보임 | `model_provider` 공백 → resume 실패(#29083), `thread_source` NULL → 목록에서 사라짐(#23979) |
| 6 | 경로 표기 3종 불일치 | `CanonicalPath` |
| 7 | `.zst` 압축 rollout | Reader가 `.jsonl.zst` 지원 (순수 관리형 구현) |
| 8 | Thread ID 충돌 (같은 PC 재Import 시 100%) | 기본 `건너뛰기`. "복사본"은 JSONL 내부 ID 전면 재작성이 필요 → Experimental |
| 9 | `guardian_review` 145 / `subagent` 95 노이즈 | 기본 `thread_source = 'user'` 필터 |
| 10 | 아카이브는 물리적 이동 | `archived`, `archived_at`, `rollout_path` 함께 복원 |
| 11 | 1.45 GB / 단일 12.8 MB | 스트리밍 파서 필수 (`Utf8JsonReader`) |
| 12 | `_sqlx_migrations` 버전이 PC마다 다르면 컬럼 자체가 다름 | Import 전 `PRAGMA table_info(threads)` 비교 검증 |
| 13 | `session_index.jsonl` append-only 중복 (136줄/129고유) | "마지막 승리"로 읽기. 재작성 금지 |
| 14 | `first_user_message` / `preview` / `title` / `name`은 모두 사용자 원문 | 로그·파일명·에러 메시지에 노출 금지 (`Domain.Diagnostics.Redact`) |

---

## 8. Phase 1 구현이 읽는 것 / 읽지 않는 것

### 읽는다

| 값 | 출처 |
|---|---|
Codex Home | `CODEX_HOME` → `%USERPROFILE%\.codex` → 저장된 수동 경로 → 사용자 선택
`state_*.sqlite` 목록 / generation | 파일명 패턴 `^state_(\d+)\.sqlite$`
migration 최신 version + description | `_sqlx_migrations` `MAX(version)`
Codex CLI 버전 | `threads.cli_version` (`updated_at_ms` 최대 행)
`threads` 행 수 / archived 행 수 | `COUNT(*)`
Codex Desktop 버전 | `config.toml` `BROWSER_USE_CODEX_APP_VERSION`
CLI 실행 파일 경로 | `config.toml` `CODEX_CLI_PATH`
`projectsMigrated` / `threadAssignmentsMigrated` | `.codex-global-state.json`
세션 / 아카이브 파일 개수 | `sessions\**\*.jsonl`, `*.jsonl.zst` 열거
`session_index.jsonl` 줄 수 | 줄 수만 (내용 미열람)
Codex 활동 신호 | `-wal` / `-shm` / `thread-writer-locks\`

### 읽지 않는다 (Phase 2 이후)

rollout JSONL 파싱 · 대화 제목/미리보기/내용 · 프로젝트 목록 · `thread_history_*.sqlite` ·
`.zst` 압축 해제 · 체인 해석 · `attachments\` · app-server 호출

### 쓰지 않는다 (Phase 7 이전)

Codex Home 아래 어떤 파일도 생성/수정/삭제하지 않는다.
프로그램 설정과 로그는 `%APPDATA%\CodexBackupManager\`에만 둔다.

---

## 9. 아직 확정하지 못한 항목

1. **`CODEX_HOME` 환경변수의 런타임 실측값** — `config.toml`이 자식 프로세스에 주입하는 값으로 추정 확정.
   `scripts/verify-phase1.ps1`이 실제 값을 출력한다.
2. **`.zst` 압축 rollout 실물** — 조사 PC에 0개. 실제 바이트 레이아웃 미검증.
3. **Import 후 Codex가 새 thread를 인식하는지** — 원본 보호 우선으로 적용 실험을 하지 않았다.
   반드시 **격리된 임시 `CODEX_HOME`** 에서 먼저 재현할 것.
4. **app-server (`thread/list`, `thread/read`) 실사용 가능성** — 미확인. Phase 7에서 격리 모듈로만 조사.
5. **`sqlite\codex-dev.db`(2.8 MB, 조사 시점에도 갱신 중)의 역할** — 스키마 미확인.
6. **`worktrees\` / `visualizations\` / `generated_images\`** — 대화가 참조하는 산출물.
   Export 완전성 범위에 포함할지 미결정 (CLAUDE.md §27과의 경계).
