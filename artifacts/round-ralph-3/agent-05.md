# 라운드3 에이전트05 원인 분석

## 결론 (한 문장)

`PaneContainerControl.BuildElement` 가 split 시 기존 `TerminalHostControl` 인스턴스를 새 `Border` 아래로 reparent 하면서 `HwndHost.BuildWindowCore` 가 재호출되어 새 child HWND 가 만들어지지만, 엔진의 `SurfaceManager` 에 등록된 swapchain 은 폐기된 old HWND 에 그대로 묶여 있고 `PaneLayoutService.OnHostReady` 도 `state.SurfaceId != 0` early-return 으로 surface 를 재생성하지 않아, 왼쪽 pane 의 PowerShell 화면이 그려지지 않은 것처럼 보인다 — 가 가장 유력한 가설입니다 (단, BuildWindowCore 재호출 자체는 코드만으로 100% empirical 확정 불가능, **추측** 표시).

## 증거 3 가지

### 증거 1 — Host reparent 가 실제로 일어나는 코드 경로

`src/GhostWin.App/Controls/PaneContainerControl.cs`

- 라인 197~212: split 후 `BuildElement` 는 `oldHosts` 에서 sessionId match 로 host 를 찾아 reuse 한 뒤 `host.PaneId = node.Id` 로 paneId 만 갈아끼웁니다.
- 라인 218~219: `if (host.Parent is Border previousBorder) previousBorder.Child = null;` — **기존 Border 에서 detach**.
- 라인 234~241: 이어서 새 `Border { Child = host, ... }` 를 만들어 host 를 새 부모 아래로 attach.
- `PaneNode.Split` (`src/GhostWin.Core/Models/PaneNode.cs:22-36`) 은 자기 자신을 branch 로 변환하면서 `oldLeaf` 와 `newLeaf` 를 자식으로 두기 때문에, **`BuildElement` 가 트리 root 부터 새 Grid → 새 Border → host 로 다시 wrap** 합니다. 즉 host 의 visual parent 체인 전체가 교체됩니다.

WPF `HwndHost` 는 visual parent 체인이 변하면 `OnVisualParentChanged` → 내부적으로 child HWND 를 새 로직으로 SetParent 하거나, 일부 케이스에서는 destroy → BuildWindowCore 재호출로 child HWND 를 다시 만듭니다. `TerminalHostControl.BuildWindowCore` 라인 40~42 의 주석 자체가 `"H3 가설 검증: 동일 인스턴스에서 BuildWindowCore 가 2회 이상 호출되면 H3 confirmed"` 라고 적혀 있어, **이 위험이 first-pane-render-failure 사이클에서 잠재 가설로 등록된 상태** 임을 코드 주석이 직접 인정합니다 (현재 codebase 가 H3 를 closed 했다는 코드/주석 증거는 찾지 못했습니다 — 일부 ARchive 문서가 mitigated 라고 주장하는 부분은 인용 금지로 직접 확인하지 않았습니다).

### 증거 2 — OnHostReady 가 surface 를 재생성하지 않는다

`src/GhostWin.Services/PaneLayoutService.cs:179-230`

라인 198: `if (state.SurfaceId != 0) return; // Already created — silent OK (정상 경로)`

→ 만약 reparent 로 인해 host 인스턴스가 재차 BuildWindowCore 를 거쳐 새 `_childHwnd` 를 만들고 다시 `HostReady` 를 fire 한다 해도, leaf state 의 `SurfaceId` 는 이미 0 이 아니므로 OnHostReady 는 무조건 early return 합니다. 즉 **새 hwnd 에 대응하는 swapchain 은 절대 재생성되지 않습니다**.

같은 파일 라인 232~238 의 `OnPaneResized` 는 `_engine.SurfaceResize(state.SurfaceId, ...)` 만 호출할 뿐, surface 의 hwnd 를 갱신하는 API 는 호출하지 않습니다 — `gw_surface_resize` 의 시그니처 자체에 hwnd 인자가 없습니다 (`src/engine-api/ghostwin_engine.cpp:581-606`).

### 증거 3 — SurfaceManager / SwapChain 은 hwnd 갱신 경로가 아예 없다

`src/engine-api/surface_manager.cpp`

- 라인 14~53 `create_swapchain`: `factory_->CreateSwapChainForHwnd(device_, surf->hwnd, &desc, ...)` — swapchain 은 생성 시점에 **hwnd 에 영구 바인딩**.
- 라인 99~106 `resize`: `pending_w/pending_h + needs_resize.store(true)` 만 set. hwnd 변경 경로 없음.
- `src/engine-api/ghostwin_engine.cpp:115-171` `render_surface`: deferred resize 시 `surf->swapchain->ResizeBuffers(...)` 만 호출. **`surf->hwnd` 는 한 번 set 되면 절대 변하지 않습니다**.

DXGI 사양상 `IDXGISwapChain1` 은 다른 hwnd 로 옮길 수 없으므로, child HWND 가 destroy 되는 순간 그 swapchain 의 Present 결과는 화면에 보이지 않습니다 (또는 silently fail). `render_surface` 는 매 frame `bind_surface(surf->rtv, surf->swapchain, ...)` 만 호출하므로 (라인 164~167) 새 hwnd 가 만들어져도 거기에는 그릴 방법이 없습니다.

게다가 `PaneContainerControl.BuildGrid` 라인 156~170 의 dispose loop 는 **인스턴스 동일성 (`liveHosts.Contains(host)`) 으로 살아있는지 판단**합니다. sessionId match 로 reuse 된 host 는 같은 인스턴스이므로 Dispose 되지 않고 — 즉 GhostWin 코드 입장에서는 "이 host 는 살아있다" 인데, 그 안의 child HWND 는 reparent 로 이미 다른 것일 수 있습니다 (= 코드는 host 인스턴스만 추적, hwnd 는 추적 안 함).

## 확신도 (0~100)

**55**

- 증거 1, 2, 3 의 코드 사실은 확정적입니다 (직접 Read 로 확인).
- 그러나 결정적 missing piece: WPF `HwndHost` 가 detach + 새 Border 아래 re-attach 시 **실제로 BuildWindowCore 를 재호출하는지** 가 코드만으로 100% 확정되지 않습니다. `TerminalHostControl.BuildWindowCore` 의 주석이 "동일 인스턴스에서 2회 이상 호출되면 H3 confirmed" 라고 진단 시그널만 심어둔 상태여서, 이게 closed 인지 open 인지를 코드 주석만으로 단정할 수 없습니다.
- 같은 코드베이스의 `render_state.cpp:14-36` `GHOSTWIN_RESIZE_DIAG` 와 line 167~179 의 "defensive merge 제거" 코멘트는 split-content-loss-v2 가 **VT 측 reflow / cell 데이터 보존 가설로 한 번 검증되었다가 reverted** 되었음을 보여줍니다. 즉 이 가설은 이미 한번 시도되어 실패했음을 시사하며, 이는 본 가설 (HWND reparent / surface stale) 의 가능성을 상대적으로 높입니다.
- "100% 매번 재현" 이라는 사용자 보고는 deterministic 메커니즘을 강하게 시사하고, reparent 는 split 마다 결정적으로 발생하므로 이와 부합합니다. 반대로 race / timing 가설은 100% 재현과 잘 맞지 않습니다.

## 대안 가설

### A — ghostty Terminal.resize → reflow 가 셀을 잃는다 (확신도 25)

- `external/ghostty/src/terminal/Terminal.zig:2827-2872` `pub fn resize` 는 `primary.resize({.cols, .rows, .reflow = self.modes.get(.wraparound)})` 를 호출하고 line 2858~2859 `self.flags.dirty.clear = true` 를 set 합니다.
- `external/ghostty/src/terminal/render.zig:263-649` `RenderState.update` 는 `t.flags.dirty` 가 0 이상이면 `redraw = true` 로 모든 row 를 다시 build 합니다. 이론상 cell 보존됩니다.
- 그러나 split 직후 WPF Grid 의 layout pass 가 host 의 ActualWidth 를 잠시 1×1 같은 극소값으로 줄였다 늘리는 shrink-then-grow 시퀀스를 거치면, ghostty 의 reflow 가 1열로 wrap 되었다가 다시 새 cols 로 unwrap 되는 경로를 거치는데, 이 reflow 경로가 일부 케이스에서 cell 을 잃을 수 있습니다.
- `src/renderer/render_state.{h,cpp}` 의 Option A backing capacity 패턴은 **GhostWin 측 RenderFrame 의 capacity 만** 보존할 뿐 ghostty 측 PageList 의 reflow 결과 자체는 보존하지 못합니다. CLAUDE.md TODO 8 (`split-content-loss-v2`) 의 코멘트도 "Option A backing buffer with max capacity 유력" 으로 GhostWin 측을 가리키지만, 이 fix 가 적용된 후에도 사용자 보고가 살아있다는 것은 — 적어도 GhostWin 측 fix 만으로는 충분하지 않다는 의미입니다.

### B — `force_all_dirty + dirty-only memcpy` 의 반대 흐름 (확신도 15)

- `src/engine-api/ghostwin_engine.cpp:145` `state.force_all_dirty()` 는 매 frame `_api.dirty_rows` 만 set.
- `src/renderer/render_state.cpp:151-184` `start_paint` 의 `for_each_row` callback 은 VT 가 dirty 라고 한 row 만 `_api.row(row_idx)` 에 memcpy.
- 라인 162 `if (dirty)` 분기와 force_all_dirty 의 상호작용으로, **VT 가 clean 이라고 보고한 row 의 _api 데이터는 갱신되지 않는데** force_all_dirty 때문에 라인 217~223 의 _api → _p copy loop 는 그 stale (혹은 reshape 후 0 으로 채워졌을 수 있는) 데이터를 _p 에 복사할 수 있습니다.
- ghostty resize 직후에는 `t.flags.dirty.clear=true` 로 redraw 가 트리거되어 모든 row 가 dirty 가 되므로 정상 흐름이지만, 그 이후 frame 부터는 PowerShell 이 다시 prompt 를 그리기 전까지 VT 가 clean 을 보고할 수 있고, 그 사이에 stale 0 데이터가 한 번 _p 로 복사된 적이 있다면 화면이 비어 보일 수 있습니다.
- 단, RenderFrame 의 cell_buffer 는 reshape 후에도 capacity 안에서 데이터가 보존되므로 (Option A) 이 시나리오는 reshape 가 capacity 초과로 새 buffer 를 alloc 하는 경우에 한정됩니다. capacity 가 초과되려면 split 후 새 dimensions 가 historical max 보다 커야 하는데, split 은 본질적으로 dimensions 를 줄이므로 이 경로는 자주 발생하지 않습니다 — 확신도가 낮은 이유.

### C — Session::vt_mutex / ConPty::Impl::vt_mutex 이중 mutex race (확신도 10)

- `src/conpty/conpty_session.cpp:425-445` `ConPtySession::resize` 는 `impl_->vt_mutex` 를 잡고 `vt_core->resize` 를 호출.
- `src/session/session_manager.cpp:369-376` `resize_session` 은 **`sess->vt_mutex`** (Session 객체의 mutex) 를 잡고 `sess->conpty->resize(cols, rows)` 와 `sess->state->resize(cols, rows)` 를 호출.
- `sess->conpty->resize` 안에서 다시 `impl_->vt_mutex` 를 잡으므로, `sess->vt_mutex` 와 `impl_->vt_mutex` 가 서로 다른 mutex 입니다 — CLAUDE.md "기술 부채" 항목의 `vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)` 가 가리키는 바로 그 부분.
- I/O thread (write) 가 `impl_->vt_mutex` 만 잡고 cell 데이터를 write 하는 동안, render thread (`start_paint`) 도 `session->conpty->vt_mutex()` (impl 측) 만 잡으므로 둘 사이에서는 일관되지만, **session_manager::resize_session 이 sess->vt_mutex 만 잡고 vt_core->resize 까지 진입하는 순간 impl 측 mutex 를 두 번 잡으면 정상이고 한 번도 안 잡으면 race** 입니다. 코드 라인 373~376 을 보면 `std::lock_guard lock(sess->vt_mutex);` 만 잡고 `sess->conpty->resize` 를 호출 → 그 안에서 `impl_->vt_mutex` 를 다시 잡습니다 (라인 438~441) → reentrant 가 아니므로 OK.
- 그러나 `state->resize(...)` (라인 375) 는 **sess->vt_mutex 만 잡고 호출** 되며 `state->resize` 의 caller contract 는 "must hold vt_mutex" 입니다 (`render_state.h:113`). render thread 는 `session->conpty->vt_mutex()` (impl 측) 를 잡고 `start_paint` 를 호출하는데 (`ghostwin_engine.cpp:146`) — 즉 **state->resize 와 start_paint 가 서로 다른 mutex 로 보호되고 있습니다**. race condition 으로 _api / _p 의 cell_buffer 를 동시에 read/write 할 수 있습니다.
- 다만 race 는 보통 100% 재현이 아닌 stochastic 결과를 만드는데, 사용자 보고는 100% 재현이라 메인 가설로 삼기 어렵습니다.

### D — `gw_surface_resize` 가 atlas cell_width 0 으로 div-by-zero / cols=0 clamp (확신도 5)

- `src/engine-api/ghostwin_engine.cpp:594-602`: `cols = w / atlas->cell_width(); if (cols < 1) cols = 1;`. cell_width 가 0 일 가능성은 거의 없지만, 일시적인 1×1 layout pass 에서 cols 가 0 으로 계산되었다가 1 로 clamp 되는 경우 ghostty 가 1×1 reflow 를 한 번 거치게 됩니다. wraparound 모드에서 multi-row prompt 의 1×1 reflow 는 PageList 가 매우 긴 1열 buffer 로 reflow 되었다가 다시 새 cols 로 unwrap 될 때 일관성 보장을 잃을 가능성이 있지만 — 이는 ghostty 측 reflow 알고리즘에 대한 깊은 분석이 필요하므로 추측입니다.

## 약점

1. **WPF HwndHost 의 reparent 동작이 BuildWindowCore 를 재호출하는지 100% 확정되지 않음** — 본 가설 1 의 핵심 미싱 피스. 검증 방법: `RenderDiag` 로그를 split 시점에 캡쳐해서 동일 host 인스턴스에서 `buildwindow-enter` 가 2 회 이상 출력되는지 확인하면 됩니다 (코드는 라인 43~46 에 이미 인스트루먼트되어 있음). 빌드/실행/사용자 시나리오 재현이 필요한데 본 작업은 "Fix 금지, 원인 분석만" 이므로 실행 검증 불가.

2. **ghostty 의 reflow 알고리즘 자체를 검증하지 않음** — `external/ghostty/src/terminal/PageList.zig` 의 `pub fn resize` 는 매우 길고 복잡한 함수이며, 1×1 wrap → 새 cols unwrap 시퀀스가 cell 을 잃지 않는지 verify 하지 못했습니다. 대안 가설 A 의 약점.

3. **artifacts/round-ralph-3 의 다른 에이전트 분석 인용 금지** 규칙 때문에 cross-validation 이 본인 작업 범위 안에서만 가능. 다른 에이전트가 이미 같은 결론에 도달했거나 반박했을 수 있지만 확인 불가.

4. **사용자 보고에 "오른쪽 새 pane 은 정상" 인지 명시 없음** — 만약 새 pane 도 안 그려진다면 본 가설 1 은 약화되고 (새 pane 은 reparent 가 아니라 신규 생성), 대안 가설 A/D 가 강화됩니다. 만약 새 pane 만 정상이라면 본 가설 1 이 강화됩니다. 사용자 추가 정보 필요.

5. **`force_all_dirty` 가 매 프레임 호출되는데 왜 reflow 후 화면이 영구적으로 비어있는가** 가 본 가설 1 만으로는 완전 설명되지 않음 — 새 child HWND 위에 swapchain 이 없어서 그리지 않더라도, PowerShell 이 새 입력에 대해 새 prompt 를 그리면 여전히 안 보이는 이유는 swapchain 이 stale hwnd 에 묶여 있다는 것으로 설명됩니다. 그러나 이 검증을 위해서는 사용자가 split 후 왼쪽 pane 에 enter 키를 눌러도 새 prompt 가 안 보이는지 확인해줘야 합니다.

6. **추측 영역 명시**: 본 답변에서 BuildWindowCore 재호출 여부 자체는 **추측** 입니다. 진단 로그가 있는 환경에서 5초 정도면 검증 가능합니다.

## 읽은 파일

| # | 파일 | 라인 범위 |
|---|---|---|
| 1 | `C:/Users/Solit/Rootech/works/ghostwin/CLAUDE.md` | 전체 (system context) |
| 2 | `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/PaneContainerControl.cs` | 1-335 |
| 3 | `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/TerminalHostControl.cs` | 1-211 |
| 4 | `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/PaneLayoutService.cs` | 1-245 |
| 5 | `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Core/Models/PaneNode.cs` | 1-93 |
| 6 | `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.h` | 1-120 |
| 7 | `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` | 1-301 |
| 8 | `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/surface_manager.cpp` | 1-137 |
| 9 | `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` | 1-633 |
| 10 | `C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` | 1-500 |
| 11 | `C:/Users/Solit/Rootech/works/ghostwin/src/conpty/conpty_session.cpp` | 410-463 |
| 12 | `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_core.cpp` | 1-207 |
| 13 | `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_bridge.c` | 1-397 |
| 14 | `C:/Users/Solit/Rootech/works/ghostwin/external/ghostty/src/terminal/Terminal.zig` | 2827-2872 |
| 15 | `C:/Users/Solit/Rootech/works/ghostwin/external/ghostty/src/terminal/render.zig` | 263-649 |
| 16 | `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Diagnostics/RenderDiag.cs` | grep 1 line |
