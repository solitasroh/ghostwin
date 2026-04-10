# F-10 원인 분석

## 결론 (한 문장, 20 단어 이내)

Resize 와 render 가 서로 다른 mutex 를 잡아 `RenderFrame` 재할당 중 race 발생.

## 증거 3

1. **Mutex 불일치 (1차 증거)**
   - `src/session/session_manager.cpp:373` `resize_session` → `std::lock_guard lock(sess->vt_mutex);` (= `Session::vt_mutex`)
   - `src/engine-api/ghostwin_engine.cpp:146` `start_paint(session->conpty->vt_mutex(), vt)` — 주석에 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex)" 명시
   - `session.h:102` 는 `state` 를 `[main+render, vt_mutex]` 로 선언하지만 실제 render 경로는 다른 객체의 mutex 를 잡음 → resize 가 `_api.reshape`/`_p.reshape` 를 실행하는 동안 render 스레드가 **비보호** 상태로 동일 `RenderFrame` 을 접근

2. **V1 vs V2 대비 (2차 증거)**
   - V1 시퀀스 `{40,5},{1,1},{200,50},{20,5},{1,1},{300,80},{40,5}` 는 `{200,50}`/`{300,80}` 단계에서 **capacity 를 넘는 grow** 를 발생시키고, `render_state.cpp:120` `cell_buffer = std::move(new_buffer)` 가 기존 `std::vector` storage 를 해제 → 이전에 얻었던 `data()` pointer 전부 invalidate
   - V2 시퀀스는 `{40,5}` 를 초과하지 않아 `cap_cols <= 40, cap_rows <= 5` fast path (`render_state.cpp:92-96`) 만 타고, storage 재할당이 **단 한 번도** 발생하지 않음 → 5/5 PASS
   - 단일 쓰레드 `test_resize_shrink_then_grow_preserves_content` 가 PASS 하는데 앱에서 100% loss 인 것과 일치 (race 만 유일한 차이)

3. **Race 접촉 지점 (3차 증거)**
   - `render_state.cpp:151-184` `start_paint` 의 `for_each_row` 람다는 `_api.row(row_idx).data()` 에 `std::memcpy` 로 쓰기를 수행
   - `render_state.cpp:217-223` 은 `_api.row → _p.row` 로 또 한 번 memcpy (dirty rows)
   - 이 두 경로가 `ConPty::vt_mutex` 만 잡는 동안, resize 스레드가 `Session::vt_mutex` 를 잡고 `_api.reshape`/`_p.reshape` 에서 `cell_buffer = std::move(new_buffer)` 를 실행하면 **destination pointer 가 방금 freed 된 old buffer** 또는 **half-constructed new buffer** 를 가리킴 → non-zero cells 이 old buffer 와 함께 삭제되어 loss ≈ old logical cells 수만큼 누적

## 확신도 (%)

**85%** — 증거 체인이 directly causal 하고, V1/V2 split 과 단일 쓰레드 test PASS 를 동시에 설명한다. 다만 (a) WPF 가 실제로 resize 와 render 를 다른 쓰레드에서 호출하는지 직접 로깅으로 확인 못 했고 (b) Alt+V split 경로가 `resize_session` 과 `apply_pending_resize` 중 어느 쪽을 타는지 한번 더 검증 필요하여 100% 는 아님.

## 대안 가설 1

**`apply_pending_resize` 미호출로 인한 mutex 우회 시나리오** — `session_manager.cpp:389-395` 는 `resize_pending` 플래그를 소비하는데, Alt+V split 경로가 이 플래그를 세팅만 해 놓고 `apply_pending_resize` 호출 타이밍 전에 **다른 경로** (예: `resize_session` 직접 호출) 로도 `state->resize` 를 트리거하여 동일 session 에 대해 같은 `TerminalRenderState::resize` 가 **짧은 간격으로 2회** 연이어 호출될 가능성. 이 경우 race 없이도 두 번째 reshape 가 첫 번째 직후의 `_p` 를 blank 로 밀어낼 수 있음 (하지만 이 또한 capacity 를 넘지 않는 한 fast path 이므로 발생 가능성은 낮음).

## 내 약점

- `for_each_row` 내부에서 VT 가 실제로 dirty=true 를 몇 번 주는지, 그리고 `api_written_nonzero` 카운터가 증상 발생 시 0 인지 >0 인지 live trace 를 못 봤다 → race 가 아니라 VT 쪽에서 blank cell 만 공급하는 별개 원인일 가능성을 배제하지 못함
- WPF Grid layout 의 shrink-then-grow 실제 dim 시퀀스를 `GHOSTWIN_RESIZE_DIAG=1` 로 찍어 확인하지 않았다 — V1 과 닮았는지 V2 와 닮았는지가 race 가설의 전제인데 empirical 확인이 없다
- `conpty->vt_mutex()` 와 `Session::vt_mutex` 가 정말로 두 개의 독립 객체인지 소스 수준에서 한번 더 교차 검증이 필요 (grep 결과로는 그래 보이지만 생성자에서 reference binding 했을 가능성 잔존)
- `test_resize_shrink_then_grow_preserves_content` PASS 는 "단일 쓰레드에서 reshape 로직 자체는 정확함" 만 증명하며, race 가설은 **multi-thread regression test** 부재 상태에서 구조 추론에 의존

## 읽은 파일

- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (전체 302 lines)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (전체 121 lines)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp:360-420`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp:130-190`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h` (grep 결과 `:101-103` 확인)
