# Mouse Input Benchmarking: 4개 터미널 코드베이스 분석

> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Complete (코드베이스 검증 완료)
> **Related**: [mouse-input.design.md](../02-design/features/mouse-input.design.md), [mouse-input.prd.md](../00-pm/mouse-input.prd.md)

---

## 0. 조사 목적

M-10a v0.1 구현에서 발견된 성능 문제 해결을 위한 참조 구현 벤치마킹.

| # | v0.1 문제 | 영향 |
|:-:|-----------|------|
| P1 | MouseEncoder/Event 매 호출 new/free + Dispatcher.BeginInvoke | 버벅임 |
| P2 | 드래그 중 release 시에만 시각 갱신 | 드래그 렌더링 누락 |
| P3 | 다중 pane 클릭 시 옆 pane 렌더링 사라짐 | SurfaceFocus 기존 이슈 |

---

## 1. 크로스 분석 (코드 근거 포함)

### Q1: VT 인코딩 객체 수명

| Terminal | 패턴 | 힙 할당 | 코드 근거 |
|----------|------|:-------:|-----------|
| **ghostty** | `encode()` 순수 함수 + 스택 38B 버퍼 | **0** | `Surface.zig:3631` `var data: WriteReq.Small.Array = undefined` |
| **WT** | `fmt::format(FMT_COMPILE)` stateless 함수 | **0** | `mouseInput.cpp` `_GenerateSGRSequence` — constexpr 순수 함수 |
| **Alacritty** | `format!` 매크로 → 스택 String | **최소** | `input/mod.rs` `sgr_mouse_report` — `format!("\x1b[<...")` |
| **WezTerm** | `write!` 매크로 → self.writer 직접 | **0** | `terminalstate/mouse.rs` `write!(self.writer, "\x1b[<...")` |

**결론: 4개 전부 Encoder 객체 없음. stateless 함수 또는 직접 write.**
v0.1의 `ghostty_mouse_encoder_new()/free()` 매 호출은 **어떤 참조 구현에도 없는 패턴**.

### Q2: Motion Throttling

| Terminal | 방식 | 시간 기반 | 코드 근거 |
|----------|------|:---------:|-----------|
| **ghostty** | 2단계: 1px 글로벌 + cell 중복 제거 | **없음** | `embedded.zig:870` 1px 필터, `mouse_encode.zig:107` `last_cell` 비교 |
| **WT** | cell 좌표 + button 비교 | **없음** | `mouseInput.cpp` `sameCoord = (lastPos == position && lastButton == button)` |
| **Alacritty** | cell + cell_side + inside_text_area | **없음** | `input/mod.rs` `if !cell_changed && same_side && same_area { return; }` |
| **WezTerm** | cell 비교 (SgrPixels만 픽셀) | **없음** | `terminalstate/mouse.rs` `last.x != event.x || last.y != event.y` |

**결론: 4개 전부 cell 기반 중복 제거. 시간 기반 throttle(16ms 등) 없음.**

### Q3: 스레드 모델

| Terminal | 처리 위치 | Dispatcher/큐 | 코드 근거 |
|----------|----------|:-------------:|-----------|
| **ghostty** | 콜백 스레드 동기 인코딩 → IO 큐 | 인코딩은 동기, 전송만 큐 | `Surface.zig:3654` `self.queueIo(.write_small)` |
| **WT** | UI 스레드 동기 (write lock) | **없음** | `TermControl.cpp` → `ControlInteractivity` → `Terminal::SendMouseEvent` |
| **Alacritty** | winit 이벤트 루프 단일 스레드 | **없음** | `event.rs` `handle_event` → `mouse_moved` 동기 |
| **WezTerm** | GUI 스레드 동기 (`&mut self`) | **없음** | `mouseevent.rs` `mouse_event_impl(&mut self)` 동기 |

**결론: 4개 전부 이벤트 스레드에서 동기 처리. Dispatcher.BeginInvoke 패턴 없음.**
v0.1의 `Dispatcher.BeginInvoke`는 불필요한 스레드 홉으로 지연 원인.

### Q4: 스크롤 처리

| Terminal | 패턴 | 코드 근거 |
|----------|------|-----------|
| **ghostty** | `pending_scroll_y` 누적 → cell_height 나누기 → 나머지 보존 | `Surface.zig:3392` |
| **WT** | `accumulatedDelta` 누적 → `WHEEL_DELTA(120)` 임계값 | `mouseInput.cpp` |
| **Alacritty** | `accumulated_scroll` 픽셀 누적 → cell_height 나누기 → `%=` 나머지 | `input/mod.rs` `scroll_terminal` |
| **WezTerm** | discrete 정규화 (delta=1) | `mouseevent.rs` `*delta = 1` |

**결론: ghostty/WT/Alacritty는 누적 방식. 고해상도 마우스 지원.**

### Q5: 다중 Pane 라우팅

| Terminal | 방식 | 코드 근거 |
|----------|------|-----------|
| **ghostty** | 각 Surface가 독립 콜백 | `embedded.zig` per-Surface `mouseButtonCallback` |
| **WT** | XAML visual tree hit-test | `Pane.cpp` `_borderTappedHandler` |
| **Alacritty** | 단일 pane (해당 없음) | — |
| **WezTerm** | 좌표 hit-test + `MouseCapture(PaneId)` | `mouseevent.rs` `get_panes_to_render()` 순회 |

**결론: GhostWin의 child HWND 구조는 ghostty와 동일 (per-pane 독립 WndProc).**

### Q5b: 드래그 중 렌더링 갱신

| Terminal | 방식 | 코드 근거 |
|----------|------|-----------|
| **ghostty** | `queueRender()` 매 cursorPos 호출 | `Surface.zig:4599` |
| **WT** | PTY 출력 → 렌더러 자동 갱신 | `Terminal.cpp` connection output 트리거 |
| **Alacritty** | `*self.ctx.dirty = true` 플래그 | `input/mod.rs` dirty → `request_redraw()` |
| **WezTerm** | pane에 직접 mouse_event → PTY 응답 → 렌더 | `mouseevent.rs` |

**결론: 모든 터미널에서 마우스 이벤트 전달 → PTY 응답 → 자동 렌더. v0.1에서 드래그 렌더링 안 되는 건 별도 원인 조사 필요.**

---

## 2. GhostWin v0.2 설계 지침

위 분석에서 도출된 **4개 터미널 공통 패턴**:

### 필수 적용 (4/4 공통)

| # | 패턴 | v0.1 | v0.2 |
|:-:|-------|------|------|
| 1 | **Encoder 객체 없음** — stateless 인코딩 | `encoder_new/free` 매 호출 | per-session 캐시 또는 stateless 전환 |
| 2 | **Cell 중복 제거** — 같은 cell이면 motion 무시 | 없음 | `lastCell` 비교 필수 |
| 3 | **동기 처리** — 이벤트 스레드에서 직접 | `Dispatcher.BeginInvoke` | WndProc → P/Invoke 직접 호출 |
| 4 | **스크롤 누적** — 픽셀/delta 누적 → cell 단위 변환 | 미구현 | `pending_scroll` + cell_height 나누기 |

### GhostWin 특수 제약

ghostty C API wrapper (`ghostty_mouse_encoder_*`)는 opaque handle 기반이라 내부 `encode()` 순수 함수를 직접 호출 불가. 대안:

| Option | 설명 | 장단점 |
|--------|------|--------|
| **A** | per-session Encoder 캐시 (new 1회, free는 session 종료 시) | C API 그대로 사용. 매 호출 new/free 제거 |
| **B** | C++ 엔진에서 ghostty 내부 `encode()` 직접 호출 (Zig 링크) | 힙 할당 0. 하지만 ghostty 내부 API 의존 |
| **C** | VT 인코딩을 C++ 자체 구현 (ghostty 미사용) | upstream 동기화 부담. C-1 constraint 위반 |

**추천: Option A** — C API 정상 사용 + per-session 캐시로 성능 문제 해결.

---

## 3. 소스 코드 참조

| Terminal | 핵심 파일 | 조사 방법 |
|----------|----------|-----------|
| ghostty | `Surface.zig`, `mouse_encode.zig`, `embedded.zig` | 로컬 소스 직접 읽기 |
| WT | `TermControl.cpp`, `mouseInput.cpp`, `ControlInteractivity.cpp` | GitHub 코드 WebFetch |
| Alacritty | `input/mod.rs`, `selection.rs` | GitHub 코드 WebFetch |
| WezTerm | `mouseevent.rs`, `terminalstate/mouse.rs` | GitHub 코드 WebFetch |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | CTO-lead 초안 |
| 0.2 | 2026-04-10 | 4개 터미널 코드베이스 직접 검증 (병렬 agent 4개) |
