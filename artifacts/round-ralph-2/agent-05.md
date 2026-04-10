# 라운드2 에이전트05 원인 분석

## 결론 (한 문장, 20 단어 이내)
Session::vt_mutex 와 ConPtySession::Impl::vt_mutex 가 서로 다른 mutex 라 render thread 와 resize thread 가 TerminalRenderState 를 동시에 건드려 content 가 사라진다.

## 증거 3 가지 (파일:라인 + 확인 내용)

1. **render thread 는 ConPtySession 내부 mutex 를 사용**
   - `src/engine-api/ghostwin_engine.cpp:146` — `bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);`
   - `src/conpty/conpty_session.cpp:143` — `std::mutex vt_mutex;` (ConPtySession::Impl 멤버)
   - `src/conpty/conpty_session.cpp:459` — `std::mutex& ConPtySession::vt_mutex() { return impl_->vt_mutex; }`
   - 즉 render thread 가 들고 있는 lock 은 ConPty 내부 Impl 의 mutex.

2. **resize 경로는 Session 구조체의 별개 mutex 를 사용 + 같은 lock 으로 state->resize() 호출**
   - `src/session/session.h:102-103` —
     `std::unique_ptr<TerminalRenderState> state;   // [main+render, vt_mutex]`
     `std::mutex vt_mutex;                          // ADR-006 extension`
     즉 Session 에 **별도의** `vt_mutex` 가 존재.
   - `src/session/session_manager.cpp:373-375` —
     `std::lock_guard lock(sess->vt_mutex);`
     `sess->conpty->resize(cols, rows);`
     `sess->state->resize(cols, rows);`
   - `resize_all` 도 동일 (`session_manager.cpp:305-307`).
   - `sess->vt_mutex` (Session) ≠ `impl_->vt_mutex` (ConPty Impl). 두 mutex 사이에 상호 배제가 전혀 없으므로 render thread 의 `state.start_paint` 와 resize thread 의 `state->resize` 는 동시에 `_api`/`_p` (reshape, dirty_rows, cell_buffer) 를 조작할 수 있다.

3. **split-content-loss-v2 dual-mutex race test 가 이 토폴로지를 그대로 empirical 재현**
   - `tests/render_state_test.cpp:525-545` 주석 —
     "Render thread: start_paint(mtx_CONPTY, vt) (ghostwin_engine.cpp:146 → session->conpty->vt_mutex())"
     "Resize thread: mtx_SESSION lock + state->resize(c, r) (session_manager.cpp:373 → sess->vt_mutex, a *different* mutex)"
     "Because the two mutexes are distinct objects, render and resize can fully interleave."
   - `tests/render_state_test.cpp:546-666` `test_dual_mutex_race_reproduces_content_loss` 는 shapes 전체가 초기 capacity (40x5) 이내라 reshape 는 metadata-only fast path 만 밟는데도 content loss/crash 가 발생하면 FAIL. 즉 "reshape 알고리즘" 이 아니라 "lock 부재" 가 원인임을 구조적으로 확정하는 테스트.
   - `CLAUDE.md` 기술 부채 목록 하단: `- [ ] vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)` — 프로젝트 자체가 이중 mutex 부채를 명시.
   - `TerminalRenderState::resize` 주석 `src/renderer/render_state.cpp:246`: "Caller contract (unchanged): must hold vt_mutex." 어느 vt_mutex 인지 명시되지 않은 상태에서 실제 호출자가 서로 다른 두 mutex 를 사용하고 있어 contract 자체가 깨져 있다.

## 확신도 (0~100)
72

이중 mutex 구조 자체는 파일로 직접 확인했고 (증거 1~2), test 가 race 를 empirical 재현한다고 주장한다 (증거 3). 다만 Alt+V 시나리오에서 "왼쪽 pane 만" 사라지는 증상과 race window 가 정확히 일치하는지는 이 세션에서 test 를 실행해 보지 못했고, 같은 증상을 설명할 수 있는 다른 경로 (아래 대안 가설) 를 완전히 배제하지 못했으므로 100 이 아닌 72.

## 대안 가설 1
`start_paint` 안의 straight memcpy 가 원인 — `src/renderer/render_state.cpp:166-184` 에서 VT 가 dirty=true 로 돌려준 row 는 조건 없이 `_api.row(r)` 로 memcpy 된다 (defensive merge 는 Round 2 에서 제거됨, L167-177 주석). Alt+V 직후 `conpty->resize` → `vt_bridge_resize` 가 terminal buffer 를 shrink/clear 하고, 다음 render iteration 에서 VT 가 "모든 row 가 dirty 이고 cells=(cp_count=0 빈 cells)" 를 돌려주면 `_api` 전체가 빈 cells 로 덮어쓰기된다. 이 경우 mutex 가 하나라도 증상은 동일. 주 가설과 양립 가능하며 실제로는 두 가지가 합쳐진 것일 수 있음.

## 약점 1
이중 mutex 가 존재한다는 것과 "왼쪽 pane 이 사라지는 증상의 원인이 이중 mutex 다" 는 별개 명제다. Split 시 Grid layout 이 실제로 resize 를 몇 번, 어느 thread 에서, 어느 시점에 호출하는지 runtime 증거 (로그/trace) 를 이 세션에서 확보하지 못했다. `OnPaneResized` 는 WPF Dispatcher thread (UI) 에서 호출되고 그 안에서 native `gw_surface_resize` → `session_mgr->resize_session` 이 동일 스택으로 실행되므로, resize 는 UI thread, render 는 engine render_loop thread (conpty_session.cpp 와 별도) 에 있다. 두 thread 의 실제 interleaving 빈도가 "매번 100% 재현" 을 뒷받침할 만큼 충분한지는 경험적으로 확인해야 하며, 만약 interleaving 이 드물다면 주 가설이 아니라 대안 가설 (VT 가 빈 cells 를 돌려주는 경로) 이 실제 원인일 가능성이 커진다.

## 읽은 파일 목록
- `C:\Users\Solit\Rootech\works\ghostwin\CLAUDE.md`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\surface_manager.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (부분)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (부분)
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` (부분)
- `C:\Users\Solit\Rootech\works\ghostwin\docs\02-design\features\split-content-loss-v2.design.md` (부분)
- `C:\Users\Solit\Rootech\works\ghostwin\docs\adr\006-vt-mutex-thread-safety.md` (부분)
