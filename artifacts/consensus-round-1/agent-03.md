# Agent R3-03 원인 분석

## 결론 (한 문장, 10~20단어)
Session 쪽 `vt_mutex` 로 resize 를 감쌌지만 render 는 ConPty Impl 쪽 `vt_mutex` 를 잡아 두 mutex 가 다른 객체이므로 `_api.cell_buffer` 가 race 로 파괴된다.

## Empirical 증거 3가지
1. `src/session/session_manager.cpp:373` — `resize_session()` 이 `std::lock_guard lock(sess->vt_mutex);` 로 **Session 객체의 vt_mutex** 를 잡고 그 안에서 `sess->state->resize(cols, rows)` 를 호출 (`src/session/session.h:103` 의 `Session::vt_mutex` 필드). 동일 패턴이 `session_manager.cpp:305` (`apply_pending_resize` 이전 호출 경로) 와 `session_manager.cpp:391` (`apply_pending_resize`) 에도 있음 — 즉 **모든 resize 진입점이 Session 쪽 mutex 를 사용**.
2. `src/engine-api/ghostwin_engine.cpp:146` — 렌더 스레드 `render_surface()` 가 `state.start_paint(session->conpty->vt_mutex(), vt)` 로 **ConPty Impl 쪽 vt_mutex** 를 전달. 주석에도 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex). I/O thread writes to VT under ConPty mutex; render must use the SAME mutex for visibility (design §4.5 — dual-mutex bug fix)" 라고 명시돼 있음. `src/conpty/conpty_session.cpp:143` 의 `ConPtySession::Impl::vt_mutex` 는 `Session::vt_mutex` 와 완전히 별개의 `std::mutex` 객체.
3. 사용자 제공 팩트 2: `test_dual_mutex_race_reproduces_content_loss` 가 193940/193940 (100%) loss 로 FAIL. 반면 `test_real_app_resize_flow` (단일 스레드) 와 `test_resize_shrink_then_grow_preserves_content` (reshape 로직만 단독) 는 PASS — 즉 reshape 로직 자체는 정상이고 **스레드 2개가 서로 다른 mutex 를 잡을 때만** content loss 재현됨. 이는 결론 1의 구조적 원인과 정확히 일치.

## 확신도 (%)
92

## 대안 가설 (1개만)
`start_paint` 내부에서 `for_each_row` 가 콜백 람다에 `_api.set_row_dirty` + `std::memcpy(dst.data(), cells.data(), ...)` 를 실행 (render_state.cpp:164-179). 만약 resize 가 같은 mutex 하에서 직렬화 되더라도 ghostty VT 의 `for_each_row` 가 resize 직후 빈 row 를 보고 VT 쪽 dirty flag 만 세팅한 상태로 `cp_count==0` 셀을 돌려주면 `_api` 의 preserved content 를 빈 셀로 덮어쓸 수 있음 — 즉 **dual-mutex race 대신 "VT resize 가 clearCells 경로를 타서 VT 쪽 cell 이 이미 비워진 채 반환된다"** 라는 가설. render_state.cpp:167-177 주석이 이미 "defensive merge 는 정상 clear/erase 경로를 깨뜨린다" 고 기록돼 있어 straight memcpy 로 복귀했다는 맥락이 있음.

## 내 결론의 약점 (1개만)
Dual-mutex race 가 구조적으로 존재하는 것은 확실하지만 (팩트 + 코드), Alt+V split 시나리오에서 resize 호출이 실제로 render 스레드와 시간적으로 겹치는지는 직접 관측하지 못함. 만약 Alt+V 가 WPF UI 스레드에서 `resize_session` → 그 직후 사용자가 PowerShell 출력 대기 상태라면 I/O 스레드는 idle 이고 render 스레드만 16ms tick 으로 동작 — 이 경우 race window 는 millisecond 단위지만 사용자 리포트 "100% 재현" 과 일치하려면 race 가 사실상 매번 발생해야 하고, 그 증명은 아직 로그/타임라인으로 보여주지 못했음. 테스트가 93M iter 를 돌려야 193K loss 를 잡았다는 것은 single-shot 확률이 0.2% 수준이라는 뜻 — 사용자 "매번 100% 재현" 과의 간극이 이 결론의 empirical 약점.

## 파일 직접 읽음 목록
- `src/renderer/render_state.h:1-120`
- `src/renderer/render_state.cpp:1-302`
- `src/session/session.h:1-147` (특히 101-103: `conpty`, `state`, `vt_mutex` 필드)
- `src/conpty/conpty_session.cpp:100-222` (특히 132-155: Impl 구조체의 `vt_mutex`)
- `src/conpty/conpty_session.cpp:400-464` (특히 425-445: `ConPtySession::resize` 내부 잠금, 459: `vt_mutex()` 접근자)
- `src/conpty/conpty_session.h:1-93` (78-81: `vt_mutex()` 주석 "Render thread should lock this same mutex NOT a separate Session::vt_mutex")
- `src/session/session_manager.cpp:340-415` (367-376: `resize_session`, 389-395: `apply_pending_resize`)
- `src/engine-api/ghostwin_engine.cpp:100-206` (115-171: `render_surface`, 139-146: mutex 선택 주석 + 호출)
- Grep `conpty->vt_mutex|state->resize|sess->vt_mutex` (9 hits 확인)
