# PRD: Mouse Input for GhostWin Terminal

> **Feature**: mouse-input
> **Project**: GhostWin Terminal
> **Author**: PM Agent Team (Discovery + Strategy + Research + PRD Synthesis)
> **Date**: 2026-04-10
> **Status**: Draft (v1.0 -- 5-terminal benchmarking + v0.1 hardware validation)
> **Milestone**: M-10 (Terminal Basic Operations)
> **Benchmarking**: `docs/00-research/mouse-input-benchmarking.md` (v0.3, 5 terminals)

---

## 1. Executive Summary

GhostWin Terminal은 현재 키보드 입력만 지원하며, 마우스 입력(클릭/스크롤/텍스트 선택)이 미구현 상태이다. 이는 vim, tmux, htop 등 마우스 인터랙션에 의존하는 TUI 앱 사용을 불가능하게 하고, 텍스트 선택-복사라는 가장 기본적인 터미널 조작조차 불가능하게 만든다.

### 기술 전략 (5-terminal benchmarking 기반)

5개 터미널(ghostty/Windows Terminal/Alacritty/WezTerm/cmux) 코드베이스의 함수 본문 전수 조사에서 다음 4가지 공통 패턴을 확인했다:

| # | 공통 패턴 | 근거 (5/5 일치) |
|:-:|-----------|----------------|
| 1 | **힙 할당 0/최소** | ghostty: 스택 38B. WT: FMT_COMPILE. Alacritty: format!. WezTerm: write!. cmux: C API 위임 |
| 2 | **Cell 좌표 중복 제거** | 5개 전부 cell 좌표 비교. 시간 기반 throttle(16ms 등) 없음 |
| 3 | **이벤트 스레드 동기 처리** | 5개 전부 Dispatcher/큐 없이 동기. cmux도 메인 스레드 동기 |
| 4 | **스크롤 픽셀 누적** | ghostty: pending_scroll. WT: accumulatedDelta. Alacritty: accumulated_scroll |

**핵심 제약**: cmux는 `ghostty_surface_mouse_*` Surface-level C API를 직접 호출하지만, GhostWin의 `-Demit-lib-vt=true` 빌드에서는 Surface 레이어가 포함되지 않아 해당 심볼이 **export되지 않는다**. 반면 `ghostty_mouse_encoder_*` 17개 심볼은 `dumpbin`으로 export 확인 완료. 따라서 GhostWin은 **Option A (per-session Encoder 캐시)** 전략을 사용한다.

**v0.1 검증 결과**: TC-1(vim 좌클릭) PASS, TC-8(Shift bypass) PASS를 확인했으나, 3건의 성능 문제를 발견:
- P1: Encoder/Event 매 호출 힙 할당 + Dispatcher.BeginInvoke -> 체감 버벅임
- P2: 드래그 중 렌더링 누락 (release 시에만 갱신)
- P3: 다중 pane 클릭 시 옆 pane 렌더링 사라짐 (기존 SurfaceFocus 이슈)

v1.0 PRD는 이 4가지 공통 패턴을 모두 준수하고, v0.1에서 발견된 3건을 해소하는 v0.2 구현 전략을 정의한다.

---

## 2. Opportunity Discovery (Opportunity Solution Tree)

### 2.1 Target Outcome

**터미널 기본 조작 완성** -- 마우스 클릭/스크롤/선택이 동작하여 "일상 터미널"로 사용 가능한 수준 달성.

### 2.2 Opportunity Map

```
[Outcome: 일상 터미널 사용 가능]
  |
  +-- [O1] TUI 앱에서 마우스 클릭/드래그 불가
  |     +-- [S1a] WndProc -> ghostty_mouse_encoder_* per-session 캐시 + VT 변환
  |     |         (v0.1 검증 완료: TC-1 PASS. v0.2: 힙 할당 0 + 동기 처리)
  |     +-- [S1b] gw_session_write로 VT 시퀀스 직접 전달 (mouse_encode 우회)
  |
  +-- [O2] 스크롤 미지원 (scrollback 탐색 불가)
  |     +-- [S2a] WM_MOUSEWHEEL -> 픽셀 누적 + cell_height 나누기 -> VT (모드 활성 시)
  |     +-- [S2b] WM_MOUSEWHEEL -> scrollback viewport 조작 (모드 비활성 시)
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
| O1 | **S1a (v0.2)** | `ghostty_mouse_encoder_*` 17개 C API export 확인. per-session 캐시로 힙 할당 0 달성. v0.1에서 기본 동작 검증 완료(TC-1 PASS). cmux의 Surface C API는 `-Demit-lib-vt` 빌드 미포함으로 사용 불가 |
| O2 | **S2a + S2b** | 마우스 모드에 따라 분기: 활성이면 VT 인코딩, 비활성이면 scrollback viewport 이동. 5개 터미널 동일 패턴. 누적 스크롤(패턴 4) 필수 적용 |
| O3 | **S3a (1차)** | ghostty Selection.zig는 C API export 없음. 1차에서 WPF 측 자체 Selection 구현. 2차에서 C API 확장 검토 |
| O4 | **Deferred** | cursor_shape은 기능적 영향이 낮아 별도 마일스톤으로 분리 |

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
| **Solution** | WndProc 마우스 캡처 -> per-session Encoder 캐시(힙 할당 0) -> ghostty mouse_encode -> VT -> ConPTY |
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

> v1.0: v0.1 hardware 검증 결과 반영. TC-1(vim 좌클릭) PASS, TC-5(스크롤) FAIL 확인으로 우선순위 재조정.

#### FR-01: Mouse Click Forwarding (TUI App Support)

**Priority**: P0 (Must Have) -- v0.1 TC-1 PASS 확인

마우스 모드가 활성화된 경우(x10/normal/button/any), WndProc에서 수신한 WM_LBUTTONDOWN/UP, WM_RBUTTONDOWN/UP, WM_MBUTTONDOWN/UP 메시지를 ghostty mouse_encode C API를 통해 VT 시퀀스로 변환하고 ConPTY에 전달한다.

- **입력**: Win32 마우스 메시지 (hwnd, msg, wParam, lParam)
- **변환**: pixel 좌표 -> `ghostty_mouse_event_set_position` (surface-space pixel). `ghostty_mouse_encoder_encode`가 cell 좌표 변환 수행
- **출력**: VT 마우스 시퀀스 (예: `\x1b[<0;5;3M` for SGR format left click at col 5, row 3)
- **v0.2 핵심**: per-session Encoder 캐시로 힙 할당 0. WndProc에서 동기 P/Invoke 호출 (Dispatcher.BeginInvoke 제거)
- **cmux 참조**: `clickCount == 1`일 때만 position 업데이트 (double-click selection 간섭 방지). WPF `e.ClickCount` 또는 WM_LBUTTONDBLCLK 분기 적용

#### FR-02: Mouse Motion Tracking

**Priority**: P0 -- v0.1 TC-2 "부분 PASS" (드래그 중 렌더링 누락 발견)

button/any 모드에서 WM_MOUSEMOVE 메시지를 캡처하여 motion 이벤트로 인코딩.

- button 모드: 버튼이 눌린 상태에서만 motion 보고
- any 모드: 버튼 무관하게 모든 motion 보고
- **cell 단위 중복 제거**: `ghostty_mouse_encoder_setopt(TRACK_LAST_CELL, true)` 활성화. 5개 터미널 전부 cell 비교만 사용, 시간 기반 throttle 없음
- **v0.2 핵심**: 드래그 중 렌더링 누락(P2) 원인 조사 + 해결 필수. PTY 응답 지연인지 render invalidation 누락인지 확인

#### FR-03: Mouse Wheel Scroll

**Priority**: P0 -- v0.1 TC-5 **FAIL** (WM_MOUSEWHEEL 미구현)

WM_MOUSEWHEEL 메시지 처리:

- **마우스 모드 활성 시**: scroll 이벤트를 button 4(up)/5(down)으로 인코딩하여 VT 시퀀스 전달
- **마우스 모드 비활성 시**: scrollback viewport를 이동 (위로 = 이전 출력, 아래로 = 최신 출력)
- **누적 스크롤**: `accumulatedScrollDelta` 픽셀 누적 -> `cell_height`로 나누기 -> 나머지 보존 (5개 터미널 공통 패턴 4). 고해상도 마우스(Logitech MX 등) 지원 필수
- **코드 참조**: ghostty `pending_scroll_y` (`Surface.zig:3392`), WT `accumulatedDelta` (`mouseInput.cpp:300`), Alacritty `accumulated_scroll.y` (`input/mod.rs`)

#### FR-04: Text Selection (Mouse Mode Disabled)

**Priority**: P1 (Should Have)

마우스 모드가 none인 경우, 터미널 영역에서 드래그로 텍스트를 선택할 수 있어야 한다.

- **단어 선택**: 더블 클릭으로 단어 단위 선택
- **줄 선택**: 트리플 클릭으로 전체 줄 선택
- **블록 선택**: Alt+드래그로 사각형 영역 선택
- **선택 영역 시각화**: 반전 색상 또는 하이라이트 오버레이
- **Shift 바이패스**: 마우스 모드 활성 중이어도 Shift를 누른 채 드래그하면 선택 모드 진입 (v0.1 TC-8 PASS 확인)

구현 참고: ghostty Selection.zig는 C API export 없음. 1차에서 WPF 측 screen buffer 읽기 + 자체 selection 렌더링. 2차에서 ghostty C API 확장 검토.

#### FR-05: Keyboard Modifier Passthrough

**Priority**: P0

마우스 이벤트 발생 시 Ctrl/Shift/Alt 상태를 `ghostty_mouse_event_set_mods`로 전달. TUI 앱이 Ctrl+Click, Shift+Click 등을 구분할 수 있어야 한다.

- Ctrl+Click: 일부 TUI 앱에서 URL 열기 등에 사용
- Shift+Click: 범위 선택, 마우스 모드 바이패스
- Alt+Click: vim의 block visual mode 등
- **주의**: Alt modifier는 wParam에 포함되지 않으므로 `GetKeyState(VK_MENU)` 사용 (e2e-headless-input H-RCA1과 동일 제약)

#### FR-06: Scroll in Scrollback (Non-Mouse-Mode)

**Priority**: P1

마우스 모드 비활성 시, 마우스 휠로 scrollback buffer를 탐색할 수 있어야 한다.

- 스크롤 시 viewport 오프셋 변경 (render layer 연동)
- 새 출력 도착 시 자동으로 최하단으로 복귀 (auto-scroll)
- 현재 viewport 위치 표시 (스크롤바 또는 인디케이터)

#### FR-07: Per-Pane Mouse Routing

**Priority**: P0 -- v0.1 TC-7 "부분 PASS" (마우스 라우팅 OK, 렌더링 이슈 별개)

다중 pane 환경에서 마우스 이벤트가 올바른 pane/session에 라우팅되어야 한다.

- 각 TerminalHostControl이 자신의 child HWND에서 마우스 메시지를 독립적으로 처리 (기존 아키텍처 C-4)
- pane 클릭 시 해당 pane으로 포커스 이동 (기존 PaneClicked 메커니즘 확장)
- **cmux 참조**: `ghostty_surface_mouse_captured` 분기 -- 우클릭 시 VT가 마우스를 캡처했으면 터미널에, 아니면 앱 메뉴에 전달. GhostWin에서는 Encoder의 mouse mode 쿼리로 대체
- **cmux 참조**: drag out-of-bounds 좌표 그대로 전달 -- libghostty가 auto-scroll 처리. WPF에서 `Mouse.Capture()` 사용

### 7.2 Non-Functional Requirements

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-01 | Mouse event latency | < 1ms from WndProc to ConPTY send_input |
| NFR-02 | Motion event throughput | WM_MOUSEMOVE 연속 수신 시 CPU 부하 < 5% |
| NFR-03 | Scroll smoothness | 60fps에서 프레임 드롭 없이 scrollback 이동 |
| NFR-04 | DPI awareness | 마우스 좌표가 DPI 스케일링 후 정확한 cell에 매핑 |
| NFR-05 | Thread safety | WndProc에서 동기 P/Invoke 호출. per-session Encoder 캐시는 WndProc 스레드에서만 접근 (single-writer). Dispatcher 마셜링 제거 |

### 7.3 Out of Scope (Deferred)

| Item | Reason | Future Milestone |
|------|--------|------------------|
| Mouse cursor shape | 기능적 영향 낮음, cursor_shape 콜백 연동 필요 | M-11 |
| Clipboard (Copy/Paste) | 선택 기능의 후속. 별도 feature로 분리 | M-10b |
| URL auto-detection + click | hyperlink 파싱 + Ctrl+Click 연동 필요 | M-12 |
| Right-click context menu | WPF ContextMenu + Airspace 고려 필요 | M-11 |
| Drag-and-drop (파일) | 터미널 drag-drop은 별도 프로토콜 | M-13+ |
| Momentum scroll (macOS only) | cmux의 phase 비트 인코딩. Windows에는 momentum scroll 없음 | N/A |

---

## 8. Technical Architecture

> 5개 터미널 코드베이스 함수 본문 전수 조사 결과 반영.
> 리서치: [`mouse-input-benchmarking.md`](../00-research/mouse-input-benchmarking.md) (v0.3)

### 8.1 GhostWin의 핵심 제약: `-Demit-lib-vt=true`

GhostWin은 ghostty를 VT 라이브러리로만 사용한다 (`-Demit-lib-vt=true -Dapp-runtime=none`). 이로 인해:

| API 계층 | Export 여부 | 근거 |
|-----------|:----------:|------|
| `ghostty_surface_mouse_*` (Surface-level) | **미포함** | Surface 레이어는 `-Dapp-runtime` 빌드에서만 포함 |
| `ghostty_mouse_encoder_*` (VT-level) | **17개 확인** | `dumpbin /exports ghostty-vt.dll`로 전수 확인 |
| `ghostty_mouse_event_*` (VT-level) | **확인** | encoder와 함께 export |

cmux는 `ghostty_surface_mouse_button`/`ghostty_surface_mouse_scroll`/`ghostty_surface_mouse_captured` 등 Surface C API를 직접 호출하여 cell 중복 제거, 스크롤 누적, Selection, mouse mode 쿼리를 모두 libghostty 내부에 위임한다. GhostWin에서는 이 경로를 사용할 수 없으므로, **Encoder-level C API + GhostWin 측 보조 로직**으로 대체해야 한다.

### 8.2 5개 터미널 공통 패턴 vs v0.1 위반 vs v0.2 적용

| # | 공통 패턴 | 코드 근거 | v0.1 위반 | v0.2 적용 |
|:-:|-----------|-----------|-----------|-----------|
| 1 | **힙 할당 0** | ghostty: 스택 38B `WriteReq.Small.Array` (`Surface.zig:3631`). WT: `FMT_COMPILE` constexpr (`mouseInput.cpp:471`). Alacritty: `format!` 스택 (`input/mod.rs`). WezTerm: `write!(self.writer)` 직접 출력 (`mouse.rs`). cmux: C API 위임 (할당 0) | `encoder_new/free` + `event_new/free` 매 WM_MOUSEMOVE마다 힙 할당/해제 | **per-session Encoder/Event 캐시**: new 1회, free는 session 종료 시 (Option A) |
| 2 | **Cell 좌표 중복 제거** | ghostty: `opts.last_cell` 비교 (`mouse_encode.zig:107`). WT: `sameCoord` 비교 (`mouseInput.cpp:340`). Alacritty: `old_point != point` (`input/mod.rs`). WezTerm: `last.x != event.x` (`mouse.rs`). cmux: libghostty 내부 처리 | 중복 제거 없음 -- 매 pixel 변화마다 VT 시퀀스 생성 | **`TRACK_LAST_CELL` 활성화**: ghostty 내장 cell 비교. 시간 기반 throttle(16ms 등) 완전 제거 |
| 3 | **이벤트 스레드 동기 처리** | ghostty: 콜백 동기 인코딩 -> IO 큐 (`Surface.zig:3654`). WT: UI 스레드 동기 (`TermControl.cpp`). Alacritty: winit 단일 스레드 (`event.rs`). WezTerm: GUI 스레드 동기 (`mouseevent.rs`). cmux: 메인 스레드 동기 (`GhosttyTerminalView.swift`) | `Dispatcher.BeginInvoke` -- 큐잉 + 스레드 홉 지연 | **WndProc에서 직접 P/Invoke**: Dispatcher 완전 제거. P/Invoke는 thread-safe. managed -> native 호출은 WndProc 스레드(메인 스레드)에서 동기 실행 |
| 4 | **스크롤 픽셀 누적** | ghostty: `pending_scroll_y` 누적 -> cell_height 나누기 -> 나머지 보존 (`Surface.zig:3392`). WT: `accumulatedDelta` -> `WHEEL_DELTA(120)` 임계값 (`mouseInput.cpp:300`). Alacritty: `accumulated_scroll.y` 픽셀 누적 -> cell_height 나누기 -> `%=` (`input/mod.rs`). cmux: `ghostty_surface_mouse_scroll` + precision 2x | 미구현 (WM_MOUSEWHEEL 자체가 없었음) | **`accumulatedScrollDelta` 픽셀 누적 + cell_height 나누기 + 나머지 보존**: 고해상도 마우스 지원 |

### 8.3 cmux 전용 패턴과 GhostWin 대체 전략

cmux는 Surface C API로 아래 기능을 libghostty에 위임하지만, GhostWin은 해당 API를 사용할 수 없어 자체 구현이 필요하다:

| cmux 패턴 | cmux 코드 | GhostWin 대체 |
|-----------|-----------|---------------|
| `ghostty_surface_mouse_captured` 분기 | 우클릭 시 VT 캡처면 터미널, 아니면 앱 메뉴 | Encoder의 `setopt_from_terminal` 후 mouse mode 조회. mode=none이면 앱 메뉴 |
| `clickCount == 1`일 때만 pos 업데이트 | double-click selection 간섭 방지 (issue #1698) | WndProc에서 WM_LBUTTONDBLCLK 감지. clickCount > 1이면 position 업데이트 스킵 |
| drag out-of-bounds 좌표 그대로 전달 | libghostty가 auto-scroll 처리 | `Mouse.Capture(host)` -- WPF가 HWND 밖 마우스도 캡처. 좌표 그대로 Encoder에 전달 |
| Y축 flip | AppKit은 `bounds.height - y` 변환 필요 | **변환 불필요** -- WPF와 Win32 모두 top-left 원점으로 ghostty와 동일 |
| Selection (드래그 선택) | `ghostty_surface_selection_*` C API | WPF 측 자체 Selection 구현 (M-10c) |
| Momentum scroll (macOS) | phase 비트 인코딩 | Windows에는 momentum scroll 없음. 단순 delta만 처리 |

### 8.4 Data Flow (v0.2 확정)

```
[Win32 WM_LBUTTON*/WM_MOUSEMOVE/WM_MOUSEWHEEL]
  |
  v
[TerminalHostControl child HWND WndProc]
  | 좌표(lParam) + 버튼(msg) + modifier(wParam) 추출
  | *** Dispatcher.BeginInvoke 제거. WndProc에서 직접 P/Invoke ***
  |
  v
[IEngineService.WriteMouseEvent(sessionId, x, y, button, action, mods)]
  | P/Invoke (managed -> native, WndProc 스레드에서 동기 실행)
  v
[gw_session_write_mouse]
  | *** per-session Encoder/Event 캐시에서 조회 (힙 할당 0) ***
  | *** setopt_from_terminal: 매 호출마다 terminal state 동기화 ***
  | *** TRACK_LAST_CELL: cell 중복 motion 자동 제거 ***
  |
  +-- (mouse mode ON) --> ghostty_mouse_encoder_encode --> VT bytes --> conpty->send_input
  |
  +-- (mouse mode OFF, scroll) --> accumulatedScrollDelta 누적 --> gw_scroll_viewport
  |
  +-- (mouse mode OFF, click/drag) --> WPF Selection (M-10c)
```

### 8.5 API Design (v0.2)

#### C++ Engine API (ghostwin_engine.h)

```c
// ── Mouse input ──

/// Forward mouse event to session's ConPTY via ghostty VT encoding.
/// Coordinates: surface-space pixels (child HWND client area).
/// button: 0=none(motion), 1=LEFT, 2=RIGHT, 3=MIDDLE, 4=SCROLL_UP, 5=SCROLL_DOWN
/// action: 0=PRESS, 1=RELEASE, 2=MOTION
/// mods: bitfield (1=SHIFT, 2=CTRL, 4=ALT, 8=SUPER)
/// Returns: GW_OK on success, or bytes written count > 0 on VT encode success
GWAPI int gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                  float x_px, float y_px,
                                  uint32_t button, uint32_t action,
                                  uint32_t mods);

/// Scrollback viewport control (non-mouse-mode only).
/// delta_rows > 0: scroll up (towards older output)
/// delta_rows < 0: scroll down (towards newer output)
GWAPI int gw_scroll_viewport(GwEngine engine, GwSessionId id, int32_t delta_rows);

/// Query current mouse mode for a session.
/// Returns: 0=none, 1=x10, 2=normal, 3=button, 4=any
GWAPI int gw_mouse_mode(GwEngine engine, GwSessionId id);
```

#### C# Interop (IEngineService)

```csharp
int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                    uint button, uint action, uint mods);
int ScrollViewport(uint sessionId, int deltaRows);
int GetMouseMode(uint sessionId);
```

### 8.6 Per-Session Encoder 캐시 (Option A 확정)

ghostty C API (`ghostty_mouse_encoder_*`)는 opaque handle 기반이라 내부 `encode()` 순수 함수를 직접 호출할 수 없다. 대안 비교 후 Option A 확정:

| Option | 설명 | 힙 할당 | 장점 | 단점 |
|--------|------|:-------:|------|------|
| **A (확정)** | per-session Encoder/Event 캐시. `new` 1회, `free`는 session 종료 시 | **사실상 0** (초기화 1회만) | C API 정상 사용, upstream 호환 유지 | session 수만큼 인스턴스 상주 (무시 가능: ~100B x N) |
| B | C++ 엔진에서 ghostty 내부 `encode()` Zig 심볼 직접 링크 | 0 | 완전 stateless | ghostty 내부 API 의존, upstream 변경 시 깨짐 |
| C | VT 인코딩을 C++ 자체 구현 | 0 | ghostty 비의존 | 5포맷x4모드 인코딩 구현/유지보수 부담, C-1 위반 |
| ~~cmux 패턴~~ | `ghostty_surface_mouse_*` C API 직접 호출 | 0 | 가장 단순 | **`-Demit-lib-vt` 빌드에서 export 안 됨** -- 사용 불가 |

**Option A 구현 상세**:

```cpp
// EngineImpl 내부
struct MouseState {
    GhosttyMouseEncoder encoder = nullptr;
    GhosttyMouseEvent   event   = nullptr;
    float               accumulatedScrollDelta = 0.0f;
};
std::unordered_map<GwSessionId, MouseState> mouse_states_;

// session 생성 시
void on_session_created(GwSessionId id) {
    auto& ms = mouse_states_[id];
    ghostty_mouse_encoder_new(nullptr, &ms.encoder);
    ghostty_mouse_event_new(nullptr, &ms.event);
    // cell 중복 제거 활성화
    bool track = true;
    ghostty_mouse_encoder_setopt(ms.encoder,
        GHOSTTY_MOUSE_ENCODER_OPT_TRACK_LAST_CELL, &track);
}

// session 종료 시
void on_session_closed(GwSessionId id) {
    if (auto it = mouse_states_.find(id); it != mouse_states_.end()) {
        ghostty_mouse_event_free(it->second.event);
        ghostty_mouse_encoder_free(it->second.encoder);
        mouse_states_.erase(it);
    }
}
```

### 8.7 Key Design Decisions

| # | Decision | Choice | Rationale |
|:-:|----------|--------|-----------|
| D-1 | Mouse encode API | `ghostty_mouse_encoder_*` (Encoder-level) | cmux 패턴(Surface-level)은 `-Demit-lib-vt` 빌드에서 사용 불가. Encoder-level 17개 심볼 export 확인 |
| D-2 | Encoder 수명 관리 | per-session 캐시 (Option A) | 5개 터미널 전부 힙 할당 0. C API opaque handle 제약으로 캐시가 유일한 방법 |
| D-3 | Motion 중복 제거 | Cell 좌표 비교 (TRACK_LAST_CELL) | 5개 터미널 전부 cell 비교만. 시간 throttle(16ms 등) **어떤 참조 구현에도 없음** |
| D-4 | 스레드 모델 | WndProc 동기 P/Invoke | 5개 터미널 전부 동기 처리. Dispatcher.BeginInvoke의 큐잉+스레드홉 지연이 v0.1 버벅임의 직접 원인 |
| D-5 | 스크롤 누적 | 픽셀 누적 + cell_height 나누기 | ghostty/WT/Alacritty 3개가 동일 패턴. 고해상도 마우스 sub-cell delta 보존 |
| D-6 | 좌표 공간 | Surface-space pixels (lParam 그대로) | ghostty mouse_encode가 pixel->cell 변환 내부 수행. child HWND가 이미 pane-local pixel 단위이므로 DPI 변환 불필요 |
| D-7 | clickCount 처리 | WM_LBUTTONDBLCLK 감지 | cmux 패턴: clickCount=1일 때만 position 업데이트. double-click selection 간섭 방지 |
| D-8 | Drag capture | `Mouse.Capture()` 또는 `SetCapture(hwnd)` | cmux: out-of-bounds 좌표 그대로 전달. libghostty가 auto-scroll 처리 |
| D-9 | Selection rendering | WPF overlay (1차) | ghostty Selection.zig가 C API export 없음. 1차 WPF 측 자체 구현, 2차 C API 확장 검토 |
| D-10 | Shift bypass | WndProc wParam modifier 검사 | Shift+마우스는 VT 인코딩 대신 선택 모드 분기. WT/Alacritty 동일 패턴. v0.1 TC-8 PASS 확인 |
| D-11 | terminal state 동기화 | `setopt_from_terminal` 매 호출 | Encoder 캐시 재사용하되 terminal의 mouse mode/format은 매번 동기화 필수 |

### 8.8 Risk Assessment (v1.0)

| # | Risk | Severity | Mitigation | 상태 |
|:-:|------|:--------:|------------|:----:|
| R-1 | ghostty mouse C API가 libvt 빌드에 미포함 | ~~HIGH~~ | `dumpbin`으로 17개 심볼 전부 export 확인 | **해소** |
| R-2 | **Encoder/Event 매 호출 힙 할당 -> 버벅임** | **HIGH** | **v0.1에서 확인됨**. per-session 캐시(D-2) + TRACK_LAST_CELL(D-3)로 힙 할당 0 달성 | **v0.2 필수** |
| R-3 | **Dispatcher.BeginInvoke 스레드 홉 -> 지연** | **HIGH** | **v0.1에서 확인됨**. WndProc 동기 P/Invoke(D-4)로 경로 단축 | **v0.2 필수** |
| R-4 | **드래그 중 렌더링 누락 (P2)** | **HIGH** | **v0.1에서 확인됨**. PTY 응답 지연 vs render invalidation 누락 원인 조사 필요. ghostty는 매 cursorPos 호출마다 `queueRender()` 트리거 | **v0.2 조사** |
| R-5 | 다중 pane 클릭 시 옆 pane 렌더링 사라짐 (P3) | **MEDIUM** | SurfaceFocus 기존 이슈. mouse-input 고유가 아닌 기존 알려진 문제. 별도 사이클 추적 | 기존 이슈 |
| R-6 | WndProc에서 모든 메시지 consume 시 OS 기본 동작 상실 | MEDIUM | 처리한 메시지만 consume, 미처리는 DefWindowProc 전달 유지 | 설계 반영 |
| R-7 | 텍스트 선택 시 render buffer 읽기 경로 부재 | HIGH | Engine API 확장(`gw_get_cell_text`) 또는 ghostty terminal C API 활용. M-10c 별도 설계 | M-10c 선행 |
| R-8 | WM_MOUSEWHEEL이 child HWND에 전달되지 않을 수 있음 | MEDIUM | Win32 기본: wheel 메시지는 포커스 윈도우로 전달. 필요시 parent forwarding 또는 `SetCapture` | M-10b 검증 |
| R-9 | cmux 패턴(Surface C API) 사용 불가 | ~~HIGH~~ | Encoder-level C API(Option A)로 대체. cell 중복/스크롤 누적은 GhostWin 자체 구현 | **설계 확정** |

---

## 9. Implementation Milestones

### Overview

```
v0.2 Implementation Order (4개 공통 패턴 준수 + v0.1 문제 해결)

M-10a: 클릭 + 모션 기반 (per-session 캐시 + 동기 처리 + cell 중복 제거)
  |
  v
M-10b: 스크롤 (누적 스크롤 + 모드 분기)
  |
  v
M-10c: 텍스트 선택 (별도 Design 필요)
  |
  v
M-10d: 통합 검증 + 성능 측정
```

### M-10a: Mouse Click + Motion v0.2 (P0, ~1주)

v0.1 대비 핵심 변경 4건:

| # | v0.1 | v0.2 | 공통 패턴 # |
|:-:|------|------|:-----------:|
| 1 | `encoder_new/free` + `event_new/free` 매 호출 | per-session Encoder/Event 캐시 | 패턴 1 |
| 2 | motion 중복 제거 없음 | `TRACK_LAST_CELL` 활성화 | 패턴 2 |
| 3 | `Dispatcher.BeginInvoke` 스레드 홉 | WndProc 동기 P/Invoke | 패턴 3 |
| 4 | 16ms 시간 throttle 계획 | 시간 throttle 완전 제거 | 패턴 2 |

구현 작업:

- [ ] C++ Engine: `gw_session_write_mouse` API + `gw_mouse_mode` API 구현
- [ ] C++ Engine: **per-session MouseState 캐시** -- `unordered_map<SessionId, MouseState>`. session 생성/종료와 생명주기 동기화
- [ ] C++ Engine: **`TRACK_LAST_CELL` 활성화** -- cell 중복 motion 자동 제거
- [ ] C++ Engine: **`setopt_from_terminal` 매 호출** -- Encoder 캐시 재사용 + terminal state 동기화
- [ ] C# Interop: `WriteMouseEvent` + `GetMouseMode` P/Invoke + IEngineService 확장
- [ ] WPF: WndProc 마우스 메시지 캡처 확장 (WM_LBUTTON/RBUTTON/MBUTTONDOWN/UP + WM_MOUSEMOVE)
- [ ] WPF: **Dispatcher.BeginInvoke 제거** -- WndProc에서 직접 `IEngineService.WriteMouseEvent` P/Invoke (동기)
- [ ] WPF: Modifier 전달 (wParam MK_CONTROL/SHIFT + GetKeyState VK_MENU for ALT)
- [ ] WPF: **clickCount 분기** (cmux 패턴) -- WM_LBUTTONDBLCLK에서 position 업데이트 스킵
- [ ] WPF: **Mouse.Capture/SetCapture** -- drag out-of-bounds 좌표 전달 (cmux 패턴)
- [ ] 조사: **드래그 중 렌더링 누락 원인** (R-4) -- PTY 응답 지연 vs render invalidation 누락
- [ ] 검증: vim `:set mouse=a` + click/drag, tmux mouse mode, htop 클릭

### M-10b: Mouse Scroll (P0, ~3일)

- [ ] WndProc: WM_MOUSEWHEEL 캡처 (R-8 검증 포함)
- [ ] **누적 스크롤**: `accumulatedScrollDelta` 픽셀 누적 -> `cell_height` 나누기 -> 나머지 보존 (패턴 4)
- [ ] 마우스 모드 활성 시: button 4(up)/5(down) VT 인코딩
- [ ] 마우스 모드 비활성 시: `gw_scroll_viewport` API + viewport offset 관리
- [ ] **auto-scroll**: 새 출력 도착 시 viewport 최하단 복귀
- [ ] 검증: vim scroll, non-mouse-mode scrollback, 고해상도 마우스 검증

### M-10c: Text Selection (P1, ~1주)

> 별도 상세 Design 필요. ghostty Selection.zig C API 미노출 제약.

- [ ] 마우스 모드 none/Shift bypass 판별 로직
- [ ] 드래그 시작/진행/종료 상태 관리
- [ ] Cell 좌표 기반 selection range 계산
- [ ] Selection 시각화 (DX11 render pass 또는 WPF overlay)
- [ ] 더블 클릭(word)/트리플 클릭(line) 선택
- [ ] Alt+드래그 rectangular selection
- [ ] Engine API 확장: `gw_get_cell_text(sessionId, startCol, startRow, endCol, endRow)` 검토
- [ ] 검증: 텍스트 선택 + 선택 영역 정확도

### M-10d: Integration + Polish (~3일)

- [ ] Per-pane 마우스 라우팅 검증 (다중 pane split 환경)
- [ ] DPI 변경 시 마우스 좌표 정확도 검증 (NFR-04)
- [ ] **성능 측정**: v0.2 개선 후 NFR-01(< 1ms latency), NFR-02(< 5% CPU) 실측
- [ ] **드래그 렌더링 검증**: R-4 조사 결과 반영하여 드래그 중 시각 갱신 확인
- [ ] vim/tmux/htop/nano 호환 smoke test
- [ ] 기존 E2E MQ-1~MQ-8 regression 확인

---

## Attribution

본 PRD는 PM Agent Team의 4단계 분석 프로세스로 작성되었습니다:
- **Discovery**: Opportunity Solution Tree (Pawel Huryn, pm-skills MIT License)
- **Strategy**: JTBD 6-Part Value Proposition + Lean Canvas
- **Research**: User Personas x3 + Competitive Analysis x5 + TAM/SAM/SOM
- **PRD Synthesis**: 9-section structured PRD

기술 분석 근거 (5-terminal benchmarking v0.3):

| Terminal | 핵심 파일 | 검증 방법 |
|----------|----------|-----------|
| ghostty | `Surface.zig`, `mouse_encode.zig`, `embedded.zig` | 로컬 소스 줄단위 읽기 |
| Windows Terminal | `mouseInput.cpp`, `ControlInteractivity.cpp`, `TermControl.cpp` | GitHub raw 소스 |
| Alacritty | `input/mod.rs`, `selection.rs`, `event.rs` | GitHub raw 소스 |
| WezTerm | `mouseevent.rs`, `terminalstate/mouse.rs`, `input.rs` | GitHub raw 소스 |
| cmux | `GhosttyTerminalView.swift`, `WorkspaceContentView.swift` | GitHub 소스 |

GhostWin 현재 코드 참조:
- `src/engine-api/ghostwin_engine.h` -- 현재 C API 19개 + Surface 4개 (마우스 API 없음)
- `src/GhostWin.App/Controls/TerminalHostControl.cs` -- 현재 WndProc (WM_LBUTTONDOWN 포커스 전용)
- `src/GhostWin.Core/Interfaces/IEngineService.cs` -- 현재 인터페이스 (마우스 메서드 없음)

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | PM Agent Team 초안 (4단계 분석) |
| 0.2 | 2026-04-10 | 4개 터미널 벤치마킹 + v0.1 smoke 성능 이슈 반영 (section 8, 9) |
| 1.0 | 2026-04-10 | **전면 재작성**. 5개 터미널 함수 본문 전수 조사(v0.3) 반영. cmux 패턴 발견 + Surface C API 미포함 제약 확정. Option A 확정. v0.1 hardware 검증 결과(5 TC) 반영. 9-section 구조 (Technical Architecture + Implementation Milestones 분리) |
