# 라운드3 에이전트07 원인 분석

## 결론 (한 문장)

Alt+V split → WPF Grid layout 이 left pane 에 SizeChanged 를 발생시키면 `gw_surface_resize` 경로가 `TerminalRenderState::resize` 를 호출하고, **ghostty VT 의 `for_each_row` 가 "row-level dirty bit 가 set 된 row 만" cell 데이터를 reports 한다는 사실** 과 결합하여 — `_api/_p.cell_buffer` 가 (어떤 형태로든) zero/truncate 된 상태가 되면 PowerShell 은 다음 프롬프트가 그려질 때까지 redraw 신호를 보내지 않으므로 left pane 은 clear color 만 Present 한다. 실제로 사용자가 보는 것은 **WPF 출력 폴더의 ghostwin_engine.dll 이 working tree 의 capacity-backed reshape fix 보다 23분 오래된 옛 binary** 라는 점이다 (즉 사용자는 working tree 의 fix 를 실행하고 있지 않음).

## 증거 3 가지

### 증거 1 — `4492b5d` commit message 가 root cause 자체를 진술함

`git show 4492b5d` (Apr 9 05:27 +0900) 의 메시지:

> ghostty VT's `for_each_row` only reports cell data on rows the VT marks as dirty, and a bare terminal-size change does NOT mark every row dirty (PowerShell, for example, only redraws on the next prompt), the render loop would run for many frames against an empty `_api/_p` and the surface would Present nothing but clear color.
>
> This was the "분할 시 처음 열린 세션이 사라지고 분할된 것만 나와" symptom reported after the first-pane-render-failure archive: splitting triggers `gw_surface_resize` -> `session_mgr::resize_session` -> `state->resize()` on the oldLeaf's existing session, which wiped the preserved-session content even though the session itself (conpty + vt) still held the real text.

이 메시지의 모든 claim 을 cross-check 했고 일치한다 (`render_state.cpp:140-184` for_each_row dirty-only memcpy + `session_manager.cpp:374-375` resize_session + `gw_surface_resize` body in `ghostwin_engine.cpp:601`).

### 증거 2 — Resize 호출 chain 확인 (코드 직접 read)

```
WPF Grid SizeChanged
  └─ TerminalHostControl.PaneResizeRequested
      └─ PaneContainerControl.OnPaneResized          (PaneContainerControl.cs:308)
          └─ PaneLayoutService.OnPaneResized         (PaneLayoutService.cs:232)
              └─ EngineService.SurfaceResize         (EngineService.cs:124)
                  └─ gw_surface_resize               (ghostwin_engine.cpp:581)
                      ├─ surface_mgr->resize         (deferred swapchain resize)
                      └─ session_mgr->resize_session (ghostwin_engine.cpp:601)
                          ├─ vt_mutex lock
                          ├─ conpty->resize          → ResizePseudoConsole + vt_core->resize
                          └─ state->resize           → TerminalRenderState::resize
                                                     (render_state.cpp:231)
```

`TerminalRenderState::resize` 가 cell_buffer 를 어떻게 다루느냐가 결정적 지점이다. `start_paint` (`render_state.cpp:151-184`) 의 `if (dirty)` gate (line 162) 는 ghostty 가 dirty 로 marking 하지 않은 row 에는 절대 쓰지 않으므로, resize 직후 `_api` 가 비어 있고 ghostty 가 dirty bit 를 set 하지 않으면 영원히 비어 있다 — 다음 prompt redraw 까지는.

### 증거 3 — Build artifact mtime 비교: 사용자는 옛 DLL 을 실행 중

```
src/renderer/render_state.cpp           2026-04-10 05:25:44   (capacity-backed Option A 패턴)
src/renderer/render_state.h             2026-04-09 19:16:50   (cap_cols/cap_rows 추가)
build/ghostwin_engine.dll               2026-04-10 05:29:38   (working tree 에서 build 됨)
src/GhostWin.App/bin/x64/Release/
        net10.0-windows/
        ghostwin_engine.dll             2026-04-10 05:06:56   ← WPF 가 실행 시 load 하는 DLL
src/GhostWin.App/bin/x64/Release/
        net10.0-windows/
        GhostWin.App.exe                2026-04-09 19:48:12
```

WPF 출력 폴더의 `ghostwin_engine.dll` (05:06) 은 `build/ghostwin_engine.dll` (05:29) 보다 **23분 오래되었다**. `scripts/build_wpf.ps1` (또는 `build_wpf_poc.ps1`) 가 working tree 변경 후 실행되지 않아서 **DLL copy step 이 누락**된 상태이다 (이건 `feedback_wpf_build_script.md` 에 정확히 기록된 known footgun 이다 — `build_ghostwin.ps1`/`dotnet build` 만으로는 DLL copy 가 일어나지 않는다).

`git status -s`:
```
 M src/renderer/render_state.cpp
 M src/renderer/render_state.h
 M tests/render_state_test.cpp
```

→ working tree 에 capacity-backed reshape fix 가 **uncommitted** 로 있고, build 폴더에는 들어갔으나 WPF 출력 폴더에는 propagate 되지 않았다. 사용자가 실행하는 DLL 은 어떤 이전 빌드 (메모리에 기록된 `4492b5d` hotfix 만 들어간 buffer-loss-v1 fix 정도 또는 그 이전) 이며, 그 이전 빌드의 동작이 `4492b5d` commit message 가 진술한 그대로 — left pane clear color, 100% 재현 — 이다.

### 보조 증거 — 코드의 in-source 진술

`render_state.h:30-38` (working tree):

> This layout fixes the split-content-loss-v2 regression: the WPF Grid layout's shrink-then-grow chain during Alt+V split would drop the old buffer on the intermediate ~1x1 `resize()` call when the storage was sized to the logical dims (4492b5d hotfix). With capacity-backed storage, shrink becomes a metadata-only update and the subsequent grow simply re-exposes the still-present cells.

`render_state.cpp:165-184` (working tree):

> defensive merge 제거됨 (2026-04-10, Round 2 합의). Round 1 (2026-04-10) 에서 cell-level merge 를 넣었던 이유는 분할 직후 VT 가 cp_count=0 인 빈 row 를 돌려준다는 가정이었으나, Agent 6/7/10 이 Screen.zig:1449 clearCells → @memset(blankCell()) 을 근거로 cls/clear/ESC[K/scroll/vim/tmux 의 정상 경로 또한 동일한 cp_count=0 cell 을 사용한다는 것을 empirical 로 확인했다. 따라서 defensive merge 는 정상 clear/erase 경로를 깨뜨린다. 원래의 straight memcpy 로 복귀.

이 두 코멘트는 (1) 이전 라운드에서 동일 버그를 다른 각도로 분석했고 (2) 가설 한 가지가 empirical 로 reject 되었고 (3) 현재 채택된 fix 는 capacity-backed Option A 라는 사실을 본 에이전트와 독립적으로 입증한다.

## 확신도 (0~100)

**88**

### 88 인 이유
- Root cause mechanism (ghostty for_each_row dirty-only + PowerShell no-redraw + state->resize buffer wipe) 은 `4492b5d` commit 에 명시되어 있고, 코드를 직접 read 하여 chain 을 모두 검증했다.
- Build artifact mtime 비교는 fact 이고 의심할 여지가 없다 — WPF 가 실행하는 DLL 은 working tree fix 가 들어가 있지 않다.
- Working tree 의 capacity-backed Option A 패턴은 split-content-loss-v2 regression (CLAUDE.md Follow-up #8) 의 정식 fix 와 일치하며 in-source 코멘트가 이를 명시한다.

### 12 가 빠진 이유
- 사용자가 실행 중인 binary 의 정확한 commit hash 를 직접 확인하지 못했다 (DLL 은 PE timestamp 가 있지만 read 안 함). 옛 빌드에 들어간 fix 의 *세부 동작* 이 `4492b5d` 의 row-by-row memcpy 인지 그 이전인지 100% 확신할 수 없다 — 그러나 어느 쪽이든 user-visible 결과 (left pane clear color) 는 동일하다.
- 가설은 `4492b5d` 의 commit message 라는 같은 시점의 evidence 에 강하게 의존한다. 그 진술 자체가 misdiagnosis 였을 가능성은 낮지만 0 은 아니다 (메모리에 기록된 first-pane-render-failure 사이클은 여러 차례 reroot 가 있었다).
- WPF 측 PaneContainerControl.BuildElement (line 197-211) 의 host migration 이 항상 sessionId fallback 으로 reuse 되는지는 host 의 SessionId field 초기값에 따라 결정되는데, host.SessionId 가 0 이 아닌 진짜 sessionId 로 set 되는 path (BuildElement line 226 또는 line 208 의 PaneId reassign) 만 검증했다. 첫 워크스페이스 생성 직후 split 이라면 host.SessionId 가 적절히 set 되어 있는지에 약한 의존이 있다.

## 대안 가설

### 대안 A — Host detach race (확률 ~6%)

`PaneContainerControl.BuildElement` line 218-219 에서 `host.Parent is Border previousBorder → previousBorder.Child = null` 로 detach 하고, line 234-241 에서 새 Border 의 Child 로 재부착한다. 이 detach/reattach 사이에 HwndHost 가 child HWND 에 대해 SetParent → DestroyWindow → Recreate 를 트리거하면 Direct3D swap chain 도 재바인딩되어 첫 frame 이 clear color 일 수 있다. 그러나 이 가설은 "PowerShell 다음 프롬프트도 안 그려진다" 와 "100% 재현" 을 모두 설명하기 어렵다. 다음 input 시 redraw 가 일어나야 하기 때문이다. 또한 사용자 보고는 "출력 내용이 사라진다" 이지 "frame 한 번 깜빡인다" 가 아니다.

### 대안 B — surface_mgr deferred resize 의 swap chain 해제 (확률 ~3%)

`gw_surface_resize` 의 `surface_mgr->resize` (line 591) 는 deferred resize (pending_w/h + needs_resize flag) 인데, 이 flag 가 다음 render loop iteration 에서 swap chain 을 release 하고 재생성하는 동안 한두 frame 빈 화면이 보일 수 있다. 그러나 sustained 100% 재현은 설명하지 못한다.

### 대안 C — vt_mutex 이중 잡힘에 의한 deadlock 또는 partial state (확률 <1%)

`session_manager.cpp:305` 가 `Session::vt_mutex` 를 잡고 `sess->conpty->resize` 를 호출 → `conpty_session.cpp:438-441` 에서 다른 mutex (`Impl::vt_mutex`) 를 잡는다. 이는 다른 mutex 인스턴스이므로 deadlock 은 아니지만 thread-safety invariant 가 흐트러질 수 있다. 그러나 사용자가 보는 결정론적 100% 재현 증상에는 부합하지 않는다 (race 라면 가끔 success 가 있어야 함).

## 약점

1. **사용자 실행 중인 DLL 의 정확한 source state 를 cross-check 하지 못함**: PE header 의 link timestamp 또는 string scan 으로 fix 가 들어간 시점을 확인하면 100% 확정 가능하다. 이번 라운드에서는 mtime 만으로 inference 했다.

2. **PowerShell 의 redraw 행동에 대한 가정**: 메시지 인용한 "PowerShell only redraws on the next prompt" 는 일반적으로 true 지만, ConPty 가 SIGWINCH 유사 signal 을 보냈을 때 PowerShell 이 어떻게 반응하는지 직접 검증하지 않았다. ghostty 가 자체 backing store 를 보존하므로 in-process 로는 문제가 안 되지만 본 분석에서는 ghostty 동작을 commit message 에 맡겼다.

3. **다중 라운드 분석의 frozen-in-time 위험**: `feedback_hypothesis_verification_required.md` 가 명시하듯, "이전 라운드 합의" 도 재검증 대상이다. `render_state.cpp:165-184` 의 "Round 1/2 합의" 코멘트가 *오늘* (2026-04-10) 시점으로 표시되어 있는 점 (현재 명목 날짜는 2026-04-09) 은 라운드 진행이 시간을 앞당겨 진행되고 있다는 신호이며, 그 합의 자체에 추가 round 가 필요할 수 있다.

4. **WPF 측 host lifecycle (대안 A) 을 deeply 검증하지 않음**: TerminalHostControl 의 BuildWindowCore / DestroyWindowCore 와 D3D11 swap chain rebind 흐름은 read 하지 않았다. capacity-backed reshape 가 fix 되면 자연 검증되겠지만 본 분석에서는 미확인 영역이다.

5. **`tests/render_state_test.cpp` 의 어떤 test 가 working tree 에서 통과/실패하는지 확인하지 않음**: 569 lines 추가된 test 가 capacity-backed Option A 의 검증에 성공했는지가 확인되어야 fix 의 신뢰도가 100% 가 된다.

## 읽은 파일

```
C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h
C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp
C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp
        (offset 320~410, 560~640)
C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp
        (offset 280~400)
C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp
        (offset 410~470)
C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp
        (offset 60~160)
C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c
        (offset 90~115)
C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs
C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs
C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs

git show 4492b5d (commit message + body)
git show 4492b5d:src/renderer/render_state.cpp (line 50~120)
git status -s (working tree state)
git log --oneline -25 -- src/renderer/render_state.{h,cpp} tests/render_state_test.cpp
ls/stat 으로 build artifact mtime 비교
        build/ghostwin_engine.dll
        build/CMakeFiles/renderer.dir/src/renderer/render_state.cpp.obj
        src/GhostWin.App/bin/x64/Release/net10.0-windows/ghostwin_engine.dll
        src/GhostWin.App/bin/x64/Release/net10.0-windows/GhostWin.App.exe
        src/GhostWin.App/bin/Release/net10.0-windows/ghostwin_engine.dll
        src/GhostWin.App/bin/Release/net10.0-windows/GhostWin.App.exe
        src/GhostWin.App/bin/Debug/net10.0-windows/ghostwin_engine.dll
```
