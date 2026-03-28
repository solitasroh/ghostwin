# GhostWin Terminal — Project Onboarding

> Windows CLI 개발자를 위한 고성능 터미널 에뮬레이터 + AI 에이전트 멀티플렉서

---

## 1. 왜 만드는가

### 문제 인식

Windows에서 Claude Code CLI, nvim 등 CLI 중심 워크플로우를 사용하는 개발자들에게는
macOS의 Ghostty + cmux에 대응하는 도구가 없다.

| 현재 Windows 선택지 | 한계 |
|-------------------|------|
| **Windows Terminal** | GPU 가속은 있으나 멀티플렉서 없음, AI 에이전트 연동 없음 |
| **WezTerm** | 크로스플랫폼이지만 Ghostty 대비 입력 지연, 메모리 효율 열세 |
| **tmux on WSL** | 네이티브가 아님, GPU 렌더링 불가, Windows 통합 약함 |
| **cmux** | macOS 전용 (Swift + AppKit), Windows 포팅 요청만 이슈로 존재 |

### 핵심 페인 포인트

- Claude Code를 여러 세션 동시 운영할 때 **어떤 세션이 입력을 기다리는지 알 수 없음**
- 팀 에이전트를 tmux 세션으로 관리할 때 **상태 추적과 알림이 없음**
- 기존 Windows 터미널들은 **AI 에이전트 워크플로우를 고려하지 않고 설계됨**
- 한국어 IME 처리가 GPU 가속 터미널에서 불안정

---

## 2. 무엇을 만드는가

### 제품 비전

**Ghostty의 성능 철학** + **cmux의 AI 에이전트 UX**를 Windows 네이티브로 구현한
경량 고성능 터미널 에뮬레이터 겸 멀티플렉서.

### 타겟 사용자

- Windows에서 Claude Code CLI를 주력으로 사용하는 개발자
- nvim, SSH, WSL 등 CLI 환경에서 작업하는 개발자
- 여러 AI 에이전트 세션을 동시에 운영하는 팀/개인

### 킬러 피처

#### 1) 경량 + 최적 성능
- Ghostty 수준의 빠른 VT 파싱 (SIMD 최적화)
- GPU 인스턴싱 기반 렌더링 — 단일 draw call로 전체 화면 렌더링
- 최소 메모리 사용, 유휴 시 GPU 점유율 ~0%
- Windows Terminal AtlasEngine 이상의 렌더링 성능 목표

#### 2) AI 에이전트 멀티플렉서
- cmux 스타일 수직 탭 사이드바 — git branch, PR 상태, 리스닝 포트 표시
- **에이전트 알림 시스템** — 세션이 입력 대기 시 탭에 시각적 알림 (notification ring)
- 통합 알림 패널 — 모든 대기 중인 에이전트를 한눈에 확인
- 미읽음 알림 탭으로 즉시 점프
- 수평/수직 pane 분할

#### 3) Claude Code 팀 에이전트 지원
- tmux 호환 세션 관리 — 팀 에이전트를 tmux 세션처럼 생성/관리/전환
- OSC hooks 연동 — Claude Code의 Stop/Notification 이벤트 수신
- 탭별 에이전트 상태 배지 (대기 / 실행 중 / 오류 / 완료)
- Named pipe 기반 훅 서버로 외부 도구 연동

#### 4) Windows 네이티브 통합
- ConPTY 기반 안정적인 PTY — cmd, PowerShell, WSL, SSH 모두 지원
- 한국어 IME 완전 지원 (TSF 연동)
- Win32 Toast 알림
- WinUI3 네이티브 룩앤필

---

## 3. 어떤 기술로 만드는가

### 설계 영감 (레퍼런스)

| 프로젝트 | 가져오는 것 | 라이선스 |
|---------|-----------|---------|
| **Ghostty** (v1.3.1) | VT 파싱 성능 철학, libghostty-vt 코어 | MIT |
| **cmux** (v0.62.2) | AI 에이전트 UX — 수직 탭, 알림 링, 멀티플렉서 패턴 | AGPL-3.0 (듀얼) |
| **Windows Terminal** | AtlasEngine D3D11 렌더러 패턴, ConPTY 사용법 | MIT |
| **ghostling** | libghostty-vt C API 사용 레퍼런스 구현 | MIT |

### 기술 스택

| 레이어 | 기술 | 선택 이유 |
|-------|------|----------|
| **VT 파싱** | libghostty-vt (Zig/C, zero-dep) | Ghostty 실전 검증, SIMD 최적화, Windows CI 지원, C API로 FFI 용이 |
| **렌더링** | DirectX 11 + HLSL | Windows Terminal AtlasEngine이 검증한 패턴, SM 4.0+ 폭넓은 호환 |
| **폰트** | DirectWrite | ClearType, 리가처, 한국어 글리프 지원 |
| **UI** | WinUI3 (C++/WinRT) | Windows 11 네이티브, SwapChainPanel로 D3D11 통합 |
| **PTY** | ConPTY (Win32 API) | Windows 공식 PTY, VS Code/Windows Terminal 동일 방식 |
| **빌드** | C++20, Zig 0.15.x | D3D11 직접 접근 + libghostty-vt 빌드 |

### 아키텍처 개요

```
┌─────────────────────────────────────────────┐
│  Layer 4 · UI Shell                         │
│  WinUI3 — 탭, pane, 알림, 설정              │
├─────────────────────────────────────────────┤
│  Layer 3 · Renderer                         │
│  DirectX 11 — GPU 인스턴싱, glyph atlas     │
├─────────────────────────────────────────────┤
│  Layer 2 · VT Core                          │
│  libghostty-vt — VT 파싱, 터미널 상태       │
├─────────────────────────────────────────────┤
│  Layer 1 · PTY                              │
│  ConPTY — 비동기 I/O, IOCP                  │
├─────────────────────────────────────────────┤
│  Layer 0 · Process                          │
│  cmd / PowerShell / WSL / SSH               │
└─────────────────────────────────────────────┘
```

### 스레드 모델

4개의 전용 스레드가 lock-free 큐로 통신:

- **UI 스레드** — XAML, 사용자 입력, 탭/pane 레이아웃
- **렌더 스레드** — D3D11 Present, glyph atlas 업데이트
- **파싱 스레드** — libghostty-vt 호출, render state 갱신
- **I/O 스레드** — ConPTY 비동기 read/write (IOCP)

---

## 4. 기술 스택 의사결정 배경 (ADR 요약)

### libghostty-vt를 VT 코어로 채택한 이유

**장점:**
- Ghostty에서 실전 검증된 파서 (v1.3.1 안정 릴리즈 기반)
- SIMD 최적화 (Highway + simdutf) — 대용량 출력에서 성능 우위
- zero-dependency — libc 의존성도 없음
- Windows CI가 ghostty 리포에서 이미 동작 중

**리스크:**
- libghostty-vt 자체는 **v0.1.0 극초기**, API 불안정 (공식 경고: "breaking changes expected")
- ghostling(레퍼런스 구현)은 아직 Windows 미지원 (libghostty-vt 라이브러리 자체는 지원)
- API 변경에 대비한 래퍼 레이어(VtCore) 격리 필수

**대안 검토:**
- 자체 VT 파서 작성 → 개발 비용 과다, 표준 준수 검증 부담
- libvterm → C 라이브러리, SIMD 없음, 성능 열위
- Windows Terminal 내부 파서 → 분리 설계 아님, 재사용 어려움

### DirectX 11을 선택한 이유

- Windows Terminal AtlasEngine이 D3D11로 **실전 검증**
- Shader Model 4.0+ — Windows 7 이후 거의 모든 GPU에서 지원
- D3D12 대비 API 단순, 싱글 스레드 렌더링에 적합
- GPU 인스턴싱으로 **단일 draw call** 전체 화면 렌더링 가능

### WinUI3을 선택한 이유

- Windows 11 네이티브 UI 프레임워크
- SwapChainPanel로 D3D11 렌더링 표면을 XAML 트리에 직접 통합
- Win32 interop 지원 — ConPTY, Named Pipe 등 기존 API와 공존 가능

---

## 5. cmux에서 이식할 기능 목록

cmux (v0.62.2, macOS 전용)의 핵심 기능을 Windows 네이티브로 재구현:

### 1차 목표 (MVP)

| cmux 기능 | GhostWin 구현 방식 |
|----------|-------------------|
| 수직 탭 사이드바 | WinUI3 커스텀 컨트롤 |
| git branch / PR 상태 표시 | git CLI 연동 |
| Notification ring (에이전트 대기 알림) | pane 테두리 색상 변경 |
| 알림 패널 + 미읽음 점프 | WinUI3 패널 |
| 수평/수직 pane 분할 | 중첩 레이아웃 매니저 |
| OSC hooks (9/99/777) | VT 시퀀스 파싱에서 직접 처리 |
| Claude Code Stop/Notification 이벤트 | Named pipe 훅 서버 |
| 탭별 에이전트 상태 배지 | UI 상태 바인딩 |
| 세션 복원 (레이아웃, 작업 디렉토리) | 설정 파일 직렬화 |

### 2차 목표

| cmux 기능 | GhostWin 구현 방식 |
|----------|-------------------|
| 인앱 브라우저 | WebView2 (Edge 기반) 통합 |
| 워크스페이스 시스템 | 탭 그룹 관리 |
| CLI / Socket API (자동화) | Named Pipe 기반 IPC |
| tmux 호환 세션 (팀 에이전트) | 세션 매니저 + 원격 attach |

---

## 6. 로드맵

| Phase | 목표 | 완료 기준 |
|-------|------|----------|
| **Phase 1** | libghostty-vt Windows 빌드 검증 | `.lib` 정적 라이브러리 빌드, C API 호출 동작 확인 |
| **Phase 2** | ConPTY + VT 파싱 파이프라인 | cmd.exe 실행, 키 입력/출력 왕복 동작 |
| **Phase 3** | DirectX 11 렌더러 | GPU 가속 렌더링, 기본 터미널 60fps+ |
| **Phase 4** | WinUI3 UI + 멀티플렉서 | 탭, pane 분할, 에이전트 알림 동작 |
| **Phase 5** | AI 에이전트 특화 + 최적화 | Claude Code 멀티 세션 워크플로우 완성 |

---

## 7. 알려진 리스크

| 리스크 | 심각도 | 대응 방안 |
|--------|--------|----------|
| libghostty-vt API 불안정 (v0.1.0) | **높음** | VtCore 래퍼로 격리, API 변경 추적 자동화 |
| libghostty-vt 빌드 시 libxml2 이슈 | **높음** | vcpkg 사전 설치로 우회 |
| Zig 0.15.x 빌드 시스템 변경 | **중간** | Zig 버전 고정, CI에서 검증 |
| SwapChainPanel DPI 변경 시 깜박임 | **중간** | Windows Terminal 코드 참고 |
| 한국어 IME + GPU 터미널 충돌 | **중간** | TSF(Text Services Framework) 직접 연동 |
| cmux AGPL-3.0 라이선스 | **중간** | 코드 직접 포팅 금지, UX 패턴만 참고하여 클린룸 재구현 |

---

## 8. 참고 자료

### 핵심 레퍼런스 코드
- **ghostling** — libghostty-vt C API 사용법 레퍼런스 (github.com/ghostty-org/ghostling)
- **AtlasEngine** — D3D11 렌더러 패턴 (github.com/microsoft/terminal `src/renderer/atlas/`)
- **ConPTY 샘플** — EchoCon, MiniTerm, GUIConsole (github.com/microsoft/terminal `samples/ConPTY/`)

### 기술 문서
- libghostty-vt API 문서: libghostty.tip.ghostty.org
- ConPTY 공식 문서: learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
- WinUI3 SwapChainPanel: learn.microsoft.com/windows/apps/develop/win2d/

### 프로젝트 현황 (2026-03-28 기준)
- Ghostty: v1.3.1 안정, MIT
- libghostty-vt: v0.1.0, API 불안정, Zig 0.15.2+ 필요
- cmux: v0.62.2, macOS 전용, AGPL-3.0 (듀얼), ~10,940 스타
- Windows Terminal AtlasEngine: D3D11, GPU 인스턴싱, 20바이트 인스턴스 구조체

---

*GhostWin Terminal — Onboarding Document v0.2*
*최종 업데이트: 2026-03-28*
