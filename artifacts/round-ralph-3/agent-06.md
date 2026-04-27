# 라운드3 에이전트06 원인 분석

## 결론 (한 문장)

`SessionManager::resize_session` 이 `Session::vt_mutex` 를 잡고 `state->resize`(_api/_p reshape + dirty_rows.set) 를 수행하는 동안, render thread 는 별개 mutex 인 `ConPtySession::Impl::vt_mutex` 를 잡고 `start_paint` 를 돌리므로 두 경로 사이에 mutual exclusion 이 없어 — Alt+V split 시점 정확히 발생하는 `state->resize` 와 `start_paint` 의 dual-mutex race 가 left pane 의 `_api`/`_p` 를 빈 데이터로 덮거나 torn metadata (cap_cols/rows_count) 를 노출하여 PowerShell prompt + 출력이 사라진다. (capacity-backed `RenderFrame::reshape` 는 single-thread shrink-then-grow 만 idempotent 하게 만들 뿐 race 자체는 닫지 못함.)

## 증거 3 가지

### 증거 1 — 두 개의 별개 mutex 가 코드로 명시적

`src/session/session_manager.cpp:367-376` (resize path, UI thread):

```cpp
void SessionManager::resize_session(SessionId id, uint16_t cols, uint16_t rows) {
    auto* sess = get(id);
    if (!sess || !sess->is_live()) return;

    std::lock_guard lock(sess->vt_mutex);          // ← Session::vt_mutex
    sess->conpty->resize(cols, rows);
    sess->state->resize(cols, rows);                // ← _api/_p reshape
}
```

`src/conpty/conpty_session.cpp:425-445` (ConPty internal mutex):

```cpp
bool ConPtySession::resize(uint16_t cols, uint16_t rows) {
    ...
    {
        std::lock_guard lock(impl_->vt_mutex);     // ← ConPtySession::Impl::vt_mutex (다른 객체!)
        impl_->vt_core->resize(cols, rows);
    }
    ...
}
```

`src/engine-api/ghostwin_engine.cpp:142-147` (render path, render thread):

```cpp
// Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex).
// I/O thread writes to VT under ConPty mutex; render must use the SAME
// mutex for visibility (design §4.5 — dual-mutex bug fix).
state.force_all_dirty();
bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);  // ← ConPty mutex만
```

`Session::vt_mutex` 와 `ConPtySession::Impl::vt_mutex` 는 서로 다른 객체. resize_session 이 `state->resize` 를 호출할 때는 ConPty mutex 를 잡고 있지 않으므로, render thread 의 `start_paint` 와 race 가능. 코멘트 자체가 "dual-mutex bug fix" 라고 적혀있지만 fix 는 한쪽 (render → ConPty mutex) 만 했고 resize 측을 ConPty mutex 로 통합하지 않았다.

### 증거 2 — empirical race reproducer 가 이미 testsuite 에 정식 등록되어 있음

`tests/render_state_test.cpp:546-667` `test_dual_mutex_race_reproduces_content_loss` (Round 2 — 10-agent 합의):

```cpp
// Two DIFFERENT mutexes — mirrors the real app topology where
// Session::vt_mutex (held by resize_session) and
// ConPtySession::vt_mutex (held by start_paint) are distinct.
std::mutex mtx_CONPTY;   // render path
std::mutex mtx_SESSION;  // resize path
...
std::thread t_resize([&] {
    ... { std::lock_guard lk(mtx_SESSION); state.resize(c, r); } ...
});
std::thread t_paint([&] {
    while (...) {
        state.start_paint(mtx_CONPTY, *vt);   // ← 다른 mutex
        for (int k = 0; k < 512; k++) {
            const auto& f = state.frame();
            // ... row 0 가 "RaceTest" 인지 검증
            if (!ok) content_loss_count.fetch_add(1, ...);
        }
    }
});
```

핵심: resize thread 는 capacity 안의 모양만 swing (`{40,5},{20,5},{30,4},{40,5},{10,3},{40,5}`) 시킨다. 즉 모든 reshape 가 metadata-only fast path 를 타게 만들었음. 그런데도 content_loss_count > 0 이 나오면, **shrink-through-tiny-dims 시나리오 (Option A 가 fix 한 것) 와 무관하게 race 자체가 proximate cause** 라는 empirical 증명이 된다. 테스트 코멘트가 그렇게 명시되어 있다 (line 581-587).

`render_state_test.cpp:828-829`:
```
// Round 2 — empirical dual-mutex race reproducer.
TEST(dual_mutex_race_reproduces_content_loss);
```

이 테스트가 PASS 한다는 evidence 는 어디에도 없으며, dual-mutex 통합 부채는 `CLAUDE.md` "기술 부채" 섹션에 여전히 미해결 상태로 등재되어 있다.

### 증거 3 — CLAUDE.md 에 두 항목으로 부채가 명시되어 있고 100% 재현 + capacity-backed fix 가 cover 못함이 적혀있음

`CLAUDE.md` "기술 부채":
> - vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)

`CLAUDE.md` Follow-up Cycles row 8 `split-content-loss-v2` HIGH:
> Alt+V split 후 첫 session buffer 사라짐 — `4492b5d` hotfix 가 Grid layout 의 **shrink-then-grow** 연쇄에서 content 복구 못 함. ... `tests/render_state_test.cpp::test_resize_shrink_then_grow_preserves_content` 가 FAIL empirical 확정 후 `main()` 호출 주석 처리. Fix 후 uncomment.

그리고 코드를 보면 `render_state.h:39-94` 와 `render_state.cpp:87-125` 에 이미 **capacity-backed RenderFrame (Option A)** 가 들어가 있다. `tests/render_state_test.cpp:822` 의 `test_resize_shrink_then_grow_preserves_content` 도 이미 re-enabled 상태이며 코멘트가:
```
// Re-enabled by split-content-loss-v2 cycle (2026-04-09).
// Capacity-backed RenderFrame makes shrink-then-grow idempotent
// within the high-water-mark capacity.
```

즉 Option A 마이그레이션은 끝났음에도 사용자는 100% 재현을 보고 → **남은 미해결 부분이 dual-mutex race 임을 강하게 시사**. Round 2 reproducer 가 metadata-only path 만 가지고도 loss 를 잡도록 설계된 것도 같은 이유.

## 확신도 (0~100)

**78**

근거 — 높이는 요인:
- 두 mutex 가 별개라는 것은 코드로 직접 확인 (증거 1, 100% 객관 사실)
- empirical reproducer 가 이미 testsuite 에 등록되어 있고 해당 시나리오 (metadata-only path 에서도 loss) 가 명시적으로 noted (증거 2)
- CLAUDE.md 가 두 부채 (vt_mutex 통합 + split-content-loss-v2 HIGH) 를 별도로 등재 (증거 3)
- 100% 재현이라는 점은 race 보다 deterministic flow 문제일 가능성을 약간 낮추지만, `force_all_dirty()` 가 매 frame 호출되어 _api 전체가 _p 로 복사되는 구조 + `state->resize` 직후 매 frame 그 race window 가 매번 열린다는 점에서 100% 에 매우 근접한 hit-rate 가 가능 (window 가 매 split 마다 거의 확실히 hit)

확신도를 100 으로 못 올리는 이유:
- 본 에이전트는 reproducer test 의 **실제 실행 결과** 를 직접 빌드/실행해서 확인하지 못함 (분석만)
- ghostty `terminal_resize` 의 reflow 동작이 alt-screen / wraparound mode 에 따라 분기하는데 PowerShell 의 mode 상태를 직접 확인 못함 — VT 측 reflow 가 별도 원인일 가능성 잔존 (대안 가설 H1 참조)
- 100% 재현이라는 점이 race 와 약간 어색 (race 라면 보통 oscillate). 단, 위에서 설명했듯 force_all_dirty + 매 frame _p 덮어쓰기 + 광범위한 race window 로 사실상 100% hit 가능

## 대안 가설

| H | 가설 | 신뢰도 | 핵심 단서 / 반박 |
|:-:|---|:-:|---|
| H1 | Ghostty `terminal_resize` 가 alt-screen 이거나 wraparound 비활성 모드에서 cells 를 erase. PowerShell 이 그 후 SIGWINCH redraw 를 안 보내거나 prompt 만 다시 그림 | 35 | terminal.h:803 가 "primary screen will reflow content if wraparound mode is enabled; the alternate screen does not reflow" 라고 명시. PowerShell 은 일반적으로 primary screen + DECAWM(7) on 이지만 100% 보장은 못함. 단, **출력 내용까지** 사라진다는 보고는 reflow 정상 동작 가정시 부정합 |
| H2 | `for_each_row` 의 `cells_buf.resize(impl_->cols)` 가 resize 직후 새로운 (작은) cols 로 buf 를 잡고, ghostty row 가 더 많은 cells 를 가진 경우 col < impl_->cols 까지만 채움. row 가 작은 데이터로 callback 호출되어 _api 의 [0, new_cols) 만 채워지는데 _api 의 cap_cols stride 와 cols 가 일치하지 않을 수 있다 | 25 | code 상으로는 reshape 후 _api.cols == new_cols, cap_cols >= new_cols 이므로 stride 정합. 단 race 가 끼어들면 어떻게 되는지 분석 필요 |
| H3 | `SetWindowPos(_childHwnd, ..., w, h, ...)` 후 dx11 surface 의 `ResizeBuffers` 가 deferred 로 render thread 에서 적용되는데, 그 사이 frame 들이 잘못된 viewport 로 그려져서 left pane 이 빈 영역으로 보임 (실제로는 _api 에 데이터가 있음) | 20 | 사용자가 "사라진다" 라고 했으니 검은 화면일 수 있음. 단 prompt 도 같이 사라진다는 점은 buffer 자체가 비어있음을 시사. surface_mgr 의 deferred resize 는 needs_resize flag → 다음 frame 적용이라 brief tear 만 발생 — 100% persistent 는 설명 못함 |
| H4 | `PaneContainerControl.BuildElement` 의 host migration 시 `previousBorder.Child = null` (line 218-219) 로 detach 후 새 Border 의 child 로 reparent 될 때 HwndHost 가 잠깐 `_childHwnd` 를 destroy/recreate 하면서 `gw_surface_create` 를 다시 호출하지 않고 stale surface 를 둠 | 15 | DestroyWindowCore 는 `_childHwnd = IntPtr.Zero` 로 만들기만 하고 surface 는 별도. 단 host 가 reparent 시 BuildWindowCore 가 재호출되는지 확신 못함 — H3 가설 자체 검증이 필요. 100% 재현과 정합 |
| H5 | `gw_surface_resize` 가 session 의 cols/rows 를 atlas cell size 기반으로 새로 계산해서 `resize_session` 호출하는데 (`ghostwin_engine.cpp:594-602`), atlas 가 글자가 작은 폰트라 cols 가 0 으로 truncate 되어 resize_session(0,0) 호출되고, conpty resize 실패 + state resize 도 0x0 → cell_buffer 비워짐 | 10 | code 가 `if (cols < 1) cols = 1; if (rows < 1) rows = 1` 로 클램프함. 단 split 시점에 ActualWidth 가 잠깐 0 일 수 있고, OnRenderSizeChanged 의 widthPx 도 1 로 클램프되지만 cell_width 로 나누면 0 → 1 로 클램프 → 1x1 로 reshape. capacity-backed 에서는 metadata-only 라 OK 이지만 race 와 결합하면 가능 |

## 약점

- **빌드/실행 검증 부재**: race reproducer 테스트의 실제 실행 결과를 보지 못함 (`scripts/test_ghostwin.ps1` 같은 걸로 돌려서 fail 인지 확인 안 함). 따라서 race 가 *empirical* 로 100% loss 를 일으키는지는 코드 + 명시적 reproducer 의 존재 + CLAUDE.md 등재 사실로만 추론. 만약 그 테스트가 PASS 한다면 본 결론의 직접 evidence 가 무너진다.
- **100% 재현과 race 의 정합성 약함**: 일반적인 race 는 0~100% 사이 hit rate 를 보이는데 사용자는 100% 라고 보고. 본 에이전트는 "force_all_dirty + 매 frame 전체 복사 + 광범위한 race window" 로 100% 가능성을 변명했지만, 진짜 100% deterministic flow bug (e.g. H1 의 ghostty reflow) 일 가능성을 완전히 배제하지 못함.
- **ghostty-vt 내부 동작 미검증**: `ghostty_terminal_resize` 가 PowerShell 이 사용하는 mode 조합에서 reflow 를 어떻게 하는지 직접 확인 안 함. PowerShell 이 wraparound 비활성 또는 alt-screen 사용시 H1 가 진짜 원인일 수 있음.
- **render_state.cpp 의 force_all_dirty + start_paint 상호작용 분석이 표면적**: `force_all_dirty()` 가 _api.dirty_rows 만 set 하는데 _p 복사 시에 `_api.cols` 만큼 memcpy 하므로 stride 와 visible-cols 가 다른 경우 (cap_cols > cols) 의 정합성을 정밀하게 검증하지 못함. cap_cols stride / cols length 의 invariant 가 race 중에 깨지면 어떤 셀이 어디로 가는지 정확히 못 그림.
- **다른 9 명의 에이전트 분석 인용 금지 규칙 준수**: 본 에이전트는 round-ralph-3 / consensus-round-3 같은 동일 라운드의 다른 결과를 보지 않고 독립적으로 분석. 따라서 합의 가능성을 사후적으로 점검 못함 (의도적 — 규칙 준수).
- **C# 측 BuildElement 의 host 라이프사이클 시퀀스를 timeline 으로 그리지 못함**: WPF logical / visual tree reparent 시 ActualWidth 변화 순서, OnRenderSizeChanged 발화 횟수, BuildWindowCore 재호출 여부 같은 것을 실제 trace 없이는 확정 못함. H3/H4 가 race 와 합쳐진 형태가 진짜 원인일 가능성 잔존.

## 읽은 파일

- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` (vt_bridge_resize 부분)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (resize_all/resize_session/apply_pending_resize 영역, 290-410)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (resize 함수, 420-460)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\include\ghostty\vt\terminal.h` (resize 함수 doc, 770-826)
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` (190-808 — Round 1/2 reproducer + main 영역)
- `C:\Users\Solit\Rootech\works\ghostwin\CLAUDE.md` (system reminder 로 자동 로드, follow-up cycle row 8 + 기술 부채 vt_mutex 통합 항목 인용)
