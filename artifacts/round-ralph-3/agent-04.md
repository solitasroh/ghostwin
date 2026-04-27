# 라운드3 에이전트04 원인 분석

## 결론 (한 문장)
`split-content-loss-v2` v2 fix (`render_state.cpp` 의 capacity-backed `RenderFrame`) 는 render_state 자체의 cell buffer 만 보호하지만, **매 frame `force_all_dirty()` → `start_paint` → `for_each_row` 가 ghostty Terminal 의 dirty row cells 를 그대로 `_api` 에 memcpy 하므로**, WPF Grid 의 split-pass 도중 `gw_surface_resize` 가 transient 작은 크기 (예: `(1,1)` 또는 매우 작은 px) 로 호출되어 `vt_core->resize` 가 ghostty Terminal 자체의 cell content 를 truncate/reflow 시킨 뒤, 후속 정상 크기 resize 시 ghostty 가 dirty 로 마크한 빈 row 를 callback 에 넘기면 capacity-backed buffer 의 내용이 zero 로 덮어씌워져 PowerShell 프롬프트가 사라집니다 — 즉 v2 fix 가 보호하는 layer (render_state) 와 손실이 발생하는 layer (ghostty terminal) 가 다릅니다.

## 증거 3 가지

### 증거 1 — 코드 v2 fix 는 적용되어 있지만 관찰 layer 가 잘못
`C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` 라인 87-125 의 `RenderFrame::reshape()` 와 라인 231-299 의 `TerminalRenderState::resize()` 는 design 문서 (`docs/02-design/features/split-content-loss-v2.design.md`) 의 D1~D14 결정대로 capacity-backed pattern 으로 구현돼 있고, 라인 274-276 에서 모든 logical row 를 dirty 로 mark 합니다. **그러나** 이 fix 는 `_api` 의 cell_buffer 자체만 보호하며, 다음 frame 에서 `_api` 의 데이터가 어떻게 update 되는지는 별개입니다.

### 증거 2 — 매 frame `force_all_dirty` + ghostty cell 그대로 memcpy
`C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` 라인 145-146:
```cpp
state.force_all_dirty();
bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);
```
`state.force_all_dirty()` 는 `_api.dirty_rows.set()` 만 합니다 (즉 `_api → _p` copy 가 매 frame 진행되도록 보장). 그러나 `_api` 자체의 cell content 는 `start_paint` 안의 `for_each_row` callback (`render_state.cpp` 라인 161-183) 에서 ghostty 의 row dirty bit 가 true 일 때만 update 됩니다:
```cpp
if (dirty) {
    _api.set_row_dirty(row_idx);
    auto dst = _api.row(row_idx);
    size_t copy_cols = std::min<size_t>(cells.size(), dst.size());
    std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData));
}
```
이 memcpy 는 ghostty 가 callback 에 넘긴 `cells` (즉 ghostty Terminal 안의 cell 데이터) 를 그대로 dst 에 덮어씁니다. ghostty 가 빈 cell 을 dirty=true 로 넘기면 PowerShell 프롬프트의 cap-protected backing 데이터도 zero 로 덮어씌워집니다.

### 증거 3 — split sequence 가 ghostty terminal 자체를 transient 작은 크기로 resize
`C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` 라인 369-376 의 `resize_session`:
```cpp
void SessionManager::resize_session(SessionId id, uint16_t cols, uint16_t rows) {
    auto* sess = get(id);
    if (!sess || !sess->is_live()) return;
    std::lock_guard lock(sess->vt_mutex);
    sess->conpty->resize(cols, rows);   // ★ ghostty Terminal.resize 호출
    sess->state->resize(cols, rows);
}
```
`conpty->resize` 는 `C:/Users/Solit/Rootech/works/ghostwin/src/conpty/conpty_session.cpp` 라인 425-441 에서 ResizePseudoConsole + `vt_core->resize(cols, rows)` 를 호출하고, `vt_core->resize` 는 `vt_bridge_resize` → `ghostty_terminal_resize` 를 호출합니다 (`src/vt-core/vt_bridge.c` 라인 100-104). ghostty Terminal.resize 는 cell content 를 reflow/truncate 합니다. 호출자는 `gw_surface_resize` (`src/engine-api/ghostwin_engine.cpp` 라인 581-606) 이며 라인 597-600 에서 cols/rows 가 cell_width/height 미만이면 `1` 로 clamp:
```cpp
uint16_t cols = static_cast<uint16_t>(w / eng->atlas->cell_width());
uint16_t rows = static_cast<uint16_t>(h / eng->atlas->cell_height());
if (cols < 1) cols = 1;
if (rows < 1) rows = 1;
eng->session_mgr->resize_session(surf->session_id, cols, rows);
```
호출자는 `PaneLayoutService.OnPaneResized` (`src/GhostWin.Services/PaneLayoutService.cs` 라인 232-238) 이고 그 위에는 `TerminalHostControl.OnRenderSizeChanged` (`src/GhostWin.App/Controls/TerminalHostControl.cs` 라인 126-141) 인데 WPF 의 Grid measure pass 에서 transient 작은 size (`sizeInfo.NewSize.Width` 또는 dpi-scaled px 가 cell_width 미만) 가 발생할 수 있고 이때 `cols=1, rows=1` 로 clamp 되어 ghostty Terminal 이 1×1 로 resize 됩니다. 또한 `docs/02-design/features/split-content-loss-v2.design.md` §2.1 의 Symptom Chain 자체가 "transient intermediate size (관찰: ~1x1)" 를 명시하고 있어 design 단계에서도 동일 시나리오가 식별됐음을 확인.

## 확신도 (0~100)
55

근거: render_state v2 fix 가 이미 적용된 것을 코드에서 확인했고, design 문서가 동일 증상을 commit 직전 단계에서 식별한 것 (`6141005`) 까지는 사실 입니다. v2 fix 가 capacity 만 보호하고 ghostty terminal 자체의 reflow/truncate 는 못 막는 것은 `start_paint` + `for_each_row` 코드 경로로 명백 합니다. 그러나 "ghostty 가 transient (1,1) 후에 정확히 어떤 cell 을 dirty 로 mark 해서 callback 에 넘기는가" 는 zig 코드 (`external/ghostty/src/terminal/Terminal.zig::resize`) 에 있고 직접 읽지 않았으므로 **추측 영역** 입니다. 또한 사용자가 "매번 100% 재현" 이라고 한 것이 v2 fix commit 이후 (즉 capacity-backed pattern 적용 후) 의 관찰인지 그 이전인지 부모가 명시하지 않아 확신도를 더 낮춥니다. 만약 v2 fix commit 이전 빌드에서 재현된 것이라면 원인은 `4492b5d` hotfix 의 single-call min() memcpy 자체 (이미 design 문서에 정확히 기술된 v1 원인) 이고 v2 fix 만 적용하면 해결될 가능성이 있어 진단이 달라집니다.

## 대안 가설

1. **v1 원인 (이미 design 문서에 명시)** — 빌드된 바이너리가 `4492b5d` hotfix 단계에 머물러 있고 v2 fix 가 아직 빌드/배포 되지 않은 상태라면, `TerminalRenderState::resize` 의 single-call min() 기반 memcpy 가 transient (1,1) 단계에서 buffer 를 1 cell 로 truncate 하고 후속 (50,30) 단계에서 복구 못 하는 v1 시나리오 (`docs/02-design/features/split-content-loss-v2.design.md` §2.1) 그대로. v2 fix 코드가 source tree 에 있어도 빌드 산출물 (`build/...` 또는 GhostWin.App.exe) 이 stale 일 가능성. **확인 방법**: `build_ghostwin.ps1` 로 클린 재빌드 후 재현 여부 확인.

2. **dual-mutex race (Session::vt_mutex vs ConPty::Impl::vt_mutex 분리)** — `session_manager.cpp:373` 가 `sess->vt_mutex` (Session level) 를 lock 하지만 `conpty->resize` 가 ConPty 내부의 별개 vt_mutex (`conpty_session.cpp:439`) 를 사용. render thread 의 `start_paint` 는 ConPty 의 vt_mutex 만 lock 합니다 (`ghostwin_engine.cpp:146`). 그래서 main thread 가 `state->resize` 진행 중일 때 render thread 가 `_api`/`_p` 에 동시 접근 가능 → race 로 reshape 도중의 buffer 에 memcpy 가 진행되어 데이터 손상. CLAUDE.md "기술 부채" 의 "vt_mutex 통합" TODO 와 일치. 100% 재현이 race 인 것이 어색하지만, split sequence 가 매 번 동일한 dispatch order 라면 race window 가 결정론적으로 hit 될 수 있음.

3. **WPF Grid 의 transient resize 자체가 안 일어남** — design 문서가 가정한 "intermediate ~1x1 pass" 가 실제 WPF Grid 의 measure 동작에서 발생하지 않을 수 있음. 그렇다면 v1/v2 모두 다른 원인이 진짜 — 예: `BuildElement` 의 host migration (sessionId match) 이 어떤 edge case 에서 실패해 새 host instance 가 생성되어 새 child HWND + 새 surface 가 만들어지고 이전 swapchain 의 frame 이 안 그려짐. PaneContainerControl.cs:201-212 의 sessionId 매칭 loop 는 `sessionId != 0` 조건이고 첫 pane 의 SessionId 가 어떤 경로에서 0 으로 reset 되면 매칭이 안 되어 새 host 가 생성됨.

4. **Render thread 가 split 직후 surface focus 가 newLeaf 로 옮겨가서 기존 left pane 의 swapchain 이 더 이상 active_surfaces 에 포함되지 않음** — `PaneLayoutService.SplitFocused` 가 `FocusedPaneId = newLeaf.Id` 로 설정 (라인 68). engine 측 `eng->focused_surface_id` 는 `gw_surface_focus` 호출에서만 업데이트되므로 left pane 의 surface 가 active 일 가능성은 있지만, render loop (`render_loop` ghostwin_engine.cpp:179-205) 가 `surface_mgr->active_surfaces()` 를 모두 iterate 하므로 left pane 도 포함되어야 함. 다만 `surface_mgr->active_surfaces` 의 정의를 직접 안 읽어서 추측 영역.

## 약점

1. **ghostty Terminal.resize (zig) 동작 미확인** — 가장 결정적인 증거인 "ghostty 가 transient resize 후 빈 cell 을 dirty=true 로 callback 에 넘기는가" 를 zig 코드로 직접 검증하지 않았습니다. `external/ghostty/src/terminal/Terminal.zig` 의 `resize` 함수와 row dirty 마크 정책을 읽어야 진짜 root cause 가 확정됩니다.

2. **"100% 재현" 의 빌드 시점 미확인** — 사용자가 본 빌드가 v2 fix (`render_state.cpp` 의 capacity-backed pattern) 를 포함하는지 확인하지 못했습니다. CLAUDE.md TODO row 8 에는 "Fix 후 uncomment" 라고 적혀 있어 fix 가 아직 안 된 것처럼 보이지만, 코드는 fix 가 들어가 있고 design 문서도 fix 의 final form 이 적용된 상태입니다. CLAUDE.md / memory / source tree 사이의 sync 가 완전치 않아 어느 빌드가 사용자 hardware 에 있는지 불명확.

3. **WPF Grid layout pass 의 transient size sequence 미관측** — design 문서가 가정한 "1x1 intermediate" 가 실제 WPF 의 measure/arrange 에서 발생하는지는 측정하지 않았습니다. 사용자 hardware 에서 `OnRenderSizeChanged` 의 `sizeInfo.NewSize` 를 로깅해야 100% 확인됩니다.

4. **Window/Workspace level 호출 경로 일부 미확인** — `WorkspaceService.cs` 의 ActivateWorkspace 경로, surface_mgr->active_surfaces() 의 정확한 정의, `surface_mgr->resize` 의 deferred swap chain ResizeBuffers 와 cell content 의 상호작용을 모두 추적하지 않아 가설 4 (대안가설) 검증이 부족.

5. **dual-mutex race 의 결정론성** — 가설 2 의 race 가 100% 재현이 되려면 dispatch order 가 매번 동일해야 하는데, 실제로 그런지는 멀티 스레드 dispatcher 에서 보장되지 않습니다. 그래서 가설 2 는 100% 재현 증상에는 약합니다 (intermittent crash 같은 sporadic 증상에는 더 적합).

## 읽은 파일

- `C:/Users/Solit/Rootech/works/ghostwin/CLAUDE.md`
- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.h`
- `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/MainWindow.xaml.cs`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/PaneContainerControl.cs`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/TerminalHostControl.cs`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/PaneLayoutService.cs`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/SessionManager.cs` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/WorkspaceService.cs` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Core/Models/PaneNode.cs`
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Interop/EngineService.cs` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Interop/NativeEngine.cs` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` (라인 100-205, 247-300, 379-417, 580-633)
- `C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` (라인 85-160, 296-396)
- `C:/Users/Solit/Rootech/works/ghostwin/src/session/session.h`
- `C:/Users/Solit/Rootech/works/ghostwin/src/conpty/conpty_session.h` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/conpty/conpty_session.cpp` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_core.h` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_core.cpp`
- `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_bridge.c` (grep 부분)
- `C:/Users/Solit/Rootech/works/ghostwin/docs/02-design/features/split-content-loss-v2.design.md` (라인 1-300)
