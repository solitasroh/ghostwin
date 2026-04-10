# PRD: Mouse Input for GhostWin Terminal

> **Feature**: mouse-input
> **Project**: GhostWin Terminal
> **Author**: PM Agent Team (Discovery + Strategy + Research + PRD Synthesis)
> **Date**: 2026-04-10
> **Status**: Draft
> **Milestone**: M-10 (Terminal Basic Operations)

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
| NFR-05 | Thread safety | WndProc(Win32 thread) -> Dispatcher(UI thread) 마셜링 안전 |

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

### 8.1 Data Flow

```
[Win32 WM_*]  -->  [TerminalHostControl.WndProc]
                         |
                         v
               [C++ Engine: MouseHandler]
                    |              |
          (mouse mode ON)    (mouse mode OFF)
                    |              |
                    v              v
          [ghostty mouse_event   [Scrollback viewport /
           + mouse_encode]        WPF Selection]
                    |
                    v
          [VT sequence bytes]
                    |
                    v
          [gw_session_write -> ConPTY]
```

### 8.2 API Additions

#### C++ Engine API (ghostwin_engine.h)

```c
// Mouse input forwarding
GWAPI int gw_mouse_input(GwEngine engine, GwSurfaceId surface_id,
                          uint32_t msg, uintptr_t wParam, intptr_t lParam);

// Scrollback viewport control (non-mouse-mode)
GWAPI int gw_scroll_viewport(GwEngine engine, GwSessionId id, int32_t delta_rows);

// Query mouse tracking state
GWAPI int gw_mouse_mode(GwEngine engine, GwSessionId id);  // returns MouseEvent enum
```

#### C# Interop (IEngineService)

```csharp
int MouseInput(uint surfaceId, uint msg, nint wParam, nint lParam);
int ScrollViewport(uint sessionId, int deltaRows);
int GetMouseMode(uint sessionId);
```

### 8.3 Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Mouse encode location | C++ Engine (using ghostty C API) | ghostty의 terminal state(mouse_event, mouse_format)에 직접 접근 필요. C# 측에서는 terminal state를 알 수 없음 |
| WndProc vs WPF event | WndProc (Win32 message) | TerminalHostControl은 HwndHost 기반이라 WPF 라우팅 이벤트가 아닌 Win32 메시지를 직접 수신. 기존 패턴(WM_LBUTTONDOWN 포커스) 확장 |
| Mouse coordinate space | Surface-space pixels (WndProc lParam) | ghostty mouse_encode가 pixel -> cell 변환을 내부 수행. DPI 변환은 WndProc 진입 시 불필요 (child HWND가 이미 pixel 단위) |
| Scroll in non-mouse-mode | Engine-side viewport offset | scrollback buffer는 ghostty terminal 내부에 있으므로, viewport offset 조작은 engine 측에서 수행 |
| Selection rendering | WPF overlay (1차) | ghostty Selection.zig가 C API export 없음. 1차에서는 render buffer 읽기 + WPF Adorner/overlay로 구현. 2차에서 ghostty C API 확장 검토 |
| Shift bypass | WndProc에서 wParam modifier 검사 | Shift 눌린 상태의 마우스 이벤트는 VT 인코딩 대신 선택 모드로 분기. Windows Terminal/Alacritty 동일 패턴 |

### 8.4 Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| ghostty mouse C API가 libvt 빌드에 포함되지 않을 수 있음 | HIGH | `terminal.c.main.zig`에 mouse_event/mouse_encode export 확인 완료. `-Demit-lib-vt=true` 빌드에서 export 여부 사전 검증 필요 |
| WndProc에서 모든 마우스 메시지를 처리하면 DefWindowProc이 호출되지 않아 OS 기본 동작 상실 | MEDIUM | 처리한 메시지만 consume, 미처리 메시지는 DefWindowProc 전달 유지 |
| 다중 pane에서 마우스 좌표가 pane 로컬이 아닌 윈도우 전체 좌표로 전달될 수 있음 | LOW | 각 TerminalHostControl의 child HWND가 독립 WndProc을 가지므로 lParam은 이미 child-local 좌표 |
| 텍스트 선택 시 render buffer 읽기 경로가 없음 | HIGH | 1차에서 Engine API 확장(`gw_get_cell_text`) 필요. 또는 ghostty terminal C API의 `render` 모듈 활용 |
| Motion 이벤트 폭주로 CPU 부하 | MEDIUM | `track_last_cell` 중복 제거 + 16ms throttle (60fps) 적용 |

---

## 9. Implementation Milestones

### M-10a: Mouse Click + Motion (P0, ~1주)

- [ ] C++ Engine: `gw_mouse_input` API 구현 (ghostty mouse_event/mouse_encode 연동)
- [ ] C++ Engine: per-session `MouseEncoder` 인스턴스 관리
- [ ] WndProc: WM_LBUTTON/RBUTTON/MBUTTONDOWN/UP + WM_MOUSEMOVE 캡처
- [ ] C# Interop: `MouseInput` P/Invoke + IEngineService 확장
- [ ] Modifier 전달 (wParam에서 MK_CONTROL/SHIFT/ALT 추출)
- [ ] 검증: vim `:set mouse=a` + click/drag

### M-10b: Mouse Scroll (P0, ~3일)

- [ ] WndProc: WM_MOUSEWHEEL 캡처 + delta 추출
- [ ] 마우스 모드 활성 시: button 4/5 VT 인코딩
- [ ] 마우스 모드 비활성 시: `gw_scroll_viewport` API + viewport offset 관리
- [ ] 검증: vim scroll, non-mouse-mode scrollback

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
- [ ] 성능 측정 (NFR-01~03)
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
