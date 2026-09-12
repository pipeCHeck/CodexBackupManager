# 프로젝트 현재 상태 및 인계 노트

이 문서는 **세션이 압축되거나 새로 시작되어도 작업을 정확히 이어갈 수 있도록** 지금까지 확정된 것,
남은 것, 주의할 것을 정리한다. 새 세션은 `CLAUDE.md` 다음, 다른 어떤 코드를 읽기 전에 이 문서부터
읽는다(§ "구현 시 참조 순서" 갱신 참고).

마지막 갱신 기준: `Phase 04_06` 커밋(`d6e2521`)까지 완료 + `Phase 04_07`(상단 진단 영역 Compact/Expand
레이아웃, horizontal ScrollBar 정합성) 작업 완료. **`Phase 04_07`은 아직 사용자가 커밋하지 않은
상태**(작업 트리에 변경 있음) — 새 세션은 `git log`/`git status`로 실제 커밋 여부를 다시 확인할 것.
사용자는 이번 `Phase 04_07`을 **Phase 4의 마지막 UI 수정**으로 명시했다 — 이 작업이 끝나면 다음은
Phase 5(Export)다.

---

## 1. 한 줄 요약

**Phase 1~4(Codex 탐색 → Read Model → Conversation Viewer → Selection, 그리고 Phase 4 자체의
사후 정합성/성능/크래시/Fidelity/레이아웃 수정 7건, 04_01~04_07)까지 전부 완료했고, Phase 5(Export)는
아직 시작하지 않았다.** 사용자가 04_07을 "Phase 4의 마지막 UI 수정"으로 명시했으므로, 다음에 할 일이
명시적으로 주어지지 않으면 Phase 5로 넘어갈 준비가 된 상태로 보고, Phase 5를 추측해서 미리 시작하지
말 것(사용자의 명시적 지시를 기다린다).

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
| (미커밋) | Phase 04_07 | **Phase 4의 마지막 UI 수정.** 상단 진단 영역을 Compact/Expand 구조로 재구성(기본 접힘, `MainViewModel.CompactSummaryText`로 Desktop/CLI/Sessions/Threads 한 줄 요약, "상세 정보" `ToggleButton`으로 기존 Rows/ProbeRows 펼침/접힘, `MaxHeight`+내부 `ScrollViewer`로 창 전체를 다시 잡아먹지 않게 제한), 본문 Grid의 고정 `RowDefinition Height="220"` 제거(→ `Auto`, 남는 공간은 프로젝트/Viewer 영역이 가져감), `App.xaml` 다크 `ScrollBar`의 horizontal 정합성 수정(`Track.Orientation`이 `ScrollBar.Orientation`을 실제로 따라가도록 `TemplateBinding` 추가 — 이전엔 안 따라갔다, PageUp/PageDown↔PageLeft/PageRight 커맨드 분리) |

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
| `CodexBackupManager.App.Tests` | ViewModel(Selection tri-state, Viewer/Selection 독립성, 카탈로그 refresh, `CompactSummaryText`), Markdown-lite 파서/렌더러, **`FlowDocumentBindingRecyclingStressTests`**(실제 STA 스레드에서 `Window`+가상화 `ListBox`+`RichTextBox`를 띄우고 왕복 스크롤 — `System.Windows.Application`은 프로세스당 하나만 만들 수 있어 `Dispatcher.Run()`만 쓴다), **`DarkScrollBarOrientationTests`**(`App.xaml` 원본 마크업에서 ScrollBar 스타일+의존 리소스만 오려내 독립 `ResourceDictionary`로 파싱, STA 스레드에서 실제 `Track.Orientation`/커맨드 검증 — `Application` 인스턴스 없이 진행) | `net10.0-windows`+`UseWPF`, `InternalsVisibleTo`로 `MainViewModel.Selection` 접근 |

마지막 전체 실행 결과(Phase 04_07 포함): `Domain 64 + Codex 166 + App 82 = 312건 전부 통과`, `dotnet build` 경고/오류 0. (Phase 04_07에서 `MainViewModelCompactSummaryTests` 2건, `DarkScrollBarOrientationTests` 3건 추가 — 후자는 `Track.Orientation` TemplateBinding을 빼면 실제 RED가 나는 것까지 확인.)

실제 `.codex` 데이터 재검증용 스크래치패드 하네스 패턴(세션마다 새로 만들어야 함, 세션 scratchpad 디렉터리에 위치):
`CodexDetectionService` → `CodexCatalogBuilder.Build` → `ConversationTranscriptBuilder.Build` 순으로 실제 카탈로그/transcript를 만들고, 대화 원문은 출력하지 않고 개수/해시/구조 메타데이터만 출력하는 방식을 계속 써왔다.

---

## 7. 다음에 할 일이 주어지면

Phase 5(Export)를 시작하게 되면 먼저 확인할 것:
- `MainViewModel.GetSelectedThreadIdsSnapshot()`으로 얻은 ThreadId 집합 → 실제 백업 대상 rollout 파일 범위(부모 체인 포함 여부 등) 매핑 로직이 아직 없다.
- `CLAUDE.md` §11~13(백업 파일 구조/manifest/체크섬)은 아직 실측 기반으로 확정되지 않은 **초안**이다 — 그대로 구현하지 말고 실제로 필요한 필드부터 다시 검토할 것.
