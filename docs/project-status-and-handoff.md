# 프로젝트 현재 상태 및 인계 노트

이 문서는 **세션이 압축되거나 새로 시작되어도 작업을 정확히 이어갈 수 있도록** 지금까지 확정된 것,
남은 것, 주의할 것을 정리한다. 새 세션은 `CLAUDE.md` 다음, 다른 어떤 코드를 읽기 전에 이 문서부터
읽는다(§ "구현 시 참조 순서" 갱신 참고).

마지막 갱신 기준: `Phase 8` 커밋(`1481627`)까지 사용자가 커밋했고, 그 위에 **Phase 08_01(CI
Stabilization / Final Release Gate)** 작업을 완료했다(작업 트리에 변경 있음 — 아직 커밋 전, 새
세션은 `git log`/`git status`로 실제 커밋 여부를 다시 확인할 것). **중요: `Phase 8` 커밋을
push한 뒤 실행된 첫 GitHub Actions Windows CI(run #1)가 FAIL했다** — Restore/Backup Format
자체의 문제가 아니라 테스트 2건의 timing 문제였다(§2 Phase 08_01 행, 아래 참고). 이 사실이
확인되기 전까지는 **`v0.1.0` Tag/GitHub Release를 만들면 안 된다** — Phase 08_01이 로컬에서
결정적으로 고쳤지만, 최종 release gate는 실제 GitHub Actions GREEN이다(이 세션은 push를 하지
않았으므로 실제 Actions 결과는 아직 관찰하지 못했다). Import Preview/`RevisionRelation`/
`ImportPlan`/Preflight contract는 Phase 6/06_01/06_02/06_03 전부를 기준으로 **FINAL FROZEN**이다
(`docs/import-preview-phase6.md` 상단 배너 참고). Backup Format V1은 `Phase 05_01`(`be2f616`)을
기준으로 **FROZEN**이다(`docs/codexbackup-format-v1.md` 상단 배너 참고). 그 위에 **Phase 7(Safe
Restore / Fast-forward Apply Core)**을 완료했다 — 이 프로젝트에서 실제 Codex `.codex` write가
**처음** 등장하는 Phase다. 새 `CodexBackupManager.Restore`/`.Restore.Tests` 프로젝트를 신설해
frozen `ImportPlan`을 실제로 적용한다: Codex 실행 여부 확인 → fresh preflight → 물리 안전성까지
재확인하는 `RestoreOperationPlan` 생성 → Snapshot → 실제 write(rollout 파일 + `threads` SQLite
INSERT/UPDATE) → post-apply validation, 그리고 이 중 어디서든 실패하면 Snapshot으로 자동 Rollback.
그 뒤 GitHub 코드 리뷰로 발견된 실제 안전성/정합성 문제를 **Phase 07_01**에서 전부 고쳤다(§2 표,
`docs/safe-restore-phase7.md` §10) — ProcessGuard의 Dispose-순서 버그, Apply 내부 backup 재오픈
TOCTOU, WAL/SHM Snapshot 누락, `.jsonl.zst` 새 segment 물리/논리 해시 혼동, IncomingAhead 메타데이터
병합 정책 부재, New Import의 `thread_source` 누락 허용, 얕은 post-validation 등. 이번 Phase 07_01에서
**Apply 버튼을 포함한 UI도 처음으로 연결**했고, 실제 `C:\Users\User\.codex`를 clone한 데이터로
New Import 계열 시나리오를 검증했다(원본에는 세션 전체에서 단 한 번도 쓰지 않았다). 이번 Phase가
실제로 지원하는 범위는 `New`→Import, `Identical`→NoOp, `IncomingAhead`→안전이 증명되는 fast-forward만,
`LocalAhead`→Skip, `Diverged`/`Unverifiable`→Apply 전체 차단이다(고위험 기능인 Diverged 자동
merge/backup 교체는 여전히 미구현 — Phase 8 이후 후보). **Phase 07_02**는 배포 전 마지막 안전성
게이트였다(§2 표, `docs/safe-restore-phase7.md` §11) — rollout append를 temp+atomic replace로
전면 교체해 crash-safety를 확보했고, `RollbackService`의 post-check가 더 이상 live target을 다시
열지 않게 고쳤으며(WAL/`-shm` 재오염 방지, RED로 실제 버그 재현), durable transaction journal +
incomplete-apply 복구(실제 자식 프로세스를 강제 종료해 검증)를 추가했고, 공식 소스 조사로 New
Import의 `threads.cwd`를 대상 PC 경로로 remap하도록 고쳤다. 그리고 **실제 `.codex` 전체를 clone한
진짜 rollout 파일로 IncomingAhead(fast-forward) E2E를 처음으로 성공시켰다.** 그 뒤 GitHub 코드
리뷰에서 배포 전 고쳐야 할 Restore edge case 3개가 발견돼 **Phase 07_03(Final Restore Edge-Case
Hardening)**을 진행했다(§2 표, `docs/safe-restore-phase7.md` §12) — New rollout도 IncomingAhead
append와 같은 수준의 temp+atomic move+durability를 갖추도록 고쳤고(진짜 자식 프로세스 크래시로
검증), 완료되지 못한 이전 Apply 판정을 Codex Home별로 scope했으며(수동 Home 선택을 지원하므로),
`IncompleteApplyRecoveryService.Recover` 자신도 journal/manifest 정합성을 스스로 재검증하도록
강화했고, 같은 EXE를 두 번 실행해도 같은 Codex Home에 동시에 Apply할 수 없도록 프로세스 간
named-Mutex lock을 추가했다(진짜 두 프로세스로 검증). **Restore Core는 이제 기능을 더 바꾸지
않고 Phase 8(Release/Packaging)로 넘어간다.** 상세 스펙과 공식 `codex-rs` 소스 조사 결과는
`docs/safe-restore-phase7.md` 참고.

그 위에 **Phase 8(Release / Self-contained EXE / Final QA)**을 진행했다. 목표는 기능 추가가
아니라 "다른 사람에게 전달 가능한 v0.1.0 Windows 배포물"을 만드는 것이었다 — Restore
알고리즘/Backup V1/`ImportPlan` semantics는 전혀 바꾸지 않았고, 공개 배포 전 발견된
release-blocker 4개만 고쳤다(각각 RED→GREEN 확인): (A) New rollout이 남길 수 있는
`.cbm-restore-tmp` 잔재를 `RollbackService.Rollback`이 manifest가 아는 rollout target에서만
정확히 파생해 정리하도록 추가(임의 glob 삭제 없음, 진짜 자식 프로세스 크래시로 검증), (B)
`IncompleteApplyRecoveryService.Recover`가 lock 획득 직후·실제 Rollback 시작 직전에
`CodexProcessGuard`를 한 번 더 확인하도록 추가(첫 확인과 Rollback 사이 시간차 동안 Codex가 다시
켜졌을 가능성 대응), (C) `SnapshotService.Create`가 각 snapshot 파일과 manifest를
`Flush(flushToDisk: true)` + atomic move로 rollout/journal과 같은 durability 수준으로
publish하도록 강화, (D) `RestoreTransactionJournalStore.TryReadDetailed`가 JSON 파싱 성공만으로
"정상 journal"이라 판단하지 않고 SnapshotId/CodexHomePath/State enum/UpdatedAtUtc까지 구조적으로
검증하도록 강화(`{}` 같은 parseable-but-invalid journal을 Corrupt로 정확히 분류). 그 외에
`.github/workflows/windows-ci.yml`(Windows CI — build/test(Release) + publish artifact job)을
신설했고, `scripts/publish-release.ps1`(restore→build→test→publish→감사→ZIP→SHA-256을 한 번에
재현하는 스크립트)을 추가했으며, self-contained/single-file publish 설정을 확정하고(App
프로젝트 `PublishTrimmed=false`, `Directory.Build.props`에 `CbmReleasePublish=true`일 때만
전체 프로젝트의 PDB 생성을 억제하는 조건부 설정 추가 — 참조 프로젝트에는 `SelfContained`/
`PublishSingleFile` 같은 RID 전용 속성이 전파되지 않는다는 것을 실제로 확인한 뒤 커스텀 속성으로
우회), 실제로 발행한 self-contained/single-file `CodexBackupManager.exe`(약 136MB, 부속 파일
없음)를 직접 실행해 실제 사용자 `.codex`를 대상으로 한 Read-Only 탐지/카탈로그 생성이 정상
동작함을 확인했고(SQLite native 의존성 로드 포함), 별도의 self-contained/single-file 스모크
하네스로 같은 Restore/Backup/Codex 어셈블리 기준 New Import/IncomingAhead/Rollback을 합성 temp
Codex Home에서 재확인했다. 상세 결과는 §2 표와 `docs/release-notes-v0.1.0.md` 참고.

그 위에 **Phase 08_01(CI Stabilization / Final Release Gate)**을 진행했다. `Phase 8` 커밋을
push한 뒤 GitHub Actions Windows CI 첫 실행(run #1)이 FAIL했고, 실패한 테스트는 정확히 2건이었다
— **둘 다 timing에 의존한 테스트 자체의 문제였다(제품 코드 버그가 아니다)**, 그리고 둘 다
근본적으로 고쳤다(CI 직렬화 같은 우회책이 아니라 테스트 자체를 결정적으로 만들었다). (1)
`BackupWriterTests.대형_payload_복사_도중_취소해도...`는 "300MB 파일 + 30ms 뒤 취소"라는
wall-clock race였다 — 로컬에서 재현해 보니 CI 부하에 따라 어느 방향으로도 어긋날 수 있는 순수
타이밍 의존 테스트였다. `BackupWriter.Write`에 테스트 전용 internal 오버로드(`sourceFileOpener`
주입, `InternalsVisibleTo`로만 노출)를 추가해 "N번째 Read 직후 스스로 취소하는" 커스텀 Stream을
주입하는 방식으로 완전히 결정적으로 재현하도록 바꿨다(신규 `StreamingHashCopyTests`도 추가해
`StreamingHashCopy.CopyWithHash` 자체의 취소 처리를 저수준에서 별도로 검증). (2)
`FlowDocumentBindingRecyclingStressTests.빠른_스크롤_왕복...`은 로컬 단독 실행조차 21~22초가
걸려 30초 timeout과 거의 여유가 없었다(진짜 deadlock이 아니라 순수 CPU-bound 렌더링 작업량
문제였다 — 반복 실행 시 항상 완료됐다, 결코 멈추지 않았다). "고정된 소수의 지점으로 점프"하는
방식은 실행은 빨라지지만 **실제 컨테이너 재활용 race를 더 이상 재현하지 못한다는 것을 RED로
직접 확인**했다 — 대신 `messageCount`(600→80)를 줄여 기존의 촘촘한 40px 스텝 알고리즘을 그대로
유지한 채 실행 시간만 21~22초 → 1~3초로 줄였다(RED로 버그가 여전히 매번 재현됨을 재확인).
`FlowDocumentBinding`의 핵심 소유권 로직(이전 소유자 강제 detach) 자체를 검증하는 새 밀리초
단위 결정적 단위 테스트도 추가했다(둘 다 RED→GREEN, 최소 15회 반복 GREEN, 인위적 CPU 부하
아래에서도 GREEN 확인). CI workflow 자체(`dotnet test` 병렬 실행 정책)는 바꾸지 않았다 — 두
실패의 근본 원인이 테스트 자체의 timing 의존성이었고, 그걸 없앤 뒤에는 실제 cross-project 병렬
실행(로컬에서 재현) + 인위적 CPU 부하 아래에서도 안정적으로 GREEN이었기 때문이다(CI 직렬화는
근본 원인을 고치지 않고 증상만 가릴 뿐이라고 판단했다). Restore 알고리즘/Backup Format
V1/`ImportPlan` semantics, Phase 8의 release-blocker A~D, self-contained/single-file publish
설정은 전혀 건드리지 않았다. **이 세션은 push하지 않았으므로 실제 GitHub Actions에서 GREEN인지는
아직 확인되지 않았다** — 다음 세션(또는 사용자)이 커밋/push한 뒤 Actions 결과를 반드시 확인할
것.

---

## 1. 한 줄 요약

**Phase 1~5(Codex 탐색 → Read Model → Conversation Viewer → Selection → Export)까지 전부
완료했고, 그 위에 Phase 05_01(Backup V1 Freeze), Phase 6(Import Preview), Phase 06_01(Revision
Relation Hardening / Import Plan Finalization), Phase 06_02(Apply Preconditions Freeze / Stale
Plan Protection), Phase 06_03(Preview Source Identity Pinning), Phase 7(Safe Restore /
Fast-forward Apply Core), 그리고 Phase 07_01(Restore Hardening / Apply UI / Real-Clone E2E)까지
마쳤다.**
Phase 4는 04_01~04_07 사후 수정을 거쳐 사용자가 실제 GUI로 확인 후 최종 PASS로 확정했다. Phase 5는
Restore Sufficiency Audit → Backup Format V1 확정 → `CodexBackupManager.Backup` 프로젝트
(ExportPlanBuilder/BackupWriter/BackupReader/BackupValidator) → 최소 Export UI → 실제 `.codex`
데이터 검증까지 마쳤고, 사용자가 커밋한 뒤 GitHub 코드 리뷰에서 Restore/Validator 정합성 문제
몇 가지가 발견돼 Phase 05_01에서 고쳤다(§3 Export 항목, `docs/codexbackup-format-v1.md` §5에
전체 목록) — 이 커밋(`be2f616`)을 기준으로 Backup Format V1이 FROZEN이다.
**Phase 6은 `.codexbackup`을 선택하면 검증 → 내용 표시 → 현재 PC Codex와 실제 rollout
lineage/내용 비교(timestamp 아님) → 프로젝트 경로 재매핑 제안 → Phase 7 계획(Preview)까지만 한다.
Codex에는 write가 0건이다**(§3 "Import Preview" 항목, `docs/import-preview-phase6.md`에 전체 스펙).
Phase 6은 커밋(`52a4a8b`)됐지만, 그 뒤 코드 리뷰/재검토로 발견된 **revision relation 판정의
안전성 경계 케이스 문제**를 **Phase 06_01**(커밋 `1f29dc4`)에서 고쳤다 — segment transition
fast-forward 오판, 로컬 metadata가 있는데 chain만 없을 때 `New`로 잘못 떨어지던 문제, 수동 프로젝트
경로 재지정, `ImportPreview` → `ImportPlan`(Phase 7이 그대로 받아 쓸 freeze 경계) 확정을 포함한다.
그 뒤 다시 코드 리뷰로 "Preview 이후 backup 파일이나 로컬 Codex 상태가 바뀌어도 frozen `ImportPlan`이
이를 알아챌 방법이 없다"는 문제가 발견돼 **Phase 06_02**에서 고쳤다 — backup 파일 전체의
streaming SHA-256을 `ImportBackupIdentity`에 담아 "Preview했던 그 파일인지"를 증명 가능하게 했고,
Preview 당시 실제로 비교에 쓰인 로컬/incoming `ConversationRevision`을 `ImportPlanConversation`에
그대로 freeze해 Phase 7이 relation을 다시 판정하지 않고도 "로컬이 그때와 같은지"만 확인할 수 있게
했으며, 이 모든 걸 종합해 Ready/NotReady를 판정하는 `ImportPlanPreflightValidator`(read-only)를
추가했다(§3 "Import Preview" 항목에 상세, `docs/import-preview-phase6.md`에 전체 스펙 — **Import
Preview/`ImportPlan` contract는 Phase 06_02 검증까지 마쳐야 최종 FREEZE**, RevisionRelation 판정
semantics 자체는 Phase 06_01에서 이미 FROZEN이고 이번 Phase에서 바꾸지 않았다). **Phase 06_03**은
그 뒤 "Preview가 읽었던 backup과 `ImportPlanBuilder`가 나중에 identity를 freeze할 때 다시 읽는
backup이 서로 다른 파일일 수 있는" TOCTOU를 고쳤다 — `ImportPreview.SourceBackupIdentity`를
분석 직후 고정하고, `ImportPlanBuilder`는 그 값과 지금 파일이 정확히 같을 때만 Plan을 만든다
(다르면 Plan 생성 자체를 거부). 이 커밋들(`52a4a8b`/`1f29dc4`/`0bfc7c8`/`fcb6371`)로 Import
Preview/`ImportPlan`/Preflight contract가 **최종 FROZEN**이다.

**Phase 7(Safe Restore / Fast-forward Apply Core)**은 frozen `ImportPlan`을 실제 Codex Home에
적용하는, 이 프로젝트 최초의 실제 write 단계다. 새 `CodexBackupManager.Restore` 프로젝트가
`RestoreExecutor`(Codex 실행 확인 → fresh preflight → `RestoreOperationPlan` 생성 → Snapshot →
실제 write → post-validation, 실패 시 자동 Rollback)를 중심으로 New Import(rollout 파일 복사 +
`threads` INSERT)와 IncomingAhead의 안전한 fast-forward(같은 파일 끝에 완결된 줄 append 또는 새
segment 파일 생성 — 물리 바이트 재확인 없이는 절대 하지 않는다)만 지원한다. Diverged/Unverifiable은
여전히 Apply 전체 차단이고, Diverged 자동 merge 같은 고위험 기능은 구현하지 않았다. 공식 `codex-rs`
소스 조사 결과와 상세 스펙은 `docs/safe-restore-phase7.md` 참고 — 특히 `.codex-global-state.json`은
Electron Desktop 앱 전용 상태로 밝혀져(코어 엔진에는 이 개념 자체가 없다) 건드리지 않기로 했다.

**Phase 07_01(Restore Hardening / Apply UI / Real-Clone E2E)**은 Phase 7 커밋(`a3de8e8`)에 대한
GitHub 코드 리뷰로 발견된 실제 안전성/정합성 문제를 고치고, 처음으로 Apply UI를 연결하고, 실제
`.codex`를 clone한 데이터로 검증한 하드닝 Phase다 — Phase 7의 아키텍처 방향(계층 구조, Snapshot/
Rollback 기반 설계)은 그대로 유지했다. 고친 것: `CodexProcessGuard`의 Dispose-후-속성-읽기 버그,
Apply 안에서 backup 파일을 여러 번 다시 여는 TOCTOU(`PinnedBackupSource`로 한 번만 열어 재사용),
SQLite Snapshot이 `-wal`/`-shm`을 놓치던 문제, `.jsonl.zst` 새 segment를 논리(압축 해제) 값으로
검증해 항상 실패하던 버그(물리 값으로 교체), IncomingAhead 메타데이터 병합 정책 신설, New Import의
`thread_source` 누락을 막는 안전장치, 얕았던 post-apply validation 강화, App이 예전 카탈로그를
재사용하지 못하게 하는 fresh-catalog 소유권 재구성. `.codex-global-state.json`/Desktop 사이드바
관련 서술은 "코스메틱 한계"라는 단정을 취소하고 "Core/CLI authority는 확정, Desktop 반영은
미검증"으로 정정했다(`docs/safe-restore-phase7.md` §1.C/E/§10.6). Fault Injection 6개 지점 전부
개별 테스트로 Rollback을 확인했고, 실제 `.codex`를 clone(New/archived/segmented-New 시나리오만,
IncomingAhead는 여전히 합성 데이터로만 검증)해 실제 스키마 호환성을 검증했다 — 실제 원본에는 이
세션 전체를 통틀어 단 한 번도 쓰지 않았다(`docs/safe-restore-phase7.md` §10.4/§10.7). 상세 내용은
`docs/safe-restore-phase7.md` §10 전체 참고.

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
| `1f29dc4` | Phase 06_01 | **Revision Relation Hardening / Import Plan Finalization.** 코드 리뷰/재검토로 발견된 Phase 6 안전성 경계 케이스 문제 수정: (1) `ConversationRevisionComparer`가 "같은 rollout id, 다른 slice 내용"을 **양쪽 다 마지막 slice일 때만** prefix 검사하던 것을 **각 쪽이 독립적으로 "여기서 끝나는지"** 보도록 수정 — `Local=[R1-short]`, `Incoming=[R1-long, R2]`처럼 한쪽만 끝나고 다른 쪽이 이어지는(뒤에 segment가 더 있어도) 정상적인 fast-forward를 예전엔 Diverged로 오판했다, (2) `ImportPreviewBuilder.DetermineRelation`이 로컬에 chain만 없으면 무조건 `New`로 판정하던 것을 — 로컬 catalog(`AllConversations`, dependency-only 포함)에 같은 ThreadId metadata가 있는데 chain만 없으면(rollout 삭제/손상) `Unverifiable`(Blocked)로 승격, 정말로 metadata도 chain도 전혀 없을 때만 `New`, (3) `ProjectPathMapping`에 `ManuallyLinked` 상태 + `WithManualOverride` 추가, `ImportPreviewBuilder.ApplyManualProjectPathOverride`로 NotFound/AutoLinked 프로젝트 모두 사용자가 폴더를 직접 재지정할 수 있게(미분류 "기타 대화"만 제외) — 실제 디렉터리 존재 확인 + `CanonicalPath` 정규화, 상태는 View code-behind가 아니라 `ImportPreview`/`ProjectPathMapping` 자체에 저장, (4) `ImportPlan`/`ImportPlanBuilder` 신설 — Preview를 Phase 7이 그대로 받아 적용할 수 있는 **freeze 경계**로 확정(`IsApplyReady`는 Blocked/RequiresDecision이 하나도 없어야 참). 실제 세그먼트 thread(21MB, 974줄, 3세그먼트)로 재검증하는 과정에서 "segment1 원본 파일이 공식 인정된 lineage cutoff(ordinal<967) 이후에도 별도로 계속 쓰인 바이트(줄 967~973)를 갖고 있을 수 있다"는 새 실측 사실을 발견 — 이 경우 그 cutoff 이후 내용까지 포함한 스냅샷은 fast-forward가 아니라 정당하게 Diverged로 판정돼야 하며, 실제로 그렇게 동작함을 확인했다. |
| `0bfc7c8` | Phase 06_02 | **Apply Preconditions Freeze / Stale Plan Protection.** 코드 리뷰로 발견된 "Preview 이후 backup/로컬 상태가 바뀌어도 frozen `ImportPlan`이 이를 알아챌 방법이 없다"는 문제 수정: (1) `ImportBackupIdentity`에 `BackupFileLength`/`BackupFileSha256`(전체 `.codexbackup`의 streaming SHA-256, source of truth)/`BackupFormatVersion`을 추가 — `CreatedAtUtc`/`AppVersion`/`TotalConversationCount`는 참고용으로 격하, (2) `ImportPreviewBuilder`가 판정에 실제로 쓴 로컬/incoming `ConversationRevision`을 버리지 않고 `ImportConversationPreview.LocalRevision`/`IncomingRevision`으로 보존하도록 리팩터링, (3) `ImportConversationPrecondition`(`ExpectedPresence: MustNotExist`\|`MustExist`, `ExpectedLocalRevision`, `ExpectedIncomingRevision`) 신설 — `ImportPlanConversation`이 이걸 그대로 freeze해 갖고 있어 Phase 7이 relation을 다시 판정하지 않고도 "로컬이 그때와 같은지"만 비교할 수 있게 함, (4) `ImportPlanPreflightValidator`(read-only) 신설 — backup identity → Blocked → UnresolvedDivergence → 대화별 precondition(로컬 MustNotExist/MustExist 재확인, incoming revision 방어적 재확인) → target path(존재 확인 + canonical 재검증) 순으로 확인해 `Ready`/`BackupChanged`/`LocalStateChanged`/`TargetPathUnavailable`/`Blocked`/`UnresolvedDivergence` 중 하나를 돌려준다. RevisionRelation 판정 semantics 자체는 바꾸지 않았다. 실측으로 실제 프로젝트 하나의 `AutoLinked` 경로가 지금 이 PC에는 존재하지 않는다는 사실을 이 preflight가 최초로 발견했다(§4 참고, 버그 아님). |
| `fcb6371` | Phase 06_03 | **Preview Source Identity Pinning.** 코드 리뷰로 발견된 "Preview가 실제로 읽은 backup 파일과, `ImportPlanBuilder`가 나중에 identity를 freeze할 때 다시 읽는 backup 파일이 서로 다른 파일일 수 있다"는 TOCTOU 문제 수정: (1) `ImportPreview`에 `SourceBackupIdentity`(`ImportBackupIdentity` 재사용) 필드 추가 — `ImportPreviewBuilder.Build(string, ...)`가 분석을 마친 직후 같은 경로를 다시 streaming hash해서 고정한다, (2) `ImportPlanBuilder.Build`가 더 이상 현재 파일을 새 identity로 "채택"하지 않는다 — `preview.SourceBackupIdentity`가 있어야 하고, 지금 그 경로를 다시 hash해서 길이+SHA-256이 정확히 같을 때만 Plan을 만들며, 다르면(1바이트 변조/다른 valid backup으로 교체/내용은 같은데 metadata만 달라 hash가 다른 경우 전부) Plan 생성 자체를 거부(`null`)한다 — CreatedAt/AppVersion/개수가 우연히 같아도 소용없다, (3) `BackupIdentityHasher`(공용 hashing/비교 로직, Backup.Import) 신설로 Preview 쪽 pin과 Plan 쪽 재확인이 같은 정의를 공유, (4) 수동 프로젝트 경로 재지정(`ApplyManualProjectPathOverride`)은 `preview with { Projects = ... }`로 다른 필드를 건드리지 않으므로 `SourceBackupIdentity`는 자동으로 보존됨을 테스트로 확인, (5) `MainViewModel`에 `_currentImportPlan` 필드 신설 — Preview 성공/경로 재지정 시 딱 한 곳(`UpdateImportPlanSummary`)에서만 `ImportPlanBuilder.Build`를 부르고 그 결과를 보존, Phase 7이 이 인스턴스를 그대로 받아 fresh preflight만 돌리면 됨, (6) `ImportPlanSummaryText` 문구를 "Apply 준비 완료"에서 "가져오기 계획 생성 완료 — 충돌 없음(적용 전 최종 검사가 필요합니다)"로 정정 — `IsApplyReady`는 Diverged/Unverifiable이 없다는 뜻일 뿐 실제 "지금 적용 가능"을 의미하지 않는다는 오해를 없앤다. |
| `a3de8e8` | Phase 7 | **Safe Restore / Fast-forward Apply Core.** 이 프로젝트 최초로 실제 `.codex` write가 등장하는 Phase — 그래서 "기능을 많이 넣는 것"보다 "중간 실패가 나도 100% 원상복구"를 최우선으로 삼았다. 새 `CodexBackupManager.Restore`/`.Restore.Tests` 프로젝트 신설. 공식 `codex-rs` 소스 조사(`docs/safe-restore-phase7.md`)로 확정한 사실: (a) `threads` 테이블의 기본값 없는 NOT NULL 컬럼 9개(`rollout_path`/`created_at`/`updated_at`/`source`/`model_provider`/`cwd`/`title`/`sandbox_policy`/`approval_mode`) 실측 스키마와 공식 소스가 정확히 일치, (b) Codex 자신의 rollout writer도 이어지는 세션은 기존 파일에 그대로 append하지만 "다른 thread가 history_base 조상으로 참조 중인 파일"은 불변으로 취급함 — 그래서 append 전 이 조건을 직접 재확인하는 안전장치를 추가, (c) `.codex-global-state.json`/`threadAssignmentsMigrated`는 `codex-rs` 코어 엔진에는 존재하지 않는다 — project/thread 배정의 Core/CLI authority는 `threads.project_id`뿐이다(Desktop 사이드바가 이 값을 실제로 반영하는지는 Phase 07_01에서 "미검증"으로 재정정, 아래 참고). 계층: `CodexProcessGuard`(실행 중 프로세스 이름/경로 기반 판정) → `SchemaCompatibilityChecker`(버전 숫자가 아니라 `PRAGMA table_info` 구조 비교) → `RestoreOperationPlanner`(frozen `ImportPlan` + fresh 카탈로그 → 구체적 file/DB 연산 목록, 물리적으로 안전하지 않으면 계획 생성 자체를 거부 — 부분 성공 없음) → `SnapshotService`(`%LOCALAPPDATA%\CodexBackupManager\Snapshots\`에 temp→검증→publish) → `RestoreExecutor`(mutation 실행 + fault injection 지점 6곳) → `RestoreValidator`(post-apply 재검증) → `RollbackService`(snapshot 기준 byte-level 복구 + 재검증). 지원 범위는 `New`→Import, `Identical`→NoOp, `IncomingAhead`→안전 증명된 fast-forward만(같은 rollout id의 물리 파일 tail이 정확히 일치할 때만 plain `.jsonl`에 append, `.jsonl.zst`에는 압축 append를 절대 시도하지 않음, 새 segment 파일 생성은 안전), `LocalAhead`→Skip, `Diverged`/`Unverifiable`→Apply 전체 차단. 실제 New Import/IncomingAhead append/새 segment 생성/두 PC 왕복(New→Update→Identical)/Codex 실행 중 차단/backup 변경 차단/history_base 조상 보호/압축 파일 차단/fault injection 4곳(첫 rollout 생성 후·SQLite 커밋 직전·직후·취소) 전부 실제 temp Codex Home에 파일+SQLite write를 수행해 검증했다. GitHub 코드 리뷰에서 실제 안전성 문제가 발견돼 Phase 07_01에서 하드닝했다(아래 참고). |
| `ddfdc36` | Phase 07_01 | **Restore Hardening / Apply UI / Real-Clone E2E.** Phase 7 코드 리뷰로 발견된 문제 수정(상세: `docs/safe-restore-phase7.md` §10, 요약: `CodexProcessGuard`의 Dispose-후-속성-읽기 버그, Apply 내부 backup 재오픈 TOCTOU→`PinnedBackupSource`로 한 번만 열어 재사용, SQLite Snapshot의 `-wal`/`-shm` 누락→항상 등록, `.jsonl.zst` 새 segment 논리/물리 해시 혼동→물리 값으로 교체, IncomingAhead 메타데이터 병합 정책(`PlannedThreadMetadataUpdate`) 신설, New Import `thread_source` 누락 차단, post-validation 강화(`threads` 행/rollout_path/archived/메타데이터/project_id 실제 일치까지 확인), fresh-catalog를 `RestoreExecutor` 자신이 만들도록 재구성, Codex 실행 여부 2차 재확인, 취소(`Cancelled`)와 실제 오류(`RolledBack`)의 결과 메시지 분리). Fault Injection 6개 지점 전부 개별 테스트로 Rollback 확인. `MainViewModel`에 `ApplyCommand`/확인 dialog/진행 상태 문구/`KnownLimitationsText`를 연결해 **처음으로 Apply UI**를 완성했다(`MainViewModelApplyTests.cs` 신규). 세션 스크래치패드 하네스로 실제 `.codex`를 clone해 New/archived/segmented-New Import를 검증했다(IncomingAhead는 여전히 합성 데이터로만 검증) — 실제 원본 `.codex`에는 세션 전체에서 단 한 번도 쓰지 않았음을 `Get-FileHash`로 재확인. `.codex-global-state.json`/Desktop 사이드바 반영 여부에 대한 이전 "코스메틱 한계" 결론을 취소하고 "Core/CLI 확정, Desktop 미검증"으로 정정했다. |
| `b298140` | Phase 07_02 | **Release Safety Gate / Full-Clone E2E / Crash Recovery.** 배포 전 마지막 안전성 게이트(상세: `docs/safe-restore-phase7.md` §11). rollout append를 in-place Seek+CopyTo에서 temp+atomic replace로 전면 교체(fault injection 3곳 추가, RED→GREEN 확인) — 크래시 중이어도 원본이 반쯤 쓰이지 않는다. `RollbackService`의 post-check가 더 이상 target DB/WAL/SHM을 다시 열지 않도록 고쳤다(별도 임시 복사본만 열어 확인) — **실제로 target을 다시 여는 옛 방식이 `-shm`을 변경한다는 것을 RED로 직접 재현**했다. 공식 `codex-rs` 소스 조사로 `threads.cwd`가 resume 시 실제 작업 디렉터리 후보로 쓰일 수 있음을 확인(`resume_config.rs`) — New Import에서 project_id가 실제로 해석됐을 때만 `threads.cwd`도 대상 PC 경로로 remap하도록 변경(rollout JSONL의 `session_meta.cwd`는 여전히 손대지 않음). `RestoreTransactionJournal`(Prepared/Applying/Completed/RolledBack) + `IncompleteApplyRecoveryService`로 크래시 후 복구 메커니즘 신설 — 별도 자식 프로세스(`CodexBackupManager.Restore.CrashSim`)를 실제로 `Process.Kill()`해 진짜 강제 종료 상태를 재현하고 다음 실행이 정확히 감지/복구함을 확인했다. Snapshot 이전 취소가 "예기치 않은 오류"로 새던 버그도 고쳤다. Apply 버튼을 `IsApplyReady` 기준으로 강화하고 완료되지 못한 이전 Apply를 막는 배너/복구 버튼을 추가했다. **세션 스크래치패드 하네스로 실제 `.codex` 전체(sessions/archived_sessions 포함)를 clone해 실제 rollout 파일 기반 IncomingAhead E2E를 처음으로 성공시켰다** — byte-precise 자르기가 아니면 `Diverged`로 오판됨을 RED로 발견 후 수정. 실제 원본에는 세션 전체에서 단 한 번도 쓰지 않았다(`Get-FileHash` 반복 재확인). |
| `eca313f` | Phase 07_03 | **Final Restore Edge-Case Hardening.** Phase 07_02 커밋 이후 GitHub 코드 리뷰로 발견된 배포 전 Restore edge case 3개를 고쳤다(상세: `docs/safe-restore-phase7.md` §12). (1) `RolloutRestoreService.CreateNewFile`(New rollout)이 `FileMode.CreateNew`+`File.Move(overwrite:false)`만 쓰던 것을 IncomingAhead append와 같은 temp+`Flush(true)`+검증+atomic move 패턴으로 교체(fault injection 3곳 `DuringNewRolloutTempWrite`/`BeforeNewRolloutMove`/`AfterNewRolloutMove` 추가) — temp 작성 중 크래시로 남은 잔재가 다음 재시도를 막던 실제 crash recovery hole을 없앴다(진짜 자식 프로세스를 `BeforeNewRolloutMove`에서 강제 종료 → 복구 → 같은 backup으로 재시도까지 `Succeeded`로 end-to-end 확인). (2) `IncompleteApplyRecoveryService.FindIncompleteForHome(snapshotRoot, codexHomePath)` 신설 — 완료되지 못한 이전 Apply를 더 이상 전체 Home 통틀어 판단하지 않고 `CanonicalPath.AreSameLocation`으로 실제 겨냥했던 Home에만 scope한다(수동 Codex Home 선택을 지원하므로) — `RestoreExecutor.Apply`/`MainViewModel.RefreshIncompleteApplyState(codexHomePath)` 둘 다 이 API로 교체. (3) `IncompleteApplyRecoveryService.Recover`가 호출자를 신뢰하던 것을 스스로 재검증하도록 강화 — journal이 `Applying`이 아니면(Completed/RolledBack/Missing) 조용히 거부, journal/manifest의 SnapshotId·CodexHomePath가 어긋나거나 journal 파일 자체가 손상되면(`RestoreTransactionJournalReadStatus.Corrupt`) `RollbackFailedCritical`로 보수적으로 거부. (4) `RestoreProcessLock`(신규, Codex Home별 named Mutex `Local\CodexBackupManager.Restore.<hash>`) 도입 — 같은 EXE를 두 번 실행해도 같은 Codex Home에 동시 Apply할 수 없게 막았다(`RestoreExecutor.Apply`/`Recover` 둘 다 사용, `TimeSpan.Zero` 즉시 판정, `AbandonedMutexException`도 정상 획득으로 처리하되 바로 이어지는 incomplete-apply 검사가 이전 crash를 잡아낸다) — **진짜 두 프로세스**로 검증(`CodexBackupManager.Restore.CrashSim`에 `lock-hold` 서브커맨드 추가). 기존 Phase 07_02 동작은 전부 회귀 없이 유지했다(495건 전부 GREEN 유지 + 신규 19건 = 514건). |
| `1481627` | Phase 8 | **Release / Self-contained EXE / Final QA.** v0.1.0 Windows 배포물을 만들었다 — Restore 알고리즘/Backup V1/`ImportPlan` semantics는 전혀 바꾸지 않고, release-blocker 4개만 고쳤다(전부 RED→GREEN, 상세: `docs/safe-restore-phase7.md`는 그대로 두고 아래 §2/§4에 정리). (A) `RollbackService.Rollback`이 manifest의 `new-rollout-*`/`appended-rollout-*` 라벨에서만 `<target>.cbm-restore-tmp`를 정확히 파생해 정리(임의 glob 삭제 없음) — 진짜 자식 프로세스를 `DuringNewRolloutTempWrite`에서 강제 종료해 검증. (B) `IncompleteApplyRecoveryService.Recover`가 lock 획득 직후·Rollback 시작 직전에 `CodexProcessGuard`를 한 번 더 확인(첫 확인 이후 Codex가 다시 켜졌을 가능성 대응) — 콜 카운트를 세는 flaky processLister로 RED→GREEN 확인. (C) `SnapshotService.Create`가 각 파일과 manifest를 `Flush(flushToDisk: true)`+atomic move로 publish. (D) `RestoreTransactionJournalStore.TryReadDetailed`가 SnapshotId/CodexHomePath/State enum/UpdatedAtUtc까지 구조적으로 검증(`{}` 같은 parseable-but-invalid journal을 Corrupt로 분류). `.github/workflows/windows-ci.yml` 신설(build-and-test + publish-artifact 2개 job, push/pull_request, contents:read만). `scripts/publish-release.ps1` 신설(clean→restore→build→test→publish→감사→ZIP→SHA-256, 실제로 두 번 실행해 재현성 확인). `Directory.Build.props`에 `CbmReleasePublish=true`(Release 구성에서만) 조건부로 전체 프로젝트 PDB 생성을 억제하는 설정 추가(`SelfContained`/`PublishSingleFile`은 참조 프로젝트에 전파되지 않는다는 것을 실측으로 확인한 뒤 커스텀 global property로 우회). `AppVersionInfo`(어셈블리 버전에서 읽음)로 로그 시작/종료 문구와 창 제목을 "Codex Backup Manager 0.1.0"으로 정리. 실제 발행한 `CodexBackupManager.exe`(self-contained/single-file, 136MB, 부속 파일 없음)를 직접 실행해 실제 사용자 `.codex`를 Read-Only로 탐지/카탈로그 생성(46 projects/112 user threads/357 threads)까지 확인했고, 그 전후 원본 4개 파일 해시가 완전히 동일함을 재확인했다. 별도 self-contained/single-file 스모크 하네스(세션 스크래치패드, 커밋 안 됨)로 같은 Restore/Backup/Codex 어셈블리 기준 New Import/IncomingAhead/Rollback을 합성 temp Codex Home에서 재확인했다(전부 Succeeded/RolledBack). `.NET 미설치 clean Windows 환경 실기 검증은 수행하지 못했다`(정직하게 알려진 한계로 남김 — 아래 §4 참고). 전체 테스트 528건(514건 + 신규 14건) 전부 GREEN, 연속 2회 확인. |
| (미커밋) | Phase 08_01 | **CI Stabilization / Final Release Gate.** `Phase 8` 커밋을 push한 뒤 GitHub Actions Windows CI 첫 실행(run #1)이 FAIL — 실패 2건 전부 timing 의존 테스트 자체의 문제였다(제품 버그 아님), 둘 다 결정적으로 고쳤다. (1) `BackupWriterTests`의 "300MB + 30ms 뒤 취소" wall-clock race를 제거 — `BackupWriter.Write`에 테스트 전용 internal 오버로드(`sourceFileOpener` 주입)를 추가해 "N번째 Read 직후 스스로 취소하는" 커스텀 Stream으로 완전히 결정적으로 재현(신규 `StreamingHashCopyTests`로 `StreamingHashCopy.CopyWithHash`의 취소 자체도 저수준에서 별도 확인). (2) `FlowDocumentBindingRecyclingStressTests`가 로컬 단독 실행도 21~22초 걸려 30초 timeout과 여유가 없었던 문제 — "고정된 소수 지점으로 점프"하는 방식은 실제 컨테이너 재활용 race를 더 이상 재현하지 못함을 RED로 직접 확인했고(기각), 대신 `messageCount`(600→80)를 줄여 기존 촘촘한 40px 스텝 알고리즘을 그대로 유지한 채 실행 시간만 1~3초로 줄였다(RED로 버그가 여전히 매번 재현됨을 재확인). `FlowDocumentBinding`의 핵심 소유권 로직 자체를 검증하는 밀리초 단위 결정적 단위 테스트도 신설(둘 다 RED→GREEN, 최소 15회 반복 + 인위적 CPU 부하 아래에서도 GREEN 확인). CI workflow의 병렬 실행 정책은 바꾸지 않았다 — 근본 원인이 테스트의 timing 의존성이었고 이미 제거했으므로 직렬화는 불필요하다고 판단(실측: cross-project 병렬 + 인위적 CPU 부하 아래에서도 안정). Restore 알고리즘/Backup Format V1/`ImportPlan` semantics, Phase 8의 release-blocker A~D, publish 설정은 전혀 건드리지 않았다. 전체 테스트 531건(528건 + 신규 3건: `StreamingHashCopyTests` 2건 + `FlowDocumentBinding` 소유권 단위 테스트 1건), release script로 최종 산출물 재생성. **이 세션은 push하지 않았으므로 실제 GitHub Actions GREEN 여부는 다음 세션/사용자가 push 후 확인해야 한다.** |

**Phase 4는 사용자가 실제 GUI로 확인 후 최종 PASS로 확정했다. Phase 5(Export)는 커밋된 뒤 Phase 05_01
hardening까지 마쳤다 — `docs/codexbackup-format-v1.md`가 이제 FROZEN 상태다. Phase 6(Import
Preview), Phase 06_01(Revision Relation Hardening), Phase 06_02(Apply Preconditions Freeze /
Stale Plan Protection), Phase 06_03(Preview Source Identity Pinning)까지 커밋됐다 —
`docs/import-preview-phase6.md`가 FROZEN 상태다.** 그 위에 **Phase 7(Safe Restore / Fast-forward
Apply Core, 커밋 `a3de8e8`)과 Phase 07_01(Restore Hardening / Apply UI / Real-Clone E2E)까지
마쳤다** — 실제 write 경로와 Apply UI가 모두 생겼으므로 다음 세션은 반드시
`docs/safe-restore-phase7.md`(§10이 Phase 07_01 addendum)를 먼저 읽고, Phase 8(Release)로
넘어가기 전에 남은 제약(§4, `docs/safe-restore-phase7.md` §10.6)을 확인할 것.

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

### Import Preview — Phase 06_02 addendum(Apply Preconditions Freeze / Stale Plan Protection)

- **문제**: Phase 06_01까지의 `ImportPlan`은 "무엇을 할지"(`Relation`/`PlannedAction`)만 얼렸지, "그
  판단이 여전히 유효한지" 확인할 근거가 없었다 — Preview 이후 같은 경로의 backup 파일이 다른 것으로
  바뀌거나, 로컬 Codex가 계속 작업으로 바뀌어도 Phase 7이 알아챌 방법이 없었다.
- **Backup identity 강화.** `ImportBackupIdentity`에 `BackupFileLength`+`BackupFileSha256`(전체
  `.codexbackup` 파일의 streaming SHA-256 — **유일한 source of truth**)와 `BackupFormatVersion`을
  추가했다. `CreatedAtUtc`/`AppVersion`/`TotalConversationCount`는 참고용으로 격하했다 — 이 값들이
  우연히 전부 같아도 hash가 다르면 다른 파일로 취급해야 한다(실제로 그렇게 동작함을 테스트로 확인).
  `ImportPlanBuilder.Build`가 backup 파일을 다시 열어 `StreamingHashCopy.HashOnly`로 스트리밍
  계산한다 — 파일 전체를 메모리에 올리지 않는다.
- **판정에 쓰인 revision을 버리지 않는다.** `ImportPreviewBuilder.DetermineRelation`이 반환하던
  단순 `RevisionRelation` 대신, 실제로 계산에 쓰인 로컬/incoming `ConversationRevision`까지 함께
  돌려주도록(`RelationComputation`) 리팩터링했다. `ImportConversationPreview`에
  `LocalRevision`/`IncomingRevision` 필드를 추가해 이 값을 보존하고,
  `ImportPlanConversation.Precondition`(`ImportConversationPrecondition`)이 이걸 그대로 freeze한다 —
  Phase 7이 다시 revision을 계산할 필요 없이 "그때의 fingerprint"를 그대로 갖고 있다(요구사항 8 —
  "exact update delta 재계산 금지").
- **Precondition 구조.** `ExpectedLocalPresence`(`MustNotExist`=New, `MustExist`=그 외 전부) +
  `ExpectedLocalRevision`(계산 가능했으면) + `ExpectedIncomingRevision`(계산 가능했으면 — 특히
  IncomingAhead의 fast-forward에 필요). New는 "로컬에 metadata도 chain도 전혀 없어야 한다"만
  확인하고, 그 외는 "지금 다시 만든 로컬 revision이 frozen된 것과 완전히(RolloutId/Boundary/길이/
  해시 시퀀스) 같은지"만 비교한다 — **relation을 다시 판정하지 않는다.**
- **`ImportPlanPreflightValidator`(read-only, Backup.Import) 신설.** 우선순위: backup identity(파일
  존재+`BackupValidator`+whole-file hash 일치) → `HasBlockingIssues`(Blocked) →
  `HasUnresolvedDivergence`(UnresolvedDivergence) → 대화별 precondition(로컬 상태 재확인, incoming
  revision 방어적 재확인 — backup identity가 이미 일치했으므로 이론상 항상 통과해야 하지만 lineage
  재구성 로직 자체의 재현성까지 확인) → 프로젝트별 target path(`Directory.Exists` + `CanonicalPath`
  재확인). 전부 통과해야 `Ready`, 아니면 `BackupChanged`/`LocalStateChanged`/`TargetPathUnavailable`/
  `Blocked`/`UnresolvedDivergence` 중 하나. 이번 Phase는 이 판정 API까지만 만들었다 — 실제 Apply는
  Phase 7.
- **RevisionRelation 판정 semantics 자체는 바꾸지 않았다** — Phase 06_01에서 이미 FROZEN된 그대로다.
  이번 Phase는 그 위에 "그 판정이 여전히 유효한가"를 확인하는 계층만 추가했다.

### Import Preview — Phase 06_03 addendum(Preview Source Identity Pinning)

- **문제(TOCTOU gap)**: Phase 06_02까지는 `ImportPlanBuilder.Build(preview, backupFilePath)`가
  **그 호출 시점의** `backupFilePath`를 새로 hash해서 identity로 "채택"했다. 즉 `Preview`는 backup
  A를 읽고 relation/project/path를 전부 A 기준으로 판정했는데, 그 이후(사용자가 화면을 보는 동안)
  같은 경로의 파일이 backup B로 바뀌면, `ImportPlanBuilder`는 B를 hash해서 `Plan.Backup`에 넣어버린다
  — 결과적으로 "판정은 A 기준, identity는 B"인 뒤섞인 Plan이 만들어질 수 있었다. B가 rollout 내용은
  같고 manifest metadata만 다른 경우, Phase 06_02의 preflight(`hash(B) == Plan.Backup.hash(B)`이므로
  항상 참)조차 이 mismatch를 잡지 못한다.
- **Preview 단계에서 identity를 pin한다.** `ImportPreview`에 `SourceBackupIdentity`
  (`ImportBackupIdentity` 재사용) 필드를 추가했다. `ImportPreviewBuilder.Build(string
  backupFilePath, ...)`가 검증·분석을 마친 직후, 같은 경로를 다시 streaming hash(길이+SHA-256)해서
  이 필드에 고정한다 — "Preview가 실제로 검증/분석한 그 바이트"의 유일한 증거다. `BackupReader`로
  직접 읽는 오버로드(경로를 모른다)로 만든 Preview는 `SourceBackupIdentity`가 `null`이다.
- **`ImportPlanBuilder`는 identity를 새로 "채택"하지 않는다.** `preview.SourceBackupIdentity`가
  없으면(`null`) Plan을 만들지 않는다. 있으면, `backupFilePath`를 **지금** 다시 streaming hash해서
  그 값과 길이+SHA-256이 **정확히** 같은지 확인하고, 같을 때만 그 `sourceIdentity`를 그대로
  `Plan.Backup`으로 재사용한다(다시 계산하지 않는다 — 이미 같음을 확인했으므로). 다르면(1바이트
  변조/같은 경로에 다른 valid backup으로 교체/`CreatedAt`·`AppVersion`·개수까지 우연히 같지만 hash만
  다른 경우 전부 포함) Plan 생성 자체를 `null`로 거부한다 — "새 파일을 현재 source로 다시 freeze"하지
  않는다. 사용자는 `[백업 불러오기]`부터 다시 해야 한다. 이 hash 계산/비교는
  `BackupIdentityHasher`(내부, Backup.Import)로 Preview 쪽 pin과 공유해 "언제 계산했든 같은 정의"임을
  보장한다.
- **수동 경로 재지정은 identity에 영향을 주지 않는다.** `ApplyManualProjectPathOverride`는
  `preview with { Projects = updatedProjects }`로 `Projects`만 바꾸므로, `SourceBackupIdentity`는
  레코드의 `with` 의미론에 의해 자동으로 그대로 보존된다(별도 코드 불필요 — 테스트로 확인).
- **`MainViewModel`이 frozen `ImportPlan`을 보존한다.** `_currentImportPlan` 필드를 신설해
  `UpdateImportPlanSummary`(Preview 성공 시, 그리고 경로 재지정 시)가 유일하게
  `ImportPlanBuilder.Build`를 부르는 곳이 되고, 그 결과를 저장한다 — 문구를 다시 보여줘야 할 때도
  Plan을 다시 만들지 않는다. Phase 7은 이 인스턴스(내부 접근자 `MainViewModel.CurrentImportPlan`,
  `InternalsVisibleTo`로 테스트에만 노출)를 그대로 받아 Apply 직전 `ImportPlanPreflightValidator`만
  fresh하게 다시 돌리면 된다 — Preview를 재해석하거나 Plan을 재생성하지 않는다.
- **"Apply 준비 완료" 문구를 정정했다.** `ImportPlan.IsApplyReady`는 Diverged/Unverifiable이 없다는
  뜻일 뿐, backup 파일이나 로컬 Codex가 지금(화면을 보고 있는 이 순간) Preview 때와 같은지는 전혀
  모른다 — 실제로 Phase 06_02 실측에서도 `IsApplyReady=true`인 Plan이 fresh preflight에서
  `TargetPathUnavailable`이 나온 적이 있었다. 그래서 `ImportPlanSummaryText`를 "Apply 준비
  완료(Phase 7에서 사용)."에서 "가져오기 계획 생성 완료 — 충돌 없음(적용 전 최종 검사가
  필요합니다)."로 바꿨다. 실제 "적용 가능"은 Phase 7이 fresh `ImportPlanPreflightValidator.Validate`로
  `Ready`를 확인했을 때만 표현해야 한다.

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
15. **(Phase 06_02, 실측 발견)** 실제 `.codex` 데이터로 `ImportPlanPreflightValidator`를 돌려 보니, "아무것도 안 바뀐" 자기 자신 재비교인데도 `Ready`가 아니라 `TargetPathUnavailable`이 나왔다 — 원인은 버그가 아니라, 실제 17개 프로젝트 중 하나(`AutoLinked`로 자동 연결된 프로젝트)가 `.codex-global-state.json`에 기록된 경로 자체가 지금 이 PC에는 존재하지 않는 폴더이기 때문이었다(진단 로그로 프로젝트 표시 이름만 출력해 확인, 실제 경로는 출력하지 않음). `ProjectPathMapper.Resolve`는 canonical path 문자열 비교만 하고 `Directory.Exists`는 확인하지 않으므로, Phase 6/06_01의 Preview는 이 상태를 몰랐고 **이번 Phase의 `ImportPlanPreflightValidator`가 이 사실을 실제로 최초로 잡아낸 것** — 이미 존재하던 실제 환경 조건이지 이번 Phase가 만든 회귀가 아니다. Phase 7은 이런 프로젝트를 만나면 해당 대화들의 `TargetProjectPath`를 재지정(폴더 다시 선택)하도록 사용자에게 요구해야 한다. **(Phase 06_03에서 재확인)** 같은 실제 프로젝트("test")가 여전히 같은 이유로 `TargetPathUnavailable`을 낸다 — Phase 06_03은 이 계층을 건드리지 않았으므로 회귀가 아니라 그대로다.
16. **(Phase 06_03)** `ImportPreviewBuilder.Build(BackupReader, ...)` 오버로드(경로를 모른 채 이미 연 reader로 직접 Preview를 만드는 경로)로 만든 `ImportPreview`는 `SourceBackupIdentity`가 항상 `null`이다 — 이 Preview로는 `ImportPlanBuilder.Build`가 항상 `null`을 돌려준다(Plan을 만들 수 없음). 현재 실제 코드 경로(`MainViewModel`/모든 테스트)는 전부 경로 기반 `Build(string, ...)` 오버로드만 쓰므로 문제가 되지 않지만, 향후 이 reader 오버로드를 외부에 새로 노출할 일이 있으면 이 제약을 기억할 것.
17. **(Phase 7, Phase 07_01에서 정정)** `.codex-global-state.json`은 건드리지 않기로 확정했다 —
    공식 `codex-rs` 소스에 이 파일/`threadAssignmentsMigrated` 개념 자체가 없고, 코어 엔진은
    `threads.project_id`만 authoritative로 본다(`docs/safe-restore-phase7.md` §1.C/E). **다만
    "Codex Desktop 앱 사이드바에는 코스메틱 한계로 반영이 안 될 수 있다"는 이전 결론은 Phase 07_01에서
    취소했다** — Electron Desktop 소스를 조사하지 못했으므로 Desktop이 `threads.project_id`를 실제로
    반영하는지는 **미검증**이며, Import된 대화가 Desktop 사이드바에서 올바른 프로젝트로 보이는지는
    별도 통합 검증이 필요한 알려진 Release 한계다(`docs/safe-restore-phase7.md` §10.6). **Phase
    07_02**에서도 안전한 격리 방법(예: `CODEX_HOME`을 격리된 clone으로 지정해 Desktop 실행)이
    공식적으로 확인되지 않아 시도하지 않았다 — `CODEX_HOME`은 Codex **CLI**가 인식하는 것으로
    실측 확인됐지만 **Desktop**(Electron)이 같은 변수를 존중하는지는 확인된 바 없다(`docs/safe-restore-phase7.md`
    §11.7). 확실한 방법이 확인되기 전까지는 실제 사용자 Desktop 상태를 건드리는 시도를 하지 않는다.
18. **(Phase 7, Phase 07_01에서도 미구현)** 로컬에 아직 등록되지 않은 프로젝트(예: 사용자가 수동으로
    고른, Codex가 모르는 새 폴더)로는 New Import 시 프로젝트 배정을 하지 않는다 — "기타 대화"로
    들어간다. `projects`/`project_roots` 테이블에 새 프로젝트를 만드는 것은 Phase 07_01에서도 범위
    밖이었다(Phase 8 이후 후보).
19. **(Phase 7)** attachment(`local_image` 등) 실제 파일 복원은 지원하지 않는다(Unsupported) — rollout JSONL 내부의 attachment 경로를 그대로 두고 파일 자체는 옮기지 않는다. 공식 소스 조사 결과 이미 기록된 rollout 안의 이미지 콘텐츠는 재개 시 다시 읽지 않는(이미 base64로 내재화돼 있는) 것으로 확인됐으므로, 대화 자체의 정상 재개/이어쓰기에는 영향이 없다 — "히스토리 편집" 같은 드문 조작에서만 원본 파일을 못 찾을 수 있다.
20. **(Phase 7 → Phase 07_01 부분 해소 → Phase 07_02에서 완전 해소)** 실제 사용자 `.codex`를 그대로
    clone해 Restore E2E를 돌리는 것은 Phase 7에서는 하지 않았다. Phase 07_01에서는 상태 DB 등 4개
    파일만 clone(`sessions`/`archived_sessions`는 비운 채)해 New/archived/segmented-New Import만
    검증했다. **Phase 07_02에서 `sessions`/`archived_sessions`를 포함한 `.codex` 전체를 clone해
    실제 rollout 파일 기반 IncomingAhead(fast-forward) E2E를 처음으로 성공시켰다** —
    `RestoreExecutor.Apply`가 `Succeeded`를 반환했고 결과 rollout이 원본(newer clone)과
    byte-for-byte 동일함을 확인했다(`docs/safe-restore-phase7.md` §11.2). 자르기를 `File.WriteAllLines`로
    했다가 줄바꿈 바이트가 바뀌어 `Diverged`로 오판되는 실제 버그를 RED로 발견해 byte-precise
    truncation으로 고쳤다 — 이 자체가 "rollout 바이트는 절대 바꾸면 안 된다"는 원칙의 실제 데이터
    재확인이다. segment-transition IncomingAhead의 실제 clone 재현과 물리적으로 불안전해 Blocked돼야
    하는 실제 사례는 이번에도 만나지 못해 미검증으로 남는다(`docs/safe-restore-phase7.md` §11.9).
21. **(Phase 07_02, 공식 소스 조사)** New Import의 `threads.cwd`는 이제 `project_id`가 실제로
    해석됐을 때만 대상 PC의 remap된 프로젝트 경로로 바뀐다 — 공식 `codex-rs` 소스
    (`resume_config.rs`)에서 `threads.cwd`가 존재 여부 확인 없이 실제 resume 작업 디렉터리 후보로
    쓰일 수 있음을 확인했기 때문이다(`docs/safe-restore-phase7.md` §11.4). rollout JSONL의
    `session_meta.cwd`는 여전히 손대지 않는다 — 이 remap은 SQLite 컬럼에만 적용되고, 향후 Codex
    자체의 backfill이 SQLite `cwd`를 rollout에서 다시 파생시킬 가능성은 배제되지 않는 알려진 한계다.
22. **(Phase 07_03, GitHub 코드 리뷰로 발견 → 완전 해소)** New rollout(`RolloutRestoreService.CreateNewFile`)이
    IncomingAhead append와 달리 `FileMode.CreateNew`+`File.Move(overwrite:false)`만 써서, temp
    작성 중 크래시가 나면 남은 temp 잔재가 다음 재시도 자체를 `IOException`으로 막을 수 있었다 —
    append와 같은 temp+`Flush(true)`+검증+atomic move 패턴으로 고쳤다(진짜 자식 프로세스 크래시로
    재현·복구·재시도까지 end-to-end 확인, `docs/safe-restore-phase7.md` §12.1). 완료되지 못한 이전
    Apply 판정(`IncompleteApplyRecoveryService`)이 Codex Home을 구분하지 않아, 수동으로 여러 Home을
    오가며 쓰는 이 프로그램의 실제 사용 패턴과 맞지 않았던 문제도 `FindIncompleteForHome`으로
    scope해 고쳤다(§12.2). `Recover` 자신이 journal/manifest 정합성을 스스로 재검증하도록
    강화했다(§12.3). 같은 EXE를 두 번 실행하면 두 프로세스가 같은 Codex Home에 동시에 Apply할 수
    있었던 문제는 `RestoreProcessLock`(named Mutex, `Local\` 세션 범위)으로 막았고 진짜 두
    프로세스로 검증했다(§12.4) — 이 lock은 같은 Windows 로그인 세션 안에서만 유효하다는 것이 알려진
    한계로 남는다.
23. **(Phase 8)** self-contained/single-file publish는 `CodexBackupManager.exe`를 실제로 발행하고
    직접 실행해(실제 사용자 `.codex`를 Read-Only로 탐지/카탈로그 생성) 확인했고, 별도
    self-contained/single-file 스모크 하네스로 같은 Restore 어셈블리 기준 New Import/IncomingAhead/
    Rollback도 재확인했다. 다만 **.NET Desktop Runtime이 전혀 설치되지 않은 clean Windows
    환경(Windows Sandbox/별도 VM)에서의 실기 검증은 수행하지 못했다** — 이 세션은 개발 머신에서만
    실행했고, 그 머신에는 이미 .NET SDK가 설치되어 있어 "런타임 미설치 환경에서도 정말 동작하는가"
    자체는 self-contained publish의 구조(모든 런타임 파일이 exe 안에 번들됨)로 미루어 짐작할 뿐
    직접 실기 확인하지는 않았다. 배포 EXE는 code-signing되지 않았다 — 처음 실행 시 Windows
    SmartScreen 경고가 뜰 수 있다(README/`docs/dist-readme.txt`에 명시).

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
| `CodexBackupManager.Backup.Tests` (Phase 5 신규, Phase 05_01/6/06_01/06_02/06_03 확장) | `ExportPlanBuilder`(dependency closure/dedupe/attachment/project 필터링 + **선택 대화 chain/metadata/ancestor/순환 누락 시 FatalErrors**), `BackupWriter`/`BackupReader`/`BackupValidator`(정상 export, 대상 파일 존재 시 시작 전 실패, 취소 시 temp 삭제 — 즉시 취소 + **(Phase 08_01) 스트리밍 mid-copy 취소**(테스트 전용 `sourceFileOpener` internal 오버로드로 주입한 커스텀 Stream이 N번째 Read 직후 스스로 취소 — 예전 "300MB+30ms" wall-clock race를 제거하고 완전히 결정적으로 재현), Export 도중 원본 변경 감지 — 실제 파일 레이스로 재현, 100MB 스트리밍 bounded-memory, 변조 탐지, manifest 누락/버전 불일치/중복 entry/path traversal/참조 누락 검증 실패, **Windows 대소문자 충돌/checksums.json 자체 중복·불안전 경로(예외 없이 Fail)/manifest 개수 필드 불일치/project 참조 무결성/manifest.json 자체 변조 탐지**), **(Phase 08_01, 신규 파일)** `StreamingHashCopyTests`(2건 — `StreamingHashCopy.CopyWithHash` 자체가 지정한 Read 횟수 이후 취소되면 정확히 `OperationCanceledException`을 던지는지, 취소 없이는 전체를 정확히 복사/해시하는지), `ImportPreviewBuilder`(실제 Export 파이프라인으로 만든 진짜 `.codexbackup`으로 New/Identical/IncomingAhead/LocalAhead/Diverged/Unverifiable 전부 재현, 여러 대화 혼합, dependency-only 분리, malformed backup → Preview 생성 금지, **Import Preview 동안 로컬 파일 수정 0건 확인**, 어떤 project에도 속하지 않은 선택 대화가 사라지지 않는지, **(Phase 06_01)** 로컬 metadata는 있는데 chain만 없으면 New가 아니라 Unverifiable, 반대로 metadata도 chain도 없어야만 진짜 New, dependency-only도 같은 원칙), `ProjectPathMapperTests`/`MetadataDifferenceAnalyzerTests`(canonical path 일치/불일치, metadata 필드별 차이), **`ImportPlanBuilderTests`**(Phase 06_01, 실패 Preview→null, New만 있으면 ApplyReady+BackupIdentity 일치, Diverged 있으면 HasUnresolvedDivergence, Unverifiable 있으면 HasBlockingIssues, TargetProjectPath가 project 소속 대화에만 채워지고 dependency-only는 null), **`TwoPcRoundTripTests`**(Phase 06_01 요구사항 5, temp fixture로 5단계 PC A↔B 왕복 시나리오 전체 재현 — IncomingAhead→Identical→LocalAhead→Diverged), 수동 경로 재지정 5건(NotFound/AutoLinked 재지정→ManuallyLinked, "기타 대화" 거부, 존재하지 않는 폴더/projectId 거부), **`ImportPlanPreflightValidatorTests`**(Phase 06_02, 14건 — backup 불변→Ready, backup 1바이트 변조/다른 valid backup으로 교체/CreatedAt·AppVersion·개수는 우연히 같지만 hash만 다름 3가지 모두 BackupChanged, New 이후 로컬에 같은 ThreadId 생김/IncomingAhead 이후 로컬이 이어써짐/다른 branch로 바뀜 3가지 모두 LocalStateChanged, IncomingAhead 미리보기 그대로면 Ready, 대상 프로젝트 폴더 삭제→TargetPathUnavailable, projectless 대화는 경로 확인 없이 정상, Diverged/Unverifiable 있으면 Ready 아님, preflight 검증 중 backup/로컬 파일 수정 0건, 24MB급 대형 rollout도 whole-file streaming hash로 정상 Ready 판정), **`PreviewSourceIdentityPinningTests`**(Phase 06_03, 8건 — Preview와 같은 backup으로 Plan 생성 성공, backup 1바이트 변조/다른 valid backup으로 교체/rollout 내용은 같은데 manifest metadata만 다름/CreatedAt·AppVersion·개수까지 같아도 hash만 다름 4가지 모두 Plan 생성 실패(`null`), 수동 경로 재지정 후 backup이 그대로면 성공(identity도 그대로 보존), 수동 경로 재지정 동안 backup이 바뀌면 실패, Plan 생성 후 backup이 바뀌는 경우는 기존 Preflight가 BackupChanged로 잡아냄을 재확인) | 전부 합성 임시 파일, 실제 사용자 데이터 없음 |
| `CodexBackupManager.Codex.Tests` (Phase 6/06_01 확장) | 기존 항목 전부 + `ConversationRevisionComparerTests`(Identical/IncomingAhead/LocalAhead/Diverged 전 조합, 세그먼트 추가, parent cutoff 경계(ordinal/byte 둘 다 off-by-one까지), `.jsonl` vs `.jsonl.zst` 동일 내용, 같은 ThreadId·다른 lineage → Diverged, `ConversationRevisionBuilder`의 Unverifiable 판정 — 체인 없음/순환/조상 누락, **(Phase 06_01)** 한쪽만 끝나고 다른 쪽은 segment가 더 있어도 진짜 prefix면 IncomingAhead/LocalAhead(양방향), 진짜 prefix가 아니면 segment가 있어도 Diverged, 3개 이상 segment, `.jsonl.zst` 논리 바이트로도 동일 원칙 확인, 조상이 있는 체인에서도 leaf 자신의 segment transition이 같은 원칙으로 동작) | `ConversationTranscriptBuilder`/`ThreadDependencyResolver` 리팩터링(`ResolveFileSlices` 공유) 후에도 기존 178건 전부 회귀 없이 통과 확인 |
| `CodexBackupManager.App.Tests` (Phase 06_03 확장) | ViewModel(Selection tri-state, Viewer/Selection 독립성, 카탈로그 refresh, `CompactSummaryText`, `MainViewModelExportTests`(Phase 5, 실제 fixture Codex Home으로 전체 Export 파이프라인 end-to-end 검증), `MainViewModelImportPreviewTests`(Phase 6, 실제 fixture Codex Home 2개로 Export→Import Preview 왕복 — 전부 Identical 확인, malformed backup 처리, **(Phase 06_01)** 실제 fixture의 진짜 프로젝트(Alpha)를 폴더 선택으로 수동 재지정 → ManuallyLinked, **(Phase 06_03)** Preview 성공 시 `MainViewModel.CurrentImportPlan`(내부 접근자)이 frozen `ImportPlan`을 보존하고 문구가 "Apply 준비 완료"가 아닌 중립적 표현인지, 경로 재지정 후에도 `Plan.Backup`의 backup identity(SHA-256/길이)가 그대로 유지되는지, **(Phase 7)** 새 Preview를 시작하는 순간 기존 frozen Plan/문구가 즉시 무효화되는지), Markdown-lite 파서/렌더러, **`FlowDocumentBindingRecyclingStressTests`**(실제 STA 스레드에서 `Window`+가상화 `ListBox`+`RichTextBox`를 띄우고 왕복 스크롤 — `System.Windows.Application`은 프로세스당 하나만 만들 수 있어 `Dispatcher.Run()`만 쓴다. **(Phase 08_01)** `messageCount`를 600→80으로 줄여(기존 촘촘한 40px 스텝 알고리즘은 그대로) 로컬 21~22초→1~3초로 단축하면서도 회귀 재현력은 그대로 유지(RED로 확인) — 같은 파일에 `FlowDocumentBinding`의 핵심 소유권 로직(이전 소유자 강제 detach)만 별도로 검증하는 밀리초 단위 결정적 단위 테스트도 추가), **`DarkScrollBarOrientationTests`**(`App.xaml` 원본 마크업에서 ScrollBar 스타일+의존 리소스만 오려내 독립 `ResourceDictionary`로 파싱, STA 스레드에서 실제 `Track.Orientation`/커맨드 검증 — `Application` 인스턴스 없이 진행) | `net10.0-windows`+`UseWPF`, `InternalsVisibleTo`로 `MainViewModel.Selection`/`MainViewModel.CurrentImportPlan` 접근 |
| **`CodexBackupManager.Restore.Tests`(Phase 7 신규, Phase 07_01/07_02/07_03/8 확장 — 15건→30건→47건→65건→79건)** | `RestoreExecutorTests`(54건, Phase 8에서 journal 구조 검증 5건(Theory 4 InlineData + 빈 객체 1건) + 정상 journal 회귀 1건 + Recover 2차 ProcessGuard 확인 1건 추가) — New Import 실제 파일+SQLite 행 생성 확인, Identical→write 0, Codex 실행 중→write 0, backup 변경→write 0, Diverged→write 0, IncomingAhead plain jsonl 안전 append, IncomingAhead 새 segment 생성(+`rollout_path` 갱신), 압축(.jsonl.zst) update는 Blocked, 물리 파일에 논리 cutoff 이후 여분 바이트가 있으면 전체 거부, 다른 대화의 history_base 조상으로 참조되는 파일에는 append 안 함, fault injection 6개 지점 전부 개별 Rollback 확인, 취소는 mutation 전/rollout 생성 후/append 후/커밋 후 각각 `Cancelled`로 구분, Two-PC 왕복 + Diverged write 0, `CodexProcessGuardTests`(7건, 실제 spawn된 프로세스 포함 — 별도 파일), **(Phase 07_02)** atomic append fault injection 3건(temp 작성 중/replace 직전/직후) + 대형 파일 스트리밍 append 1건, New Import cwd remap 2건(해석됨/안됨), durable transaction journal 6건(사전 취소 write 0/Completed·RolledBack journal/incomplete-apply 새 Apply 거부/Recover가 Codex 실행 중이면 거부/Recover 정상 복구), **(Phase 07_03)** New rollout temp/atomic move fault injection 3건 + stale temp 재시도 성공 1건, `FindIncompleteForHome` Home-scope 2건(다른 Home 안 돌려줌/다른 Home Apply 안 막음), `Recover` consistency gate 6건(Completed·RolledBack 재복구 거부, journal/manifest SnapshotId·CodexHomePath 불일치 거부, 호출자 기대 Home 불일치 거부, 손상된 journal 보수적 거부), `RollbackServiceShmSafetyTests`(3건, WAL+SHM/WAL만/둘다없음 — Rollback 후 target이 snapshot과 byte-for-byte 동일한지 직접 검증, live target을 다시 열던 옛 코드로는 실제로 실패함을 RED로 확인), `CrashRecoveryIntegrationTests`(4건 — 기존 atomic-replace/SQLite-commit 크래시 2건 + **(Phase 07_03)** New rollout temp-생성-후-move-전 크래시→복구→같은 backup 재시도 성공 1건 + 같은 Codex Home 동시 Apply 차단/다른 Home 비차단을 **진짜 두 프로세스**(`CrashSim`의 신규 `lock-hold` 서브커맨드)로 검증하는 1건, 전부 별도 `CodexBackupManager.Restore.CrashSim` 자식 프로세스를 실제 `Process.Kill()`/named Mutex로 조작), **(Phase 07_03 신규 파일)** `RestoreProcessLockTests`(4건, 단일 프로세스·다중 스레드로 named Mutex 소유권 규칙 검증 — 같은 Home 한쪽만 획득/다른 Home 동시 획득/Abandoned 처리/대소문자·`\\?\` prefix 무관), **(Phase 8, release-blocker A, 신규 파일)** `RollbackStaleTempCleanupTests`(3건 — New rollout/append가 남긴 stale temp를 Rollback이 정확히 지우는지, rollout이 아닌 라벨 옆의 동일 이름 파일은 임의로 지우지 않는지), **(Phase 8, release-blocker C, 신규 파일)** `SnapshotServiceTests`(3건 — 대형 파일 snapshot의 hash 일치, manifest.json이 정상 publish돼 재로딩 가능한지 + temp 파일 잔재 없음, 존재하지 않는 파일은 ExistedBefore=false), **(Phase 8, release-blocker A, `CrashRecoveryIntegrationTests` 추가 1건)** New rollout temp 작성 중(`DuringNewRolloutTempWrite`) 진짜 자식 프로세스 강제 종료 → 복구가 derived temp까지 지우는지 → 같은 backup 재시도 성공까지 end-to-end 확인(총 5건) | 전부 `TestCodexHomeBuilder`(실측 threads 스키마 그대로 반영)로 만든 합성 temp Codex Home, 실제 사용자 `.codex`는 이 프로젝트의 자동화 테스트 어디에서도 열지 않는다(§7/§11.2 clone 하네스는 세션 스크래치패드일 뿐 이 테스트 프로젝트에 포함되지 않는다) |
| **`CodexBackupManager.App.Tests`(Phase 07_01/07_02/07_03 확장 — `MainViewModelApplyTests.cs`, 4건→8건→9건)** | `ApplyCommand.CanExecute`가 `IsApplyReady`까지 보는지, 확인 dialog에서 거부하면 아무 것도 호출되지 않는지(RED→GREEN), 완전히 동일한 두 fixture Codex Home 사이의 Apply가 `NothingToDo`로 끝나고 Plan을 무효화하는지, `KnownLimitationsText`가 항상 채워져 있는지, **(Phase 07_02)** 완료되지 못한 이전 Apply가 있으면 `ApplyCommand`가 비활성화되는지(RED로 확인), [이전 상태로 복구] 승인 시 실제로 해소되고 target 파일이 원래대로 복원되는지, **(Phase 07_03)** 서로 다른 두 fixture Codex Home 사이를 오갈 때 다른 Home의 미완료 Apply가 지금 화면에 나타나지 않고 그 Home을 다시 선택하면 나타나는지(snapshot/journal 자체는 그대로 보존되는지도 확인) | 실제 fixture Codex Home(`RepositoryFixtures.CopyCodexHomeFixtureToTemp`) 2개로 Export→Import Preview→Apply 왕복, 실제 사용자 `.codex`는 쓰지 않는다 |

마지막 전체 실행 결과(Phase 08_01 포함): `Domain 64 + Codex 200 + Backup 90 + Restore 79 + App 98 =
531건 전부 통과`, cross-project 병렬 실행 + 인위적 CPU 부하 아래에서도 반복 GREEN 확인,
`dotnet build -c Release`도 경고/오류 0. 의존 방향은
`App → Restore → Backup → Codex → Domain`(단방향, 역방향 없음).

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
`CodexDetectionService` → `CodexCatalogBuilder.Build` → `ConversationTranscriptBuilder.Build` 순으로 실제 카탈로그/transcript를 만들고, 대화 원문은 출력하지 않고 개수/해시/구조 메타데이터만 출력하는 방식을 계속 써왔다. Phase 6에서는 여기에 `ExportPlanBuilder`/`ManifestBuilder`/`BackupWriter`로 실제 선택 대화(최대 40개, 세그먼트/분기 사례 우선 포함)를 진짜 `.codexbackup`으로 만들고, `ImportPreviewBuilder.Build`로 그 backup을 같은 카탈로그와 다시 비교(자기 자신이므로 전부 `Identical`이어야 함)하는 절차를 추가했다 — 실제 rollout 파일 하나를 복사해 앞부분만 자르거나(IncomingAhead/LocalAhead 재현) 뒷부분을 다르게 바꿔(Diverged 재현) 합성 스냅샷도 실제 파일 기반으로 만들었다. **Phase 06_01**에서는 실제 3-segment thread(21MB, segment1 974줄)를 골라, segment2의 실제 history_base cutoff(ordinal&lt;967)까지만 정확히 자른 segment1 스냅샷을 "로컬"로 써서 진짜 segment-transition IncomingAhead를 재현했다 — 원본 segment1 파일을 그대로(자르지 않고) 썼을 때는 원본이 cutoff 이후에도 더 쓰인 바이트를 갖고 있어 오히려 Diverged가 정확한 답이라는 것도 함께 확인했다(§4-13). **Phase 06_03**에서는 실제 Preview(40개 선택)로 만든 backup 파일을 별도 scratch 복사본으로 만들어 Preview→Plan을 정상적으로 통과시킨 뒤, 그 복사본만 1바이트 변조해 "Preview 이후 같은 경로의 파일이 바뀌면 Plan 생성 자체가 거부되는지"를 확인했다 — 실제 backup 데이터 기준으로 예상대로 동작했다(§4-16 근처 결과 참고, `.codex` 원본은 여전히 건드리지 않는다).

---

## 7. 다음에 할 일이 주어지면

Phase 7(`a3de8e8`), Phase 07_01(`ddfdc36`), Phase 07_02(`b298140`), Phase 07_03(`eca313f`), Phase
8(`1481627`), Phase 08_01(CI Stabilization / Final Release Gate, 이 문서 갱신 시점 기준 아직
미커밋)까지 완료했다 — `docs/safe-restore-phase7.md`가 Restore Core의 정식 스펙이다(§10이 Phase
07_01, §11이 Phase 07_02, §12가 Phase 07_03 addendum — **Phase 8/08_01은 이 문서를 건드리지
않았다**, Restore 알고리즘/Backup V1/`ImportPlan` semantics를 전혀 바꾸지 않았기 때문이다).
v0.1.0 self-contained/single-file `CodexBackupManager.exe`를 실제로 발행해 직접 실행까지
확인했고, `.github/workflows/windows-ci.yml`/`scripts/publish-release.ps1`/
`docs/release-notes-v0.1.0.md`가 Phase 8에서 새로 생겼다. **중요**: `Phase 8` 커밋을 push한 뒤
실제 GitHub Actions Windows CI 첫 실행이 FAIL했다(테스트 2건, timing 문제 — Phase 08_01에서
결정적으로 고쳤다). **다음 세션이 이어받으면 먼저 확인할 것**:

- **`v0.1.0` Tag/GitHub Release는 아직 만들면 안 된다** — 실제 GitHub Actions GREEN을 아직
  확인하지 못했다(이 세션은 push하지 않았다). 다음에 할 일: (1) 사용자가 이 작업 트리를 검토 후
  `Phase 08_01`로 커밋/push, (2) **실제 GitHub Actions 결과를 반드시 확인** — GREEN이면 그제야
  (3) 사용자가 확인한 뒤 실제 `v0.1.0` 태그/GitHub Release/ZIP+SHA256SUMS 업로드(이 세션은
  태그/Release/Push를 전혀 만들지 않았다 — 명시적 요청이 없었다). GitHub Actions가 여전히 FAIL
  한다면 Phase 08_01의 "실제 원인 분리 → deterministic화" 접근을 그대로 반복할 것 — timeout만
  늘리거나 테스트를 skip하는 임시방편은 쓰지 말 것(이 세션이 이미 그렇게 하지 않기로 결정한
  이유는 아래 Phase 08_01 커밋 설명과 §2 표 참고).
- `docs/project-status-and-handoff.md` §4 항목 23(알려진 한계, Phase 8)과
  `docs/safe-restore-phase7.md` §12.7(Phase 07_03 시점 한계, Phase 8이 바꾸지 않음)을 먼저 읽는다.
  특히 정직하게 남은 항목들: **.NET 미설치 clean Windows 환경 실기 검증 미수행**(Windows
  Sandbox/별도 VM 필요 — 이 세션은 개발 머신에서만 실행했다), **code-signing 안 됨**(SmartScreen
  경고 가능), Codex Desktop 사이드바 미검증, segment-transition IncomingAhead 실제 clone
  미검증, `Diverged` 자동 병합/새 프로젝트 자동 생성/`.jsonl.zst` Update 미지원, Snapshot 자동
  삭제 없음(README/`docs/dist-readme.txt`에 이미 안내함), `RestoreProcessLock`은 같은 Windows
  로그인 세션 범위.
- v1.1 이후 후보로 남을 만한 것들(이번 Phase가 일부러 손대지 않은 것): Diverged 자동
  merge/backup 교체/복사본 가져오기 UI, Codex Desktop 사이드바 실측 검증(안전한 격리 방법이
  공식적으로 확인되면), Snapshot 자동 정리 정책, code-signing, `.NET` 미설치 clean 환경 실기
  검증.
- `CodexBackupManager.Restore` 프로젝트의 안전 원칙(계획 생성 자체를 거부하면 write 0건, Snapshot
  없이는 아무것도 안 씀, 물리 안전성 재확인 없이 append/새 rollout 생성 안 함, backup은
  `PinnedBackupSource`로 Apply 전체에서 한 번만 읽음, rollout append/New rollout 생성 둘 다 항상
  temp+atomic move(+Rollback이 그 잔재까지 정리), Rollback 검증은 절대 live target을 다시 열지
  않음, durable transaction journal로 crash 복구 가능, incomplete-apply/`Recover`는 항상 Codex
  Home별로 scope되고 Core가 스스로 journal(구조까지)/manifest 정합성을 재검증하며 lock 획득 직후
  ProcessGuard를 한 번 더 확인함, 같은 Codex Home에는 프로세스가 달라도 동시에 Apply할 수 없음
  (`RestoreProcessLock`))을 그대로 신뢰하고 그 위에 기능을 더할 것 — 이미 있는 안전장치를 우회하거나
  다시 만들지 말 것.
- **실제 `.codex`로 검증할 때도 여전히 read-only/clone-only 원칙을 지킨다** — 자동화 테스트는 절대
  실제 원본 `.codex`에 write하지 말 것. Phase 8에서도 실제 발행한 EXE를 실행해 Read-Only 탐지까지
  했고, 매번 `Get-FileHash`로 원본 4개 파일(`state_5.sqlite`/`session_index.jsonl`/
  `.codex-global-state.json`/`config.toml`)의 불변을 재확인해 왔다 — 이 습관을 계속 유지할 것.
- 실제 self-contained/single-file publish는 `-p:SelfContained=true -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true -p:CbmReleasePublish=true`(마지막 것은 이
  프로젝트만의 커스텀 속성 — PDB 억제용, `Directory.Build.props` 참고)로 확정했다.
  `scripts/publish-release.ps1`을 실제로 실행해 재현성을 확인했다 — 새로 publish 관련 설정을
  바꿀 일이 있으면 이 스크립트와 `Directory.Build.props`/`CodexBackupManager.App.csproj`를 같이
  본다.
- `manifest.attachments[]`와 `BackupConversationMetadata`의 원본 38컬럼 필드가 여전히 Restore가
  쓸 수 있는 유일한 재료다 — `resolvedTitle` 같은 가공값을 원본 대신 쓰지 말 것.
- `docs/codexbackup-format-v1.md`가 Backup V1의 정식 스펙이다(FROZEN) — `CLAUDE.md` §11~13은 이제 이
  문서로 대체된 **초안**이니 그대로 구현하지 말 것.
