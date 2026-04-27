# Agent R3-04 원인 분석

## 결론 (한 문장, 10~20단어)
Resize 경로와 Render 경로가 서로 다른 두 mutex 를 잡고 있어, reshape 이 락 보호 없이 start_paint / frame() 과 병행 실행되며 첫 pane 의 cell 메타데이터·버퍼가 손실된다 (이중 mutex race).

## Empirical 증거 3가지
1. **두 mutex 객체가 물리적으로 분리되어 있음.**
   - `src/session/session.h:103` — `std::mutex vt_mutex;` 가 `Session` 구조체 멤버로 선언됨
   - `src/conpty/conpty_session.cpp:143` — `std::mutex vt_mutex;` 가 `ConPtySession::Impl` 멤버로 별도 선언됨
   - 두 필드 모두 이름만 같을 뿐 **완전히 다른 객체** (각각 `sess->vt_mutex` 와 `sess->conpty->vt_mutex()` 로 접근)

2. **Render 경로와 Resize 경로가 서로 다른 mutex 를 잡음.**
   - Render: `src/engine-api/ghostwin_engine.cpp:146` — `state.start_paint(session->conpty->vt_mutex(), vt);` → ConPty 쪽 mutex
   - Render: `src/renderer/render_state.cpp:133` — `std::lock_guard lock(vt_mutex);` (start_paint 최상단)
   - Resize: `src/session/session_manager.cpp:373-375` — `std::lock_guard lock(sess->vt_mutex); sess->conpty->resize(...); sess->state->resize(...);` → Session 쪽 mutex
   - `resize_session` 은 `gw_surface_resize` (`ghostwin_engine.cpp:601`) 에서 pane resize 시 호출됨. 즉 Alt+V split 직후 WPF Grid layout 의 shrink-then-grow 연쇄가 이 경로로 흘러들어간다.

3. **유닛 테스트가 dual-mutex race 를 100% 재현.**
   - `tests/render_state_test.cpp:546` `test_dual_mutex_race_reproduces_content_loss` — 문서상 "Session::vt_mutex (resize_session) 과 ConPtySession::vt_mutex (start_paint) 가 분리되어 있다" 를 그대로 모방 (comment L554-555). **reshape shape 은 전부 초기 capacity (40x5) 내로 제한** (L579-587) → content-preserving reshape 의 fast path 만 타게 설계. 그런데도 5초 stress 에서 193940/193940 (100%) content loss. 이는 reshape 이 cell_buffer 를 건드리지 않고 `cols`/`rows_count` 메타데이터만 바꿔도, start_paint 와 frame() 이 그 메타데이터를 락 없이 읽기 때문에 **torn read (찢어진 읽기)** 가 발생함을 뜻한다. 반면 같은 파일의 `test_real_app_resize_flow` (단일 스레드) 는 PASS → 멀티스레드에서만 터진다는 방증.

## 확신도 (%)
90

## 대안 가설 (1개만)
WPF Grid layout 의 shrink-then-grow 연쇄가 `cap_cols`/`cap_rows` 도 함께 성장시키는 경로 (slow path, L101-124) 를 밟는 구간에서, 현재 `reshape` 이 `cell_buffer = std::move(new_buffer)` 를 하는 순간 render 쓰레드가 예전 pointer 로 `row(r)` 을 읽으며 use-after-free 를 일으키는 것 — 즉 tear 가 메타데이터가 아닌 **buffer 포인터** 쪽에서 발생. (본질은 여전히 lock 부재이지만 트리거가 다름.)

## 내 결론의 약점 (1개만)
테스트 `test_dual_mutex_race_reproduces_content_loss` 는 단일 `TerminalRenderState` 에 대해 fake mutex 두 개를 걸고 돌리는 **in-process 모델** 이다. 실제 앱에서 `resize_session` 이 호출되는 빈도와 `start_paint` 의 병행 정도가 다를 수 있어, 100% loss 라는 수치가 실제 Alt+V 한 번의 "첫 pane 의 프롬프트 사라짐" 과 정확히 같은 메커니즘인지 — 또는 단순히 "가능성을 증명했을 뿐" 인지 — 를 구분하려면 프로덕션 환경에서 `GHOSTWIN_RESIZE_DIAG=1` 로그를 떠서 Alt+V 순간의 `_api[total=N->M]` 이 실제로 감소하는지 확인이 필요하다. 확실하지 않음.

## 파일 직접 읽음 목록
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h:1-147 (특히 L103 `std::mutex vt_mutex`)
- C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp:1-463 (특히 L132-155 Impl, L143 `std::mutex vt_mutex`, L425-445 resize, L459 `vt_mutex()` accessor)
- C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp:100-350 (특히 L146 render 호출), L581-605 (`gw_surface_resize` → `resize_session`)
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp:340-400 (특히 L369-376 `resize_session`)
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h:1-120 (특히 L39-94 `RenderFrame`, L93 `reshape`)
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp:1-301 (특히 L87-125 `reshape`, L132-229 `start_paint`, L231-299 `resize`)
- C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp:540-660 (특히 L553-557 dual mutex comment, L579-587 capacity-bounded shapes)
