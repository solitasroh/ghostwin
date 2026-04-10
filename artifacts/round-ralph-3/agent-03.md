# 라운드3 에이전트03 원인 분석

## 결론 (한 문장)
Alt+V split 직후 oldLeaf(왼쪽 pane) 의 child HWND 가 약 절반 폭으로 OnRenderSizeChanged 를 받아 SurfaceResize → ghostty `Terminal.resize` 가 호출되는데, ghostty 의 `Screen.resize` 는 default `shell_redraws_prompt=.true` 로 인해 cursor 가 output 이 아니면 prompt 행 cell 을 `clearCells` 로 비워 버리고, 그 다음 PowerShell 이 OSC 133 semantic prompt 를 보내지 않으므로 prompt 를 redraw 하지 않아 사용자에게는 "PowerShell 프롬프트와 출력이 사라진" 것으로 보인다.

## 증거 3 가지

### 증거 1 — ghostty 의 default 가 prompt 를 적극적으로 지움
파일: `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig`
- L89: `shell_redraws_prompt: osc.semantic_prompt.Redraw = .true,` ← **default = .true**
- L2843~2849: `Terminal.resize` 가 primary screen 의 `Screen.resize` 를 호출할 때 `.prompt_redraw = self.flags.shell_redraws_prompt` 그대로 전달

파일: `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Screen.zig`
- L1708~1750: `if (opts.prompt_redraw != .false and self.cursor.semantic_content != .output)` 분기에서 `.true` 의 경우 `promptIterator(.left_up, …)` 로 prompt 시작점을 찾고 그 아래 모든 row 를 `clearCells(page, row, cells)` 로 빈 cell 로 덮음.
- L2858: `self.flags.dirty.clear = true;` — 추가로 화면 clear flag 를 세움.
즉 ghostty 는 매 resize 마다 cursor 가 output 행이 아니면 prompt 라인을 적극적으로 비워 버린다.

### 증거 2 — split 직후 oldLeaf 가 반드시 사이즈 변화 + resize_session 호출을 받음
파일: `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- L244~296: `BuildElement` 가 split 분기에서 `Grid` 를 새로 만들고 `RowDefinition`/`ColumnDefinition` 에 `node.Ratio (0.5)` Star + `GridSplitter` 4px + `(1−Ratio)` Star 를 셋업한 뒤 oldLeaf 의 host 를 column 0 / row 0 에 reparent. 즉 split 전에는 ContentControl 의 Content 자리에서 100% 폭이었던 oldLeaf 가 split 후엔 약 50% 폭으로 줄어듬.
- L218~220: 기존 host 가 reparent 되더라도 child HWND 자체는 살아남지만, 새 Grid 의 layout pass 에서 사이즈가 변경되므로 `TerminalHostControl.OnRenderSizeChanged` 가 fire 됨.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs`
- L126~141: `OnRenderSizeChanged` 가 새 px 사이즈로 `SetWindowPos` 한 뒤 `PaneResizeRequested?.Invoke(this, new(PaneId, widthPx, heightPx))` 발사.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- L232~238: `OnPaneResized` → `_engine.SurfaceResize(state.SurfaceId, widthPx, heightPx)` 로 그대로 native 위임.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`
- L581~606: `gw_surface_resize` → `cols = w/cell_width, rows = h/cell_height` 로 환산 후 `eng->session_mgr->resize_session(surf->session_id, cols, rows)`.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp`
- L369~376: `resize_session` → `sess->vt_mutex` lock → `conpty->resize(cols, rows)` + `state->resize(cols, rows)`.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp`
- L425~445: `ConPtySession::resize` → `ResizePseudoConsole` + `impl_->vt_core->resize(cols, rows)` → 즉 ghostty `Terminal.resize` 가 split 직후 oldLeaf 에 대해 반드시 한 번 호출됨.

이 흐름은 어떤 hotfix 도 우회 불가. 즉 split 시 oldLeaf 의 ghostty Terminal 에서 `Screen.resize` 의 prompt_redraw 분기는 100% 발화한다.

### 증거 3 — PowerShell 은 default 로 OSC 133 을 보내지 않음 + RenderState capacity hotfix 의 범주 밖
파일: `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp`
- L73~77: shell auto-detect 가 `pwsh.exe` → `powershell.exe` → `cmd.exe` 순서. plain pwsh/powershell 은 OSC 133 (`ESC ] 133 ; A/B/C/D`) 을 native 로 발송하지 않음 (PSReadLine 의 PromptText/oh-my-posh/starship 등 prompt 모듈을 별도 구성하지 않은 이상). 따라서 ghostty 의 `cursor.semantic_content` 는 `.output` 으로 transition 되지 않은 채 default 상태로 머물고, `Screen.resize` 의 prompt clearCells 분기가 항상 hit 됨.

파일: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- L87~125 + L231~299: `RenderFrame::reshape` 의 capacity-backed Option A hotfix 는 `_api`/`_p` snapshot buffer 의 storage 만 보존. ghostty Terminal/Screen 의 cell buffer 는 별개이며 reshape 와 무관. 즉 ghostty 가 cell 을 비우면 다음 `start_paint` 에서 ghostty 의 빈 row 가 `for_each_row` 로 그대로 들어와 `_api` 의 해당 row 를 빈 cell 로 덮어쓴다 (defensive merge 가 Round 2 합의로 제거됨, L167~179 주석 참고). capacity hotfix 는 WPF Grid 의 shrink-then-grow 1×1 → restore 시 _api/_p 의 cell 손실만 막을 뿐, ghostty 자체의 prompt clear 는 막지 못한다.

## 확신도 (0~100)
**75**.

근거가 강한 부분 (확실): ghostty 의 `shell_redraws_prompt=.true` default + `Screen.resize` 의 clearCells 코드 경로 + WPF split 시 oldLeaf 의 OnRenderSizeChanged → SurfaceResize 흐름은 코드로 명확히 검증됨.

추측이 섞인 부분: (1) PowerShell 이 OSC 133 을 보내지 않는다는 점은 일반적 사실이지만 사용자 환경에서 PSReadLine 7.x 의 PromptText 셋업, oh-my-posh 등 외부 prompt 모듈이 깔려 있는지는 미확인 — 만약 깔려 있으면 `cursor.semantic_content` 가 `.input` 일 수 있으므로 분기는 여전히 hit 되지만 동작이 다를 수 있음. (2) ghostty 가 `clearCells` 후에 PowerShell 이 정말 redraw 하지 않는지는 empirical 검증 안 됨 — `ResizePseudoConsole` 후 PowerShell 이 어떤 escape 를 돌려보내는지 확인하려면 conpty 의 RX 로그를 봐야 함. (3) 사용자 영상/스크린샷이 없어 "사라진" 정확한 증상 (cell 자체가 빈 글자로 덮였는지, _p 가 비어 검은 화면인지, render path 가 skip 되는지) 의 시각적 매칭은 미검증.

## 대안 가설

### H-A — Defensive merge 제거의 부작용 (확률 중)
2026-04-10 Round 2 합의로 `start_paint` 의 cell-level defensive merge 가 제거되었음 (`render_state.cpp` L167~179). 코멘트는 "Screen.zig:1449 clearCells → @memset(blankCell()) 이 cls/clear/ESC[K/scroll/vim/tmux 정상 경로에도 동일하므로 defensive merge 는 정상 erase 를 깨뜨린다" 고 적혀 있으나, **resize 시 ghostty 의 prompt clearCells 도 동일한 빈 cell 을 사용**하므로 우리는 그것을 정상 동작으로 받아들이고 있음. 이 가설은 본 결론과 결국 같은 root cause 의 다른 표현일 수 있음.

### H-B — RenderState capacity hotfix 가 여전히 불완전 (확률 낮~중)
CLAUDE.md TODO row 8 (`split-content-loss-v2`) 이 여전히 OPEN 으로 적혀 있고 메모리 인덱스에도 "Unit test `test_resize_shrink_then_grow_preserves_content` empirical FAIL 확정 후 main() 호출 주석 처리" 라고 기록됨. 그러나 `render_state.h` 의 주석은 Option A capacity-backed 으로 이미 fix 됐다고 주장. 두 기록이 모순. 이 모순이 풀리지 않은 채 production 코드가 부분적 fix 라면, capacity 보존이 안 되는 경계 조건 (예: 새 dim 이 `cap` 을 초과하는 grow path 의 row 단위 memcpy 에서 stride 불일치) 이 있을 수 있음. 다만 이 path 는 split 직후 폭이 줄어드는 경우엔 hit 되지 않으므로 이번 증상의 main suspect 는 아님.

### H-C — TerminalHostControl reparent 시 child HWND lifetime race (확률 낮)
PaneContainerControl 의 `BuildElement` 는 host 의 Border parent 를 detach 하고 새 Border 에 attach. WPF HwndHost 는 reparent 시 내부적으로 child HWND 의 parent HWND 를 SetParent 로 변경. 만약 그 사이 BuildWindowCore/DestroyWindowCore 의 race 가 있다면 swapchain 이 일시적으로 lost 될 수 있음. 그러나 `_initialHost` 폐기 + Option B 단일 owner 로 first-pane-render-failure 사이클에서 거의 닫혔음. 그래도 split 시점에서는 dispose 가 deferred Background priority 로 처리되므로 (PaneContainerControl L156~168) 일시적 race 가 아주 작은 시간창에 존재할 수 있음.

### H-D — `dirty.clear=true` flag 가 두 번째 부수효과를 만들고 있음 (확률 낮)
ghostty `Terminal.resize` L2858 이 `self.flags.dirty.clear = true` 를 세팅. 이를 우리가 어떻게 처리하는지 (renderer clear vs no-op) 확인 필요. 만약 우리 vt_bridge 가 이 flag 를 읽어서 _api/_p 를 자체 clear 한다면 두 번째 손실 path. 본 분석에서 vt_bridge 의 `dirty.clear` handling 코드는 확인하지 않았음.

## 약점

1. **Empirical 검증 부재**: 본 분석은 정적 코드 트레이싱에만 의존. `GHOSTWIN_RESIZE_DIAG=1` 을 켜고 split 을 재현해서 `[resize-diag] before/after` 라인의 `_api[total]` 변화 + `first text row` 변화를 보면 한 번에 확정 가능. 분석자는 빌드/실행 권한이 없거나 사용하지 않았음.
2. **ghostty 의 `cursor.semantic_content` 초기값과 transition 규칙 미확인**: `.output` 이 default 인지, `.input`/`.prompt` 가 default 인지에 따라 분기 hit 여부가 바뀜. PowerShell 이 OSC 133 없이도 어떤 시점에 transition 되는지 미확인.
3. **`shell_redraws_prompt` 의 OSC payload format 미확인**: `osc.semantic_prompt.Redraw` enum 의 정확한 set 시점 미확인. shell 이 명시적으로 `.false` 로 set 하지 않는 한 default `.true` 가 유지되는지 확인 필요.
4. **PowerShell 의 resize 후 prompt redraw 동작 미검증**: 정말로 redraw 하지 않는지 conpty RX 로그로 확인하지 않음. ConPty + PSReadLine 환경에서 SIGWINCH 에 가까운 동작이 발생할 수도 있음.
5. **CLAUDE.md TODO 와 코드 주석의 모순 해석 미해결**: row 8 split-content-loss-v2 가 OPEN 으로 적혀 있는데 코드 주석은 Option A 로 fix 됐다고 함. 어느 쪽이 최신 상태인지 확정 못함.
6. **다른 PDCA agent 분석 미참조** (룰): 이 영역은 이미 라운드 1~2 에서 분석된 정황이 있을 수 있는데, 룰 상 인용 금지여서 reuse 불가.
7. **본 분석은 oldLeaf (왼쪽) 만 사라지는 이유를 잘 설명하지만 newLeaf (오른쪽) 가 정상으로 보이는 시나리오를 직접 검증하지 않았음**: newLeaf 도 BuildWindowCore 에서 처음 만든 사이즈로 SurfaceCreate → 곧바로 측정/배치 후 SurfaceResize 가 있을 수 있음. 그래도 newLeaf 는 새 PowerShell 인스턴스라 prompt 가 막 spawn 되어 정상 상태일 가능성이 높음.

## 읽은 파일

- `C:\Users\Solit\Rootech\works\ghostwin\CLAUDE.md` (system context)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (gw_surface_resize 영역)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (resize_session)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (ConPtySession::resize + shell auto-detect)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` (VtCore::resize)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` (vt_bridge_resize → ghostty_terminal_resize)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig` (resize, shell_redraws_prompt default)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Screen.zig` (resize, prompt clearCells 분기)
