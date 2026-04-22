# M-14 Render Thread Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** GhostWin 렌더 경로를 안전한 frame snapshot 구조로 바꾸고, `idle`, `대량 출력`, `4-pane resize` 시나리오에서 성능 기준선을 다시 세운다.

**Architecture:** 먼저 Release 빌드 기준 계측 경로를 넣어 "어디서 느린지"를 수치로 고정한다. 그 다음 `TerminalRenderState` 를 front/back snapshot 구조로 바꿔 resize 와 draw 사이 경계를 닫고, `force_all_dirty()` 상시 호출을 없애서 dirty-driven render/present 로 되돌린다. 마지막으로 pane 수 증가 시 `Present(1, 0)` 경로가 실제로 얼마나 병목인지 재측정하고, 비교 터미널과 같은 시나리오로 검증한다.

**Tech Stack:** C++20, WPF + native engine DLL, DX11, ConPTY, ghostty-vt, PowerShell, PresentMon/GPU-Z/Task Manager

---

## File Map

### 핵심 코드

| 파일 | 역할 |
|------|------|
| `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp` | render loop, per-surface draw, selection/composition setter, perf 계측 진입점 |
| `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\surface_manager.h` | surface별 resize 상태, 마지막 visual epoch 같은 render-side 메타데이터 |
| `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h` | `RenderFrame`, `TerminalRenderState` public contract |
| `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp` | `_api` → render snapshot 복사, resize 처리, dirty propagation |
| `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\quad_builder.cpp` | 실제 row/cell 순회 hot path |
| `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\dx11_renderer.h` | draw/present 계측 노출용 최소 API |
| `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\dx11_renderer.cpp` | `upload_and_draw()` / `Present(1, 0)` 시간 측정 |
| `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h` | `Session` 단위 visual invalidation epoch |
| `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp` | activate / IME preview 같은 redraw 트리거 |

### 테스트 / 계측 / 결과

| 파일 | 역할 |
|------|------|
| `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp` | snapshot 안전성, resize/read stress 재현 |
| `C:\Users\Solit\Rootech\works\ghostwin\tests\dx11_render_test.cpp` | DX11 draw smoke, 필요 시 perf flag smoke 확인 |
| `C:\Users\Solit\Rootech\works\ghostwin\scripts\measure_render_baseline.ps1` | `idle/load/resize` 시나리오 자동 실행 및 로그 수집 |
| `C:\Users\Solit\Rootech\works\ghostwin\docs\04-report\features\m14-render-baseline.report.md` | 내부 수치 + 외부 비교 기록 |
| `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\m14-render-thread-safety.md` | 최종 결과 반영 |
| `C:\Users\Solit\Rootech\works\ghostwin\docs\00-pm\m14-render-thread-safety.prd.md` | PM 문서 업데이트 (숫자 확정/조정 결과 반영) |

### 구현 순서

1. W1 계측
2. W2 snapshot 안전화
3. W3 visual invalidation / idle 회복
4. W4 pane/present 재측정
5. W5 외부 비교 + 문서 닫기

---

### Task 1: W1 측정 가능성 확보

**Files:**
- Create: `C:\Users\Solit\Rootech\works\ghostwin\scripts\measure_render_baseline.ps1`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\dx11_renderer.h`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\dx11_renderer.cpp`
- Test: `C:\Users\Solit\Rootech\works\ghostwin\tests\dx11_render_test.cpp`

- [ ] **Step 1: render perf 로그 형식을 먼저 고정한다**

```cpp
struct RenderPerfSample {
    uint64_t frame_id;
    uint32_t surface_id;
    double start_paint_ms;
    double build_ms;
    double upload_draw_ms;
    double present_ms;
    bool vt_dirty;
    bool visual_dirty;
    bool resize_applied;
};
```

- [ ] **Step 2: `ghostwin_engine.cpp` 에 opt-in 계측 경로를 넣는다**

```cpp
const bool perf_enabled = std::getenv("GHOSTWIN_RENDER_PERF") != nullptr;
if (perf_enabled) {
    LOG_I("render-perf",
          "frame=%llu sid=%u vt_dirty=%d visual_dirty=%d resize=%d "
          "start=%.3f build=%.3f draw=%.3f present=%.3f",
          sample.frame_id, sample.surface_id, sample.vt_dirty,
          sample.visual_dirty, sample.resize_applied,
          sample.start_paint_ms, sample.build_ms,
          sample.upload_draw_ms, sample.present_ms);
}
```

- [ ] **Step 3: `dx11_renderer` 에 `Present(1, 0)` 시간 반환 경로를 넣는다**

```cpp
struct DrawPerfResult {
    double upload_draw_ms = 0.0;
    double present_ms = 0.0;
};

DrawPerfResult DX11Renderer::upload_and_draw_timed(
    const void* instances, uint32_t count, uint32_t bg_count);
```

- [ ] **Step 4: Release 기준 baseline 수집 스크립트를 만든다**

```powershell
param(
    [ValidateSet('idle','load','resize')] [string]$Scenario = 'idle',
    [int]$DurationSec = 60
)

$env:GHOSTWIN_RENDER_PERF = '1'
msbuild GhostWin.sln /p:Configuration=Release /p:Platform=x64
Start-Process -FilePath ".\build\Release\GhostWin.App.exe"
```

- [ ] **Step 5: DX11 smoke 가 계측 추가 후에도 깨지지 않는지 확인한다**

Run:

```powershell
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:GhostWinTestName=dx11_render_test
.\build\tests\Debug\dx11_render_test.exe
```

Expected:

```text
Build succeeded.
=== S5+S6+S7 PASSED ===
```

- [ ] **Step 6: Release baseline 을 3개 시나리오로 한 번씩 수집한다**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 60
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario load -DurationSec 60
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario resize -DurationSec 60
```

Expected:

```text
[baseline] scenario=idle  samples written
[baseline] scenario=load  samples written
[baseline] scenario=resize samples written
```

- [ ] **Step 7: Commit**

```bash
git add src/engine-api/ghostwin_engine.cpp src/renderer/dx11_renderer.h src/renderer/dx11_renderer.cpp scripts/measure_render_baseline.ps1
git commit -m "plan: add m14 render baseline instrumentation hooks"
```

---

### Task 2: W2 frame snapshot 안전화

**Files:**
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.h`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\renderer\render_state.cpp`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\tests\render_state_test.cpp`

- [ ] **Step 1: 현재 topology 를 반영한 failing stress test 를 먼저 쓴다**

```cpp
static bool test_frame_snapshot_stays_consistent_while_resize_mutates_back_buffer() {
    // render thread: start_paint(mtx, vt) -> frame() read without lock
    // resize thread: resize() under same mtx
    // expected after fix: no torn frame, no empty row guard dependency
}
```

- [ ] **Step 2: test 가 현 구조에서 실패하거나 guard 에 의존함을 확인한다**

Run:

```powershell
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:GhostWinTestName=render_state_test
.\build\tests\Debug\render_state_test.exe
```

Expected:

```text
render_state_test reports the new snapshot stress as FAIL
```

- [ ] **Step 3: `TerminalRenderState` 를 `_front/_back` snapshot 구조로 바꾼다**

```cpp
class TerminalRenderState {
public:
    [[nodiscard]] const RenderFrame& frame() const { return _front; }
private:
    RenderFrame _api;
    RenderFrame _front;
    RenderFrame _back;
};
```

- [ ] **Step 4: `resize()` 는 `_front` 를 건드리지 않고 `_api/_back` 만 바꾸게 만든다**

```cpp
void TerminalRenderState::resize(uint16_t cols, uint16_t rows) {
    _api.reshape(cols, rows);
    _back.reshape(cols, rows);
    for (uint16_t r = 0; r < rows; r++) _api.set_row_dirty(r);
}
```

- [ ] **Step 5: `start_paint()` 는 `_back` 에 dirty row 를 복사한 뒤 마지막에 `_front` 와 swap 한다**

```cpp
for (uint16_t r = 0; r < _api.rows_count; r++) {
    if (_api.is_row_dirty(r)) {
        auto src = _api.row(r);
        auto dst = _back.row(r);
        std::memcpy(dst.data(), src.data(), std::min(src.size(), dst.size()) * sizeof(CellData));
    }
}
std::swap(_front, _back);
```

- [ ] **Step 6: race 완화용 empty-span guard 를 구조상 불필요한 위치에서 걷어낸다**

```cpp
// remove guard comments that explain "another thread may reshape() while render reads _p"
// keep only invariants that are still logically required
```

- [ ] **Step 7: render_state 테스트를 다시 돌려 PASS 를 확인한다**

Run:

```powershell
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:GhostWinTestName=render_state_test
.\build\tests\Debug\render_state_test.exe
```

Expected:

```text
=== Results: 0 failed ===
```

- [ ] **Step 8: Commit**

```bash
git add src/renderer/render_state.h src/renderer/render_state.cpp tests/render_state_test.cpp
git commit -m "fix: make render frame snapshot safe across resize"
```

---

### Task 3: W3 강제 full redraw 제거와 visual invalidation 분리

**Files:**
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\session\session.h`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.cpp`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\surface_manager.h`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`

- [ ] **Step 1: visual redraw 이유를 VT dirty 와 분리하는 epoch 를 추가한다**

```cpp
struct Session {
    std::atomic<uint32_t> visual_epoch{1};
};

struct RenderSurface {
    uint32_t last_visual_epoch = 0;
};
```

- [ ] **Step 2: selection / composition / activate 경로에서 epoch 를 증가시킨다**

```cpp
session->visual_epoch.fetch_add(1, std::memory_order_relaxed);
```

적용 위치:

- `gw_session_set_composition`
- `gw_session_set_selection`
- `SessionManager::activate`
- IME preview update path

- [ ] **Step 3: render path 에서 `visual_dirty` 를 계산하고 `force_all_dirty()` 상시 호출을 제거한다**

```cpp
const uint32_t visual_epoch = session->visual_epoch.load(std::memory_order_acquire);
const bool visual_dirty = (surf->last_visual_epoch != visual_epoch);

bool vt_dirty = state.start_paint(session->conpty->vt_mutex(), vt);
if (!vt_dirty && !visual_dirty && !resize_applied) return;
surf->last_visual_epoch = visual_epoch;
```

- [ ] **Step 4: IME / selection overlay 는 "항상 full redraw" 가 아니라 "상태가 바뀐 프레임만 redraw" 로 바꾼다**

```cpp
// composition_visible 자체가 아니라
// last_composition_* 와 현재 상태 비교 결과를 visual_dirty reason 으로 사용
```

- [ ] **Step 5: Release baseline 을 다시 수집해 idle/load 가 실제로 내려갔는지 확인한다**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 60
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario load -DurationSec 60
```

Expected:

```text
[baseline] idle samples updated
[baseline] load samples updated
```

- [ ] **Step 6: Commit**

```bash
git add src/session/session.h src/session/session_manager.cpp src/engine-api/surface_manager.h src/engine-api/ghostwin_engine.cpp
git commit -m "perf: stop forcing full redraw every render frame"
```

---

### Task 4: W4 multi-pane present 경로 재측정과 정책 고정

**Files:**
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\src\engine-api\ghostwin_engine.cpp`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\scripts\measure_render_baseline.ps1`
- Test: `C:\Users\Solit\Rootech\works\ghostwin\tests\dx11_render_test.cpp`

- [ ] **Step 1: per-surface `resize_applied / vt_dirty / visual_dirty / present_ms` 를 로그에 남긴다**

```cpp
LOG_I("render-perf",
      "sid=%u dirty(vt=%d visual=%d resize=%d) present=%.3f panes=%zu",
      surf->session_id, vt_dirty, visual_dirty, resize_applied, present_ms, active.size());
```

- [ ] **Step 2: 4-pane resize 시나리오를 baseline 스크립트에 추가/고정한다**

```powershell
if ($Scenario -eq 'resize') {
    # 4-pane 구성 → splitter drag 또는 window resize 반복
}
```

- [ ] **Step 3: 현재 M-14에서는 `Present(1, 0)` 를 유지하되 "clean surface skip-present" 가 실제로 동작하는지 확인한다**

```cpp
if (!vt_dirty && !visual_dirty && !resize_applied) {
    return; // no upload, no draw, no present
}
```

- [ ] **Step 4: 4-pane resize baseline 을 다시 측정한다**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario resize -DurationSec 60
```

Expected:

```text
[baseline] resize samples updated
```

- [ ] **Step 5: 여기서도 GhostWin이 명확히 밀리면 follow-up 조건을 기록한다**

```text
If p95 or present_ms still scales linearly with pane count after clean-surface skip,
record "Present policy follow-up required" instead of adding tearing mode in M-14.
```

- [ ] **Step 6: Commit**

```bash
git add src/engine-api/ghostwin_engine.cpp scripts/measure_render_baseline.ps1
git commit -m "perf: measure multi-pane present behavior after dirty gating"
```

---

### Task 5: W5 외부 비교와 문서 닫기

**Files:**
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\docs\04-report\features\m14-render-baseline.report.md`
- Modify: `C:\Users\Solit\Rootech\works\ghostwin\docs\00-pm\m14-render-thread-safety.prd.md`
- Modify: `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\m14-render-thread-safety.md`
- Modify: `C:\Users\Solit\obsidian\note\Projects\GhostWin\Backlog\tech-debt.md`

- [ ] **Step 1: 내부 결과를 report 문서에 정리한다**

```markdown
| Scenario | Before | After | Delta | Notes |
|----------|--------|-------|-------|------|
| idle     | ...    | ...   | ...   | ...  |
| load     | ...    | ...   | ...   | ...  |
| resize   | ...    | ...   | ...   | ...  |
```

- [ ] **Step 2: WT / WezTerm / Alacritty 비교표를 같은 시나리오로 채운다**

```markdown
| Scenario | GhostWin | WT | WezTerm | Alacritty | Verdict |
|----------|----------|----|---------|-----------|---------|
```

- [ ] **Step 3: PRD 의 가설 budget 숫자를 실측 결과로 확정하거나 조정한다**

```markdown
초기 가설 budget → 실측 확정값
```

- [ ] **Step 4: Obsidian milestone 과 tech debt 를 현재 상태로 갱신한다**

```markdown
- M-14 status: active -> done (or active with follow-up)
- Tech debt #26: closed / narrowed / follow-up split
```

- [ ] **Step 5: 최종 검증 명령을 다시 한 번 돌린다**

Run:

```powershell
msbuild GhostWin.sln /p:Configuration=Release /p:Platform=x64
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:GhostWinTestName=render_state_test
.\build\tests\Debug\render_state_test.exe
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 60
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario load -DurationSec 60
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario resize -DurationSec 60
```

Expected:

```text
Build succeeded.
=== Results: 0 failed ===
[baseline] idle samples updated
[baseline] load samples updated
[baseline] resize samples updated
```

- [ ] **Step 6: Commit**

```bash
git add docs/04-report/features/m14-render-baseline.report.md docs/00-pm/m14-render-thread-safety.prd.md C:/Users/Solit/obsidian/note/Projects/GhostWin/Milestones/m14-render-thread-safety.md C:/Users/Solit/obsidian/note/Projects/GhostWin/Backlog/tech-debt.md
git commit -m "docs: close m14 render safety and perf baseline work"
```

---

## Spec Coverage Check

| PRD 요구 | 이 plan 에서 다루는 Task |
|----------|--------------------------|
| W1 측정 가능성 확보 | Task 1 |
| frame ownership 안정화 | Task 2 |
| idle 낭비 제거 | Task 3 |
| multi-pane present 정책 점검 | Task 4 |
| 경쟁 비교 검증 | Task 5 |
| Release-only 성능 판정 | Task 1, Task 5 |
| ghostty fork OPT 15/16 영향 확인 | Task 1 |
| PM/Obsidian 문서 갱신 | Task 5 |

## Plan Self-Review

- placeholder scan 완료: `TODO/TBD/implement later` 없음
- 현재 코드 기준과 충돌하는 옛 `dual mutex` 전제를 plan 본문에서 사용하지 않음
- 숫자 goal 은 PM 문서 기준으로 "가설 budget → 측정 후 확정" 흐름을 유지함
- M-14 범위를 넘는 `DXGI tearing 모드` 전환은 follow-up 조건으로만 남기고, 본 plan 에서는 기준선 회복까지만 다룸

## Execution Handoff

Plan complete and saved to `docs/01-plan/features/temp-m14-render-thread-safety.plan.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
