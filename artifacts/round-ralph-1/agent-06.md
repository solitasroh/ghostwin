# 라운드1 에이전트06 원인 분석

## 결론 (한 문장, 20 단어 이내)
Grid 재배치 중 intermediate 작은 크기 resize 이벤트가 ghostty VT 를 1 셀 수준으로 축소시켜 primary screen reflow 로 프롬프트를 날림.

## 직접 확인한 증거 3 가지

1. `src\GhostWin.App\Controls\TerminalHostControl.cs:126-141` — `OnRenderSizeChanged` 가 `sizeInfo.NewSize.Width * dpi` 를 그대로 사용하고, 134-135 줄에서 `if (widthPx < 1) widthPx = 1` 으로 강제함. 즉 WPF layout pass 중 NewSize 가 0 이 되는 intermediate 단계가 있으면 `PaneResizeRequested(PaneId, 1, 1)` 이 그대로 올라감. Grid Children 이 reparent 되는 동안 WPF 가 min measure 또는 zero measure 를 주는 케이스가 흔해서, PaneContainer 가 BuildGrid 로 old host 를 새 Grid 로 옮기는 split flow 에서 이 경로가 100% hit 될 가능성이 높음. **이 경로는 "100% 재현" 증상과 일치함** (추측: WPF 의 정확한 layout sequence 는 추가 검증 필요).

2. `src\engine-api\ghostwin_engine.cpp:581-606` — `gw_surface_resize` 는 surface 에 대한 swapchain resize 뿐 아니라 593-602 줄에서 **해당 session 의 cols/rows 도 같이 작게 만드는 `eng->session_mgr->resize_session(surf->session_id, cols, rows)`** 를 부르고, 이 호출이 `src\session\session_manager.cpp:369-376` 의 `resize_session` → `sess->conpty->resize(cols, rows)` → `src\conpty\conpty_session.cpp:425-445` 의 `ResizePseudoConsole` + `impl_->vt_core->resize(cols, rows)` → `src\vt-core\vt_core.cpp:76-85` 의 `vt_bridge_resize` → ghostty `terminal_resize` 로 이어짐. 즉 1x1 로 들어온 WPF resize 이벤트가 그대로 ghostty VT 까지 도달함.

3. `external\ghostty\src\terminal\Terminal.zig:2827-2848` — `pub fn resize` 가 primary screen 에 대해 `.reflow = self.modes.get(.wraparound)` 로 `primary.resize` 를 호출함. wraparound 는 기본 ON 이므로 cols 가 80 → 1 로 극단적으로 줄면 기존 각 행은 1 글자 너비로 wrap 되어야 하고, rows 가 24 → 1 이 되면 viewport 한 줄에 cursor 가 있는 현재 행만 남고 나머지는 scrollback 으로 밀려나거나 버려짐. 또한 `src\vt-core\vt_bridge.c:100-104` 는 `ghostty_terminal_resize(terminal, cols, rows, 0, 0)` 로 cell_width_px / cell_height_px 를 0 으로 넘기고 있어 reflow 계산에 필요한 pixel 정보가 손실됨 (확실하지 않음: 0 이 reflow 자체를 깨뜨리는지는 ghostty 내부 로직 추가 확인 필요).

## 확신도 (0 부터 100 사이 숫자)

55

- Grid 재배치 → intermediate 작은 크기 resize → VT 축소 → reflow 손실이라는 **체인 자체는 코드상 명확**하지만, **WPF 가 split 시 실제로 (1px, 1px) 혹은 0 width 를 intermediate 로 주는지** 는 본인이 직접 WPF layout runtime 을 계측한 것이 아니라 코드 상의 defensive clamp (`widthPx < 1 → 1`) 의 존재로 추론한 것이므로 정황 증거 수준. 100% 재현이라는 증상과 잘 맞지만 "확정" 은 아님.
- CLAUDE.md Follow-up Cycles 항목 8 (`split-content-loss-v2`) 에 이미 "hotfix `4492b5d` 가 Grid layout 의 **shrink-then-grow** 연쇄에서 content 복구 못 함" 으로 기록되어 있고, 테스트 `test_resize_shrink_then_grow_preserves_content` 가 FAIL 한 empirical 기록이 있음. 내 분석은 그 failure 의 **상류 원인 (VT reflow)** 을 짚은 것이고 project context 의 "min() 기반 memcpy truncate" 설명과 **다른 층위**임. 즉 render_state 층 (내가 의심하는 VT reflow) 과 `render_state.cpp` 층 (project context 의 min memcpy 가설) 둘 다 성립할 수 있고, 어느 쪽이 100% 재현의 **primary** 인지 단정하기 어려움.

## 두 번째로 가능한 원인 (대안 가설)

`src\session\session_manager.cpp:369-376` `resize_session` 이 **Session::vt_mutex** 를 잡지만 render thread 의 `start_paint` 는 `src\engine-api\ghostwin_engine.cpp:146` 에서 **ConPtySession::vt_mutex** (다른 mutex) 를 잡음. CLAUDE.md "기술 부채 → vt_mutex 통합" 에도 이 이중 mutex 가 명시되어 있음. 이 경우 resize 도중 render thread 의 `for_each_row` callback 이 VT 의 일시적 resize mid-state 를 읽을 수 있고, `_api` row 에 일부 memcpy 가 일어난 뒤 VT 가 완전히 축소되어 다음 frame 에서 덮어써짐. 다만 race condition 은 통상 "100% 재현" 이 되기 어려워서 primary 보다는 secondary.

## 내 결론의 약점 (empirical 반박 가능 지점)

1. **WPF Grid reparent 가 실제로 NewSize.Width=0 intermediate 를 유발하는지 본인이 breakpoint 나 log trace 로 확인한 것이 아님**. `if (widthPx < 1) widthPx = 1` 의 존재만으로는 0 width 가 실제 run-time 에 온다는 증거가 되지 않음 (defensive code 는 hypothetical 케이스에도 달릴 수 있음). 반박 방법: `OnRenderSizeChanged` 에 `sizeInfo.NewSize.Width`, `sizeInfo.PreviousSize.Width`, stack trace 를 log 해서 split 직후 실제로 NewSize 가 0 또는 1 px 로 오는지 관측. 만약 always 절반 크기로만 온다면 내 가설 기각.

2. **내 분석은 ghostty reflow 결과 PowerShell 프롬프트가 scrollback 으로 밀려난다고 가정하지만, ghostty `primary.resize` 가 1x1 같은 극단 축소에서 정확히 어떤 결과를 돌려주는지** 본인이 ghostty 내부를 step 한 것이 아니라 `.reflow = ...` 한 줄로 유추함. 반박 방법: ghostty `Screen.resize` 소스를 열어서 새 rows 가 1 일 때 cursor 가 있는 line 을 preserve 하는지 truncate 하는지 확인, 또는 `GHOSTWIN_RESIZE_DIAG=1` 로 `start_paint` 의 `resize-diag` log 를 뽑아서 reshape 전/후 `_api.row(0)` text 가 실제로 비어있는지 관측 (이미 `src\renderer\render_state.cpp:28-298` 에 instrumentation 이 있음).

3. **CLAUDE.md "Option A backing buffer with max capacity" 언급과 내 가설의 관계가 애매함**. Option A 는 render_state 층에서 memcpy 로 content 를 보존하는 fix 인데, 내 가설 (VT reflow 가 root cause) 이 맞다면 Option A 로는 해결 안 됨 (source data 인 VT 자체가 이미 잃었기 때문). 반면 CLAUDE.md 는 Option A 를 "유력" fix 로 적었으므로, project 내 다른 분석은 reflow 보다 `_api` memcpy truncate 를 더 유력하게 봄. 이 불일치가 내 가설의 약점. 반박 방법: `TerminalRenderState::resize` 에 breakpoint 걸고 before/after `_api.frame()` 의 row 0 text 를 dump 해서, reshape **단독** 으로 content loss 가 발생하는지 / VT resize 후 start_paint 에서 loss 가 발생하는지 층위를 분리 관찰.

## 내가 직접 읽은 파일 목록

- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` : 1-302
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` : 1-121
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs` : 1-335
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs` : 1-211
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs` : 1-246
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` : 1-634
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` : 1-501
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` : 1-464
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` : 1-208
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` : 95-104 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.h` : 80-90 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\MainWindow.xaml.cs` : 1-422
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\WorkspaceService.cs` : 1-147
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\SessionManager.cs` : 1-96
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` : 199-320
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig` : 2807-2848 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\c\terminal.zig` : 380-440 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\c\main.zig` : 110-160 (grep context)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\include\ghostty\vt\terminal.h` : 669-825 (grep context)
