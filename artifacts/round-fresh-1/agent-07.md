# F-07 원인 분석

## 결론 (한 문장, 20 단어 이내)

`RenderFrame::reshape` slow path 가 copy bound 로 shrink 된 logical dims 를 써서 cap 초과 grow 시 capacity 내 잔존 셀이 drop 된다.

## 증거 3

### 증거 1: slow path 의 copy bound 가 logical dims 기반

`src/renderer/render_state.cpp:110-111`:

```cpp
const uint16_t copy_rows = std::min(rows_count, cap_rows);
const uint16_t copy_cols = std::min(cols, cap_cols);
```

`rows_count` / `cols` 는 **logical view dims** (render_state.h:41-42 주석 그대로). Shrink 직후 logical 이 작아진 상태면 copy bound 도 그만큼 작아진다. 반면 capacity 에는 이전 content 가 여전히 `[0, cap_cols) × [0, cap_rows)` 범위로 살아있다 (fast path 가 shrink 를 metadata-only 로 처리했기 때문 — render_state.cpp:92-96). Copy bound 는 "버퍼에 살아있는 영역" 이 아니라 "현재 노출 중인 영역" 을 가리킨다 → mismatch.

### 증거 2: V1 4/4 FAIL vs V2 5/5 PASS 를 slow path trigger 여부로 완전히 설명

V1 `{40,5},{1,1},{200,50},...`:
- `allocate(40,5)` → cap=(40,5), logical=(40,5)
- `reshape(1,1)` → fast path (1<=40, 1<=5). cap 유지, logical=(1,1)
- `reshape(200,50)` → **slow path** (200>40). `copy_rows = min(1,5) = 1`, `copy_cols = min(1,40) = 1`. **단 1 셀만 신 버퍼로 복사**. 나머지 39×5 = 195 셀 drop.

V2 `{40,5},{20,5},{30,4},{40,5},{10,3},{40,5}`:
- 모든 dim 이 40x5 이하. **매 호출 fast path** (render_state.cpp:92-96). Slow path 의 버그가 발현될 기회 없음. 5/5 PASS.

기존 unit test `test_resize_shrink_then_grow_preserves_content (40,5→1,1→20,5)` 가 PASS 하는 이유도 동일 — 20<=40, 5<=5 이라 두 번째 reshape 도 **fast path**. slow path 의 `copy_rows/cols = min(logical, cap)` 버그는 이 테스트로는 건드려지지 않는다.

### 증거 3: 실제 Alt+V 경로에서 slow path trigger 가 가능함

호출 체인:

1. `TerminalHostControl.OnRenderSizeChanged` (`src/GhostWin.App/Controls/TerminalHostControl.cs:126-141`) → `PaneResizeRequested?.Invoke(...)`
2. `PaneLayoutService.OnPaneResized` (`src/GhostWin.Services/PaneLayoutService.cs:232-238`) → `_engine.SurfaceResize(...)`
3. `gw_surface_resize` (`src/engine-api/ghostwin_engine.cpp:581-606`) → `session_mgr->resize_session(cols, rows)` with `cols = w / cell_width`, `rows = h / cell_height`, **floor=1**
4. `SessionManager::resize_session` (`src/session/session_manager.cpp:369-376`) → `conpty->resize(cols,rows)` (VT 내부 resize) → `state->resize(cols,rows)` (`TerminalRenderState::resize` → `RenderFrame::reshape` × 2)

즉 WPF layout pass 중 intermediate 크기가 **먼저 감소, 이후 증가** 하기만 하면 slow path trigger 가능. Alt+V split 은 Grid 의 3-child (left/splitter/right) 재배치를 통해 **기존 host 가 먼저 ActualWidth 가 줄었다가 새 컨테이너에 reparent 되면서 다시 측정** 되는 전형적인 shrink-then-grow 시퀀스이다 (PaneContainerControl.cs:135-181 `BuildGrid` → 구 host 를 `Border.Child = null` 로 detach 후 재부착 — PaneContainerControl.cs:217-219). 양쪽 pane 에 대해 이런 경로가 발생하며, 양쪽의 초기 capacity 가 분할 전 전체 window dims 기반일 수도, 각 pane 기반일 수도 있어 **한 쪽이라도 grow-over-cap 을 맞으면 empty.** WPF Grid 의 intermediate measure 는 `GridLength.Auto` splitter + `1.0-Ratio` star 때문에 정확한 중간값을 예측하기 어렵지만, `{cols=1, rows=1}` clamp 가 `w/cell_width < 1` 에서 발생하는 `gw_surface_resize:599` 도 이에 기여한다 (실제 하드웨어에서 pane dims 가 과소 측정되면 1 로 clamp → logical 이 1x1 로 수축한 직후 최종 크기로 grow).

## 확신도 (%)

**72%**

- Slow path 자체의 버그는 **empirical 로 확정** (V1 4/4 FAIL, 수식으로 copy_rows=1/copy_cols=1 재현). 이 부분 확신도 95%.
- 이 버그가 **Alt+V 실 사용자 증상의 단일 root cause** 인지는 중간값. `OnRenderSizeChanged` 의 shrink→grow-over-cap 시퀀스가 실제 하드웨어에서 발생한다는 직접 증거(로그/반복 재현)는 이 세션에서 읽지 못했다. 기여도 추정 근거는 (a) slow path 가 확실히 content loss 를 만드는 유일한 코드 경로이고 (fast path 는 metadata-only), (b) `TerminalRenderState::resize` 이후 `_api` 내 nonzero 가 사라지면 `start_paint` 의 `vt_raw_nonzero` 역시 VT resize 에 의해 감소했을 것이므로 `_p` 로 propagate 할 content 자체가 손실된다. VT 가 자체 scrollback 에서 복구해 줄지는 ghostty 내부 동작이라 미확인.
- 확실하지 않음: Alt+V 시 실제로 grow > cap 이 **매번** 발생하는지. Logical 이 shrink-only 로만 간다면 fast path 만 타고 증상은 다른 원인일 수 있다 (그 경우 대안 가설 참조).

## 대안 가설 1

**A1: VT (ghostty) 의 `resize()` 자체가 shrink 시 hard line history 를 drop**

`ConPtySession::resize` (`src/conpty/conpty_session.cpp:425-445`) 는 `ResizePseudoConsole` 호출 후 `impl_->vt_core->resize(cols, rows)` 로 VT 에 resize 를 전파한다. ghostty 의 Screen.resize 가 shrink 시 **현재 화면에서 벗어난 row 를 history 로 밀어넣지 않거나**, shrink→grow 사이에 empty 로 재할당할 가능성. `for_each_row` 가 `vt_raw_nonzero=0` 을 돌려주면 `start_paint` 의 `_api` 는 grow 후 memcpy 대상이 empty 가 되어 빈 화면이 된다. 이 경우 `RenderFrame::reshape` 은 무죄이고 fix 는 VT 쪽 (또는 VT resize 를 shrink-skip 처리) 이다. 이 가설을 가르는 단서는 `GHOSTWIN_RESIZE_DIAG=1` 을 켠 상태에서 `start-paint-diag` 로그의 `vt_raw_nz` 가 resize 직후 0 이면 VT 원인, 그 전부터 `_api` 가 비어 있으면 render_state 원인이다. `resize-diag` 로그에 `before_api_total` 이 0 으로 찍히면 "resize 호출 직전에 이미 render_state 가 비어 있었다" 를 의미하므로 이 역시 VT/propagation 쪽 원인을 시사한다.

## 내 약점

1. **VT 내부 동작 미확인** — ghostty `Screen.resize` 가 shrink 시 content 를 어떻게 처리하는지 코드로 확인하지 않았다. 나의 slow-path 가설이 맞더라도 VT 가 먼저 empty 를 돌려주면 root cause 는 VT 쪽이 되고 내 fix 는 무의미해진다.
2. **실제 Alt+V 로그 부재** — `GHOSTWIN_RESIZE_DIAG=1` 로 캡처된 `resize-diag` 전후 카운터를 보지 않았다. `before_cap_c`/`before_cap_r` 값이 사용자 하드웨어에서 실제로 `new_c`/`new_r` 보다 작은지 (= slow path 진입) 를 직접 관찰한 증거가 없다. V1 테스트는 인공적 시나리오일 뿐 사용자 증상과의 대응은 **추측**이다.
3. **WPF Grid intermediate measure 추정** — Alt+V split 시 WPF 가 정확히 어떤 dims 로 intermediate measure 를 내는지 (measure/arrange 각 단계별로) 는 WPF 내부 구현이라 확정짓지 못했다. Shrink 가 발생하지 않고 grow-only 경로라면 `copy_rows = min(rows_count, cap_rows)` 의 min 이 의미 있게 작동해서 모든 셀을 복사할 수 있고, 내 가설은 부분적으로 약해진다 (단, 그 경우 `cols <= cap_cols` 이므로 어차피 fast path 에 흡수되어 문제가 없어야 한다).
4. **2 개의 RenderFrame** — `TerminalRenderState` 는 `_api` 와 `_p` 두 개의 `RenderFrame` 을 가지고 있고 `resize()` 는 둘 다 reshape 한다 (render_state.cpp:266-267). `_p` 는 직전 frame 의 render 용이므로 shrink-then-grow 에서 `_p` 의 content 가 살아남는지 여부는 slow path 버그의 영향을 동일하게 받지만, 사용자 증상에서 "첫 프롬프트" 가 사라지는 것이 `_api` 의 손실 때문인지 `_p` 의 손실 때문인지, 아니면 `start_paint` 이후 VT 가 재전송 못 하는 문제인지 분리하지 못했다.

## 읽은 파일

- `src/renderer/render_state.cpp` (전체, 1-301)
- `src/renderer/render_state.h` (전체, 1-120)
- `src/session/session_manager.cpp` (330-449)
- `src/engine-api/ghostwin_engine.cpp` (110-170, 575-633)
- `src/conpty/conpty_session.cpp` (420-463)
- `src/GhostWin.App/Controls/PaneContainerControl.cs` (전체, 1-334)
- `src/GhostWin.App/Controls/TerminalHostControl.cs` (60-210)
- `src/GhostWin.Services/PaneLayoutService.cs` (170-245) + grep 결과
