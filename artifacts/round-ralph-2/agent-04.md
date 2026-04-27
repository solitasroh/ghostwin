# 라운드2 에이전트04 원인 분석

## 결론 (한 문장, 20 단어 이내)
Session::vt_mutex 와 ConPty::Impl::vt_mutex 가 서로 다른 mutex 라 resize 와 render 가 같은 RenderFrame 을 race 한다.

## 증거 3 가지 (파일:라인 + 확인 내용)

1. **`src/session/session.h:101-103`** — `Session` 이 자체 `std::mutex vt_mutex` (line 103) 를 가지고, 그 아래 `conpty` 와 `state` 가 모두 `[..., vt_mutex]` 라는 ownership comment (line 101-102) 로 보호된다고 명시. 한편 **`src/conpty/conpty_session.cpp:143`** 의 `struct Impl` 은 **별개의** `std::mutex vt_mutex;` 를 선언하고, `src/conpty/conpty_session.cpp:459` 의 `std::mutex& ConPtySession::vt_mutex() { return impl_->vt_mutex; }` 는 이 Impl 쪽 mutex 를 반환. 즉 `Session::vt_mutex` 와 `session->conpty->vt_mutex()` 는 **서로 다른 두 객체**. CLAUDE.md 의 "기술 부채 — vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)" 가 이 이중 mutex 의 존재를 별도로 인정하고 있음.

2. **`src/session/session_manager.cpp:369-376`** — `resize_session()` 이 `std::lock_guard lock(sess->vt_mutex);` (line 373) 로 **Session 쪽** mutex 를 잡고 `sess->conpty->resize(cols, rows);` (line 374, 내부에서 ConPty 쪽 mutex 를 잠깐 잡고 해제), 이어서 `sess->state->resize(cols, rows);` (line 375) 를 호출. 즉 `state->resize` → `RenderFrame::reshape` (`src/renderer/render_state.cpp:266-267` 의 `_api.reshape(...)` / `_p.reshape(...)`) 이 실행되는 구간에서 Session 쪽 mutex 만 holding 하고 **ConPty 쪽 mutex 는 풀려 있음**.

3. **`src/engine-api/ghostwin_engine.cpp:142-147`** — render 경로가 `// Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex). ... render must use the SAME mutex for visibility` 주석 + `bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);` 로 **ConPty 쪽** mutex 만 잡는다. `start_paint` 은 `src/renderer/render_state.cpp:151-184` 에서 `for_each_row` 콜백 안에 `_api.set_row_dirty(row_idx); auto dst = _api.row(row_idx); std::memcpy(dst.data(), cells.data(), ...)` 로 `_api` 의 cell_buffer 를 자유롭게 쓴다. 결과적으로 (a) resize 스레드의 `_api.reshape()` 내부 `cell_buffer = std::move(new_buffer)` (line 120) 와 (b) render 스레드의 `_api.row(r)` + `std::memcpy` 가 **서로 다른 mutex 하에** 완전 interleave 가능. Alt+V split 시 `gw_surface_resize` (ghostwin_engine.cpp:601) → `resize_session` 경로가 왼쪽 pane session 에 대해서도 호출되면, 그 session 의 `TerminalRenderState::_api` buffer 가 render 스레드 memcpy 와 동시에 move 되거나 재할당되어 왼쪽 pane 의 기존 prompt/output cell 이 유실된다.

## 확신도 (0~100)

55

근거: dual-mutex 자체는 코드를 직접 읽고 100% 확인됨 (기술 부채로도 등록됨). 그러나 "이 race 가 100% 재현율의 blank 증상을 실제로 유발한다" 는 인과 관계는 race timing 에 의존하므로 단독 코드 리딩으로는 **100% 확정 불가**. CLAUDE.md `split-content-loss-v2` row 8 및 `tests/render_state_test.cpp` 의 `test_resize_shrink_then_grow_preserves_content` 가 이미 단일-thread shrink-then-grow 시나리오는 Option A reshape 로 PASS 함을 명시하므로, multi-thread race 가 남은 주요 후보라는 점이 확신도를 중간 이상으로 올리지만, ghostty VT 측 resize 동작 (대안 가설 참조) 을 배제할 수 없어 55 로 제한.

## 대안 가설 1

**VT 덮어쓰기 가설** — `src/renderer/render_state.cpp:161-184` 의 `for_each_row` 콜백이 `if (dirty)` 분기에서 `std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData));` 로 `_api.row(row_idx)` 를 VT 의 cell 로 덮어쓴다. Option A reshape 이 `_api` 의 기존 cell 을 **cap 공간에 보존**해도, 직후 `start_paint` 에서 ghostty VT 가 해당 row 를 dirty 로 보고하면 VT 가 제공하는 (대부분 blank 인) cell 로 preserved content 가 **즉시 덮어씌워진다**. `src/vt-core/vt_core.cpp:99-100` 의 `cells_buf.resize(impl_->cols);` 가 VT 의 현재 cols 를 기준으로 고정이고, `render_state.cpp:272-273` 주석 ("bare terminal resize does NOT mark every row dirty") 은 저자의 가정에 불과하며 ghostty 서브모듈 내부 `ghostty_terminal_resize` 의 dirty 마킹 동작은 이 저장소에서 직접 검증 불가 (**추측**). 이 가설이 맞다면 race 와 무관하게 단일 스레드만으로도 증상이 재현된다 — 하지만 현재 HEAD 의 `test_resize_shrink_then_grow_preserves_content` 가 PASS 한다면 (단일 VtCore + 단일 state) 이 경로가 단독으로는 부족할 수 있음.

## 약점 1

본 분석은 증상의 **실행 타이밍 로그/diag** 를 확보하지 못했다. `GHOSTWIN_RESIZE_DIAG=1` 로 켤 수 있는 resize-diag (`render_state.cpp:186-202`, `278-298`) 가 있지만 해당 로그 캡처 없이 이 세션을 진행했으므로, "Alt+V 후 `_api` 가 실제로 언제 어떤 cell 상태였는지" 를 empirical 로 증명하지 못했다. 또한 `ghostty_terminal_resize` 이후 row dirty 가 어떻게 마킹되는지는 submodule 내부여서 이 저장소의 코드만으로 **확정 불가**. race 인과를 확정하려면 TSAN 또는 diag 로그 + reproduce 가 필요.

## 읽은 파일 목록

- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp (280-405)
- C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp (410-463)
- C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp (100-170, 540-606)
- C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp (80-140)
- C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c (80-130)
- C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp (180-240)
