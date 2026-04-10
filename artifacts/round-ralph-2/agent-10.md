# 라운드2 에이전트10 원인 분석

## 결론 (한 문장, 20 단어 이내)
Split 직후 VT가 빈 cells를 dirty로 돌려주어 start_paint가 `_api` row를 blank로 덮어쓴다.

## 증거 3 가지 (파일:라인 + 확인 내용)

1. **`src/renderer/render_state.cpp:162-184`** — `start_paint()`의 row copy 로직.
   - line 162 `if (dirty)` → line 165 `auto dst = _api.row(row_idx)` → line 178 `std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData))`.
   - defensive merge가 제거된 **straight memcpy**. VT가 dirty+빈 cells를 돌려주는 순간 `_api`의 해당 row가 무조건 blank로 덮임. `copy_cols = min(cells.size(), dst.size())`이므로 VT cells 전체 길이만큼 덮어씀.

2. **`src/renderer/render_state.cpp:167-177`** — Round 2 합의 주석이 **문제를 그대로 증언**.
   - 원문 인용: "Round 1 (2026-04-10) 에서 cell-level merge 를 넣었던 이유는 **분할 직후 VT 가 cp_count=0 인 빈 row 를 돌려준다는 가정**이었으나, Agent 6/7/10 이 Screen.zig:1449 clearCells → @memset(blankCell()) 을 근거로 cls/clear/ESC[K/scroll/vim/tmux 의 정상 경로 또한 동일한 cp_count=0 cell 을 사용한다는 것을 empirical 로 확인했다. 따라서 defensive merge 는 정상 clear/erase 경로를 깨뜨린다. 원래의 straight memcpy 로 복귀."
   - 즉 코드베이스 자체가 **"split 직후 VT가 빈 row를 돌려주는 현상"을 과거에 관측했었고, merge fix가 제거되었음**을 명시. 증상과 정확히 일치.

3. **`src/engine-api/ghostwin_engine.cpp:145-146`** — render 루프가 매 프레임 **무조건 `force_all_dirty` + `start_paint`**를 호출.
   - `state.force_all_dirty();` → `_api.dirty_rows.set()` (모든 row 강제 dirty).
   - `state.start_paint(...)` → `_p.dirty_rows = _api.dirty_rows` + `_api → _p` 전체 row 복사.
   - 따라서 일단 `_api`의 한 row가 blank로 덮이면 **다음 프레임에 `_p`까지 전파**되어 화면에 그대로 출력. PowerShell이 `SIGWINCH` 이후 prompt를 redraw 하기 전까지는 blank 상태 고정.

보조 증거 — `src/GhostWin.App/Controls/TerminalHostControl.cs:126-141` `OnRenderSizeChanged` → `PaneResizeRequested` → `PaneLayoutService.OnPaneResized` (`src/GhostWin.Services/PaneLayoutService.cs:232-238`) → `_engine.SurfaceResize` (`src/GhostWin.Interop/EngineService.cs:124-125`) → native `gw_surface_resize` (`src/engine-api/ghostwin_engine.cpp:581-606`) 는 session cols/rows를 재계산해 `session_mgr->resize_session` → `conpty->resize` → `vt_core->resize` → `vt_bridge_resize` → `ghostty_terminal_resize`를 호출. **Alt+V 직후 왼쪽 pane의 HwndHost가 새 사이즈로 layout되면서 이 경로가 트리거**됨. 그 직후의 첫 `start_paint`가 VT가 리턴하는 빈 dirty row를 그대로 `_api`에 덮는다.

## 확신도 (0~100)
72

근거: 경로가 paper trail로 완결되어 있고, 코드 주석이 과거 관측을 명시적으로 기록. 다만 "VT가 split 직후 blank cells를 실제로 돌려준다"는 empirical 확인은 이 세션에서 직접 로그로 재현하지 않았음 (`GHOSTWIN_RESIZE_DIAG=1` + `start-paint-diag` 카운터로 확인 가능하지만 실행하지 않음). 주석은 "Round 2 합의에서 이 가정이 반박되었다"고 쓰고 있는데, 그럼에도 사용자가 여전히 100% 재현한다는 사실은 **반박이 불완전**했거나 **다른 clear 경로 (ghostty `terminal_resize` 내부의 erase)가 동일한 blank cell을 정당하게 방출**하고 있음을 시사.

## 대안 가설 1
**`RenderFrame::reshape` slow path (`render_state.cpp:104-124`) — `copy_cols = std::min(cols, cap_cols)` 의 shrink-then-grow-beyond-cap 순서 의존성**. CLAUDE.md 의 follow-up row 8 (`split-content-loss-v2`) 과 `artifacts/split-content-loss-v2/` 가 이미 이 케이스를 HIGH priority 로 기록 중이며, `test_resize_shrink_then_grow_preserves_content` 가 empirical FAIL 상태로 evidence commit (`6141005`). 단, 이 케이스는 WPF Grid 가 **cap 을 초과할 만큼 grow** 해야 발동하는데 Alt+V split 만으로는 보통 cap 이내에서 수축→복원이 일어나므로 증거 #2 의 straight-memcpy 경로보다 우선순위가 낮다고 판단.

## 약점 1
- **ghostty `terminal_resize` (`vt_bridge.c:100-104` → `ghostty_terminal_resize`) 의 실제 반환 cell 상태를 직접 관측하지 않았음**. Screen.zig:1449 인용은 render_state.cpp 의 주석을 재인용한 것이며 ghostty 소스는 이 세션에서 열지 않았음 ⇒ **추측 요소 존재**.
- "사용자는 100% 재현" + "주석은 문제가 반박됨" 사이의 모순을 empirical 로 해소하지 않음. 사용자가 최신 빌드가 아닐 가능성, `4492b5d` 하프픽스만 적용된 빌드일 가능성, 또는 Round 2 합의가 실제로는 불완전했을 가능성 중 **어느 것인지 확인 안 함**.
- `SessionManager::resize_session` (`session_manager.cpp:369-376`) 이 `sess->vt_mutex` 를 잡고 `conpty->resize` 는 내부 `impl_->vt_mutex` 를 잡는 이중 mutex 구조가 살아있음 (`render_surface` 는 `conpty->vt_mutex()` 사용). 이로 인한 race 가 blank cell 을 `_api` 에 들여보낼 가능성은 배제 못 했음.

## 읽은 파일 목록
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Interop\EngineService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\surface_manager.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (부분 — resize, create_session)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (부분 — resize)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` (부분 — resize, for_each_row)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` (부분 — vt_bridge_resize)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\SessionManager.cs`
