# 프로젝트 현재 상태 및 인계 노트

이 문서는 **세션이 압축되거나 새로 시작되어도 작업을 정확히 이어갈 수 있도록** 지금까지 확정된 것,
남은 것, 주의할 것을 정리한다. 새 세션은 `CLAUDE.md` 다음, 다른 어떤 코드를 읽기 전에 이 문서부터
읽는다(§ "구현 시 참조 순서" 갱신 참고).

마지막 갱신 기준: `Phase 06` 커밋(`52a4a8b`)까지 완료. 그 위에 GitHub 코드 리뷰/재검토로 발견된 Phase 6
정합성 문제를 고치는 **Phase 06_01(Revision Relation Hardening / Import Plan Finalization)을
완료**했다. Backup Format V1은 `Phase 05_01`(`be2f616`)을 기준으로 **FROZEN**이다
(`docs/codexbackup-format-v1.md` 상단 배너 참고). **Import Preview(Phase 6)는 Phase 06_01 검증까지
전부 PASS해야 최종 FREEZE로 본다** — `docs/import-preview-phase6.md` 상단 배너 참고. `.codexbackup`을
검증하고 현재 PC Codex와 실제 rollout lineage/내용으로 비교해 `New`/`Identical`/`IncomingAhead`/
`LocalAhead`/`Diverged`/`Unverifiable`를 판정하고, Phase 7이 그대로 받아 쓸 수 있는 `ImportPlan`으로
freeze하는 것까지 하지만, **Codex에는 여전히 아무것도 쓰지 않는다**(Apply는 Phase 7).
**Phase 06_01 작업은 아직 사용자가 커밋하지 않은 상태**(작업 트리에 변경 있음) — 새 세션은
`git log`/`git status`로 실제 커밋 여부를 다시 확인할 것.

---

## 1. 한 줄 요약

**Phase 1~5(Codex 탐색 → Read Model → Conversation Viewer → Selection → Export)까지 전부
완료했고, 그 위에 Phase 05_01(Backup V1 Freeze), Phase 6(Import Preview), Phase 06_01(Revision
Relation Hardening / Import Plan Finalization)까지 마쳤다.**
Phase 4는 04_01~04_07 사후 수정을 거쳐 사용자가 실제 GUI로 확인 후 최종 PASS로 확정했다. Phase 5는
Restore Sufficiency Audit → Backup Format V1 확정 → `CodexBackupManager.Backup` 프로젝트
(ExportPlanBuilder/BackupWriter/BackupReader/BackupValidator) → 최소 Export UI → 실제 `.codex`
데이터 검증까지 마쳤고, 사용자가 커밋한 뒤 GitHub 코드 리뷰에서 Restore/Validator 정합성 문제
몇 가지가 발견돼 Phase 05_01에서 고쳤다(§3 Export 항목, `docs/codexbackup-format-v1.md` §5에
전체 목록) — 이 커밋(`be2f616`)을 기준으로 Backup Format V1이 FROZEN이다.
**Phase 6은 `.codexbackup`을 선택하면 검증 → 내용 표시 → 현재 PC Codex와 실제 rollout
lineage/내용 비교(timestamp 아님) → 프로젝트 경로 재매핑 제안 → Phase 7 계획(Preview)까지만 한다.
Codex에는 write가 0건이다**(§3 "Import Preview" 항목, `docs/import-preview-phase6.md`에 전체 스펙).
Phase 6은 커밋(`52a4a8b`)됐지만, 그 뒤 GitHub 코드 리뷰/재검토로 발견된 **revision relation 판정의
안전성 경계 케이스 문제**를 **Phase 06_01**에서 고쳤다 — segment transition fast-forward 오판,
로컬 metadata가 있는데 chain만 없을 때 `New`로 잘못 떨어지던 문제, 수동 프로젝트 경로 재지정,
`ImportPreview` → `ImportPlan`(Phase 7이 그대로 받아 쓸 freeze 경계) 확정을 포함한다(§3 "Import
Preview" 항목에 상세, `docs/import-preview-phase6.md`에 전체 스펙 — **Import Preview는 Phase 06_01
검증까지 마쳐야 최종 FREEZE**). **Apply/Restore(Phase 7)는 아직 손대지 않았다** — 다음에 할 일이
명시적으로 주어지지 않으면 Phase 7을 추측해서 미리 시작하지 말 것.

---

## 2. Phase별 완료 내역과 커밋 매핑

| 커밋 | Phase | 내용 |
|---|---|---|
| `2dbcbd0` | Phase 1 | Codex Home 자동 탐색(`CODEX_HOME` → `%USERPROFILE%\.codex` → 저장된 경로 → 수동 지정), 검증, 설정 저장 |
| `48fb11d` / `932f0cc` | Phase 2 / 02_01 | rollout 파서, thread 체인(세그먼트/분기), 프로젝트/제목 해결, 카탈로그(46 프로젝트/112 사용자 대화 실측) |
| `8503885` / `643e79f` / `a0e6ce6` | Phase 3 / 03_01 / 03_02 | Conversation Viewer: User/Assistant 메시지 파싱, `event_msg`/`response_item` authoritative-source 정책, 내부 주입 콘텐츠 필터(공식 마커 쌍 매칭), assistant 메시지엔 필터 미적용 |
| `494ae8b` | Phase 4 | Selection: 체크박스 선택(프로젝트 3상태/대화), `ConversationSelectionState`(Domain, WPF 비의존), Viewer 포커스와 완전 분리 |
| `5ad6f4e` | Phase 04_01 | 대량 선택(전체 선택/해제) 시 중앙 상태를 한 번만 바꾸도록 수정(이전엔 프로젝트 수만큼 `Changed` 발생) |
| `6b4a2e5` | Phase 04_02 | UI/UX 1차 개선: 버튼 대비(`ControlTemplate`), 트리 레이아웃 확장+`GridSplitter`+말줄임/ToolTip, Markdown-lite 렌더러 도입(제목/목록/코드블록/인라인 코드) + `RichTextBox`+`FlowDocumentBinding`, `ScrollUnit="Pixel"` |
| `b3a9628` | Phase 04_03 | `FlowDocument`를 `Lazy<T>`로 지연 생성(성능) — Blocks(순수 데이터)는 즉시 파싱, `FlowDocument`는 최초 바인딩 시점에만 생성 |
| `b4b4bca` | Phase 04_04 | **스크롤 반복 크래시 root cause 수정**: `FlowDocumentBinding`이 재사용되는 `FlowDocument`를 재활용된 `RichTextBox`에 재대입할 때 이전 소유자로부터 먼저 떼어내지 않아 `ArgumentException` 발생 → 대입 전 강제 detach로 수정. `CrashDiagnostics`(미처리 예외 로깅) 추가. 영구 회귀 테스트(`FlowDocumentBindingRecyclingStressTests`, 실제 STA+가상화 스트레스) 추가 |
| `0591945` | Phase 04_05 | Viewer Fidelity: 빈 User 메시지 정책(공식 Codex와 동일하게 `IsNullOrWhiteSpace`면 숨김, 비텍스트 콘텐츠는 placeholder), Markdown-lite에 굵게/기울임/링크/인용문 추가, Codex 스타일 레이아웃(Assistant 평문형/User 작은 말풍선, 라벨 제거), 타이포그래피 조정 |
| `d6e2521` | Phase 04_06 | Assistant 메시지를 완전 평면에서 "은은한 카드"로 되돌림(`PanelAlt` 배경, `CornerRadius=9`, `Padding=16,14`, Border 없음), User 말풍선에 오른쪽 여백(`Margin=0,2,24,2`) 추가, `App.xaml`에 재사용 가능한 다크 `ScrollBar` 암시적 스타일 추가(화살표 숨김/얇은 Thumb/hover·pressed 밝기), Markdown-lite `_`/`__` intraword 오탐 수정(`snake_case_name` 같은 식별자가 더 이상 기울임/굵게로 오인식되지 않음, `*`/`**`는 기존 정책 유지) |
| `e599d9e` | Phase 04_07 | **Phase 4의 마지막 UI 수정.** 상단 진단 영역을 Compact/Expand 구조로 재구성(기본 접힘, `MainViewModel.CompactSummaryText`로 Desktop/CLI/Sessions/Threads 한 줄 요약, "상세 정보" `ToggleButton`으로 기존 Rows/ProbeRows 펼침/접힘, `MaxHeight`+내부 `ScrollViewer`로 창 전체를 다시 잡아먹지 않게 제한), 본문 Grid의 고정 `RowDefinition Height="220"` 제거(→ `Auto`, 남는 공간은 프로젝트/Viewer 영역이 가져감), `App.xaml` 다크 `ScrollBar`의 horizontal 정합성 수정(`Track.Orientation`이 `ScrollBar.Orientation`을 실제로 따라가도록 `TemplateBinding` 추가 — 이전엔 안 따라갔다, PageUp/PageDown↔PageLeft/PageRight 커맨드 분리) |

| `472f2aa` | Phase 5 | **Export.** Restore Sufficiency Audit(실제 `state_5.sqlite.threads` 38컬럼 전수 실측 + 공식 Codex Rust 소스 대조 → `ThreadRow`를 38컬럼 보존하도록 확장 — 이때는 아직 `source` 컬럼이 누락된 상태였다, 아래 Phase 05_01 참고), `ThreadDependencyResolver` 추출(Viewer의 `ConversationTranscriptBuilder`와 Export가 동일한 dependency closure 규칙 공유), `CodexBackupManager.Backup`/`.Backup.Tests` 프로젝트 신설(`ExportPlanBuilder`/`ManifestBuilder`/`ChecksumService`/`BackupWriter`/`BackupReader`/`BackupValidator`), `.codexbackup` V1 포맷 확정(`docs/codexbackup-format-v1.md`), 스트리밍 복사+incremental SHA-256(대형 rollout도 메모리 비례 증가 없음), atomic publish(temp→self-validate→move), Export 도중 원본 변경 감지, `LocalImageAttachmentScanner`(non-text content 첨부 정책), 최소 Export UI(`MainViewModel.ExportCommand`, `SaveFileDialog`) |
| `be2f616` | Phase 05_01 | **Backup V1 Freeze / Restore Sufficiency Hardening.** GitHub 코드 리뷰로 발견된 Phase 5 정합성 문제 10건 수정: (1) `threads.source`(NOT NULL) 컬럼이 `ThreadRow`/`ThreadRowReader`에서 누락돼 있던 것을 발견·수정 + 38컬럼 전체를 실제 스키마와 기계적으로 대조하는 회귀 테스트(`ThreadRowSchemaCoverageTests`/`BackupConversationMetadataCoverageTests`) 추가, (2) `resolvedTitle` 등 가공값이 원본 `title`/`name`/`firstUserMessage`/`preview`/`rollout_path` 등을 대체하던 것을 원본 필드 전부 별도 보존으로 수정, (3) 선택 대화의 chain/metadata/ancestor 누락을 warning으로 넘기던 것을 `ExportPlan.FatalErrors`로 승격해 Export 전체 FAIL(temp도 안 만듦), (4) 첨부 원본 경로 ↔ entry 경로 역매핑(`manifest.attachments[]`/`BackupAttachmentMetadata`) 신설, (5) `LocalImageAttachmentScanner`가 `ConversationItemParser`와 동일한 file-level authoritative-source 정책(event_msg 있으면 그것만, 없을 때만 response_item)을 쓰도록 수정, (6)~(9) `BackupValidator` hardening(malformed 입력에서 예외 대신 항상 Fail 반환, Windows 대소문자 충돌 검출, manifest 내부 개수/참조 일관성 검사, `manifest.json`도 체크섬 보호 대상에 포함 — 순환 아님을 재확인), (10) `CancelExportCommand` + 조건부 취소 버튼 추가(300MB급 실제 mid-copy 취소 확인) |
| `52a4a8b` | Phase 6 | **Import Preview + Update/Divergence Analysis.** `.codexbackup`을 선택하면 `BackupValidator`로 검증 → 현재 PC Codex와 실제 rollout lineage/내용을 비교해 `RevisionRelation`(New/Identical/IncomingAhead/LocalAhead/Diverged/Unverifiable)을 판정 → 프로젝트 경로 재매핑 제안 → Phase 7 계획(Preview)까지만 한다. **timestamp로 판정하지 않는다** — `ConversationRevision`/`RolloutSlice`(논리적 슬라이스 fingerprint, 압축 해제 후 바이트+SHA-256)로 실제 내용을 비교한다. 새 모델(`Domain.Codex.Import`: `RolloutSlice`/`ConversationRevision`/`RevisionRelation`/`MetadataDifferences`/`ProjectPathMapping`), `ConversationRevisionBuilder`/`ConversationRevisionComparer`(Codex 프로젝트, `IRolloutSliceReader`로 로컬 파일/backup ZIP entry를 추상화해 같은 코드 공유), `ThreadDependencyResolver.ResolveFileSlices` 신설(Viewer의 `ConversationTranscriptBuilder`도 이걸 쓰도록 리팩터링 — 파일별 cutoff 판단이 drift하지 않게), `BackupCatalogReader`(backup의 `payload/rollouts/`만으로 로컬과 같은 `ThreadChainResolver` lineage를 재구성 — manifest에 별도 lineage 필드를 추가하지 않았다), `ImportPreviewBuilder`/`ImportConflictAnalyzer`/`MetadataDifferenceAnalyzer`/`ProjectPathMapper`(Backup 프로젝트), 최소 Import Preview UI(`MainViewModel.ImportPreviewCommand`, `OpenFileDialog`). Codex에는 write가 0건이다. |
| (미커밋) | Phase 06_01 | **Revision Relation Hardening / Import Plan Finalization.** GitHub 코드 리뷰/재검토로 발견된 Phase 6 안전성 경계 케이스 문제 수정: (1) `ConversationRevisionComparer`가 "같은 rollout id, 다른 slice 내용"을 **양쪽 다 마지막 slice일 때만** prefix 검사하던 것을 **각 쪽이 독립적으로 "여기서 끝나는지"** 보도록 수정 — `Local=[R1-short]`, `Incoming=[R1-long, R2]`처럼 한쪽만 끝나고 다른 쪽이 이어지는(뒤에 segment가 더 있어도) 정상적인 fast-forward를 예전엔 Diverged로 오판했다, (2) `ImportPreviewBuilder.DetermineRelation`이 로컬에 chain만 없으면 무조건 `New`로 판정하던 것을 — 로컬 catalog(`AllConversations`, dependency-only 포함)에 같은 ThreadId metadata가 있는데 chain만 없으면(rollout 삭제/손상) `Unverifiable`(Blocked)로 승격, 정말로 metadata도 chain도 전혀 없을 때만 `New`, (3) `ProjectPathMapping`에 `ManuallyLinked` 상태 + `WithManualOverride` 추가, `ImportPreviewBuilder.ApplyManualProjectPathOverride`로 NotFound/AutoLinked 프로젝트 모두 사용자가 폴더를 직접 재지정할 수 있게(미분류 "기타 대화"만 제외) — 실제 디렉터리 존재 확인 + `CanonicalPath` 정규화, 상태는 View code-behind가 아니라 `ImportPreview`/`ProjectPathMapping` 자체에 저장, (4) `ImportPlan`/`ImportPlanBuilder` 신설 — Preview를 Phase 7이 그대로 받아 적용할 수 있는 **freeze 경계**로 확정(`IsApplyReady`는 Blocked/RequiresDecision이 하나도 없어야 참). 실제 세그먼트 thread(21MB, 974줄, 3세그먼트)로 재검증하는 과정에서 "segment1 원본 파일이 공식 인정된 lineage cutoff(ordinal<967) 이후에도 별도로 계속 쓰인 바이트(줄 967~973)를 갖고 있을 수 있다"는 새 실측 사실을 발견 — 이 경우 그 cutoff 이후 내용까지 포함한 스냅샷은 fast-forward가 아니라 정당하게 Diverged로 판정돼야 하며, 실제로 그렇게 동작함을 확인했다(§4-13 참고). |

**Phase 4는 사용자가 실제 GUI로 확인 후 최종 PASS로 확정했다. Phase 5(Export)는 커밋된 뒤 Phase 05_01
hardening까지 마쳤다 — `docs/codexbackup-format-v1.md`가 이제 FROZEN 상태다. Phase 6(Import
Preview)은 커밋됐고, 그 위에 Phase 06_01 hardening까지 마쳤다 — `docs/import-preview-phase6.md`가
이제 FROZEN 상태다(Phase 06_01 완료 기준).** Phase 7(Safe Restore)은 아직 시작 전이다.

---

## 3. 확정된 핵심 설계 결정 (재확인 없이 신뢰해도 되는 것)

### 데이터 계층
- `history_base.thread_id`는 **rollout ID**이지 안정적인 thread ID가 아니다(공식 `HistoryPosition` 구조체로 확인). `RolloutFileReference.OwnRolloutId`(`SegmentId ?? ThreadId`)로 색인한다.
- 파일 단위 authoritative-source 정책: 한 rollout 파일에 `event_msg`/`item_completed`가 하나라도 있으면 그것만 쓰고 같은 파일의 `response_item`은 전부 버린다. 없을 때만 `response_item` 폴백.
- 내부 주입 콘텐츠 필터(`ConversationItemParser.IsInjectedContent`)는 확인된 5개 마커(`<app-context>`, `<recommended_plugins>`, `<turn_aborted>`, `<multi_agent_mode>`, `<environment_context>`)의 **시작+종료 태그 쌍이 정확히 일치할 때만** 숨긴다(공식 Rust `matches_marked_text`와 동일). 마커 비교는 **대소문자 무시**(공식 소스가 `eq_ignore_ascii_case`를 씀 — 확인됨, 대소문자 구분으로 바꾸지 말 것). 판정은 `content` 배열 원소 단위(한 메시지에 여러 마커가 섞여 들어오는 실측 사례 있음). "소문자 태그로 시작" 같은 구조 규칙은 쓰지 않는다(과거 이 방식이 `<code>`/`<summary>`/`<xml>` 같은 정상 사용자 메시지를 오탐으로 지운 적 있음).
- 이 필터는 `role="user"`에만 적용한다. `role="assistant"`는 확인된 마커를 만들어낼 수 없는 role(공식 소스에서 assistant fragment는 빈 마커 사용)이라 필터 미적용.
- 빈 메시지 정책(Phase 04_05, 공식 Codex TUI 소스로 확인): 텍스트가 `IsNullOrWhiteSpace`면 숨김. 텍스트가 없어도 `content` 원소에 **"text" 키 자체가 없으면**(이미지 등) `"[이미지]"`/`"[음성]"`/`"[첨부 파일]"` placeholder 표시. **"text" 키는 있지만 값이 빈 문자열인 경우는 첨부가 아니라 그냥 빈 텍스트로 취급**(실측으로 발견한 버그, 이미 수정됨 — 키의 유무와 값의 빈 여부를 절대 혼동하지 말 것). zero-width/control 문자에 대한 별도 필터는 없다(공식 소스도 표준 trim만 사용, 실측에도 그런 사례 없음 — 과설계 금지).
- `AssistantPhase`는 `commentary`/`final`만 매핑한다. 실측에서 `"final_answer"`라는 **미매핑 phase 값**을 하나 발견했다(현재는 조용히 `null`로 떨어짐) — Phase 5 이전에 굳이 다룰 필요는 없지만, 나중에 phase 관련 작업을 할 때는 이 값의 존재를 기억할 것.

### Selection (Phase 4)
- `ConversationSelectionState`(`Domain.Codex.Selection`, WPF 비의존, `HashSet<string>` 기반)가 선택의 유일한 source of truth. ThreadId 기준.
- Viewer 포커스(`MainViewModel.SelectConversation`/TreeView `SelectedItem`)와 백업 선택(체크박스)은 **완전히 분리된 별개 상태** — 어느 쪽도 서로 참조하지 않는다. 이 분리를 깨는 변경은 절대 하지 말 것.
- 대량 선택(전체 선택/프로젝트 선택)은 중앙 상태를 **한 번만** 변경하고 화면 갱신 알림만 항목마다 한 번씩 보낸다(수천 개에서도 안전).
- 카탈로그 refresh(같은 Codex Home 재확인) 시 사라진 ThreadId만 선택에서 제거, 존재하는 건 유지. 다른 Codex Home으로 바뀌면 전체 초기화.
- `MainViewModel.GetSelectedThreadIdsSnapshot()`이 Phase 5(Export)가 쓸 불변 스냅샷 API다.

### Conversation Viewer 렌더링 (Phase 4_01~04_05)
- `ConversationMessageViewModel.Body`(`FlowDocument`)는 **`Lazy<T>`로 지연 생성**한다 — 생성자에서 만들지 않는다. `Blocks`(Markdown-lite 파싱 결과, 순수 데이터)는 즉시 만들어도 된다(WPF 비의존이라 background thread에서 만들어도 안전). **`FlowDocument` 같은 WPF 객체는 절대 worker thread에서 만들지 말 것** — 항상 UI 스레드에서 바인딩 시점에 지연 생성돼야 한다.
- `FlowDocumentBinding`(`RichTextBox.Document` 첨부 속성)은 대입 전에 그 문서가 **다른 살아있는 RichTextBox의 자식이면 먼저 강제로 떼어낸다**. 이 로직을 제거하거나 단순화하면 스크롤 시 재현되는 `ArgumentException` 크래시가 되돌아온다 — `FlowDocumentBindingRecyclingStressTests`가 이걸 지킨다.
- Markdown-lite(`CodexBackupManager.App.Rendering`)는 **subset 파서**다: 문단/줄바꿈/제목(`#`~`######`)/번호·불릿 목록/인용문(`>`)/펜스 코드블록/인라인 코드/굵게(`**`/`__`)/기울임(`*`/`_`)/링크(`[text](url)`, 실제 네비게이션은 안 함 — 색+밑줄+ToolTip만). 외부 markdown 라이브러리(Markdig 등)는 검토 후 도입하지 않기로 결정했다(NuGet 최소화 원칙 + 어차피 FlowDocument 변환 계층은 직접 짜야 함). 완전한 CommonMark로 확장하려 하지 말 것 — "subset" 범위를 유지하는 게 명시적 지시다.
  - **(Phase 04_06)** `_`/`__` delimiter는 CommonMark와 같은 방식으로 intraword 오탐을 막는다 — delimiter 바로 바깥쪽이 영문/숫자/밑줄(`[A-Za-z0-9_]`)이면 emphasis로 인정하지 않는다(`(?<![A-Za-z0-9_])`/`(?![A-Za-z0-9_])` lookaround). 이 덕분에 `snake_case_name`, `SOME_CONSTANT_NAME`, `foo__bar__baz` 같은 코드 식별자가 더 이상 기울임/굵게로 오인식되지 않는다. `*`/`**`는 이 제약이 없다(기존처럼 "여는/닫는 기호 바로 안쪽이 공백이면 제외" 정책만 유지) — 코드에서 `*`가 식별자에 그대로 붙어 쓰이는 경우가 드물기 때문이다.
- Codex 스타일 레이아웃: Assistant는 **완전 평면도, 무거운 채팅 말풍선도 아닌 "은은한 문서 카드"**(`PanelAlt` 배경, `CornerRadius=9`, `Padding=16,14`, Border 없음, 왼쪽 정렬, 최대 폭 760). User는 작은 말풍선(오른쪽 정렬, 최대 폭 520, 내용 크기만큼만, 오른쪽 여백 `Margin=0,2,24,2`로 창 벽에 붙지 않게 함). `User`/`Assistant`/`commentary`/`final` 라벨은 UI에서 제거했다(Domain에는 Phase 값 그대로 보존). **Assistant를 다시 큰 말풍선으로 되돌리거나, 카드를 완전 평면으로 되돌리는 변경은 이미 두 번 되돌린 결정이니 재요청 없이 임의로 바꾸지 말 것.**
- `VirtualizingPanel.ScrollUnit="Pixel"`로 스크롤이 항목 단위가 아니라 픽셀 단위로 부드럽게 움직인다 — 이걸 되돌리면 "스크롤이 딱딱하다"는 문제가 재발한다.
- **(Phase 04_06)** `App.xaml`에 `ScrollBar`용 암시적(키 없는) 다크 스타일이 있다 — 화살표 버튼은 `Opacity=0`(클릭은 그대로 동작), Track은 투명, Thumb만 얇게(9px) 보이고 hover/드래그 시 밝아진다. 앱의 모든 `ScrollViewer`/`ScrollBar`에 자동 적용되므로 새 화면을 추가할 때 별도 스타일링이 필요 없다. 스크롤 동작(가상화/Pixel 단위/재활용) 자체는 건드리지 않는 순수 `ControlTemplate` 교체다.

### 상단 진단 영역 (Phase 04_07)
- 상단 진단 정보(State DB/Migration/Archived/탐색 후보 등, `MainViewModel.Rows`/`ProbeRows`)는 문제가 생겼을 때만 보면 되는 정보라 **기본은 접힘**이다. 대신 접힌 상태에서도 `MainViewModel.CompactSummaryText`("Desktop {버전} · CLI {버전} · Sessions {개수} · Threads {개수}")가 항상 한 줄 보인다. `MainWindow.xaml`의 `ToggleButton x:Name="DetailToggle"`(Style: `DetailToggleLink`, App.xaml)을 누르면 기존 Rows/ProbeRows 상세 패널이 펼쳐지고(`Visibility`가 `IsChecked`에 `ElementName` 바인딩), 다시 누르면 접힌다. 이건 순수 UI 표시 상태라 ViewModel에 별도 프로퍼티를 만들지 않았다(과설계 방지) — MVVM을 깨는 게 아니라 "표시 여부"는 View 전용 관심사라는 판단이다.
- 연결 실패 안내(`DetailText`, `IsConnected=False`일 때)와 경고(`WarningText`, `HasWarning`)는 이 Compact/Expand 구조와 완전히 무관하다 — 접어도 절대 숨겨지지 않는다. 새로 뭔가를 이 영역에 추가할 때 실수로 이 두 개를 토글 안쪽에 넣지 않도록 주의할 것.
- 본문 Grid(`Grid.Row="3"`)의 위쪽 행은 고정 `220`이 아니라 `Auto`다 — 상세 정보를 펼쳐도 `Border MaxHeight="260"`+내부 `ScrollViewer`로 막혀 있어 창 전체를 다시 잡아먹지 않는다. 창을 세로로 늘리면 남는 공간은 아래쪽 `Height="*"` 행(프로젝트 트리 + Conversation Viewer)이 가져간다. **이 Auto/MaxHeight 구조를 다시 고정 높이로 되돌리지 말 것** — 이번 Phase 04_07 자체가 "상단이 화면 절반을 차지한다"는 문제를 고치기 위한 것이었다.

### 진단
- `CrashDiagnostics`(App/Services)가 UI 스레드 미처리 예외를 `%APPDATA%\CodexBackupManager\logs`에 기록한다(exception type/HResult/스택 프레임 타입·메서드만/ConversationMessages 개수/Body 렌더링 개수/WorkingSet/PrivateMemory). 예외를 삼키지 않고 `Handled`를 건드리지 않는다 — 이 장치는 "임시 진단용"으로 시작했지만 현재 코드베이스에 남아 있고, 제거해 달라는 요청은 없었다.

### ScrollBar (Phase 04_06~04_07)
- `App.xaml`의 암시적(`x:Key` 없는) `ScrollBar` 스타일이 앱의 모든 `ScrollViewer`/`ScrollBar`에 자동 적용된다. `Track.Orientation`은 `ScrollBar.Orientation`과 **별개의 의존 속성**이라 `TemplateBinding`으로 명시적으로 연결하지 않으면 horizontal ScrollBar에서도 Track이 계속 세로로 배치된다 — Phase 04_06에서 처음 도입했을 때 실제로 이 연결이 빠져 있었고, Phase 04_07에서 실제 RED(`DarkScrollBarOrientationTests`, `App.xaml` 원본 마크업을 오려내 검증)로 확인 후 고쳤다. Vertical은 `PageUp`/`PageDown`, Horizontal은 `PageLeft`/`PageRight` 커맨드를 쓴다(Track의 기존 `RepeatButton` 인스턴스는 그대로 두고 `Command` 속성만 트리거로 바꾼다 — `Setter.Value`로 새 엘리먼트를 만들면 여러 ScrollBar가 같은 Style을 공유할 때 엘리먼트 재사용 문제가 생길 위험이 있어 피했다).

### Export — Phase 5 + Phase 05_01 hardening (전체 스펙은 `docs/codexbackup-format-v1.md`, FROZEN)
- **Restore Sufficiency Audit이 먼저다.** `ThreadRow`/`ThreadRowReader`는 실제 `state_5.sqlite.threads`
  38컬럼 전부를 보존한다. **Phase 5 최초 구현엔 `source`(NOT NULL) 컬럼이 빠져 있었다** — GitHub 코드
  리뷰로 발견해 Phase 05_01에서 고쳤고, 같은 누락이 재발하지 않도록 실제 38컬럼 목록과
  `ThreadRow`/`ThreadRowReader.KnownColumns`/`BackupConversationMetadata`를 기계적으로 대조하는
  회귀 테스트(`ThreadRowSchemaCoverageTests`, `BackupConversationMetadataCoverageTests`)를 추가했다
  — **새 컬럼을 추가할 때는 반드시 이 테스트들이 통과하는지 확인할 것.** `sandbox_policy`/
  `approval_mode`는 공식 소스 확인 결과 opaque JSON/enum 직렬화 문자열이라 **해석하지 않고 그대로**
  보존한다. `has_user_event`는 실제 이 PC 스키마엔 있지만 공개 `codex-rs` 클론에서는 컬럼 정의를
  못 찾았다(버전 차이로 추정) — 값은 보존하되 의미는 확정하지 않았다. **컬럼 존재 여부는 항상
  실측(`PRAGMA table_info`)을 신뢰하고 공식 소스는 보조 자료로만 쓴다**는 원칙이 이번에도 실측으로
  증명됐다.
- **가공값이 원본을 대체하지 않는다.** `resolvedTitle`/`resolvedProjectId`(제목 우선순위/프로젝트
  해결 결과)는 표시용 편의 필드일 뿐이고, `title`/`name`/`firstUserMessage`/`preview`/
  `originalRolloutPath`/`source` 등 원본 컬럼값을 별도로 그대로 보존한다(Phase 05_01 정정 — 최초
  구현은 가공값이 사실상 원본의 유일한 표현이었다).
- **Dependency closure는 새 규칙을 만들지 않았다.** `CodexBackupManager.Codex.Threads.ThreadDependencyResolver`
  (`ResolveChainLinks`/`SelectFiles`)가 `ThreadChainResolver.ResolveAncestry` + `ThreadChain.ParentThreadId`를
  그대로 재사용해 "어느 rollout 파일이 필요한가"를 계산한다. `ConversationTranscriptBuilder`(Viewer)도
  내부적으로 같은 `SelectFiles`를 호출하도록 리팩터링했다 — **Viewer와 Export가 100% 같은 lineage
  판단을 쓴다.** 조상이 여러 세그먼트로 나뉘어 있고 자식이 그 중 **중간** 세그먼트에서 분기했다면,
  그 이후 세그먼트(조상 자신의 별도 연속)는 dependency에서 제외한다(실제 rollout으로 검증됨).
- **선택 vs dependency 구분 + "부분 성공 금지"(Phase 05_01).** `MainViewModel.GetSelectedThreadIdsSnapshot()`에
  있는 ThreadId만 `BackupConversationMetadata.IsSelected=true`이고 `manifest.conversationCount`에
  잡힌다. **선택한 대화의 chain/metadata가 없거나, 조상 rollout을 찾을 수 없거나, 조상 체인에
  순환 참조가 있으면 — 예전처럼 warning으로 넘기지 않고 `ExportPlan.FatalErrors`에 기록해 Export
  전체를 FAIL시킨다**(`BackupWriter.Write`가 temp 파일조차 만들지 않는다). 이 구분이 없으면 사용자가
  "완료됐다"고 믿은 백업이 실제로는 대화 일부를 잃은 채였을 수 있다 — 이게 Phase 05_01의 가장 중요한
  수정이다. 반대로 첨부(`local_image`) 파일 하나가 삭제돼서 없는 것처럼 **optional**한 문제는 여전히
  warning으로만 남기고 Export는 성공 처리한다. 여러 선택 대화가 같은 조상을 공유하면 그 조상의
  rollout 파일은 여전히 **한 번만** payload에 포함된다.
- **원본 바이트를 그대로 보존한다.** `.jsonl`/`.jsonl.zst`를 파싱해서 다시 쓰지 않고
  `StreamingHashCopy`(80KB 고정 버퍼)로 스트리밍 복사하면서 SHA-256을 incremental로 같이 계산한다.
  실제 288MB rollout으로 확인: 메모리 증가가 파일 크기에 비례하지 않는다.
- **첨부(attachment) 정책은 실측으로 좁혔다**(§2 첨부 조사 참고). 실제 rollout content에서
  비-텍스트 content 원소는 `{type:"local_image", path}`(외부 파일 경로)와 `{type:"image_url"/
  "input_image", ...}`(이미 rollout 바이트 안에 있어 별도 처리 불필요, `image_url`은 전부 `data:`
  URI였다 — Phase 05_01에서 `response_item` 쪽도 실측 확인) 두 모양뿐이었다. `attachments\`
  (붙여넣기 텍스트)/`visualizations\`/`generated_images\`는 구조적으로 안전하게 추적할 참조 방법을
  찾지 못해 **V1에서 의도적으로 제외**했다. `LocalImageAttachmentScanner`는 이제
  `ConversationItemParser`와 **완전히 같은 file-level authoritative-source 정책**을 쓴다(event_msg가
  있는 파일은 그것만, 없는 파일만 response_item을 본다 — Phase 05_01 이전엔 항상 event_msg만 봐서
  fallback-only 파일의 참조를 놓칠 위험이 있었다, 실측으로는 0건이었지만 방어적으로 통일했다).
  **첨부 원본 경로 ↔ entry 경로의 명시적 역매핑(`manifest.attachments[]`)이 Phase 05_01에서
  추가됐다** — 이전엔 conversation의 entry 목록만 있어서 같은 basename이 다른 디렉터리에 있었을 때
  역매핑이 불가능했다. `local_image.path`가 가리키는 파일이 Export 시점에 없으면(흔함 — 임시
  스크린샷 등) 조용히 버리지 않고 manifest에 경고 개수로 남긴다(이건 여전히 optional이라 FAIL이
  아니다).
- **checksums.json 정책(Phase 05_01 정정)**: `manifest.json`도 이제 체크섬 보호 대상이다 —
  `checksums.json`이 `manifest.json`의 해시를 기록하지만 `manifest.json`은 `checksums.json`
  내용을 전혀 참조하지 않으므로 **순환이 아니다**(최초 판단은 이걸 순환으로 오판해 manifest를
  체크섬 밖에 뒀었다). 여전히 체크섬에 없는 건 `checksums.json` 자기 자신뿐이다(이건 진짜 순환).
  SHA-256은 accidental corruption 탐지용이며 전자서명이 아니다 — 문서에 명시해 뒀다.
- **Atomic publish + self-validation, hardened(Phase 05_01)**: `BackupWriter`는 최종 목적지 옆에
  temp 파일을 만들고, 전부 쓴 뒤 `BackupReader`/`BackupValidator`로 **다시 열어서** 검사한 뒤에만
  `File.Move`로 최종 파일이 된다. Phase 05_01에서 Validator에 추가된 것: Windows 기준(대소문자 무시)
  entry 충돌 검출, `checksums.json` 자체의 중복/안전 경로 검사(예외 없이), manifest 내부
  `conversationCount`/`dependencyConversationCount`/`projectCount`/`payloadCount`가 실제 배열/entry
  개수와 일치하는지, project가 실제 선택된 thread만 참조하는지, threadId 중복 없음, 허용된 위치
  (`manifest.json`/`checksums.json`/`payload/` 하위)만 존재하는지. **Validator는 이제 malformed
  입력에서 절대 예외를 던지지 않는다**(최상위 `try/catch`로 이중 방어) — Phase 6부터 `.codexbackup`이
  외부 입력이 되므로 필수. 실패/취소/예외 시 temp만 지우고 **기존 목적지 파일은 절대 건드리지
  않는다**. `overwrite` 파라미터로 명시적으로 제어한다.
- **Export 도중 원본 변경 감지**: 각 payload 파일을 열기 직전/복사 직후 `(Length, LastWriteTimeUtc)`를
  스냅샷 비교한다. Codex가 실행 중이라 rollout이 append되는 상황을 실제 파일 변조 테스트로
  재현·확인했다(`SourceChangedDuringExportException` → Export 전체 실패 + temp 삭제).
- **UI는 core를 그대로 호출할 뿐이다.** `MainViewModel.ExportCommand`가
  `ExportPlanBuilder.Build()` → `ManifestBuilder.Build()` → `BackupWriter.Write()`를 순서대로
  부르고, ZIP/체크섬/atomic 로직은 전혀 갖고 있지 않다. 저장 위치는 `BackupFilePicker`(WPF 내장
  `SaveFileDialog`)로 고르고, 기본 파일 이름은 시각 기반이다(대화 제목 원문 사용 금지, 요구사항 15).
  **Phase 05_01에서 `CancelExportCommand`를 추가했다** — Export 중에만 보이는 취소 버튼, 300MB급
  payload 복사 도중 취소해도 temp가 즉시 삭제되고 목적지가 생기지 않음을 실제 파일로 확인했다.

### Import Preview — Phase 6(전체 스펙은 `docs/import-preview-phase6.md`)

- **timestamp로 판정하지 않는다.** `updated_at`/파일 수정 시각은 UI 참고 정보일 뿐이고,
  `RevisionRelation`(New/Identical/IncomingAhead/LocalAhead/Diverged/Unverifiable) 판정은 항상 실제
  rollout lineage + 논리적 바이트 내용(`ConversationRevision`/`RolloutSlice`)으로 결정된다.
- **전체 파일이 아니라 "이 conversation이 실제로 소비하는 슬라이스"만 비교한다.** child가 parent
  rollout의 중간에서 분기한 뒤 parent가 계속 쓰였어도, 그 이후 내용은 fingerprint에 포함하지
  않는다 — Export의 dependency closure 경계 판정과 완전히 같다(`ThreadDependencyResolver`).
  `.jsonl.zst`는 스트리밍으로 압축 해제한 논리 바이트 기준으로 비교한다(Backup V1 payload 자체는
  여전히 원본 압축 바이트 그대로 유지 — 이건 바뀌지 않았다).
- **새 lineage 규칙을 만들지 않았다.** `ThreadDependencyResolver`에 `ResolveFileSlices`를 추가해
  "파일별로 어디까지 포함하는가"(ordinal/byte cutoff) 판단을 뽑아냈고, Viewer의
  `ConversationTranscriptBuilder.ReadChainMessages`도 이 메서드를 쓰도록 리팩터링했다 — Viewer/
  Export/Import Preview 세 곳이 완전히 같은 코드로 lineage 경계를 판단한다(drift 없음, 기존 178개
  Codex.Tests 전부 그대로 통과해 회귀 없음을 확인했다).
- **backup 쪽 lineage는 manifest가 아니라 rollout 파일 자체에서 다시 만든다.** `BackupCatalogReader`가
  `payload/rollouts/`의 파일명(`RolloutFileNamePattern`)과 각 파일의 `session_meta`(로컬과 완전히
  같은 파서, `CodexSessionParser`에 스트림 오버로드 추가)로 `ThreadChainResolver.Resolve`를 그대로
  호출한다 — manifest에 별도 lineage 필드를 추가하지 않았다(원본 파일이 이미 진실을 담고 있다).
  이 재구성은 `payload/rollouts/` 전체를 대상으로 하므로, 선택 대화든 dependency-only 조상이든
  manifest에 나열된 어떤 thread의 ancestry도 해석할 수 있다(Phase 05_01의 "부분 성공 금지" 덕분에
  closure가 항상 완전하다).
- **`IRolloutSliceReader`로 로컬 파일과 backup ZIP entry를 추상화했다.** `ConversationRevisionBuilder`/
  `ConversationRevisionComparer`(Codex 프로젝트)는 바이트를 "어디서" 읽는지 모른다 —
  `LocalFileRolloutSliceReader`(로컬 `File.Open`)와 `BackupRolloutSliceReader`(ZIP entry `Open()`,
  Backup 프로젝트)가 같은 인터페이스 뒤에서 교체된다.
- **"같은 rollout id, 다른 길이"는 실제로 byte prefix인지 다시 읽어서 확인한다.** 두 슬라이스의 전체
  해시만 비교해서는 "한쪽이 다른 쪽의 이어쓰기"인지 "우연히 길이가 다른 발산"인지 구분할 수 없다 —
  `ConversationRevisionComparer`는 더 긴 쪽을 짧은 쪽 길이로 다시 잘라 해시해 짧은 쪽과 정확히
  일치할 때만 `IncomingAhead`/`LocalAhead`로 판정하고, 아니면 `Diverged`다.
  **(Phase 06_01 정정)** 이 판정은 "두 revision이 모두 그 인덱스가 자신의 마지막 슬라이스일 때만"이
  아니라, **각 쪽이 "여기서 끝나는지"를 독립적으로 본다** — 한쪽만 끝나고 다른 쪽이 계속돼도(뒤에
  segment가 더 있어도) 끝난 쪽이 계속되는 쪽의 진짜 byte prefix면 여전히 fast-forward로 인정한다
  (아래 06_01 addendum 참고). 여전히, 양쪽 모두 그 위치 뒤에도 계속되는데 내용이 다르면 안전하게
  `Diverged`로 취급한다.
- **metadata 차이는 revision 관계와 완전히 분리한다.** `MetadataDifferenceAnalyzer`가 cwd/프로젝트
  연결/고정 여부/섹션/제목/최근 사용 시각 차이를 별도로 보여줄 뿐, Phase 6은 병합 정책을 정하지
  않는다(Phase 7의 몫).
- **선택 대화가 어떤 project 그룹에도 안 걸리는 경우를 방어한다.** 실제 UI 선택은 항상 로컬
  카탈로그의 "기타 대화" 그룹을 거치므로 이론상 발생하지 않지만, 만약 그런 경우가 생기면
  `ImportPreviewBuilder`가 조용히 화면에서 빠뜨리지 않고 "기타 대화" fallback 그룹에 넣는다(실제
  `.codex` 데이터로 최초 발견 → 테스트로 RED 확인 → 수정, 아래 §9 실제 데이터 검증 참고).
- **프로젝트 경로 재매핑은 판정만 한다.** `ProjectPathMapper`가 backup의 원본 루트 경로를 현재 PC의
  로컬 프로젝트 canonical path와 대조해 자동 연결을 제안할 뿐, Codex에는 쓰지 않는다(실제 재지정은
  Phase 7).
- **UI는 core를 그대로 호출할 뿐이다.** `MainViewModel.ImportPreviewCommand`가
  `ImportPreviewBuilder.Build()`를 부르고, ZIP/rollout 비교 로직은 전혀 갖고 있지 않다. 결과는
  `ImportPreviewViewModel`로 감싸 내부 enum 영어명이 아니라 한국어 라벨(신규/동일/업데이트 가능/
  현재 PC가 더 최신/분기 충돌/확인 불가)로 보여준다.

### Import Preview — Phase 06_01 addendum(Revision Relation Hardening / Import Plan Finalization)

- **Segment transition fast-forward 오판 수정.** 같은 rollout id에서 slice 내용이 다를 때, 예전엔
  `i == a.Count-1 && i == b.Count-1`(양쪽 다 마지막 slice)일 때만 prefix 검사를 했다. 그래서
  `Local=[R1-short]`, `Incoming=[R1-long, R2]`(로컬이 R1이 아직 짧았을 때의 과거 snapshot이고
  incoming은 그 뒤 R1이 이어써진 다음 새 segment R2까지 생긴 상태)를 무조건 `Diverged`로 오판했다.
  지금은 "이 인덱스가 **내** 마지막 슬라이스인지"를 각 쪽에서 독립적으로 판단해 — 한쪽만 끝나고
  다른 쪽이 계속되면(뒤에 segment가 몇 개 더 있어도) 끝난 쪽이 계속되는 쪽의 진짜 byte prefix일 때
  여전히 `IncomingAhead`/`LocalAhead`로 인정한다. 양쪽 모두 그 위치 뒤에도 계속되는데 이미 다르면
  (`Local=[R1-local,R2...]`/`Incoming=[R1-incoming,R2...]`) 여전히 안전하게 `Diverged`다. 실제
  세그먼트 3개짜리 real thread(21MB, segment1 974줄)로 재현하는 과정에서 **segment1 원본 파일이
  공식 인정된 lineage cutoff(ordinal&lt;967) 이후에도 별도로 계속 쓰인 바이트(967~973번 줄)를 갖고
  있을 수 있다**는 사실을 실측으로 처음 확인했다 — 이 cutoff 이후 내용까지 포함한 스냅샷을 "local"로
  쓰면 (진짜 fast-forward가 아니라) 정당하게 `Diverged`로 판정된다. cutoff까지만 정확히 자른
  스냅샷으로는 `IncomingAhead`가 올바르게 재현됨을 확인했다 — "원본 파일 전체 ≠ 이 체인이 인정하는
  범위"라는 원칙이 실측으로 다시 증명된 사례다.
- **로컬 metadata는 있는데 chain만 없으면 `New` 금지.** `ImportPreviewBuilder.DetermineRelation`이
  `!localChains.ContainsKey(threadId)`만 보고 `New`로 판정하던 것을 고쳤다 — 로컬 카탈로그의
  `AllConversations`(dependency-only/internal thread 포함, state DB 행이 있는 전체)에 같은
  ThreadId가 있으면(=이 PC가 이 대화를 "알고 있으면") rollout 파일이 삭제/손상돼 chain을 못
  만들어도 `Unverifiable`(→`Blocked`)로 처리한다. metadata도 chain도 전혀 없을 때만 진짜 `New`다.
  New로 잘못 분류하면 Phase 7이 이미 존재하는 대화를 "새 Import"로 취급해 위험한 동작을 할 수
  있었다.
- **수동 프로젝트 경로 재지정.** `ProjectPathMappingStatus.ManuallyLinked` 신설 +
  `ProjectPathMapping.WithManualOverride(path)`(순수 데이터, "기타 대화"에는 예외를 던진다) +
  `ImportPreviewBuilder.ApplyManualProjectPathOverride(preview, projectId, path)`(실제 디렉터리
  존재 확인 + `CanonicalPath` 정규화 후 그 프로젝트의 `PathMapping`만 교체한 새 `ImportPreview`를
  돌려준다 — revision 판정 결과는 재계산하지 않는다). `NotFound`뿐 아니라 `AutoLinked`도 사용자가
  원하면 덮어쓸 수 있다. override 상태는 View code-behind가 아니라 `ImportPreview`/
  `ProjectPathMapping` 자체에 저장된다 — `MainViewModel`은 `_lastImportPreviewDomain`(원본
  도메인 객체)을 들고 있다가 override 요청 시 이 메서드로 갱신할 뿐이다.
- **`ImportPlan`/`ImportPlanBuilder` 신설(Backup.Import).** `ImportPreview`를 Phase 7이 UI 트리를
  다시 해석하지 않고 그대로 받아 적용할 수 있는 형태로 freeze한다 — `ImportPlanConversation`(ThreadId/
  IsSelected/Relation/PlannedAction/TargetProjectPath), `ImportPlanProject`, `HasBlockingIssues`
  (Unverifiable=Blocked가 하나라도 있으면 항상 참 — 예외 없음), `HasUnresolvedDivergence`
  (Diverged=RequiresDecision이 하나라도 있으면 참, 아직 사용자 결정 메커니즘이 없으므로),
  `IsApplyReady = !HasBlockingIssues && !HasUnresolvedDivergence`. 검증 실패 Preview는 Plan
  자체를 만들지 않는다(`null`). `MainViewModel`이 Preview를 만들거나 경로를 재지정할 때마다
  `ImportPlanSummaryText`를 갱신해 화면에 Apply 준비 상태를 보여준다(적용은 하지 않는다).

---

## 4. 알려진 미해결 항목 / 주의사항

1. **"빈 User 메시지 버블" 원본 리포트는 재현하지 못했다.** 실제 데이터(4013+1648건 User 메시지), 실제 프로덕션 transcript 경로, 실제 WPF STA 가상화 스트레스(100만+ 표본) 전부 0건이었다. 공식 Codex 정책에 맞춘 방어적 수정(§3의 "빈 메시지 정책")은 적용했지만, **사용자가 이 버그를 다시 보면 정확한 위치(어느 대화, 몇 번째 메시지)를 받아서 재조사해야 한다** — 추가로 추측성 수정을 하지 말 것.
2. `AssistantPhase`에 매핑되지 않는 `"final_answer"` phase 값이 실측에 존재한다(위 §3 참고). 지금은 무시해도 되지만 향후 phase 관련 작업 시 확인 필요.
3. `.zst` 압축 rollout은 이 PC에 실물이 0개라 합성 fixture로만 검증했다(`docs/codex-storage-format.md` §9). **Export의 스트리밍 복사 경로도 실제 `.zst` 파일로는 아직 검증하지 못했다** — 같은 한계.
4. Export가 `attachments\`(붙여넣기 텍스트)/`visualizations\`/`generated_images\`의 실제 파일은 포함하지 않는다(§3 Export 항목 참고) — 구조적으로 안전한 참조 추적 방법을 찾지 못해 의도적으로 제외했다. `local_image`(스크린샷 등) 참조만 포함한다.
5. `has_user_event` 컬럼의 정확한 의미(공식 소스 미확인)는 여전히 확정하지 못했다 — 값은 보존하지만 해석하지 않는다.
6. 다단계 분기(조상의 조상)의 실제 사례가 여전히 이 PC 데이터에 없어 dependency closure를 3단계 이상으로는 실측 검증하지 못했다(합성 fixture로는 검증함).
7. ~~`ThreadRow`가 `threads.source` 컬럼을 누락했었다~~ — **Phase 05_01에서 수정 완료**(§3 Export 항목). 재발 방지 회귀 테스트 추가됨.
8. SHA-256은 accidental corruption 탐지용이며 전자서명(authenticity 보장)이 아니다 — `docs/codexbackup-format-v1.md` §3.3에 명시. 위조된 payload+체크섬 쌍을 함께 다시 만들면 이 검증을 통과할 수 있다.
9. 첨부 dedupe는 원본 절대경로 문자열 비교(대소문자 무시) 기준이다 — 심볼릭 링크 등으로 실제로는 같은 파일을 다른 경로가 가리키는 경우까지는 동일 파일로 인식하지 못할 수 있다(실측 사례 없음).
10. **(Phase 6)** 이 PC의 실제 `.codex` 데이터에는 `Diverged`/`LocalAhead` 실제 사례가 없어(같은 rollout이 두 PC에서 각각 앞서가는 상황은 실제로 두 PC를 오간 이력이 있어야 함), 합성 스냅샷(실제 rollout 파일을 복사해 앞부분만 자르거나 뒷부분을 다르게 바꾼 것)으로 재현·검증했다. `New`/`Identical`/`IncomingAhead`는 실제 데이터만으로도(Export 후 자기 자신과 비교, 실제로 두 PC를 흉내낸 합성 스냅샷) 확인했다.
11. ~~**(Phase 6)** 프로젝트 경로 재매핑(`ProjectPathMapper`)은 canonical path 일치 여부로만 자동 연결한다 — 수동 재지정 UI가 없었다.~~ — **Phase 06_01에서 완료**: `ProjectPathMapping.WithManualOverride`/`ImportPreviewBuilder.ApplyManualProjectPathOverride` + 폴더 선택 버튼(NotFound/AutoLinked 둘 다 재지정 가능, "기타 대화"만 제외). 다만 실제 폴더를 "재배치"하는 것(Codex에 반영)은 여전히 Phase 7의 몫이다 — Phase 06_01은 Preview/Plan 단계에서 override 값을 고정해 두는 것까지만 한다.
12. **(Phase 6)** dependency-only(조상) 대화의 metadata 차이는 계산하지만 UI에는 노출하지 않는다(요구사항 11 — 조상은 상세 정보에서만) — 필요하면 `ImportConversationPreview.Metadata`는 이미 갖고 있으므로 UI만 추가하면 된다.
13. **(Phase 06_01)** 실제 세그먼트 thread(3세그먼트, segment1 974줄)로 확인한 결과, **segment1 원본 rollout 파일이 이 thread 체인이 공식적으로 인정하는 lineage cutoff(segment2의 history_base, ordinal&lt;967) 이후에도 별도로 계속 쓰인 바이트(967~973번 줄, 총 974줄 중 마지막 7줄)를 갖고 있었다** — 즉 "원본 파일 전체 길이"와 "이 체인이 실제로 소비하는 길이"가 다를 수 있다는 걸 실측으로 새로 확인했다. 이 뒤쪽 바이트가 무엇을 의미하는지(같은 thread의 무관한 후속 활동인지, 다른 이유인지)는 조사하지 않았다 — revision 비교 알고리즘은 이미 cutoff까지만 보므로 정확성에 영향은 없지만, 향후 이 부분을 다시 조사할 일이 있으면 "원본 rollout 파일 = 이 thread의 전체 내용"이라고 가정하지 말 것.
14. **(Phase 06_01)** `Diverged` 상태의 사용자 결정 메커니즘(로컬 유지/backup으로 교체/복사본으로 가져오기)은 아직 없다 — `ImportPlan.HasUnresolvedDivergence`가 있으면 `IsApplyReady`가 항상 거짓이 되어 Phase 7이 적용을 거부해야 한다는 정책만 확정했다. 실제 선택지 구현은 Phase 7.

---

## 5. 다음 세션이 지켜야 할 규칙(요약, 전문은 `CLAUDE.md`)

- **Read-Only 원칙**: `.codex` 원본은 절대 쓰지 않는다. 실제 데이터로 검증할 때는 항상 작업 전후 `state_*.sqlite`/`session_index.jsonl`/`.codex-global-state.json`/`config.toml` 해시를 비교해서 무변경을 확인한다.
- **Commit/Push는 사용자가 명시적으로 요청할 때만.** 지금까지 전부 사용자가 직접 커밋했다(`Phase 04_05`까지).
- **TDD**: 실제 RED 확인 → 최소 수정 → GREEN. 특히 이번 세션에서 "실제 데이터에 없다고 새 기능을 안 만들 필요는 없다"(공식 소스 근거가 있으면 실제 사례가 없어도 반영), 반대로 "증거 없이 추측성 수정은 하지 않는다"는 두 원칙이 계속 같이 적용됐다.
- **범위 통제**: 요청된 Phase/작업 범위를 벗어나지 않는다. 이번 세션에서 Markdown 파서를 role-agnostic하게 고쳤다가 의도치 않게 Assistant 메시지에도 영향을 준 적이 있었다(§3의 `text:""` 버그) — 코드 변경의 부수 효과가 요청 범위를 벗어나지 않는지 항상 실제 데이터로 재검증할 것.
- **한국어로 보고.** 코드 식별자/API/파일명/원문 로그는 영어 유지.
- **불확실하면 추측하지 말고 공식 Codex 소스(`scratchpad/codex-src`, 세션마다 다시 클론될 수 있음) 또는 실제 `.codex` 데이터로 먼저 확인.**

---

## 6. 테스트 구조 지도

| 프로젝트 | 대상 | 비고 |
|---|---|---|
| `CodexBackupManager.Domain.Tests` | `CanonicalPath`, `ConversationSelectionState` | WPF 비의존, 순수 |
| `CodexBackupManager.Codex.Tests` | 탐지/카탈로그/rollout 파서/`ConversationItemParser`/`ConversationTranscriptBuilder`/`ThreadDependencyResolver`/확장된 `ThreadRowReader` 등 | 합성 fixture(`tests/Fixtures/CodexHome`) 사용, 실제 사용자 데이터 커밋 안 함 |
| `CodexBackupManager.Backup.Tests` (Phase 5 신규, Phase 05_01/6/06_01 확장) | `ExportPlanBuilder`(dependency closure/dedupe/attachment/project 필터링 + **선택 대화 chain/metadata/ancestor/순환 누락 시 FatalErrors**), `BackupWriter`/`BackupReader`/`BackupValidator`(정상 export, 대상 파일 존재 시 시작 전 실패, 취소 시 temp 삭제 — 즉시 취소 + **300MB급 mid-copy 취소** 둘 다, Export 도중 원본 변경 감지 — 실제 파일 레이스로 재현, 100MB 스트리밍 bounded-memory, 변조 탐지, manifest 누락/버전 불일치/중복 entry/path traversal/참조 누락 검증 실패, **Windows 대소문자 충돌/checksums.json 자체 중복·불안전 경로(예외 없이 Fail)/manifest 개수 필드 불일치/project 참조 무결성/manifest.json 자체 변조 탐지**), `ImportPreviewBuilder`(실제 Export 파이프라인으로 만든 진짜 `.codexbackup`으로 New/Identical/IncomingAhead/LocalAhead/Diverged/Unverifiable 전부 재현, 여러 대화 혼합, dependency-only 분리, malformed backup → Preview 생성 금지, **Import Preview 동안 로컬 파일 수정 0건 확인**, 어떤 project에도 속하지 않은 선택 대화가 사라지지 않는지, **(Phase 06_01)** 로컬 metadata는 있는데 chain만 없으면 New가 아니라 Unverifiable, 반대로 metadata도 chain도 없어야만 진짜 New, dependency-only도 같은 원칙), `ProjectPathMapperTests`/`MetadataDifferenceAnalyzerTests`(canonical path 일치/불일치, metadata 필드별 차이), **`ImportPlanBuilderTests`**(Phase 06_01, 실패 Preview→null, New만 있으면 ApplyReady+BackupIdentity 일치, Diverged 있으면 HasUnresolvedDivergence, Unverifiable 있으면 HasBlockingIssues, TargetProjectPath가 project 소속 대화에만 채워지고 dependency-only는 null), **`TwoPcRoundTripTests`**(Phase 06_01 요구사항 5, temp fixture로 5단계 PC A↔B 왕복 시나리오 전체 재현 — IncomingAhead→Identical→LocalAhead→Diverged), 수동 경로 재지정 5건(NotFound/AutoLinked 재지정→ManuallyLinked, "기타 대화" 거부, 존재하지 않는 폴더/projectId 거부) | 전부 합성 임시 파일, 실제 사용자 데이터 없음 |
| `CodexBackupManager.Codex.Tests` (Phase 6/06_01 확장) | 기존 항목 전부 + `ConversationRevisionComparerTests`(Identical/IncomingAhead/LocalAhead/Diverged 전 조합, 세그먼트 추가, parent cutoff 경계(ordinal/byte 둘 다 off-by-one까지), `.jsonl` vs `.jsonl.zst` 동일 내용, 같은 ThreadId·다른 lineage → Diverged, `ConversationRevisionBuilder`의 Unverifiable 판정 — 체인 없음/순환/조상 누락, **(Phase 06_01)** 한쪽만 끝나고 다른 쪽은 segment가 더 있어도 진짜 prefix면 IncomingAhead/LocalAhead(양방향), 진짜 prefix가 아니면 segment가 있어도 Diverged, 3개 이상 segment, `.jsonl.zst` 논리 바이트로도 동일 원칙 확인, 조상이 있는 체인에서도 leaf 자신의 segment transition이 같은 원칙으로 동작) | `ConversationTranscriptBuilder`/`ThreadDependencyResolver` 리팩터링(`ResolveFileSlices` 공유) 후에도 기존 178건 전부 회귀 없이 통과 확인 |
| `CodexBackupManager.App.Tests` | ViewModel(Selection tri-state, Viewer/Selection 독립성, 카탈로그 refresh, `CompactSummaryText`, `MainViewModelExportTests`(Phase 5, 실제 fixture Codex Home으로 전체 Export 파이프라인 end-to-end 검증), `MainViewModelImportPreviewTests`(Phase 6, 실제 fixture Codex Home 2개로 Export→Import Preview 왕복 — 전부 Identical 확인, malformed backup 처리, **(Phase 06_01)** 실제 fixture의 진짜 프로젝트(Alpha)를 폴더 선택으로 수동 재지정 → ManuallyLinked)), Markdown-lite 파서/렌더러, **`FlowDocumentBindingRecyclingStressTests`**(실제 STA 스레드에서 `Window`+가상화 `ListBox`+`RichTextBox`를 띄우고 왕복 스크롤 — `System.Windows.Application`은 프로세스당 하나만 만들 수 있어 `Dispatcher.Run()`만 쓴다), **`DarkScrollBarOrientationTests`**(`App.xaml` 원본 마크업에서 ScrollBar 스타일+의존 리소스만 오려내 독립 `ResourceDictionary`로 파싱, STA 스레드에서 실제 `Track.Orientation`/커맨드 검증 — `Application` 인스턴스 없이 진행) | `net10.0-windows`+`UseWPF`, `InternalsVisibleTo`로 `MainViewModel.Selection` 접근 |

마지막 전체 실행 결과(Phase 06_01 포함): `Domain 64 + Codex 200 + Backup 66 + App 87 = 417건 전부
통과`, `dotnet build` 경고/오류 0. 의존 방향은 `App → Backup → Codex → Domain`(단방향, 역방향 없음).

**여전히 커버하지 못한 테스트 항목(정직하게 남김)**: 대량의 요구 테스트 목록 중 다음은
자동화 테스트로 명시적으로 덮지 않았다 — 향후 Phase 7 작업 시 필요하면 추가할 것.
- 실제 `.jsonl.zst` 파일로의 byte-for-byte 보존(합성 데이터로만 검증, §4-3 참고). Phase 6/06_01의
  revision 비교도 같은 한계 — `.zst` 쪽은 합성 fixture로만 확인했다.
- Writer의 일반 예외(디스크 풀 등) 발생 시 temp 삭제 — "원본 변경 감지"/"취소" 예외 경로는 실제로
  검증했지만, 그 밖의 임의 예외 종류까지 전부 개별 테스트하지는 않았다(다만 `finally` 블록이
  예외 종류에 무관하게 temp를 지우므로 구조적으로는 커버된다)
- selection snapshot에 없는 대화가 "우연히" 다른 곳에서 독립적으로 export되지 않는다는 것은
  `ExportPlanBuilder` 테스트로 확인했지만, UI 트리 체크박스 조작까지 엮은 end-to-end는 아니다
- **(Phase 6/06_01)** 실제 `.codex` 데이터에 3단계 이상 분기(조상의 조상)나 진짜로 두 PC를 오간
  `Diverged`/`LocalAhead` 사례가 없어, 실제 rollout 파일을 복사한 합성 스냅샷으로 재현했다(§4-10
  참고). `New`/`Identical`/`IncomingAhead`(segment transition 포함)는 실제 세그먼트/분기 thread로
  직접 확인했다(아래 하네스 절차 참고).

실제 `.codex` 데이터 재검증용 스크래치패드 하네스 패턴(세션마다 새로 만들어야 함, 세션 scratchpad 디렉터리에 위치):
`CodexDetectionService` → `CodexCatalogBuilder.Build` → `ConversationTranscriptBuilder.Build` 순으로 실제 카탈로그/transcript를 만들고, 대화 원문은 출력하지 않고 개수/해시/구조 메타데이터만 출력하는 방식을 계속 써왔다. Phase 6에서는 여기에 `ExportPlanBuilder`/`ManifestBuilder`/`BackupWriter`로 실제 선택 대화(최대 40개, 세그먼트/분기 사례 우선 포함)를 진짜 `.codexbackup`으로 만들고, `ImportPreviewBuilder.Build`로 그 backup을 같은 카탈로그와 다시 비교(자기 자신이므로 전부 `Identical`이어야 함)하는 절차를 추가했다 — 실제 rollout 파일 하나를 복사해 앞부분만 자르거나(IncomingAhead/LocalAhead 재현) 뒷부분을 다르게 바꿔(Diverged 재현) 합성 스냅샷도 실제 파일 기반으로 만들었다. **Phase 06_01**에서는 실제 3-segment thread(21MB, segment1 974줄)를 골라, segment2의 실제 history_base cutoff(ordinal&lt;967)까지만 정확히 자른 segment1 스냅샷을 "로컬"로 써서 진짜 segment-transition IncomingAhead를 재현했다 — 원본 segment1 파일을 그대로(자르지 않고) 썼을 때는 원본이 cutoff 이후에도 더 쓰인 바이트를 갖고 있어 오히려 Diverged가 정확한 답이라는 것도 함께 확인했다(§4-13).

---

## 7. 다음에 할 일이 주어지면

Phase 7(Safe Restore/Apply)을 시작하게 되면 먼저 확인할 것:
- `docs/import-preview-phase6.md`가 Import Preview의 정식 스펙이다(Phase 06_01 완료 기준
  **FROZEN**) — `RevisionRelation` 정의/segment transition을 포함한 fast-forward 판정 규칙/
  divergence 규칙/metadata diff 정책/수동 path remapping/`ImportPlan` freeze 경계 전부 여기 있다.
  Phase 7은 **`ImportPlan`(=`Backup.Import.ImportPlan`, `ImportPreview`가 아니다)을 입력으로 받아
  실제로 적용하는 계층이다** — `ImportPreviewBuilder`/`ImportPlanBuilder`가 이미 만든 결과를 그대로
  받아 쓰고, Preview 판정 로직이나 UI 트리를 다시 해석하지 말 것.
- **`ImportPlan.PlannedAction`이 이미 기본 정책을 정해 뒀다**: `New`→Import,
  `Identical`→NoOp(다시 쓰지 않기), `IncomingAhead`→Fast-forward Update, `LocalAhead`→Skip(로컬을
  뒤로 되돌리지 않기), `Diverged`→RequiresDecision(자동 merge 금지, 사용자 결정 필요),
  `Unverifiable`→Blocked(적용 금지). **`ImportPlan.IsApplyReady`가 거짓이면(Blocked나
  RequiresDecision이 하나라도 있으면) Phase 7은 적용을 거부해야 한다** — 이 판정을 우회하거나
  다시 계산하지 말 것. Phase 7은 이 계획을 실행하는 Snapshot/Rollback/실제 쓰기를 더하는 것이지,
  판정 정책 자체를 다시 정하는 게 아니다.
- **Fast-forward(IncomingAhead) 적용은 파일 append/복사 + state DB 갱신이 필요하다** —
  `ConversationRevision`/`RolloutSlice`가 이미 "정확히 어느 rollout id부터 몇 바이트가 새로
  추가됐는지"를 알고 있으므로(`ConversationRevisionComparer`의 prefix 검증 로직 재사용 가능), 이
  정보를 그대로 Restore 계획에 활용할 것 — 처음부터 다시 diff를 계산하지 말 것. **(Phase 06_01)**
  이 prefix 검증은 "양쪽 다 마지막 slice일 때"뿐 아니라 "한쪽만 끝나고 다른 쪽은 segment가 더
  있을 때"도 올바르게 동작하도록 고쳤다 — 세그먼트가 있는 실제 대화의 fast-forward도 이 로직을
  그대로 믿고 써도 된다.
- **Diverged는 자동 merge하지 않는다.** Phase 6/06_01은 판정만 했다(`ImportPlan.HasUnresolvedDivergence`)
  — Phase 7에서 "로컬 유지/backup으로 교체(고위험)/복사본으로 가져오기" 중 어떤 선택지를 어떻게
  구현할지, 그리고 사용자 결정을 어떻게 받아 `IsApplyReady`를 참으로 만들지(예: 사용자가 선택지를
  고르면 그 conversation만 Plan에서 제외하고 나머지로 진행하는 등) 아직 결정된 게 없다. 두 rollout을
  메시지 단위로 임의로 합치는 시도는 하지 말 것(요구사항으로 명시적으로 금지됨).
- **프로젝트 경로 수동 재지정은 Phase 06_01에서 완성했다** — `ImportPlanConversation.TargetProjectPath`에
  최종 확정된 경로(자동 연결이든 수동 재지정이든)가 이미 들어 있다. Phase 7은 이 값을 그대로 쓰면
  된다 — `ProjectPathMapper`/`ImportPreviewBuilder.ApplyManualProjectPathOverride`를 다시 부르거나
  경로 판정을 다시 할 필요가 없다. 실제로 그 경로에 Codex 프로젝트를 연결/생성하는 것만 Phase 7의 몫.
- `manifest.attachments[]`(원본 경로 ↔ entry 경로 역매핑)와 `BackupConversationMetadata`의 원본
  38컬럼 필드 전부가 Restore가 실제로 쓸 수 있는 재료다 — `resolvedTitle` 같은 가공값이 아니라
  원본 필드를 기준으로 Restore 로직을 설계할 것.
- `docs/codexbackup-format-v1.md`가 Backup V1의 정식 스펙이다(FROZEN) — `CLAUDE.md` §11~13은 이제 이
  문서로 대체된 **초안**이니 그대로 구현하지 말 것.
- Thread ID 충돌 처리(기본 "건너뛰기"), Snapshot/Rollback, Codex 실행 여부 확인은 전부 Phase 7의
  영역이다 — Phase 6/06_01은 판정/미리보기/freeze까지만 하고 Codex에 아무것도 쓰지 않았다(실제
  `.codex` 데이터로 재확인: 이번 세션 작업 전후 `state_5.sqlite`/`-wal`/`-shm`/`session_index.jsonl`/
  `.codex-global-state.json`/`config.toml` 해시 전부 동일).
- Phase 5가 의도적으로 제외한 것(`attachments\`/`visualizations\`/`generated_images\` 실제 파일,
  §3 Export 항목·§4 참고)을 Import/Restore 완전성 기준에 포함시킬지는 아직 결정되지 않았다 — 사용자
  요청 없이 임의로 범위를 넓히지 말 것.
- **(Phase 06_01 실측 발견)** rollout 원본 파일의 전체 길이가 그 thread 체인이 실제로 인정하는
  길이보다 길 수 있다(§4-13) — Restore/검증 로직에서 "파일 = 이 thread의 전체 내용"이라고 가정하지
  말고, 항상 `ConversationRevision`/`RolloutSlice`가 계산한 논리적 슬라이스 경계를 기준으로 삼을 것.
