# F-04 원인 분석

## 결론 (한 문장)
resize는 Session::vt_mutex로 잠그고 render는 ConPty vt_mutex로 잠가서 reshape 중 buffer가 교체되는 순간 render 스레드가 그대로 옛 포인터를 읽는다.

## 증거 3 가지

1. **`session_manager.cpp:369-376` — resize_session 은 `sess->vt_mutex` 로만 잠근다.**
   ```cpp
   std::lock_guard lock(sess->vt_mutex);
   sess->conpty->resize(cols, rows);
   sess->state->resize(cols, rows);  // reshape 실행 → cell_buffer = std::move(new_buffer)
   ```
   여기서 잠기는 것은 `Session::vt_mutex` 한 개뿐이다. `state->resize` 내부에서 `_api.reshape`/`_p.reshape` 가 `std::vector<CellData> new_buffer(...)` 를 할당하고 `cell_buffer = std::move(new_buffer)` 로 교체한다 (render_state.cpp:104, 120). 교체 직전/직후에 `RenderFrame::cell_buffer.data()` 가 가리키는 메모리가 완전히 바뀐다.

2. **`ghostwin_engine.cpp:140-147` — render 스레드는 `session->conpty->vt_mutex()` 로 잠근다.**
   ```cpp
   state.force_all_dirty();
   bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);
   ```
   주석에도 명시적으로 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex)" 라고 적혀 있다. 그런데 resize 는 `Session::vt_mutex` 를 쓴다. 두 mutex 는 C 항(문제 진술) 에서 확인된 대로 서로 다른 객체이므로, resize 가 lock 을 잡고 있어도 render 스레드는 아무 제약 없이 `start_paint` → `for_each_row` → `_api.row(r)` 경로로 동시에 진입할 수 있다. `_api.row(r)` 는 `cell_buffer.data() + r * cap_cols` 를 리턴하는데, 바로 그 시점에 `reshape` 가 `cell_buffer = std::move(new_buffer)` 를 실행하면 render 가 확보한 포인터는 **해제된 메모리** 가 된다 (또는 새 zero 버퍼를 가리키게 된다).

3. **V1 vs V2 스트레스 테스트 비대칭이 정확히 이 가설을 설명한다.**
   V1 크기배열 `{40,5} → {1,1} → {200,50} → ...` 은 `{200,50}` 에서 `cap_cols=200`, `cap_rows=50` 으로 커지는 slow path 를 강제로 타고, 그 안에서 `std::vector<CellData> new_buffer(10000, ...)` 할당 + memcpy + move 가 실행된다 (render_state.cpp:101-120). 이 window 가 수십 μs~수 ms 단위로 열린다. V2 는 `{40,5}, {20,5}, {30,4}, {40,5}, {10,3}, {40,5}` 로 모두 초기 `{40,5}` 의 cap 안에 들어가므로 fast path (line 92-96) 만 타고 `cols`/`rows_count` 메타데이터만 바뀐다. Fast path 에는 buffer 교체가 아예 없으므로 경합 창이 존재하지 않는다. V1 = 100% FAIL (22~27만 loss), V2 = 0 loss 는 "slow path 의 buffer 교체 구간에서만 race" 와 정확히 일치한다. C 항의 "단일 스레드 reshape PASS" 도 이 해석과 정합한다 — 경합 상대가 없으면 잘못된 mutex 를 써도 문제가 드러나지 않는다.

## 확신도 (%)

92%

핵심 증거가 세 지점에서 독립적으로 모두 같은 방향을 가리킨다: (a) 코드 경로에 다른 mutex 를 쓴다는 사실, (b) 렌더 코드에 "NOT Session::vt_mutex" 라는 주석이 이미 박혀 있다는 점 (과거에 같은 문제를 한 번 수정한 흔적), (c) V1/V2 비대칭이 slow path 만 FAIL 이라는 사실. 나머지 8% 는 stress test 가 실제 production 호출 경로와 100% 동일한지 직접 실행으로 확인하지 못했기 때문.

## 대안 가설

- **H2. `apply_pending_resize` (session_manager.cpp:389-395) 경로에서도 동일한 mutex 버그.** 같은 파일 바로 아래에 있는 경로가 역시 `sess->vt_mutex` 를 잡고 `state->resize` 를 호출한다. 따라서 이것은 한 곳의 오타가 아니라 "SessionManager 전역이 Session::vt_mutex 를 정규 lock 으로 알고 있는데 render 만 ConPty mutex 를 쓴다" 라는 더 구조적인 쪽에 가깝다. H1 의 보강이지 경쟁 가설은 아님.
- **H3. `start_paint` 가 잡은 lock 이 풀린 뒤 `_p.row()` 가 drained 될 때 cap 이 중간에 바뀜.** 추측. `_p` 는 `_api` 로부터 dirty 행만 copy 되는데 (render_state.cpp:217-223), copy 자체는 lock 안이라 안전하다. 단, `_p` 에 대한 외부 read (`frame()` 호출자) 가 lock 바깥이라면 여기에도 경합이 있을 수 있다. 본 증상 (100% 재현) 의 주범은 아님.
- **H4. `reshape` 자체의 copy_cols/copy_rows 로직 버그.** 단일 스레드 unit test `test_resize_shrink_then_grow_preserves_content` 가 FAIL 했다는 점을 보면 아주 배제되지는 않는다. 다만 V2 에서 fast path 만 PASS 라는 사실은 "reshape 로직 자체 버그" 보다 "slow path 경합" 을 더 강하게 지시한다. 두 원인이 겹쳐 있을 가능성도 있음 (추측).
- **H5. VT 쪽 resize (`conpty->resize`) 가 terminal buffer 를 clear 해서 그 결과가 다음 `for_each_row` 에서 zero 로 들어온다.** 가능은 하지만, 그렇다면 V2 크기 변경 (논리 view 도 변하는) 에서도 같은 증상이 나와야 한다. V2 가 0 loss 이므로 기각.

## 내 약점

- ConPtySession 내부 구현 (`conpty->vt_mutex()` 가 정확히 어떤 객체인지, `conpty->resize` 가 이 mutex 를 스스로 잡는지) 을 직접 열어보지 않았다. 만약 `conpty->resize` 가 내부적으로 ConPty vt_mutex 를 잡는다면 render 와의 경합은 그 구간에서는 차단되지만, `state->resize` 호출 자체는 여전히 ConPty mutex 바깥이다 — 즉 핵심 결론은 변하지 않음. 그래도 직접 확인하지 못한 가정이 남아 있음.
- V1 stress test 의 실제 코드 (`main()` 주석 처리 위치, 멀티스레드 구조) 를 읽지 않고 사용자가 준 "V1 100% FAIL" 을 그대로 증거로 썼다. 만약 stress test 가 단일 스레드라면 3번 증거의 설명력이 떨어진다. C 항에 "단일 스레드 PASS" 라고 명시돼 있으므로 stress 는 멀티스레드라고 해석했으나, 이것은 해석이지 직접 확인은 아니다.
- `ConPty mutex` 와 `Session mutex` 가 "원래 하나로 통합해야 하는데 방치된 기술부채" 로 CLAUDE.md 에 명시돼 있는 것 (`vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)`) 을 나중에 알았다. 사전 정보 없이 독립 분석했어야 하나, 결론이 우연히 그 기술부채 TODO 와 일치한다.

## 읽은 파일 목록
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp (1-302)
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h (1-121)
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp (350-430)
- C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp (140-180)
