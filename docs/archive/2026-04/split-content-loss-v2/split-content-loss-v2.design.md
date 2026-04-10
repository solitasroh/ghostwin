# split-content-loss-v2 Design Document

> **Summary**: `TerminalRenderState::resize` 의 `min()` 기반 shrink-destructive memcpy 를 제거하고 `RenderFrame` 을 **logical (cols, rows_count) + backing capacity (cap_cols, cap_rows)** 2-tier 구조로 재설계. Shrink 는 metadata-only, grow 는 capacity 이내면 metadata-only, capacity 초과 시에만 reallocate + row-by-row remap. Consumer (quad_builder / terminal_window / ghostwin_engine) 무수정.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 Follow-up Cycle #8 (HIGH)
> **Author**: 노수장
> **Date**: 2026-04-09
> **Status**: Draft (v0.1)
> **Plan**: [`docs/01-plan/features/split-content-loss-v2.plan.md`](../../01-plan/features/split-content-loss-v2.plan.md)
> **Parent fix (incomplete)**: `4492b5d fix(render): preserve cell buffer across resize`
> **Evidence commit**: `6141005 test(render): add split-content-loss-v2 regression evidence`

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | `4492b5d` 의 single-call content-preserving memcpy 가 WPF Grid layout 의 **shrink-then-grow** 연쇄 (intermediate pass `~1x1` → final pass `60x30`) 에서 idempotent 하지 않음. 첫 shrink 가 old buffer 를 `std::move` 로 drop 하면 후속 grow 가 복구할 정보가 없음. 증상: Alt+V split 후 좌측 pane 의 buffer empty. Empirical 확정: `test_resize_shrink_then_grow_preserves_content` FAIL. |
| **Solution** | `RenderFrame` 의 storage 를 `cap_cols × cap_rows` 에 고정 (high-water mark). `cols/rows_count` 은 logical view size. `row(r)` 은 physical stride `cap_cols` 로 offset 계산, span length 는 logical `cols`. `reshape(new_c, new_r)` 이 capacity 이내면 metadata-only (memcpy 0), 초과 시 reallocate + row-by-row remap. `TerminalRenderState::resize` 는 `_api.reshape()` + `_p.reshape()` 위임 + `_api.dirty_rows` set. |
| **Function/UX Effect** | Alt+V/Alt+H split 후 좌측/상단 pane 에 pre-split shell content 유지. 연속/혼합 split 에서 첫 session 생존. `TerminalRenderState::resize` 의 public signature 동일. Memory overhead: 100×30 기준 ≈ 96 KB/session (`cap × 16B × 2 buffer`). 300×120 극단 기준 ≈ 1.15 MB/session. Consumer 3 파일 (quad_builder / terminal_window / ghostwin_engine) 무수정. |
| **Core Value** | `std::vector` 의 `capacity ≠ size` 패턴을 `RenderFrame` 에 그대로 적용하여 logical view / physical storage 분리라는 **structural invariant** 확보. 어떤 resize sequence 에서도 `0..cap` 영역 content 는 무손실. Single-call memcpy hotfix 가 대응 못 하는 transient intermediate pass 를 재설계로 구조적 해소. |

---

## 1. Design Goals

### 1.1 Primary Goals

- **G1 (Correctness)**: 임의의 resize sequence `r1, r2, ..., rN` 에 대해, `max(cap)` 이하 영역의 cell content 가 손실 없이 보존
- **G2 (Idempotency)**: `resize(A) → resize(B) → resize(A)` 가 A→A 와 동일한 content 를 복구 (단, 중간에 `start_paint` 로 VT write 가 발생하지 않는 경우)
- **G3 (API transparency)**: `RenderFrame::row(r)` 의 span length semantics 유지 → consumer 무수정
- **G4 (Minimal memory overhead)**: worst-case footprint 가 `cap_cols × cap_rows × sizeof(CellData) × 2` 로 bounded
- **G5 (Empirical validation)**: `test_resize_shrink_then_grow_preserves_content` PASS + 신규 regression test 3개 PASS + 사용자 hardware smoke 5/5 PASS

### 1.2 Non-Goals

- **NG1**: Capacity 의 auto-shrink (high-water mark 유지) — follow-up cycle 후보
- **NG2**: `ghostty_vt` 의 scrollback snapshot refill — 복잡도 + API 미확인
- **NG3**: `PaneLayoutService` / WPF Grid 수정 — 근본 원인 회피
- **NG4**: Cursor position 의 logical bounds 외부 clamping — 관찰된 문제 없음 (§7.3 참조)
- **NG5**: Performance benchmark — `render-overhead-measurement` follow-up cycle 로 분리

### 1.3 Design Principles

- **Single source of truth**: `RenderFrame::cell_buffer` 는 stable backing storage, `cols/rows_count` 는 logical view window
- **Structural invariant**: `cap_cols >= cols && cap_rows >= rows_count` (resize 완료 후 항상 유지)
- **Metadata-only path 우선**: capacity 이내 resize 는 O(1) + dirty_rows set (O(rows_count))
- **Consumer API contract 불변**: `row(r)` 가 반환하는 span 의 length 는 항상 logical `cols`, consumer 는 stride 를 몰라도 됨
- **근거 기반 수정**: `6141005` 의 empirical FAIL test 를 기준으로 fix 성공/실패 판정 (feedback_hypothesis_verification_required 준수)

---

## 2. Root Cause Analysis

### 2.1 Symptom Chain

```
User: Alt+V
 → MainWindow.OnTerminalKeyDown (KeyBinding bubble handler, fix 1207e5f)
 → PaneLayoutService.SplitFocused
   → oldLeaf 의 SessionId, hwnd, SurfaceId 유지
   → newLeaf 생성, new TerminalHostControl mount
 → WPF Grid layout pass 1 (measure/arrange)
   → oldLeaf pane container 가 transient intermediate size (관찰: ~1x1 또는 매우 작음)
   → OnRenderSizeChanged fire
 → PaneLayoutService.OnPaneResized (oldLeaf)
 → _engine.SurfaceResize(surfaceId, w_px, h_px)
 → gw_surface_resize (ghostwin_engine.cpp:581)
 → session_mgr->resize_session(sid, cols, rows)    ← cols=1, rows=1
 → state->resize(1, 1)                              ← ★ old buffer dropped
 → WPF Grid layout pass 2 (final arrange)
   → oldLeaf pane container 가 final size (~60x30)
 → PaneLayoutService.OnPaneResized (oldLeaf)
 → _engine.SurfaceResize(surfaceId, w_px, h_px)
 → gw_surface_resize
 → session_mgr->resize_session(sid, cols, rows)    ← cols=60, rows=30
 → state->resize(60, 30)                            ← 이 시점에 old_api 는 1x1
 → memcpy(min(1,30) × min(1,60) = 1 cell)           ← 복구 불가능
 → 화면: 1 cell 만 남음 → "빈 화면" 증상
```

### 2.2 Invariant Violation

`4492b5d` hotfix 의 `TerminalRenderState::resize` 는 다음 invariant 를 **single call 에 대해** 만족:

```
Invariant-single:
  After resize(new_c, new_r):
    For r in [0, min(old_r, new_r)),
      For c in [0, min(old_c, new_c)),
        _api.row(r)[c] == old_api.row(r)[c] (before resize)
```

그러나 **multi-call sequence** 에 대해서는:

```
Invariant-sequence (REQUIRED but not satisfied):
  After resize(r1) → resize(r2) → ... → resize(rN):
    For r in [0, min(initial_r, rN.r)),
      For c in [0, min(initial_c, rN.c)),
        _api.row(r)[c] == initial.row(r)[c]
```

위반 시나리오: `initial 40x5 → resize(1, 1) → resize(20, 5)` 에서 `rN = (20, 5)` 이므로 기대값은 initial 의 `min(40,20) × min(5,5) = 20 × 5` cell 복구. 실제: `1 × 1` cell 만 복구. **19 × 5 + 1 × 4 = 99 cell 손실**.

### 2.3 Why Option A Fixes This

Option A 의 invariant:

```
Invariant-capacity:
  After any resize sequence r1, r2, ..., rN:
    cap_cols == max(initial_c, r1.c, r2.c, ..., rN.c)
    cap_rows == max(initial_r, r1.r, r2.r, ..., rN.r)
    cell_buffer size == cap_cols * cap_rows (never shrunk)

    For r in [0, min(initial_r, rN.r)),
      For c in [0, min(initial_c, rN.c)),
        _api.row(r)[c] == initial.row(r)[c]
```

핵심: **`cell_buffer` 의 storage 는 shrink 에서 건드려지지 않으므로 `0..initial_c` 영역의 data 는 capacity 가 축소되지 않는 한 physically 생존**. `row(r)` 이 physical stride 로 offset 계산하므로 나중에 `cols` 가 다시 커져도 올바른 위치에서 data 를 읽음.

---

## 3. Locked-in Design Decisions

| # | 결정 | 값 | 근거 |
|---|---|---|---|
| **D1** | `RenderFrame` 에 capacity 필드 추가 | `uint16_t cap_cols = 0;` `uint16_t cap_rows = 0;` | G1 invariant, Plan §6.2 |
| **D2** | `RenderFrame::row(r)` stride | **physical (`cap_cols`)** | Metadata-only resize 가 memcpy 없이 가능하려면 stride 가 `cols` 에 종속되면 안 됨 |
| **D3** | `row(r)` span length | **logical (`cols`)** | Consumer 가 iterate 하는 범위는 `[0, cols)` 이어야 함 (no visual artifact) |
| **D4** | `RenderFrame::allocate(c, r)` semantics | 초기 allocate 는 `cap = (c, r)` 로 시작, logical 도 `(c, r)` 로 설정 | 첫 ctor 경로 호환 |
| **D5** | Reshape API 위치 | `RenderFrame::reshape(new_c, new_r)` 신규 (private method 아닌 public) | `TerminalRenderState::resize` 가 `_api` / `_p` 양쪽에 위임 |
| **D6** | Metadata-only 조건 | `new_c <= cap_cols && new_r <= cap_rows` | Capacity 안에서 shrink 또는 grow 모두 metadata-only |
| **D7** | Capacity-exceeded 처리 | new cap = `max(current cap, new)`, new storage 할당 + row-by-row remap, old storage drop | Capacity monotonic grow (high-water mark), initial grow 이후에도 preservation 유지 |
| **D8** | Remap 시 copy 범위 | `min(rows_count, cap_rows) × min(cols, cap_cols)` (= current logical view 가 아니라 logical ∩ old cap) | grow 시 logical 만 복사하면 되나 storage 에는 아직 접근 가능한 hidden cell 이 있음. Simplicity 우선 — logical 영역만 remap, hidden cells 은 new storage 에 존재하지만 zero-init (§7.1 참조) |
| **D9** | `reshape` 후 `dirty_rows` | `0..rows_count` set (full propagate) | Consumer 가 `_p` 를 읽으므로 `start_paint` 이 다음에 `_api → _p` copy 를 수행해야 함 |
| **D10** | `reshape` 후 `cursor` | 기존과 동일 (변경 없음) | Plan §2.3 Risk-bounded, 현재 OOB 관찰된 바 없음 |
| **D11** | `TerminalRenderState::resize` signature | **유지** (`void resize(uint16_t, uint16_t)`) | Caller (`session_manager.cpp:375`) 무수정 |
| **D12** | `TerminalRenderState::resize` 구현 | `_api.reshape(c, r); _p.reshape(c, r); for r in [0,rows) _api.set_row_dirty(r);` | D5, D9 |
| **D13** | `_api.clear_all_dirty()` 위치 | 기존대로 `start_paint` 끝에서 clear (reshape 에서는 touch 안 함) | 현재 `TerminalRenderState` 동작 유지 |
| **D14** | 주석 업데이트 | `render_state.cpp::resize` 의 기존 주석 (50+ lines) 을 신 설계로 교체. `4492b5d` / `6141005` 커밋 reference | Plan §4.2 Quality Criteria |
| **D15** | Unit test 활성화 | `tests/render_state_test.cpp::main():279` 의 주석 제거 | Plan FR-05 |
| **D16** | 신규 unit test | `test_reshape_metadata_only`, `test_reshape_capacity_retention`, `test_row_stride_after_shrink` 3개 | Plan FR-07 |
| **D17** | Consumer 변경 | **없음** (`quad_builder.cpp`, `terminal_window.cpp`, `ghostwin_engine.cpp`) | G3, Plan FR-09 |
| **D18** | `_p.clear_all_dirty` 호출 | `start_paint` 의 기존 흐름 유지 | D12, D13 |
| **D19** | Cursor clamp | **하지 않음** | Plan NG4, 관찰된 문제 없음, 향후 `cursor-clamp-on-shrink` follow-up 후보 |
| **D20** | Memory footprint logging | 하지 않음 | Plan NG5, `render-overhead-measurement` follow-up 으로 분리 |

---

## 4. Detailed Design — File-by-File Diffs

### 4.1 `src/renderer/render_state.h`

#### 4.1.1 `RenderFrame` struct 재설계 (line 17-44)

**Before**:
```cpp
struct RenderFrame {
    std::vector<CellData> cell_buffer;  // rows * cols
    uint16_t cols = 0;
    uint16_t rows_count = 0;

    std::span<CellData> row(uint16_t r) {
        return { cell_buffer.data() + r * cols, cols };
    }
    std::span<const CellData> row(uint16_t r) const {
        return { cell_buffer.data() + r * cols, cols };
    }

    std::bitset<constants::kMaxRows> dirty_rows;

    bool is_row_dirty(uint16_t r) const { return r < constants::kMaxRows && dirty_rows.test(r); }
    void set_row_dirty(uint16_t r) { if (r < constants::kMaxRows) dirty_rows.set(r); }
    void clear_all_dirty() { dirty_rows.reset(); }
    bool any_dirty() const { return dirty_rows.any(); }

    CursorInfo cursor{};

    void allocate(uint16_t c, uint16_t r) {
        cols = c;
        rows_count = r;
        cell_buffer.resize(static_cast<size_t>(c) * r);
        dirty_rows.reset();
    }
};
```

**After**:
```cpp
/// Render frame data — flat buffer with separate logical view and physical
/// storage, inspired by std::vector's capacity/size distinction.
///
/// `cols` / `rows_count` are the *logical* view dimensions exposed to
/// consumers (quad_builder, terminal_window, ghostwin_engine). Iteration
/// is always bounded by these.
///
/// `cap_cols` / `cap_rows` are the *physical* storage dimensions of
/// `cell_buffer`. They are a high-water mark over the lifetime of this
/// frame: once grown, the capacity never shrinks (`reshape()` guarantees
/// `cap_cols >= cols && cap_rows >= rows_count`). `row(r)` uses `cap_cols`
/// as the stride so that `reshape()` can change `cols` / `rows_count`
/// without touching `cell_buffer` whenever the new size fits in capacity.
///
/// This layout fixes the split-content-loss-v2 regression: the WPF Grid
/// layout's shrink-then-grow chain during Alt+V split would drop the old
/// buffer on the intermediate ~1x1 `resize()` call when the storage was
/// sized to the logical dims (4492b5d hotfix). With capacity-backed
/// storage, shrink becomes a metadata-only update and the subsequent
/// grow simply re-exposes the still-present cells.
///
/// See commit `6141005` for the regression test
/// (`test_resize_shrink_then_grow_preserves_content`).
struct RenderFrame {
    std::vector<CellData> cell_buffer;  // cap_rows * cap_cols
    uint16_t cols = 0;         // logical view width (visible cells per row)
    uint16_t rows_count = 0;   // logical view height (visible rows)
    uint16_t cap_cols = 0;     // physical storage stride (never shrinks)
    uint16_t cap_rows = 0;     // physical storage height (never shrinks)

    std::span<CellData> row(uint16_t r) {
        // Physical stride (cap_cols) for offset, logical length (cols)
        // for span size. Consumer iterates [0, cols) and never sees the
        // hidden cells beyond that.
        return { cell_buffer.data() + static_cast<size_t>(r) * cap_cols, cols };
    }
    std::span<const CellData> row(uint16_t r) const {
        return { cell_buffer.data() + static_cast<size_t>(r) * cap_cols, cols };
    }

    std::bitset<constants::kMaxRows> dirty_rows;

    bool is_row_dirty(uint16_t r) const { return r < constants::kMaxRows && dirty_rows.test(r); }
    void set_row_dirty(uint16_t r) { if (r < constants::kMaxRows) dirty_rows.set(r); }
    void clear_all_dirty() { dirty_rows.reset(); }
    bool any_dirty() const { return dirty_rows.any(); }

    CursorInfo cursor{};

    /// Initial allocation: sets both logical and physical dimensions to (c, r).
    /// Called from TerminalRenderState ctor only.
    void allocate(uint16_t c, uint16_t r) {
        cols = c;
        rows_count = r;
        cap_cols = c;
        cap_rows = r;
        cell_buffer.assign(static_cast<size_t>(c) * r, CellData{});
        dirty_rows.reset();
    }

    /// Content-preserving reshape:
    ///   - If (new_c, new_r) fits within (cap_cols, cap_rows):
    ///       metadata-only (no memcpy, no reallocation). The existing
    ///       cell data at offsets [0, new_r) × [0, new_c) within the
    ///       backing storage is implicitly preserved.
    ///   - Otherwise:
    ///       grow capacity to max(current, new), allocate a new backing
    ///       buffer, and row-by-row memcpy the overlap from the old
    ///       storage (bounded by logical rows_count × cols) to the new
    ///       storage. The old backing buffer is dropped.
    ///
    /// Dirty rows are NOT modified here — the caller
    /// (TerminalRenderState::resize) is responsible for marking rows dirty
    /// so the next `start_paint()` propagates _api → _p.
    void reshape(uint16_t new_c, uint16_t new_r);
};
```

#### 4.1.2 `TerminalRenderState::resize` signature (line 62-63) — 무변경

```cpp
/// Resize (caller must hold vt_mutex).
void resize(uint16_t cols, uint16_t rows);
```

### 4.2 `src/renderer/render_state.cpp`

#### 4.2.1 `RenderFrame::reshape` 추가 (신규)

새 코드 (render_state.cpp 내부 namespace ghostwin 블록 상단 또는 `TerminalRenderState::resize` 앞에 배치):

```cpp
void RenderFrame::reshape(uint16_t new_c, uint16_t new_r) {
    // Fast path: new dims fit inside existing capacity.
    // Just update the logical view — the backing storage already holds
    // the cell data at offsets [0, new_r) × [0, new_c) (implicitly).
    if (new_c <= cap_cols && new_r <= cap_rows) {
        cols = new_c;
        rows_count = new_r;
        return;
    }

    // Slow path: at least one dim exceeds capacity. Grow capacity
    // monotonically (high-water mark), allocate new backing buffer,
    // and row-by-row memcpy the overlap from the old storage.
    const uint16_t new_cap_c = std::max(cap_cols, new_c);
    const uint16_t new_cap_r = std::max(cap_rows, new_r);

    std::vector<CellData> new_buffer(
        static_cast<size_t>(new_cap_c) * new_cap_r, CellData{});

    // Copy overlap: bounded by min(logical, old_cap) to ensure we don't
    // read past old storage. `rows_count` and `cols` here are the current
    // logical view (before reshape) which is always <= cap by invariant.
    const uint16_t copy_rows = std::min(rows_count, cap_rows);
    const uint16_t copy_cols = std::min(cols, cap_cols);
    for (uint16_t r = 0; r < copy_rows; r++) {
        const CellData* src = cell_buffer.data() +
                              static_cast<size_t>(r) * cap_cols;
        CellData* dst = new_buffer.data() +
                        static_cast<size_t>(r) * new_cap_c;
        std::memcpy(dst, src, copy_cols * sizeof(CellData));
    }

    cell_buffer = std::move(new_buffer);
    cap_cols = new_cap_c;
    cap_rows = new_cap_r;
    cols = new_c;
    rows_count = new_r;
}
```

#### 4.2.2 `TerminalRenderState::resize` 재작성 (line 67-128)

**Before**: 50+ lines 의 주석 + `old_api/old_p` snapshot + manual row-by-row memcpy 2 block (`_api` / `_p` 각각).

**After**:
```cpp
void TerminalRenderState::resize(uint16_t cols, uint16_t rows) {
    // Content-preserving resize via RenderFrame::reshape (Option A:
    // backing-capacity pattern). Shrinks become metadata-only; grows
    // within capacity become metadata-only; grows beyond capacity grow
    // the backing storage monotonically.
    //
    // Fixes split-content-loss-v2: the WPF Grid layout's shrink-then-grow
    // chain during Alt+V split would drop the old buffer on the
    // intermediate ~1x1 resize when storage was bound to logical dims
    // (4492b5d hotfix). See commit 6141005 for the regression test
    // (`test_resize_shrink_then_grow_preserves_content`).
    //
    // Caller contract (unchanged): must hold vt_mutex.

    _api.reshape(cols, rows);
    _p.reshape(cols, rows);

    // Mark all logical rows dirty so the next start_paint() propagates
    // the preserved cell data through to _p. Without this flag, ghostty
    // VT's for_each_row would only report rows it considers dirty — and
    // a bare terminal resize does NOT mark every row dirty (PowerShell
    // only redraws on the next prompt).
    for (uint16_t r = 0; r < rows; r++) {
        _api.set_row_dirty(r);
    }
}
```

#### 4.2.3 `TerminalRenderState` ctor (line 10-13) — 무변경

```cpp
TerminalRenderState::TerminalRenderState(uint16_t cols, uint16_t rows) {
    _api.allocate(cols, rows);
    _p.allocate(cols, rows);
}
```

`allocate` 의 semantics 가 `cap = logical` 로 초기화하는 것으로 변경되어 ctor 경로는 그대로 호환.

#### 4.2.4 `start_paint` (line 15-65) — 무변경

`for_each_row` callback 내부의 `_api.row(row_idx)` 및 `copy_cols = std::min<size_t>(cells.size(), dst.size())` 는 `dst.size() == cols` (logical) 를 반환하므로 현재 동작 유지. 마지막의 `_api → _p` copy loop (line 53-59) 도 `_api.row(r)` / `_p.row(r)` 의 span length 가 동일 (둘 다 같은 logical cols) 하고 `_api.cols` (copy 길이) 도 동일.

### 4.3 `src/renderer/quad_builder.cpp` — 무변경

**Verification** (line 55-91):

```cpp
for (uint16_t r = 0; r < frame.rows_count; r++) {     // uses logical rows_count ✓
    auto row = frame.row(r);                          // returns span { ptr, cols } ✓
    for (uint16_t c = 0; c < frame.cols; c++) {       // uses logical cols ✓
        const auto& cell = row[c];                    // indexes [0, cols) ✓
        ...
    }
}
```

`frame.row(r)[c]` 는 `cell_buffer[r * cap_cols + c]` 로 해석됨. `c < cols <= cap_cols` 이므로 physical stride 가 logical cols 보다 크거나 같아도 항상 정확한 cell 에 접근. **API 투명성 검증 완료**.

### 4.4 `src/renderer/terminal_window.cpp` — 무변경

**Verification** (line 69-71):
```cpp
const auto& frame = state->frame();
uint32_t count = builder.build(frame, *atlas, renderer->context(),
    std::span<QuadInstance>(staging));
```

`frame` 전체를 `QuadBuilder::build` 에 pass-through. §4.3 의 verification 이 그대로 적용.

### 4.5 `src/engine-api/ghostwin_engine.cpp` — 무변경

**Verification** (line 149-152):
```cpp
const auto& frame = state.frame();
uint32_t bg_count = 0;
uint32_t count = builder.build(frame, *atlas, renderer->context(),
    std::span<QuadInstance>(staging), &bg_count);
```

§4.3 과 동일한 pattern. 무수정.

---

## 5. Test Design

### 5.1 Test Matrix

| # | Test | Category | 목적 |
|:-:|---|---|---|
| T1 | `test_allocate_and_access` | 기존 | 초기 allocate 후 `cols == 80`, `rows_count == 24`, `cell_buffer.size() == 80 * 24`. Design 후에도 `cap = logical = (80, 24)` 이므로 buffer size 동일. PASS 유지 |
| T2 | `test_start_paint_with_data` | 기존 | `start_paint` 가 `_api.row(r)` 에 올바른 offset 으로 write. `cap == logical` 인 첫 alloc 상태에서는 offset 이 기존과 동일. PASS 유지 |
| T3 | `test_second_paint_clean` | 기존 | 2회 start_paint 에서 두 번째가 clean. 변경 없음. PASS 유지 |
| T4 | `test_resize` | 기존 | `resize(120, 40)` 후 `cols == 120`, `rows_count == 40`, `cell_buffer.size() == 120 * 40`. Grow 이므로 reallocate path 실행 + cap = (120, 40). PASS 유지 |
| T5 | `test_resize_preserves_content` | 기존 (4492b5d) | `40x5 → 30x5` shrink 후 row 0 에 "Preserved" 보존. **metadata-only path** 통과 (30 <= 40, 5 <= 5). Row 0 의 storage 는 건드려지지 않고 cols 만 30 으로 축소. `row(0)` 이 `{ data, 30 }` span 반환 → 0..9 의 'P','r','e','s','e','r','v','e','d' 확인. PASS 유지 |
| T6 | `test_resize_grow_preserves_content` | 기존 (4492b5d) | `40x5 → 80x10` grow. 80 > cap_cols(40), 10 > cap_rows(5) → reallocate path. `new_cap_c = 80`, `new_cap_r = 10`, row-by-row remap. Row 0 에 "GrowTest" 보존, Row 5 는 zero-init. **Remap 시 old stride (40) → new stride (80)** 가 정확해야 함. PASS 유지 |
| **T7** | **`test_resize_shrink_then_grow_preserves_content`** | **기존 (disabled)** | **본 cycle 의 target**: `40x5 → resize(1,1) → resize(20,5)` 후 row 0 에 "ShrinkGrow" 완전 보존. 첫 resize(1,1) 은 metadata-only (1 <= 40, 1 <= 5) → cap 유지 (40, 5). 두 번째 resize(20,5) 은 metadata-only (20 <= 40, 5 <= 5) → cap 유지. Row 0 의 "ShrinkGrow" 가 backing storage 에 그대로 존재. `main()` 에서 comment 제거 필요. **FAIL → PASS 전환이 본 cycle 의 핵심 metric** |
| T8 | `test_cursor_propagation` | 기존 | `start_paint` 후 cursor 가 `(x=2, y=0)`. 변경 없음. PASS 유지 |
| **T9** | **`test_reshape_metadata_only`** | **신규** | `RenderFrame` 을 40x10 으로 allocate → write via start_paint → `reshape(20, 5)` → `cell_buffer.size() == 40 * 10` (변화 없음) 확인 + `cap_cols == 40 && cap_rows == 10` + `cols == 20 && rows_count == 5` + row 0 의 0..19 cell 이 여전히 존재 |
| **T10** | **`test_reshape_capacity_retention`** | **신규** | 40x5 allocate → write "CapTest" on row 0 → reshape(1, 1) → reshape(80, 10) → cap_cols == 80, cap_rows == 10, cell_buffer.size() == 800, row 0 의 "CapTest" 복구 (리얼로케이션 후 remap 경로 검증). 중간 reshape(1, 1) 에서 capacity 가 안 줄었음을 암묵적으로 확인 |
| **T11** | **`test_row_stride_after_shrink`** | **신규** | 120x30 allocate → write row 0 = "Row0", row 1 = "Row1" → reshape(60, 30) → `frame.row(0)[0..3]` == "Row0" and `frame.row(1)[0..3]` == "Row1". **Physical stride 가 cap_cols(120) 로 유지되는지** 를 행간 간섭 없이 검증 (만약 stride 를 logical cols 로 사용했다면 row 1 은 row 0 의 cell 60..119 를 overlap 함) |

**합계**: 기존 8 + 신규 3 = **11 test 모두 PASS** (T7 uncomment 포함).

### 5.2 Test File 변경 영역

```
tests/render_state_test.cpp:
  + static bool test_reshape_metadata_only() { ... }          // T9
  + static bool test_reshape_capacity_retention() { ... }     // T10
  + static bool test_row_stride_after_shrink() { ... }        // T11
  main():
    - // TEST(resize_shrink_then_grow_preserves_content);      // 주석 해제
    + TEST(resize_shrink_then_grow_preserves_content);
    + TEST(reshape_metadata_only);
    + TEST(reshape_capacity_retention);
    + TEST(row_stride_after_shrink);
```

### 5.3 Build & Run

```powershell
# Engine + test binary 빌드
powershell -ExecutionPolicy Bypass -File scripts/build_ghostwin.ps1 -Config Release

# Test 실행 (build_ghostwin.ps1 내부 또는 별도 스크립트)
# .claude/rules/build-environment.md 에 따라 scripts/build_*.ps1 사용 필수
```

Expected output:
```
=== RenderState Test Suite (S8) ===

[TEST] allocate_and_access... PASS
[TEST] start_paint_with_data... PASS
[TEST] second_paint_clean... PASS
[TEST] resize... PASS
[TEST] resize_preserves_content... PASS
[TEST] resize_grow_preserves_content... PASS
[TEST] resize_shrink_then_grow_preserves_content... PASS    ← 이전: disabled
[TEST] reshape_metadata_only... PASS                         ← 신규
[TEST] reshape_capacity_retention... PASS                    ← 신규
[TEST] row_stride_after_shrink... PASS                       ← 신규
[TEST] cursor_propagation... PASS

=== Results: 11 passed, 0 failed ===
```

### 5.4 Hardware Smoke (User verification)

Plan §4.1 Definition of Done 의 S1..S5 scenario 수행:

| # | Scenario | Expected |
|:-:|---|---|
| S1 | `scripts/build_wpf.ps1 -Config Release` → 앱 실행 → `Get-ChildItem` 실행 → Alt+V split | 좌측 pane 에 `Get-ChildItem` 출력 그대로, 우측 pane 에 새 shell |
| S2 | S1 초기 상태에서 Alt+H split | 상단 pane 에 기존 content 유지 |
| S3 | Alt+V → Alt+V → Alt+V 3회 연속 | 가장 왼쪽 pane 에 초기 shell content 유지 |
| S4 | Alt+V → (우측 pane focus) → Alt+H | 좌측 pane + 우측 상단 pane 모두 content 유지 |
| S5 | `Write-Output "Hello"` 실행 → Alt+V split | 좌측 pane 에 "Hello" 보임 |

**수용 기준**: 5 scenarios 중 **5 PASS**. 1개라도 FAIL 시 iterate 또는 RCA 추가.

### 5.5 Regression Guard

- `PaneNodeTests` (9/9) 유지
- `VtCore` test suite (10/10) 유지 — 변경 영역 밖
- WPF 빌드: 0 warning, 0 error

---

## 6. Rollback Plan

### 6.1 Rollback Trigger

다음 중 하나 발생 시 rollback:

1. **T5, T6 (기존 preserve test)** 이 FAIL
2. **T1..T4, T8 (기타 기존 test)** 중 1개라도 FAIL
3. **Hardware smoke** 에서 이전에 없던 새로운 crash 또는 visual artifact
4. WPF 빌드 warning/error 증가
5. `quad_builder` / `terminal_window` / `ghostwin_engine` 의 frame.row(r)[c] 접근에서 assertion 또는 out-of-bounds

### 6.2 Rollback Procedure

```bash
git revert <commit-of-split-content-loss-v2>
# 또는 구체적으로:
#   git checkout HEAD~1 -- src/renderer/render_state.h src/renderer/render_state.cpp tests/render_state_test.cpp
#   git commit -m "revert: split-content-loss-v2 until RCA"
```

단일 cycle 의 변경은 3 파일 (`render_state.h`, `render_state.cpp`, `render_state_test.cpp`) 에 국한되어 revert 가 안전. Parent fix `4492b5d` 는 건드리지 않으므로 되돌아가면 single-call content-preserving hotfix 상태가 유지됨.

### 6.3 Post-Rollback

- `test_resize_shrink_then_grow_preserves_content` 의 `main()` 주석 복원
- `docs/04-report/split-content-loss-v2.report.md` 에 rollback 사유 + 추가 RCA 필요 항목 기록
- CLAUDE.md Follow-up Cycles row 8 을 "rollback, pending new RCA" 로 업데이트

---

## 7. Edge Cases & Risk Response

### 7.1 Hidden cells beyond logical bounds

**시나리오**: `RenderFrame` 이 120x30 으로 grow 된 상태에서 `start_paint` 이 row 0 에 120 cell 쓰고, `reshape(60, 30)` 이후 다시 `reshape(120, 30)`.

- **기대 동작**: row 0 의 60..119 cell 이 복구됨 (hidden 이었지만 backing storage 에 존재)
- **실제 동작**: D8 의 remap 은 capacity 이내 reshape 에서는 실행 안 됨 (metadata-only) → hidden cell 생존 → **기대 동작 매치** ✓
- **주의**: Consumer 가 `cell_buffer` 직접 접근하지 않으므로 hidden cell 이 unintended 노출되는 일은 없음

### 7.2 Reshape 직후 start_paint 전에 consumer 가 `frame()` 접근

**시나리오**: `TerminalRenderState::resize` 직후 `_p.row(r)[c]` 접근.

- **_p 상태**: `_p.reshape(cols, rows)` 만 호출되었으므로 logical dims 는 새 값. `_p.cell_buffer` 는 metadata-only 또는 reallocate 후 remap 상태. **Row data 는 이전 `start_paint` 의 결과 그대로 존재**.
- **Consumer 영향**: `quad_builder.cpp` 는 `_p` 의 cell 을 read-only 로 사용. 이전 프레임의 content 가 그대로 보이는 상태 → Alt+V split 에서 좌측 pane 의 pre-split content 가 바로 노출 → **기대 동작** ✓
- **`start_paint` 이후**: `_api → _p` copy 가 `_api.dirty_rows` 기반으로 수행. Reshape 이후 caller (`TerminalRenderState::resize`) 가 `_api.set_row_dirty(r)` 를 모든 rows 에 set 하므로, 다음 `start_paint` 은 ghostty VT 의 `for_each_row` 가 report 하는 dirty row 만 memcpy → **stale 영역은 그대로 `_api` 에 남음, 하지만 `_p` copy loop 는 `_api` 의 모든 dirty row 를 `_p` 로 propagate**.
  - 주의: `for_each_row` 가 report 하지 않은 row 는 `_api` 의 기존 값을 계속 보유. 그 row 가 `_api.set_row_dirty(r)` 로 dirty 마킹 되었으므로 `_p` copy loop 는 **기존 `_api` 값을 `_p` 로 복사**. 결과적으로 `_p` 의 row data 가 `_api` 의 (VT update 이전) 값으로 갱신 — 이는 **reshape 이전의 `_p` 상태와 동등**. 즉 metadata-only reshape 에서 `_p` 가 불필요하게 stale 데이터로 overwrite 되는 loop 가 실행되지만, 결과는 동등. Correctness 유지, performance 는 marginal.
- **판정**: 동작 차이 없음. Risk 없음.

### 7.3 Cursor OOB after shrink

**시나리오**: `_api.cursor.x = 100`, reshape(50, 30) 이후 cursor.x 가 logical bounds (50) 밖.

- **현재 동작 (D10, D19)**: cursor 필드 변경 없음. `frame.cursor.x == 100` 인 채로 consumer 에 노출
- **Consumer 영향**: `quad_builder.cpp` 의 cursor 렌더 로직이 `cell_w_ * cursor.x + padding_left_` 로 위치 계산 → 화면 밖 pixel 에 그림. Clip out of sight, crash 없음
- **위험**: 시각적 artifact (cursor 가 잠시 안 보임) 가능. 실제로는 `start_paint` 이 VT 로부터 새 cursor 를 읽어오면서 즉시 보정됨
- **판정**: Non-critical. Follow-up `cursor-clamp-on-shrink` cycle 후보로 TODO

### 7.4 `allocate()` 가 `reshape()` 와 다른 ctor-only semantics

**시나리오**: 누군가 `RenderFrame` 인스턴스에 대해 `allocate()` 를 2번 호출.

- **현재 호출자**: `TerminalRenderState` ctor 내부에서만 사용 (`_api.allocate()`, `_p.allocate()`). 외부 호출 없음
- **위험**: 미래에 누군가 `allocate()` 를 reset 용도로 재호출하면 `cap_cols/rows` 가 리셋되어 용도 혼선
- **완화**: `allocate()` 에 `// Internal use only: initial allocation from TerminalRenderState ctor. For subsequent resizes, use reshape().` 주석 추가. Header 의 선언 위에도 comment block 로 명시
- **후속**: 필요 시 `allocate()` 를 `private` 으로 숨기고 `TerminalRenderState` 를 `friend` 선언 — 본 cycle scope 밖

### 7.5 `_p` 에 대한 cursor propagation

**시나리오**: `_api.cursor = old_api.cursor` 의 기존 동작은 `start_paint` 에서 `_p.cursor = _api.cursor` 로 propagate. Reshape 에서는 cursor 를 touch 안 함.

- **판정**: 현재 `TerminalRenderState::resize` 도 cursor 를 직접 만지지 않음 (`_api.cursor = old_api.cursor;` line 110, 121 은 snapshot → 새 buffer 로 복사하는 것). 신 설계에서는 cursor 도 `RenderFrame` 의 member 로 유지되고 `reshape` 이 건드리지 않으므로 **cursor 값 그대로 보존** → 동등한 동작

### 7.6 Thread safety

**현재 invariant**: `TerminalRenderState::resize` caller 는 `vt_mutex` holding 필요 (`render_state.h:62` 주석).

- `_api.reshape()` 은 `cell_buffer.assign` + 내부 pointer 재할당 → non-atomic
- `_p.reshape()` 도 동일
- `start_paint()` 은 `vt_mutex` 를 lock 후 `_api` 와 `_p` 둘 다 access
- **충돌 가능성**: `resize` 가 `vt_mutex` holding 중이면 `start_paint` 이 대기 → 충돌 없음
- **판정**: 기존 invariant 유지. 변경 없음

### 7.7 Empty dims (`0x0`)

**시나리오**: `reshape(0, 0)` 호출 시.

- **현재 hotfix 동작**: `old_api.rows_count = 0`, `old_api.cols = 0`, `copy_rows = 0`, `copy_cols = 0` → memcpy 실행 0회. `_api.allocate(0, 0)` → `cell_buffer.resize(0)`
- **신 설계**: `0 <= cap_cols && 0 <= cap_rows` (cap 이 이전에 positive 였다면) → metadata-only 로 `cols = 0, rows_count = 0` 설정. cell_buffer 는 이전 cap 크기 유지. `row(r)` 은 length 0 의 span 반환 → for loop 안 돌음
- **판정**: 동작 동등. `gw_surface_resize` 가 `cols < 1` 이면 `cols = 1` 로 clamp (ghostwin_engine.cpp:599-600) 하므로 실제로 0 이 들어올 일은 거의 없지만 ctor 직후의 race 에서만 가능. 안전

---

## 8. Memory Footprint Analysis

### 8.1 Footprint Formula

```
per_session_render_state_memory
  = 2 buffers (_api + _p)
  × cap_cols × cap_rows
  × sizeof(CellData)
```

### 8.2 CellData size

`src/common/cell_data.h` 를 확인해야 하지만, Plan §3.2 기준 추정치 ≈ 16 bytes (codepoints[4] + style_flags + fg_packed + bg_packed + cp_count + padding). 본 design 기준 값은 Do phase 에서 `sizeof(CellData)` 확인 후 Report 에 기록.

### 8.3 Scenario별 Footprint

| Scenario | cap_cols | cap_rows | Cells | Bytes (16B/cell) | 2 buffers |
|---|:-:|:-:|:-:|:-:|:-:|
| Initial (80x24) | 80 | 24 | 1920 | 30 KB | 60 KB |
| Standard (120x30) | 120 | 30 | 3600 | 58 KB | 115 KB |
| Large (200x60) | 200 | 60 | 12000 | 192 KB | 384 KB |
| Extreme (300x120) | 300 | 120 | 36000 | 576 KB | 1152 KB |
| Ultra (500x200, 4K monitor) | 500 | 200 | 100000 | 1600 KB | 3200 KB |

### 8.4 Multi-session Scaling

- 10 session × Standard (120x30): ≈ 1.15 MB 총
- 10 session × Large (200x60): ≈ 3.84 MB 총
- 10 session × Ultra: ≈ 32 MB 총

**판정**: Standard/Large 범위에서는 footprint 증가 허용 범위. Ultra (4K monitor, 500x200) 에서 session 당 3.2 MB 는 의도적으로 큰 window 를 사용한 user 의 trade-off.

### 8.5 Shrink Policy (Deferred)

현재 cycle 은 **capacity never shrinks**. 문제 발생 시 후속 cycle 에서:

- **Option SA**: 일정 시간 연속 `cols/rows < cap * 0.5` 이면 reshape 시 shrink
- **Option SB**: Explicit API (`RenderFrame::shrink_to_fit()`) 를 exposing 해서 client 가 trigger
- **Option SC**: Session close 직전 shrink (benefits 없음 — close 시 어차피 dealloc)

본 cycle scope 밖.

---

## 9. Implementation Order (Do Phase Preview)

1. **S1**: `src/renderer/render_state.h` 의 `RenderFrame` 에 `cap_cols/cap_rows` 추가 + `row(r)` stride 변경 + `allocate()` 수정 + `reshape()` 선언 추가
2. **S2**: `src/renderer/render_state.cpp` 에 `RenderFrame::reshape()` 정의 추가
3. **S3**: `src/renderer/render_state.cpp` 의 `TerminalRenderState::resize()` 재작성 (`_api.reshape + _p.reshape + set_row_dirty` 패턴)
4. **S4**: `tests/render_state_test.cpp` 에 T9/T10/T11 신규 test 추가
5. **S5**: `tests/render_state_test.cpp::main()` 에서 T7 주석 해제 + T9/T10/T11 등록
6. **S6**: `scripts/build_ghostwin.ps1 -Config Release` 로 빌드 (warning 0 확인)
7. **S7**: Test binary 실행 → 11/11 PASS 확인
8. **S8**: `scripts/build_wpf.ps1 -Config Release` 로 WPF 빌드 (0W/0E)
9. **S9**: 앱 실행 → Hardware smoke S1..S5 검증
10. **S10**: `git add` + commit (message: `fix(render): capacity-backed cell buffer for shrink-then-grow preservation`)
11. **S11**: (분리 커밋) Plan/Design 문서 완성 본
12. **S12**: `/pdca analyze split-content-loss-v2` 로 gap 검증

---

## 10. Coding Convention Compliance

### 10.1 C++ Style

- Namespace `ghostwin` (기존 유지)
- 소문자 snake_case 함수명: `reshape`, `row`, `set_row_dirty` (기존 스타일)
- `static_cast<size_t>` 로 uint16 × uint16 overflow 방지 (기존 `render_state.h:41` 스타일)
- `std::memcpy` with explicit `sizeof(CellData)` (기존 스타일)
- `std::move` / `std::vector::assign` (기존 `render_state.cpp:96-97` 스타일)
- `/// @file` 및 `///` doxygen 주석 (기존 스타일)

### 10.2 Build System

- `scripts/build_ghostwin.ps1` (engine + test binary) 사용 필수 (`.claude/rules/build-environment.md`)
- `scripts/build_wpf.ps1 -Config Release` (WPF 최종 빌드) 사용 필수 (`feedback_wpf_build_script.md`)
- 직접 `cmake --build build` 금지

### 10.3 Commit Rule

- English, lowercase start, no period
- 본 cycle 최종 commit: `fix(render): capacity-backed cell buffer for shrink-then-grow preservation`
- 또는 분리: (1) `refactor(render): introduce RenderFrame::reshape` + (2) `test(render): enable shrink-then-grow regression guards`
- No AI attribution, no `--no-verify`

---

## 11. Related Artifacts

- Plan: `docs/01-plan/features/split-content-loss-v2.plan.md` (v0.1, 2026-04-09)
- Parent hotfix: `4492b5d fix(render): preserve cell buffer across resize` (incomplete for Grid layout shrink-then-grow)
- Evidence commit: `6141005 test(render): add split-content-loss-v2 regression evidence`
- Source:
  - `src/renderer/render_state.h` (수정 대상)
  - `src/renderer/render_state.cpp` (수정 대상)
  - `tests/render_state_test.cpp` (수정 대상)
- Consumer verification (무수정):
  - `src/renderer/quad_builder.cpp:55-91`
  - `src/renderer/terminal_window.cpp:69-72`
  - `src/engine-api/ghostwin_engine.cpp:149-152`
- Downstream resize chain (무수정):
  - `src/engine-api/ghostwin_engine.cpp:581-605` (`gw_surface_resize`)
  - `src/session/session_manager.cpp:369-376` (`resize_session`)
- Prior cycle: `docs/archive/2026-04/first-pane-render-failure/` (Appendix A `4492b5d` amendment)
- Memory:
  - `project_split_content_loss_v2_pending.md`
  - `feedback_hypothesis_verification_required.md`
  - `feedback_wpf_build_script.md`
  - `feedback_no_workaround.md`

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-09 | Initial draft. RCA invariant 분석 (§2) + locked decisions 20건 (§3) + file-by-file diffs (§4) + test matrix 11 test (§5) + rollback plan (§6) + edge cases 7건 (§7) + memory footprint analysis (§8) + implementation order (§9) | 노수장 |
