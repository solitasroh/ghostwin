# 라운드1 에이전트01 원인 분석

## 결론 (한 문장, 20 단어 이내)
Alt+V 분할 중 WPF Grid의 중간 1px 레이아웃 패스가 VT를 1×1로 축소시켜 prompt 내용이 빈 row로 덮어쓰여짐.

## 직접 확인한 증거 3 가지

1. **C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs : 126-141** — `OnRenderSizeChanged` 에서 layout pass 의 새 크기를 그대로 `PaneResizeRequested` 로 발행하고, 폭이 1 미만이면 1 로만 clamp 함. WPF Grid 가 split 생성 직후 intermediate layout pass 에서 왼쪽 column을 일시적으로 아주 작게 만들면, 이 이벤트가 1px 수준으로 그대로 전파됨. 이 이벤트는 `PaneContainerControl.OnPaneResized(308행)` → `PaneLayoutService.OnPaneResized` → `_engine.SurfaceResize` 경로로 흘러 `gw_surface_resize` 를 호출.

2. **C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp : 581-606** 의 `gw_surface_resize` 가 `eng->session_mgr->resize_session(surf->session_id, cols, rows);` 를 호출하고, cols/rows 는 `width_px / cell_width` 로 계산되며 최소 1 로 clamp 됨 (597-600 행). 이어서 **C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp : 369-376** 의 `SessionManager::resize_session` 이 `sess->conpty->resize(cols, rows)` 를 통해 ghostty VT 의 `vt_core->resize(1, 1)` 를 실행함. 즉 왼쪽 pane 의 VT 내부 Screen 이 실제로 1×1 로 축소되어 primary screen 의 prompt/output cell 이 축소된 view 밖으로 밀려남.

3. **C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp : 162-184** (`start_paint` 의 for_each_row 콜백) — Round 2 합의로 defensive merge 가 제거되어 straight memcpy 만 수행함. 주석 (167~177행) 스스로 "분할 직후 VT 가 cp_count=0 인 빈 row 를 돌려준다" 는 관측을 인정하면서도 "clear 경로도 빈 cell 을 쓴다" 는 이유로 merge 를 빼고 복귀시킴. 그 결과 shrink-then-grow 이후 VT 가 돌려주는 빈 row (cp_count=0) 가 `_api.row(row_idx)` 에 그대로 memcpy 되어, `RenderFrame::reshape` 의 capacity-backed storage 가 보존한 prompt cell 내용을 덮어씀.

## 확신도
78

## 두 번째로 가능한 원인 (대안 가설)
`SessionManager::resize_session` 이 `sess->vt_mutex` (Session 구조체 별도 mutex) 를 lock 하지만, render thread 는 `session->conpty->vt_mutex()` (ConPtySession 내부 별도 mutex) 를 사용함. 즉 두 mutex 가 다른 객체임. 따라서 render thread 의 `start_paint` 가 `state->resize` 와 병행 실행될 수 있고, `_api.reshape` / dirty_rows / cols/rows_count 변경이 race 를 일으킬 가능성.

## 내 결론의 약점
- Ghostty VT 의 `Terminal.resize()` 가 shrink-then-grow 시 실제로 primary screen content 를 날리는지, 아니면 scrollback 에 보존했다가 re-expose 하는지 Zig 소스를 직접 읽지 않았다.
- VT 가 돌려주는 빈 row 가 정확히 어떤 dirty 플래그를 갖는지는 런타임에 실측하지 않음.
- 2번 가설 (mutex race) 도 진짜 원인일 수 있음.

## 읽은 파일
- src/GhostWin.App/ViewModels/MainWindowViewModel.cs : 1-156
- src/GhostWin.Services/PaneLayoutService.cs : 1-246
- src/renderer/render_state.h : 1-121
- src/renderer/render_state.cpp : 1-302
- src/engine-api/ghostwin_engine.cpp : 100-180, 550-630
- src/session/session.h : 1-148
- src/session/session_manager.cpp : 1-500
- src/conpty/conpty_session.cpp : 1-100, 135-205, 425-460
- src/vt-core/vt_core.cpp : 70-150
- src/GhostWin.App/Controls/PaneContainerControl.cs : 1-335
- src/GhostWin.App/Controls/TerminalHostControl.cs : 1-211
