# 라운드1 에이전트05 원인 분석

## 결론 (한 문장, 20 단어 이내)
Session::vt_mutex 와 ConPty::Impl::vt_mutex 가 분리된 이중 mutex 구조 때문에 resize_session 과 start_paint 가 같은 `_api` 프레임을 비동기로 동시 접근하는 레이스입니다.

## 직접 확인한 증거 3 가지

1. **`src/engine-api/ghostwin_engine.cpp:146` vs `src/session/session_manager.cpp:373-376`** — 렌더 스레드가 `start_paint` 에 넘기는 mutex 는 `session->conpty->vt_mutex()` (= `ConPtySession::Impl::vt_mutex`) 인 반면, `SessionManager::resize_session` 은 **`sess->vt_mutex` (= `Session` 멤버 mutex)** 만 잡고 `sess->state->resize(cols, rows)` 를 호출합니다. `ghostwin_engine.cpp:146` `bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);` 와 `session_manager.cpp:373-375` `std::lock_guard lock(sess->vt_mutex); sess->conpty->resize(...); sess->state->resize(cols, rows);` 이 두 경로는 **같은 `TerminalRenderState` 인스턴스**를 건드리면서도 **서로 다른 락**을 잡습니다. CLAUDE.md 의 "TODO — 기술 부채" 에도 "vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)" 가 명시되어 있습니다.

2. **`tests/render_state_test.cpp:546-666` `test_dual_mutex_race_reproduces_content_loss`** — 두 개의 별도 mutex (`mtx_CONPTY`, `mtx_SESSION`) 를 "real app topology 를 mirror" 라고 주석으로 명시하고, 첫 paint 로 `_api` 에 `"RaceTest"` 를 채운 뒤 resize thread 와 paint thread 를 5 초간 병렬로 돌리면서 row 0 의 첫 8 cell 이 `"RaceTest"` 로 일치하지 않으면 `content_loss_count` 를 증가시킵니다. line 579-587 주석은 "Resize thread: swing shapes entirely within the initial capacity (40x5) so all reshape calls hit the metadata-only fast path. ... reshape mutates cols / rows_count without any lock that start_paint / frame() observes" 라고 명시적으로 레이스가 원인임을 기록합니다. 테스트는 `cl == 0 && cg == 0` 일 때만 PASS 입니다. 이 테스트의 존재 자체가 "이중 mutex → content loss" empirical 증명입니다.

3. **`src/renderer/render_state.cpp:86-125` (`RenderFrame::reshape`) 와 line 266-267 (`TerminalRenderState::resize`)** — `reshape` 는 fast path (`new_c <= cap_cols && new_r <= cap_rows`) 에서 `cols = new_c; rows_count = new_r;` 를 **락 없이** 단순 대입합니다. Slow path 에서는 `cell_buffer = std::move(new_buffer); cap_cols = new_cap_c; cap_rows = new_cap_r; cols = new_c; rows_count = new_r;` 네 필드를 연속 대입합니다. 같은 시점에 `start_paint` (line 151-184) 는 `_api.row(row_idx)` 를 호출하는데 `row()` 는 `cell_buffer.data() + r * cap_cols` 로 offset 을 계산합니다 (`render_state.h:50`). Resize thread 가 `cap_cols` 를 새 값으로 쓰는 순간, render thread 는 **찢어진 `cap_cols` 값과 기존 `cell_buffer` 포인터**로 wrong offset 을 계산하거나, 이미 move 로 소멸된 구 `cell_buffer` 메모리를 참조할 수 있습니다. 또한 `resize` 가 line 274-276 에서 모든 logical row 를 dirty 로 mark 하는 동안 `start_paint` line 162-183 `if (dirty) memcpy(dst.data(), cells.data(), ...)` 가 VT 가 돌려준 cell (resize 와중이라 cp_count=0 인 blank cell 을 돌려줄 가능성) 로 `_api` 를 덮어써서 preserved content 가 실제로 blank 로 교체되는 시나리오도 발생 가능합니다.

## 확신도 (0 부터 100 사이 숫자)

78

- 이중 mutex 구조 확인은 100% 확실 (소스 직접 확인).
- 이 구조가 race 로 content loss 를 유발한다는 것은 tests/render_state_test.cpp:546 의 명시적 repro test 가 이미 존재한다는 점에서 empirical 뒷받침이 강합니다.
- 다만 "Alt+V split 에서 사용자가 본 증상이 **정확히** 이 race 로만 재현되는지" 는 경쟁 가설 (VT 내부 shrink-through-tiny-dims content loss, WPF Grid 중간 크기 전파 등) 과의 상대 기여도를 확정하기 위해 실제 실행 중 stack trace / diag log 를 봐야 empirical 하게 확정 가능. 그래서 100 이 아닌 78.

## 두 번째로 가능한 원인 (대안 가설)

**ghostty VT 내부의 shrink-through-tiny-dims content loss + dirty row 덮어쓰기 연쇄**

- `TerminalHostControl.OnRenderSizeChanged` (`src/GhostWin.App/Controls/TerminalHostControl.cs:126-141`) 이 WPF Grid layout pass 마다 호출되고, Alt+V split 직후 Grid 가 재구성되면서 중간 크기 (예: `width_px = 1~5`) 를 transient 하게 전파할 수 있습니다.
- `gw_surface_resize` (`src/engine-api/ghostwin_engine.cpp:581-606`) 는 `width_px / cell_width` 로 cols 를 계산하고 `if (cols < 1) cols = 1` 로 하한을 둡니다. 그러면 `resize_session(session_id, 1, 1)` 이 호출될 수 있고, `ConPtySession::resize` → `vt_core->resize(1, 1)` → `vt_bridge_resize` → `ghostty_terminal_resize(1, 1)` 로 전파됩니다.
- ghostty 내부 screen buffer 가 1x1 으로 shrink 되는 순간 기존 PowerShell output 의 대부분 cell 은 truncate 됩니다 (추측 — ghostty 내부 `resize` 동작의 정확한 semantics 는 submodule 코드를 열어보지 않아 **확실하지 않음**). 그 후 Grid layout 이 final 크기로 grow back 해도 VT 의 data 는 이미 loss. 이 경로는 C++ `RenderFrame::reshape` 의 Option A capacity backing 으로는 보호되지 않습니다.
- 추가로 `render_state.cpp:274-276` 이 resize 후 모든 logical row 를 dirty 로 mark 하면, 다음 `start_paint` 에서 VT 가 "dirty=true, cp_count=0" 인 blank cell 을 돌려주는 순간 `_api.row(r)` 가 blank 로 덮어써지고, 그 결과 `_p.row(r)` 도 blank 가 되어 최종 화면에 prompt 가 사라지는 인과가 성립합니다. `render_state.cpp:167-184` 주석은 "Round 2 에서 defensive merge 를 제거하고 straight memcpy 로 복귀" 라고 기록하며 그 이유로 "정상 cls/clear/ESC[K/scroll/vim/tmux 경로도 cp_count=0 cell 을 사용하기 때문에 merge 가 정상 경로를 깨뜨린다" 라고 합니다. 즉 이 straight memcpy 는 정상 경로를 살리는 대신 "VT 가 dirty=true + blank 를 돌려주는 resize 직후 시점" 에서 preserved content 를 지우는 부작용을 가집니다.

## 내 결론의 약점 (empirical 반박 가능 지점)

1. **Alt+V split 에서 "매번 100% 재현" 이라는 사용자 보고와 race 가 잘 맞지 않을 수 있음**. Race condition 은 일반적으로 비결정적입니다. 100% 재현이라는 빈도는 race 가 아니라 **deterministic 한 VT-level content loss** (대안 가설) 를 더 가리킬 수 있습니다. 따라서 우선 원인과 부원인이 반대일 가능성이 있습니다.

2. **`test_dual_mutex_race_reproduces_content_loss` 의 현재 상태를 확인하지 못함**. 이 테스트가 현재 PASS 인지 FAIL 인지 CI / 최근 실행 로그를 직접 보지 않았습니다. PASS 상태라면 "race 가 이미 다른 방식으로 막혔다" 는 뜻이고 제 1차 가설이 약해집니다. 테스트가 FAIL (= loss > 0) 인 상태로 존재한다면 1차 가설이 강해집니다.

3. **reshape fast path 의 단순 대입은 CPU 아키텍처에 따라 64-bit alignment 가 맞으면 tear-free 일 수 있음**. x86-64 에서 aligned uint16_t 단일 store 는 tear 되지 않으므로, race 가 있더라도 `cols` 단일 read 는 old 나 new 둘 중 하나를 보고 중간값은 안 나옵니다. 이 경우 race 로 인한 content loss 는 "`cell_buffer` 가 move 로 소멸된 구 블록을 참조" 경로 (slow path) 로만 발생하고, Alt+V 의 초기 split 은 보통 shrink (fast path) 만 겪으므로 slow path race 가 안 터질 수 있습니다. 이 경우 제 1차 가설이 약해집니다.

4. **사용자가 본 "사라진다" 가 실제로는 "렌더 안 된다"** 일 가능성. 예를 들어 left pane 의 surface 가 swapchain resize 후 ClearRenderTargetView 는 되었는데 첫 `start_paint` 가 skip 되어 frame 이 empty 로 present 되는 경로도 가능. 이 경우 `_api` 는 멀쩡한데 `_p` → GPU 경로에서만 실패합니다. 저는 `dx11_renderer.cpp` / `surface_manager.cpp` 의 deferred ResizeBuffers 경로를 읽지 않았으므로 이 가설의 empirical 반박을 하지 못했습니다.

## 내가 직접 읽은 파일 목록

- `C:/Users/Solit/Rootech/works/ghostwin/CLAUDE.md` : 전체 (프로젝트 상태, Follow-up Cycles row 8 split-content-loss-v2, TODO — 기술 부채 vt_mutex 통합)
- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.h` : 1-120 (RenderFrame + reshape API + TerminalRenderState)
- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` : 1-301 (reshape, start_paint, resize 전체)
- `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_core.cpp` : 1-207 (VtCore::resize, for_each_row, Impl::cols 상호작용)
- `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` : 120-180, 440-610 (gw_surface_resize, gw_session_resize, start_paint 호출, gw_surface_create)
- `C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` : 1-500 (resize_session, resize_all, apply_pending_resize, create_session)
- `C:/Users/Solit/Rootech/works/ghostwin/src/conpty/conpty_session.cpp` : 420-459 (ConPtySession::resize, vt_mutex 접근)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/PaneContainerControl.cs` : 1-334 (BuildGrid, BuildElement host migration, OnPaneResized dispatch)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/TerminalHostControl.cs` : 1-210 (OnRenderSizeChanged, BuildWindowCore, HostReady 이벤트)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/PaneLayoutService.cs` : 1-245 (SplitFocused, OnHostReady, OnPaneResized)
- `C:/Users/Solit/Rootech/works/ghostwin/tests/render_state_test.cpp` : 540-680 (test_dual_mutex_race_reproduces_content_loss, 주변 clear/erase regression guards)
