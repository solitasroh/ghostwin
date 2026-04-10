# mouse-input Planning Document

> **Summary**: 터미널 영역 마우스 클릭/스크롤/텍스트 선택 기능 구현 (M-10 마일스톤)
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft
> **PRD**: [mouse-input.prd.md](../../00-pm/mouse-input.prd.md)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 마우스 입력이 전혀 미구현 — vim/tmux/htop 등 TUI 앱 사용 불가, 텍스트 선택/복사 불가, 스크롤 불가. 경쟁 터미널 대비 기본 기능 부재 |
| **Solution** | WndProc 마우스 메시지 캡처 → C++ Engine에서 ghostty mouse_event/mouse_encode C API로 VT 시퀀스 변환 → ConPTY 전달. 비활성 모드에서는 scrollback + 텍스트 선택 |
| **Function/UX Effect** | vim :set mouse=a 완전 동작, 마우스 휠 스크롤, 드래그 텍스트 선택. "일상 터미널로 사용 가능" 수준 달성 |
| **Core Value** | 터미널 기본 기능 완성 — 경쟁 테이블 진입 자격 확보. 복사/붙여넣기(M-10 후속)의 선행 조건 |

---

## 1. Overview

### 1.1 Purpose

GhostWin에 마우스 입력 기능을 추가하여 "일상 터미널로 사용 가능한" 수준을 달성한다. ghostty upstream의 battle-tested 마우스 인코딩 C API를 활용하여 5가지 포맷(X10/UTF8/SGR/URxvt/SGR-Pixels) + 4가지 모드(x10/normal/button/any)를 네이티브 지원한다.

### 1.2 Background

- Phase 5-E pane-split 완료 → 다중 pane 환경에서 마우스 입력이 자연스러운 다음 단계
- P0 부채 전체 청산 완료 → 새 기능 개발에 집중 가능
- PRD: `docs/00-pm/mouse-input.prd.md` (PM Agent Team 분석 완료)
- Beachhead: Vim/Neovim 마우스 사용 개발자

### 1.3 Related Documents

- PRD: `docs/00-pm/mouse-input.prd.md`
- Roadmap: `docs/01-plan/roadmap.md` (M-10)
- ghostty mouse API: `external/ghostty/include/ghostty/vt/mouse/`
- ghostty mouse example: `external/ghostty/example/c-vt-encode-mouse/`

---

## 2. Scope

### 2.1 In Scope (PRD FR 기반)

| Sub-milestone | 항목 | Priority | 예상 |
|:-------------:|------|:--------:|:----:|
| **M-10a** | FR-01 마우스 클릭 → VT 시퀀스 전달 | P0 | 1주 |
| **M-10a** | FR-02 마우스 모션 트래킹 (button/any 모드) | P0 | |
| **M-10a** | FR-05 Ctrl/Shift/Alt modifier 전달 | P0 | |
| **M-10a** | FR-07 다중 pane 마우스 라우팅 | P0 | |
| **M-10b** | FR-03 마우스 휠 스크롤 (VT + scrollback) | P0 | 3일 |
| **M-10b** | FR-06 Scrollback viewport 이동 (비활성 모드) | P1 | |
| **M-10c** | FR-04 텍스트 선택 (드래그, 더블/트리플 클릭) | P1 | 1주 |
| **M-10d** | 통합 검증 + DPI + 성능 | P0 | 3일 |

### 2.2 Out of Scope (PRD §7.3)

- 마우스 커서 모양 변경 (cursor_shape) → M-11
- 클립보드 복사/붙여넣기 → M-10 후속 별도 feature
- URL auto-detection + Ctrl+Click → M-12
- Right-click context menu → M-11
- Drag-and-drop (파일) → M-13+

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Sub-MS | Priority |
|----|-------------|:------:|:--------:|
| FR-01 | WndProc 마우스 클릭(LB/RB/MB down/up) → ghostty mouse_encode → VT 시퀀스 → gw_session_write | M-10a | P0 |
| FR-02 | button/any 모드에서 WM_MOUSEMOVE → motion VT 시퀀스 (cell 중복 제거) | M-10a | P0 |
| FR-03 | WM_MOUSEWHEEL → 마우스 모드 시 VT scroll / 비활성 시 scrollback viewport | M-10b | P0 |
| FR-04 | 마우스 모드 none/Shift bypass 시 드래그 텍스트 선택 (word/line/block) | M-10c | P1 |
| FR-05 | Ctrl/Shift/Alt modifier를 mouse_event에 전달 | M-10a | P0 |
| FR-06 | 비활성 모드 scrollback viewport 이동 + auto-scroll | M-10b | P1 |
| FR-07 | 다중 pane에서 올바른 pane/session으로 마우스 라우팅 | M-10a | P0 |

### 3.2 Non-Functional Requirements

| ID | Criteria | Target |
|----|----------|--------|
| NFR-01 | 마우스 이벤트 지연 | WndProc → gw_session_write < 1ms |
| NFR-02 | Motion CPU 부하 | 연속 WM_MOUSEMOVE 시 < 5% |
| NFR-03 | Scroll 부드러움 | 60fps 프레임 드롭 없음 |
| NFR-04 | DPI 정확도 | 스케일링 후 정확한 cell 매핑 |

---

## 4. Technical Architecture

### 4.1 데이터 흐름 (PRD §8.1)

```
[Win32 WM_*] → [TerminalHostControl.WndProc]
                       │
                       ▼
             [C++ Engine: gw_mouse_input]
                  │              │
         (mouse mode ON)   (mouse mode OFF)
                  │              │
                  ▼              ▼
         [ghostty mouse_event  [gw_scroll_viewport /
          + mouse_encode]       WPF Selection]
                  │
                  ▼
         [VT bytes → gw_session_write → ConPTY]
```

### 4.2 계층별 변경 (PRD §8.2)

| Layer | File | 변경 |
|-------|------|------|
| **C++ Engine API** | `ghostwin_engine.h/cpp` | `gw_mouse_input`, `gw_scroll_viewport`, `gw_mouse_mode` 추가 |
| **C++ VtCore** | `vt_core.h/cpp` | ghostty mouse_event/mouse_encode 바인딩 + per-session MouseEncoder |
| **C# Interop** | `NativeEngine.cs` | P/Invoke 3개 추가 |
| **C# Interop** | `EngineService.cs` | `MouseInput`, `ScrollViewport`, `GetMouseMode` 구현 |
| **C# Core** | `IEngineService.cs` | 인터페이스 3개 메서드 추가 |
| **WPF** | `TerminalHostControl.cs` | WndProc 마우스 메시지 캡처 확장 |

### 4.3 핵심 설계 결정 (PRD §8.3)

| 결정 | 선택 | 근거 |
|------|------|------|
| 마우스 인코딩 위치 | C++ Engine (ghostty C API) | terminal state(mode/format)에 직접 접근 필요 |
| 이벤트 수신 방식 | WndProc (Win32 message) | HwndHost 기반 → WPF 라우팅 이벤트 불가 |
| 좌표 공간 | Surface-space pixels (lParam) | ghostty mouse_encode가 pixel→cell 변환 수행 |
| 텍스트 선택 | WPF overlay (1차) | ghostty Selection.zig C API 미노출 |

---

## 5. Implementation Order

```
M-10a: 마우스 클릭 + 모션 (~1주)
  ├─ T-1: C++ Engine API (gw_mouse_input + MouseEncoder)
  ├─ T-2: C# Interop (P/Invoke + IEngineService)
  ├─ T-3: WndProc 마우스 메시지 캡처 확장
  └─ T-4: vim :set mouse=a 검증

M-10b: 스크롤 (~3일)
  ├─ T-5: WM_MOUSEWHEEL 처리 (VT + scrollback 분기)
  ├─ T-6: gw_scroll_viewport API
  └─ T-7: vim/scrollback 검증

M-10c: 텍스트 선택 (~1주)
  ├─ T-8: Selection 상태 관리 (드래그, word/line)
  ├─ T-9: Selection 시각화 (overlay/render)
  └─ T-10: Shift bypass + Alt rectangular

M-10d: 통합 검증 (~3일)
  ├─ T-11: 다중 pane 라우팅 검증
  ├─ T-12: DPI 변경 검증
  └─ T-13: vim/tmux/htop/nano smoke test
```

---

## 6. Risks and Mitigation (PRD §8.4)

| Risk | Severity | Mitigation |
|------|:--------:|------------|
| ghostty mouse C API가 libvt 빌드에 미포함 | HIGH | `-Demit-lib-vt=true` 빌드에서 export 여부 사전 검증 (T-1 시작 전) |
| 텍스트 선택 시 render buffer 읽기 경로 없음 | HIGH | 1차: Engine API 확장(`gw_get_cell_text`) 검토. 불가 시 VT screen dump 활용 |
| Motion 이벤트 폭주 → CPU 부하 | MEDIUM | ghostty `track_last_cell` 중복 제거 + 16ms throttle |
| WndProc에서 DefWindowProc 미호출 시 OS 동작 상실 | MEDIUM | 처리한 메시지만 consume, 나머지는 DefWindowProc 전달 유지 |

---

## 7. Success Criteria

### 7.1 Definition of Done

- [ ] vim `:set mouse=a` — 클릭, 드래그, 스크롤 100% 동작
- [ ] tmux mouse mode — pane 클릭, 스크롤 동작
- [ ] htop — 프로세스 마우스 클릭 동작
- [ ] 비활성 모드 — 마우스 휠 scrollback 이동
- [ ] 텍스트 선택 — 드래그/더블/트리플 클릭 동작
- [ ] 다중 pane — 올바른 pane으로 마우스 라우팅

### 7.2 Quality Criteria

- [ ] NFR-01~04 충족
- [ ] 기존 E2E MQ-1~MQ-8 regression 없음
- [ ] 5가지 마우스 포맷 + 4가지 모드 커버

---

## 8. Next Steps

1. [ ] **사전 검증**: ghostty mouse C API가 libvt 빌드에 포함되는지 확인
2. [ ] Design 문서 작성 (`/pdca design mouse-input`) — M-10a부터 상세 설계
3. [ ] M-10a 구현 시작

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft (PRD 기반) | Claude + 노수장 |
