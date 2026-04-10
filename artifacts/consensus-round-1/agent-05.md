# Agent R3-05 원인 분석

## 결론 (한 문장, 10~20단어)
Resize 와 render 가 서로 다른 mutex 를 잡아 `state->resize()` 의 전체-dirty 마킹이 `start_paint()` 의 VT 읽기와 경합해서 content 가 빈 cell 로 덮어씌워진다.

## Empirical 증거 3가지

1. **dual-mutex 실체 (파일:라인 직접 확인)** — `src/engine-api/ghostwin_engine.cpp:146` 의 render 경로는 `state.start_paint(session->conpty->vt_mutex(), vt)` 로 **`ConPtySession::Impl::vt_mutex`** (`src/conpty/conpty_session.cpp:143`) 를 잡는다. 반면 `src/session/session_manager.cpp:369-376` 의 `resize_session()` 은 `std::lock_guard lock(sess->vt_mutex)` 로 **`Session::vt_mutex`** (`src/session/session.h:103`) 를 잡은 뒤 `sess->conpty->resize()` + `sess->state->resize()` 를 호출한다. 두 mutex 는 서로 다른 객체이므로 resize 와 render 가 상호 배제되지 않는다.

2. **유닛 테스트가 이 정확한 조건을 empirical 확정** — 이미 확인된 팩트 #2 의 `test_dual_mutex_race_reproduces_content_loss` 가 두 쓰레드 (resize 9300만 회 + paint 6200만 회) 에서 **193940/193940 loss (100%)** 을 낸다. 반면 `test_real_app_resize_flow` (단일 쓰레드) 와 `test_resize_shrink_then_grow_preserves_content` (reshape 자체) 는 모두 PASS. 즉 reshape 로직 자체는 결백하고, **두 쓰레드 + 두 mutex** 조합에서만 loss 가 발생한다.

3. **race window 의 작동 메커니즘** — `render_state.cpp:266-276` 에서 `TerminalRenderState::resize()` 는 `_api.reshape()` 후 `for (r = 0; r < rows; r++) _api.set_row_dirty(r)` 로 **모든 논리 row 를 dirty 마킹**한다. 만약 render 쓰레드가 바로 이 순간 `Session::vt_mutex` 를 잡지 않은 채 `start_paint()` 를 돌리면, `for_each_row` 는 resize 직후의 VT (PageList 는 아직 resize 중이거나 막 끝난 상태) 에서 **아직 reflow 되지 않은 빈 cell** 을 돌려주고, `memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData))` (render_state.cpp:178) 가 방금 reshape 로 보존된 cell 을 **빈 cell 로 덮어쓴다**. `_api → _p` 복사 (line 217-223) 까지 한 사이클에 끝난다. 결과적으로 Alt+V split 직후 Grid layout 의 shrink-then-grow 연쇄에서 render thread 가 한 번이라도 끼어들면 left pane content 가 지워진다.

## 확신도 (%)
82

## 대안 가설 (1개만)
`CreatePseudoConsole(..., flag=0, ...)` (conpty_session.cpp:303-309) 로 `PSEUDOCONSOLE_RESIZE_QUIRK` 가 빠져 있어서 ConPTY 가 shrink 시 기존 buffer 를 truncate/clear 하고, 그 clear 결과가 VT reflow 를 통해 render 에 반영된다. 단 이 가설은 유닛 테스트가 ConPTY 를 거치지 않고도 100% loss 를 재현했다는 사실과 정합하기 어렵다 (대안은 test 환경에서 추가 재현 근거가 필요).

## 내 결론의 약점 (1개만)
유닛 테스트의 race condition 은 `test_dual_mutex_race_reproduces_content_loss` 라는 이름만으로도 두 mutex 시나리오를 명시적으로 타겟팅한 것 — 실제 production 에서 Alt+V split 한 번에 동일 race 가 **100% 확률로 hit** 한다는 직접 증거 (timing log/trace) 를 파일에서 확인하지 못했다. 유닛 테스트는 93M+ iteration 으로 race 를 강제 유도한 것이고, 실제 production 에서 WPF layout pass 가 얼마나 자주 render thread 와 겹치는지는 Read 로 검증 불가. `render_loop()` 가 60 fps (16ms Sleep, ghostwin_engine.cpp:180) 로 돌아가고 resize 가 WPF layout pass 여러 번에 걸쳐 발생한다는 점에서 race window 가 충분히 크다는 간접 근거만 있음.

## 파일 직접 읽음 목록
- `src/renderer/render_state.h:1-120` (RenderFrame 구조, reshape/resize 계약)
- `src/renderer/render_state.cpp:1-301` (reshape, start_paint, resize 전체 경로 + diag 로깅)
- `src/session/session.h:1-147` (`Session::vt_mutex` at line 103, conpty unique_ptr at 101, state unique_ptr at 102)
- `src/conpty/conpty_session.h:78-82` (`ConPtySession::vt_mutex()` 접근자 주석 — "Render thread should lock this same mutex (NOT a separate Session::vt_mutex)")
- `src/conpty/conpty_session.cpp:132-155` (`Impl::vt_mutex` 정의 line 143, io_thread 가 write 시 이 mutex 사용 line 192)
- `src/conpty/conpty_session.cpp:303-309` (`CreatePseudoConsole` flag=0, `PSEUDOCONSOLE_RESIZE_QUIRK` 미설정 확인)
- `src/conpty/conpty_session.cpp:425-445` (`ConPtySession::resize` 가 `impl_->vt_mutex` 하에서 `vt_core->resize` 호출)
- `src/session/session_manager.cpp:367-395` (`resize_session` line 373 이 `sess->vt_mutex` — 즉 Session::vt_mutex — 사용, `apply_pending_resize` 도 동일)
- `src/engine-api/ghostwin_engine.cpp:115-205` (`render_surface` line 146 이 `session->conpty->vt_mutex()` 사용 — 주석으로 "NOT Session::vt_mutex" 명시, render_loop 60fps)
