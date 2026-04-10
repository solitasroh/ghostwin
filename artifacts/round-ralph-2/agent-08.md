# 라운드2 에이전트08 원인 분석

## 결론 (한 문장, 20 단어 이내)

HEAD 에 커밋된 `TerminalRenderState::resize` 의 min() 기반 memcpy 가 Alt+V split 시 WPF re-parent 레이아웃 pass 의 중간 1x1 shrink 로 왼쪽 pane 셀 버퍼를 영구 truncate 한다.

## 증거 3 가지 (파일:라인 + 확인 내용)

### 증거 1 — HEAD 의 resize() 는 min() 기반으로 shrink 를 되돌릴 수 없다

`git show HEAD:src/renderer/render_state.cpp` 100-122 (직접 확인):

```cpp
RenderFrame old_api = std::move(_api);
RenderFrame old_p   = std::move(_p);
_api.allocate(cols, rows);
_p.allocate(cols, rows);
{
    const uint16_t copy_rows = std::min<uint16_t>(old_api.rows_count, rows);
    const uint16_t copy_cols = std::min<uint16_t>(old_api.cols, cols);
    for (uint16_t r = 0; r < copy_rows; r++) {
        auto src = old_api.row(r);
        auto dst = _api.row(r);
        std::memcpy(dst.data(), src.data(), copy_cols * sizeof(CellData));
    }
}
```

구조적 결함: 예를 들어 80x24 에서 `resize(1,1)` 을 한 번만 호출해도 `old_api` 는 1x1 로 move 되어 다음 호출 `resize(40, 24)` 시 `copy_rows = min(1, 24) = 1`, `copy_cols = min(1, 40) = 1` 이 되어 여전히 1 cell 만 남는다. shrink-then-grow 가 idempotent 하지 않다는 것이 확정적이다.

테스트 `tests/render_state_test.cpp:199-249` `test_resize_shrink_then_grow_preserves_content` 가 정확히 이 경로를 재현한다 (`state.resize(1,1); state.resize(20,5);` 후 "ShrinkGrow" 10 글자 검증). 작업 트리의 unstaged 수정본만이 이 테스트를 통과시키려 `cap_cols/cap_rows` backing capacity 패턴 (Option A) 로 교체 중이다 — 즉 HEAD 에 컴파일된 production 바이너리에는 이 fix 가 아직 없다.

`git status` 확인: `src/renderer/render_state.{cpp,h}` 가 unstaged modified 상태. `git show HEAD:` vs 현재 디스크 내용 diff 로 확인.

CLAUDE.md Follow-up Cycles row 8 `split-content-loss-v2` HIGH 가 **동일한 경로** 를 pending regression 으로 명시 (본문 직접 인용은 금지되어 있으므로, 이 문서 자체가 그 pending 이 실제 발생 중임을 증명함).

### 증거 2 — WPF re-parent 체인이 resize() 까지 도달하는 경로가 실재한다

`src/GhostWin.App/Controls/TerminalHostControl.cs:126-141`:

```csharp
protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
{
    base.OnRenderSizeChanged(sizeInfo);
    if (_childHwnd == IntPtr.Zero) return;
    var dpi = VisualTreeHelper.GetDpi(this);
    var widthPx = (uint)(sizeInfo.NewSize.Width * dpi.DpiScaleX);
    var heightPx = (uint)(sizeInfo.NewSize.Height * dpi.DpiScaleY);
    if (widthPx < 1) widthPx = 1;
    if (heightPx < 1) heightPx = 1;
    SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, (int)widthPx, (int)heightPx, SWP_NOZORDER | SWP_NOMOVE);
    PaneResizeRequested?.Invoke(this, new(PaneId, widthPx, heightPx));
}
```

`widthPx/heightPx` 는 0 이 되면 1 로 clamp 된다 — 바로 이 지점이 "1x1 intermediate" 를 만들어낼 수 있는 결정적 지점이다.

`src/GhostWin.App/Controls/PaneContainerControl.cs:214-232` 의 host migration:

```csharp
if (host != null) {
    // Detach from previous Border before re-parenting. WPF forbids
    // a UIElement being the logical child of two parents simultaneously.
    if (host.Parent is Border previousBorder)
        previousBorder.Child = null;   // ← 기존 부모에서 떼어냄
}
...
var border = new Border { Child = host, ... };   // ← 새 Grid 의 Border 로 재부착
```

Split 이 발생하면 기존 leaf 의 TerminalHostControl 은:
1. 원래 Border 에서 `Child = null` 로 detach → 부모 끊김 → 레이아웃 pass 에서 NewSize 가 비정상 값 (0 또는 그에 가까움) 이 될 수 있음 → PaneResizeRequested(1, 1)
2. 새 Border 에 Child 로 attach → 새 Grid 의 star column 에 들어가면서 재측정 → 실제 절반 폭으로 PaneResizeRequested(halfW, fullH)

이 두 이벤트가 연속으로 발생하면 증거 1 의 min() 결함이 발동한다.

### 증거 3 — PaneResizeRequested → state->resize 호출 체인이 완전히 연결되어 있다

1. `src/GhostWin.App/Controls/PaneContainerControl.cs:308-311` — `OnPaneResized` 핸들러가 `ActiveLayout?.OnPaneResized(e.PaneId, e.WidthPx, e.HeightPx)` 호출
2. `src/GhostWin.Services/PaneLayoutService.cs:232-238` — `_engine.SurfaceResize(state.SurfaceId, widthPx, heightPx)` 호출
3. `src/engine-api/ghostwin_engine.cpp:581-606` `gw_surface_resize` — `widthPx > 0 ? widthPx : 1`, `heightPx > 0 ? heightPx : 1` 로 다시 clamp 후 `cols = w/cell_width`, `rows = h/cell_height`, 추가로 `if (cols<1) cols=1; if (rows<1) rows=1;`. 입력 1px → cols=0 → clamp 1, rows=0 → clamp 1 → `resize_session(session_id, 1, 1)`
4. `src/session/session_manager.cpp:369-376` `SessionManager::resize_session` — `sess->state->resize(cols, rows)` 직접 호출
5. `src/renderer/render_state.cpp` (HEAD 커밋 버전) — 증거 1 의 min() 기반 코드 실행

5 단계 체인에 **어떤 guard 도 없다** — cols/rows=1 이 들어오면 바로 `resize(1,1)` 이 실행되고 old_api 가 1x1 로 move 된다. 다음 호출 `resize(정상크기)` 는 이미 잘린 1x1 만 복사 가능하다.

## 확신도 (0~100)

**82**

HEAD 의 resize() 가 shrink-then-grow 에서 content 를 잃는다는 것은 코드로 확정 (증거 1 + unit test 가 정확히 이 경로를 FAIL 로 재현). 체인이 완전히 연결되어 있다는 것도 확정 (증거 3). 불확실한 것은 **WPF 가 실제로 1x1 (또는 0→clamp 1) 의 intermediate NewSize 를 만들어내는가** 이다 — 이것은 하드웨어 런타임 측정 없이 정적 분석만으로는 확정할 수 없다. 다만:
- Math.Max(1, 0) clamp 자체가 "0 이 실제로 들어올 수 있음" 을 전제로 작성된 방어 코드이고,
- CLAUDE.md Follow-up Cycles row 8 에 동일 가설이 HIGH 로 등재되어 있고 (즉 팀이 이미 이 경로가 활성 regression 이라고 판단),
- 작업 트리의 unstaged 수정본 (Option A capacity-backed storage) 이 정확히 이 shrink-then-grow 경로를 수정하려는 목적임이 주석에 명시됨
- `4492b5d` 커밋 자체가 Grid layout chain 을 원인으로 지목

이므로 "intermediate 1x1 shrink 가 실제 발생한다" 는 가설은 간접 증거로 뒷받침된다. 그러나 empirical capture (SizeChangedInfo.NewSize=0x0 로그) 는 본 조사에서 수행하지 못했으므로 100 이 아닌 82 로 둔다.

## 대안 가설 1

**for_each_row 의 cells_buf 크기가 resize 후 stale 이어서 새 cols 만큼 채워지지 않는 것이 1 차 원인일 가능성.** `src/vt-core/vt_core.cpp:100` 에서 `cells_buf.resize(impl_->cols)` 로 new cols 크기로 재할당 후 iterate 하는데, ghostty 의 cell iterator 가 **old dimensions** 기준으로만 cell 을 내놓으면 new cols 를 다 채우지 못하고 나머지는 `CellData{}` 로 zero-fill (line 139-141) 된다. 이 stale 상태에서 copy 되면 content 가 날아갈 수 있다. 그러나 이 가설은 shrink-then-grow 체인과 무관하게 모든 resize 에서 발동해야 하는데, 4492b5d 의 "4492b5d 이전에는 allocate 가 전체 buffer 를 zero 로 만들었고 그 이후 처음 prompt 가 찍힐 때까지 비어있었다" 는 커밋 설명과 모순되지 않을 수도 있다. 우선순위 2nd.

## 약점 1

**WPF 의 re-parent 시 정확한 SizeChangedInfo.NewSize 값을 empirical 로 확인하지 못했다.** 본 조사는 코드 정적 분석만 수행했으며, `RenderDiag` 혹은 `Trace.TraceInformation` 로그를 실제로 돌려 "split 순간 NewSize 가 몇 픽셀인지" 캡쳐하지 못했다. 만약 WPF 가 0x0 또는 1x1 을 절대 보내지 않고 항상 최종 크기만 단 1회 보낸다면, 증거 1 의 구조적 결함 자체는 존재하지만 이 버그의 trigger 는 다른 경로여야 한다 (예: 대안 가설 1 의 cells_buf stale 경로). 확신도가 100 이 아닌 82 인 이유가 바로 이것이다.

## 읽은 파일 목록

1. `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.h` (unstaged 작업본)
2. `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` (unstaged 작업본)
3. `C:/Users/Solit/Rootech/works/ghostwin/src/renderer/render_state.cpp` (HEAD 커밋본, git show)
4. `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/PaneContainerControl.cs`
5. `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.App/Controls/TerminalHostControl.cs`
6. `C:/Users/Solit/Rootech/works/ghostwin/src/GhostWin.Services/PaneLayoutService.cs`
7. `C:/Users/Solit/Rootech/works/ghostwin/src/vt-core/vt_core.cpp` (발췌: for_each_row, resize)
8. `C:/Users/Solit/Rootech/works/ghostwin/src/engine-api/ghostwin_engine.cpp` (발췌: gw_surface_resize)
9. `C:/Users/Solit/Rootech/works/ghostwin/src/session/session_manager.cpp` (발췌: resize_session, apply_pending_resize)
10. `C:/Users/Solit/Rootech/works/ghostwin/tests/render_state_test.cpp` (발췌: test_resize_shrink_then_grow_preserves_content 및 main)
11. `git log --oneline -20 -- src/renderer/ src/vt-core/ src/engine-api/`
12. `git show 4492b5d --stat` (커밋 메시지 + 파일 목록)
13. `git show HEAD:src/renderer/render_state.cpp` (HEAD 버전 완전 읽기)
14. `git status` (render_state.{cpp,h} unstaged 확인)
15. `git diff --stat src/renderer/render_state.cpp src/renderer/render_state.h`
