# 프로젝트 현재 상태 및 인계 노트

이 문서는 **세션이 압축되거나 새로 시작되어도 작업을 정확히 이어갈 수 있도록** 지금까지 확정된 것,
남은 것, 주의할 것을 정리한다. 새 세션은 `CLAUDE.md` 다음, 다른 어떤 코드를 읽기 전에 이 문서부터
읽는다(§ "구현 시 참조 순서" 갱신 참고).

마지막 갱신 기준: `Phase 04_05` 커밋(`0591945`)까지 완료, 작업 트리 clean.

---

## 1. 한 줄 요약

**Phase 1~4(Codex 탐색 → Read Model → Conversation Viewer → Selection, 그리고 Phase 4 자체의
사후 정합성/성능/크래시/Fidelity 수정 5건)까지 전부 완료했고, Phase 5(Export)는 아직 시작하지 않았다.**
다음에 할 일이 명시적으로 주어지지 않으면 Phase 5를 추측해서 시작하지 말 것.

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

Phase 5(Export)~7(Safe Restore)은 **아직 시작 전**이다.

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
- Codex 스타일 레이아웃: Assistant는 평문형(카드/테두리 없음, 왼쪽 정렬, 최대 폭 760), User는 작은 말풍선(오른쪽 정렬, 최대 폭 520, 내용 크기만큼만). `User`/`Assistant`/`commentary`/`final` 라벨은 UI에서 제거했다(Domain에는 Phase 값 그대로 보존).
- `VirtualizingPanel.ScrollUnit="Pixel"`로 스크롤이 항목 단위가 아니라 픽셀 단위로 부드럽게 움직인다 — 이걸 되돌리면 "스크롤이 딱딱하다"는 문제가 재발한다.

### 진단
- `CrashDiagnostics`(App/Services)가 UI 스레드 미처리 예외를 `%APPDATA%\CodexBackupManager\logs`에 기록한다(exception type/HResult/스택 프레임 타입·메서드만/ConversationMessages 개수/Body 렌더링 개수/WorkingSet/PrivateMemory). 예외를 삼키지 않고 `Handled`를 건드리지 않는다 — 이 장치는 "임시 진단용"으로 시작했지만 현재 코드베이스에 남아 있고, 제거해 달라는 요청은 없었다.

---

## 4. 알려진 미해결 항목 / 주의사항

1. **"빈 User 메시지 버블" 원본 리포트는 재현하지 못했다.** 실제 데이터(4013+1648건 User 메시지), 실제 프로덕션 transcript 경로, 실제 WPF STA 가상화 스트레스(100만+ 표본) 전부 0건이었다. 공식 Codex 정책에 맞춘 방어적 수정(§3의 "빈 메시지 정책")은 적용했지만, **사용자가 이 버그를 다시 보면 정확한 위치(어느 대화, 몇 번째 메시지)를 받아서 재조사해야 한다** — 추가로 추측성 수정을 하지 말 것.
2. `AssistantPhase`에 매핑되지 않는 `"final_answer"` phase 값이 실측에 존재한다(위 §3 참고). 지금은 무시해도 되지만 향후 phase 관련 작업 시 확인 필요.
3. `.zst` 압축 rollout은 이 PC에 실물이 0개라 합성 fixture로만 검증했다(`docs/codex-storage-format.md` §9).
4. Phase 5 Export의 구체적 백업 파일 구조/manifest 필드는 아직 설계하지 않았다 — `CLAUDE.md` §11~13이 초안이고 실제 구현 시 다시 확정 필요.

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
| `CodexBackupManager.Codex.Tests` | 탐지/카탈로그/rollout 파서/`ConversationItemParser`/`ConversationTranscriptBuilder` 등 | 합성 fixture(`tests/Fixtures/CodexHome`) 사용, 실제 사용자 데이터 커밋 안 함 |
| `CodexBackupManager.App.Tests` | ViewModel(Selection tri-state, Viewer/Selection 독립성, 카탈로그 refresh), Markdown-lite 파서/렌더러, **`FlowDocumentBindingRecyclingStressTests`**(실제 STA 스레드에서 `Window`+가상화 `ListBox`+`RichTextBox`를 띄우고 왕복 스크롤 — `System.Windows.Application`은 프로세스당 하나만 만들 수 있어 `Dispatcher.Run()`만 쓴다) | `net10.0-windows`+`UseWPF`, `InternalsVisibleTo`로 `MainViewModel.Selection` 접근 |

마지막 전체 실행 결과: `Domain 64 + Codex 166 + App 71 = 301건 전부 통과`, `dotnet build` 경고/오류 0.

실제 `.codex` 데이터 재검증용 스크래치패드 하네스 패턴(세션마다 새로 만들어야 함, 세션 scratchpad 디렉터리에 위치):
`CodexDetectionService` → `CodexCatalogBuilder.Build` → `ConversationTranscriptBuilder.Build` 순으로 실제 카탈로그/transcript를 만들고, 대화 원문은 출력하지 않고 개수/해시/구조 메타데이터만 출력하는 방식을 계속 써왔다.

---

## 7. 다음에 할 일이 주어지면

Phase 5(Export)를 시작하게 되면 먼저 확인할 것:
- `MainViewModel.GetSelectedThreadIdsSnapshot()`으로 얻은 ThreadId 집합 → 실제 백업 대상 rollout 파일 범위(부모 체인 포함 여부 등) 매핑 로직이 아직 없다.
- `CLAUDE.md` §11~13(백업 파일 구조/manifest/체크섬)은 아직 실측 기반으로 확정되지 않은 **초안**이다 — 그대로 구현하지 말고 실제로 필요한 필드부터 다시 검토할 것.
