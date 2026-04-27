# F-08 원인 분석

## 결론 (한 문장, 20 단어 이내)

render 스레드의 `ConPtySession::vt_mutex` 와 resize 경로의 `Session::vt_mutex` 가 서로 다른 뮤텍스라 `_api` 재할당이 race 로 덮인다.

## 증거 3

1. **`ghostwin_engine.cpp:146`** (render thread) —
   `state.start_paint(session->conpty->vt_mutex(), vt)` 를 호출. `start_paint` 내부의 `std::lock_guard lock(vt_mutex)` 는 **`ConPtySession::Impl::vt_mutex`** 를 잠근다. 이 시점에 `_api.cell_buffer` 전체를 `memcpy` 로 읽고 `_p` 로 복사한다 (`render_state.cpp:132-228`).

2. **`session_manager.cpp:373` / `391`** (main / resize thread) —
   `resize_session()` 와 `apply_pending_resize()` 는 `std::lock_guard lock(sess->vt_mutex)` 즉 **`Session::vt_mutex`** (ADR-006 extension, `session.h:103`) 를 잠그고 `sess->state->resize(cols, rows)` 를 호출한다. 여기서 `_api.reshape()` → `cell_buffer = std::move(new_buffer)` 로 backing storage 를 통째로 교체한다 (`render_state.cpp:120`).

3. **두 뮤텍스는 동일 객체가 아님** — `session.h:101-103` 에서 `conpty` 는 `ConPtySession` 의 내부 `vt_mutex()` 를 노출하고, `Session::vt_mutex` 는 별도 필드로 선언되어 있음. `grep` 결과 `conpty->vt_mutex` 참조는 `ghostwin_engine.cpp:146` 단 1곳뿐. render 는 A 를 잠그고, resize 는 B 를 잠근다. A 와 B 는 전혀 동기화되지 않는다.

   결과: render 가 `_api.row(r)` 로 `cell_buffer.data() + r*cap_cols` 포인터를 꺼낸 상태에서 resize 쓰레드가 `std::move(new_buffer)` 를 실행하면, render 가 들고 있던 포인터는 해제된 vector 의 내부 storage 를 가리킨다 — read UAF + stale pointer 로 `vt_raw_nonzero > 0` 인데 `api_written_nonzero == 0` 또는 `_p.total_nonzero` 가 0 이 되는 증상이 설명됨. 그리고 `V1` 스트레스처럼 큰 resize (`{200,50}`, `{300,80}`) 가 반드시 slow-path (`new_cap > old_cap`) 를 타서 `std::move` + 새 vector 할당을 유발하므로 22만~27만 loss 가 매번 재현되고, `V2` 는 모든 reshape 가 fast-path (`new_c <= cap_cols && new_r <= cap_rows`) 로 떨어져 backing storage 교체가 0 회 → loss 0 으로 관측된다. 단일 쓰레드 unit test 가 PASS 하는 것도 동일하게 설명된다 (race 없음).

## 확신도 (%)

**92%**

- 파일을 직접 읽어서 두 mutex 가 서로 다른 객체임을 확인
- V1/V2 테스트의 slow-path/fast-path 분기와 PASS/FAIL 패턴이 정확히 일치
- 단일 쓰레드 unit test PASS 와 multi-thread 실패의 갭을 설명
- 8% 불확실성: 실제 production 에서 resize 경로가 `SessionManager::resize_session` 만 쓰는지, `apply_pending_resize` 도 같은 경로인지 — 둘 다 `Session::vt_mutex` 라 결론은 같지만 호출 frequency 는 미확인. 그리고 WPF Grid shrink-then-grow 사이클에서 실제로 `cap_cols/cap_rows` 를 넘겨서 slow-path 에 진입하는지 여부는 앱 dim 로그 없이 추측.

## 대안 가설 1

**WPF Grid layout 의 shrink-then-grow 순서가 `_p` 를 정지 상태로 두고 `_api` 만 갱신되어 `_p` 에 stale zero 가 남는다.** `resize()` 가 `_api.reshape()` + `_p.reshape()` 를 호출하지만 `_api` 만 `set_row_dirty` 로 표시하고 `_p` 는 다음 `start_paint()` 의 dirty-row copy 를 기다린다. 만약 `start_paint()` 가 `vt_mutex` 를 잡지 못하거나, `vt.for_each_row` 가 cp_count=0 cell 을 돌려주면 `_p` 는 영영 0 으로 남을 수 있다. 다만 이 가설은 단일 쓰레드 PASS 를 설명 못 하고 `_api_total` 에서도 loss 가 관측되어야 하므로 증거 강도는 낮음. (추측)

## 내 약점

- `ConPtySession::vt_mutex()` 의 실제 구현 (주소, lifetime) 을 직접 읽지 않았다. "노출 경로가 다르다" 만으로 "다른 객체" 라고 단정한 부분은 `conpty.h` / `conpty.cpp` 확인이 필요하다. 만약 `ConPtySession` 이 생성자에서 `Session::vt_mutex` 를 reference 로 받아서 `vt_mutex()` 로 되돌려준다면 가설 전체가 무너진다.
- V1/V2 시나리오가 앱 실제 resize sequence 와 동일한지 empirical 검증 없음 — 내가 본 건 `render_state.cpp` 단일 파일과 session wiring 뿐. 실제 WPF 의 Grid `ArrangeOverride` 가 어떤 dim 순서로 `resize_session` 을 부르는지는 관측하지 않았다.
- ADR-006 원문과 feedback memory (`feedback_e2e_bash_session_limits.md` 같은 과거 race 기록) 를 읽지 않았다.

## 읽은 파일

- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (1-302)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (1-120)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (350-410)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (130-190)
- grep: `vt_mutex` in `src/session/`
- grep: `conpty->vt_mutex|conpty_->vt_mutex` in `src/`
