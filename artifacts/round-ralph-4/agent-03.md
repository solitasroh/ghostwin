# 라운드4 에이전트03 원인 분석

## 결론 (한 문장)
`SessionManager::resize_session` 이 `Session::vt_mutex` 로 `state->resize()` 를 보호하는 반면, 렌더 스레드의 `state.start_paint()` 는 `ConPtySession::vt_mutex` (완전히 다른 mutex 객체) 로만 동기화되기 때문에, Alt+V split 시 Grid 레이아웃이 연쇄적으로 resize 를 트리거하는 동안 `_api`/`_p` RenderFrame 이 unsynchronized 하게 동시 접근되어 content 가 torn/loss 된다. (이중 mutex 레이스)

## 증거 3 가지

### 증거 1 — 렌더 경로가 잡는 mutex 는 ConPty 쪽
`src/engine-api/ghostwin_engine.cpp:140-146` (render closure 내부):
```
// Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex).
// I/O thread writes to VT under ConPty mutex; render must use the SAME
// mutex for visibility (design §4.5 — dual-mutex bug fix).
state.force_all_dirty();
bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);
```
주석 스스로 "dual-mutex bug fix" 라고 밝히고 있으며, 렌더가 잡는 것은 `ConPtySession::vt_mutex` 이다 (`conpty_session.cpp:459` `std::mutex& ConPtySession::vt_mutex() { return impl_->vt_mutex; }`).

### 증거 2 — resize 경로가 잡는 mutex 는 Session 쪽 (다른 객체)
`src/session/session_manager.cpp:369-376`:
```
void SessionManager::resize_session(SessionId id, uint16_t cols, uint16_t rows) {
    auto* sess = get(id);
    if (!sess || !sess->is_live()) return;

    std::lock_guard lock(sess->vt_mutex);  // Session::vt_mutex
    sess->conpty->resize(cols, rows);
    sess->state->resize(cols, rows);       // ← _api/_p reshape + dirty_rows.set()
}
```
그리고 `conpty->resize` 내부 (`conpty_session.cpp:425-445`) 는 자신만의 `impl_->vt_mutex` (ConPty mutex) 를 새로 잡아 `vt_core->resize` 만 보호한다. 즉 `state->resize` 는 오직 `Session::vt_mutex` 만 보호되고, ConPty 쪽 mutex 는 전혀 잡혀 있지 않은 상태에서 `_api.reshape` / `_p.reshape` / `dirty_rows.set()` 이 실행된다.

이 동일한 순간, 별개 스레드인 render 루프가 `ConPtySession::vt_mutex` 만 잡은 채 `start_paint` 내부에서 `_api.cell_buffer`, `_api.cols`, `_api.rows_count`, `_api.cap_cols`, `_p.row(r)`, `_p.cell_buffer` 를 모두 읽고/쓰고 있다 (`render_state.cpp:132-228`). 두 스레드 모두 **동일한 `TerminalRenderState` 인스턴스를 공유**하면서 **서로 다른 mutex 로 진입**하므로 상호 배제가 사실상 없다.

CLAUDE.md 의 "기술 부채" 섹션 자체에도 이 사실이 남아 있다:
> vt_mutex 통합 (Session::vt_mutex ↔ ConPty::Impl::vt_mutex 이중 mutex)

### 증거 3 — 이 레이스를 정확히 재현하는 테스트가 이미 존재
`tests/render_state_test.cpp:546-667` `test_dual_mutex_race_reproduces_content_loss`:
```
// Two DIFFERENT mutexes — mirrors the real app topology where
// Session::vt_mutex (held by resize_session) and
// ConPtySession::vt_mutex (held by start_paint) are distinct.
std::mutex mtx_CONPTY;   // render path
std::mutex mtx_SESSION;  // resize path
```
그리고 resize 스레드는 40x5 capacity 안의 shape 만 돌려 (`{40,5},{20,5},{30,4},{40,5},{10,3},{40,5}`) **metadata-only reshape 경로만 타도록** 의도적으로 격리한 뒤 "loss 가 0 이 아니면 race 자체가 proximate cause" 라고 명시한다. 이 테스트는 `main()` (line 829) 에서 활성화되어 있으며 주석에서 "Round 2 — empirical dual-mutex race reproducer" 로 지정되어 있다.

추가로 `test_real_app_resize_flow` (line 471-523) 는 Alt+V 에서 일어나는 정확한 시퀀스 — write → start_paint → `vt->resize(20,5)` → `state->resize(20,5)` → 다시 `start_paint` → _p 에 content 남아있는지 검증 — 을 single-thread 로 재현한다. single-thread 환경에서는 이 순서만으로도 content 손실이 재현되므로, dual-mutex race 에 더해 **단일 스레드 시퀀스 수준의 2차 요인**도 동시에 있을 가능성이 있다 (아래 대안 가설 참조).

## 확신도 (0~100)
65

(근거 기반 판정: 소스 레이어에서 mutex mismatch 가 주석/테스트로 명시적 확정이므로 "레이스가 존재한다" 는 사실은 100%. 그러나 "이 레이스가 PowerShell 프롬프트 사라짐의 *최종 proximate cause* 라는 점" 은 100% 가 아니다. Alt+V 1회 100% 재현이라는 증상은 순수 레이스보다 결정론적 시퀀스 문제에 더 가깝기 때문이다. render_state.cpp 에 이미 존재하는 capacity-backing 패턴이 shrink-then-grow 를 single-thread 로는 방어하므로, 관측된 100% 재현을 완전히 설명하려면 race + 또다른 단일 스레드 요인의 중첩을 가정해야 한다.)

## 대안 가설

### H-A — ghostty VT 쪽 reflow shrink 가 primary 우선 content loss
`Terminal.resize` → `primary.resize(reflow = wraparound)` → `PageList.resize` → cols 감소 시 `resizeCols` 가 reflow 하는데, Alt+V 로 인해 중간 step 에서 `1×1` 같은 극단적 작은 grid 가 들어오면 PowerShell 프롬프트 cell 이 reflow 중 손실/잘림 가능. `state->resize` 가 아무리 capacity 를 보존해도, 다음 `start_paint` 의 `for_each_row` 가 VT 에서 **덮어쓰기 memcpy** 를 수행 (`render_state.cpp:178`) 하므로 VT 가 비어 있으면 `_api` 도 비워진다. Terminal.zig:2859 `self.flags.dirty.clear = true;` 는 이 가능성을 더 강화한다.

### H-B — shrink-then-grow 연쇄에서 logical `rows_count=1, cols=1` 로 밀어넣힌 intermediate reshape 가 원인
`test_reshape_capacity_retention` 주석 (render_state_test.cpp:367-380) 이 "capacity 를 넘어 grow 하는 순간 min(rows_count, cap_rows) × min(cols, cap_cols) = 1×1 cells 만 copy 된다" 는 설계상 trade-off 를 명시하고 있다. Alt+V 가 실제로 1x1 을 거친 뒤 capacity 를 **초과**하는 grow 를 일으키면 (예: split 으로 더 큰 창으로 확장), 이 경로에서 content 가 1x1 만 남고 버려진다. 단, Alt+V 후 왼쪽 pane 은 일반적으로 더 좁아지지 커지진 않으므로 이 가설의 가능성은 낮다.

### H-C — host 재사용 시 reparent 순서 문제로 인한 중복 resize
`PaneContainerControl.BuildElement` (PaneContainerControl.cs:214-220) 가 `host.Parent is Border previousBorder` 일 때 `previousBorder.Child = null` 한 뒤 새 Border 로 옮기는데, 이 과정에서 `OnRenderSizeChanged` 가 여러 번 fire 되면서 intermediate 작은 크기로 resize 가 연쇄. 이 가설은 증거 1/2 와 **중첩**으로 작용 — resize 연쇄가 많아질수록 dual-mutex race window 도 넓어진다.

## 약점
- **동적 재현을 직접 실행하지 못함**: 빌드/테스트 실행 권한이 제한되어 `test_dual_mutex_race_reproduces_content_loss` 와 `test_real_app_resize_flow` 의 현재 PASS/FAIL 결과를 직접 확인하지 못했다. 이 두 테스트가 실제로 FAIL 해야 본 분석이 100% 확정된다.
- **ghostty `Terminal.resize` 내부 reflow 손실 가능성**: Terminal.zig:2859 `self.flags.dirty.clear = true;` 및 `primary.resize(reflow=true)` 가 shrink 시 어떤 cell 을 살리고 어떤 cell 을 버리는지 `PageList.resizeCols` 상세 경로는 읽지 못했다 (매우 길다). 즉 "VT 자체가 shrink 시 content 를 reflow 해서 보존한다" 는 가정이 깨질 수 있다. 이 경우 render_state 쪽 fix 와 무관하게 VT 레이어에서 이미 content 가 날아가 있을 수 있다 — 증거 3 의 `test_real_app_resize_flow` 가 명확히 FAIL 하는지가 이 분기를 가른다.
- **단일 원인 단정 위험**: 증상이 "100% 재현" 이므로 순수 race 만으로 설명하기는 약하다. 증거 1/2 의 mutex mismatch 는 확정 사실이지만, 그것이 *모든* 재현을 설명하는 *유일한* 원인이라는 보장은 없다. 대안 가설 H-A 와의 조합 가능성 높음.
- **render thread 가 `force_all_dirty()` 를 매 프레임 호출** (`ghostwin_engine.cpp:145`) 하지만 `_api` 의 dirty 만 set 하므로, 다음 start_paint 가 VT 의 `for_each_row` 로 _api cell 을 덮어쓴다. 즉 render 루프는 항상 VT 가 source of truth — `state->resize` 의 content preservation 은 딱 다음 한 프레임 분량만 의미가 있다. 이 구조 자체가 증거/가설 체인을 복잡하게 만든다.
- `artifacts/split-content-loss-v2/before-split.png` / `after-split.png` 의 이미지 자체는 직접 열어보지 않았다 (Read tool 로 이미지 diff 불가). 증상의 픽셀 수준 특성 (완전 블랙 vs 부분 손실 vs 프롬프트 한 줄만 살아남음 등) 을 확인 못함.

## 읽은 파일
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\terminal_window.cpp` (line 180-300)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_core.cpp` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\vt-core\vt_bridge.c` (line 95-108)
- `C:\Users\Solit\Rootech\works\ghostwin\src\conpty\conpty_session.cpp` (line 395-465)
- `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` (line 195-420)
- `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` (line 130-610)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\TerminalHostControl.cs` (전체)
- `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` (line 1-840, 전체)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\Terminal.zig` (line 2820-2872, `resize` 함수)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\c\terminal.zig` (line 395-450, C API `resize` wrapper)
- `C:\Users\Solit\Rootech\works\ghostwin\external\ghostty\src\terminal\PageList.zig` (line 940-1100, `resize` / `resizeCols` 진입부)
- grep: `resize_session`, `start_paint`, `state->resize`, `OnPaneResized` 등 호출 site 확인
