# 라운드1 에이전트09 원인 분석

## 결론 (한 문장, 20 단어 이내)
Ghostty Terminal.resize의 `shell_redraws_prompt=true` 경로가 prompt 라인을 clearCells로 지우고, start_paint가 straight memcpy로 `_api`를 빈 셀로 덮어쓴다.

## 직접 확인한 증거 3 가지

1. `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig:2844-2849` — Terminal.resize가 `primary.resize(.{.cols=.., .rows=.., .reflow=.., .prompt_redraw = self.flags.shell_redraws_prompt})`를 호출. PowerShell (PSReadLine)은 OSC 133 semantic prompt marking을 발행해 `shell_redraws_prompt = true`가 되는 경로가 존재.

2. `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Screen.zig:1708-1748` — Screen.resize의 prompt_redraw 분기: `.last`는 cursor 라인을 `clearCells(page, row, cells)`로 호출, `.true`는 promptIterator로 prompt 시작부터 아래로 이어지는 모든 row를 차례로 `clearCells`. 그리고 `Screen.zig:1449`의 `clearCells`는 `@memset(cells, self.blankCell())`로 셀을 blank (cp_count=0)로 초기화.

3. `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp:162-183` — start_paint의 for_each_row 콜백이 `if (dirty)`일 때 VT가 돌려준 cells를 `_api.row`에 straight memcpy. 주석(170-177)에 "defensive merge 제거됨 (2026-04-10, Round 2 합의)"으로 적혀 있고, Agent 6/7/10이 "clearCells → @memset(blankCell())가 cls/clear/ESC[K 정상 경로에서도 쓰인다"는 근거로 merge를 뺐다고 명시. 그 결과 resize 중 clearCells로 blank화된 prompt 영역이 dirty=true + cp_count=0 으로 보고되면 `_api`의 보존된 cell 내용을 blank로 덮어쓴다. 그 다음 `_api → _p` 복사(217-223)로 `_p` 역시 blank가 되어 Present 화면에 PowerShell 출력이 사라진다.

## 확신도
55

## 두 번째로 가능한 원인 (대안 가설)
Alt+V split 후 기존 TerminalHostControl이 새 Border로 재부모될 때 WPF HwndHost가 내부적으로 DestroyWindowCore → BuildWindowCore를 재실행하여 child HWND가 파괴·재생성되는 반면, `PaneLayoutService.OnHostReady` 의 `if (state.SurfaceId != 0) return;` 가드 때문에 새 HWND로 SurfaceCreate가 재호출되지 않아서 기존 surface의 swapchain이 이미 파괴된 stale HWND에 바인딩된 채 렌더링 결과가 화면에 나타나지 않는다.

## 내 결론의 약점
1. PowerShell이 OSC 133 prompt sequence를 실제로 emit하는지, 그리고 ghostty Terminal의 `flags.shell_redraws_prompt`가 실제 런타임에 true로 설정되는지를 코드 런타임 로그로 직접 확인하지 못했다.
2. Screen.resize의 `clearCells` 경로는 보통 "prompt 시작부터 아래로"만 지우므로, cursor 위쪽의 이전 명령 출력까지 사라진다는 사용자 증상을 완전히 설명하려면 추가 메커니즘이 필요하다.
3. defensive merge 제거 주석의 날짜(2026-04-10)가 오늘(2026-04-09)보다 하루 미래여서 이 수정이 실제로 현재 바이너리에 들어 있는지 직접 확인하지 못했다.
4. VT가 reshape 후 실제로 cp_count=0의 blank cell을 돌려주는 런타임 로그를 본인이 직접 확인한 것이 아니라 코드 경로만 추적했다.

## 읽은 파일
- src/GhostWin.App/MainWindow.xaml : 1-323
- src/GhostWin.App/MainWindow.xaml.cs : 1-422
- src/GhostWin.App/ViewModels/MainWindowViewModel.cs : 1-155
- src/GhostWin.Services/PaneLayoutService.cs : 1-245
- src/GhostWin.Core/Models/PaneNode.cs : 1-93
- src/GhostWin.App/Controls/PaneContainerControl.cs : 1-334
- src/GhostWin.App/Controls/TerminalHostControl.cs : 1-210
- src/engine-api/surface_manager.h : 1-85
- src/engine-api/ghostwin_engine.cpp : 90-330, 580-631
- src/session/session_manager.cpp : 90-140, 290-400
- src/conpty/conpty_session.cpp : 420-463
- src/renderer/render_state.cpp : 1-300
- src/vt-core/vt_core.cpp : 60-180
- external/ghostty/src/terminal/Screen.zig : 1440-1750
- external/ghostty/src/terminal/Terminal.zig : 2830-2880
- external/ghostty/src/terminal/render.zig : 270-385
