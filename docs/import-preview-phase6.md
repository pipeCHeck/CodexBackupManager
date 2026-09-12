# Phase 6 — Import Preview + Update/Divergence Analysis 스펙

이 문서는 Phase 6(`.codexbackup`을 선택했을 때 검증 → 내용 표시 → 현재 PC Codex와 비교 → 경로
재매핑 제안 → Phase 7 계획 Preview)의 정식 스펙이다. `docs/codexbackup-format-v1.md`가 "무엇을 어떤
포맷으로 백업하는가"를 다룬다면, 이 문서는 "그 backup을 다른 PC의 현재 상태와 어떻게 비교하고
보여주는가"를 다룬다.

> **범위**: Phase 6은 판정과 미리보기까지만 한다. Codex 원본에는 어떤 write도 하지 않는다(SQLite
> INSERT/UPDATE, rollout append/복사, global-state 변경, Snapshot/Rollback 전부 Phase 7의 역할).

---

## 1. 핵심 원칙 — 왜 "중복" 개념을 쓰지 않는가

이 프로그램의 실제 사용 시나리오는 두 PC를 오가면서 같은 Codex 대화를 계속 이어서 작업하는 것이다:

```
PC A에서 Thread X 작업 → Export → PC B에서 Import → 같은 Thread X 계속 작업
→ Export → PC A에서 다시 Import
```

같은 ThreadId가 로컬에 이미 있다고 "중복 → 건너뛰기/교체"로 처리하면 이 시나리오 자체가 불가능해
진다. 그래서 Phase 6부터는 같은 ThreadId를 가진 두 conversation(로컬 ↔ backup)의 관계를
`RevisionRelation`으로 명시적으로 분류한다.

**timestamp는 절대 판정 근거가 아니다.** `updated_at`이 크다고, 파일 수정 시각이 최신이라고 더
"새것"이라고 가정하지 않는다 — 시간값은 UI 참고 정보일 뿐이다. 실제 rollout lineage와 논리적 바이트
내용만이 판정 근거다.

---

## 2. `RevisionRelation`(`Domain.Codex.Import.RevisionRelation`)

| 값 | 의미 | Phase 7 기본 계획(`ImportPlannedAction`) |
|---|---|---|
| `New` | backup ThreadId가 현재 로컬 Codex에 없음(`CodexCatalog.Chains`에 없음) | `Import` — 새로 Import |
| `Identical` | 로컬과 backup이 완전히 같은 conversation revision | `NoOp` — 아무것도 하지 않음 |
| `IncomingAhead` | 로컬이 backup의 논리적 prefix — backup이 같은 lineage 위에서 더 진행됨 | `Update` — Fast-forward 갱신 |
| `LocalAhead` | backup이 로컬의 논리적 prefix — 로컬이 같은 lineage 위에서 더 진행됨 | `Skip` — 로컬을 뒤로 되돌리지 않음 |
| `Diverged` | 공통 지점 이후 로컬/backup 양쪽에서 각각 다른 작업이 진행됨(또는 같은 ThreadId지만 서로 무관한 lineage) | `RequiresDecision` — 자동 merge 금지, 사용자 결정 필요 |
| `Unverifiable` | lineage 정보 누락/손상/순환 참조 등으로 안전한 관계 판정 자체가 불가능 | `Blocked` — 적용 금지 |

내부 enum 영어명은 UI에 그대로 노출하지 않는다(`ImportConversationRowViewModel.StatusLabel`):

```
New            → 신규
Identical      → 동일
IncomingAhead  → 업데이트 가능
LocalAhead     → 현재 PC가 더 최신
Diverged       → 분기 충돌
Unverifiable   → 확인 불가
```

---

## 3. `ConversationRevision` / `RolloutSlice` — revision fingerprint 구조

`Domain.Codex.Import` 네임스페이스:

```
RolloutSlice
  RolloutId              // File.OwnRolloutId — 세그먼트/파일 자신의 안정 ID
  File                   // 재해싱용 참조(로컬 파일 경로 또는 backup ZIP entry 경로)
  Boundary               // Full | OrdinalCutoff | ByteOffsetCutoff
  LogicalByteLength      // 포함된 논리(압축 해제) 바이트 길이
  Sha256Hex              // 포함된 바이트의 SHA-256

ConversationRevision
  ThreadId
  OrderedSlices[]        // 뿌리(가장 오래된 조상) → leaf 순서
```

**핵심 원칙**: 원본 파일 전체가 아니라 **이 conversation이 실제로 소비하는 부분**만 fingerprint한다.
child가 parent rollout의 중간에서 분기한 뒤 parent가 계속 사용됐다면, Export payload에는 parent
파일 전체가 들어 있을 수 있지만(byte-preservation 정책), revision 비교에서는 분기 지점까지만
본다 — 이건 Export의 dependency closure 경계 판정(`ThreadDependencyResolver`)과 완전히 같다.

`.jsonl.zst`는 압축 해제한 논리 바이트 기준으로 비교한다(스트리밍, 파일 전체를 메모리에 올리지
않는다) — 한쪽 PC는 `.jsonl`, 다른 쪽은 `.jsonl.zst`일 수 있기 때문이다. Backup V1 payload 자체는
여전히 원본 압축 바이트 그대로 보존한다(바뀌지 않았다).

### 3.1 만드는 방법 — `Codex.Revisions.ConversationRevisionBuilder`

```
Build(threadId, chains, IRolloutSliceReader, ct) → ConversationRevisionBuildResult
```

1. `chains`에 `threadId`가 없으면 → Unverifiable("체인 없음").
2. `ThreadDependencyResolver.ResolveChainLinks`로 조상 체인을 얻는다(Export/Viewer와 같은 코드).
   순환 참조가 있으면 → Unverifiable. 조상 체인 정보가 일부라도 없으면(경고 발생) → Unverifiable.
3. 각 링크(조상 → leaf 순서)마다 `ThreadDependencyResolver.ResolveFileSlices`로 파일별 컷오프
   (ordinal 우선, 없으면 byte offset)를 계산하고, `RolloutSliceHasher`로 그 슬라이스만 스트리밍
   해시한다.
4. 전부 성공하면 `ConversationRevision(threadId, 슬라이스 순서 목록)`.

**새 lineage 규칙을 만들지 않는다.** `ThreadDependencyResolver.ResolveFileSlices`(신설)가
"파일별로 어디까지 포함하는가"(ordinal/byte cutoff)를 계산하고, `Conversation.ConversationTranscriptBuilder`
(Viewer)도 이 메서드를 쓰도록 리팩터링했다 — Viewer/Export/Import Preview 세 곳이 완전히 같은
코드로 lineage 경계를 판단한다.

### 3.2 backup 쪽 lineage 재구성 — `Backup.Import.BackupCatalogReader`

manifest에 별도 lineage 필드를 추가하지 않았다. 대신 backup의 `payload/rollouts/` entry들만으로
로컬 카탈로그를 만들 때와 완전히 같은 절차를 반복한다:

1. entry 파일명을 `RolloutFileNamePattern`으로 파싱해 threadId/segmentId/timestamp/압축 형태를 얻는다
   (이름 규칙에 맞지 않는 entry는 조용히 무시 — 로컬 탐색과 동일한 정책).
2. 각 entry를 열어 `session_meta`를 `CodexSessionParser`(스트림 오버로드 신설)로 읽는다 — 원본
   rollout 파일이 이미 `history_base`/`forked_from_id`를 갖고 있으므로 그대로 재사용한다.
3. `ThreadChainResolver.Resolve`를 그대로 호출해 `ThreadChain` 맵을 만든다.

Export가 선택 대화 + 필요한 조상 전체를 항상 포함하므로(Phase 05_01 "부분 성공 금지" 정책),
`payload/rollouts/` 전체로 만든 체인 맵은 manifest에 나열된 어떤 conversation(선택이든
dependency-only든)의 ancestry도 해석할 수 있다.

### 3.3 `IRolloutSliceReader` — 로컬/backup 공통 추상화(`Codex.Rollout`)

```
interface IRolloutSliceReader { Hash(file, cutoffOrdinal, cutoffByte, ct) → Result }

LocalFileRolloutSliceReader     // 로컬 파일을 File.Open으로 연다
BackupRolloutSliceReader        // BackupReader.OpenEntry로 ZIP entry를 연다 (Backup 프로젝트)
```

`ConversationRevisionBuilder`/`ConversationRevisionComparer`는 바이트를 "어디서" 읽는지 전혀 모른다
— 이 인터페이스 뒤에서 로컬/backup이 교체된다. 새 출처(예: 클라우드 스토리지)가 생겨도 이
인터페이스만 구현하면 나머지 로직은 그대로 재사용된다.

---

## 4. `ConversationRevisionComparer` — 비교 알고리즘

```
Compare(local, localReader, incoming, incomingReader, ct) → RevisionRelation
```
(New/Unverifiable은 여기서 판정하지 않는다 — revision 자체를 만들 수 있었는지에 달려 있으므로
`ImportPreviewBuilder`가 그 앞단에서 결정한다. 이 함수는 "둘 다 정상적으로 만들어졌을 때"의 4가지
관계만 판정한다.)

각 위치 `i`(공통 구간)에서:

1. `RolloutId`가 다르면 → **Diverged**(lineage 자체가 다르다 — 순환/무관한 대화를 같은 ThreadId로
   재사용한 경우 등, "안전한 쪽"으로 수렴한다).
2. `RolloutId`가 같고 길이/해시가 완전히 같으면 → 다음 위치로.
3. `RolloutId`가 같은데 길이/해시가 다르면:
   - 이 위치가 **양쪽 다 마지막 슬라이스가 아니면** → **Diverged**(비-마지막 위치의 불일치는
     이 leaf 기준으로 설명할 수 있는 시나리오가 없다 — 안전하게 발산 취급).
   - **양쪽 다 마지막 슬라이스면**(같은 파일이 한쪽에서 더 길게 이어써졌을 가능성) → §4.1로.

공통 구간이 전부 일치했는데 슬라이스 개수가 다르면(한쪽에만 뒤이은 segment가 있음) →
개수가 더 많은 쪽이 **더 진행된 쪽**(`IncomingAhead`/`LocalAhead`).

### 4.1 "같은 파일, 다른 길이" — 실제로 byte prefix인지 다시 읽어서 확인한다

두 슬라이스의 **전체 해시만** 비교해서는 "한쪽이 다른 쪽의 진짜 이어쓰기"인지 "우연히 길이가 다른
순수 발산"인지 구분할 수 없다(다른 두 해시로부터 prefix 관계를 대수적으로 유도할 수 없다). 그래서:

1. 길이가 같은데 해시가 다르면 → 무조건 **Diverged**(append로 설명 불가).
2. 길이가 다르면, **더 긴 쪽을 짧은 쪽의 길이만큼 다시 잘라 재해시**한다(`IRolloutSliceReader`로
   해당 파일을 byte offset 컷오프로 다시 읽는다).
3. 재해시 결과가 짧은 쪽과 **길이+해시 전부 정확히 일치**해야만 진짜 prefix로 인정하고
   `IncomingAhead`(backup이 더 김) 또는 `LocalAhead`(로컬이 더 김)로 판정한다.
4. 일치하지 않으면 → **Diverged**(길이만 다르고 내용은 처음부터 갈라진 경우).

---

## 5. Fast-forward / Divergence 판정 — 요구사항 매핑

```
Local:  R1(full) R2(first 100KB)
Incoming: R1(full) R2(first 180KB) R3(full)
```

- `R1`은 양쪽 동일 → 통과.
- `R2`는 같은 rollout id, 로컬(100KB) < incoming(180KB) → incoming의 R2를 100KB로 다시 잘라
  해시 → 로컬의 R2 해시와 일치하면 이 위치는 "이어쓰기"로 인정.
- 로컬은 슬라이스 2개, incoming은 3개 → 공통 구간(R1, R2) 전부 일치 + incoming에 R3가 더 있음 →
  **IncomingAhead**.

반대로 로컬 쪽에 더 많은 슬라이스가 있으면 **LocalAhead**. ThreadId만 같다는 이유로 fast-forward
가능하다고 판단하지 않는다 — 위 알고리즘을 항상 전부 통과해야 한다.

---

## 6. Metadata 차이 — `MetadataDifferenceAnalyzer`(`Backup.Import`)

`RevisionRelation`(conversation 내용)과 완전히 분리된 판정이다. 같은 conversation content라도 cwd/
프로젝트 연결/고정 여부/섹션/제목/최근 사용 시각은 PC별로 다를 수 있다.

```
MetadataDifferences
  CwdDiffers               // threads.cwd, CanonicalPath 기준 비교(대소문자/\\?\ prefix 무시)
  ProjectAssignmentDiffers // 해결된 프로젝트 연결(ResolvedProjectId)
  PinnedDiffers            // 사이드바 고정 여부
  SectionDiffers           // 사이드바 섹션 ID
  TitleDiffers             // title/name 원본 값
  RecencyDiffers           // recency_at_ms
```

`local`이 `null`이면(=New 대화, 비교 대상 자체가 없음) `MetadataDifferences.None`을 돌려준다.
**Phase 6은 이 차이를 보여주기만 하고 병합 정책을 정하지 않는다** — 어떤 필드를 incoming으로
갱신하고 어떤 필드는 로컬을 유지할지는 Phase 7의 몫이다.

---

## 7. 프로젝트 경로 재매핑 Preview — `ProjectPathMapper`(`Backup.Import`)

```
ProjectPathMappingStatus
  NotApplicable   // "기타 대화"(미분류) 그룹 — 매핑 대상 아님
  AutoLinked      // 로컬 프로젝트와 canonical path가 일치해 자동 연결됨
  NotFound        // 일치하는 로컬 프로젝트를 찾지 못함 — 사용자가 폴더를 다시 지정해야 함
```

backup manifest의 `project.originalRootPaths`를 현재 로컬 카탈로그(`CodexCatalog.Projects`)의 각
프로젝트 `RootPaths`와 `CanonicalPath.AreSameLocation`(대소문자/`\\?\` prefix 무시)으로 비교해
자동 연결을 제안한다. **판정만 한다** — 이 선택은 Phase 6에서 Codex에 쓰지 않는다. 프로젝트가 현재
컴퓨터에 없어도(경로를 못 찾아도) conversation 자체의 Import 여부는 이 매핑과 무관하다(기존 설계
원칙 유지).

---

## 8. `ImportPreview` 구조 — `Backup.Import.ImportPreviewBuilder`

```
ImportPreviewBuilder.Build(backupFilePath | BackupReader, CodexCatalog localCatalog, ct) → ImportPreview

ImportPreview
  Success                          // BackupValidator 통과 여부. false면 다른 필드는 비어 있다
                                    // (malformed backup → Preview 생성 금지)
  ValidationErrors
  Manifest
  Projects: ImportProjectPreview[] // 선택된 대화만(요구사항 11)
  DependencyOnlyConversations[]    // 선택되지 않은 조상 — 일반 목록이 아니라 여기에만
  Warnings

ImportProjectPreview
  ProjectId, DisplayName
  PathMapping: ProjectPathMapping
  Conversations: ImportConversationPreview[]
  CountOf(RevisionRelation)        // UI 요약(상태별 개수)용

ImportConversationPreview
  ThreadId, IsSelected, ResolvedTitle
  Relation: RevisionRelation
  PlannedAction: ImportPlannedAction   // Import|NoOp|Update|Skip|RequiresDecision|Blocked
  Metadata: MetadataDifferences
  Warnings                             // 이 대화 판정 중 발견한 개별 문제(Unverifiable 사유 등)
```

**절차**:
1. `BackupValidator.Validate`로 검증한다. 실패하면 `ImportPreview.Failed(errors)`를 즉시 돌려주고
   그 외 아무것도 하지 않는다.
2. `BackupCatalogReader.Build`로 backup 쪽 lineage(`Chains`)를 재구성한다.
3. manifest의 모든 conversation(선택 + dependency-only)에 대해 `RevisionRelation`을 판정한다
   (로컬에 없으면 `New`, 있으면 양쪽 `ConversationRevision`을 만들어 비교, 어느 한쪽이라도
   Unverifiable이면 `Unverifiable`).
4. `MetadataDifferenceAnalyzer.Compare`로 metadata 차이를 계산한다.
5. `ImportConflictAnalyzer.Decide(relation)`으로 `PlannedAction`을 결정한다(§2 표와 동일한 매핑).
6. manifest의 각 project를 `ProjectPathMapper`로 매핑하고, 그 project의 **선택된** 대화만
   `ImportProjectPreview.Conversations`에 담는다.
7. **선택된 대화인데 어떤 project 그룹에도 속하지 않는 경우**(실제 UI 선택은 항상 "기타 대화"
   그룹을 거치므로 이론상 발생하지 않지만, 방어적으로) "기타 대화" fallback 그룹을 만들어 넣는다 —
   화면에서 조용히 사라지는 대화가 있으면 안 된다(실제 `.codex` 데이터 검증 중 발견 → 회귀 테스트로
   고정, §9 참고).

`ImportPreviewBuilder`는 core 조합만 한다 — ViewModel은 이 클래스만 부른다(요구사항 9).

---

## 9. 실제 데이터 검증 결과 요약

실제 `.codex`(357 thread, 46 프로젝트, rollout 371개)로:

- 실제 사용자 대화 40개(세그먼트 7개·분기 3개 포함)를 선택해 진짜 `.codexbackup`을 만들고, **같은
  카탈로그와 다시 비교 → 42건(선택 40 + dependency-only 2) 전부 `Identical`**, metadata 차이 0건.
  세그먼트/분기 실제 사례를 포함해도 예외나 `Unverifiable`이 발생하지 않았다.
- 실제 rollout 파일 하나를 복사해 앞부분만 자른 스냅샷으로 `IncomingAhead`/`LocalAhead`를,
  공통 앞부분 뒤에 합성 줄을 추가한 스냅샷으로 `Diverged`를 재현 — 전부 기대한 관계로 정확히
  판정됐다.
- 이 검증 과정에서 "선택된 대화가 project 그룹 없이 만들어지면 Preview에서 사라진다"는 방어
  로직의 허점을 실제로 발견했다(합성 테스트 설계 중 발견, 실제 프로덕션 경로에서는 발생 불가능한
  조건이지만 안전을 위해 고쳤다) — RED 재현 → fallback 그룹 추가 → GREEN, 회귀 테스트
  (`어떤_project에도_속하지_않은_선택_대화도_Preview에서_사라지지_않는다`)로 고정했다.
- 작업 전후 `state_5.sqlite`/`-wal`/`-shm`/`session_index.jsonl`/`.codex-global-state.json`/
  `config.toml` SHA-256이 전부 동일 — Import Preview 동안 Codex 원본에 어떤 write도 없었다.

**남은 한계**: 실제 데이터에는 진짜로 두 PC를 오간 `Diverged`/`LocalAhead` 사례가 없어 위 검증은
실제 파일을 복사·수정한 합성 스냅샷 기반이다. `.jsonl.zst` 비교도 합성 fixture로만 확인했다(이
PC에 실물 `.zst`가 0개, 기존 알려진 한계와 동일).
