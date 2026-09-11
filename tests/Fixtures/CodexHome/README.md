# tests/Fixtures/CodexHome

**이 폴더의 모든 파일은 테스트용 합성 데이터입니다. 실제 사용자의 `.codex` 데이터가 아닙니다.**

CLAUDE.md §31 원칙에 따라 실제 사용자의 `.codex` 데이터는 Repository에 커밋하지 않습니다.
대신 Phase 0 조사에서 확인한 *구조*만 흉내 낸 작은 픽스처를 둡니다.

## 구성

```
CodexHome/
├─ config.toml                      literal string / 테이블 중첩 / 배열 포함
├─ session_index.jsonl              3줄 / 고유 2건 (중복 1건 — append-only 로그 재현)
├─ .codex-global-state.json         app-server-projects-migration-by-host 포함
├─ state_4.sqlite                   실제 스키마의 부분집합 (generation 4)
├─ sessions/2026/01/02/             rollout JSONL 2개
└─ archived_sessions/               rollout JSONL 1개
```

## 픽스처가 담고 있는 검증 포인트

| 항목 | 값 | 무엇을 검증하는가 |
|---|---|---|
| `BROWSER_USE_CODEX_APP_VERSION` | `99.123.45678` | Desktop 버전 파싱 (basic string) |
| `CODEX_CLI_PATH` | `C:\Fixture\bin\codex.exe` | CLI 경로 파싱 (literal string, 이스케이프 없음) |
| state DB 파일명 | `state_4.sqlite` | `state_5` 하드코딩이 없음을 증명 |
| `_sqlx_migrations` 최신 version | `7` | migration 버전 읽기 |
| `threads` 행 수 | 4 (archived 1) | 행 수 집계 |
| 최신 `cli_version` | `9.9.9` | `updated_at_ms` 최대 행에서 읽기 |
| `threads.cwd` | `\\?\C:\Fixture\...` | 실제와 동일한 확장 길이 prefix 형태 |
| `threadAssignmentsMigrated` | `false` | Phase 0에서 확인한 마이그레이션 중간 상태 재현 |
| sessions `*.jsonl` | 2 | 재귀 열거 |
| archived `*.jsonl` | 1 | 아카이브 열거 |
| `session_index.jsonl` | 3줄 | 줄 수 집계(중복 포함) |
| `[projects.'c:\fixture\projects\한글 프로젝트']` | — | 한글 경로가 섞여도 스캐너가 깨지지 않음 |

## 주의

테스트는 이 폴더를 **읽기만** 합니다. 쓰기 방지 테스트(`CodexHomeIsNeverWrittenTests`)는
이 폴더를 임시 위치로 복사한 뒤 조사 전후 스냅샷을 비교합니다.
