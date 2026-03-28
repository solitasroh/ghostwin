# cmux AI 에이전트 UX + 멀티플렉서 패턴 심층 리서치

> GhostWin Terminal 프로젝트 — 기술 리서치 문서
> 작성일: 2026-03-28
> 목적: cmux의 핵심 UX를 Windows에서 클린룸 재구현하기 위한 참고 자료

---

## 목차

1. [cmux 기능 상세 분석](#1-cmux-기능-상세-분석)
2. [Claude Code OSC Hooks 연동](#2-claude-code-osc-hooks-연동)
3. [에이전트 상태 추적 시스템](#3-에이전트-상태-추적-시스템)
4. [tmux 호환 세션 관리](#4-tmux-호환-세션-관리)
5. [경쟁 제품 AI 터미널 분석](#5-경쟁-제품-ai-터미널-분석)
6. [라이선스 리스크 분석](#6-라이선스-리스크-분석)
7. [GhostWin 구현 로드맵 제언](#7-ghostwin-구현-로드맵-제언)

---

## 1. cmux 기능 상세 분석

### 1.1 기본 정보

| 항목 | 내용 |
|------|------|
| 버전 | v0.62.2 (2025년 3월 14일 릴리즈) |
| 라이선스 | AGPL-3.0 (듀얼 라이선스) |
| 플랫폼 | macOS 14+ (Apple Silicon / Intel) |
| 언어 | Swift + AppKit (네이티브, non-Electron) |
| 터미널 엔진 | libghostty (Ghostty의 렌더링 라이브러리) |
| GitHub | github.com/manaflow-ai/cmux (~10,940 스타) |

**확인된 사실**: cmux는 Ghostty를 포크한 것이 아니라, libghostty를 라이브러리로 사용하는 독립 앱이다. "libghostty for terminal rendering"을 사용하며, WebKit(인앱 브라우저) + Unix Socket(IPC)을 결합한 3-레이어 아키텍처를 사용한다.

출처: [GitHub - manaflow-ai/cmux](https://github.com/manaflow-ai/cmux), [HN 토론](https://news.ycombinator.com/item?id=47079718)

---

### 1.2 전체 기능 목록 (v0.62.2 기준)

#### 핵심 레이아웃 시스템

- **계층 구조**: Window → Workspace(수직 탭) → Pane → Surface(터미널/브라우저)
- **레이아웃 엔진**: Bonsplit — 수평/수직 pane 분할, 중첩 레이아웃 지원
- **분할 방향**: left, right, up, down

#### 수직 탭 사이드바 (Workspace Sidebar)

사이드바 각 탭에 실시간으로 표시되는 정보:

| 정보 항목 | 표시 방식 | 업데이트 방식 |
|----------|-----------|--------------|
| git branch 이름 | 텍스트 | 브랜치 전환 시 실시간 |
| 연결된 PR 번호/상태 | 아이콘 + 숫자 | 폴링 방식 (추측) |
| 작업 디렉토리 (CWD) | 텍스트 (축약) | 디렉토리 변경 시 |
| 리스닝 포트 목록 | 포트 번호 리스트 | 포트 열림/닫힘 시 |
| 최신 알림 텍스트 | 알림 본문 | 알림 수신 시 |
| 미읽음 알림 배지 | 파란 링 | 알림 수신 시 |

**실현 가능성**: 상 (GhostWin에서 모두 구현 가능)

- git branch: `git branch --show-current` CLI 폴링 또는 파일시스템 워처로 `.git/HEAD` 감시
- PR 상태: GitHub CLI (`gh pr status`) 또는 GitHub REST API 폴링
- CWD: Claude Code의 `CwdChanged` hook 또는 PTY OSC 7 시퀀스 수신
- 포트: `netstat` / `ss` 폴링 또는 ETW(Event Tracing for Windows) 기반 감시
- 알림 배지: 알림 수신 시 탭 UI 상태 업데이트

`set-status` CLI 명령으로 SF Symbol 아이콘을 포함한 커스텀 사이드바 메타데이터 설정 가능.
`set-progress` 명령으로 진행률 표시줄도 표시 가능.

---

### 1.3 Notification Ring (에이전트 대기 알림) 동작 원리

**확인된 사실**: cmux의 알림 시스템은 다음 계층으로 구성된다.

```
입력 소스
├── OSC 9   — ESC ] 9 ; message BEL        (iTerm2 호환, 메시지만)
├── OSC 777 — ESC ] 777 ; notify ; Title ; Body BEL  (rxvt/VTE 호환)
├── OSC 99  — ESC ] 99 ; params ; payload ST  (Kitty 프로토콜, 풍부한 기능)
└── CLI     — cmux notify --title "T" --body "B"

시각적 피드백 계층
├── 파란 링 (Notification Ring): pane 테두리에 파란색 링 표시
├── 사이드바 배지: 해당 workspace 탭에 미읽음 배지
├── 알림 패널: 모든 알림을 시간순으로 리스트
└── 데스크톱 알림: macOS 시스템 알림 (조건부 억제)
```

#### 알림 생명주기 (4단계)

1. **수신(Received)**: 알림 패널에 추가, 데스크톱 알림 발화
2. **미읽음(Unread)**: workspace 탭에 배지 표시 유지
3. **읽음(Read)**: 해당 workspace를 조회하면 자동 소거
4. **제거(Dismissed)**: 패널에서 수동 삭제

#### 데스크톱 알림 억제 조건

- cmux 윈도우가 포커스된 상태
- 알림을 보낸 workspace가 현재 활성화됨
- 알림 패널이 열려있음

#### 키보드 단축키

| 단축키 | 동작 |
|--------|------|
| `⌘⇧I` | 알림 패널 열기/닫기 |
| `⌘⇧U` | 가장 최근 미읽음 알림 workspace로 즉시 점프 |
| 알림 클릭 | 해당 workspace로 이동 |

출처: [cmux docs/notifications](https://cmux.com/docs/notifications)

---

### 1.4 알림 패널 + 미읽음 점프 UX

**확인된 사실**:
- 알림 패널은 모든 workspace의 알림을 시간순으로 통합하여 표시
- 각 알림 항목을 클릭하면 해당 workspace로 즉시 전환
- `⌘⇧U`로 "가장 최근 미읽음 알림"이 있는 workspace로 원클릭 점프
- 알림에는 title, subtitle(OSC 99), body 포함 가능

**GhostWin 구현 방향**:
- WinUI3 Flyout 또는 커스텀 패널로 알림 패널 구현
- `Ctrl+Shift+U` 단축키로 미읽음 탭 점프
- Win32 Toast 알림으로 데스크톱 알림 연동

---

### 1.5 세션 복원 메커니즘

**확인된 사실 (중요한 제한사항)**:

cmux의 세션 복원은 **부분적**이다:

| 복원 항목 | 지원 여부 |
|----------|----------|
| 윈도우 레이아웃 (pane 구성) | 지원 |
| 작업 디렉토리 (CWD) | 지원 |
| 스크롤백 히스토리 | 지원 |
| 브라우저 네비게이션 히스토리 | 지원 |
| **실행 중인 프로세스 상태** | **미지원** |
| **Claude Code / tmux / vim 세션** | **미지원** |

공식 문서 인용: "does not restore live process state inside terminal apps — for example, active Claude Code/tmux/vim sessions are not resumed after restart yet."

**GhostWin 구현 방향**:
- 레이아웃과 CWD는 JSON 설정 파일로 직렬화하여 복원
- 실행 중인 프로세스 복원은 2차 목표로 미룸 (tmux 연동으로 우회 가능)
- tmux on WSL 세션을 GhostWin에서 attach하는 방식으로 실질적 복원 제공

---

### 1.6 인앱 브라우저

**확인된 사실**:
- macOS WebKit 기반 (네이티브, Chromium 아님)
- 에이전트가 스크립트 API로 브라우저를 직접 제어 가능

#### 브라우저 자동화 CLI 명령

```bash
cmux browser open <url>
cmux browser snapshot --interactive   # 접근성 트리 스냅샷 (텍스트 DOM)
cmux browser click "e10"              # 요소 참조로 클릭
cmux browser fill "e14" "value"       # 폼 필드 채우기
cmux browser type "text"              # 텍스트 입력
cmux browser eval "document.title"    # JS 평가
```

`snapshot --interactive` 실행 시 요소를 `e10`, `e14` 같은 참조로 표현하여
AI 에이전트가 DOM 구조를 텍스트로 파악하고 조작 가능.

**GhostWin 구현 방향**:
- WebView2 (Chromium 기반, Edge) 통합
- `ghostwin browser` CLI 명령 또는 Named Pipe API로 동일 기능 제공
- 실현 가능성: **중** (WebView2 자동화 API가 WebKit보다 제한적)

---

### 1.7 CLI / Socket API 상세

**확인된 사실**: cmux는 JSON-RPC over Unix Socket 방식을 사용한다.

#### 소켓 경로

| 환경 | 경로 |
|------|------|
| 릴리즈 빌드 | `/tmp/cmux.sock` |
| 디버그 빌드 | `/tmp/cmux-debug.sock` |
| 태그된 디버그 | `/tmp/cmux-debug-<tag>.sock` |
| 환경변수 오버라이드 | `CMUX_SOCKET_PATH` |

#### 요청 형식 (JSON-RPC, 줄바꿈 종료 필수)

```json
{"id":"req-1","method":"workspace.list","params":{}}
```

#### 응답 형식

```json
{"id":"req-1","ok":true,"result":{...}}
```

#### 핵심 API 메서드 목록

| 카테고리 | 메서드 | 설명 |
|---------|--------|------|
| **Workspaces** | `workspace.list` | 전체 workspace 목록 조회 |
| | `workspace.create` | 새 workspace 생성 |
| | `workspace.select` | 특정 workspace 전환 |
| | `workspace.current` | 현재 활성 workspace 조회 |
| | `workspace.close` | workspace 닫기 |
| **Surfaces/Panes** | `surface.split` | pane 분할 (direction: left/right/up/down) |
| | `surface.list` | 현재 pane 목록 조회 |
| | `surface.focus` | 특정 pane 포커스 |
| | `surface.send_text` | pane에 텍스트 전송 |
| | `surface.send_key` | 특수키 전송 (enter, tab, esc, 방향키 등) |
| **Notifications** | `notification.create` | 알림 생성 |
| | `notification.list` | 알림 목록 조회 |
| | `notification.clear` | 알림 초기화 |
| **System** | `system.ping` | 연결 확인 |
| | `system.capabilities` | 지원 기능 조회 |
| | `system.identify` | 앱 정보 조회 |

#### 접근 제어 모드

| 모드 | 설명 |
|------|------|
| Off | 소켓 API 비활성화 |
| cmux processes only (기본값) | cmux 자식 프로세스만 연결 가능 |
| allowAll | 모든 로컬 프로세스 허용 (`CMUX_SOCKET_MODE` 환경변수) |

**GhostWin 구현 방향**:
- Named Pipe (`\\.\pipe\ghostwin`) 기반 JSON-RPC 서버로 동일 패턴 구현
- Windows Named Pipe는 접근 제어(DACL)로 보안 관리 가능
- CLI 명령도 동일하게 `ghostwin <command>` 형태로 제공

출처: [cmux API Reference](https://cmux.com/docs/api)

---

## 2. Claude Code OSC Hooks 연동

### 2.1 Claude Code Hooks 시스템 전체 이벤트 목록

**확인된 사실**: Claude Code의 훅 시스템은 `~/.claude/settings.json` 또는 `.claude/settings.json`에 설정.

#### 전체 훅 이벤트 (공식 문서 기준)

| 이벤트 | 발화 시점 | GhostWin 활용 |
|--------|-----------|--------------|
| `SessionStart` | 세션 시작/재개 시 | 탭에 세션 시작 배지 표시 |
| `UserPromptSubmit` | 사용자 입력 제출 시 | 에이전트 "실행 중" 상태로 변경 |
| `PreToolUse` | 툴 호출 실행 전 | 위험한 툴 실행 전 경고 |
| `PermissionRequest` | 권한 요청 다이얼로그 표시 시 | 탭에 "권한 필요" 배지 |
| `PostToolUse` | 툴 호출 성공 후 | 진행 상황 업데이트 |
| `PostToolUseFailure` | 툴 호출 실패 후 | 탭에 "오류" 배지 |
| **`Notification`** | **Claude가 알림을 보낼 때** | **Notification Ring 트리거** |
| `SubagentStart` | 서브에이전트 생성 시 | 하위 작업 추적 |
| `SubagentStop` | 서브에이전트 완료 시 | 하위 작업 완료 표시 |
| `TaskCreated` | 태스크 생성 시 | 진행 상황 추적 |
| `TaskCompleted` | 태스크 완료 시 | 완료 알림 |
| **`Stop`** | **Claude 응답 완료 시** | **"대기 중" 상태로 변경** |
| `StopFailure` | API 오류로 턴 종료 시 | 탭에 "오류" 배지 |
| `TeammateIdle` | 팀 에이전트 유휴 상태 | 대기 중 알림 |
| `InstructionsLoaded` | CLAUDE.md 로드 시 | - |
| `ConfigChange` | 설정 파일 변경 시 | - |
| `CwdChanged` | 작업 디렉토리 변경 시 | 사이드바 CWD 업데이트 |
| `FileChanged` | 감시 파일 변경 시 | - |
| `WorktreeCreate` | worktree 생성 시 | - |
| `WorktreeRemove` | worktree 삭제 시 | - |
| `PreCompact` | 컨텍스트 압축 전 | - |
| `PostCompact` | 컨텍스트 압축 후 | - |
| `Elicitation` | MCP 서버 사용자 입력 요청 시 | - |
| `ElicitationResult` | MCP elicitation 응답 후 | - |
| `SessionEnd` | 세션 종료 시 | 탭 세션 종료 표시 |

출처: [Claude Code Hooks Guide](https://code.claude.com/docs/en/hooks-guide)

---

### 2.2 GhostWin에서 가장 중요한 훅 이벤트

#### Notification 이벤트 — Notification Ring 트리거의 핵심

```json
{
  "hooks": {
    "Notification": [
      {
        "matcher": "permission_prompt|idle_prompt",
        "hooks": [
          {
            "type": "command",
            "command": "ghostwin notify --session-id $CLAUDE_SESSION_ID --title \"Claude 대기 중\" --body \"입력이 필요합니다\""
          }
        ]
      }
    ]
  }
}
```

`Notification` 이벤트의 matcher 값:
- `permission_prompt`: 권한 요청 시
- `idle_prompt`: 입력 대기 시 (가장 중요)
- `auth_success`: 인증 성공 시
- `elicitation_dialog`: MCP 입력 요청 시

#### Stop 이벤트 — 에이전트 완료 감지

```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "ghostwin set-status --session-id $CLAUDE_SESSION_ID --status idle"
          }
        ]
      }
    ]
  }
}
```

**주의**: `Stop` 이벤트는 Claude가 응답을 완료할 때마다 발화. 작업이 완전히 완료된 것인지, 단순히 다음 입력을 기다리는 것인지는 컨텍스트로 판단 필요.

---

### 2.3 훅 입력 데이터 형식 (stdin으로 JSON 수신)

```json
{
  "session_id": "abc123",
  "cwd": "C:\\Users\\user\\project",
  "hook_event_name": "Notification",
  "notification_type": "idle_prompt"
}
```

`CwdChanged` 이벤트 시:
```json
{
  "session_id": "abc123",
  "cwd": "C:\\Users\\user\\project\\src",
  "hook_event_name": "CwdChanged"
}
```

---

### 2.4 OSC 시퀀스 — 터미널에서 직접 수신하는 방법

**확인된 사실**: Claude Code는 훅 외에도 터미널 OSC 시퀀스를 직접 발화한다.

#### OSC 시퀀스 형식 비교

| 시퀀스 | 형식 | 제목+본문 | 부제목 | 알림 ID | 호환 터미널 |
|--------|------|-----------|--------|---------|------------|
| **OSC 9** | `ESC ] 9 ; message BEL` | 메시지만 | 없음 | 없음 | iTerm2, ConEmu, Windows Terminal |
| **OSC 777** | `ESC ] 777 ; notify ; Title ; Body BEL` | 있음 | 없음 | 없음 | rxvt-unicode, VTE 기반 |
| **OSC 99** | `ESC ] 99 ; params ; payload ST` | 있음 | 있음 | 있음 | Kitty |

Windows Terminal은 OSC 777 지원을 이미 구현 (PR #14425).

#### VT 파서에서 OSC 훅 수신 구현 패턴 (C++ 의사 코드)

```cpp
// libghostty-vt 파서 콜백에서 처리
void onOscSequence(int code, std::string_view payload) {
    switch (code) {
        case 9:
            // iTerm2 style: payload = "message"
            triggerNotification("", payload, "");
            break;
        case 777:
            // rxvt style: payload = "notify;Title;Body"
            auto parts = split(payload, ';');
            if (parts[0] == "notify")
                triggerNotification(parts[1], parts[2], "");
            break;
        case 99:
            // Kitty style: "key=value:key=value;base64_payload"
            parseKittyNotification(payload);
            break;
        case 7:
            // OSC 7: CWD 업데이트 (file:///path/to/dir)
            updateCwd(payload);
            break;
    }
}
```

**실현 가능성**: 상 — libghostty-vt가 OSC 파싱을 제공하므로 콜백 연결만 하면 됨

---

### 2.5 Named Pipe 기반 훅 서버 아키텍처 (Windows)

```
Claude Code (훅 실행)
       │
       ├── settings.json hooks.Notification
       │   └── command: "ghostwin-hook.exe notify --pipe \\.\pipe\ghostwin-hook"
       │
       ▼
ghostwin-hook.exe (JSON-RPC 클라이언트)
       │ Named Pipe: \\.\pipe\ghostwin-hook
       ▼
GhostWin 훅 서버 (메인 프로세스 내)
       │
       ├── 세션 ID로 탭 특정
       ├── 탭 상태 업데이트 (idle/running/error)
       └── Notification Ring 트리거
```

**구현 핵심**:
- `ghostwin-hook.exe`: GhostWin과 함께 설치되는 경량 CLI 도구
- Named Pipe `\\.\pipe\ghostwin-hook`에 JSON 메시지 전송
- GhostWin 훅 서버: CreateNamedPipe + IOCP 기반 비동기 수신
- 세션 식별: `CLAUDE_SESSION_ID` 환경변수 or CWD + 프로세스 ID로 탭 매칭

---

## 3. 에이전트 상태 추적 시스템

### 3.1 에이전트 세션 상태 종류

GhostWin에서 구현할 상태 모델:

```
Idle (대기 중)       — 초기 상태, 입력 준비됨
  │
  ├─ [사용자 입력] ──→ Running (실행 중)
  │                      │
  │                      ├─ [Stop 이벤트] ──→ Idle
  │                      ├─ [Notification 이벤트] ──→ WaitingForInput (입력 대기)
  │                      └─ [StopFailure / 오류 감지] ──→ Error (오류)
  │
  ├─ [WaitingForInput] ──→ [사용자 입력] ──→ Running
  └─ [Error] ──→ [수동 재시작] ──→ Idle
```

| 상태 | UI 표현 | 색상 | 배지 |
|------|---------|------|------|
| `Idle` | 빈 상태 | 기본 | 없음 |
| `Running` | 스피너/펄스 | 초록 | 실행 아이콘 |
| `WaitingForInput` | Notification Ring | 파란 | 알림 점 |
| `Error` | 오류 아이콘 | 빨간 | 오류 아이콘 |
| `Completed` | 체크마크 | 회색 | 완료 아이콘 |

---

### 3.2 상태 변경 감지 방법 비교

| 방법 | 신뢰도 | 구현 난이도 | 설명 |
|------|--------|------------|------|
| **Claude Code Hooks** | 높음 | 낮음 | 공식 API, 가장 권장. `Notification`, `Stop` 이벤트 직접 수신 |
| **OSC 시퀀스** | 높음 | 낮음 | VT 파서에서 직접 처리. OSC 9/777/99 수신 시 알림 상태로 변경 |
| **출력 패턴 분석** | 낮음 | 높음 | Claude Code 프롬프트 패턴을 regex로 감지. 불안정, 권장하지 않음 |
| **Claude Code API** | 중간 | 중간 | Claude Code의 REST API (있는 경우). 문서 없음 (추측) |

**권장 전략**: Claude Code Hooks + OSC 시퀀스를 동시에 지원하여 이중화.

---

### 3.3 탭별 상태 배지 UI 패턴

cmux의 파란 링 대신 GhostWin은 다음 패턴으로 구현:

```
┌──────────────────────────────────┐
│ ⚡ /project/app (main)           │  ← Running: 파란 스피너
│   PR #42  · :8080                │
├──────────────────────────────────┤
│ 🔵 /project/api (feat/auth)      │  ← WaitingForInput: 파란 원
│   PR #43  Claude 대기 중          │
├──────────────────────────────────┤
│ ✅ /project/docs (main)          │  ← Completed: 초록 체크
│   PR #41                         │
├──────────────────────────────────┤
│ ❌ /project/test (fix/bug)       │  ← Error: 빨간 X
│   오류 발생                       │
└──────────────────────────────────┘
```

---

### 3.4 여러 에이전트 동시 운영 시 UX 문제와 해결책

| 문제 | 해결책 |
|------|--------|
| 어떤 탭이 입력 대기 중인지 모름 | Notification Ring + 통합 알림 패널 |
| 알림이 너무 많아 노이즈 발생 | 알림 억제 조건 (포커스된 탭 등) + 알림 무음/일시정지 기능 |
| 빠른 탭 전환이 번거로움 | `Ctrl+Shift+U`로 미읽음 탭 즉시 점프 |
| 에이전트 진행 상황 파악 불가 | `set-progress` 명령으로 진행률 표시 |
| 에러가 어느 탭인지 모름 | 에러 배지 + 알림 패널에서 에러 표시 |
| 알림 맥락 부족 ("Claude is waiting" 만 표시) | 훅에서 추가 컨텍스트 정보를 body에 포함 |

---

## 4. tmux 호환 세션 관리

### 4.1 tmux의 세션/윈도우/pane 모델

```
tmux Server (백그라운드 데몬)
└── Session "project-a"
    ├── Window 0 "editor"
    │   ├── Pane 0 (nvim)
    │   └── Pane 1 (terminal)
    └── Window 1 "server"
        └── Pane 0 (node server)

Client A ──attach──▶ Session "project-a"
Client B ──attach──▶ Session "project-a"  (동시 attach 가능)
```

**핵심 특성**:
- 서버-클라이언트 분리: 클라이언트가 detach해도 세션 유지
- Unix Domain Socket으로 IPC (`/tmp/tmux-UID/default`)
- 동일 세션에 여러 클라이언트 동시 attach 가능

---

### 4.2 Windows에서 tmux-like 세션 관리 구현 방법

#### 방법 1: psmux 활용 (추측 - 존재 확인됨)

**확인된 사실**: psmux는 Rust로 작성된 Windows 네이티브 tmux 구현체.

| 항목 | 내용 |
|------|------|
| 언어 | Rust (50%) + PowerShell (50%) |
| PTY | ConPTY 직접 사용 (WSL/Cygwin 불필요) |
| 호환성 | .tmux.conf 읽기, tmux 테마 지원 |
| 명령어 | 76개 명령 + 126개 format variable |
| 세션 관리 | `new-session`, `attach`, `ls` 지원 |

GhostWin에서 psmux를 서브시스템으로 활용하거나 참고 구현으로 사용 가능.

#### 방법 2: GhostWin 자체 세션 매니저 구현

```
GhostWin Session Manager
├── SessionStore: 세션 상태를 JSON으로 디스크 저장
├── Named Pipe Server: \\.\pipe\ghostwin-session
│   └── 외부 클라이언트 attach/detach 지원
├── ConPTY Pool: 각 세션의 ConPTY 인스턴스 관리
└── Session Restore: 앱 재시작 시 레이아웃 복원
```

---

### 4.3 세션 attach/detach 메커니즘 (Windows)

tmux의 attach/detach 개념을 GhostWin에 적용하는 방법:

```
Scenario: GhostWin 재시작 후 Claude Code 세션 복원

1. GhostWin 시작 시 SessionStore에서 이전 세션 목록 로드
2. 각 세션의 ConPTY 인스턴스 재생성 또는 기존 프로세스 재연결
   - ConPTY는 프로세스가 살아있는 동안 재연결 가능 (한계 있음)
   - 더 실용적: tmux on WSL을 통해 세션 유지
3. UI에서 세션 목록 표시 → 사용자가 attach할 세션 선택
```

**현실적 한계**: Windows ConPTY는 tmux와 달리 프로세스 수명에 종속됨.
`tmux on WSL` + GhostWin attach 패턴이 실용적인 해결책.

---

### 4.4 Named Pipe 기반 IPC 세션 관리 구현 (Windows C++)

```cpp
// GhostWin 훅 서버 핵심 구조
class HookServer {
    HANDLE m_pipe;

    void start() {
        m_pipe = CreateNamedPipe(
            L"\\\\.\\pipe\\ghostwin-hook",
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            4096, 4096, 0, nullptr
        );
        // IOCP 기반 비동기 수신 루프 시작
    }

    void onMessage(std::string_view json) {
        // JSON 파싱 → 이벤트 타입 확인
        // session_id로 탭 특정
        // 탭 상태 업데이트
        // UI 스레드에 PostMessage로 알림
    }
};
```

---

## 5. 경쟁 제품 AI 터미널 분석

### 5.1 Warp Terminal

| 항목 | 내용 |
|------|------|
| 플랫폼 | macOS, Linux, Windows |
| 라이선스 | 상용 (무료 플랜 + Pro) |
| 기반 기술 | Rust + WebGPU 렌더링 |
| AI 엔진 | Oz 에이전트 (자체 + Claude Code, Codex, Gemini CLI 연동) |
| 멀티플렉서 | 없음 (탭 기반, pane 분할 지원) |
| 에이전트 지원 수준 | 높음 |

**Warp AI 핵심 기능**:
- **Oz Agent**: 자연어로 터미널 제어, 서브쉘에서 실행, 클라우드에서 실행 가능
- **Block 기반 출력**: 각 명령과 출력이 독립적 블록 단위로 관리 (선택, 복사, 참조 용이)
- **멀티 에이전트**: Warp Oz + Claude Code + Codex + Gemini CLI를 동시 실행 지원
- **Warp Drive**: 커맨드 히스토리, 계획, 의사결정을 중앙 저장소에 보관

**Claude Code 공식 연동**: [warpdotdev/claude-code-warp](https://github.com/warpdotdev/claude-code-warp) — Warp 공식 Claude Code 통합 플러그인

**SWE-Bench 성능**: "터미널 벤치 #1, SWE-Bench Verified #5" 주장 (700,000+ 개발자 사용)

**GhostWin 대비 차이점**:
- Warp는 Block-based UX가 핵심, GhostWin은 멀티플렉서 + 에이전트 알림이 핵심
- Warp는 상용 클라우드 의존, GhostWin은 로컬 네이티브
- Warp는 Windows 지원 있음 → GhostWin의 직접 경쟁자

---

### 5.2 Wave Terminal

| 항목 | 내용 |
|------|------|
| 플랫폼 | macOS, Linux, **Windows** |
| 라이선스 | Apache 2.0 (오픈소스) |
| 기반 기술 | Go + Electron (WebView2 기반 추측) |
| AI 엔진 | Wave AI (컨텍스트 인식, BYOK 지원 예정) |
| 멀티플렉서 | Workspace → Tab → Block 계층 |
| 에이전트 지원 수준 | 중간 |

**Wave 핵심 기능**:
- **위젯 시스템**: 터미널 내 파일 미리보기, 마크다운, 이미지, 오디오/비디오 인라인 렌더링
- **Durable Sessions**: 세션 영구 보존, 유니버설 히스토리 검색
- **Remote Connections**: SSH/원격 머신 통합
- **wsh 명령**: Wave 제어 및 위젯 실행 CLI
- **Slide-Out Chat Panel**: `Cmd-Shift-A`로 AI 패널 열기 (터미널 출력, 파일 접근 가능)

**GhostWin 대비 차이점**:
- Wave는 그래픽 위젯 렌더링에 강점, GhostWin은 순수 터미널 성능 + 에이전트 알림에 강점
- Wave는 오픈소스(Apache 2.0), 참고 구현으로 활용 가능
- Wave는 Windows 지원 있음 → GhostWin의 간접 경쟁자

---

### 5.3 Tabby Terminal

| 항목 | 내용 |
|------|------|
| 플랫폼 | macOS, Linux, Windows |
| 라이선스 | MIT (오픈소스) |
| 기반 기술 | Electron + Angular |
| AI 엔진 | 없음 (AI 기능 없음) |
| 멀티플렉서 | 있음 (탭 + 분할 지원) |
| SSH 관리 | 강점 (그래픽 SSH 세션 관리) |
| 에이전트 지원 수준 | 없음 |

**주의**: "Tabby"는 두 개의 별개 프로젝트가 있음:
1. **Tabby Terminal** (Eugeny/tabby) — MIT 라이선스 터미널 에뮬레이터, AI 없음
2. **TabbyML** (TabbyML/tabby) — self-hosted AI 코딩 어시스턴트, 터미널 아님

Tabby Terminal은 Electron 기반이라 GhostWin(네이티브 Win32)의 성능 철학과 상반됨.

---

### 5.4 에이전트 지원 수준 비교 매트릭스

| 기능 | cmux | Warp | Wave | Tabby | GhostWin (목표) |
|------|------|------|------|-------|----------------|
| AI 에이전트 알림 | ✅ (핵심) | ✅ | ⚠️ | ❌ | ✅ (핵심) |
| 수직 탭 사이드바 | ✅ | ❌ | ❌ | ❌ | ✅ |
| Notification Ring | ✅ | ❌ | ❌ | ❌ | ✅ |
| git/PR 상태 | ✅ | ⚠️ | ❌ | ❌ | ✅ |
| OSC 9/777/99 | ✅ | ⚠️ | ❌ | ❌ | ✅ |
| Claude Code 훅 | ✅ | ✅ | ❌ | ❌ | ✅ |
| 인앱 브라우저 | ✅ (WebKit) | ❌ | ✅ | ❌ | ✅ (WebView2) |
| Socket/CLI API | ✅ | ❌ | ✅ (wsh) | ❌ | ✅ |
| 세션 복원 | ⚠️ (레이아웃만) | ❌ | ✅ | ⚠️ | ✅ (레이아웃) |
| Windows 지원 | ❌ | ✅ | ✅ | ✅ | ✅ (목표) |
| 오픈소스 | AGPL-3.0 | ❌ | Apache 2.0 | MIT | (미정) |
| GPU 렌더링 | ✅ (libghostty) | ✅ (WebGPU) | ❌ | ❌ | ✅ (D3D11) |

---

## 6. 라이선스 리스크 분석

### 6.1 cmux AGPL-3.0 라이선스 해석

**확인된 사실**: AGPL-3.0은 GPL-3.0에 네트워크 서비스 조항을 추가한 강한 카피레프트 라이선스.

#### AGPL-3.0 핵심 의무

| 의무 | 내용 | GhostWin 관련성 |
|------|------|----------------|
| 소스 공개 | 수정된 코드를 배포 시 소스 공개 | 코드를 직접 포팅하면 의무 발생 |
| 같은 라이선스 | 파생물도 AGPL-3.0으로 배포해야 함 | 코드를 직접 포팅하면 의무 발생 |
| 네트워크 조항 | SaaS로 제공 시에도 소스 공개 의무 | 해당 없음 (로컬 앱) |
| UX 패턴 참고 | **의무 없음** — 아이디어는 보호 안 됨 | **안전** |

**중요**: cmux가 듀얼 라이선스를 제공하더라도, AGPL 코드를 직접 사용하지 않는 한 GhostWin은 라이선스 의무를 지지 않는다.

---

### 6.2 클린룸 재구현의 법적 안전성

**확인된 사실 (Google v. Oracle 판례 기반)**:

2021년 미국 대법원 Google v. Oracle 판결:
> "구글의 자바 API 사용은 공정이용(fair use)에 해당한다."

이 판결이 클린룸 재구현에 미치는 영향:
- API 명세(인터페이스)는 저작권 보호가 약함
- 기능적 아이디어는 저작권 보호 대상이 아님
- 단, **특정 코드의 표현(expression)**은 저작권 보호 대상

AWS의 DocumentDB 사례:
- MongoDB(AGPL) API를 클린룸 재구현
- AGPL 의무 없이 MongoDB 호환 서비스 제공
- AWS는 AGPL 이전 마지막 버전까지만 호환성을 명시

---

### 6.3 UX 패턴만 참고하는 것의 범위 — 어디까지가 안전한가

#### 안전한 것 (아이디어의 영역)

- 수직 탭 사이드바라는 **개념** (사이드바에 탭을 배치한다는 아이디어)
- git branch, PR 상태를 탭에 표시한다는 **기능적 아이디어**
- notification ring으로 에이전트 대기를 표시한다는 **UX 패턴**
- OSC 시퀀스로 알림을 수신한다는 **프로토콜 연동 방식** (OSC는 공개 표준)
- 알림 패널 + 미읽음 점프라는 **인터랙션 패턴**
- JSON-RPC over Unix Socket이라는 **아키텍처 패턴** (일반적인 IPC 방식)

#### 경계선 (주의 필요)

- cmux의 특정 시각적 디자인과 **완전히 동일한** 픽셀 레벨 UI → 트레이드 드레스 침해 가능성
- cmux 공식 문서의 특정 문구나 설명을 **그대로 복사** → 저작권 침해

#### 위험한 것 (금지)

- cmux Swift 코드를 C++/C#으로 **직역 변환** → AGPL 의무 발생
- cmux 소스의 알고리즘을 **코드 수준에서 참고** → AGPL 의무 발생 가능성

---

### 6.4 유사 사례 비교

| 사례 | 내용 | 결과 |
|------|------|------|
| Google v. Oracle (2021) | 구글의 Java API 재구현 | 공정이용 인정, 재구현 합법 |
| AWS DocumentDB vs MongoDB | AGPL MongoDB를 클린룸 재구현 | 합법, 현재 서비스 중 |
| OpenOffice vs Microsoft Office | Office UX 패턴 참고 재구현 | 합법 (UX는 보호 안 됨) |
| Wine vs Windows | Win32 API 스펙 역공학 구현 | 합법 (API 명세는 보호 안 됨) |

---

### 6.5 라이선스 리스크 최소화 권장사항

1. **소스 코드를 보지 않는 팀 분리**: cmux 코드를 읽은 개발자와 GhostWin을 구현하는 개발자를 분리 (pure clean room)
2. **기능 사양 문서화**: 구현 전 "무엇을 구현할 것인가"를 기능 사양으로 먼저 문서화 (이 리서치 문서가 그 역할)
3. **독립적인 설계 결정**: 가능한 경우 cmux와 다른 방식으로 구현 (예: Windows Named Pipe vs Unix Socket)
4. **법률 자문 권고**: 상업적 배포 전 IP 전문 변호사 검토 권장

---

## 7. GhostWin 구현 로드맵 제언

### 7.1 Phase 4 (WinUI3 UI + 멀티플렉서) 세부 계획

이 리서치를 바탕으로 Phase 4 구현 순서 제언:

#### 4-1단계: 수직 탭 사이드바 기본
- WinUI3 커스텀 ListView로 수직 탭 구현
- 탭에 CWD, 프로세스 이름 표시
- 탭 추가/제거/전환

#### 4-2단계: OSC 훅 수신
- libghostty-vt의 OSC 파싱 콜백에서 OSC 9/777/99 처리
- Notification Ring: pane 테두리 파란색으로 변경
- Win32 Toast 알림 연동

#### 4-3단계: Named Pipe 훅 서버
- `\\.\pipe\ghostwin-hook` 서버 구현
- `ghostwin-hook.exe` CLI 도구 빌드
- Claude Code `~/.claude/settings.json` 자동 설정 도우미

#### 4-4단계: 통합 알림 패널
- WinUI3 Flyout 기반 알림 패널
- `Ctrl+Shift+U` 미읽음 탭 점프

#### 4-5단계: git/PR 상태 사이드바
- `git branch --show-current` 폴링 (1초)
- GitHub CLI 또는 GitHub REST API로 PR 상태 폴링 (30초)
- 사이드바 실시간 업데이트

### 7.2 핵심 구현 차별화 포인트

GhostWin이 cmux와 다르게 가져갈 수 있는 부분:

| 항목 | cmux | GhostWin 차별화 |
|------|------|----------------|
| 알림 시스템 | macOS 알림 | Win32 Toast + Action Center 통합 |
| 브라우저 | WebKit | WebView2 (Chrome 호환성 더 높음) |
| IPC | Unix Socket | Windows Named Pipe (더 Windows 친화적) |
| 세션 복원 | 레이아웃만 | tmux on WSL attach로 실질 복원 |
| 원격 지원 | 로컬만 (SSH 미지원) | Windows 원격 데스크톱 + SSH 세션 연동 |
| IME | macOS 기본 | TSF 직접 연동으로 한국어 완벽 지원 |

---

## 참고 자료

### 공식 문서

- [cmux 공식 사이트](https://cmux.com/)
- [cmux GitHub](https://github.com/manaflow-ai/cmux)
- [cmux API 레퍼런스](https://cmux.com/docs/api)
- [cmux 알림 문서](https://cmux.com/docs/notifications)
- [cmux v0.62.2 릴리즈 노트](https://github.com/manaflow-ai/cmux/releases/tag/v0.62.2)
- [Claude Code Hooks Guide](https://code.claude.com/docs/en/hooks-guide)

### 기술 레퍼런스

- [Windows Terminal OSC 777 구현 PR](https://github.com/microsoft/terminal/pull/14425)
- [OSC 시퀀스 스펙 (Kitty 문서)](https://sw.kovidgoyal.net/kitty/desktop-notifications/)
- [psmux — Windows tmux 구현](https://github.com/psmux/psmux)
- [Claude Code 알림 훅 구현 가이드](https://kane.mx/posts/2025/claude-code-notification-hooks/)
- [Windows Named Pipes 공식 문서](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipes)

### 경쟁 제품

- [Warp Terminal](https://www.warp.dev/)
- [Wave Terminal](https://www.waveterm.dev/)
- [warpdotdev/claude-code-warp](https://github.com/warpdotdev/claude-code-warp)

### 라이선스/법률

- [AGPL-3.0 TLDRLegal](https://www.tldrlegal.com/license/gnu-affero-general-public-license-v3-agpl-3-0)
- [Google v. Oracle 대법원 판결 요약](https://www.supremecourt.gov/opinions/20pdf/18-956_d18f.pdf)

---

*리서치 문서 v1.0 — GhostWin Terminal 프로젝트*
*작성일: 2026-03-28*
