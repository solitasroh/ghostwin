# 라운드2 에이전트03 원인 분석

## 결론 (한 문장, 20 단어 이내)

Alt+V split 시 WPF Grid shrink-then-grow가 `session_mgr::resize_session`을 연쇄 호출해 ghostty VT cols를 축소→복원시키고, 직후 `start_paint`의 dirty-row memcpy가 VT에서 비워진 cell을 `_api`에 덮어써 왼쪽 pane 내용이 사라진다 (render_state Option A fix로도 커버되지 않는 상위 경로).

## 증거 3 가지 (파일:라인 + 확인 내용)

### 증거 1 — resize chain: `SurfaceResize → session_mgr::resize_session → ConPty::resize + state->resize`

`src/engine-api/ghostwin_engine.cpp:581-606` `gw_surface_resize`는 `surface_mgr->resize` 이후 `eng->session_mgr->resize_session(surf->session_id, cols, rows)`를 호출. `src/session/session_manager.cpp:369-376` `SessionManager::resize_session`:
```cpp
std::lock_guard lock(sess->vt_mutex);
sess->conpty->resize(cols, rows);    // ResizePseudoConsole + vt_core->resize
sess->state->resize(cols, rows);      // TerminalRenderState::resize
```
즉 WPF의 `OnRenderSizeChanged → PaneResizeRequested → OnPaneResized → SurfaceResize` 경로에서 호출되는 한 번의 resize가 **VT 자체도 resize**함 (`src/conpty/conpty_session.cpp:425-444`의 `ConPtySession::resize`가 `ResizePseudoConsole + impl_->vt_core->resize`). 따라서 Grid layout이 shrink-then-grow 시퀀스를 일으키면 VT cols도 그 시퀀스를 받음.

### 증거 2 — `ghostwin_debug.log`의 text count 드롭 (empirical, 앱 자체 로그)

`ghostwin_debug.log` (2026-04-10 05:54 실행):
- line 35 `frame 26: total=7661 bg=7592 text=69 size=1612x1146` — split 직전, full-size session 0, 69개 text cell (prompt + 내용)
- line 41 `Created session 1 (total: 2)` — Alt+V 분할 발생
- line 43 `gw_surface_create: hwnd=0x00440588 session=1 806x1152` — 오른쪽 pane 생성 (half width)
- line 45 `frame 31: total=3812 bg=3796 text=16 size=806x1152` — **text가 69→16으로 급락**
- line 45~65 전체 `text=16`으로 지속
- line 66 `frame 52: total=3865 bg=3796 text=69` — PowerShell이 새 prompt 그리면서 text 복원 (session 1 쪽 prompt)

즉 엔진 자체 diag가 frame 단위로 "rendered text cell count"를 기록하는데, split 직후 cell count가 급감함. 이건 `_api`/`_p` 버퍼에 실제로 empty cell이 들어간 증거. HwndHost 단순 reparent 문제라면 engine-side text count는 유지되어야 함.

### 증거 3 — `start_paint` dirty-row 덮어쓰기 경로 + VT가 resize 시 dirty 마크

`src/renderer/render_state.cpp:151-184` `start_paint`:
```cpp
vt.for_each_row([...](uint16_t row_idx, bool dirty, std::span<const CellData> cells) {
    if (row_idx >= _api.rows_count) return;
    if (dirty) {
        _api.set_row_dirty(row_idx);
        auto dst = _api.row(row_idx);
        size_t copy_cols = std::min(cells.size(), dst.size());
        std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData));
    }
});
```
그리고 `src/vt-core/vt_core.cpp:92-151` `VtCore::for_each_row`는 line 100에서 `cells_buf.resize(impl_->cols)`로 **VT의 현재 cols**에 맞춰 span 크기 결정, line 143에서 callback에 전달. VT가 `resize`(cols=작은값) 후 복원하면서 모든 row를 dirty로 마킹하고 빈 cell을 돌려주면, 위 memcpy가 `_api.row`를 **비어있는 VT cells로 overwrite**함. `src/renderer/render_state.cpp:231-299` `TerminalRenderState::resize`가 Option A capacity-backed reshape로 `_api`/`_p` buffer를 preserve해도, 다음 `start_paint` 한 번이 VT에서 읽어와 그대로 덮어씀. 즉 현재 Option A fix는 `_api/_p` 내부 경로만 커버하고, **VT→_api 재복사 경로**는 막지 못함.

추가 맥락: `CLAUDE.md` Follow-up Cycles row 8 "split-content-loss-v2"가 HIGH pending으로 명시돼 있고, `project_split_content_loss_v2_pending.md` §Fix strategies에서 Option A 단독으로는 부족하고 Option B (VT-level refill on grow) 등이 필요함을 이미 저자가 예상. `git diff HEAD --stat src/renderer/` 결과 render_state.cpp +279 lines uncommitted — Option A가 HEAD에 commit 되지 않은 worktree 변경. 05:29에 빌드된 obj + 05:54에 실행된 log 모두 Option A 포함 빌드이며, 그 빌드에서도 증상 재현.

## 확신도 (0~100)

72

- 증거 1 (파일 경로/라인): 100% — 직접 Read 확인
- 증거 2 (engine log text drop): 90% — log 파일에서 직접 확인. 단 frame별 하나의 surface 크기만 logging하는 구조라 session별 분리 식별은 간접
- 증거 3 (VT가 resize 후 모든 row dirty 마크한다는 가정): 50% — 일반적 terminal reflow 동작 기반 **추측**. ghostty upstream `ghostty_terminal_resize` 내부 구현 미확인
- root cause가 위 chain의 어디인지 (VT reflow vs dirty-row overwrite vs HwndHost reparent) 는 precise bisect에 RenderDiag+resize-diag 활성화 실행 log가 필요 — 현재 두 diag 모두 env var gate 꺼진 상태로 실행된 log만 존재

## 대안 가설 1

**HwndHost reparent 시 child HWND 재생성 + `_leaves[paneId].SurfaceId != 0` silent return**

`src/GhostWin.App/Controls/PaneContainerControl.cs:214-220` `BuildElement`가 session-based host migration에서 `if (host.Parent is Border previousBorder) previousBorder.Child = null`로 부모 교체. WPF `HwndHost`는 visual parent 변경 시 `DestroyWindowCore` + `BuildWindowCore`를 호출할 수 있음 (Microsoft docs 기반 추측). 새 `BuildWindowCore`가 새 HWND를 생성 → `HostReady` 이벤트 fire → `PaneLayoutService.OnHostReady` 진입 → `src/GhostWin.Services/PaneLayoutService.cs:198`의 `if (state.SurfaceId != 0) return;` silent OK path로 빠져나감 → 새 HWND에는 surface가 바인딩되지 않고 기존 surface는 파괴된 old HWND를 가리킴 → 왼쪽 pane 완전 blank.

이 가설은 "왼쪽 pane만 사라진다"는 증상과 일치하지만, 증거 2의 text count 드롭과는 충돌함 (HWND 문제면 engine text count는 유지되어야). 단 `diag_frame` counter가 global이라 frame log가 active surface 중 하나만 기록할 수 있어서 간접 증거의 해석 여지 있음.

## 약점 1

ghostty VT의 `ghostty_terminal_resize` 내부 동작 (shrink 후 row content drop 여부, grow 후 dirty 마킹 범위)을 upstream 소스에서 직접 확인하지 못함. 증거 3의 "VT가 resize 시 모든 row dirty 마크" 는 통상 terminal reflow 패턴 기반 **추측**. 또한 WPF Grid layout이 실제로 Alt+V split 시 기존 pane을 일시적으로 1×1 수준까지 축소하는지는 `project_split_content_loss_v2_pending.md` 의 저자 진술에 의존. 실제 hardware에서 `GHOSTWIN_RESIZE_DIAG=1` + `GHOSTWIN_RENDERDIAG=3` 환경변수로 재실행해서 `[resize-diag]` 및 `OnPaneResized` 호출 시퀀스 (cols, rows 값, 순서)를 찍어야 chain 상 어느 step에서 content loss가 발생하는지 bisect 가능. 현재 log에는 이 두 diag가 모두 꺼진 상태.

## 읽은 파일 목록

- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs
- C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Diagnostics\RenderDiag.cs (부분)
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp
- C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h
- C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp
- C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp (resize_session 부분)
- C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp (::resize 부분)
- C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp
- C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c (resize/dirty 부분)
- C:\Users\Solit\Rootech\works\ghostwin\src\common\log.h
- C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp (shrink_then_grow 테스트 + main() 부분)
- C:\Users\Solit\Rootech\works\ghostwin\ghostwin_debug.log (frame/surface/session 로그)
- C:\Users\Solit\AppData\Local\Temp\ghostwin_engine_debug.log (session_create 로그)
- C:\Users\Solit\.claude\projects\C--Users-Solit-Rootech-works-ghostwin\memory\project_split_content_loss_v2_pending.md
