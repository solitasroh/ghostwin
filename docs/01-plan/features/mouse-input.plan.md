# mouse-input Planning Document (v1.0)

> **Summary**: 터미널 마우스 클릭/스크롤/텍스트 선택 — 5개 터미널 벤치마킹 기반 설계 (M-10)
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft (v1.0 — PRD v1.0 + 벤치마킹 v0.3 반영)
> **PRD**: [mouse-input.prd.md](../../00-pm/mouse-input.prd.md)
> **Benchmarking**: [mouse-input-benchmarking.md](../../00-research/mouse-input-benchmarking.md)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 마우스 입력 전혀 미구현 — vim/tmux 사용 불가, 텍스트 선택/복사 불가, 스크롤 불가. v0.1에서 성능 버벅임(Encoder 매 호출 생성 + Dispatcher 오버헤드) 확인 |
| **Solution** | `ghostty_mouse_encoder_*` C API + per-session Encoder 캐시 + WndProc 동기 P/Invoke + cell 중복 제거 + 스크롤 누적. 5개 터미널(ghostty/WT/Alacritty/WezTerm/cmux) 공통 패턴 적용 |
| **Function/UX Effect** | vim `:set mouse=a` 완전 동작, 마우스 휠 스크롤, 드래그 텍스트 선택. 버벅임 없는 부드러운 반응 |
| **Core Value** | "일상 터미널" 수준 달성 + 경쟁 터미널과 동등한 마우스 지원 |

---

## 1. Overview

### 1.1 Purpose

GhostWin에 마우스 입력 기능을 추가한다. 5개 터미널 코드베이스 줄단위 분석에서 확인된 4가지 공통 패턴을 적용하여, v0.1에서 발견된 성능 문제를 원천 해결한다.

### 1.2 Background

- v0.1 구현: TC-1(vim 좌클릭) PASS, TC-8(Shift bypass) PASS 확인. 하지만 성능 버벅임 + 드래그 렌더링 누락 + 다중 pane 렌더링 이슈 발견
- 벤치마킹: ghostty/WT/Alacritty/WezTerm/cmux 코드베이스 함수 본문 전수 조사 완료
- cmux 패턴 발견: `ghostty_surface_mouse_*` Surface API 직접 호출이 최적이나, GhostWin의 `-Demit-lib-vt=true` 빌드에서 미포함 → Option A (per-session Encoder 캐시) 확정

### 1.3 Related Documents

| 문서 | 경로 |
|------|------|
| PRD v1.0 | `docs/00-pm/mouse-input.prd.md` |
| Benchmarking v0.3 | `docs/00-research/mouse-input-benchmarking.md` |
| ghostty mouse API example | `external/ghostty/example/c-vt-encode-mouse/` |

---

## 2. 핵심 기술 결정 (벤치마킹 근거)

### 2.1 API 선택: Option A 확정

| Option | 방식 | 가능 여부 |
|--------|------|:---------:|
| **A (확정)** | `ghostty_mouse_encoder_*` per-session 캐시 | O (17개 심볼 export 확인) |
| B | `ghostty_surface_mouse_*` Surface API | X (libvt 빌드 미포함) |
| C | VT 인코딩 자체 구현 | X (C-1 constraint 위반) |

### 2.2 4가지 공통 패턴 적용

| # | 패턴 | v0.1 문제 | v1.0 적용 |
|:-:|-------|-----------|-----------|
| 1 | **힙 할당 최소화** | Encoder/Event 매 호출 new/free | per-session Encoder+Event 캐시 (new 1회) |
| 2 | **Cell 중복 제거** | 없음 | `track_last_cell = true` (ghostty 내장) |
| 3 | **동기 처리** | Dispatcher.BeginInvoke | WndProc → P/Invoke 직접 호출 |
| 4 | **스크롤 누적** | 미구현 | `pending_scroll` + cell_height 나누기 |

### 2.3 cmux 참조 패턴 (GhostWin 대체 전략)

| cmux 패턴 | GhostWin 대체 |
|-----------|---------------|
| `ghostty_surface_mouse_captured` | Encoder `setopt_from_terminal` → mode=none이면 비활성 |
| `clickCount == 1` pos 업데이트 | WPF에서 clickCount 체크 |
| drag out-of-bounds 좌표 전달 | `Mouse.Capture()` |
| Y축 flip (`bounds.height - y`) | WPF는 top-left origin, 변환 불필요 |

---

## 3. Scope

### 3.1 In Scope

| Sub-MS | 항목 | Priority | 예상 |
|:------:|------|:--------:|:----:|
| **M-10a** | FR-01 마우스 클릭 VT 전달 + per-session Encoder 캐시 | P0 | 1주 |
| **M-10a** | FR-02 모션 트래킹 (cell 중복 제거) | P0 | |
| **M-10a** | FR-05 Ctrl/Shift/Alt modifier | P0 | |
| **M-10a** | FR-07 다중 pane 라우팅 | P0 | |
| **M-10b** | FR-03 마우스 휠 스크롤 (VT + 누적) | P0 | 3일 |
| **M-10b** | FR-06 Scrollback viewport (비활성 모드) | P1 | |
| **M-10c** | FR-04 텍스트 선택 (드래그/더블/트리플) | P1 | 1주 |
| **M-10d** | 통합 검증 + DPI + 성능 | P0 | 3일 |

### 3.2 Out of Scope

- 마우스 커서 모양 변경 → M-11
- 클립보드 복사/붙여넣기 → M-10 후속
- URL auto-detection → M-12
- Right-click context menu → M-11

---

## 4. Requirements (PRD §7 참조)

### 4.1 Functional Requirements

| ID | Requirement | Priority | v0.1 결과 |
|----|-------------|:--------:|:---------:|
| FR-01 | 마우스 클릭 → ghostty mouse_encode → VT → ConPTY | P0 | TC-1 PASS |
| FR-02 | button/any 모드 motion 트래킹 (cell 중복 제거) | P0 | TC-2 부분 |
| FR-03 | WM_MOUSEWHEEL 스크롤 (VT + scrollback 분기) | P0 | TC-5 FAIL |
| FR-04 | 텍스트 선택 (드래그/word/line/block) | P1 | — |
| FR-05 | Ctrl/Shift/Alt modifier 전달 | P0 | PASS (TC-8) |
| FR-06 | 비활성 모드 scrollback viewport | P1 | — |
| FR-07 | 다중 pane 마우스 라우팅 | P0 | TC-7 부분 |

### 4.2 Non-Functional Requirements

| ID | Criteria | Target | 근거 |
|----|----------|--------|------|
| NFR-01 | 마우스 이벤트 지연 | < 1ms | WT: UI 동기, ghostty: 스택 38B |
| NFR-02 | Motion CPU 부하 | < 5% | cell 중복 제거로 VT 전송 최소화 |
| NFR-03 | Scroll 부드러움 | 60fps 드롭 없음 | 누적 패턴 (ghostty/Alacritty) |
| NFR-04 | DPI 정확도 | 정확한 cell 매핑 | ghostty encoder의 pixel→cell 변환 |

---

## 5. Implementation Order

```
M-10a v1.0: 마우스 클릭 + 모션 (~1주)
  T-1: C++ Engine — per-session Encoder/Event 캐시 + gw_session_write_mouse
       - EngineImpl에 unordered_map<SessionId, {Encoder, Event}> 추가
       - session 생성 시 encoder_new + event_new
       - session 종료 시 encoder_free + event_free
       - setopt_from_terminal 매 호출 (mode/format 동기화)
       - track_last_cell = true 설정
  T-2: C# Interop — P/Invoke + IEngineService.WriteMouseEvent
  T-3: WndProc — 마우스 메시지 캡처 + 동기 P/Invoke (Dispatcher 없음)
       - WM_*BUTTONDOWN/UP + WM_MOUSEMOVE
       - lParam 좌표 + wParam modifier 추출
       - 직접 engine.WriteMouseEvent 호출 (BeginInvoke 금지)
  T-4: 빌드 + vim :set mouse=a 검증

M-10b v1.0: 스크롤 (~3일)
  T-5: WM_MOUSEWHEEL 처리
       - 마우스 모드 활성: button 4/5 VT 인코딩
       - 마우스 모드 비활성: scrollback viewport 이동
  T-6: 스크롤 누적 패턴
       - per-session pending_scroll_y 누적
       - cell_height 나누기 → 정수 delta 추출
       - 나머지 보존 (Alacritty `%=` 패턴)
  T-7: vim/scrollback 검증

M-10c: 텍스트 선택 (~1주, 별도 Design)
  T-8~T-12: Selection 상태, 시각화, Shift bypass, word/line/block

M-10d: 통합 검증 (~3일)
  T-13~T-15: 다중 pane, DPI, vim/tmux/htop smoke
```

---

## 6. Risks and Mitigation

| Risk | Severity | Mitigation | 상태 |
|------|:--------:|------------|:----:|
| ghostty mouse API libvt 미포함 | ~~HIGH~~ | T-1에서 17개 심볼 export 확인 | **해소** |
| ghostty Surface API 미포함 | ~~HIGH~~ | Option A (Encoder 캐시) 확정 | **해소** |
| **Encoder 매 호출 new/free 성능** | **HIGH** | per-session 캐시 (패턴 1) | v1.0 적용 |
| **Dispatcher.BeginInvoke 지연** | **HIGH** | WndProc 동기 P/Invoke (패턴 3) | v1.0 적용 |
| **드래그 중 렌더링 누락** | **MEDIUM** | 원인 진단 필요 (ConPTY→렌더러 경로 조사) | M-10d |
| **다중 pane 렌더링 사라짐** | **MEDIUM** | SurfaceFocus 기존 이슈, 별도 추적 | 기존 이슈 |
| WM_MOUSEWHEEL child HWND 전달 | MEDIUM | child에 직접 오지 않으면 parent forwarding | T-5 |

---

## 7. Success Criteria

### 7.1 Definition of Done

- [ ] vim `:set mouse=a` — 클릭, 드래그, 스크롤 동작
- [ ] 비활성 모드 — 마우스 휠 scrollback 이동
- [ ] 다중 pane — 올바른 pane으로 마우스 라우팅
- [ ] 성능 — 버벅임 없음 (v0.1 대비 개선 확인)
- [ ] TC-5 (스크롤) PASS

### 7.2 Quality Criteria

- [ ] NFR-01~04 충족
- [ ] 기존 E2E MQ-1~MQ-8 regression 없음

---

## 8. Affected Files (Estimate)

| Layer | File | 변경 |
|-------|------|------|
| C++ Engine | `ghostwin_engine.h/cpp` | `gw_session_write_mouse` + per-session 캐시 |
| C++ Engine | `session_manager.h/cpp` | SessionState에 Encoder/Event 멤버 추가 |
| C# Core | `IEngineService.cs` | `WriteMouseEvent` |
| C# Interop | `NativeEngine.cs`, `EngineService.cs` | P/Invoke + 구현 |
| WPF | `TerminalHostControl.cs` | WndProc 마우스 + 동기 P/Invoke |
| WPF | `PaneContainerControl.cs` | 이벤트 구독 |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial draft |
| 1.0 | 2026-04-10 | PRD v1.0 + 벤치마킹 v0.3 반영. 4 공통 패턴, Option A 확정, v0.1 검증 결과 |
