# F-03 원인 분석

## 결론 (한 문장)
`TerminalRenderState::resize` 와 `start_paint` 가 **서로 다른 mutex** 를 잡아서 `reshape` slow path 의 buffer 재할당과 render 스레드의 cell memcpy 가 동시에 실행되는 data race 다.

## Empirical 증거 3 가지

1. **resize 와 render 가 서로 다른 mutex 를 잡는다 (코드 직독)**
   - `src/session/session_manager.cpp:373` `resize_session` 은 `std::lock_guard lock(sess->vt_mutex)` — **Session::vt_mutex**
   - `src/engine-api/ghostwin_engine.cpp:146` render_surface 는 `state.start_paint(session->conpty->vt_mutex(), vt)` — **ConPtySession::Impl::vt_mutex**
   - `src/conpty/conpty_session.cpp:143` 에서 ConPty 의 `vt_mutex` 는 `Impl` 구조체 안에 독립 선언 (`std::mutex vt_mutex;`)
   - 사용자가 제공한 fact C 와 일치: "`Session::vt_mutex` 와 `ConPtySession::Impl::vt_mutex` 는 다른 객체"
   - 즉 `resize_session` 이 `sess->vt_mutex` 를 잡고 `state->resize(...)` 로 진입해서 `_api.reshape` 내부에서 `cell_buffer = std::move(new_buffer)` (`render_state.cpp:120`) 를 실행하는 그 순간, render 스레드는 ConPty mutex 만 잡은 채 `start_paint` 안에서 `_api.row(row_idx).data()` 에 `std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData))` (`render_state.cpp:178`) 를 수행 가능 — **lock 순서가 겹치지 않으므로 두 operation 이 병렬 진입 가능**

2. **스트레스 테스트의 V1 vs V2 패턴이 정확히 slow path 분기와 일치**
   - V1 `{40,5}, {1,1}, {200,50}, {20,5}, {1,1}, {300,80}, {40,5}` 는 `{200,50}` 과 `{300,80}` 에서 `new_c > cap_cols || new_r > cap_rows` 가 되어 `render_state.cpp:98-124` slow path 진입 → `std::vector<CellData> new_buffer(...)` 할당 + `cell_buffer = std::move(new_buffer)` 로 storage pointer 가 바뀜
   - V2 `{40,5}, {20,5}, {30,4}, {40,5}, {10,3}, {40,5}` 는 모두 초기 `cap=(40,5)` 안쪽이므로 `new_c <= cap_cols && new_r <= cap_rows` → `render_state.cpp:92-95` fast path (metadata-only, `cols`/`rows_count` 만 씀, backing storage 는 건드리지 않음)
   - **V1 만 전부 fail (220k/220k), V2 는 전부 PASS (14.2M cells 검사)** — fast path 는 storage pointer 가 그대로이므로 race 가 있어도 memcpy 대상이 여전히 valid buffer 이고, slow path 만 pointer/size 가 바뀌어서 race window 가 생긴다. 분기에 딱 맞아 떨어지는 패턴.

3. **Alt+V 시 WPF Grid layout 의 shrink-then-grow 가 slow path 를 강제로 유발**
   - `render_state.h:29-35` 주석과 `render_state.cpp:236-244` 주석 모두 "WPF Grid layout's shrink-then-grow chain during Alt+V split would drop the old buffer on the intermediate ~1x1 resize" 를 명시
   - Alt+V 직후 한쪽 pane 이 `{80,24}` → `{~1,~1}` → `{40,24}` 같은 연쇄 resize 를 겪고, 초기 `cap=(80,24)` 안에 들어가는 shrink 는 fast path 지만, 그 다음 다른 pane 의 실측 크기가 최초 capacity 보다 **커질 때** slow path 가 한 번 이상 반드시 실행됨. 즉 Alt+V 는 V1 시나리오와 동일한 구조 (capacity 확장 이벤트) 를 재현 가능성이 매우 높음
   - 단일 쓰레드 unit test (`test_real_app_resize_flow`, `test_resize_shrink_then_grow_preserves_content`) 가 PASS 하는 것도 mutex mismatch 해석과 일치 — 단일 쓰레드에서는 reshape 가 원자적으로 끝나고 memcpy 와 interleave 할 상대 스레드가 없음

## 확신도
72%

## 대안 가설 1 개
**Grid shrink 단계에서 `cols=0` 또는 `rows_count=0` 으로 logical view 가 zero clamp** 되어 그 이후 VT 가 dirty 로 보낸 cell 들이 `if (row_idx >= _api.rows_count) return;` (`render_state.cpp:161`) 또는 `copy_cols = std::min(cells.size(), dst.size())` = 0 에 의해 전부 drop 되는 시나리오. 이 경우는 mutex race 가 없어도 설명 가능하지만, `fact C` 의 `cguard=0` (torn metadata 없음) 과 V2 의 성공을 같이 설명하기 어렵다 — V2 도 `{10,3}` 같은 shrink 를 거치는데 PASS.

## 내 결론의 약점
- fact B 의 loss 수치 `220,877/220,877` 가 **전체 cell 수와 정확히 동일** 이라는 점 — 순수 race 라면 일부만 corrupt 되는 게 자연스럽고 전체가 0 으로 보이는 것은 race 보다는 "view 가 new buffer 를 가리키고 old 의 내용은 아직 한 번도 propagate 되지 않았다" 같은 **structural** 문제에 더 가까울 수 있음. 즉 race 보다는 "resize 직후 `_p` 가 비어있고 `_api` 로부터 copy 받기 전인 상태" 가 관측되는 것일 수 있음 — dirty mark 후 `start_paint` 가 VT 에서 cell 을 다시 읽어오지만 VT 측도 blank 로 초기화됐다면 `_api` 마저 0 이 됨
- `RenderFrame::reshape` 의 fast path 는 backing storage 를 건드리지 않지만, slow path 와 data race 가 생길 수 있는지는 실제 sampling 없이는 단정 불가 — Alt+V 단일 transition 에서 slow path 가 실제로 호출되는지 로그로 확인이 필요하지만 본 분석에서는 정적 읽기만 수행했으므로 **추측** 의 여지 있음
- `conpty->resize` 가 VT 의 terminal buffer 를 어떻게 재구성하는지 (cell clear 여부) 는 이 분석에서 확인하지 않았음. `conpty->resize` 가 VT 를 blank 로 만들고 PowerShell 이 prompt 재출력 전에 stress-test 가 read 를 하면 loss 가 100% 가 될 수 있음 — 이 경로도 fact B 를 설명 가능하며 mutex race 와 무관한 **third hypothesis**

## 직접 읽은 파일
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (전체 1-301)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (전체 1-120)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (1-100, 340-440)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (120-200)
- `Grep vt_mutex` on `src/` (conpty_session.h/.cpp, session.h, terminal_window.cpp 경로 확인용)
