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
- Ghostty 수준의 빠른 VT 파싱 (Phase 1에서 검증 완료, 7/7 테스트 PASS)
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
| **VT 파싱** | libghostty-vt (Zig/C, zero-dep) via DLL | Ghostty 실전 검증, DLL로 GNU/MSVC 격리, C API로 FFI 용이 |
| **렌더링** | DirectX 11 + HLSL | Windows Terminal AtlasEngine이 검증한 패턴, SM 4.0+ 폭넓은 호환 |
| **폰트** | DirectWrite | Grayscale AA, 리가처, 한국어 글리프 지원 |
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
- **[Phase 1 검증 완료]** C 브릿지 + VtCore 래퍼로 7/7 테스트 PASS

**리스크:**
- libghostty-vt 자체는 **v0.1.0 극초기**, API 불안정 (공식 경고: "breaking changes expected")
- ghostling(레퍼런스 구현)은 아직 Windows 미지원 (libghostty-vt 라이브러리 자체는 지원)
- API 변경에 대비한 래퍼 레이어(VtCore) 격리 필수
- **[Phase 1 발견]** GNU 타겟 static lib은 MSVC 링커와 COMDAT 불호환 — DLL로 격리 필요 (ADR-003)

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

### Phase 5 목표 (멀티세션 UI)

| cmux 기능 | GhostWin 구현 방식 | 우선순위 |
|----------|-------------------|:--------:|
| 수직 탭 사이드바 | WinUI3 커스텀 컨트롤 (세션 목록, 활성 탭 하이라이트) | 필수 |
| 수평/수직 pane 분할 | 중첩 Grid 레이아웃 매니저 | 필수 |
| 세션 복원 (레이아웃, 작업 디렉토리) | 설정 파일 JSON 직렬화 | 선택 |
| 설정 패널 | 폰트, 색상, 키바인딩 UI | 필수 |

### Phase 6 목표 (AI 에이전트 특화)

| cmux 기능 | GhostWin 구현 방식 | 우선순위 |
|----------|-------------------|:--------:|
| OSC hooks (9/99/777) | VT 시퀀스 파싱에서 직접 처리 | 필수 |
| Notification ring (에이전트 대기 알림) | pane 테두리 색상 + 탭 배지 | 필수 |
| 알림 패널 + 미읽음 점프 | WinUI3 패널 | 필수 |
| Claude Code Stop/Notification 이벤트 | Named pipe 훅 서버 | 필수 |
| 탭별 에이전트 상태 배지 | UI 상태 바인딩 | 필수 |
| git branch / PR 상태 표시 | git CLI 연동 | 선택 |

### 이후 목표

| cmux 기능 | GhostWin 구현 방식 |
|----------|-------------------|
| 인앱 브라우저 | WebView2 (Edge 기반) 통합 |
| 워크스페이스 시스템 | 탭 그룹 관리 |
| CLI / Socket API (자동화) | Named Pipe 기반 IPC |
| tmux 호환 세션 (팀 에이전트) | 세션 매니저 + 원격 attach |

---

## 6. 로드맵

### 완료된 Phase (Phase 1~4)

| Phase | 목표 | 완료 기준 | Match Rate | 완료일 |
|-------|------|----------|:----------:|:------:|
| **Phase 1** | libghostty-vt Windows 빌드 | DLL + C 브릿지 + VtCore, 10 테스트 | 96% | 2026-03-29 |
| **Phase 2** | ConPTY + VT 파이프라인 | cmd.exe 왕복 동작 | 100% | 2026-03-29 |
| **Phase 3** | DirectX 11 렌더러 | GPU 인스턴싱, 2-pass 렌더링, 글리프 아틀라스 | 96.6% | 2026-03-30 |
| **Phase 4** | WinUI3 셸 + 렌더링 완성도 | 7개 Sub-Feature (A~G) 전체 완료 | 94~100% | 2026-04-01 |

#### Phase 4 상세 (7개 Sub-Feature)

| ID | Feature | 내용 | Match Rate |
|----|---------|------|:----------:|
| A | winui3-shell | SwapChainPanel, 커스텀 타이틀바, 렌더 스레드 분리 | 94% |
| B | tsf-ime | TSF + Hidden HWND 한글 IME (ADR-011) | 99% |
| C | cleartype-subpixel | Composition → Grayscale AA + 감마 보정 (ADR-010) | — |
| D | nerd-font-fallback | 4단계 폴백 체인 (Primary→CJK→NerdFont→Emoji) | 96% |
| E | quadinstance-opt | 68B R32 → 32B StructuredBuffer | 100% |
| F | dpi-aware-rendering | DPI 변환 행렬 + atlas 재생성 | 98.6% |
| G | mica-backdrop | MicaBackdrop + try/catch 폴백 | — |

#### Phase 4에서 달성한 것 (사실 기반)

**달성됨:**
- 단일 세션 터미널 엔진 (Grayscale AA 83/100, ClearType 미지원 — WT 대비 텍스트 선명도 열위)
- 한글 IME (TSF + Hidden HWND, 94 테스트)
- Nerd Font + CJK advance-centering
- 12개 ADR로 기술 결정 문서화

**코드 완료, 런타임 검증 미수행:**
- DPI-aware 래스터라이즈 (코드 구현됨, 고DPI 모니터 육안 검증 미실행)
- 유휴 GPU < 1% (Waitable swapchain + Sleep(1) 코드 완료, GPU-Z 실측 미실행)

**미달성 (원래 Phase 4 범위였으나 Phase 5로 이관):**
- 멀티 탭/세션 (빈 ListView placeholder만 존재)
- Pane 분할
- 에이전트 알림
- 설정 패널

### 진행할 Phase (Phase 5~6)

| Phase | 목표 | 완료 기준 | 상태 |
|-------|------|----------|:----:|
| **Phase 5** | **멀티세션 UI** | 탭 추가/제거/전환, pane 분할, 다중 ConPTY, 설정 패널 | ✅ 거의 완료 (M-8~M-10.5) |
| **Phase 6** | **AI 에이전트 특화** | OSC hooks, 알림 링, 에이전트 배지, Named pipe 서버 | 🎯 **다음 우선순위 (M-11 직후)** |

### 확정 실행 순서 (2026-04-16 갱신)

기존 WPF 마일스톤(M-11~M-13) + M-11.5 (E2E 자동화) + Phase 6 통합 순서:

```
M-11 세션 복원 (cmux)
  → M-11.5 E2E 테스트 자동화 체계화 (기반 인프라)
  → 🎯 Phase 6-A: OSC hook + 알림 링 (핵심 가설 검증)
  → 🎯 Phase 6-B: 알림 패널 + 배지 + Toast
  → (M-12 설정 UI / 🎯 Phase 6-C: Named pipe + git 병행)
  → M-13 한글 입력 미리보기
```

**근거**:
- Phase 6 는 onboarding 이 정의한 **이 프로젝트의 존재 이유** (§6 인용: "이 Phase 가 GhostWin 의 존재 이유 — AI 에이전트 멀티플렉서")
- Phase 6-A 는 멀티세션 UI(완료) 만 의존 — M-12/M-13 대기 불필요
- M-12 는 3대 비전 축 어디에도 직접 기여 안 함 → Phase 6-B 뒤로 후순위 조정
- 핵심 가설(OSC hook 캡처) 검증 후 후속 우선순위 재평가

상세 마일스톤 문서: Obsidian `Milestones/roadmap.md`

### Phase 5: 멀티세션 UI (다음 착수)

Phase 4까지 단일 세션 터미널의 렌더링 엔진은 완성되었다.
Phase 5에서는 이 엔진 위에 **제품으로서의 UI**를 구축한다.

| FR | 기능 | 설명 | 우선순위 |
|----|------|------|:--------:|
| FR-01 | **탭 매니저** | 수직 사이드바 탭 목록, 탭 추가(+)/닫기(x)/전환(클릭), Ctrl+T/W 단축키 | 필수 |
| FR-02 | **다중 ConPTY** | 탭별 독립 ConPTY 세션, 각 세션 독립 RenderState/GlyphAtlas | 필수 |
| FR-03 | **탭 UI** | 활성 탭 하이라이트, 탭 이름 (프로세스명/CWD), 탭 순서 변경 드래그 | 필수 |
| FR-04 | **Pane 분할** | 수평/수직 분할, 중첩 Grid, Ctrl+Shift+D/E | 필수 |
| FR-05 | **설정 패널** | 폰트 선택, 크기, 색상 테마, 키바인딩 커스터마이즈 | 필수 |
| FR-06 | **키보드 내비게이션** | Ctrl+Tab/Shift+Tab 탭 전환, Alt+1~9 탭 점프 | 필수 |
| FR-07 | **세션 복원** | 앱 종료 시 탭/pane 레이아웃 + CWD 저장, 재시작 시 복원 | 선택 |

### Phase 6: AI 에이전트 특화

Phase 5의 멀티세션 UI 위에 **cmux의 차별화 기능**을 구현한다.
이 Phase가 GhostWin의 존재 이유 — "AI 에이전트 멀티플렉서".

| FR | 기능 | 설명 | 우선순위 |
|----|------|------|:--------:|
| FR-01 | **OSC hooks 파싱** | OSC 9/99/777 + 커스텀 시퀀스, VT 파서에서 이벤트 발행 | 필수 |
| FR-02 | **에이전트 알림 링** | 세션이 입력 대기 시 탭에 시각적 알림 (컬러 링/배지) | 필수 |
| FR-03 | **알림 패널** | 모든 대기 중 에이전트 목록, 클릭으로 해당 탭 점프 | 필수 |
| FR-04 | **에이전트 상태 배지** | 탭별 아이콘: 대기/실행중/오류/완료 | 필수 |
| FR-05 | **Named pipe 훅 서버** | Claude Code Stop/Notification 이벤트 수신 | 필수 |
| FR-06 | **Toast 알림** | 창이 비활성일 때 Windows Toast로 에이전트 대기 알림 | 필수 |
| FR-07 | **git branch/PR 표시** | 사이드바에 각 세션의 git branch, PR 상태 | 선택 |

---

## 7. 알려진 리스크

| 리스크 | 심각도 | 대응 방안 | 상태 |
|--------|--------|----------|:----:|
| libghostty-vt API 불안정 (v0.1.0) | **높음** | VtCore 래퍼로 격리, API 변경 추적 자동화 | 대응 완료 |
| GNU static lib ↔ MSVC 링커 불호환 | **높음** | DLL 방식으로 GNU/MSVC 격리 (ADR-003) | **해결됨** |
| 한국어 Windows + Ninja 로케일 충돌 | **중간** | `/showIncludes` 접두사 영어 패치 (`build_ghostwin.ps1`) | **해결됨** |
| SwapChainPanel DPI 깜박임 | **중간** | DPI 변환 행렬 + atlas 재생성 (Phase 4-F) | **해결됨** |
| 한국어 IME + GPU 터미널 충돌 | **중간** | TSF + Hidden HWND 직접 연동 (ADR-011) | **해결됨** |
| cmux AGPL-3.0 라이선스 | **중간** | 코드 직접 포팅 금지, UX 패턴만 참고하여 클린룸 재구현 | 유지 |
| Zig 0.15.x 빌드 시스템 변경 | **중간** | Zig 버전 고정 (`.zig-version`), CI에서 검증 | 유지 |
| 다중 ConPTY 세션 메모리 관리 | **중간** | 세션별 리소스 격리 + 탭 닫기 시 완전 해제 | Phase 5 대응 |
| WinUI3 XAML 동적 레이아웃 성능 | **낮음** | 가상화 ListView, 필요 시 커스텀 컨트롤 | Phase 5 대응 |

---

## 8. 참고 자료

### 핵심 레퍼런스 코드
- **ghostling** — libghostty-vt C API 사용법 레퍼런스 (github.com/ghostty-org/ghostling)
- **AtlasEngine** — D3D11 렌더러 패턴 (github.com/microsoft/terminal `src/renderer/atlas/`)
- **ConPTY 샘플** — EchoCon, MiniTerm, GUIConsole (github.com/microsoft/terminal `samples/ConPTY/`)
- **cmux UX** — 수직 탭, 알림 링, 에이전트 상태 패턴 (github.com/nicholasgasior/cmux)

### 기술 문서
- libghostty-vt API 문서: libghostty.tip.ghostty.org
- ConPTY 공식 문서: learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
- WinUI3 SwapChainPanel: learn.microsoft.com/windows/apps/develop/win2d/

### 프로젝트 현황 (2026-04-01 기준)
- Ghostty: v1.3.1 안정, MIT
- libghostty-vt: v0.1.0, API 불안정, Zig 0.15.2+ 필요
- **Phase 1~4 완료**: 단일 세션 터미널 엔진 완성 (VT+ConPTY+DX11+WinUI3+IME+DPI)
- **Phase 5 착수 예정**: 멀티세션 UI (탭, pane, 설정)
- cmux: v0.62.2, macOS 전용, AGPL-3.0 (듀얼), ~10,940 스타
- 테스트: 10 unit + 94 IME E2E

### ADR 목록 (12건)

| ADR | 결정 |
|-----|------|
| 001 | windows-gnu + simd=false (CRT 독립) |
| 002 | C 브릿지 레이어 (MSVC typedef 충돌 회피) |
| 003 | DLL 방식 유지 (GNU-MSVC COMDAT 불호환) |
| 004 | MSVC /utf-8 강제 (CP949 충돌) |
| 005 | SDK 22621 버전 고정 |
| 006 | vt_mutex 스레드 안전성 |
| 007 | R32 QuadInstance → 32B StructuredBuffer |
| 008 | 2-Pass 렌더링 (CJK 클리핑 방지) |
| 009 | Code-only WinUI3 + CMake |
| 010 | Composition Grayscale AA + 감마 보정 |
| 011 | TSF + Hidden Win32 HWND (IME) |
| 012 | CJK Advance-Centering |

---

*GhostWin Terminal — Onboarding Document v0.6*
*최종 업데이트: 2026-04-16 (M-11.5 E2E 자동화 체계화 추가 — M-11 과 Phase 6-A 사이)*
