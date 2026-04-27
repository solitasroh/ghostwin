# 라운드2 에이전트06 원인 분석

## 결론 (한 문장, 20 단어 이내)
HwndHost reparent 로 child HWND 재생성되지만 OnHostReady 가 기존 SurfaceId 때문에 새 HWND 를 engine 에 재등록하지 않아 swapchain 이 죽은 hwnd 에 묶여 왼쪽 pane 이 비어 보인다.

## 증거 3 가지 (파일:라인 + 확인 내용)

1. **`src/engine-api/surface_manager.cpp:27-28, 65-86`** — `SurfaceManager::create`가 `CreateSwapChainForHwnd(device_, surf->hwnd, ...)` 로 swapchain 을 hwnd 에 **영구 바인딩**하고 `surf->hwnd = hwnd` 로 저장. `Grep "surf->hwnd =" **/*.cpp` 결과 이 파일 70 라인 단 1건 — **hwnd 를 갱신하는 경로가 전혀 없음**. swapchain 은 최초 hwnd 에 종속.

2. **`src/GhostWin.Services/PaneLayoutService.cs:191-198`** — `OnHostReady(paneId, hwnd, ...)` 가 `if (state.SurfaceId != 0) return;` 로 early return. host 가 재사용되어 다시 fire 될 때 이미 SurfaceId 가 할당되어 있으므로 **새 hwnd 는 engine 에 절대 전달되지 않음**. 코멘트도 "Already created — silent OK (정상 경로)" 로 재진입 자체를 정상 경로로 가정.

3. **`src/GhostWin.App/Controls/PaneContainerControl.cs:135-242`** — `BuildGrid` 가 `Content = BuildElement(...)` 로 visual tree 를 교체하고, 재사용 host 는 `previousBorder.Child = null` (218-219) 으로 이전 Border 에서 detach 후 새 Border 의 `Child = host` (234-239) 로 re-attach. `TerminalHostControl.cs:38-117` BuildWindowCore 는 `CreateWindowEx(..., hwndParent.Handle, ...)` 로 WPF visual parent HWND 에 종속된 child HWND 를 만들고, DestroyWindowCore (119-124) 는 `DestroyWindow` 호출 — HwndHost 의 reparent 시 파괴/재생성 순서가 정확히 이 경로를 탐.

## 확신도 (0~100)
70

이유: 1 · 2 는 코드에서 직접 확인(OnHostReady early-return + hwnd 비갱신). 3 의 마지막 고리 — **WPF HwndHost 가 logical/visual parent 변경 시 자동으로 DestroyWindowCore→BuildWindowCore 를 호출한다** — 는 WPF 프레임워크 내부 동작이라 본 세션에서 직접 코드로 확인하지 못했음 (**추측** 부분). 이 고리가 성립하지 않으면 증상의 다른 원인일 수 있음. 또한 hotfix 후 regression 이 여전하다는 "100% 재현" 증상을 본인이 실행으로 확인하지 못함 (로그/런타임 관찰 없이 코드만 읽음).

## 대안 가설 1
**`TerminalRenderState::resize` 의 `set_row_dirty` 가 cap_rows 보다 큰 새 rows 에 대해 조용히 drop** — `render_state.h:58` `is_row_dirty` 와 `set_row_dirty` 는 `constants::kMaxRows` 상한으로만 가드, reshape grow 경로 (render_state.cpp:266-276) 는 먼저 `_api.reshape` → 그 다음 `for (r = 0; r < rows; r++) _api.set_row_dirty(r)` 로 새 rows 를 마킹. 이 순서 자체는 맞지만, 만약 `_p` 의 cap 은 확장되었어도 `start_paint` 의 row copy (217-223) 가 `_api.is_row_dirty(r)` 만으로 필터되면 **dirty 마킹된 새 rows 는 VT 가 아직 data 를 돌려주지 않은 상태에서 memcpy 가 비어있는 _api 셀을 _p 로 밀어냄** → _p 가 blank 로 덮임. 단, 이건 "왼쪽 pane 의 기존 PowerShell 프롬프트" 가 이미 memcpy 전에 _api 안에 있었는지에 따라 갈림. 관찰 부족으로 배제 못함.

## 약점 1
- **실행 미확인**: 본 조사에서 실제 실행/로그를 확인하지 않고 코드만 읽음. `GHOSTWIN_RESIZE_DIAG=1` 환경변수로 실행해 `resize-diag` 라인의 `_api[total=N->M]` 을 관찰하면 render_state 경로에서 content 가 살아남는지 1회로 판정 가능 (증거1 가설) 하지만 수행 안 함.
- **WPF HwndHost reparent 동작 추측**: 증거3 의 마지막 고리는 프레임워크 일반 지식 기반 추측. 반례: Border.Child 재할당만으로는 visual parent HWND 가 바뀌지 않고 BuildWindowCore 가 재호출되지 않을 가능성 — 이 경우 원인은 완전히 다른 곳에 있음.
- **Grid measure/layout 1x1 중간 상태 vs. backing-capacity fix** 의 상호작용 미검증: `render_state.h:29-38` 주석은 이미 Option A backing capacity pattern 으로 shrink-then-grow 를 cover 한다고 claim 하지만 MEMORY.md 는 `split-content-loss-v2` 가 **pending** 이라고 기록. 두 문서가 모순 — 코드와 메모리 중 어느 쪽이 최신인지 본 조사로는 결정 불가.

## 읽은 파일 목록
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (전체 302 라인)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (전체 121 라인)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs` (전체 335 라인)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs` (전체 246 라인)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs` (전체 211 라인)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\surface_manager.cpp` (전체 137 라인)
- Glob: `**/PaneContainerControl*`, `**/PaneLayoutService*`, `**/render_state*`, `**/TerminalHostControl*`, `**/engine*.cpp`, `**/surface*.cpp`
- Grep: `SurfaceCreate|surface_create|bind_surface` (*.cs), `surf->hwnd\s*=` (*.cpp), `UpdateHwnd|ReparentSurface|rebind_surface|bind_hwnd` (all), `SplitFocused|Alt.*V|Vertical` (MainWindow.xaml.cs), `Vertical` (PaneNode.cs), `reparent|BuildWindowCore.*twice|re-create.*hwnd` (all)
