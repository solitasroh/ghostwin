# Phase 6-C PRD — 외부 통합 (External Integration)

> **문서 종류**: Product Requirements Document (PRD)
> **작성일**: 2026-04-16
> **Phase**: 6-C
> **소유자**: 노수장
> **선행 Phase**: Phase 6-B 알림 인프라 — 완료 (97% Match Rate)

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | Phase 6-A/B에서 OSC 시퀀스 기반 알림은 동작하지만, Claude Code Hooks(Stop/Notification)의 구조화된 JSON 이벤트를 직접 수신하는 경로가 없음. OSC는 "무언가 왔다" 수준이고, Claude Code가 보내는 이벤트 종류(idle_prompt, permission_prompt, stop)를 구분할 수 없음. 또한 사이드바에 git branch/PR 상태가 없어서 어떤 탭이 어떤 브랜치에서 작업 중인지 파악 불가 |
| **Solution** | Named Pipe 훅 서버(`\\.\pipe\ghostwin-hook`) + 경량 CLI 도구(`ghostwin-hook.exe`)로 Claude Code Hooks JSON을 직접 수신. git branch/PR 상태를 사이드바에 표시하여 세션별 작업 컨텍스트 제공 |
| **Function / UX Effect** | Claude Code `settings.json`에 GhostWin 훅 등록 → Stop/Notification 이벤트 발생 시 해당 탭의 AgentState가 정밀하게 전환 (idle_prompt→WaitingForInput, stop→Idle). 사이드바에 `main`, `feat/auth` 같은 branch 이름 + PR #42 표시 |
| **Core Value** | 비전 ② AI 에이전트 멀티플렉서의 **정밀 상태 추적 완성**. Phase 6-A/B(OSC 기반 간접 감지) → Phase 6-C(Claude Code Hooks 직접 연결)로 에이전트 상태 추적의 신뢰도를 간접→직접으로 격상. Windows에서 cmux의 Socket API에 대응하는 Named Pipe IPC 기반 확립 |

---

## 1. 배경과 동기

### 1.1 Phase 6-A/B가 달성한 것

- OSC 9/99/777 기반 알림 캡처 → amber dot + 알림 패널 + 배지
- AgentState 5-state 모델 (stdout 타임스탬프 + OSC 기반 전환)
- Toast 클릭 → 탭 전환

### 1.2 Phase 6-A/B의 한계

| 한계 | 영향 |
|------|------|
| **OSC = 간접 감지** | Claude Code가 "Stop" 이벤트를 보냈는지, "Notification" 이벤트를 보냈는지 구분 불가. OSC 9는 둘 다 동일하게 처리 |
| **AgentState 전환이 추측 기반** | Running→Idle 전환이 5초 무출력 타이머에 의존. Claude Code의 실제 Stop 이벤트와 무관 |
| **git 상태 미표시** | 5개 탭이 열려있어도 어떤 탭이 어떤 브랜치에서 작업 중인지 보이지 않음 |
| **외부 도구 연동 경로 없음** | GhostWin을 프로그래밍 방식으로 제어할 수 없음 (cmux의 Socket API에 대응 없음) |

### 1.3 Claude Code Hooks가 제공하는 것

Claude Code 공식 Hooks 시스템 (`~/.claude/settings.json`):

| 이벤트 | 발화 시점 | Phase 6-C 활용 |
|--------|----------|---------------|
| `Stop` | Claude 응답 완료 시 | AgentState → Idle (정확한 타이밍) |
| `Notification` (idle_prompt) | 입력 대기 시 | AgentState → WaitingForInput |
| `Notification` (permission_prompt) | 권한 요청 시 | AgentState → WaitingForInput + "권한 필요" 메시지 |
| `UserPromptSubmit` | 사용자 입력 제출 시 | AgentState → Running |
| `SubagentStart/Stop` | 서브에이전트 생명주기 | 하위 작업 추적 (v2) |
| `CwdChanged` | 작업 디렉토리 변경 시 | 사이드바 CWD 실시간 갱신 |

Hooks는 `command` 타입으로 실행 → JSON을 stdin으로 전달 → GhostWin의 Named Pipe에 전송.

---

## 2. 타겟 사용자

### 2.1 1차 타겟

**Claude Code 병렬 운영 개발자** — 3~10개 세션에서 Claude Code를 돌리면서, 각 세션의 정확한 상태(실행중/대기/권한필요/완료)와 작업 브랜치를 실시간 추적해야 하는 사용자.

### 2.2 사용자 시나리오

**시나리오 A: Claude Code Hooks 직접 연결**

> 개발자가 `~/.claude/settings.json`에 GhostWin 훅을 등록한다:
> ```json
> {
>   "hooks": {
>     "Stop": [{ "hooks": [{ "type": "command", "command": "ghostwin-hook stop" }] }],
>     "Notification": [{ "hooks": [{ "type": "command", "command": "ghostwin-hook notify" }] }]
>   }
> }
> ```
> Claude Code가 작업을 완료하면 `Stop` 훅 → `ghostwin-hook.exe` → Named Pipe → GhostWin이 해당 탭의 AgentState를 Idle로 정확히 전환. OSC 5초 타이머보다 즉각적.

**시나리오 B: git branch 확인**

> 5개 탭이 열려있다. 사이드바에:
> ```
> WS-1  feat/auth     PR #42
> WS-2  main          ●
> WS-3  fix/bug-123
> WS-4  feat/api      PR #55
> WS-5  develop
> ```
> 어떤 세션이 어떤 브랜치에서 작업 중인지 한눈에 파악.

---

## 3. 기능 요구사항

### FR-05: Named Pipe 훅 서버

**우선순위**: 필수 | **규모**: 대

| 항목 | 상세 |
|------|------|
| **파이프 경로** | `\\.\pipe\ghostwin-hook` |
| **프로토콜** | 줄바꿈 종료 JSON (cmux Socket API와 동일 패턴) |
| **서버 위치** | C# GhostWin.App 프로세스 내 (백그라운드 스레드) |
| **클라이언트** | `ghostwin-hook.exe` 경량 CLI (C# 단일 파일, self-contained) |
| **접근 제어** | 기본: 같은 사용자 프로세스만 연결 가능 (DACL) |
| **지원 이벤트** | `stop`, `notify`, `prompt`, `cwd-changed` |
| **세션 매칭** | 환경변수 `GHOSTWIN_SESSION_ID` 또는 CWD+PID 기반 |

**메시지 형식** (ghostwin-hook → GhostWin):

```json
{
  "event": "stop",
  "session_id": "abc123",
  "cwd": "C:\\Users\\user\\project",
  "data": {
    "stop_hook_reason": "end_turn"
  }
}
```

**ghostwin-hook.exe CLI**:

```
ghostwin-hook stop              # Stop 이벤트 전송 (stdin에서 JSON 읽기)
ghostwin-hook notify            # Notification 이벤트 전송
ghostwin-hook prompt            # UserPromptSubmit 이벤트 전송
ghostwin-hook set-status <state> # 직접 상태 설정 (idle/running/error)
```

### FR-07: git branch/PR 표시

**우선순위**: 선택 | **규모**: 중

| 항목 | 상세 |
|------|------|
| **branch 감지** | `git branch --show-current` 또는 `.git/HEAD` 파일 워치 |
| **PR 감지** | `gh pr view --json number,state` (GitHub CLI, 선택적) |
| **폴링 주기** | branch: CWD 변경 시 + 5초 폴링, PR: 30초 폴링 |
| **표시 위치** | 사이드바 각 탭의 CWD 아래에 branch 이름 + PR 번호 |
| **PR 없는 경우** | branch 이름만 표시 |
| **git 없는 디렉토리** | 표시 없음 |

---

## 4. 비기능 요구사항

| 항목 | 기준 |
|------|------|
| **Named Pipe 응답 지연** | < 10ms (파이프 연결 → JSON 파싱 → AgentState 변경) |
| **ghostwin-hook.exe 크기** | < 5MB (self-contained single-file publish) |
| **메모리** | Named Pipe 서버 상주: < 1MB 추가 |
| **git 폴링 부하** | `git branch --show-current`: < 1ms/호출, 5초 간격 |
| **보안** | Named Pipe DACL: 현재 사용자만 접근 가능 |

---

## 5. 범위 밖 (명시적 제외)

| 항목 | 이유 | 대안 |
|------|------|------|
| cmux 호환 JSON-RPC API 전체 구현 | 과도한 범위. v1에서는 훅 수신만 | 수요 발생 시 추가 |
| SubagentStart/Stop 추적 | v2 범위 | AgentState 확장으로 추가 가능 |
| 인앱 브라우저 (WebView2) | 별도 Phase | 독립 마일스톤 |
| Claude Code 자동 설정 도우미 | v1에서는 수동 settings.json 편집 | 설치 스크립트로 자동화 가능 |
| GitLab/Bitbucket PR 지원 | GitHub CLI만 v1 | API 추상화로 확장 가능 |

---

## 6. 기술 의존성

### 6.1 Phase 6-A/B에서 물려받는 자산

| 자산 | Phase 6-C 활용 |
|------|---------------|
| `IOscNotificationService` | Named Pipe 이벤트도 동일 서비스로 라우팅 가능 |
| `AgentState` enum | Stop/Notification 이벤트로 정밀 전환 |
| `SessionInfo` / `WorkspaceInfo` | git branch/PR 프로퍼티 추가 |
| `WorkspaceItemViewModel` | 사이드바 branch/PR 바인딩 |
| 알림 패널 | Named Pipe 알림도 패널에 표시 |

### 6.2 신규 의존성

| 의존성 | 용도 |
|--------|------|
| `System.IO.Pipes` (.NET) | NamedPipeServerStream |
| GitHub CLI (`gh`) | PR 상태 조회 (선택적, 미설치 시 graceful degradation) |

---

## 7. 성공 기준

| # | 기준 | 검증 방법 |
|:-:|------|----------|
| 1 | `ghostwin-hook stop` 실행 → 해당 탭 AgentState = Idle | 수동 확인 |
| 2 | `ghostwin-hook notify` 실행 → 해당 탭 WaitingForInput + 알림 패널 항목 추가 | 수동 확인 |
| 3 | Claude Code `settings.json`에 훅 등록 → 실제 Stop 이벤트 시 탭 상태 변경 | 수동 확인 (Claude Code 세션) |
| 4 | 사이드바에 git branch 이름 표시 | 수동 확인 |
| 5 | `gh` 설치 시 PR 번호 표시, 미설치 시 graceful degradation | 수동 확인 |
| 6 | Named Pipe 응답 < 10ms | 로그 타임스탬프 |
| 7 | 동시 5개 세션에서 훅 이벤트 정상 라우팅 | 수동 확인 |

---

## 8. 구현 순서 제안

```
FR-05 Named Pipe 훅 서버    ← 핵심. Claude Code 직접 연결
  ├── W1: Named Pipe 서버 (C#, System.IO.Pipes)
  ├── W2: ghostwin-hook.exe CLI (C# 단일 파일)
  ├── W3: AgentState 정밀 전환 (Stop→Idle, Notification→WaitingForInput)
  └── W4: Claude Code settings.json 연동 테스트
      ↓
FR-07 git branch/PR 표시    ← 부가 기능
  ├── W5: git branch 감지 + 사이드바 표시
  └── W6: PR 상태 폴링 (GitHub CLI, 선택)
```

**예상 기간**: 2~3일

---

## 9. 리스크

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| Named Pipe 보안 (악의적 프로세스가 연결) | 중 | DACL로 현재 사용자만 허용 + 세션 ID 검증 |
| ghostwin-hook.exe 배포 (PATH에 추가 필요) | 낮 | GhostWin 설치 시 동일 디렉토리에 배치, 절대 경로로 훅 등록 |
| Claude Code Hooks stdin JSON 형식 변경 | 낮 | 방어적 파싱 (unknown 필드 무시, 필수 필드만 확인) |
| `gh` CLI 미설치 시 PR 표시 실패 | 낮 | graceful degradation (branch만 표시) |
| 세션 매칭 오류 (여러 탭에서 같은 CWD) | 중 | `GHOSTWIN_SESSION_ID` 환경변수 우선 → CWD+PID 폴백 |

---

## 10. 경쟁 제품 대비 차별점

| 기능 | cmux | Warp | Wave | GhostWin (6-C 목표) |
|------|:----:|:----:|:----:|:-------------------:|
| Claude Code Hooks 직접 연결 | Unix Socket | ✅ (Warp 전용) | ❌ | ✅ (Named Pipe) |
| Socket/Pipe IPC API | ✅ (JSON-RPC) | ❌ | ✅ (wsh) | ✅ (Named Pipe JSON) |
| git branch 사이드바 표시 | ✅ | ❌ | ❌ | ✅ |
| PR 상태 표시 | ✅ | ❌ | ❌ | ✅ (GitHub CLI) |
| Windows 네이티브 | ❌ | ✅ | ✅ | ✅ |

---

## 참조 문서

- Phase 6-A Report: `docs/archive/2026-04/phase-6-a-osc-notification-ring/`
- Phase 6-B Report: `docs/archive/2026-04/phase-6-b-notification-infra/`
- cmux 리서치: `docs/00-research/cmux-ai-agent-ux-research.md` (§1.7 CLI/Socket API, §2.5 Named Pipe 아키텍처)
- Claude Code Hooks 공식 문서: [code.claude.com/docs/en/hooks-guide](https://code.claude.com/docs/en/hooks-guide)

---

*Phase 6-C PRD v1.0 — External Integration (2026-04-16)*
