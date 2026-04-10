# PRD: Mouse Input for GhostWin Terminal

> **Feature**: mouse-input
> **Project**: GhostWin Terminal
> **Author**: PM Agent Team (Discovery + Strategy + Research + PRD Synthesis)
> **Date**: 2026-04-10
> **Status**: Draft (v0.2 -- benchmarking update)
> **Milestone**: M-10 (Terminal Basic Operations)
> **v0.2 Update**: 2026-04-10 -- 4개 터미널 벤치마킹 + v0.1 smoke 성능 이슈 반영 (section 8, 9)

---

## 1. Executive Summary

GhostWin Terminal은 현재 키보드 입력만 지원하며, 마우스 입력(클릭/스크롤/텍스트 선택)이 완전히 미구현 상태이다. 이는 vim, tmux, htop 등 마우스 인터랙션에 의존하는 TUI 앱의 사용을 불가능하게 하고, 텍스트 선택-복사라는 가장 기본적인 터미널 조작조차 불가능하게 만든다. 본 PRD는 WPF HwndHost의 WndProc에서 Win32 마우스 메시지를 캡처하고, ghostty의 C API(mouse_event + mouse_encode)를 통해 VT 마우스 시퀀스로 변환하여 ConPTY에 전달하는 3계층 구조를 정의한다. 텍스트 선택은 터미널이 마우스 모드를 비활성화한 경우(none 모드) WPF 측에서 직접 관리한다.

---

## 2. Opportunity Discovery (Opportunity Solution Tree)

### 2.1 Target Outcome

**터미널 기본 조작 완성** -- 마우스 클릭/스크롤/선택이 동작하여 "일상 터미널"로 사용 가능한 수준 달성.

### 2.2 Opportunity Map

```
[Outcome: 일상 터미널 사용 가능]
  |
  +-- [O1] TUI 앱에서 마우스 클릭/드래그 불가
  |     +-- [S1a] WndProc 마우스 메시지 캡처 + ghostty mouse_encode VT 변환
  |     +-- [S1b] gw_session_write로 VT 시퀀스 직접 전달 (mouse_encode 우회)
  |
  +-- [O2] 스크롤 미지원 (scrollback 탐색 불가)
  |     +-- [S2a] WM_MOUSEWHEEL -> VT 스크롤 시퀀스 (마우스 모드 활성 시)
  |     +-- [S2b] WM_MOUSEWHEEL -> scrollback viewport 조작 (마우스 모드 비활성 시)
  |
  +-- [O3] 텍스트 선택/복사 불가
  |     +-- [S3a] 마우스 모드 none일 때 WPF 측 Selection 관리
  |     +-- [S3b] ghostty Selection.zig 직접 연동 (C API 확장 필요)
  |
  +-- [O4] 마우스 커서 모양이 항상 기본 화살표
        +-- [S4a] ghostty cursor_shape 콜백 연동 -> WPF Cursor 변경
```

### 2.3 Solution Selection

| Opportunity | Selected Solution | Rationale |
|-------------|-------------------|-----------|
| O1 | S1a | ghostty upstream이 mouse_event/mouse_encode C API를 완전 제공. 5가지 포맷(X10/UTF8/SGR/URxvt/SGR-Pixels) + 4가지 모드(x10/normal/button/any) 지원. 우회(S1b) 대비 정확도/호환성 우월 |
| O2 | S2a + S2b | 마우스 모드에 따라 분기: 활성이면 VT 인코딩, 비활성이면 scrollback viewport 이동. Windows Terminal/Alacritty 동일 패턴 |
| O3 | S3a (1차) | ghostty Selection.zig는 C API export가 없어 직접 바인딩 불가. 1차에서 WPF 측 자체 Selection 구현. 2차에서 C API 확장 검토 |
| O4 | Deferred | cursor_shape은 기능적 영향이 낮아 별도 마일스톤으로 분리 |

---

## 3. Value Proposition (JTBD 6-Part Framework)

### 3.1 Job-to-be-Done

**내가 터미널에서 vim/tmux 같은 TUI 앱을 사용할 때**, 마우스로 UI 요소를 클릭하고, 텍스트를 스크롤하고, 출력을 선택-복사하고 싶다. **그래야** 키보드만으로 모든 조작을 외우지 않아도 빠르게 작업할 수 있다.

### 3.2 Value Proposition

| Part | Content |
|------|---------|
| **Functional** | WM_LBUTTON/RBUTTON/MBUTTON + WM_MOUSEWHEEL + WM_MOUSEMOVE 메시지를 캡처하여 ghostty VT 마우스 인코딩으로 변환, ConPTY에 전달. 마우스 모드 비활성 시 scrollback + 텍스트 선택 제공 |
| **Emotional** | "이제 진짜 터미널처럼 쓸 수 있다" -- 키보드 전용의 불편함 해소 |
| **Social** | 경쟁 터미널(WT, Alacritty, WezTerm)과 동등한 마우스 지원으로 "미완성 터미널" 인식 탈피 |
| **Unique Differentiator** | ghostty의 5가지 마우스 포맷 + 4가지 모드 네이티브 지원. upstream C API 활용으로 인코딩 정확도 보장 |
| **Fear of Loss** | 마우스 미지원 시 vim/tmux/htop 사용자가 GhostWin 채택 불가. 터미널의 기본 기능 부재로 제품 신뢰도 저하 |
| **Triggering Event** | Phase 5-E pane-split 완료로 다중 pane 환경 확보. pane 내부 상호작용이 다음 자연스러운 단계 |

### 3.3 Lean Canvas

| Section | Content |
|---------|---------|
| **Problem** | 1) TUI 앱 마우스 클릭 불가 2) 스크롤 미지원으로 scrollback 탐색 불가 3) 텍스트 선택-복사 불가 |
| **Customer Segments** | 1) 터미널 파워 유저 (vim/tmux/htop) 2) 일반 사용자 (텍스트 복사 필요) 3) Windows 개발자 (WT 대안 탐색) |
| **Unique Value** | ghostty upstream의 5-format mouse encoder 네이티브 통합. 모든 마우스 프로토콜 자동 지원 |
| **Solution** | WndProc 마우스 캡처 -> C++ Engine API -> ghostty mouse_encode -> VT 시퀀스 -> ConPTY |
| **Channels** | GitHub 릴리스, 터미널 커뮤니티 (r/commandline, Hacker News) |
| **Revenue Streams** | 오픈소스 (직접 수익 없음). 사용자 기반 확대 -> 후속 기능(AI agent UX) 채택률 향상 |
| **Cost Structure** | 개발 인력 1인, 추정 2-3주. ghostty C API 바인딩 + WPF 이벤트 처리 + 텍스트 선택 구현 |
| **Key Metrics** | 1) 마우스 모드 TUI 앱 호환 테스트 통과율 2) scrollback 스크롤 응답 시간 3) 텍스트 선택-복사 성공률 |
| **Unfair Advantage** | ghostty Zig 코어의 battle-tested 마우스 인코딩 로직 직접 활용 (다른 Windows 터미널은 자체 구현) |

---

## 4. Market Research

### 4.1 User Personas

#### Persona 1: "Vim 개발자 민수"
- **Demographics**: 30대 백엔드 개발자, Windows + WSL 환경
- **Behavior**: 하루 8시간+ 터미널 사용. vim에서 마우스 클릭으로 커서 이동, 스크롤로 코드 탐색
- **Pain Point**: GhostWin에서 vim :set mouse=a 후 마우스가 전혀 반응하지 않음
- **Goal**: "내 현재 워크플로를 그대로 GhostWin에서 재현하고 싶다"
- **Adoption Blocker**: 마우스 미지원 = 즉시 이탈

#### Persona 2: "devops 엔지니어 지연"
- **Demographics**: 20대 후반, kubectl/docker/htop 상시 사용
- **Behavior**: htop에서 마우스로 프로세스 클릭, tmux에서 pane 전환
- **Pain Point**: scrollback으로 이전 로그 탐색 시 마우스 스크롤 불가
- **Goal**: "빠르게 로그 올려보고 필요한 부분 복사하고 싶다"
- **Adoption Blocker**: 스크롤/복사 불가 = 업무 효율 저하

#### Persona 3: "캐주얼 사용자 현우"
- **Demographics**: 40대, PowerShell/cmd 간헐적 사용
- **Behavior**: 터미널 출력에서 에러 메시지 선택 -> 복사 -> 검색에 붙여넣기
- **Pain Point**: 텍스트 선택이 안 되어 수동으로 타이핑
- **Goal**: "그냥 드래그해서 복사되면 된다"
- **Adoption Blocker**: 선택-복사 불가 = 가장 기본적인 기대 불충족

### 4.2 Competitive Analysis

| Feature | Windows Terminal | Alacritty | WezTerm | iTerm2 | Ghostty (macOS) |
|---------|-----------------|-----------|---------|--------|-----------------|
| Mouse Click (TUI) | Full | Full | Full | Full | Full |
| Mouse Scroll | Full | Full | Full | Full | Full |
| Text Selection | Full (shift bypass) | Full | Full (semantic) | Full (smart) | Full |
| Mouse Formats | X10/UTF8/SGR | X10/UTF8/SGR | X10/UTF8/SGR/SGR-Pixels | X10/UTF8/SGR | X10/UTF8/SGR/URxvt/SGR-Pixels |
| Mouse Modes | x10/normal/button/any | x10/normal/button/any | x10/normal/button/any | x10/normal/button/any | x10/normal/button/any |
| Selection Modifier | Shift (bypass mouse mode) | Shift | Shift | Cmd (macOS) | Shift/Cmd |
| URL Click | Yes (Ctrl+Click) | Yes | Yes | Yes (Cmd+Click) | Yes |
| Right-Click Menu | Yes | No (paste) | Yes | Yes | Yes |
| Rectangular Selection | Alt+Drag | Not built-in | Alt+Drag | Cmd+Alt+Drag | Alt+Drag |

**Key Takeaway**: 모든 주요 경쟁 제품이 마우스 입력을 완전 지원. GhostWin의 마우스 미지원은 경쟁 테이블에 올라갈 자격조차 없는 수준.

### 4.3 Market Sizing (TAM/SAM/SOM)

| Metric | Value | Basis |
|--------|-------|-------|
| **TAM** | Windows 터미널 사용자 전체 ~15M명 | Stack Overflow Survey 2024: 개발자의 ~65%가 Windows, 그 중 ~30%가 터미널 적극 사용 |
| **SAM** | GPU 가속 터미널 관심 사용자 ~1M명 | Alacritty GitHub stars 60K+, WezTerm 20K+, Ghostty 관심도 기반 추정 |
| **SOM** | GhostWin 초기 채택 목표 ~1K명 | Windows + ghostty 코어 조합에 관심을 가질 얼리어답터. 마우스 미지원 시 이 중 ~80% 이탈 추정 |

---

## 5. Beachhead Segment

### 5.1 Segment Scoring

| Criteria | Persona 1 (Vim 개발자) | Persona 2 (DevOps) | Persona 3 (캐주얼) |
|----------|:---:|:---:|:---:|
| Pain Severity (1-5) | 5 | 4 | 3 |
| Willingness to Try | 5 | 4 | 2 |
| Technical Fit | 5 | 5 | 3 |
| Word-of-Mouth Potential | 5 | 4 | 1 |
| **Total** | **20** | **17** | **9** |

### 5.2 Selection

**Beachhead: Persona 1 (Vim/Neovim 마우스 사용 개발자)**

이유:
1. 마우스 미지원이 가장 직접적인 이탈 원인 (vim mouse=a 필수)
2. 기술 커뮤니티에서 영향력이 크고 word-of-mouth 확산 기대
3. 마우스 프로토콜의 정확도(SGR 포맷 등)에 민감하여 ghostty upstream 활용의 기술적 우위가 가장 잘 드러나는 세그먼트
4. vim/neovim 마우스 테스트가 명확한 검증 기준 제공

---

## 6. Go-To-Market Strategy

### 6.1 GTM Channels

| Channel | Action | Timing |
|---------|--------|--------|
| GitHub Release | v0.6.0 "Mouse Input" 마일스톤 릴리스 | M-10 완료 시 |
| README | "Features" 섹션에 마우스 지원 추가 | 릴리스 동시 |
| r/commandline | "GhostWin now supports full mouse input" 포스트 | 릴리스 후 1주 |
| Demo GIF | vim + tmux + htop 마우스 조작 시연 | 릴리스 동시 |

### 6.2 Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| vim `:set mouse=a` 호환 | 100% (click, scroll, visual mode drag) | Manual smoke test |
| tmux mouse mode | 100% (pane click, scroll, resize) | Manual smoke test |
| htop mouse click | 100% (process select, menu click) | Manual smoke test |
| Scrollback scroll (non-mouse-mode) | < 16ms per scroll event | Perf measurement |
| Text selection accuracy | Word/line/block 모드 정상 | Manual test |
| Mouse format coverage | 5/5 (X10, UTF8, SGR, URxvt, SGR-Pixels) | Unit test |
| Mouse mode coverage | 4/4 + none (x10, normal, button, any) | Unit test |

---

## 7. Product Requirements

### 7.1 Functional Requirements

#### FR-01: Mouse Click Forwarding (TUI App Support)

**Priority**: P0 (Must Have)

마우스 모드가 활성화된 경우(x10/normal/button/any), WndProc에서 수신한 WM_LBUTTONDOWN/UP, WM_RBUTTONDOWN/UP, WM_MBUTTONDOWN/UP 메시지를 ghostty mouse_event + mouse_encode C API를 통해 VT 시퀀스로 변환하고, `gw_session_write`를 통해 해당 session의 ConPTY에 전달한다.

- **입력**: Win32 마우스 메시지 (hwnd, msg, wParam, lParam)
- **변환**: pixel 좌표 -> ghostty `mouse_event_set_position` (surface-space pixel). `mouse_encode`가 cell 좌표 변환 수행
- **출력**: VT 마우스 시퀀스 (예: `\x1b[<0;5;3M` for SGR format left click at col 5, row 3)
- **포맷**: 터미널 상태에 따라 자동 선택 (`mouse_encoder_setopt_from_terminal`)

#### FR-02: Mouse Motion Tracking

**Priority**: P0

button/any 모드에서 WM_MOUSEMOVE 메시지를 캡처하여 motion 이벤트로 인코딩.

- button 모드: 버튼이 눌린 상태에서만 motion 보고
- any 모드: 버튼 무관하게 모든 motion 보고
- cell 단위 중복 제거: `mouse_encoder`의 `track_last_cell` 옵션 활용

#### FR-03: Mouse Wheel Scroll

**Priority**: P0

WM_MOUSEWHEEL 메시지 처리:

- **마우스 모드 활성 시**: scroll 이벤트를 button 4(up)/5(down)으로 인코딩하여 VT 시퀀스 전달
- **마우스 모드 비활성 시**: scrollback viewport를 이동 (위로 = 이전 출력, 아래로 = 최신 출력)
- scroll delta: WHEEL_DELTA(120) 단위로 3줄 이동 (Windows 기본값, 시스템 설정 존중)

#### FR-04: Text Selection (Mouse Mode Disabled)

**Priority**: P1 (Should Have)

마우스 모드가 none인 경우, 터미널 영역에서 드래그로 텍스트를 선택할 수 있어야 한다.

- **단어 선택**: 더블 클릭으로 단어 단위 선택
- **줄 선택**: 트리플 클릭으로 전체 줄 선택
- **블록 선택**: Alt+드래그로 사각형 영역 선택
- **선택 영역 시각화**: 반전 색상 또는 하이라이트 오버레이
- **Shift 바이패스**: 마우스 모드 활성 중이어도 Shift를 누른 채 드래그하면 선택 모드 진입

구현 참고: 1차에서는 ghostty의 Selection.zig C API가 export되지 않으므로, WPF 측에서 screen buffer 읽기 + 자체 selection 렌더링으로 구현. 2차에서 ghostty C API 확장 검토.

#### FR-05: Keyboard Modifier Passthrough

**Priority**: P0

마우스 이벤트 발생 시 Ctrl/Shift/Alt 상태를 `mouse_event_set_mods`로 전달. TUI 앱이 Ctrl+Click, Shift+Click 등을 구분할 수 있어야 한다.

- Ctrl+Click: 일부 TUI 앱에서 URL 열기 등에 사용
- Shift+Click: 범위 선택, 마우스 모드 바이패스
- Alt+Click: vim의 block visual mode 등

#### FR-06: Scroll in Scrollback (Non-Mouse-Mode)

**Priority**: P1

마우스 모드 비활성 시, 마우스 휠로 scrollback buffer를 탐색할 수 있어야 한다.

- 스크롤 시 viewport 오프셋 변경 (render layer 연동)
- 새 출력 도착 시 자동으로 최하단으로 복귀 (auto-scroll)
- 현재 viewport 위치 표시 (스크롤바 또는 인디케이터)

#### FR-07: Per-Pane Mouse Routing

**Priority**: P0

다중 pane 환경에서 마우스 이벤트가 올바른 pane/session에 라우팅되어야 한다.

- 각 TerminalHostControl이 자신의 child HWND에서 마우스 메시지를 독립적으로 처리
- pane 클릭 시 해당 pane으로 포커스 이동 (기존 PaneClicked 메커니즘 확장)
- 포커스 pane 변경 후 후속 마우스 이벤트가 새 pane으로 라우팅

### 7.2 Non-Functional Requirements

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-01 | Mouse event latency | < 1ms from WndProc to gw_session_write |
| NFR-02 | Motion event throughput | WM_MOUSEMOVE 연속 수신 시 CPU 부하 < 5% |
| NFR-03 | Scroll smoothness | 60fps에서 프레임 드롭 없이 scrollback 이동 |
| NFR-04 | DPI awareness | 마우스 좌표가 DPI 스케일링 후 정확한 cell에 매핑 |
| NFR-05 | Thread safety | WndProc 에서 동기 P/Invoke 호출. per-session Encoder 캐시는 WndProc 스레드에서만 접근 (single-writer). v0.2 에서 Dispatcher 마셜링 제거 |

### 7.3 Out of Scope (Deferred)

| Item | Reason | Future Milestone |
|------|--------|------------------|
| Mouse cursor shape | 기능적 영향 낮음, cursor_shape 콜백 연동 필요 | M-11 |
| Clipboard (Copy/Paste) | 선택 기능의 후속. 별도 feature로 분리 | M-10b |
| URL auto-detection + click | hyperlink 파싱 + Ctrl+Click 연동 필요 | M-12 |
| Right-click context menu | WPF ContextMenu + Airspace 고려 필요 | M-11 |
| Drag-and-drop (파일) | 터미널 drag-drop은 별도 프로토콜 | M-13+ |

---

## 8. Technical Architecture

> **v0.2 업데이트** (2026-04-10): v0.1 smoke 테스트에서 발견된 3건의 성능 문제 + 4개 터미널 코드베이스 벤치마킹 결과를 반영. 리서치: [`mouse-input-benchmarking.md`](../00-research/mouse-input-benchmarking.md)

### 8.1 v0.1 문제 요약

| # | 문제 | 원인 | 영향 |
|:-:|-------|------|------|
| P1 | **버벅임** (마우스 이동 시 체감 지연) | `ghostty_mouse_encoder_new()/free()` + `ghostty_mouse_event_new()/free()` 를 매 WM_MOUSEMOVE 마다 힙 할당/해제 + `Dispatcher.BeginInvoke` 스레드 홉 | 모든 마우스 인터랙션에서 체감 |
| P2 | **드래그 중 렌더링 누락** | motion 이벤트는 ConPTY로 전달되나, VT 응답이 올 때까지 시각 갱신 없음 (release 시에만 갱신) | vim visual mode 드래그 깨짐 |
| P3 | **다중 pane 클릭 시 옆 pane 렌더링 사라짐** | SurfaceFocus 호출 시 기존 이슈 (mouse-input 고유가 아닌 기존 알려진 문제) | pane-split 환경에서 마우스 클릭 |

### 8.2 4개 터미널 벤치마킹 공통 패턴

4개 터미널(ghostty/Windows Terminal/Alacritty/WezTerm) 코드베이스를 직접 분석하여 도출한 공통 패턴. **v0.1에서 위반한 항목 전부**가 성능 문제의 근본 원인.

| # | 공통 패턴 | v0.1 위반 | v0.2 적용 |
|:-:|-----------|-----------|-----------|
| 1 | **힙 할당 0** -- stateless 순수 함수 또는 writer 직접 출력. Encoder 객체 매 호출 생성/파괴 없음 | `encoder_new/free` + `event_new/free` 매 호출 | per-session Encoder/Event 캐시 (Option A) |
| 2 | **Cell 중복 제거** -- 같은 cell 좌표이면 motion VT 시퀀스 무시. 시간 기반 throttle(16ms 등) 없음 | 중복 제거 없음 | `track_last_cell` 활성화 (ghostty 내장 기능) |
| 3 | **이벤트 스레드 동기 처리** -- Dispatcher/BeginInvoke/큐 없이 동기 인코딩 후 PTY 전송 | `Dispatcher.BeginInvoke` 스레드 홉 | WndProc 에서 P/Invoke 직접 호출 |
| 4 | **스크롤 누적** -- `pending_scroll` 픽셀 누적 후 `cell_height` 로 나누고 나머지 보존. 고해상도 마우스 지원 | 미구현 | `accumulatedScrollDelta` + cell_height 나누기 |

코드 근거:
- ghostty: `Surface.zig:3631` 스택 38B 버퍼, `mouse_encode.zig:107` last_cell 비교, `Surface.zig:3654` queueIo 동기
- WT: `mouseInput.cpp` `_GenerateSGRSequence` constexpr 순수 함수, `sameCoord` 비교
- Alacritty: `input/mod.rs` `sgr_mouse_report` format!, `cell_changed && same_side` 비교
- WezTerm: `terminalstate/mouse.rs` `write!` 직접 출력, `last.x != event.x` 비교

### 8.3 Data Flow (v0.2)

```
[Win32 WM_LBUTTON*/WM_MOUSEMOVE/WM_MOUSEWHEEL]
  |
  v
[TerminalHostControl child HWND WndProc]
  | 좌표(lParam) + 버튼(msg) + modifier(wParam) 추출
  | *** v0.2: Dispatcher.BeginInvoke 제거. WndProc에서 직접 P/Invoke ***
  |
  v
[IEngineService.WriteMouseEvent(sessionId, x, y, button, action, mods)]
  | P/Invoke (managed -> native, thread-safe)
  v
[gw_session_write_mouse]
  | *** v0.2: per-session Encoder/Event 캐시에서 조회 (힙 할당 0) ***
  | *** v0.2: track_last_cell 로 cell 중복 motion 자동 제거 ***
  |
  +-- (mouse mode ON) --> ghostty mouse_encode --> VT bytes --> conpty->send_input
  |
  +-- (mouse mode OFF) --> scrollback viewport 이동 또는 WPF Selection
```

### 8.4 API Design (v0.2)

#### C++ Engine API (ghostwin_engine.h)

```c
/// Forward mouse event to session's ConPTY via ghostty VT encoding.
/// Coordinates: surface-space pixels (child HWND client area).
/// button: 0=none(motion), 1=LEFT, 2=RIGHT, 3=MIDDLE, 4=SCROLL_UP, 5=SCROLL_DOWN
/// action: 0=PRESS, 1=RELEASE, 2=MOTION
/// mods: bitfield (1=SHIFT, 2=CTRL, 4=ALT, 8=SUPER)
GWAPI int gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                  float x_px, float y_px,
                                  uint32_t button, uint32_t action,
                                  uint32_t mods);

/// Scrollback viewport control (non-mouse-mode only).
GWAPI int gw_scroll_viewport(GwEngine engine, GwSessionId id, int32_t delta_rows);
```

#### C# Interop (IEngineService)

```csharp
int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                    uint button, uint action, uint mods);
int ScrollViewport(uint sessionId, int deltaRows);
```

### 8.5 Key Design Decisions (v0.2)

| # | Decision | Choice | Rationale |
|:-:|----------|--------|-----------|
| D-1 | Mouse encode 위치 | C++ Engine (ghostty C API) | terminal state(mode/format)에 직접 접근 필요. C# 측에서는 terminal state 조회 불가 |
| D-2 | WndProc vs WPF event | WndProc (Win32 message) | HwndHost 기반이라 WPF 라우팅 이벤트 불가. 기존 WM_LBUTTONDOWN 포커스 패턴 확장 |
| D-3 | Mouse coordinate space | Surface-space pixels (lParam 그대로) | ghostty mouse_encode 가 pixel->cell 변환 내부 수행. child HWND 가 이미 pixel 단위이므로 DPI 변환 불필요 |
| D-4 | Encoder 수명 관리 | **per-session 캐시 (Option A)** | ghostty C API 가 opaque handle 기반이라 내부 `encode()` 순수 함수 직접 호출 불가. 4개 터미널 모두 stateless 이지만 GhostWin 은 C API wrapper 제약으로 per-session 캐시가 유력. new 1회, free 는 session 종료 시 |
| D-5 | Motion 중복 제거 | **Cell 좌표 비교 (시간 throttle 없음)** | 4개 터미널 전부 cell 중복 제거만 사용. 16ms throttle 은 v0.1 PRD 에서 제안했으나 **어떤 참조 구현에도 없는 패턴**. ghostty 내장 `track_last_cell` 활용 |
| D-6 | 스레드 모델 | **WndProc 동기 P/Invoke (Dispatcher 제거)** | 4개 터미널 전부 이벤트 스레드에서 동기 처리. P/Invoke 는 thread-safe. Dispatcher.BeginInvoke 의 큐잉+스레드홉 지연 제거 |
| D-7 | 스크롤 누적 | **pending_scroll 픽셀 누적 + cell_height 나누기** | ghostty/WT/Alacritty 3개가 동일 패턴. 고해상도(Logitech 등) 마우스에서 sub-cell delta 보존 |
| D-8 | Selection rendering | WPF overlay (1차) | ghostty Selection.zig 가 C API export 없음. 1차 WPF 측 자체 구현, 2차 C API 확장 검토 |
| D-9 | Shift bypass | WndProc wParam modifier 검사 | Shift 마우스 이벤트는 VT 인코딩 대신 선택 모드 분기. WT/Alacritty 동일 패턴 |

### 8.6 GhostWin 특수 제약: Encoder Option 비교

ghostty C API (`ghostty_mouse_encoder_*`)는 opaque handle 기반이라 내부 `encode()` 순수 함수를 직접 호출할 수 없다. 대안 3가지:

| Option | 설명 | 힙 할당 | 장점 | 단점 |
|--------|------|:-------:|------|------|
| **A** (선택) | per-session Encoder/Event 캐시. new 1회, free 는 session 종료 시 | **사실상 0** (초기화 1회만) | C API 정상 사용, upstream 호환 유지 | session 수만큼 Encoder 인스턴스 상주 (무시 가능 수준) |
| B | C++ 엔진에서 ghostty 내부 `encode()` 직접 호출 (Zig 심볼 링크) | 0 | 완전 stateless | ghostty 내부 API 의존, upstream 변경 시 깨짐 |
| C | VT 인코딩을 C++ 자체 구현 | 0 | ghostty 비의존 | 5포맷x4모드 인코딩 직접 구현/유지보수 부담, C-1 constraint 위반 |

### 8.7 Risk Assessment (v0.2)

| # | Risk | Severity | Mitigation | 상태 |
|:-:|------|:--------:|------------|:----:|
| R-1 | ghostty mouse C API 가 libvt 빌드에 미포함 | ~~HIGH~~ | T-1 검증 완료: `dumpbin` 으로 17개 심볼 전부 export 확인 | **해소** |
| R-2 | **Encoder/Event 매 호출 힙 할당 --> 버벅임** | **HIGH** | **v0.1 smoke 에서 확인됨**. per-session 캐시 (D-4) + track_last_cell (D-5) 로 힙 할당 0 달성 | **v0.2 필수** |
| R-3 | **Dispatcher.BeginInvoke 스레드 홉 --> 지연** | **HIGH** | **v0.1 smoke 에서 확인됨**. WndProc 동기 P/Invoke (D-6) 로 경로 단축. 4개 터미널 전부 동기 처리 패턴 | **v0.2 필수** |
| R-4 | **드래그 중 렌더링 누락** | **HIGH** | **v0.1 smoke 에서 확인됨**. 원인 조사 필요: PTY 응답 지연인지, render invalidation 누락인지. ghostty 는 `queueRender()` 를 매 cursorPos 호출에서 트리거 (참조) | **v0.2 조사** |
| R-5 | 다중 pane 클릭 시 옆 pane 렌더링 사라짐 | **MEDIUM** | SurfaceFocus 기존 이슈. mouse-input 고유가 아닌 기존 알려진 문제. 별도 사이클 추적 | 기존 이슈 |
| R-6 | WndProc 에서 모든 메시지 consume 시 OS 기본 동작 상실 | MEDIUM | 처리한 메시지만 consume, 미처리는 DefWindowProc 전달 유지 | 설계 반영 |
| R-7 | 텍스트 선택 시 render buffer 읽기 경로 부재 | HIGH | Engine API 확장(`gw_get_cell_text`) 또는 ghostty terminal C API `render` 모듈 활용. M-10c 별도 설계 검토 | M-10c 선행 |
| R-8 | WM_MOUSEWHEEL 이 child HWND 에 전달되지 않을 수 있음 | MEDIUM | Win32 기본 동작으로 wheel 메시지는 포커스 윈도우로 전달. 필요시 parent forwarding 또는 SetCapture | T-7 검증 |
| R-9 | **v0.1 의 16ms throttle 설계가 참조 구현에 없는 패턴** | LOW | v0.2 에서 **시간 기반 throttle 완전 제거**. cell 기반 중복 제거만 적용 (4개 터미널 전부 동일) | **v0.2 반영** |

---

## 9. Implementation Milestones

### M-10a: Mouse Click + Motion v0.2 (P0, ~1주)

> v0.2: v0.1 smoke 에서 확인된 3건의 성능 문제 + 4개 터미널 벤치마킹 공통 패턴 반영.

**v0.2 핵심 변경 (v0.1 대비)**:

| # | v0.1 | v0.2 | 근거 |
|:-:|------|------|------|
| 1 | `encoder_new/free` + `event_new/free` 매 호출 | per-session Encoder/Event 캐시 | 4개 터미널 전부 힙 할당 0 (8.2 패턴 1) |
| 2 | motion 중복 제거 없음 | `track_last_cell` 활성화 | 4개 터미널 전부 cell 중복 제거 (8.2 패턴 2) |
| 3 | `Dispatcher.BeginInvoke` 스레드 홉 | WndProc 동기 P/Invoke | 4개 터미널 전부 동기 처리 (8.2 패턴 3) |
| 4 | 16ms 시간 throttle 계획 | 시간 throttle 제거 | 어떤 참조 구현에도 없는 패턴 (8.5 D-5) |

**구현 작업**:

- [ ] C++ Engine: `gw_session_write_mouse` API 구현 (ghostty mouse_event/mouse_encode 연동)
- [ ] C++ Engine: **per-session Encoder/Event 캐시** -- `EngineImpl` 에 `unordered_map<SessionId, {Encoder, Event}>`. session 생성 시 new, 종료 시 free (8.6 Option A)
- [ ] C++ Engine: **`track_last_cell` 활성화** -- `GHOSTTY_MOUSE_ENCODER_OPT_TRACK_LAST_CELL = true` 로 cell 중복 motion 자동 제거
- [ ] C++ Engine: **`setopt_from_terminal` 매 호출** -- Encoder 캐시 재사용하되 terminal state(mode/format) 는 매번 동기화
- [ ] C# Interop: `WriteMouseEvent` P/Invoke + IEngineService 확장
- [ ] WPF: WndProc 마우스 메시지 캡처 (WM_LBUTTON/RBUTTON/MBUTTONDOWN/UP + WM_MOUSEMOVE)
- [ ] WPF: **Dispatcher.BeginInvoke 제거** -- WndProc 에서 직접 `IEngineService.WriteMouseEvent` P/Invoke 호출 (동기)
- [ ] WPF: Modifier 전달 (wParam MK_CONTROL/SHIFT + GetKeyState VK_MENU for ALT)
- [ ] 조사: **드래그 중 렌더링 누락 원인** (R-4) -- PTY 응답 지연 vs render invalidation 누락 확인
- [ ] 검증: vim `:set mouse=a` + click/drag, tmux mouse mode, htop 클릭

### M-10b: Mouse Scroll (P0, ~3일)

- [ ] WndProc: WM_MOUSEWHEEL 캡처
- [ ] **스크롤 누적**: `accumulatedScrollDelta` 픽셀 누적 -> `cell_height` 나누기 -> 나머지 보존 (8.2 패턴 4)
- [ ] 마우스 모드 활성 시: button 4(up)/5(down) VT 인코딩
- [ ] 마우스 모드 비활성 시: `gw_scroll_viewport` API + viewport offset 관리
- [ ] 검증: vim scroll, non-mouse-mode scrollback, 고해상도 마우스(Logitech 등) 검증

### M-10c: Text Selection (P1, ~1주)

- [ ] 마우스 모드 none/Shift bypass 판별 로직
- [ ] 드래그 시작/진행/종료 상태 관리
- [ ] Cell 좌표 기반 selection range 계산
- [ ] Selection 시각화 (DX11 render pass 또는 WPF overlay)
- [ ] 더블 클릭(word)/트리플 클릭(line) 선택
- [ ] Alt+드래그 rectangular selection
- [ ] 검증: 텍스트 선택 + 선택 영역 정확도

### M-10d: Integration + Polish (~3일)

- [ ] Per-pane 마우스 라우팅 검증 (다중 pane split 환경)
- [ ] DPI 변경 시 마우스 좌표 정확도 검증
- [ ] **성능 측정**: v0.2 개선 후 NFR-01(< 1ms latency), NFR-02(< 5% CPU) 재측정
- [ ] **드래그 렌더링 검증**: R-4 조사 결과 반영하여 드래그 중 시각 갱신 확인
- [ ] vim/tmux/htop/nano 호환 smoke test

---

## Attribution

본 PRD는 PM Agent Team의 4단계 분석 프로세스로 작성되었습니다:
- **Discovery**: Opportunity Solution Tree (Pawel Huryn, pm-skills MIT License)
- **Strategy**: JTBD 6-Part Value Proposition + Lean Canvas
- **Research**: User Personas x3 + Competitive Analysis x5 + TAM/SAM/SOM
- **PRD Synthesis**: 8-section structured PRD

기술 분석 근거:
- ghostty upstream `src/terminal/c/mouse_event.zig`, `mouse_encode.zig` -- C API export 확인
- ghostty `src/input/mouse.zig`, `mouse_encode.zig` -- Action/Button/Format/Event 타입 정의
- ghostty `src/terminal/mouse.zig` -- Event(none/x10/normal/button/any) + Format(x10/utf8/sgr/urxvt/sgr_pixels)
- GhostWin `TerminalHostControl.cs` -- 현재 WndProc 구현 (WM_LBUTTONDOWN 포커스 전용)
- GhostWin `ghostwin_engine.h` -- 현재 C API 19개 + Surface 4개 (마우스 관련 API 없음)
- GhostWin `IEngineService.cs` -- 현재 인터페이스 (마우스 메서드 없음)

v0.2 벤치마킹 근거:
- ghostty `Surface.zig:3631` -- 스택 38B 버퍼, 힙 할당 0
- ghostty `mouse_encode.zig:107` -- `last_cell` 비교 (cell 중복 제거)
- ghostty `Surface.zig:3654` -- `queueIo(.write_small)` 동기 인코딩
- ghostty `Surface.zig:3392` -- `pending_scroll_y` 누적 스크롤
- WT `mouseInput.cpp` -- `_GenerateSGRSequence` constexpr, `sameCoord` 비교, `accumulatedDelta` 누적
- Alacritty `input/mod.rs` -- `sgr_mouse_report` format!, `cell_changed` 비교, `accumulated_scroll` 누적
- WezTerm `terminalstate/mouse.rs` -- `write!` 직접 출력, `last.x != event.x` 비교

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | PM Agent Team 초안 (4단계 분석) |
| 0.2 | 2026-04-10 | section 8/9 업데이트: v0.1 smoke 3건 성능 문제 + 4개 터미널 벤치마킹 공통 패턴 반영. 16ms throttle 제거, per-session 캐시, Dispatcher 제거, 스크롤 누적 추가 |
