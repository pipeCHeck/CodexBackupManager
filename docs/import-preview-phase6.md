# Phase 6 — Import Preview + Update/Divergence Analysis 스펙

이 문서는 Phase 6(`.codexbackup`을 선택했을 때 검증 → 내용 표시 → 현재 PC Codex와 비교 → 경로
재매핑 제안 → Phase 7 계획 Preview)의 정식 스펙이다. `docs/codexbackup-format-v1.md`가 "무엇을 어떤
포맷으로 백업하는가"를 다룬다면, 이 문서는 "그 backup을 다른 PC의 현재 상태와 어떻게 비교하고
보여주는가"를 다룬다.

> **상태: FROZEN (Phase 06_01 완료 기준).** Phase 6에서 처음 확정한 스펙을 재검토로 발견된 안전성
> 경계 케이스 문제(§4의 segment transition, §2의 New/Unverifiable 정책, §7 수동 경로 재지정, §8.1
> `ImportPlan` freeze 경계)에 맞춰 Phase 06_01에서 수정했다. Phase 7(Safe Restore/Apply)은 이 문서를
> 그대로 신뢰하고 시작해도 된다 — 특히 **`ImportPreview`가 아니라 `ImportPlan`을 입력으로 받을 것**
> (§8.1). 이후 변경이 필요하면 이 문서를 먼저 갱신할 것.

> **범위**: Phase 6/06_01은 판정과 미리보기(및 그 freeze)까지만 한다. Codex 원본에는 어떤 write도
> 하지 않는다(SQLite INSERT/UPDATE, rollout append/복사, global-state 변경, Snapshot/Rollback 전부
> Phase 7의 역할).

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
| `New` | backup ThreadId가 현재 로컬 Codex에 **정말로 전혀 없음**(chain도 metadata도 없음 — §2.1) | `Import` — 새로 Import |
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

### 2.1 New vs Unverifiable(Phase 06_01 정정)

**"New"는 반드시 chain과 metadata가 둘 다 없을 때만 판정한다.** 최초 구현은
`!localChains.ContainsKey(threadId)`만 보고 `New`로 판정했는데, 이러면 다음 상황을 오판한다: 로컬
state DB(`CodexCatalog.AllConversations` — 선택 대화든 dependency-only/internal thread든 전부
포함)에 같은 ThreadId 행이 이미 있는데, rollout 파일이 삭제됐거나 경로가 손상돼 `Chains`에서만
빠진 경우. 이건 "새 대화"가 아니라 "로컬 상태가 손상돼 안전하게 판정할 수 없는" 경우다 — `New`로
잘못 분류하면 Phase 7이 이미 존재하는 대화를 "새 Import"로 취급해 위험한 동작(예: 기존 metadata를
덮어쓰거나 중복 행을 만드는 시도)을 할 수 있다.

판정 순서(`ImportPreviewBuilder.DetermineRelation`):

```
existsInLocalCatalog = localCatalog.AllConversations에 이 ThreadId가 있는지

hasLocalChain=false, existsInLocalCatalog=false  → New
hasLocalChain=false, existsInLocalCatalog=true   → Unverifiable("metadata는 있지만 chain 없음")
hasLocalChain=true                                → 정상적으로 ConversationRevision을 만들어 비교
```

dependency-only(조상) thread도 같은 원칙을 그대로 적용한다 — `AllConversations`는 선택 대화와
dependency-only thread를 구분하지 않고 전부 담고 있으므로 별도 분기가 필요 없다.

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
3. `RolloutId`가 같은데 길이/해시가 다르면 — **각 쪽이 "이 위치에서 끝나는지"를 독립적으로 본다**
   (Phase 06_01 정정, §4.0 참고):
   - **양쪽 다 여기서 끝나면**(`i`가 둘 다의 마지막 인덱스) → §4.1로(같은 파일이 한쪽에서 더 길게
     이어써졌을 가능성).
   - **한쪽만 여기서 끝나고 다른 쪽은 계속되면**(뒤에 segment가 더 있음) → §4.1과 같은 prefix
     검증을 하되, "끝난 쪽"이 "계속되는 쪽"의 진짜 byte prefix일 때만 계속되는 쪽이 앞선 것으로
     인정한다(§4.0).
   - **양쪽 다 여기서 끝나지 않는데(둘 다 뒤에 더 있는데) 이미 다르면** → **Diverged**(이 leaf
     기준으로 정상적인 fast-forward 시나리오로 설명할 수 없다 — 안전하게 발산 취급).

공통 구간이 전부 일치했는데 슬라이스 개수가 다르면(한쪽에만 뒤이은 segment가 있음) →
개수가 더 많은 쪽이 **더 진행된 쪽**(`IncomingAhead`/`LocalAhead`).

### 4.0 Segment transition — 한쪽만 끝나고 다른 쪽은 계속돼도 fast-forward로 인정한다(Phase 06_01)

최초 구현은 "양쪽 다 마지막 슬라이스일 때만" prefix 검사를 했다. 이러면 다음과 같은 정상 시나리오를
오판했다:

```
Local    = [R1-short]
Incoming = [R1-long, R2]
```

로컬은 R1이 아직 "A B"였을 때의 과거 snapshot이고, incoming에서는 그 뒤 R1에 "C D"가 이어써진
다음 새 segment R2까지 생긴 상태다. R1은 로컬의 마지막 슬라이스지만 incoming의 마지막 슬라이스가
아니므로(뒤에 R2가 더 있다), 예전 구현은 이 지점을 무조건 **Diverged**로 취급했다. 하지만 이건
명백한 **IncomingAhead**여야 한다.

**수정 원칙**: 같은 RolloutId에서 내용이 다를 때, 한쪽 revision이 바로 그 slice에서 끝나고 다른
쪽은 동일 slice가 더 길거나 그 뒤 추가 segment까지 있으며, **짧은 쪽 slice가 긴 쪽 slice의 정확한
logical byte prefix이면**, 뒤에 segment가 더 존재하더라도 정상 fast-forward로 인정한다:

```
Local    = [R1-short]
Incoming = [R1-long, R2, R3]     ← R1-short가 R1-long의 정확한 prefix면 → IncomingAhead
                                    (R2/R3가 몇 개 더 있어도 상관없다)

Local    = [R1-long, R2]
Incoming = [R1-short]            ← 반대 방향 → LocalAhead
```

단, **양쪽 모두 그 slice 뒤에도 계속되는 경우**는 여전히 안전하게 Diverged로 유지한다:

```
Local    = [R1-local, R2...]
Incoming = [R1-incoming, R2...]  ← 둘 다 R1 뒤에 더 있는데 R1부터 이미 다르다 → Diverged
```

길이만 보고 판단하지 않는다 — §4.1과 동일하게 실제 prefix hash/byte 검증을 항상 거친다.

**실측으로 확인된 함정**: 이 원칙을 실제 3-segment thread(21MB, segment1 974줄)로 검증하는 과정에서,
**segment1 원본 rollout 파일이 이 체인이 공식적으로 인정하는 cutoff(segment2의 history_base,
`ordinal<967`) 이후에도 별도로 계속 쓰인 바이트(967~973번째 줄)를 갖고 있을 수 있다**는 사실을
처음 확인했다. 이 상태에서 segment1 **원본 파일 전체**를 "로컬의 과거 snapshot"으로 쓰면, 로컬이
공식 cutoff보다 더 많은 바이트를 갖게 되어 §4.1의 prefix 검증에 실패하고 **정당하게 Diverged**로
판정된다(끝난 쪽이 계속되는 쪽보다 같거나 더 길면 안전하게 발산 취급 — §4.1의 "shorterCandidate ≥
longerCandidate" 가드). cutoff까지만 정확히 자른 스냅샷을 쓰면 `IncomingAhead`가 올바르게
재현된다. 즉 **"원본 rollout 파일 전체" ≠ "이 thread 체인이 실제로 인정하는 논리적 범위"**라는
원칙이 여기서도 그대로 적용된다 — Restore/검증 로직에서 파일 전체 길이를 그 thread의 전체 내용으로
가정하지 말 것.

### 4.1 "같은 파일, 다른 길이" — 실제로 byte prefix인지 다시 읽어서 확인한다

두 슬라이스의 **전체 해시만** 비교해서는 "한쪽이 다른 쪽의 진짜 이어쓰기"인지 "우연히 길이가 다른
순수 발산"인지 구분할 수 없다(다른 두 해시로부터 prefix 관계를 대수적으로 유도할 수 없다). 그래서:

1. 길이가 같은데 해시가 다르면 → 무조건 **Diverged**(append로 설명 불가).
2. "끝난" 쪽이 "계속되는" 쪽과 같거나 더 길면 → 무조건 **Diverged**(끝난 쪽이 더 많은/같은 바이트를
   갖고 있는데 상대는 별도로 계속됐다는 뜻 — 정상적인 이어쓰기로 설명할 수 없다).
3. "끝난" 쪽이 더 짧으면, **"계속되는" 쪽을 "끝난" 쪽의 길이만큼 다시 잘라 재해시**한다
   (`IRolloutSliceReader`로 해당 파일을 byte offset 컷오프로 다시 읽는다).
4. 재해시 결과가 "끝난" 쪽과 **길이+해시 전부 정확히 일치**해야만 진짜 prefix로 인정하고
   `IncomingAhead`(backup 쪽이 계속됨) 또는 `LocalAhead`(로컬 쪽이 계속됨)로 판정한다.
5. 일치하지 않으면 → **Diverged**(길이만 다르고 내용은 처음부터 갈라진 경우).

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
  ManuallyLinked  // (Phase 06_01) 사용자가 직접 폴더를 선택해 재지정함
```

backup manifest의 `project.originalRootPaths`를 현재 로컬 카탈로그(`CodexCatalog.Projects`)의 각
프로젝트 `RootPaths`와 `CanonicalPath.AreSameLocation`(대소문자/`\\?\` prefix 무시)으로 비교해
자동 연결을 제안한다. **자동 판정 자체는 Codex에 쓰지 않는다.** 프로젝트가 현재 컴퓨터에 없어도
(경로를 못 찾아도) conversation 자체의 Import 여부는 이 매핑과 무관하다(기존 설계 원칙 유지).

### 7.1 수동 경로 재지정(Phase 06_01)

Preview 단계에서 사용자가 프로젝트 경로를 직접 재지정할 수 있다 — Phase 7이 실제 Apply를 하기
전에 경로 결정을 미리 끝내 두기 위해서다.

```
ProjectPathMapping.CanManuallyOverride  // Status != NotApplicable("기타 대화"만 제외)
ProjectPathMapping.WithManualOverride(resolvedLocalPath)
  → Status를 ManuallyLinked로, ResolvedLocalPath를 새 값으로, LinkedLocalProjectId를 null로 바꾼
    새 레코드를 돌려준다(순수 데이터, 파일 시스템 접근 없음). "기타 대화"에 부르면
    InvalidOperationException.

ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, projectId, userSelectedPath)
  → 1. preview.Success가 아니면 InvalidOperationException
    2. Directory.Exists(userSelectedPath)가 아니면 DirectoryNotFoundException
    3. CanonicalPath.Create(userSelectedPath)로 정규화
    4. projectId와 일치하는 프로젝트를 못 찾으면 ArgumentException
    5. 그 프로젝트의 PathMapping만 WithManualOverride로 교체한 새 ImportPreview를 돌려준다
       (다른 프로젝트/대화의 Relation·PlannedAction은 그대로 — 경로 재매핑은 revision 판정과
       무관하므로 다시 계산하지 않는다)
```

**`NotFound`뿐 아니라 `AutoLinked`도 사용자가 원하면 덮어쓸 수 있다** — 자동 연결이 틀렸거나 다른
경로를 쓰고 싶을 수 있기 때문이다. `NotApplicable`("기타 대화")에는 경로 선택 자체를 요구하지
않는다(버튼을 아예 보여주지 않는다).

**override 상태는 View code-behind가 아니라 이 메서드(그리고 `ProjectPathMapping` 자체)에만
저장된다.** `MainViewModel`은 `_lastImportPreviewDomain`(원본 `ImportPreview`)을 들고 있다가,
사용자가 폴더를 고르면 이 메서드로 새 `ImportPreview`를 만들어 그 필드를 교체할 뿐이다 — override
상태 자체를 별도로 기억하는 로직이 ViewModel/View에 없다. 그래서 Phase 7은 이 필드가 반영된
`ImportPlan`(§8.1)만 받으면 되고, UI가 무엇을 눌렀는지 다시 알아낼 필요가 없다.

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

### 8.1 `ImportPreview` → `ImportPlan` freeze 경계(Phase 06_01)

Phase 7이 UI 트리(`ImportPreviewViewModel`/`ProjectNodes` 등)를 다시 해석하지 않고 그대로 받아
적용할 수 있도록, `Backup.Import.ImportPlanBuilder`가 `ImportPreview`를 `ImportPlan`으로 freeze한다:

```
ImportPlanBuilder.Build(preview, backupFilePath) → ImportPlan?     // preview.Success가 false면 null

ImportPlan
  Backup: ImportBackupIdentity(BackupFilePath, CreatedAtUtc, AppVersion, TotalConversationCount)
  Projects: ImportPlanProject[](ProjectId, DisplayName, PathStatus, TargetProjectPath)
  Conversations: ImportPlanConversation[](ThreadId, IsSelected, Relation, PlannedAction, TargetProjectPath)
                 // 선택 대화 + dependency-only 전부(후자는 TargetProjectPath=null)
  HasBlockingIssues       // Blocked(Unverifiable)가 하나라도 있으면 항상 참 — 예외 없음
  HasUnresolvedDivergence // RequiresDecision(Diverged)이 하나라도 있으면 참
  IsApplyReady            // !HasBlockingIssues && !HasUnresolvedDivergence
```

**`IsApplyReady`가 거짓이어도 `ImportPlan` 자체는 만들어진다** — "Preview는 가능하지만 지금 당장
전부 적용하기엔 안전하지 않다"는 상태를 표현하기 위해서다(Diverged가 하나라도 있으면 Preview는
계속 볼 수 있어야 하지만, 사용자 결정 없이 그대로 적용해서는 안 된다). **`Unverifiable`은 항상
blocking이다** — 예외적으로 무시하고 진행하는 경로는 없다.

Phase 7은 이 `ImportPlan`을 입력으로 받아야 한다 — `ImportPreview`나 `ImportPreviewViewModel`을
다시 읽어 자기만의 판단을 내리지 말 것. `ImportPlanConversation.PlannedAction`이 이미 확정된
계획이고, `TargetProjectPath`에 자동 연결/수동 재지정 결과가 이미 반영돼 있다.

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

### 9.1 Phase 06_01 재검증

- 위 검증(40개 선택, 세그먼트 7·분기 3 포함)을 Phase 06_01 코드로 다시 실행 — **결과 완전히
  동일**(42건 전부 `Identical`, metadata 차이 0건), 회귀 없음을 확인했다.
- **실제 3-segment thread(segment1 21MB/974줄)로 segment transition IncomingAhead를 재현**했다.
  처음에는 segment1 원본 파일을 그대로 "로컬의 과거 snapshot"으로 썼더니 `Diverged`가 나왔다 —
  조사 결과 segment1 파일이 이 체인의 공식 cutoff(`ordinal<967`) 이후에도 7줄이 더 있었기 때문이었다
  (§4.0 참고, 실측으로 새로 확인한 사실). segment2의 실제 history_base cutoff까지만 정확히 자른
  스냅샷으로 다시 시도하니 `IncomingAhead`가 정확히 재현됐다.
- 로컬에 metadata는 있지만 chain이 없는 상태(§2.1)는 합성 fixture로 RED→GREEN 확인했다(이 PC의
  실제 `.codex`에는 이런 손상 상태가 존재하지 않는다 — 존재해서도 안 된다).
- 실제 fixture Codex Home(`tests/Fixtures/CodexHome`)의 진짜 프로젝트("Alpha")를 대상으로 App
  레이어(`MainViewModel`)에서 폴더 선택 → `ManuallyLinked` 재지정까지 end-to-end로 확인했다.
- 작업 전후 source-of-truth 해시 6개 전부 동일(Phase 6 검증 때와 값도 동일 — 이 세션에서 `.codex`에
  어떤 것도 쓰지 않았다는 뜻).

**남은 한계**: 실제 데이터에는 진짜로 두 PC를 오간 `Diverged`/`LocalAhead` 사례가 없어 위 검증은
실제 파일을 복사·수정한 합성 스냅샷 기반이다. `.jsonl.zst` 비교도 합성 fixture로만 확인했다(이
PC에 실물 `.zst`가 0개, 기존 알려진 한계와 동일). 3단계 이상 분기(조상의 조상)도 실제 사례가 없다.
