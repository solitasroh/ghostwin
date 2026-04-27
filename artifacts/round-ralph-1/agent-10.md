# 라운드1 에이전트10 원인 분석

## 결론 (한 문장, 20 단어 이내)
이중 mutex (Session::vt_mutex 와 ConPtySession::impl_->vt_mutex) 경합으로 resize 중인 버퍼를 render thread 가 읽어 내용 유실.

## 직접 확인한 증거 3 가지

1. **`src/engine-api/ghostwin_engine.cpp:142-146`** — render thread 의 `render_surface()` 가 `state.start_paint(session->conpty->vt_mutex(), vt)` 를 호출. 주석에 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex). ... (design §4.5 — dual-mutex bug fix)" 라고 명시. 즉 render 는 `ConPtySession::impl_->vt_mutex` 를 lock.

2. **`src/session/session_manager.cpp:369-376`** — `SessionManager::resize_session` 이 `std::lock_guard lock(sess->vt_mutex);` 로 **`Session::vt_mutex`** (다른 mutex 객체, `session.h:103` 에 정의) 를 lock 한 뒤 `sess->state->resize(cols, rows);` 를 호출. 이 경로는 `ConPtySession::impl_->vt_mutex` 를 전혀 건드리지 않음. 따라서 render 와 resize 가 **완전히 독립적으로 interleave** 가능.

3. **`src/renderer/render_state.cpp:231-299` + `tests/render_state_test.cpp:546-667`** — `TerminalRenderState::resize()` 는 `_api.reshape()` / `_p.reshape()` 호출 후 `dirty_rows.set()` 을 실행하는데 이 동안 `cell_buffer = std::move(new_buffer)`, `cols`/`rows_count`/`cap_cols` mutation 이 전부 **lock 없이** 일어남. 한편 render 쪽 `start_paint()` 는 `_api.row(r).data()` 로 원시 포인터를 취해 memcpy 하므로, 동시에 `cell_buffer` 가 move 되면 dangling read. 테스트 `test_dual_mutex_race_reproduces_content_loss` 가 이 경로를 재현하도록 작성되어 있고, `main()` 에서 활성화되어 있음 (line 829).

추가 정황:
- **`src/GhostWin.App/Controls/TerminalHostControl.cs:126-141`** — `OnRenderSizeChanged` 가 WPF layout pass 에서 UI thread 동기 호출 → `PaneResizeRequested` → `PaneLayoutService.OnPaneResized` → `gw_surface_resize` → `resize_session`. Alt+V split 직후 `BuildGrid` 가 Grid 의 Column 을 재구성하며 기존 pane 의 size 를 `(전체)` → `(절반)` 으로 변경시키는데 WPF Grid 는 intermediate ~1x1 measure pass 를 수반하는 것이 4492b5d 주석에 기록되어 있음 (`render_state.h:30-35`). 즉 split 한 번에 최소 2회 이상의 `resize_session` 이 UI thread 에서 호출되고, render thread 는 16ms 주기로 계속 `start_paint` 를 돌리므로 race window 가 항상 열림.
- **`CLAUDE.md` 기술 부채 섹션** — "vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)" 가 미해결 항목으로 명시.

## 확신도 (0 부터 100 사이 숫자)
72

## 두 번째로 가능한 원인 (대안 가설)
`RenderFrame::reshape()` 의 slow path (capacity 초과 시 `cell_buffer = std::move(new_buffer);`) 가 split-content-loss-v2 fix 후에도 여전히 정상 동작하지 않는 single-thread 경로의 regression. 구체적으로는 shrink-then-grow 시 fast path 가 먼저 타서 `cols`/`rows_count` 만 1x1 로 줄어든 상태에서 다음 grow 호출이 다시 fast path 로 들어가지만, `_p` frame 은 start_paint 의 dirty-row memcpy 가 VT 가 dirty 로 보고하지 않으면 propagate 하지 않아서 `_p` 쪽만 stale. 다만 `render_state.cpp:274-276` 이 resize 후 모든 logical row 를 `_api.set_row_dirty(r)` 로 마킹하고 있어서 이 경로는 partial 방어가 되어있음. 단일 스레드 unit test `test_resize_shrink_then_grow_preserves_content` 는 PASS 하도록 되어있으므로 (line 822) 이 가설의 증거 강도는 주 가설보다 낮음.

## 내 결론의 약점 (empirical 반박 가능 지점)
1. **동적 검증 부재** — 나는 코드 path 와 주석, test 파일만 읽었고 실제 실행 중 race 가 매번 hit 되는지 GHOSTWIN_RESIZE_DIAG=1 로그로 확인하지 못했다. 만약 WPF 의 Dispatcher 가 `OnRenderSizeChanged` → `OnPaneResized` 전체 chain 을 render thread Sleep(16) 간격 사이에 전부 완료한다면 실제로는 race window 가 거의 열리지 않고 증상 원인은 다른 곳일 수 있다.
2. **"매번 100% 재현"의 설명** — race 는 일반적으로 확률적이다. 100% 재현은 race 보다는 deterministic bug 의 특성. 다만 split 직후 WPF layout pass 가 최소 2회의 resize 를 일관된 순서로 발생시키고 render thread 가 Sleep(16) 으로 거의 항상 중간에 깨어난다면 practical 하게 100% 로 보일 수 있다. 추측.
3. **render_state_test.cpp:819-822** 에 `test_resize_shrink_then_grow_preserves_content` 가 "Re-enabled by split-content-loss-v2 cycle" 주석과 함께 활성화되어 있는데, 만약 이 테스트가 실제 CI 에서 PASS 하고 있다면 split-content-loss-v2 fix 자체는 올바르게 들어간 것이고, 남은 원인이 dual-mutex race 일 가능성이 올라간다. 반대로 이 테스트가 FAIL 중이라면 주 가설이 아닌 alternative 가설이 맞을 수 있다. 나는 실제 테스트 실행 결과를 확인하지 못했다.
4. **`memory/MEMORY.md` 의 `project_split_content_loss_v2_pending.md` 엔트리** 가 "HIGH regression pending" + "Unit test `test_resize_shrink_then_grow_preserves_content` empirical FAIL 확정 후 `main()` 호출 주석 처리 → evidence commit `6141005`" 라고 기록되어 있다. 하지만 내가 직접 본 `render_state_test.cpp:822` 는 해당 TEST 가 활성화되어 있다. 이것은 메모리 노트가 오래됐거나, 그 이후 fix 가 들어갔음을 암시한다. 확실하지 않다.

## 내가 직접 읽은 파일 목록
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` : 1-121
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` : 1-302
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs` : 1-335
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs` : 1-246
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs` : 1-94
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` : 100-206, 580-606
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` : 290-400
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h` : 1-140
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` : 430-464 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs` : 120-150 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` : 199-330, 471-667, 800-839
