# Plan — Phase 6-C: 외부 통합 (External Integration)

> **문서 종류**: Plan
> **작성일**: 2026-04-16
> **PRD 참조**: `docs/00-pm/phase-6-c-external-integration.prd.md`
> **선행 완료**: Phase 6-A (93%) + Phase 6-B (97%)
> **비전 축**: ② AI 에이전트 멀티플렉서

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | Phase 6-A/B의 OSC 기반 간접 감지는 Claude Code 이벤트 종류(Stop/Notification/idle_prompt/permission_prompt)를 구분하지 못함. AgentState 전환이 5초 타이머 추측에 의존하여 부정확. 사이드바에 git branch/PR 없어서 세션별 작업 컨텍스트 파악 불가 |
| **Solution** | Named Pipe 훅 서버(`\\.\pipe\ghostwin-hook`) + `ghostwin-hook.exe` CLI로 Claude Code Hooks JSON 직접 수신 → AgentState 정밀 전환. git branch/PR 사이드바 표시 |
| **Function / UX Effect** | Claude Code `settings.json`에 훅 등록 → Stop 즉시 Idle, Notification 즉시 WaitingForInput. 사이드바에 `feat/auth PR #42` 표시. OSC 기반 기존 경로도 폴백으로 유지 |
| **Core Value** | 비전 ② AI 에이전트 멀티플렉서의 **정밀 상태 추적 완성**. OSC(간접) + Named Pipe(직접) 이중화로 에이전트 추적 신뢰도 최대화. cmux Socket API 대응 Windows IPC 기반 확립 |

---

## 1. 현재 상태 (Phase 6-A/B 기반)

### 1.1 이미 있는 것

```
에이전트 상태 감지 (간접)
├── OSC 9/99/777 → IOscNotificationService → NeedsAttention=true
├── stdout 타임스탬프 → AgentState.Running (5초 무출력 → Idle)
├── exit code → AgentState.Completed / Error
└── 알림 패널 + 배지 + Toast 클릭

부족한 것
├── Claude Code Stop 이벤트 직접 수신 ❌
├── Notification 이벤트 종류 구분 (idle_prompt vs permission_prompt) ❌
├── 외부 도구의 GhostWin 프로그래밍 제어 ❌
└── git branch / PR 상태 표시 ❌
```

### 1.2 Phase 6-C에서 추가할 것

```
Named Pipe 훅 서버 (\\.\pipe\ghostwin-hook)
├── ★ NEW: NamedPipeServerStream (C#, 백그라운드 스레드)
├── ★ NEW: ghostwin-hook.exe CLI (stdin JSON → Named Pipe 전송)
├── ★ NEW: AgentState 정밀 전환 (Stop→Idle, Notification→WaitingForInput)
└── ★ NEW: Claude Code settings.json 연동

git branch/PR 사이드바
├── ★ NEW: SessionInfo.GitBranch / GitPrNumber
├── ★ NEW: CWD 변경 시 git branch 감지
└── ★ NEW: 사이드바에 branch 이름 + PR 번호 표시
```

---

## 2. 기능 요구사항 상세

### FR-05: Named Pipe 훅 서버

**핵심 아이디어**: Claude Code Hooks가 실행하는 `ghostwin-hook.exe`가 Named Pipe를 통해 GhostWin에 JSON 이벤트를 전송. GhostWin은 이벤트를 파싱하여 정확한 AgentState 전환 수행.

#### 2.1.1 아키텍처

```
Claude Code (자식 프로세스, ConPTY 안)
  │
  ├─ settings.json hooks.Stop
  │   └─ command: "ghostwin-hook.exe stop"
  │
  ├─ settings.json hooks.Notification
  │   └─ command: "ghostwin-hook.exe notify"
  │
  ▼
ghostwin-hook.exe (stdin에서 JSON 읽기)
  │
  ├─ Claude Code가 전달한 JSON 파싱 (session_id, cwd, hook_event_name 등)
  ├─ GHOSTWIN_SESSION_ID 환경변수에서 세션 ID 추출
  │
  ▼ Named Pipe: \\.\pipe\ghostwin-hook
  │
GhostWin 훅 서버 (App 프로세스, 백그라운드 스레드)
  │
  ├─ JSON 파싱 → 이벤트 타입 확인
  ├─ 세션 매칭 (sessionId 또는 CWD+PID)
  ├─ Dispatcher.BeginInvoke → UI 스레드
  │   ├─ AgentState 전환
  │   ├─ 알림 패널 항목 추가 (Notification 이벤트 시)
  │   └─ Toast 발사 (창 비활성 시)
  └─ 응답 JSON 반환 ({"ok": true})
```

#### 2.1.2 세션 매칭 전략

Claude Code는 ConPTY 자식 프로세스 안에서 실행됩니다. 훅이 실행될 때 어느 탭의 세션인지 매칭해야 합니다.

**전략 (우선순위 순)**:

1. **`GHOSTWIN_SESSION_ID` 환경변수** — GhostWin이 ConPTY 세션 생성 시 환경변수로 주입. 가장 정확
2. **CWD 매칭** — Claude Code JSON의 `cwd` 필드와 SessionInfo.Cwd 비교. 동일 CWD 복수 세션 시 모호
3. **PID 매칭** — ConPTY 자식 프로세스 트리 순회. 복잡하지만 정확

v1에서는 **환경변수 + CWD 폴백** 조합으로 구현.

#### 2.1.3 지원 이벤트

| 이벤트 | AgentState 전환 | 알림 패널 | Toast |
|--------|:---------------:|:---------:|:-----:|
| `stop` | → Idle | — | — |
| `notify` (idle_prompt) | → WaitingForInput | 항목 추가 | 조건부 발사 |
| `notify` (permission_prompt) | → WaitingForInput | "권한 필요" 항목 | 조건부 발사 |
| `prompt` (UserPromptSubmit) | → Running | — | — |
| `cwd-changed` | — (CWD 갱신만) | — | — |

#### 2.1.4 ghostwin-hook.exe CLI

```
ghostwin-hook.exe stop              # stdin JSON 읽기 → stop 이벤트 전송
ghostwin-hook.exe notify            # stdin JSON 읽기 → notify 이벤트 전송
ghostwin-hook.exe prompt            # stdin JSON 읽기 → prompt 이벤트 전송
ghostwin-hook.exe set-status idle   # 직접 상태 설정 (디버깅/테스트용)
```

- C# 단일 파일 프로젝트, self-contained publish (< 5MB)
- GhostWin.App 출력 디렉토리에 동일 배치
- Named Pipe 연결 실패 시 exit 0 (Claude Code 훅이 오류로 처리하지 않도록)

### FR-07: git branch/PR 표시

**핵심 아이디어**: 각 세션의 CWD에서 git 상태를 읽어 사이드바에 표시.

#### 2.2.1 데이터 모델

```csharp
// SessionInfo에 추가
public string GitBranch { get; set; } = "";
public string GitPrInfo { get; set; } = "";  // "PR #42" 또는 ""
```

#### 2.2.2 감지 방법

- **branch**: `git -C {cwd} branch --show-current` (CWD 변경 시 + 5초 폴링)
- **PR**: `gh pr view --json number,state -q .number` (30초 폴링, `gh` 미설치 시 스킵)

#### 2.2.3 사이드바 UI

기존 CWD 표시 아래에 branch + PR 정보 추가:

```
┌──────────────────────────────────┐
│ ⚡ Terminal                       │
│   C:\Users\project               │
│   feat/auth  PR #42             │  ← 새로 추가
└──────────────────────────────────┘
```

---

## 3. 구현 순서 (6 Waves)

| Wave | 범위 | 의존 | 검증 | 예상 |
|:----:|------|:---:|------|:----:|
| **W1** | Named Pipe 서버 (C#, NamedPipeServerStream, 백그라운드) | — | 파이프 연결 + echo 테스트 | 2시간 |
| **W2** | ghostwin-hook.exe CLI (C# 단일 파일, stdin→Pipe 전송) | W1 | `echo '{"event":"stop"}' \| ghostwin-hook stop` | 1.5시간 |
| **W3** | 이벤트 라우팅 (JSON 파싱 → AgentState 전환 + 알림) | W1,W2 | Stop/Notify → AgentState 변경 확인 | 1.5시간 |
| **W4** | 세션 매칭 (환경변수 주입 + CWD 폴백) | W3 | 멀티 탭에서 올바른 탭 매칭 | 1시간 |
| **W5** | git branch 감지 + 사이드바 표시 | — | branch 이름 표시 확인 | 1시간 |
| **W6** | Claude Code 실제 연동 + PR 표시 + 통합 검증 | W1-W5 | Claude Code 세션에서 Stop → Idle 전환 | 1시간 |

**총 예상**: ~8시간 (2일)

---

## 4. 변경 파일 예상

### 4.1 신규 프로젝트 (1개)

| 프로젝트 | 내용 |
|---------|------|
| `src/GhostWin.Hook/` | ghostwin-hook.exe CLI (C# 콘솔 앱, 단일 파일) |

### 4.2 신규 파일 (5~7개)

| 파일 | 프로젝트 | 내용 |
|------|---------|------|
| `HookPipeServer.cs` | GhostWin.Services | Named Pipe 서버 (NamedPipeServerStream) |
| `IHookPipeServer.cs` | GhostWin.Core/Interfaces | 훅 서버 인터페이스 |
| `HookMessage.cs` | GhostWin.Core/Models | 훅 메시지 모델 (event, sessionId, data) |
| `GitStatusService.cs` | GhostWin.Services | git branch/PR 폴링 서비스 |
| `IGitStatusService.cs` | GhostWin.Core/Interfaces | git 서비스 인터페이스 |
| `Program.cs` | GhostWin.Hook | ghostwin-hook.exe 진입점 |

### 4.3 변경 파일 (6~8개)

| 파일 | 변경 내용 |
|------|----------|
| `SessionInfo.cs` | GitBranch, GitPrInfo 프로퍼티 |
| `WorkspaceInfo.cs` | GitBranch, GitPrInfo 미러링 |
| `WorkspaceItemViewModel.cs` | GitBranch, GitPrInfo 바인딩 |
| `MainWindow.xaml` | 사이드바에 branch/PR TextBlock |
| `App.xaml.cs` | HookPipeServer + GitStatusService DI + 시작/종료 |
| `OscNotificationService.cs` | Named Pipe 이벤트 통합 라우팅 |
| `SessionManager.cs` | GHOSTWIN_SESSION_ID 환경변수 주입 |
| `GhostWin.sln` | GhostWin.Hook 프로젝트 추가 |

---

## 5. 설계 결정 (Design 단계에서 확정할 항목)

| # | 결정 항목 | 선택지 | 현재 기울기 |
|:-:|----------|--------|:-----------:|
| D-1 | Named Pipe 서버 구현 | A: System.IO.Pipes (C#) / B: Win32 CreateNamedPipe (C++) | **A** (C# 프로세스 내) |
| D-2 | ghostwin-hook.exe 빌드 | A: self-contained single-file / B: framework-dependent | **A** (PATH 의존 없음) |
| D-3 | 세션 매칭 | A: 환경변수+CWD / B: PID 트리 순회 / C: 환경변수만 | **A** |
| D-4 | git 폴링 방식 | A: Process.Start("git") / B: LibGit2Sharp | **A** (외부 의존 없음) |
| D-5 | OSC vs Named Pipe 우선순위 | A: Named Pipe 우선 / B: OSC 우선 / C: 동등 (먼저 온 것 처리) | **C** |
| D-6 | PR 감지 | A: `gh` CLI / B: GitHub REST API | **A** (설치 여부 런타임 확인) |

---

## 6. 리스크 및 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| **Named Pipe 보안** | 중 | DACL로 현재 사용자만 접근 + 세션 ID 검증 |
| **ghostwin-hook.exe 크기** | 낮 | self-contained single-file ~5MB. trimmed publish로 2~3MB 가능 |
| **Claude Code stdin JSON 형식 변경** | 낮 | 방어적 파싱 (System.Text.Json, unknown 프로퍼티 무시) |
| **동일 CWD 복수 세션** | 중 | GHOSTWIN_SESSION_ID 환경변수가 1순위. CWD는 폴백 |
| **gh CLI 미설치** | 낮 | `Process.Start` 실패 시 graceful skip (branch만 표시) |
| **Named Pipe 서버 종료 순서** | 중 | MainWindow.OnClosing에서 HookPipeServer.Stop() 호출 (엔진 DetachCallbacks 전) |

---

## 7. Phase 6-A/B 교훈 적용

| 교훈 | Phase 6-C 적용 |
|------|---------------|
| Design에서 "의도적 간소화" 명시 | D-1~D-6 결정 목록 미리 정의 |
| I/O thread 안전 패턴 다이어그램 | Named Pipe 스레드 → Dispatcher.BeginInvoke 경로 명시 |
| ListBox 포커스 문제 | 신규 UI 요소에 Focusable=False 기본 적용 |
| 렌더 스레드 경쟁 조건 | Named Pipe 이벤트는 UI 스레드에서만 상태 변경 (경쟁 없음) |
| msbuild 전체 빌드 의무 | GhostWin.Hook 프로젝트 추가 후 솔루션 빌드 검증 |

---

## 8. 성공 기준

| # | 기준 | 중요도 |
|:-:|------|:------:|
| 1 | `ghostwin-hook stop` → 해당 탭 AgentState = Idle | 필수 |
| 2 | `ghostwin-hook notify` → WaitingForInput + 알림 패널 항목 | 필수 |
| 3 | Claude Code 실제 세션에서 Stop → 탭 상태 즉시 전환 | 필수 |
| 4 | 멀티 탭에서 세션 매칭 정확 (올바른 탭에 이벤트 라우팅) | 필수 |
| 5 | 사이드바에 git branch 표시 | 필수 |
| 6 | `gh` 설치 시 PR 번호 표시 | 선택 |
| 7 | Named Pipe 응답 < 10ms | 필수 |
| 8 | GhostWin 종료 시 Named Pipe 서버 정상 종료 | 필수 |

---

## 9. 다음 단계

1. **`/pdca design phase-6-c-external-integration`** — 구현 명세 (Wave별 코드 수준 상세)
2. **구현** — Wave 1 → 6 순서
3. **Claude Code 실제 연동 테스트** — 가장 중요한 검증

---

## 참조

- **PRD**: `docs/00-pm/phase-6-c-external-integration.prd.md`
- **Phase 6-A/B 아카이브**: `docs/archive/2026-04/phase-6-a-osc-notification-ring/`, `docs/archive/2026-04/phase-6-b-notification-infra/`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md` (§1.7 CLI/Socket API, §2.5 Named Pipe)
- **Claude Code Hooks**: [code.claude.com/docs/en/hooks-guide](https://code.claude.com/docs/en/hooks-guide)
- **로드맵**: `docs/01-plan/roadmap.md`

---

*Phase 6-C Plan v1.0 — External Integration (2026-04-16)*
