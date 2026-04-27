# F-05 원인 분석

## 결론 (한 문장, 20 단어 이내)

`_p` 프레임이 `_api` 와 독립적으로 `reshape` 되어 스트라이드 재매핑 시 dirty row 복사 없이 이전 content 가 사라진다.

## 증거 3

1. **`render_state.cpp:266-267` 의 dual-reshape** — `TerminalRenderState::resize` 가 `_api.reshape(cols, rows)` 와 `_p.reshape(cols, rows)` 를 **동일한 새 dims 로 각각** 호출한다. `_api` 는 `for_each_row` 가 VT 에서 cell 을 다시 채울 source 가 있지만, `_p` 는 오직 `start_paint` 의 dirty-row memcpy 로만 갱신된다 (`render_state.cpp:217-223`). Alt+V split 시 `_p` 의 `cap_cols` 가 shrink→grow 로 stride 가 바뀌면, 기존 `cell_buffer` 의 row offset 이 (최초 grow 호출 후) 재배치되며 shrink 단계에서는 cap 이 그대로라 metadata-only 이지만 다음 grow 가 다른 dim 으로 오면 `copy_cols = min(cols, cap_cols)` 에서 직전에 shrink 해둔 `cols` 값으로 잘림 → 이전 prompt 의 오른쪽이 truncate. 이후 `_api` 가 dirty 로 set 되어도 `_p` 에는 **truncated row 만** 전파된다.

2. **`resize` 가 `_api` 만 dirty mark, `_p` 는 skip** (`render_state.cpp:274-276`) — 주석에 명시된 의도는 "`_api.set_row_dirty(r)` → 다음 `start_paint` 에서 `_api → _p` 전파" 이지만, `start_paint` 은 오직 VT 가 dirty 로 알려주는 row 만 `_api` 에 memcpy 한다 (`render_state.cpp:162-183`). PowerShell 은 resize 직후 VT 에게 **아무 row 도 dirty 라고 알리지 않기** 때문에 (주석 273 번째 줄이 직접 인정: "bare terminal resize does NOT mark every row dirty"), `_api` 의 "manually set dirty" flag 만 남고 cell content 는 VT 가 resize 후 다시 render 하지 않는 한 공백이다. `_p` 는 `_api` 의 공백 row 를 그대로 복사 → loss.

3. **V1 (크기 진폭 큰 6-step) 4/4 FAIL vs V2 (진폭 작은 6-step) 5/5 PASS** — V1 `{40,5}→{1,1}→{200,50}→{20,5}→{1,1}→{300,80}` 은 **두 번의 grow (1→200, 20→300) 가 서로 다른 cap 으로 발생**. 첫 grow 에서 `cap = {200,50}` 로 확장, 그 다음 `{20,5}` shrink 는 metadata-only 로 cap 유지 (200,50), `{1,1}` shrink 도 metadata-only, `{300,80}` 에서 또 grow 시 `copy_rows = min(rows_count, cap_rows) = min(1, 50) = 1`, `copy_cols = min(cols, cap_cols) = min(1, 200) = 1` 로 **정확히 1 cell 만 복사**. 나머지 50×200 = 9,999 cell 분 content 는 drop. V2 는 모든 step 이 cap 내부에서 움직여 metadata-only 경로만 타므로 loss=0. 이것이 "min() 기반 memcpy 가 shrink 한 번으로 content 를 truncate" 를 직접 재현 (`render_state.cpp:110-111`).

## 확신도 (%)

**80%**

근거: 코드 path 는 empirical 하게 확정 (`reshape` 의 `copy_rows = min(rows_count, cap_rows)` 가 shrink 후 grow 에서 logical `rows_count` 를 그대로 쓰는 패턴). V1/V2 차이도 copy bound 로 정량 설명됨. 다만 Alt+V 의 실제 dim 시퀀스가 V1 패턴인지 V2 패턴인지 본 문제에서 직접 관측하지 못했고, `session_manager.cpp:369-395` 가 `Session::vt_mutex` 와 `conpty->resize` 이중 호출을 하는 것은 봤지만 WPF Grid 의 실제 shrink-then-grow dim 값은 로그가 없어 "첫 shrink 가 어느 값까지 가는가" 는 추측 (문서 Follow-up #8 의 주장 "Grid shrink-then-grow chain" 을 신뢰).

## 대안 가설 1

**`_p` vs `_api` 의 `cap_cols` 불일치로 인한 stride corruption**: `_api.reshape` 는 첫 grow 시 `cap_cols` 를 200 으로 grow, 이후 `_p.reshape` 가 호출되지만 `_p` 는 독립 storage 라 자기 자신의 `cap_cols` (e.g. 40) 만 사용. 다음 `start_paint` 에서 `_p.row(r)` 는 `_p.cap_cols` 로 offset 계산, `_api.row(r)` 는 200 으로 offset 계산. 하지만 `memcpy(dst.data(), src.data(), _api.cols * sizeof(CellData))` 에서 `_api.cols` 만큼 복사하므로 `_p` 의 storage 가 작으면 **overflow 또는 wrong row 에 write**. Option A "backing buffer with max capacity" 는 `_api` 와 `_p` 의 cap 을 동일 max 로 묶어야 일관되는데 현재는 그렇지 않다. 이 가설은 첫 grow 가 두 프레임에 같은 dim 으로 들어오므로 cap 도 동일하게 grow 해 방어되지만, `start_paint` 의 메모리 레이아웃 가정이 무너질 risk 는 잔존.

## 내 약점

- WPF Grid 의 실제 Alt+V 분할 시 resize dim 시퀀스를 직접 보지 못했다. `session_manager.cpp` 의 `resize_session` 은 단일 `(cols, rows)` pair 만 받으므로 시퀀스는 상위 layer (WPF `PaneContainerControl` 의 LayoutUpdated 핸들러) 에서 발생. 이 파일은 이번 task 의 읽기 목록에 없어 미확인.
- `ConPtySession::Impl::vt_mutex` 와 `Session::vt_mutex` 이중 mutex 관련 — `resize_session` 은 **`Session::vt_mutex`** 로 lock 하고, `start_paint` 은 `conpty->vt_mutex()` (`ghostwin_engine.cpp:146`) 로 lock. 두 mutex 가 다른 객체라는 fact 는 받았지만 이 부분이 race 를 어떻게 만드는지는 "각각 독립 lock" 이상으로 추적 못함. 본 결론의 주 원인 (single-thread `reshape` content truncate) 은 mutex 독립적이라 race 가 필요 없지만, V1/V2 의 멀티스레드 stress context 에서 추가 mutation 은 배제 못함.
- `CellData` 의 POD 여부 확인 안 함 — `std::memcpy` 의 UB 없음은 POD 가정. `cp_count > 0` check 는 union/variant 가 아닐 가능성 시사하지만 원본 struct 를 보지 않았다.

## 읽은 파일

- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` (1-301, 전체)
- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.h` (1-121, 전체)
- `C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` (369-404)
- `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` (140-180)
