# 라운드3 에이전트10 원인 분석

## 결론 (한 문장)
Alt+V split 시 `gw_surface_resize` 가 UI 스레드에서 `Session::vt_mutex` 를 잡고 `state->resize()` 를 호출하지만, 60Hz 렌더 스레드의 `start_paint()` 는 같은 `state` 를 **별개의** `ConPtySession::Impl::vt_mutex` 로 보호하기 때문에 두 스레드가 동시에 `_api`/`_p` 의 `cell_buffer` 를 reshape/read 하게 되어 첫 pane 의 cell 데이터가 손실된다 (dual-mutex 미스매치, 이중 mutex로 인한 data race).

## 증거 3 가지

### 증거 1 — Render path 가 잡는 mutex
파일: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp:142-146`
```cpp
// Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex).
// I/O thread writes to VT under ConPty mutex; render must use the SAME
// mutex for visibility (design §4.5 — dual-mutex bug fix).
state.force_all_dirty();
bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);
```
60fps `render_loop()` (line 173, 별개 스레드) 가 매 프레임마다 `session->conpty->vt_mutex()` 를 잡고 `state.start_paint()` 안에서 `_api.row(r)` / `_api.cell_buffer.data()` 를 읽는다. 주석이 명시하듯 `Session::vt_mutex` 를 의도적으로 회피한다.

### 증거 2 — Resize path 가 잡는 mutex
파일: `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp:369-376`
```cpp
void SessionManager::resize_session(SessionId id, uint16_t cols, uint16_t rows) {
    auto* sess = get(id);
    if (!sess || !sess->is_live()) return;

    std::lock_guard lock(sess->vt_mutex);          // ← 별개의 mutex
    sess->conpty->resize(cols, rows);
    sess->state->resize(cols, rows);               // ← 같은 _api/_p 를 reshape
}
```
Alt+V split 호출 체인은 다음과 같이 UI 스레드에서 이 함수에 도달한다:

`PaneLayoutService.SplitFocused` → Grid layout pass → `OnPaneResized` → `_engine.SurfaceResize` → `gw_surface_resize` (`ghostwin_engine.cpp:601`) → `eng->session_mgr->resize_session` → `lock(sess->vt_mutex)` → `sess->state->resize(cols, rows)`.

여기서 잡는 락은 `Session::vt_mutex` 인데, render 스레드는 같은 객체를 본 적이 없다.

### 증거 3 — 두 mutex 가 서로 다른 객체임을 직접 확인
파일 1: `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h:101-103`
```cpp
std::unique_ptr<ConPtySession> conpty;               // [main+IO, vt_mutex]
std::unique_ptr<TerminalRenderState> state;          // [main+render, vt_mutex]
std::mutex vt_mutex;                                 // ADR-006 extension
```
`Session::vt_mutex` 는 Session 구조체 안에 직접 선언된 별개의 `std::mutex` 인스턴스다.

파일 2: `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.h:78-81`
```cpp
/// Access the internal vt_mutex used by I/O thread.
/// Render thread should lock this same mutex (NOT a separate Session::vt_mutex)
/// to ensure visibility of VT writes.
std::mutex& vt_mutex();
```
`ConPtySession` 은 자체 `Impl::vt_mutex` 를 노출한다. 헤더 주석이 "Render thread should lock this same mutex (NOT a separate Session::vt_mutex)" 로 dual-mutex 가 의도된 함정임을 명시하고 있고, render path 는 그 권고를 따랐지만 `resize_session` 은 따르지 않았다.

추가로 `CLAUDE.md` 의 "## TODO — 기술 부채" 에 다음 항목이 평문으로 적혀 있다:
> - [ ] vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)

이미 알려진 부채이고 본 라운드의 증상을 정확히 일으킬 수 있는 결함이다.

### 보강 — 동시 접근으로 손실이 발생하는 메커니즘
파일: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp:87-125, 231-299`

`reshape()` 의 slow path (`new_c > cap_cols || new_r > cap_rows`) 는
1. `std::vector<CellData> new_buffer(...)` 를 새로 할당
2. row-by-row `memcpy` 로 옛 데이터 복사
3. `cell_buffer = std::move(new_buffer);` ← 옛 vector 가 dropped
4. `cap_cols`, `cap_rows`, `cols`, `rows_count` 갱신

이 4단계가 락 없이 진행되는 동안 render 스레드는 `_api.row(r)` 안에서 `cell_buffer.data() + r * cap_cols` 포인터 산술을 한다. 옛 cap_cols 와 새 cell_buffer 가 섞이거나, 혹은 옛 cell_buffer 가 std::move 로 비워지는 순간을 read 가 보면 row 0 의 prompt 가 통째로 비어 보인다. 첫 pane 에만 영향이 가는 이유는, split 시 새 leaf 의 surface 는 이제 막 생성되어 render path 진입 전이지만, 기존 leaf (oldLeaf) 는 매 프레임 render 가 진행 중이기 때문이다.

또한 fast path (capacity 안) 에서도 락이 없으면 reshape 가 `cols`/`rows_count` 를 업데이트하는 사이에 render 가 옛 `cap_cols` 와 새 `cols` 를 섞어 `std::span` 을 만들 수 있어 정합성이 깨질 수 있다 — `dual_mutex_race_reproduces_content_loss` 테스트가 metadata-only 경로에서도 손실을 노리는 이유가 이것이다.

## 확신도 (0~100)
**85**

근거가 되는 코드 사실 자체는 **확정** (mutex 두 개가 별개라는 것, 두 path 가 각자 다른 락을 잡는다는 것은 line-level evidence). 또한 `tests/render_state_test.cpp` 의 working tree diff 에 `test_dual_mutex_race_reproduces_content_loss` 라는 동일한 이름의 race reproducer 가 작성되어 있어 해당 라운드의 다른 에이전트들이 동일한 결론에 수렴한 정황이 있다 (확신 보강). 다만 본 분석은 실제로 해당 race 를 빌드/실행해서 손실 카운터가 0 이 아님을 본 것은 아니므로 (run 못 함) 100% 가 아닌 85 로 둔다. 또한 split-content-loss-v2 가 4492b5d hotfix + 후속 Option A backing-capacity 패턴 에도 불구하고 100% 재현된다는 사용자 증언과, 두 fix 모두 single-threaded 정적 호출 흐름만 다룬다는 사실이 race 를 강하게 시사한다.

## 대안 가설

### 대안 1 — `vt->resize` 가 ghostty Screen 의 cell 을 zero 로 채워 다음 `for_each_row` 가 빈 row 를 보냄
가능성 있음. `test_real_app_resize_flow` 가 정확히 이 flow 를 단일 스레드에서 시뮬레이션한다. 만약 vt resize 자체가 cell 을 클리어한다면 race 와 무관하게 매번 100% 재현될 것이다. 다만 ghostty 의 `Screen.resize` 가 cell 데이터를 보존하는 것이 일반 터미널의 표준 동작이므로 (그리고 그렇지 않으면 첫 pane 뿐 아니라 두 번째 pane 도 손실되어야 하는데 사용자는 "왼쪽만 사라짐" 이라고 보고) 이 가설은 race 보다 약하다. **추측** 영역.

### 대안 2 — Grid layout 의 shrink-then-grow 연쇄가 capacity 패턴조차 우회
working tree 의 backing-capacity 패턴은 `cap_cols/cap_rows` 가 monotonic grow 이지만, 만약 shrink 후 grow 시점에 `new_c > cap_cols` 인 경계가 만들어지면 slow path 의 memcpy 가 이미 truncate 된 logical cols/rows 만 복사한다 (line 110-111, `min(cols, cap_cols)`). 즉 shrink 가 cap 안에서 일어났다면 logical cols/rows 만 줄어 있다가, 그 다음 cap 을 초과하는 grow 가 오면 logical 값으로 copy 범위가 잘려 손실이 발생한다. 이는 6141005 evidence test 가 노린 시나리오 (`shrink to 1x1, grow beyond cap`) 와 일치한다. 단일 스레드에서도 발생 가능. 가능성 중간. **race 와 동시에 존재할 수도 있음.**

### 대안 3 — host migration (sessionId 매칭) 과정에서 oldLeaf 의 host 가 뜻밖에 새 host 로 교체되어 renderer 가 빈 surface 를 그림
`PaneContainerControl.BuildElement` (line 195-220) 는 `oldHosts[node.Id]` 매칭 → `sessionId` 매칭 → 신규 생성 순으로 host 를 결정한다. 만약 매칭이 실패해 신규 host 가 만들어지면 oldLeaf 의 surfaceId 가 새 HwndHost 의 OnHostReady 까지 유지되지 못하고 새 surface 로 갈아탈 수 있다. 다만 `_hostsByWorkspace` cache 와 `Border previousBorder` 재부모 로직이 명시적으로 reuse 를 우선하므로 가능성은 낮다. 약함.

### 대안 4 — `force_all_dirty()` 가 호출되기 전에 backing buffer 가 비어있는데도 `start_paint()` 가 dirty=false 로 빠져 다음 프레임에 빈 frame 이 보존됨
ghostwin_engine.cpp:145 가 매 프레임 `force_all_dirty()` 를 호출한다. 그래서 dirty 누락 자체는 일어나지 않는다. 약함.

## 약점

1. **빌드/실행 미수행**: 본 분석은 정적 코드 검증만 했다. dual-mutex race 가 실제로 100% 재현 빈도를 만들 만큼 충분히 좁은 윈도우인지, 또는 가끔만 트리거되는지는 측정하지 않았다. `state->resize()` 가 첫 split 시 보통 1~2회만 호출된다면 100% 재현이라는 사용자 증언과 race 의 확률적 성격이 어울리지 않을 수 있다. (다만 Grid layout 이 split 시 여러 번 layout pass 를 트리거하면 race 윈도우가 충분히 넓어진다.)

2. **render thread 의 `force_all_dirty()` + reshape 의 `dirty_rows.set()`** 이 race 와 결합해 새 빈 frame 을 곧바로 _p 로 propagate 시킨다는 메커니즘은 정합적이지만, `start_paint` 가 락 안에서 vt 로부터 freshly populated row 를 다시 받을 가능성도 있다. 만약 vt 자체에 prompt 가 살아있고 dirty 만 set 되면 다음 paint 가 즉시 복원해야 한다. 그런데도 사용자가 "사라진 채로 머무름" 을 본다면 (a) vt 도 빈 상태이거나 (b) race 가 vt read 자체를 깨고 있거나 (c) prompt 는 사실 살아있지만 split 직후 PowerShell 이 prompt 를 다시 그리지 않고 dirty=true 인 `cp_count=0` 빈 row 로 덮어쓰는 것일 수 있다. 어느 시나리오든 race 는 충분조건이지만 유일한 원인이 아닐 가능성을 배제하지 못한다.

3. **사용자가 "100% 재현" 이라고 한 것이 진짜 100% 인지 (혹은 80~90% 를 100% 로 표현했는지) 모름.** race 는 보통 윈도우가 좁으면 100% 가 아니다. 그러나 split 시 layout pass 가 여러 번 발생하므로 16ms × 여러 번 = race 윈도우가 매우 넓을 수 있어 100% 도 설명 가능하다.

4. **render_state.cpp 가 working tree modified 상태**: HEAD commit (`6141005`) 는 evidence-only 였고, working tree 의 Option A backing-capacity 패턴은 아직 commit 되지 않았다. 본 분석은 working tree 코드를 기준으로 했지만, 만약 사용자가 실제로 빌드해서 실행한 바이너리가 (a) HEAD commit (`4492b5d` only, no Option A) 이라면 대안 2 가 race 보다 더 가능성이 높고, (b) working tree 라면 race 가 가장 가능성이 높다.

## 읽은 파일

- `C:\Users\Solit\Rootech\works\ghostwin\CLAUDE.md`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Core\Models\PaneNode.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (working tree diff)
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` (working tree diff)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h`
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp`
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (lines 130-205, 580-606)
- git history: `4492b5d`, `6141005`, file history of `render_state.cpp`
- git working-tree status (modified files in `src/renderer/` 및 `tests/`)
