# Codex Backup Manager — Claude Development Guide

## 1. 프로젝트 목적

이 프로젝트는 **OpenAI Codex의 로컬 프로젝트 및 대화 데이터를 조회, 선택, 내보내기, 불러오기, 복원할 수 있는 Windows 데스크톱 프로그램**을 만드는 것이 목적이다.

핵심 목표는 다음과 같다.

- 사용자의 PC에서 Codex 데이터 위치를 자동으로 탐색한다.
- 자동 탐색에 실패하면 사용자가 직접 Codex 데이터 폴더를 지정할 수 있다.
- Codex에 존재하는 프로젝트와 대화 목록을 표시한다.
- 가능하면 선택한 대화의 실제 내용을 프로그램 안에서 열람할 수 있게 한다.
- 프로젝트 전체 또는 개별 대화를 선택할 수 있게 한다.
- 선택한 데이터를 하나의 백업 파일로 내보낼 수 있게 한다.
- 다른 PC에서 해당 백업 파일을 불러올 수 있게 한다.
- 현재 PC의 Codex에 프로젝트/대화를 안전하게 적용하여 다시 사용할 수 있게 한다.
- 복원 과정에서 기존 Codex 데이터가 손상되지 않도록 안전장치를 둔다.

이 프로그램은 단순한 JSONL 뷰어가 아니라, 최종적으로는 **Codex 프로젝트/대화 백업 및 마이그레이션 관리자**를 목표로 한다.

---

# 2. 가장 중요한 개발 원칙

## 2.1 직접 구현한다

이 프로젝트의 핵심 기능은 직접 구현한다.

다음과 같은 외부 프로그램을 실행하거나 필수 의존성으로 삼지 않는다.

- codex-claude-transfer / cct
- Codex_Sync
- 기타 Codex 백업/이전 CLI

유사 오픈소스 프로젝트는 구조나 아이디어를 참고할 수는 있지만, 이 프로그램이 동작하기 위해 해당 프로그램이 설치되어 있어야 하는 구조로 만들지 않는다.

가능하면 OpenAI Codex 공식 소스 코드, 공식 프로토콜, 실제 로컬 저장 구조를 우선적으로 참고한다.

---

## 2.2 Codex 원본 데이터 보호가 최우선이다

읽기 기능은 가능한 한 Read-Only로 구현한다.

특히 다음 파일 또는 저장소를 함부로 수정하지 않는다.

- `session_index.jsonl`
- `state_*.sqlite`
- `sessions/**/*.jsonl`
- `archived_sessions/**`
- 기타 Codex 내부 상태 파일

목록 조회, 검색, 미리보기, Export 과정에서는 **Codex 원본 파일을 절대 수정하지 않는다.**

Import / Apply 과정에서 수정이 필요하다면 반드시 다음 순서를 따른다.

1. 현재 Codex 상태 검사
2. Codex 실행 여부 검사
3. 복원 대상 검증
4. 현재 상태 Snapshot 생성
5. 실제 적용
6. 적용 결과 검증
7. 실패 시 Rollback 가능 상태 유지

안전성이 확실하지 않은 방식으로 Codex DB를 직접 UPDATE하지 않는다.

---

# 3. 지원 플랫폼

초기 버전은 **Windows 전용**으로 개발한다.

우선 지원 환경:

- Windows 10
- Windows 11
- OpenAI Codex Desktop / Codex CLI 계열의 로컬 데이터

macOS / Linux 지원은 V1 범위에 포함하지 않는다.

단, 내부 로직은 가능하면 플랫폼 의존 코드를 분리하여 이후 확장 가능하게 작성한다.

---

# 4. Codex 위치 탐색

프로그램 시작 시 Codex 데이터 위치를 자동으로 찾는다.

우선적으로 확인할 후보:

1. `CODEX_HOME` 환경 변수
2. `%USERPROFILE%\.codex`
3. 향후 확인된 공식 Codex 데이터 경로
4. 프로그램 설정에 저장된 이전 수동 경로

탐색 성공 시 현재 연결된 Codex 경로를 UI에 표시한다.

예:

```text
Codex 연결됨
C:\Users\User\.codex
```

자동 탐색에 실패하면 사용자가 직접 폴더를 선택할 수 있어야 한다.

수동으로 지정한 폴더가 실제 Codex 데이터 폴더인지 검증해야 한다.

검증 예시:

- `sessions` 폴더 존재 여부
- `session_index.jsonl` 존재 여부
- Codex 관련 SQLite 파일 존재 여부
- 알려진 Codex 디렉터리 구조와의 일치 여부

단순히 폴더가 존재한다는 이유만으로 유효한 Codex Home으로 판단하지 않는다.

---

# 5. 데이터 탐색 구조

프로그램 내부에서는 Codex 저장 파일 구조와 UI 표현 구조를 분리한다.

예상 내부 계층:

```text
CodexLocator
CodexStorageReader
CodexSessionParser
CodexProjectResolver
CodexConversationRepository
BackupExporter
BackupImporter
RestoreService
IntegrityValidator
SnapshotService
```

각 클래스/모듈은 역할을 명확하게 분리한다.

거대한 하나의 Manager 클래스에 모든 기능을 넣지 않는다.

---

# 6. 프로젝트 목록

Codex 세션에서 프로젝트를 식별한다.

프로젝트 구분의 기본 기준은 세션 메타데이터에 존재하는 작업 디렉터리(`cwd`)이다.

예:

```text
C:\_UserProjects\Unreal\Balhwajeom_Project
C:\_UserProjects\Tools\UProjectHub
```

같은 `cwd` 또는 정규화 후 동일한 프로젝트 경로를 사용하는 세션은 같은 프로젝트로 묶는다.

Windows 경로 표현 차이를 고려한다.

예:

```text
C:\Project\Test
\\?\C:\Project\Test
c:\project\test
```

필요하면 내부 비교용 Canonical Path를 별도로 생성한다.

원본 경로 문자열은 보존한다.

---

# 7. 대화 목록

각 프로젝트 하위에 해당 프로젝트와 연결된 Codex 대화를 표시한다.

예:

```text
Balhwajeom_Project
 ├─ Gameplay Tag 시스템 구조
 ├─ 조사 시스템 구현
 └─ Interaction Component 수정
```

프로젝트로 분류할 수 없는 대화는 별도의 항목으로 표시한다.

예:

```text
기타 대화
미분류
프로젝트 없음
```

대화 목록에는 가능하면 다음 정보를 표시한다.

- 제목
- Thread / Session ID
- 생성 날짜
- 마지막 수정 날짜
- 프로젝트 경로
- 세션 파일 경로
- 상태
  - 정상
  - 일부 데이터 누락
  - 손상 가능성
  - Archive 상태

---

# 8. 대화 내용 열람

가능하면 프로그램 안에서 Codex 대화 내용을 읽을 수 있게 한다.

최소 V1에서는 다음 메시지를 구분해서 표시한다.

- User
- Assistant / Codex

가능하면 이후 다음 항목도 표현할 수 있도록 파서를 확장 가능하게 만든다.

- Tool Call
- Tool Result
- Reasoning 관련 공개 가능한 메타데이터
- 파일 변경
- 명령 실행 결과
- 시스템 이벤트

단, V1에서는 대화 내용을 완벽하게 렌더링하는 것보다 **프로젝트/대화 목록과 Export/Import 안정성**이 더 중요하다.

---

# 9. 선택 방식

사용자는 다음 단위로 선택할 수 있어야 한다.

- 프로젝트 전체
- 개별 대화
- 여러 프로젝트
- 여러 대화

프로젝트를 선택하면 기본적으로 해당 프로젝트의 모든 대화가 선택된다.

개별 대화 선택도 가능해야 한다.

선택 상태는 UI에서 명확하게 보여준다.

---

# 10. Export

선택한 프로젝트 또는 대화를 하나의 백업 파일로 내보낸다.

초기 전용 확장자는 임시로 다음 중 하나를 사용할 수 있다.

```text
.codexbackup
```

전용 확장자를 사용하되 내부적으로 ZIP 기반 컨테이너를 사용할 수 있다.

단, 구현 세부사항이 UI 사용자에게 불필요하게 노출될 필요는 없다.

---

# 11. 백업 파일 구조

> **§11~13은 Phase 5 구현 전 초안이며, 실측 기반으로 확정된 정식 스펙이 아니다.**
> Backup Format V1의 정식 스펙은 `docs/codexbackup-format-v1.md`이다(Phase 05_01 완료 기준
> **FROZEN**) — Restore Sufficiency Audit, manifest/checksum/dependency-closure/attachment 정책이
> 전부 실측·공식 소스 대조로 확정되어 있다. 아래 §11~13의 예시(파일 배치, manifest 필드 이름 등)는
> 실제 구현과 다르다. Phase 6/7 작업 시에는 아래 초안이 아니라 `docs/codexbackup-format-v1.md`를
> 따를 것.

백업 파일은 단순히 Codex 폴더를 통째로 ZIP 하는 방식으로 만들지 않는다.

우리 프로그램의 독립적인 백업 포맷을 정의한다.

예시:

```text
backup.codexbackup

manifest.json

projects/
    project-001.json

sessions/
    session-001/
        metadata.json
        rollout.jsonl

    session-002/
        metadata.json
        rollout.jsonl

checksums/
    sha256.json
```

실제 구조는 구현 과정에서 더 적절한 방식으로 변경할 수 있다.

---

# 12. manifest

백업 파일에는 반드시 Manifest가 존재해야 한다.

최소 정보:

```text
backupFormatVersion
appVersion
createdAt
sourceOS
sourceCodexVersion
projectCount
conversationCount
```

각 Project:

```text
projectId
displayName
originalPath
sessionIds
```

각 Session:

```text
sessionId
threadId
title
originalCwd
createdAt
updatedAt
sourceFile
checksum
```

Codex 원본에서 확인할 수 없는 값은 억지로 만들어내지 않는다.

---

# 13. 체크섬

백업 파일 내부 데이터에는 무결성 검사를 적용한다.

우선 SHA-256을 사용한다.

Import 전에 다음을 검사한다.

- Manifest 존재 여부
- Backup Format Version
- 필수 파일 존재 여부
- SHA-256 일치 여부
- JSON / JSONL parse 가능 여부
- Session ID 충돌 여부

검사 실패 시 바로 Codex에 적용하지 않는다.

---

# 14. Import Preview

백업 파일을 선택했다고 바로 Codex에 적용하지 않는다.

먼저 내용을 보여준다.

예:

```text
Balhwajeom_Project.codexbackup

프로젝트: 1
대화: 27

Balhwajeom_Project
 ├ Gameplay Tag 시스템 구조
 ├ 조사 시스템 구현
 └ ...

원본 위치:
C:\_UserProjects\Unreal\Balhwajeom_Project

현재 상태:
경로를 찾을 수 없음
```

사용자가 실제 적용 전에 가져올 내용을 확인할 수 있어야 한다.

---

# 15. 프로젝트 경로 재매핑

다른 컴퓨터로 옮길 경우 프로젝트 실제 위치가 달라질 수 있다.

예:

```text
PC A
C:\_UserProjects\Unreal\Balhwajeom_Project

PC B
D:\Projects\Balhwajeom_Project
```

따라서 Import 시 프로젝트 경로를 재지정할 수 있어야 한다.

UI 예:

```text
원본 프로젝트 경로
C:\_UserProjects\Unreal\Balhwajeom_Project

현재 프로젝트 경로
D:\Projects\Balhwajeom_Project

[폴더 선택]
```

프로젝트가 현재 컴퓨터에 존재하지 않아도 대화 데이터 자체를 가져오는 것이 가능한 구조라면 이를 허용한다.

나중에 프로젝트 경로를 다시 연결할 수 있도록 설계한다.

---

# 16. 충돌 처리

Import하려는 Thread / Session이 현재 Codex에 이미 존재할 수 있다.

충돌 시 자동 덮어쓰기하지 않는다.

최소한 다음 옵션을 고려한다.

- 건너뛰기
- 기존 항목 교체
- 복사본으로 가져오기

초기 기본값은 `건너뛰기`로 한다.

`기존 항목 교체`는 충분한 안전성이 확보되기 전까지 제한하거나 Experimental 기능으로 두어도 된다.

---

# 17. Snapshot

Apply 전에 현재 Codex 상태 Snapshot을 만든다.

Snapshot은 최소한 이번 복원 작업이 변경하게 될 파일을 복구할 수 있어야 한다.

가능하면 Snapshot 자체도 별도 관리한다.

예:

```text
Snapshots/
  2026-09-11_153012/
```

사용자는 최근 Import 이전 상태로 돌아갈 수 있어야 한다.

---

# 18. Rollback

Import 도중 오류가 발생하면 가능한 한 자동으로 이전 상태로 복원한다.

예:

```text
Apply 시작
↓
세션 12개 중 8개 복원
↓
오류 발생
↓
중단
↓
Snapshot 기반 Rollback
↓
기존 Codex 상태 복구
```

중간 실패 상태를 그대로 방치하지 않는다.

---

# 19. Codex 실행 중 처리

읽기 기능:

- Codex 실행 중에도 허용 가능
- 단, 읽는 도중 변경되는 파일에 대한 예외 처리를 한다.

쓰기 / Apply:

- 가능하면 Codex가 종료된 상태에서만 수행한다.

Codex 프로세스가 실행 중이면 사용자에게 알려준다.

예:

```text
Codex가 현재 실행 중입니다.

안전한 복원을 위해 Codex를 종료한 뒤 다시 시도해 주세요.
```

프로세스를 강제 종료하는 기능은 초기 버전에서는 넣지 않아도 된다.

---

# 20. state_*.sqlite 접근 원칙

SQLite 상태 DB를 읽는 것은 가능하다.

하지만 직접 수정은 최대한 피한다.

원칙:

```text
READ: 허용
WRITE: 최후의 수단
```

Codex가 공식적으로 제공하는 app-server / protocol / import 관련 기능 중 안정적으로 사용할 수 있는 기능이 있다면 직접 DB 수정보다 우선한다.

단, Experimental API 하나에 프로그램 전체 구조를 종속시키지 않는다.

---

# 21. Codex App Server

Codex의 공식 app-server / protocol에서 다음과 같은 기능이 안정적으로 사용 가능한지 조사한다.

예:

```text
thread/list
thread/read
thread/turns/list
thread/items/list
```

가능하면 다음 용도에 활용할 수 있다.

- 세션 목록 조회
- 세션 내용 조회
- Codex가 실제로 인식하는 Thread 확인
- Import 후 복원 검증

하지만 파일 기반 백업 포맷과 우리 프로그램의 핵심 도메인 모델은 app-server와 분리한다.

Codex 버전 변경으로 API가 달라져도 Export 파일 자체가 무용지물이 되지 않아야 한다.

---

# 22. Import 후 검증

파일을 복사했다고 성공으로 판단하지 않는다.

복원 완료 후 다음을 검사한다.

- 세션 파일 존재
- 데이터 Parse 성공
- 필요한 Metadata 존재
- Codex가 해당 Thread를 인식하는지
- 가능하면 해당 Thread Resume 가능 여부

검증 실패 시 사용자에게 명확하게 표시한다.

---

# 23. 버전 호환성

Codex의 저장 구조는 앞으로 변경될 수 있다고 가정한다.

따라서 Codex 데이터 접근 코드는 Version Adapter 구조를 고려한다.

예:

```text
ICodexStorageAdapter

CodexStorageAdapterV1
CodexStorageAdapterV2
```

백업 파일 포맷도 Version을 가진다.

```text
backupFormatVersion = 1
```

향후 포맷 변경 시 Migration 가능하게 만든다.

---

# 24. UI 기본 구조

초기 UI는 다음 구조를 목표로 한다.

```text
┌──────────────────────────────────────────────────┐
│ Codex Backup Manager              Codex 연결됨 ● │
├─────────────────┬────────────────────────────────┤
│ 검색            │ 대화 내용                       │
│                 │                                │
│ 프로젝트        │ User                           │
│                 │ ...                            │
│ □ Project A     │                                │
│   □ Chat 1      │ Codex                          │
│   □ Chat 2      │ ...                            │
│                 │                                │
│ □ Project B     │                                │
│   □ Chat 3      │                                │
│                 │                                │
│ 기타            │                                │
│   □ Chat 4      │                                │
├─────────────────┴────────────────────────────────┤
│ 3개 선택됨      [내보내기] [백업 불러오기]       │
└──────────────────────────────────────────────────┘
```

UI는 화려함보다 명확성과 안정성을 우선한다.

---

# 25. V1 우선순위

다음 순서대로 구현한다.

## Phase 1 — Codex 탐색

- Codex Home 자동 탐색
- 수동 지정
- 경로 검증
- 설정 저장

## Phase 2 — Read Model

- Codex 세션 탐색
- JSONL Parser
- 세션 Metadata 읽기
- 프로젝트 그룹화
- 대화 목록 출력

## Phase 3 — Conversation Viewer

- 선택한 대화 읽기
- User / Assistant 메시지 표시
- 오류 세션 표시

## Phase 4 — Selection

- 프로젝트 선택
- 대화 선택
- 다중 선택

## Phase 5 — Export

- Manifest
- 독립 백업 포맷
- ZIP Container
- SHA-256
- Export 검증

## Phase 6 — Import Preview

- 백업 열기
- Manifest 검사
- 체크섬 검사
- 프로젝트/대화 목록 표시
- 충돌 검사
- 경로 재매핑

## Phase 7 — Safe Restore

- Codex 실행 여부 확인
- Snapshot
- Apply
- 검증
- Rollback

---

# 26. V1에서 하지 않을 것

다음 기능은 초기에 구현하지 않아도 된다.

- Cloud Sync
- 계정 로그인
- 자체 서버
- 여러 사용자의 백업 공유
- 자동 주기 백업
- Google Drive / Dropbox 연동
- Unreal 프로젝트 실제 파일 백업
- Git Repository 백업
- Codex 설정 전체 백업
- Claude Code 대화 Import
- ChatGPT 일반 대화 Import
- macOS
- Linux

Scope를 불필요하게 넓히지 않는다.

---

# 27. 실제 프로젝트 파일은 백업하지 않는다

여기서 말하는 `프로젝트 Export`는 다음을 의미한다.

```text
Codex 프로젝트 정보
+
해당 프로젝트와 연결된 Codex 대화
```

다음은 포함하지 않는다.

```text
.git/
Source/
Content/
*.uproject
node_modules/
프로젝트 소스 전체
```

실제 개발 프로젝트 파일 백업은 Git 또는 별도 백업 도구의 역할이다.

---

# 28. 오류 처리

오류를 숨기지 않는다.

예:

```text
이 세션의 JSONL 파일을 찾을 수 없습니다.
```

```text
Manifest의 SHA-256 값과 실제 파일이 일치하지 않습니다.
```

```text
이 백업은 현재 지원하지 않는 Backup Format Version 3으로 생성되었습니다.
```

```text
Codex가 실행 중이라 안전한 복원을 진행할 수 없습니다.
```

가능한 경우 사용자에게 해결 방법도 함께 제시한다.

---

# 29. Logging

개발 중 문제 분석을 위해 구조화된 로그를 남긴다.

로그 수준:

- Debug
- Info
- Warning
- Error

개인 대화 전문을 기본 로그에 기록하지 않는다.

민감 데이터가 로그에 그대로 남지 않도록 주의한다.

---

# 30. 테스트 원칙

Parser, Export, Import, Path Mapping, Conflict Detection은 UI와 분리하여 단위 테스트 가능하게 만든다.

특히 다음 테스트를 작성한다.

## CodexLocator

- 기본 `.codex` 검색
- CODEX_HOME
- 잘못된 경로
- 수동 지정

## Path Normalization

- 대소문자 차이
- `\\?\` Prefix
- Trailing slash
- 상대 경로가 들어온 경우

## Session Parser

- 정상 JSONL
- 빈 JSONL
- 일부 잘린 JSONL
- 알 수 없는 Event Type
- 오래된 Format

## Export

- 대화 1개
- 프로젝트 전체
- 여러 프로젝트
- 중복 세션
- Checksum

## Import

- 정상 파일
- 손상된 ZIP
- Manifest 없음
- Checksum 불일치
- 동일 Session ID 존재
- 프로젝트 경로 변경
- Codex 버전 차이

## Restore

- 정상 Restore
- 중간 실패
- Rollback
- Snapshot 복구

---

# 31. 테스트 데이터

실제 사용자의 `.codex` 데이터를 테스트 코드에 그대로 넣지 않는다.

실제 구조를 모방한 작은 Fixture를 만든다.

예:

```text
Tests/Fixtures/CodexHome/
Tests/Fixtures/BackupV1/
```

민감한 실제 대화나 토큰이 Git Repository에 Commit되지 않게 한다.

---

# 32. Git 작업 원칙

작업은 가능한 한 작은 단위로 나눈다.

하나의 Commit에 여러 독립 기능을 섞지 않는다.

예:

```text
feat: detect Codex home directory
feat: parse Codex session metadata
feat: group sessions by project cwd
feat: export selected sessions
feat: validate codexbackup manifest
```

사용자가 명시적으로 요청하지 않았다면 임의로 Commit하거나 Push하지 않는다.

---

# 33. 구현 과정에서 불확실한 부분

Codex 내부 저장 구조와 Import 방식은 버전에 따라 바뀔 수 있다.

따라서 다음 행동을 하지 않는다.

- 확인하지 않은 필드를 추측하여 DB에 기록
- 인터넷 글 하나만 보고 Codex 구조를 확정
- 현재 로컬 데이터 한 샘플만 보고 모든 버전이 동일하다고 가정
- Import가 성공했는지 확인하지 않고 성공으로 표시

불확실한 경우 다음 순서로 확인한다.

1. 현재 설치된 Codex 실제 구조
2. OpenAI Codex 공식 소스
3. 공식 Issue / Discussion
4. 최소 재현 테스트
5. 그 다음 구현

---

# 34. Claude가 작업할 때의 응답 방식

각 단계의 구현 전에 다음을 먼저 확인한다.

- 현재 Repository 구조
- 기존 코드
- 기존 테스트
- 현재 Git 상태
- 변경 영향 범위

작업 후에는 반드시 알려준다.

1. 무엇을 변경했는지
2. 어떤 파일을 변경했는지
3. 어떤 테스트를 실행했는지
4. 테스트 결과
5. 아직 남은 위험 요소
6. 다음 추천 단계

작업하지 않은 내용을 완료했다고 표현하지 않는다.

테스트하지 않은 기능을 정상 동작한다고 단정하지 않는다.

---

# 35. 작업 범위 통제

사용자가 특정 Phase를 요청하면 해당 Phase에 집중한다.

예를 들어 Phase 2 작업 중에는 갑자기 Import UI나 Cloud Sync를 구현하지 않는다.

필요한 기반 리팩터링은 가능하지만 관련 없는 기능을 추가하지 않는다.

---

# 36. 최종 성공 기준

V1은 다음 시나리오가 성공하면 완성으로 본다.

## PC A

1. 프로그램 실행
2. Codex 자동 탐색
3. 프로젝트 목록 표시
4. 프로젝트 선택
5. 해당 프로젝트의 대화 목록 확인
6. 일부 대화 또는 전체 프로젝트 선택
7. `.codexbackup` Export

## PC B

1. 프로그램 실행
2. Codex 자동 탐색
3. `.codexbackup` Import
4. 백업 내용 Preview
5. 프로젝트 경로 재매핑
6. 충돌 확인
7. Snapshot 생성
8. Apply
9. Codex 실행
10. 복원된 대화가 목록에 표시
11. 복원된 대화를 열거나 Resume 가능

이 전체 과정이 기존 Codex 대화를 손상시키지 않고 수행되어야 한다.

---

# 37. 가장 중요한 한 문장

> 이 프로그램의 최우선 목표는 "Codex 데이터를 많이 건드리는 것"이 아니라, 사용자의 기존 Codex 데이터를 절대 잃지 않으면서 프로젝트와 대화를 안전하게 이동시키는 것이다.

---

# 38. 확정된 기술 스택 (Phase 1에서 결정)

| 항목 | 결정 |
|---|---|
| 언어 / 런타임 | **C# / .NET 10 (LTS)** — 라이브러리 `net10.0`, WPF `net10.0-windows` |
| UI | **WPF (MVVM)**. 외부 MVVM 프레임워크 없음 |
| 배포 | `win-x64` 단일 exe (`dotnet publish -r win-x64 --self-contained /p:PublishSingleFile=true`) |
| SQLite | `Microsoft.Data.Sqlite` — `Mode=ReadOnly`, `Pooling=False` |
| JSON | `System.Text.Json` (`Utf8JsonReader` 스트리밍) |
| ZIP / SHA-256 | BCL (`System.IO.Compression`, `System.Security.Cryptography`) |
| `.zst` (Phase 2 이후) | 순수 관리형 구현 (외부 exe 호출 금지) |
| 테스트 | xUnit |
| 로깅 | 직접 구현한 파일 로거 (redaction 규칙을 완전히 통제하기 위함) |

.NET 10을 고른 이유: 2026-09 기준 .NET 10이 현재 LTS다. .NET 9는 STS로 지원이 종료되었고,
.NET 8 LTS는 2026-11에 끝나므로 새 프로젝트가 선택할 버전이 아니다.

**NuGet 패키지는 최소한으로 쓴다.** 현재 4개뿐이다
(`Microsoft.Data.Sqlite` + 테스트 3개). TOML 스칼라 리더, 로거, MVVM 베이스,
폴더 선택 대화상자는 모두 직접 구현했다.

---

# 39. 구현 시 참조 순서

코드를 쓰기 전에 이 순서로 확인한다.

1. **`docs/project-status-and-handoff.md`** — 지금까지 어느 Phase까지 끝났는지, 확정된 설계
   결정, 남은 작업/주의사항을 정리한 인계 문서. 세션이 새로 시작되거나 압축된 뒤에는 **가장 먼저**
   읽는다. 작업을 마칠 때(특히 Phase가 바뀌거나 중요한 설계 결정이 생겼을 때)는 이 문서도 최신
   상태로 갱신한다.
2. **`docs/codex-storage-format.md`** — 실제 조사로 확정된 저장 구조. 구현의 1차 기준.
3. **`docs/phase0-codex-investigation-2026-09-11.md`** — 조사 원본 기록과 판단 근거.
4. 이 문서(`CLAUDE.md`) — 개발 원칙.
5. 현재 코드와 테스트.

`docs/codex-storage-format.md` §9에 **아직 확정하지 못한 항목**이 정리되어 있다.
그 목록에 있는 내용은 추측으로 구현하지 않고, 먼저 확인하거나 사용자에게 알린다.
