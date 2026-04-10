# F-06 원인 분석

## 결론 (한 문장, 20 단어 이내)

Resize 경로 (`Session::vt_mutex`) 와 Render 경로 (`ConPtySession::vt_mutex`) 가 서로 다른 mutex 를 잡아 `reshape()` 와 `start_paint()` 의 memcpy 가 경합한다.

## 증거 3

1. **Resize lock 대상**: `src/session/session_manager.cpp:373` — `resize_session` 은 `std::lock_guard lock(sess->vt_mutex);` 를 잡고 `sess->state->resize(cols, rows);` 를 호출한다. 즉 `TerminalRenderState::resize` → `RenderFrame::reshape` 의 `new_buffer` 할당과 row-by-row memcpy (`render_state.cpp:104-118`) 는 **Session 의** vt_mutex 보호 아래서 진행된다.

2. **Render lock 대상**: `src/engine-api/ghostwin_engine.cpp:142-146` — 주석 명시 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex)". `state.start_paint(session->conpty->vt_mutex(), vt);` 이고 `start_paint` (`render_state.cpp:132-133`) 는 `std::lock_guard lock(vt_mutex);` 로 **ConPty 의** 다른 mutex 를 잡은 뒤 `_api.row(row_idx)` 에 `std::memcpy` (`render_state.cpp:178`) 한다.

3. **경합 시나리오와 관측 일치**: Resize 가 `_api.cell_buffer = std::move(new_buffer)` 를 수행하는 순간, 동시에 렌더 스레드가 이전 `cell_buffer.data()` 포인터로 memcpy 를 진행하거나 또는 reshape 직후 dirty-mark 된 행을 새 buffer 에 쓰는 동안 `for_each_row` 가 VT 의 **resize 직후 blank row** 를 돌려주어 prompt 영역을 `cp_count=0` 으로 덮는다 (`render_state.cpp:167-179` 의 straight memcpy, defensive merge 제거됨). 단일 쓰레드 unit test (`test_resize_shrink_then_grow_preserves_content`, `test_real_app_resize_flow`) 는 PASS 하지만 V1 스트레스 (`{40,5}→{1,1}→{200,50}...`) 는 4/4 FAIL — **단일 쓰레드 reshape 자체는 정상**이고 실패는 멀티스레드 경합 경로에서만 발생한다는 empirical 패턴에 부합한다. V2 (작은 변동) 가 5/5 PASS 인 것은 큰 capacity 성장 (→ `std::move` + 새 할당) 이 V1 에만 존재하기 때문이다.

## 확신도 (%)

65% — mutex 불일치는 파일 내 주석과 코드로 **확정**이지만, 이 경합이 곧바로 "왼쪽 pane 의 prompt 사라짐" 증상으로 이어진다는 인과는 로그 증적 없이 추론이다. V1/V2 FAIL/PASS 분기가 capacity 성장 유무와 상관한다는 점은 **추측**.

## 대안 가설 1

**VT resize 직후 blank row 재공급 + defensive merge 제거의 상호작용.** `render_state.cpp:167-184` 주석은 "Round 2 합의로 defensive merge 제거, 정상 clear 경로를 깨뜨리므로" 라고 기록되어 있다. 만약 ghostty VT 가 `conpty->resize()` 직후 **모든 dirty row 를 `cp_count=0` blank cell 로 1회 돌려준다면**, `start_paint` 의 straight memcpy 가 `reshape()` 가 preserve 한 prompt content 를 즉시 blank 로 덮어버린다. Mutex 경합이 없어도 재현 가능. V2 가 PASS 인 이유는 작은 변동이라서 reshape 후 dirty mark 개수가 적거나 VT 가 blank row 를 돌려주지 않을 가능성. 이 가설이 맞다면 fix 는 render_state.cpp 가 아니라 `for_each_row` 소비 로직에 있어야 한다.

## 내 약점

- 실제 VT (ghostty `Screen.zig`) 가 resize 직후 **어떤 row 를 어떤 dirty flag 로 돌려주는지** 직접 확인하지 못함. 주석의 "Screen.zig:1449 clearCells → @memset(blankCell())" 은 인용에 해당해서 피했지만, 스스로 검증 없이 대안 가설에 의존함.
- V1 과 V2 의 스트레스 테스트 실제 코드를 읽지 못하고 차이를 capacity 성장 유무로 추정한 것은 **추측**.
- Mutex 경합이 실제 race 를 만드는지, 아니면 false alarm (둘 다 같은 스레드에서만 호출되는지) 를 SessionManager / WPF host 의 호출 스레드 확인 없이 단정.
- 로그 (`GHOSTWIN_RESIZE_DIAG=1`) 를 실행해서 `before/after` `_api_total` 과 `_p_total` 을 확인하면 reshape 직후 데이터 유실인지 start_paint 덮어쓰기인지 분간 가능한데, 이번 분석은 static read 만으로 진행함.

## 읽은 파일

- `src/renderer/render_state.cpp` (전체, 302 lines)
- `src/renderer/render_state.h` (전체, 121 lines)
- `src/session/session_manager.cpp:340-420` (resize_session + apply_pending_resize)
- `src/engine-api/ghostwin_engine.cpp:120-200` (render_surface + render_loop)
