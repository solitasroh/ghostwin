# 라운드1 에이전트03 원인 분석

## 결론 (한 문장, 20 단어 이내)
ghostty `Screen.resize` 의 `prompt_redraw=.true` 경로가 왼쪽 pane resize 중 prompt 라인 cell 을 clearCells 로 삭제함.

## 직접 확인한 증거 3 가지

1. `external/ghostty/src/terminal/Screen.zig:1698-1750` — `if (opts.prompt_redraw != .false and self.cursor.semantic_content != .output)` 블록이 resize 수행 **직전** 에 prompt 라인의 모든 cell 에 대해 `self.clearCells(page, row, cells)` 를 호출. `.true` 브랜치 (1723-1748) 는 `promptIterator(.left_up)` 로 prompt 시작점을 찾은 뒤 거기부터 `rowIterator(.right_down)` 로 내려가면서 **여러 row 의 cell 을 space 로 덮어씀**. `@memset(blankCell())` 경로 (render_state.cpp:172-173 주석의 Screen.zig:1449 와 동일). 따라서 Alt+V 로 왼쪽 pane 이 resize 되는 순간 VT 자체가 prompt + 그 이후 row 의 cell 을 `cp_count=0` 으로 지워버림.

2. `external/ghostty/src/terminal/Terminal.zig:2827-2849` — `Terminal.resize` 가 `primary.resize` 에 `.prompt_redraw = self.flags.shell_redraws_prompt` 를 전달. `Terminal.zig:89` 에서 `shell_redraws_prompt: osc.semantic_prompt.Redraw = .true` 로 **기본값이 `.true`**. PowerShell 이 OSC133 semantic prompt 를 내보내는 한 이 flag 가 유지되어 resize 마다 위 clear 경로가 hit 됨.

3. `ghostwin_debug.log:11,43,190` 와 `:45-51` 등의 surface 별 frame 로그에서 **split 직후 왼쪽 surface 의 text quad count 가 69 에서 16 (초기 UI chrome 수준) 으로 영구 복귀** 하는 패턴이 관찰됨 (예: frame 150 `text=16 size=806x1152` 와 frame 151 `text=69 size=806x1152` 가 교차 — 두 surface 가 번갈아 찍히면서 한쪽만 cell 이 사라졌음을 보여줌). `ghostwin_engine.cpp:115-171 render_surface` 는 매 프레임 `state.force_all_dirty()` 후 VT 로부터 다시 읽어오므로, VT 가 blank cell 을 돌려주는 즉시 `_api` 가 덮어써지고 `_p` → swapchain 에도 그대로 전파됨. `render_state.cpp` Option A (backing capacity) 는 `_api`/`_p` **자체** 의 버퍼 shrink/grow 에는 강건하나, **VT 가 cell 을 직접 지우는 이 경로를 막지 못함**.

## 확신도 (0 부터 100 사이 숫자)
60

## 두 번째로 가능한 원인 (대안 가설)
`SessionManager::resize_session` 과 render thread 간 **이중 mutex race** (`session_manager.cpp:373` 의 `sess->vt_mutex` ↔ `ghostwin_engine.cpp:146` 의 `session->conpty->vt_mutex()`). resize 시 `state->resize` 는 `sess->vt_mutex` 로 보호되지만 render thread 의 `state.start_paint` 는 ConPty 내부 mutex 를 잡는다. 두 mutex 가 다르므로 render thread 가 `_api.cell_buffer` 를 읽고 있는 동안 UI thread 가 `std::move` 로 교체하면 torn read 가 발생할 수 있음. CLAUDE.md `TODO — 기술 부채` 의 "vt_mutex 통합" 항목과 동일. 다만 이 race 가 실제로 "prompt 만 깔끔하게 사라짐" 같은 symptom 을 만드는지는 직접 empirical 로 확인 불가 — 보통 torn read 는 crash 또는 garbage cell 로 나타나지 cleanly blank 로 나타나지는 않음.

## 내 결론의 약점 (empirical 반박 가능 지점)
1. **"출력 내용 (command output) 도 함께 사라진다"** 는 사용자 증상을 prompt_redraw 가설만으로 완전히 설명 못 함. Screen.zig:1709 의 `self.cursor.semantic_content != .output` 조건은 cursor 가 **output 라인이 아닐 때만** clear 를 수행. 즉 output 자체는 보존돼야 함. 사용자가 본 "출력 내용" 이 실제로는 prompt 근처의 영역인지 아니면 scrollback 인지 불명확.
2. PowerShell 이 Windows ConPty 의 `ResizePseudoConsole` 에 반응하여 prompt 를 **실제로 redraw 하는지 안 하는지** 를 직접 로그/strace 로 확인하지 않음. 만약 PowerShell 이 즉시 redraw 한다면 clearCells 직후 다시 cell 이 채워져서 symptom 이 발현되지 않음 — 이 경우 가설 기각.
3. `ghostwin_debug.log` 의 `text=16` → `text=69` 교차 패턴이 "왼쪽 pane 만 text=16" 이라고 단정할 직접적인 surface_id 로그가 없음. `render_surface` 는 surface pointer 만 찍고 pane id 는 안 찍으므로 어느 surface 가 어느 pane 인지 log 만으로 100% 확정 불가.
4. `GHOSTWIN_RESIZE_DIAG=1` 로 재현한 `resize-diag` 로그가 부재. Option A 의 capacity/cols 값 추이를 empirical 로 안 봄.

## 내가 직접 읽은 파일 목록
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp : 1-301
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h : 1-120
- C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp : 90-290, 440-634
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp : 280-430
- C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp : 410-463
- C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp : 60-100
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\terminal_window.cpp : 180-240
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs : 1-334
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs : 1-210
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs : 1-245
- C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig : 2827-2870 (pub fn resize)
- C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Screen.zig : 1-80, 1695-1800 (resize + prompt_redraw)
- C:\Users\Solit\Rootech\works\ghostwin\ghostwin_debug.log : 1-399 (split surface 렌더 프레임 로그)
