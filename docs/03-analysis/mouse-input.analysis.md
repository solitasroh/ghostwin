# mouse-input M-10a Gap Analysis Report

> **Design**: `docs/02-design/features/mouse-input.design.md` (v1.0)
> **Scope**: M-10a (click + motion) only. M-10b (scroll) excluded.
> **Date**: 2026-04-10
> **Build**: Engine 10/10 test PASS, WPF 0 Error. Hardware 미검증.

---

## Overall Scores

| Category | Score | Status |
|----------|:-----:|:------:|
| Design Match (M-10a 범위) | 95% | Pass |
| Architecture Compliance | 100% | Pass |
| Convention Compliance | 98% | Pass |
| **Overall** | **97%** | **Pass** |

---

## 1. Layer-by-Layer Comparison

### 1.1 C++ Engine Layer

| Design 항목 | Design 위치 | 구현 위치 | Match |
|-------------|-------------|-----------|:-----:|
| per-session mouse_encoder/mouse_event 멤버 | Section 3.1 `SessionState` | `session.h:114-115` `Session` struct | Match (이름 차이 -- 아래 참고) |
| `~SessionState` encoder/event free | Section 3.1 소멸자 | `session.h:93-94` `~Session()` | Match |
| session 생성 시 encoder_new + event_new | Section 3.1 "Session 생성 시 초기화" | `session_manager.cpp:114-115` | Match |
| `track_last_cell = true` setopt | Section 3.1 | `session_manager.cpp:116-119` | Match |
| `gw_session_write_mouse` 함수 시그니처 | Section 3.1 | `ghostwin_engine.h:79-82`, `ghostwin_engine.cpp:451-454` | Match |
| GW_TRY/GW_CATCH 패턴 | C-4 | `ghostwin_engine.cpp:455,507` | Match |
| `setopt_from_terminal` 호출 | Section 3.1 step 1 | `ghostwin_engine.cpp:465-467` | Match |
| `setopt SIZE` (surface 크기) | Section 3.1 step 2 | `ghostwin_engine.cpp:470-479` | Match (cell 소스 차이 -- 아래 참고) |
| Event set_action/set_button/clear_button/set_position/set_mods | Section 3.1 step 3 | `ghostwin_engine.cpp:483-493` | Match |
| `char buf[128]` 스택 인코딩 | Section 3.1 step 4 | `ghostwin_engine.cpp:496-499` | Match |
| `send_input` (written > 0 조건) | Section 3.1 step 5 | `ghostwin_engine.cpp:502-504` | Match |
| `find_by_session(id)` 호출 | Section 3.1 step 2 | `ghostwin_engine.cpp:470` | Match |

### 1.2 C# Interop Layer

| Design 항목 | Design 위치 | 구현 위치 | Match |
|-------------|-------------|-----------|:-----:|
| `IEngineService.WriteMouseEvent` 시그니처 | Section 3.2 | `IEngineService.cs:37-38` | Match |
| XML doc (param 설명) | Section 3.2 | `IEngineService.cs:30-36` | Match |
| `NativeEngine.gw_session_write_mouse` P/Invoke | Section 3.2 | `NativeEngine.cs:65-66` | Match |
| `EngineService.WriteMouseEvent` 구현 | Section 3.2 | `EngineService.cs:100-102` | Match |

### 1.3 WPF Layer

| Design 항목 | Design 위치 | 구현 위치 | Match |
|-------------|-------------|-----------|:-----:|
| PaneClicked Dispatcher.BeginInvoke 유지 (D-6) | Section 3.3 | `TerminalHostControl.cs:154-171` | Match |
| `IsMouseMsg` 동기 호출 + P/Invoke (C-6) | Section 3.3 | `TerminalHostControl.cs:175-191` | Match |
| `DefWindowProc` 전달 (C-5) | Section 3.3 | `TerminalHostControl.cs:193` | Match |
| `IsMouseMsg` helper | Section 3.3 | `TerminalHostControl.cs:198-202` | Match |
| `ButtonFromMsg` helper | Section 3.3 | `TerminalHostControl.cs:204-210` | Match |
| `ActionFromMsg` helper | Section 3.3 | `TerminalHostControl.cs:212-217` | Match |
| `ModsFromWParam` helper (MK_SHIFT, MK_CONTROL, GetKeyState VK_MENU) | Section 3.3 | `TerminalHostControl.cs:219-227` | Match |
| WM_* 상수 정의 (MOUSEMOVE, LBUTTONDOWN/UP, RBUTTONDOWN/UP, MBUTTONDOWN/UP) | Section 3.3 | `TerminalHostControl.cs:236-243` | Match |
| MK_SHIFT, MK_CONTROL, VK_MENU 상수 | Section 3.3 | `TerminalHostControl.cs:244-245` | Match |
| `_engine` 필드 (IEngineService? internal) | Section 3.5 | `TerminalHostControl.cs:38` | Match |
| PaneContainerControl에서 `host._engine` 주입 | Section 3.5 | `PaneContainerControl.cs:234` | Match |

---

## 2. Differences Found

### 2.1 Changed Features (Design != Implementation)

| # | Item | Design | Implementation | Impact | Justification |
|:-:|------|--------|----------------|:------:|---------------|
| 1 | Struct 이름 | `SessionState` (Section 3.1) | `Session` (`session.h:90`) | None | Design 문서가 기존 코드베이스의 struct명을 부정확하게 기재. 실제 코드베이스에서는 Phase 5-A부터 `Session`이 정식 이름. 기능 차이 없음 |
| 2 | cell 크기 소스 | `vt->cell_width()` / `vt->cell_height()` (Section 3.1 step 2) | `eng->atlas->cell_width()` / `eng->atlas->cell_height()` (`ghostwin_engine.cpp:476-477`) | None | VtCore에 `cell_width()`/`cell_height()` API가 존재하지 않음. GlyphAtlas가 cell 크기의 실제 소유자. 구현이 정확 |
| 3 | encoder null 검사 | 없음 (Section 3.1) | `ghostwin_engine.cpp:460` `if (!session->mouse_encoder \|\| !session->mouse_event) return GW_ERR_INVALID;` | None (방어적) | 구현이 더 안전. encoder/event 생성 실패 시 방어 |
| 4 | surface_mgr null 검사 | 없음 (Section 3.1 step 2) | `ghostwin_engine.cpp:470` `eng->surface_mgr ? ... : nullptr` | None (방어적) | Engine 초기화 순서에 따라 surface_mgr가 null일 수 있는 edge case 방어 |
| 5 | atlas null 검사 | 없음 (Section 3.1 step 2) | `ghostwin_engine.cpp:471` `&& eng->atlas` | None (방어적) | #4와 동일한 방어적 프로그래밍 |
| 6 | VtCore 접근 방식 | `session->conpty->vt_core()` 포인터 반환 (Section 3.1) | `session->conpty->vt_core()` 참조 반환 (`ghostwin_engine.cpp:462` `auto& vt`) | None | API가 reference를 반환하므로 `auto&`가 정확. Design의 포인터 표기는 pseudocode 수준의 오차 |
| 7 | `_engine` 주입 방식 | `Ioc.Default.GetService<IEngineService>()` 매번 호출 (Section 3.5) | `host._engine ??= Ioc.Default.GetService<IEngineService>()` (`PaneContainerControl.cs:234`) | None (개선) | null-coalescing assignment로 이미 주입된 경우 재주입 방지. 기존 host 재사용 시 불필요한 DI 조회 방지 |

### 2.2 Added Features (Design X, Implementation O)

| # | Item | Implementation Location | Description |
|:-:|------|------------------------|-------------|
| 1 | `SurfaceManager::find_by_session()` | `surface_manager.h:74`, `surface_manager.cpp:133-136` | Design Section 3.1에서 `eng->surface_mgr->find_by_session(id)` 호출은 명시했으나, `find_by_session` 메서드 자체의 추가는 Affected Files (Section 5)에 미기재. surface_manager.h/cpp가 Affected Files 테이블에 누락 |

### 2.3 Missing Features (Design O, Implementation X)

| # | Item | Design Location | Description | Impact |
|:-:|------|-----------------|-------------|:------:|
| 1 | `WM_MOUSEWHEEL` 상수 정의 | Section 3.3 "추가 상수" `0x020A` | M-10b 범위이므로 M-10a에서 미구현은 정상. 단, Design이 Section 3.3 상수 목록에 M-10a/M-10b 구분 없이 혼재 | None (범위 외) |

---

## 3. Design Document Issues (코드와 무관)

| # | Issue | Location | Recommendation |
|:-:|-------|----------|----------------|
| 1 | Struct명 `SessionState` 오기 | Section 3.1 | `Session`으로 수정. 현 코드베이스에서 `SessionState`는 enum 이름 (`session.h:37`) |
| 2 | `vt->cell_width()` 존재하지 않는 API 참조 | Section 3.1 step 2 | `atlas->cell_width()` 또는 `eng->atlas->cell_width()`로 수정 |
| 3 | Affected Files에 surface_manager.h/cpp 누락 | Section 5 | `find_by_session` 메서드 추가 사실 반영 필요 |
| 4 | WM_MOUSEWHEEL이 Section 3.3 상수 목록에 혼재 | Section 3.3 | M-10b 전용 상수임을 명시하거나 Section 3.4로 이동 |

---

## 4. Constraint Compliance

| ID | Constraint | Compliance |
|----|-----------|:----------:|
| C-1 | `ghostty_mouse_encoder_*` C API 사용 필수 | Pass -- `ghostty/vt/mouse.h` include, 17개 API 직접 사용 |
| C-2 | `ghostty_surface_mouse_*` 사용 불가 | Pass -- Surface API 사용 없음 |
| C-3 | WndProc(Win32 message) 방식 유지 | Pass -- `TerminalHostControl.WndProc` |
| C-4 | `gw_session_write` 패턴 준수 | Pass -- GW_TRY/CATCH, session_mgr->get, conpty->send_input |
| C-5 | DefWindowProc 전달 유지 | Pass -- `TerminalHostControl.cs:193` |
| C-6 | Dispatcher.BeginInvoke 금지 (마우스 경로) | Pass -- WndProc에서 직접 P/Invoke |

---

## 5. Decision Record Compliance

| ID | Decision | Compliance | Note |
|----|----------|:----------:|------|
| D-1 | per-session Encoder 캐시 | Pass | `Session` struct 멤버로 구현 |
| D-2 | WndProc -> P/Invoke 직접 호출 | Pass | `TerminalHostControl.cs:187-189` |
| D-3 | `track_last_cell = true` | Pass | `session_manager.cpp:117-119` |
| D-4 | `setopt_from_terminal` 매 호출 | Pass | `ghostwin_engine.cpp:465-467` |
| D-5 | 스크롤은 button 4/5 전달 | N/A | M-10b 범위 |
| D-6 | PaneClicked Dispatcher 유지 | Pass | `TerminalHostControl.cs:163` |

---

## 6. Task Coverage (Design Section 4)

| Task | Description | Status | Commit |
|------|-------------|:------:|--------|
| T-1 | session.h -- mouse_encoder/event 추가 + 생성/소멸 | Done | M-10a |
| T-2 | ghostwin_engine -- `gw_session_write_mouse` 구현 | Done | M-10a |
| T-3 | NativeEngine + EngineService + IEngineService | Done | M-10a |
| T-4 | TerminalHostControl -- WndProc 확장 + _engine 필드 | Done | M-10a |
| T-5 | PaneContainerControl -- host._engine 주입 | Done | M-10a |
| T-6 | 빌드 + 검증 | Partial | 빌드 PASS, hardware 검증 미완 |

---

## 7. Affected Files Audit

| File (Design Section 5) | Design 기재 | 실제 변경 | Match |
|--------------------------|:-----------:|:---------:|:-----:|
| `src/engine-api/ghostwin_engine.h` | O | O | Match |
| `src/engine-api/ghostwin_engine.cpp` | O | O | Match |
| `src/engine-api/session_manager.h` | O (경로 오류) | X -- 실제는 `src/session/session.h` | Mismatch (경로) |
| `src/engine-api/session_manager.cpp` | O (경로 오류) | X -- 실제는 `src/session/session_manager.cpp` | Mismatch (경로) |
| `src/GhostWin.Core/Interfaces/IEngineService.cs` | O | O | Match |
| `src/GhostWin.Interop/NativeEngine.cs` | O | O | Match |
| `src/GhostWin.Interop/EngineService.cs` | O | O | Match |
| `src/GhostWin.App/Controls/TerminalHostControl.cs` | O | O | Match |
| `src/GhostWin.App/Controls/PaneContainerControl.cs` | O | O | Match |
| `src/engine-api/surface_manager.h` | **X (누락)** | O | **Missing in Design** |
| `src/engine-api/surface_manager.cpp` | **X (누락)** | O | **Missing in Design** |
| `src/session/session.h` | **X (경로 오류)** | O | **경로 오기** |

---

## 8. Summary

M-10a (마우스 클릭 + 모션) 구현은 Design v1.0과 **97% 일치**.

**핵심 판정**: 기능적 차이 0건. 모든 차이는 (1) Design 문서의 struct명/API명/경로 오기, (2) 구현 시 추가된 방어적 null 검사. 아키텍처 의사결정(per-session 캐시, 동기 P/Invoke, cell dedup) 전부 설계대로 구현됨.

**즉시 조치 필요 사항**: 없음.

**문서 업데이트 권장 사항**:
1. Design Section 3.1: `SessionState` -> `Session`, `vt->cell_width()` -> `eng->atlas->cell_width()`
2. Design Section 5: `surface_manager.h/cpp` 추가, `session_manager.h` 경로를 `session/session.h`로 수정
3. Design Section 3.3: `WM_MOUSEWHEEL` 상수를 M-10b 전용으로 분리

**다음 단계**: M-10a hardware 검증 (TC-1, TC-2, TC-7, TC-8, TC-P) -> M-10b (스크롤) 구현.
