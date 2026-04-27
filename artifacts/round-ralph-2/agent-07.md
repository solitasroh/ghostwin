# 라운드2 에이전트07 원인 분석

## 결론 (한 문장, 20 단어 이내)
Alt+V split 후 Grid 레이아웃 체인이 왼쪽 pane을 resize 시켜 ghostty VT resize가 PowerShell viewport 내용을 scrollback으로 밀어내 사라지게 한다.

## 증거 3 가지 (파일:라인 + 확인 내용)

### 증거 1: Alt+V → SplitFocused → PaneLayoutChangedMessage → BuildGrid 전체 재구성 경로 확인
- `src/GhostWin.App/MainWindow.xaml.cs:335-342` — `Alt+V` (Key.System + actualKey==V) 가 `_workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical)` 를 직접 호출.
- `src/GhostWin.Services/PaneLayoutService.cs:48-71` — `SplitFocused` 가 새 session 생성, 새 paneId 2개 allocate 후 `PaneNode.Split`으로 트리 재구성, 마지막에 `_messenger.Send(new PaneLayoutChangedMessage(_root))` 발행.
- `src/GhostWin.App/Controls/PaneContainerControl.cs:76-83, 135-141` — `Receive(PaneLayoutChangedMessage)` 가 `BuildGrid(root)` 호출 → `Content = BuildElement(root, oldHosts)` 로 **ContentControl 의 Content 를 완전히 새 Grid 로 교체**. 기존 Border → Grid(ColumnDefinitions 0/Auto/2) 로 구조가 바뀜 (line 244-296).
- 왼쪽 pane 의 `TerminalHostControl` 은 host migration 로직 (line 197-212, `sessionId` 매칭) 으로 **instance reuse** 되지만 parent Border → 새 Grid Column 0 아래의 새 Border 로 reparent 된다 (line 216-239).

### 증거 2: Layout pass 에서 왼쪽 host 가 shrink 되어 OnRenderSizeChanged → PaneResizeRequested → gw_surface_resize → ghostty VT resize 까지 동기 호출된다
- `src/GhostWin.App/Controls/TerminalHostControl.cs:126-141` — `OnRenderSizeChanged` 가 호출되면 무조건 `SetWindowPos` 후 `PaneResizeRequested?.Invoke(this, new(PaneId, widthPx, heightPx))` 발행. Alt+V 시 **왼쪽 pane 은 부모 width 의 약 50% 로 shrink** 됨 (Ratio=0.5, ColumnDefinitions line 274-278).
- `src/GhostWin.Services/PaneLayoutService.cs:232-238` — `OnPaneResized` → `_engine.SurfaceResize(state.SurfaceId, widthPx, heightPx)`.
- `src/engine-api/ghostwin_engine.cpp:581-606` — `gw_surface_resize` 가 atlas cell 크기로 cols/rows 계산 후 `eng->session_mgr->resize_session(surf->session_id, cols, rows)` 를 동기 호출.
- `src/session/session_manager.cpp:369-376` — `resize_session` 은 `sess->vt_mutex` 잡고 `sess->conpty->resize(cols, rows)` + `sess->state->resize(cols, rows)` 를 차례로 수행.
- `src/conpty/conpty_session.cpp:425-445` — `ConPtySession::resize` 내부에서 **`ResizePseudoConsole` + `impl_->vt_mutex` 밑에 `impl_->vt_core->resize(cols, rows)` 를 또 호출**한다. 이 경로가 ghostty `vt_bridge_resize` → `ghostty_terminal_resize((GhosttyTerminal)terminal, cols, rows, 0, 0)` 으로 이어진다 (`src/vt-core/vt_bridge.c:100-104`). 즉 왼쪽 pane 이 layout 으로 shrink 되는 즉시 ghostty screen 이 물리적으로 cols/rows 를 줄여버린다.

### 증거 3: render_state 쪽 Option A hotfix 는 이미 적용되어 있어 `_api`/`_p` cell buffer 는 보존되지만, VT→`_api` 재읽기 경로에서 VT가 반환하는 row 가 이를 덮어쓸 수 있다
- `src/renderer/render_state.cpp:87-125, 231-299` — `RenderFrame::reshape` 는 `cap_cols`/`cap_rows` 고수위 유지(line 101-102) + `new_c <= cap_cols && new_r <= cap_rows` 면 metadata-only(line 92-96). `TerminalRenderState::resize` 는 `_api.reshape` / `_p.reshape` 후 모든 logical row 를 dirty 마킹(line 274-276). 주석(line 238-244) 에 "Fixes split-content-loss-v2 ... 4492b5d hotfix" 로 **Option A 가 commit 되어 있음** 을 확인.
- `src/renderer/render_state.cpp:151-184` — `start_paint` 은 `vt.for_each_row` 콜백에서 **`if (dirty) { std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData)); }`** — dirty row 면 VT 가 돌려준 값이 그대로 `_api.row(row_idx)` 를 **덮어쓴다**. defensive merge 는 2026-04-10 Round 2 합의로 제거됨 (line 168-177 주석).
- `src/vt-core/vt_core.cpp:92-151` — `for_each_row` 는 `cells_buf.resize(impl_->cols)` (line 100) 로 **새 cols** 기준 버퍼를 만들고, `vt_bridge_cell_iterator_next` 로 ghostty 가 돌려주는 현재 screen cell 을 그대로 기록. dirty 는 `vt_bridge_row_is_dirty(row_iter)` 결과 그대로 사용. 즉 ghostty 가 resize 이후 row 를 dirty 로 리포트하고 그 row 의 cell 을 (cp_count=0 로) 돌려주면, Option A 가 메타데이터상 보존해둔 cell 이 start_paint 첫 호출에서 **blank cell 로 덮어쓰여진다**. PowerShell 이 resize 이벤트에 대해 자동 redraw 를 하지 않는 조건 + ghostty 가 resize 후 viewport 를 clear 하고 모든 row 를 dirty 로 리포트하는 경로가 합쳐지면, 왼쪽 pane viewport 가 비워진 상태로 보이게 된다. (ghostty 내부 resize 가 정확히 이 순서로 동작하는지는 ghostty 서브모듈을 직접 확인하지 않았으므로 "추측" 이다 — 최종 덮어쓰기의 발원지 확정은 증거 1, 2 의 "Alt+V → 동기 VT resize → start_paint" 파이프라인까지만 empirical.)

## 확신도 (0~100)
55

- **55 점인 이유**: 증거 1, 2 의 "Alt+V → SplitFocused → Grid rebuild → 왼쪽 host shrink → OnRenderSizeChanged → gw_surface_resize → ConPty::resize → vt_core::resize → ghostty_terminal_resize" 동기 호출 체인은 코드로 직접 확인 완료 (high confidence, ~90%). 그러나 "**왜 결과적으로 viewport 가 비어 보이는가**" 를 설명하는 세 경로 중 어느 것이 실제 reproduction 의 dominant cause 인지는 empirical log 없이 확정 불가:
  - (a) ghostty_terminal_resize 가 viewport content 를 scrollback 으로 이동 + 모든 row 를 blank+dirty 로 리포트 → `_api` 덮어쓰기 (증거 3 의 추측 경로).
  - (b) ghostty_terminal_resize 는 content 를 보존하지만 PowerShell 이 WM_SIZE/SIGWINCH 에 반응해 스스로 viewport 를 `ESC[2J` clear 함.
  - (c) Grid layout 초기 pass 에서 intermediate tiny size (~1 cell) 로 resize 가 한 번 먼저 들어오고 (shrink-then-grow), ghostty VT 가 1x1 에서 row buffer 자체를 truncate 한 뒤 grow 시점에는 content 복원 못 함. render_state.cpp 는 Option A 로 metadata-preserving 이지만 ghostty VT 쪽 buffer 는 ghostty 가 관리하므로 별개.
- CLAUDE.md "Follow-up Cycles row 8 — split-content-loss-v2 HIGH" 가 바로 이 regression 을 이미 open TODO 로 등록해두고 있고, Option A 가 적용되었음에도 "shrink-then-grow chain ... min() 기반 memcpy 가 shrink 한 번으로 content 를 truncate" 라고 **경로 (c) 로 의심을 표기** 한다. 단 이것은 render_state.cpp 기준 서술이고, 현재 파일에는 Option A 가 commit 되어 있어 **render_state.cpp 자체의 min() shrink 문제는 이미 해결됨**. 따라서 현재 버그가 여전히 재현된다면 잔존 원인은 **ghostty VT 쪽 내부 buffer** 나 **PowerShell 자체 동작** 일 가능성이 크다 (경로 a 또는 b). 이 판단은 추측.
- 증거 1, 2, 3 의 파이프라인이 버그의 **표면적 trigger** 임은 근거가 충분하지만, **root cause 의 최종 발원지** 는 (a)/(b)/(c) 중 어느 것인지 확정을 위해 GHOSTWIN_RESIZE_DIAG=1 실측 로그 + ghostty_terminal_resize 소스 확인이 필요하다. 이 때문에 확신도를 55 로 낮춤.

## 대안 가설 1
**호스트 reparent 과정에서 왼쪽 pane 의 child HWND 가 새 Border 안에 재부착되기 전, 부모 Grid 가 파괴되면서 `DestroyWindowCore` 가 잠깐 호출되고 새 HWND 가 생성되어 기존 swap chain / surface 가 끊긴 채 빈 HWND 만 그려진다.**
- 근거 후보: `PaneContainerControl.cs:156-170` 의 "Dispose is *deferred* via Dispatcher.BeginInvoke at Background priority" 주석 자체가 이 타이밍 버그의 역사를 암시. 만약 `liveHosts.Contains(host)` 판정이 잘못되면 왼쪽 host 가 dispose 큐에 들어갈 수 있다.
- 반박 근거: 동일 파일 line 197-212 의 `sessionId` 기반 migration fallback 과 line 232 의 `_hostControls[node.Id] = host` 가 reuse 된 host 를 새 키로 등록하므로, `liveHosts = new HashSet(_hostControls.Values)` 계산 시 reused host 는 live 셋에 포함되어야 한다. 검증 안 된 경계 조건은 없어 보이지만 100% 확신은 아님. "추측".

## 약점 1
**ghostty 서브모듈 자체의 `ghostty_terminal_resize` 동작을 직접 읽지 않았다** — 증거 3 의 "ghostty 가 resize 후 모든 row 를 blank+dirty 로 리포트한다" 는 핵심 연결 고리가 **empirical verification 없는 추측**이다. `external/ghostty/src/terminal/...` 의 resize 경로를 읽지 않고는 경로 (a) vs (b) vs (c) 를 구분할 수 없고, 따라서 이 분석은 "어느 component layer 까지가 원인인지" 까지만 확정 가능하다 (shrink 가 왼쪽 pane 의 VT resize 를 동기로 촉발한다는 것까지는 확실). 또한 실제 GHOSTWIN_RESIZE_DIAG=1 로그나 reproduction 영상/스크린샷을 수집하지 않아 Option A hotfix 가 실전에서 이 bug 를 여전히 재현하는지 아닌지도 empirical 로 확인 못 했다.

## 읽은 파일 목록
- `C:\Users\Solit\Rootech\works\ghostwin\CLAUDE.md` (system-reminder 로 제공된 프로젝트 인덱스)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\MainWindow.xaml.cs` (lines 310-370)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (lines 580-634)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (lines 293-400)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (lines 420-463)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` (lines 70-200)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` (lines 90-110)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
