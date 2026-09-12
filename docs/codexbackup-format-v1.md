# `.codexbackup` Version 1 — Export 포맷 스펙 및 Restore Sufficiency Audit

이 문서는 Phase 5(Export) 착수 전에 수행한 **Restore Sufficiency Audit**(어떤 metadata가 있어야
다른 PC에서 실제로 thread를 복원할 수 있는지 조사)과, 그 결과로 확정한 **Backup Format V1** 스펙을
함께 기록한다. `docs/codex-storage-format.md`가 "Codex가 무엇을 어떻게 저장하는가"를 다룬다면, 이
문서는 "우리가 그중 무엇을, 어떤 형태로 백업하는가"를 다룬다.

> **상태: FROZEN (Phase 05_01 완료 기준).** Phase 5에서 처음 확정한 스펙을 GitHub 코드 리뷰로 발견된
> 정합성 문제(§5)에 맞춰 Phase 05_01에서 수정했다. Phase 6(Import Preview)은 이 문서를 그대로
> 신뢰하고 구현을 시작해도 된다 — 이후 변경이 필요하면 이 문서를 먼저 갱신하고 버전을 다시 정의한다.

---

## 1. Restore Sufficiency Audit

### 1.1 방법

1. 실제 `.codex`(이 PC, `state_5.sqlite`, migration 최신 version 52)에 **읽기 전용**으로
   `PRAGMA table_info(threads)` + 컬럼별 non-null/distinct count를 실측했다(작업 전후
   `state_*.sqlite`/`-wal`/`session_index.jsonl`/`.codex-global-state.json`/`config.toml` 해시
   불변 확인, `-shm`만 SQLite 프로토콜상 재구성됨).
2. 공식 OpenAI Codex Rust 소스(`scratchpad/codex-src`, `codex-rs/`)에서 `threads` 테이블 마이그레이션
   파일, `ThreadMetadata`/`ThreadRow` 구조체, 실제 `INSERT INTO threads` 문을 찾아 대조했다.

### 1.2 실측: `state_5.sqlite.threads` — 38컬럼, 357행

| 컬럼 | 타입 | notnull | non-null/전체 | distinct | 비고 |
|---|---|---|---|---|---|
| id | TEXT | 0(PK) | 357/357 | 357 | |
| rollout_path | TEXT | 1 | 357/357 | 357 | |
| created_at | INTEGER | 1 | 357/357 | 349 | 초 단위 |
| updated_at | INTEGER | 1 | 357/357 | 355 | 초 단위 |
| source | TEXT | 1 | 357/357 | 33 | `"vscode"` 또는 subagent JSON |
| model_provider | TEXT | 1 | 357/357 | 1 | 비면 resume 실패(#29083) |
| cwd | TEXT | 1 | 357/357 | 56 | |
| title | TEXT | 1 | 357/357 | 311 | 원문 |
| sandbox_policy | TEXT | 1 | 357/357 | 126 | **JSON 문자열**(아래 1.3) |
| approval_mode | TEXT | 1 | 357/357 | 3 | 공식 enum 직렬화 문자열 |
| tokens_used | INTEGER | 1 | 357/357 | 322 | |
| has_user_event | INTEGER | 1 | 357/357 | 1 | ※ 이 PC 스키마엔 있으나 공개 소스 트리에서는 확인 못함(1.4) |
| archived | INTEGER | 1 | 357/357 | 2 | |
| archived_at | INTEGER | 0 | 1/357 | 1 | |
| git_sha / git_branch / git_origin_url | TEXT | 0 | 199·221·197/357 | 77·4·8 | |
| cli_version | TEXT | 1 | 357/357 | 28 | |
| first_user_message | TEXT | 1 | 357/357 | 304 | 원문 |
| agent_nickname / agent_role / agent_path | TEXT | 0 | 31·17·3/357 | 28·2·3 | subagent 전용, 대부분 비어있음 |
| memory_mode | TEXT | 1 | 357/357 | 1 | |
| model / reasoning_effort | TEXT | 0 | 356/357 | 3·3 | |
| created_at_ms / updated_at_ms | INTEGER | 0 | 357/357 | 357 | 트리거로 자동 채워짐 |
| thread_source | TEXT | 0 | 357/357 | 3 | NULL이면 UI 목록에서 사라짐(#23979) |
| preview | TEXT | 1 | 357/357 | 304 | 원문 |
| recency_at / recency_at_ms | INTEGER | 1 | 357/357 | 355·357 | |
| history_mode | TEXT | 1 | 357/357 | 2 | |
| name | TEXT | 0 | 130/357 | 121 | 원문 |
| is_pinned | INTEGER | 1 | 357/357 | 1 | |
| thread_section_id / section_position / section_entered_at_ms | — | 0 | 1/357 | 1 | "Pinned" 섹션 1건만 |
| project_id | TEXT | 0 | 0/357 | 0 | 마이그레이션 미완료(§ codex-storage-format.md 5) |

### 1.3 공식 소스 대조 결과

- **정확한 위치**: `ThreadMetadata`(`state/src/model/thread_metadata.rs`), 원시 행 `ThreadRow`
  (같은 파일)를 `TryFrom`으로 변환. 컬럼은 개별 migration 파일로 누적 추가된다(예:
  `0043_threads_is_pinned.sql`, `0045_threads_section.sql`, `0018_phase2_selection_snapshot.sql`의
  `memory_mode`, `0020_threads_model_reasoning_effort.sql` 등).
- **`sandbox_policy`/`approval_mode`는 opaque JSON 문자열이다.** 실제 타입은
  `protocol/src/protocol.rs`의 `SandboxPolicy`/`AskForApproval` enum이고, `state/src/extract.rs`의
  `enum_to_string()`이 `serde_json::to_value`로 직렬화해 TEXT 컬럼에 넣는다. `SandboxPolicy`는
  내부 태그(`serde(tag="type")`)라 `DangerFullAccess`만 단순 문자열(`"danger-full-access"`)이고
  나머지(`ReadOnly`/`ExternalSandbox`/`WorkspaceWrite`)는 `{"type":"workspace-write", ...}` 형태
  객체다. **→ 우리는 이 값을 파싱/해석하지 않고 opaque 문자열로 그대로 보존한다** — 스키마를
  안다고 우리가 재구성할 필요는 없고, Restore 시 그대로 다시 넣으면 된다(CLAUDE.md §33 "추측 금지").
- **실제 `INSERT INTO threads` 문**(`state/src/runtime/threads.rs`, `insert_thread_if_absent`)의
  대상 컬럼: `id, rollout_path, created_at, updated_at, recency_at, created_at_ms, updated_at_ms,
  recency_at_ms, source, originator, history_mode, thread_source, agent_nickname, agent_role,
  agent_path, model_provider, model, reasoning_effort, cwd, cli_version, title, name, preview,
  sandbox_policy, approval_mode, tokens_used, first_user_message, archived, archived_at,
  thread_section_id, section_position, section_entered_at_ms, git_sha, git_branch, git_origin_url,
  memory_mode, project_id` (+ `daybreak_enabled`, 이 PC 스키마엔 없는 컬럼 — 버전 차이).
  **NOT NULL이고 기본값이 없는 진짜 필수 컬럼은 `id, rollout_path, created_at, updated_at, source,
  model_provider, cwd, title, sandbox_policy, approval_mode`뿐**이고 나머지는 스키마 기본값이 있거나
  nullable이다.

### 1.4 확인된 불일치 — 이 프로젝트의 원칙을 다시 증명한 사례

`has_user_event`는 **실제 이 PC의 `state_5.sqlite`에는 존재하고 100% 채워져 있지만**, 공개
`codex-src` 클론에서는 `threads` 컬럼으로 전혀 발견되지 않았다. 이는 클론된 오픈소스 트리가 이
PC의 Codex Desktop 빌드와 정확히 같은 버전/브랜치가 아니거나, Desktop 전용 비공개 코드에만 있는
컬럼일 수 있음을 뜻한다. **"공식 소스 확인" 하나만으로 확정하지 않고 항상 실제 데이터와 함께
교차검증해야 한다는 CLAUDE.md §33 원칙이 이번에도 실측으로 증명됐다** — 공식 소스만 봤다면
`has_user_event`를 "존재하지 않는 컬럼"으로 잘못 결론 내릴 뻔했다. **결론: 컬럼 존재 여부는 항상
`PRAGMA table_info(threads)` 실측을 신뢰하고, 공식 소스는 "의미를 이해하는 보조 자료"로만 쓴다.**

### 1.5 결론 — Backup V1이 보존할 thread metadata

**위 38개 컬럼 전부를 보존한다.** "지금 필요한 것만" 고르지 않는다 — 컬럼 구성은 버전마다 다르고
(`docs/codex-storage-format.md` §4), 지금 안 쓰는 값이 Restore 시점엔 필요할 수 있다. 구현:
`Domain.Codex.Threads.ThreadRow`를 이 38개 컬럼 전체를 담도록 확장하고(`ThreadRowReader.KnownColumns`도
동일하게 확장), 모르는/opaque 값(`sandbox_policy`, `approval_mode` 등)은 문자열 그대로 담는다.
컬럼이 없는 Codex 버전에서는 전부 `null`로 남긴다(추측하지 않는다, 기존 `ThreadRow` 설계 원칙 그대로).

> **Phase 05_01 정정**: Phase 5 최초 구현은 "38개 전부 보존"이라고 문서에 적었지만 실제로는
> `source` 컬럼(NOT NULL)이 `ThreadRowReader.KnownColumns`와 `ThreadRow`에서 누락돼 있었다 —
> GitHub 코드 리뷰로 발견됐다. `ThreadRow`/`ThreadRowReader`/`BackupConversationMetadata` 세
> 곳을 실제 38컬럼과 기계적으로 대조하는 테스트(`ThreadRowSchemaCoverageTests`,
> `BackupConversationMetadataCoverageTests`)를 추가해 같은 종류의 누락이 다시 생기면 빌드가
> 아니라 테스트가 확실히 실패하게 만들었다. 또한 Manifest의 `resolvedTitle`(가공값, 제목 우선순위
> 결과)이 원본 `title`/`name`/`firstUserMessage`/`preview` 컬럼의 대체물로 쓰이고 있었다 — 이제는
> 원본 4개 필드를 전부 별도로 그대로 보존하고, `resolvedTitle`은 "표시용 편의 필드"로만 남긴다
> (§3.2 참고).

---

## 2. 첨부/비-텍스트 콘텐츠 조사

### 2.1 방법

실제 rollout 371개 파일(161,048줄)을 스트리밍으로 훑어 `event_msg/item_completed`의 `content` 배열
중 `"text"` 키가 없는 원소의 **모양(속성 이름 조합)**과 `type` 값만 집계했다(값/본문은 출력하지
않음). 이어서 `attachments`/`visualizations`/`generated_images`/`worktrees`/`pasted text file:` 같은
마커 문자열이 등장하는 줄의 **위치(top type/payload type/item type)만** 집계했다(마찬가지로 값은
출력하지 않음). 공식 소스에서 같은 개념을 찾아 대조했다.

### 2.2 결과

| 마커/모양 | 실측 | 공식 소스 대조 | V1 정책 |
|---|---|---|---|
| `content[].{type:"local_image", path}` | 367건, **path가 전부 `attachments\` 밖**(외부 경로) | — | **포함.** Export 시점에 파일이 존재하면 payload로 포함(dedup), 없으면(흔함 — 임시 스크린샷 등) manifest에 warning 기록. 조용히 버리지 않는다 |
| `content[].{type:"image_url", image_url}` | 22건 | — | 값이 data URI든 원격 URL이든 rollout 파일 바이트 자체에 이미 들어있다(원본 파일을 byte-for-byte 그대로 복사하므로) → **별도 처리 불필요** |
| `attachments\<uuid>\pasted-text.txt` + `pasted-text-attachments.json` | 실물 17개/361KB. rollout content의 JSON **키**로는 0건 참조, `attachments` **문자열**로는 116줄 있으나 UserMessage/CommandExecution/WebSearch 등 다양한 곳에 흩어져 있어 하나의 구조적 참조 메커니즘이 아니었다 | 이 이름의 메커니즘(`pasted-text-attachments.json`)은 공개 `codex-rs`에 **없음**(`grep` 0건). 가장 가까운 것은 `tui/src/goal_files.rs`의 `attachments/pasted-text-N.txt` + 메시지 본문에 평문으로 삽입되는 `"pasted text file: {path}. Read this file before continuing."` — 이름 규칙(N번호 vs uuid)부터 다르다. 이 PC의 파일명은 uuid 방식이라 Desktop 전용/비공개 기능일 가능성. 실측에서 `"pasted text file:"` 문자열은 0건이었다 | **V1에서 제외.** 구조적으로 신뢰할 수 있는 참조를 찾지 못했다(추측 금지). 알려진 한계로 문서화 |
| `visualizations` 문자열 | 8,500줄/283파일, 그러나 절대다수가 `turn_context`/`thread_settings_applied`/`world_state`/`session_meta`(환경/설정 echo)이고 실제 메시지 안(`item_completed`)에는 43건뿐 | `::codex-inline-vis{file="..."}` 인라인 지시어(`tui/src/inline_visualization.rs`)로 참조 가능하다고 확인됨 | 실측에서 이 지시어 문자열 자체(`codex-inline-vis`)는 **0건** — 이 데이터셋엔 실제로 트리거된 사례가 없다. **V1에서 제외**, 지시어가 실제로 나타나는 사례를 만나면 재검토 |
| `generated_images` 문자열 | 97줄/10파일, 전부 `function_call`/`function_call_output`(도구 호출/결과) 또는 `Extension`/`FileChange` 항목 | 이미지 생성 도구가 **세션의 cwd 기준 상대 경로**(`environment.cwd.join("generated_images")`)를 도구 출력 텍스트에 절대경로로 그대로 삽입한다(`ext/image-generation/src/artifact.rs`) — 구조화된 필드가 아니라 자유 텍스트 | **V1에서 제외.** 자유 텍스트에서 파일 경로를 안전하게 추출하는 규칙이 아직 없다(오탐 위험) — 알려진 한계로 문서화, Export 시 이런 참조가 있는 대화라면 warning 없이 조용히 넘어가지만 이 문서에 명시했으니 향후 재검토 대상 |
| `worktrees` 문자열 | 848줄/80파일, 대부분 `cwd`/`workspace_roots` 등 환경 설정 맥락 | git worktree 체크아웃 런타임 디렉터리(`worktree/src/settings.rs`), rollout content에 직접 임베드되는 근거 없음 | **V1에서 제외**(CLAUDE.md §27과 동일 경계 — 실제 프로젝트/워크트리 파일은 백업 대상이 아니다) |

### 2.3 V1 첨부 정책 요약

- **포함하는 것**: `local_image.path`가 가리키는 파일 중 Export 시점에 실존하는 것만, dedupe해서
  `payload/attachments/`에 담는다. 원본 절대경로는 Manifest에 참고용으로만 남긴다(다른 PC에서
  그대로 못 쓰는 로컬 경로임을 명시).
- **포함하지 않는 것**: `attachments\`(pasted-text) 폴더 전체, `visualizations\`, `generated_images\`
  가 담고 있는 실제 파일, `worktrees\`. 전부 "구조적으로 안전하게 참조를 추적할 수 없다"는 실측
  근거로 제외했다 — 임의로 대상을 넓히거나 좁히지 않았다.
- **경고 정책**: `local_image.path`가 가리키는 파일이 없으면(스크린샷 임시파일 등, 흔함) manifest의
  `Warnings`에 개수만 기록하고 원본 경로는 로그에 남기지 않는다.
- **원본 경로 ↔ entry 경로 역매핑(Phase 05_01)**: `manifest.attachments[]`(`BackupAttachmentMetadata`)에
  `{entryPath, originalAbsolutePath}` 쌍을 전부 기록한다. 이전에는 conversation의
  `payloadAttachmentEntries`가 entry 경로만 나열해서, 같은 basename이 서로 다른 디렉터리에 있었던
  경우 "이 entry가 원래 어떤 로컬 파일이었는지" 역으로 알 수 없었다 — GitHub 코드 리뷰로 발견됐다.
- **Scanner의 authoritative-source 정책(Phase 05_01)**: `LocalImageAttachmentScanner`는 이제
  `ConversationItemParser`와 **완전히 같은 파일 단위 정책**을 쓴다 — 한 rollout 파일에
  `event_msg/item_completed`가 하나라도 있으면 그 파일의 `local_image` 참조만 신뢰하고,
  하나도 없는 파일(fallback-only)에서만 `response_item`을 본다. 실측(2026-09-12, 371개 파일):
  fallback-only 파일 51개는 전부 non-text content가 0건이었지만, 이 형식 자체가 그런 콘텐츠를
  담을 수 없다는 보장은 아니므로 방어적으로 정책을 통일했다. `response_item`의 non-text content는
  실측 결과 `input_image.image_url`(403건, **전부 `data:` URI** — 이미 rollout 바이트 안에
  내용이 있어 별도 파일 참조가 아니다)뿐이었다. Scanner는 `image_url`이 `data:`/원격 URL이 아닐
  때만(=로컬 파일 경로로 보일 때만) 외부 참조로 취급한다.

---

## 3. Backup Format V1 — 확정 스펙

### 3.1 컨테이너 구조

```
<name>.codexbackup                       (ZIP, System.IO.Compression)
├─ manifest.json
├─ checksums.json
└─ payload/
   ├─ rollouts/<원본 파일명>              (원본 바이트 그대로, .jsonl 또는 .jsonl.zst)
   └─ attachments/<n>/<원본 파일명>       (local_image가 가리키는 실존 파일, n = 충돌 방지용 dedupe 인덱스)
```

ZIP entry 이름에는 **사용자 PC의 절대경로를 넣지 않는다**. rollout 파일명은 Codex 자체가 이미
`threadId`/타임스탬프로 전역적으로 유일하게 짓기 때문에 날짜별 하위 폴더 없이 평평하게 담아도
충돌하지 않는다(`archived_sessions`의 파일도 같은 명명 규칙). attachment는 원본 경로가 임의의
위치일 수 있어 파일명 충돌 가능성이 있으므로 인덱스 폴더로 분리한다.

### 3.2 Manifest (`manifest.json`)

```jsonc
{
  "backupFormatVersion": 1,
  "appVersion": "…",
  "createdAtUtc": "2026-09-12T00:00:00Z",
  "sourceOS": "Windows",
  "sourceCodexDesktopVersion": "26.903.…",
  "sourceCodexCliVersion": "0.153.…",
  "projectCount": 0,           // 선택된(사용자가 고른) 대화가 속한 프로젝트 수만
  "conversationCount": 0,      // 선택된 대화 수만 — ancestor-only는 제외(요구사항)
  "dependencyConversationCount": 0, // 선택되진 않았지만 체인 복원에 필요해 포함된 조상 thread 수
  "payloadCount": 0,           // rollout + attachment 합계
  "projects": [
    { "projectId": "…" /* null이면 미분류 */, "displayName": "…", "originalRootPaths": ["…"],
      "conversationThreadIds": ["…"] }
  ],
  "conversations": [
    {
      "threadId": "…",
      "isSelected": true,        // false = dependency-only(조상)

      // 표시용 편의 필드(가공값) — 아래 원본 컬럼들을 대체하지 않는다(Phase 05_01 정정, §1.5).
      "resolvedTitle": "…", "titleSource": "StateName|SessionIndex|Title|FirstUserMessage|Preview",
      "resolvedProjectId": "…",   // CodexProjectResolver의 3중 경로 해결 결과. Projects 그룹핑과 같은 값

      // 원본 threads 컬럼(38컬럼) 그대로 — Phase 05_01에서 title/name/firstUserMessage/preview/
      // source/rollout_path/created_at(_ms)/updated_at(_ms)/recency_at(_ms)/archived_at/
      // sectionEnteredAtMs를 추가로 보존하게 됐다. Restore가 그대로 재사용할 값이 아니라(특히
      // originalRolloutPath), 원본 metadata를 잃지 않기 위한 보존이다.
      "projectId": null, "originalCwd": "…", "originalRolloutPath": "…", "source": "vscode",
      "title": "…", "name": "…", "firstUserMessage": "…", "preview": "…",
      "createdAtSeconds": 0, "createdAtMs": 0, "createdAtUtc": "…",
      "updatedAtSeconds": 0, "updatedAtMs": 0, "updatedAtUtc": "…",
      "recencyAtSeconds": 0, "recencyAtMs": 0,
      "archived": false, "archivedAtSeconds": null, "archivedAtUtc": null,
      "historyMode": "paginated", "threadSource": "user",
      "cliVersion": "…", "modelProvider": "…", "model": "…", "reasoningEffort": "…",
      "memoryMode": "…", "sandboxPolicyRaw": "{…opaque…}", "approvalMode": "…",
      "tokensUsed": 0, "hasUserEvent": true,
      "gitSha": null, "gitBranch": null, "gitOriginUrl": null,
      "agentNickname": null, "agentRole": null, "agentPath": null,
      "isPinned": false, "threadSectionId": null, "sectionPosition": null, "sectionEnteredAtMs": null,

      "payloadRolloutEntries": ["payload/rollouts/rollout-….jsonl", "…"], // 이 thread 체인 재구성에 필요한 파일들(순서 보존)
      "payloadAttachmentEntries": ["payload/attachments/0/shot.png"]      // 아래 top-level attachments[]로 원본 경로를 역매핑
    }
  ],
  "attachments": [
    { "entryPath": "payload/attachments/0/shot.png", "originalAbsolutePath": "C:\\Users\\…\\shot.png" }
  ],
  "warnings": ["…"]   // 누락된 attachment 개수 등. 사용자 원문/개인 경로 없음
}
```

**`selected` vs `dependency` 구분**: `conversations[].isSelected=false`인 항목은
`GetSelectedThreadIdsSnapshot()`엔 없었지만 어떤 선택 대화의 조상 체인이라 파일이 필요해 포함된
thread다. `projectCount`/`conversationCount`는 `isSelected=true`만 센다(Validator가 이 일치를
직접 검사한다, §3.7).

**`attachments[]`(Phase 05_01 신설)**: `payloadAttachmentEntries`의 entry 경로만으로는 "원래 어떤
로컬 파일이었는지" 역으로 알 수 없다(같은 basename이 다른 디렉터리에 있었을 수 있고, entry 이름은
dedupe를 위해 인덱스 폴더로 분리돼 있다) — 이 배열이 유일한 신뢰 가능한 역매핑이다.

### 3.3 checksums.json — 체크섬 정책(순환 참조 회피)

```jsonc
{ "entries": [ { "path": "payload/rollouts/…", "byteLength": 12345, "sha256": "…" }, … ] }
```

- **모든 payload 파일**(rollout + attachment) + **`manifest.json` 자신**에 대해 하나씩 존재한다.
- **Phase 05_01 정정**: 최초 설계는 "manifest.json도 자기 자신을 해시하면 순환이 생긴다"고 판단해
  체크섬 목록에서 아예 뺐다. 다시 검토한 결과 이는 틀린 판단이었다 — **`checksums.json`이
  `manifest.json`의 해시를 기록**하고 `manifest.json`은 `checksums.json`의 내용을 전혀 참조하지
  않으므로 방향이 하나뿐이라 순환이 아니다(진짜 순환은 "A가 B를, B가 A를 참조"할 때만 생긴다).
  그래서 이제 `manifest.json`도 accidental corruption을 체크섬으로 탐지할 수 있다(manifest 1바이트가
  바뀌어도 여전히 유효한 JSON으로 파싱될 수 있는 경우까지 잡아낸다).
- **여전히 체크섬에 없는 것은 `checksums.json` 자기 자신뿐이다** — 이건 진짜 순환(자기 자신의
  최종 바이트를 알아야 자기 자신의 해시를 계산할 수 있음)이라 불가능하다. `checksums.json`의
  무결성은 "정상적으로 JSON 파싱된다"는 구조적 검사와, 그 안에 담긴 각 항목이 실제 ZIP entry와
  길이/해시가 일치하는지로 간접 검증한다.
- **SHA-256은 accidental corruption(전송 오류, 디스크 오류, 수동 편집 실수 등) 탐지용
  integrity 체크섬이지 전자서명이 아니다.** 서명 키가 없으므로 "이 백업이 정말 이 앱이 만든 것"이라는
  authenticity(신원 보증)는 보장하지 않는다 — 악의적으로 조작한 사람이 payload와 체크섬을 똑같이
  다시 계산해 넣으면 이 검증은 통과한다. V1의 목표는 "실수로 손상된 백업을 조용히 성공 처리하지
  않는 것"이지 "위조 방지"가 아니다.
- Import(Phase 6)/Validator는 `checksums.json`을 **유일한 체크섬 출처**로 신뢰한다.

### 3.4 Dependency Closure(체인 재구성) 정책

**기존 `ThreadChainResolver`/`ConversationTranscriptBuilder`의 lineage 규칙을 그대로 재사용한다
(새로 만들지 않는다)**. `ThreadChainResolver.ResolveAncestry`가 뿌리→leaf 순서의 조상 thread ID
목록을 주면:

- 마지막(leaf, 즉 원래 선택된 thread) 항목은 **자기 체인의 파일 전부**(`ThreadChain.Files`)를 포함한다.
- 그 앞의 각 조상 항목은, 자신의 자식이 가리키는 rollout ID(`childChain.ParentThreadId`)와
  `OwnRolloutId`가 일치하는 파일까지만(그 이후 세그먼트는 조상 자신의 무관한 별도 연속이므로 제외)
  포함한다 — `ConversationTranscriptBuilder.ReadChainMessages`가 메시지를 읽을 때 파일을 고르는
  것과 **완전히 동일한 경계 판정**이다.
- 이 판정 로직을 `CodexBackupManager.Codex.Threads.ThreadDependencyResolver`로 추출해 Viewer
  (`ConversationTranscriptBuilder`)와 Export(`ExportPlanBuilder`)가 **동일한 코드**를 쓴다(문서
  요구사항의 "공통 dependency resolver").
- 여러 선택 대화가 같은 조상 rollout을 공유하면 payload는 **한 번만** 포함한다(파일 절대경로
  기준 dedupe).
- 순환 참조/누락된 부모는 `ThreadChainResolver.ResolveAncestry`가 이미 무한루프 없이 처리한다
  (그 결과를 계속 돌려준다) — **단, Phase 05_01부터는 이걸 warning으로 넘기지 않는다.** 선택한
  대화의 조상 rollout을 일부라도 찾을 수 없거나 순환 참조가 있으면 그 Export 전체를 FAIL시킨다
  (아래 "부분 성공 금지" 정책 참고). "파일이 없으면 어차피 복사할 것도 없다"는 최초 판단은
  틀렸다 — 선택한 대화 하나를 **불완전하게** 백업해 놓고 성공으로 보여주는 것 자체가 백업 도구로서
  받아들일 수 없는 결과이기 때문이다.

### 3.4.1 부분 성공 금지 정책(Phase 05_01)

다음 중 하나라도 선택한 대화에서 발생하면 `ExportPlan.FatalErrors`에 기록되고,
`BackupWriter.Write`는 **temp 파일조차 만들지 않고** 즉시 실패를 돌려준다(기존 목적지 파일이
있었다면 그대로 보존된다):

- 선택한 대화의 rollout 파일 체인 자체를 찾을 수 없음
- 선택한 대화의 state DB metadata(`ThreadRow`)를 찾을 수 없음
- 선택한 대화의 조상 체인에서 순환 참조 발견
- 선택한 대화의 조상 rollout 파일 일부를 찾을 수 없어 완전한 dependency closure를 만들 수 없음

반대로 다음은 여전히 **optional**로 취급해 `Warnings`에만 기록하고 Export는 성공 처리한다:

- 참조된 `local_image` 첨부 파일이 Export 시점에 이미 삭제되어 없음

이 구분의 기준은 "이게 없으면 대화 내용 자체를 신뢰할 수 없는가"다 — rollout/metadata/closure는
대화의 본체이고, 첨부 이미지 하나는 부가 정보다.

### 3.5 원본 바이트 보존

`.jsonl`/`.jsonl.zst` 전부 **원본 바이트를 그대로** ZIP entry로 스트리밍 복사한다. 파싱/재작성/압축
해제-재압축을 하지 않는다 — unknown event, 암호화된 reasoning, `end_byte_offset` semantics를 전부
보존하기 위해서다. Export한 payload의 SHA-256은 원본 파일의 SHA-256과 항상 같아야 한다(自기
검증 §3.7에서 실측 확인).

### 3.6 원본 변경 감지(Export 도중)

각 payload 파일을 열기 직전에 `(Length, LastWriteTimeUtc)`를 스냅샷하고, 스트리밍 복사가 끝난
직후 같은 값을 다시 읽어 비교한다. 하나라도 다르면(길이 변경, 수정시각 변경) 그 자리에서 Export
전체를 실패 처리하고 temp 파일을 지운다 — Codex가 실행 중이어서 그 파일이 append/rotate됐을 가능성을
"부분적으로 성공"으로 위장하지 않기 위해서다. Codex 프로세스를 강제 종료하지 않는다(요구사항).

### 3.7 Atomic Publish + Self Validation

0. `ExportPlan.FatalErrors`가 하나라도 있으면(§3.4.1) 여기서부터 시작하지 않는다 — temp 파일도
   만들지 않고 즉시 실패를 돌려준다.
1. 최종 파일과 같은 디렉터리에 `.{name}.tmp-{16자리 랜덤}.codexbackup`을 만든다(같은 볼륨 →
   최종 이동이 원자적).
2. 스트리밍으로 payload를 전부 쓰고 SHA-256을 계산하며, `manifest.json`의 바이트를 확정해 그
   해시를 `checksums.json`에 추가한 뒤, 마지막에 `checksums.json`/`manifest.json`을 쓴다.
3. temp 파일을 닫은 뒤 **`BackupReader`/`BackupValidator`로 다시 열어** 검사한다(Phase 05_01
   hardening 이후 목록):
   - manifest.json 존재·파싱·`backupFormatVersion`
   - checksums.json 존재·파싱, 그 안의 경로 **중복 없음** + **안전한 상대경로**
   - ZIP entry 이름 **ordinal 중복 없음** + **Windows 기준(대소문자 무시) 충돌 없음** + path
     traversal 없음 + **허용된 위치(`manifest.json`/`checksums.json`/`payload/` 하위)만 존재**
   - manifest 안 **threadId 중복 없음**
   - **`conversationCount`/`dependencyConversationCount`/`projectCount`/`payloadCount`가 실제
     배열/entry 개수와 정확히 일치**
   - **project의 `conversationThreadIds`가 실제로 선택된(`isSelected=true`) thread만 참조**
   - conversation → payload(rollout/attachment) 참조가 checksums.json/ZIP 양쪽에 다 있음
   - **모든 `payload/` ZIP entry가 checksums.json에도 있음**(반대 방향 누락 확인)
   - 모든 checksum entry의 byte length/SHA-256이 실제 압축 해제 결과와 일치(변조 탐지)
   - rollout payload 최소 JSONL parse 가능 여부(`.jsonl.zst`는 압축 해제 후)
   - **어떤 이유로도 예외를 던지지 않는다** — malformed 입력은 전부 `BackupValidationResult.Fail`로
     돌아온다(Phase 6부터 `.codexbackup`이 외부 입력이 되므로 필수).
4. 전부 PASS일 때만 `File.Move(temp, destination, overwrite)`로 최종 파일이 된다. 실패/취소/예외
   시 temp를 삭제하고 **기존에 있던 정상 backup 파일은 절대 건드리지 않는다**(먼저 지우지 않고
   move 시점에만 교체하므로, 검증 실패 시 기존 파일이 그대로 남는다).
5. `overwrite` 여부는 API 파라미터로 명시적으로 받는다(기본값 false — 목적지가 이미 있으면
   시작 전에 즉시 실패).

### 3.8 Export UI Cancellation(Phase 05_01)

`MainViewModel`에 `CancelExportCommand`를 추가했다 — Export 중에만 버튼이 보이고
(`IsExporting`으로 `Visibility` 바인딩), 누르면 `BackupWriter.Write`에 넘긴
`CancellationToken`을 취소한다. 실제 취소 처리(temp 삭제, 목적지 미생성)는 core
(`BackupWriter`)가 스트리밍 복사 도중에도 반응하도록 이미 구현되어 있었다 — 300MB급 payload
복사 도중 취소해도 temp가 즉시 삭제되고 목적지 파일이 생기지 않음을 실제 파일로 확인했다
(`BackupWriterTests.대형_payload_복사_도중_취소해도…`).

### 3.9 이번에 포함하지 않는 것(§9 CLAUDE.md와 동일 원칙)

`state_*.sqlite`/`thread_history_*.sqlite`/`logs_*.sqlite`/`goals_*.sqlite`/`memories_*.sqlite`/
`queue_*.sqlite`/`-shm`/`-wal`/기타 projection 캐시는 Backup에 절대 포함하지 않는다. 필요한
metadata만 §3.2의 독립 JSON으로 추출한다.

---

## 4. 알려진 한계(V1)

1. `attachments\`(pasted-text) 폴더, `visualizations\`, `generated_images\`가 실제로 담고 있는
   파일은 V1 Export에 포함되지 않는다(§2.3). 구조적으로 안전한 참조 추적 방법을 찾지 못했다 —
   추측으로 텍스트에서 경로를 파싱하지 않았다.
2. `has_user_event` 컬럼의 정확한 Rust 타입/의미는 공개 소스에서 확인하지 못했다(§1.4) — 값은
   그대로 보존하지만 해석하지는 않는다.
3. `.zst` 압축 rollout은 이 PC에 실물이 0개라 Export 스트리밍 복사 경로가 실제 `.zst` 파일로는
   아직 검증되지 못했다(합성 fixture로만 검증, `docs/codex-storage-format.md` §9와 동일한 기존 한계).
4. 다단계 분기(조상의 조상)의 실제 사례는 여전히 이 PC 데이터에 없어 실측 검증하지 못했다(기존
   한계, `docs/codex-storage-format.md` §9-8과 동일).
5. SHA-256은 accidental corruption 탐지용이며 전자서명(authenticity 보장)이 아니다(§3.3).
6. 첨부 dedupe는 원본 절대경로 문자열 비교(`StringComparer.OrdinalIgnoreCase`) 기준이다 —
   심볼릭 링크나 대소문자만 다른 두 경로가 실제로 같은 파일을 가리키는 경우까지는 동일 파일로
   인식하지 못할 수 있다(실제 데이터에서 그런 사례는 발견되지 않았다).

---

## 5. Phase 05_01 — Backup V1 Freeze / Restore Sufficiency Hardening

GitHub 코드 리뷰에서 발견된 Phase 5 최초 구현의 정합성 문제를 수정하고 스펙을 FROZEN 상태로
확정한 작업. 변경 요약(각 항목의 근거/세부 내용은 위 해당 절에 있다):

| # | 문제 | 수정 |
|---|---|---|
| 1 | `threads.source`(NOT NULL 컬럼)가 `ThreadRow`/`ThreadRowReader`에서 누락 | 추가 + 38컬럼 전체를 실제 스키마와 기계적으로 대조하는 회귀 테스트 추가(§1.5) |
| 2 | `resolvedTitle`(가공값)이 원본 `title`/`name`/`firstUserMessage`/`preview`/`rollout_path` 등을 대체 | 원본 컬럼을 전부 별도 필드로 그대로 보존(§3.2) |
| 3 | 선택한 대화의 chain/metadata/ancestor가 없어도 warning만 남기고 "성공" 처리 | `ExportPlan.FatalErrors` 도입 — 하나라도 있으면 temp도 만들지 않고 즉시 실패(§3.4.1) |
| 4 | 첨부 원본 경로 ↔ entry 경로 역매핑이 없음 | `manifest.attachments[]`(`BackupAttachmentMetadata`) 신설(§3.2) |
| 5 | Scanner가 `response_item`-only(fallback) 파일의 이미지 참조를 놓칠 수 있음 | `ConversationItemParser`와 같은 file-level authoritative 정책으로 통일(§2.3) |
| 6 | Validator가 malformed 입력(중복 checksum 경로 등)에서 예외를 던질 수 있음 | 중복/불안전 경로를 먼저 검사해 항상 `Fail`로 반환, 최상위 `try/catch`로 이중 방어(§3.7) |
| 7 | Windows 대소문자 무시 entry 충돌 미검출 | `OrdinalIgnoreCase` 그룹핑 추가 검사(§3.7) |
| 8 | manifest 내부 개수 필드(`conversationCount` 등)가 실제 배열과 다를 수 있어도 미검출 | 4개 카운트 필드 + project 참조 무결성 검사 추가(§3.7) |
| 9 | `manifest.json`을 "순환 참조"로 오판해 체크섬 보호 대상에서 제외 | `checksums.json`이 `manifest.json`의 해시를 기록(단방향, 순환 아님)(§3.3) |
| 10 | Export 취소 UI가 없음(core엔 있었으나 버튼이 없었음) | `CancelExportCommand` + 조건부 표시 버튼 추가, 300MB급 실제 취소 확인(§3.8) |

이 표의 모든 항목은 실제 RED(수정 전 재현) → GREEN(수정 후 통과) 테스트로 확인했다. 실제
`.codex` 데이터로 짧은/segmented/attachment/288MB 대화 4종을 다시 Export해 전부 재검증 PASS를
확인했고, source-of-truth 해시는 작업 전후 동일했다.
