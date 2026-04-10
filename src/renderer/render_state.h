#pragma once

/// @file render_state.h
/// _api/_p dual-state pattern for thread-safe render state management.
/// Flat buffer + dirty-row-only copy via bitset.

#include "common/render_constants.h"
#include "vt_core.h"
#include <cstdint>
#include <vector>
#include <bitset>
#include <mutex>

namespace ghostwin {

/// Render frame data — flat buffer with separate logical view and physical
/// storage, inspired by std::vector's capacity/size distinction.
///
/// `cols` / `rows_count` are the *logical* view dimensions exposed to
/// consumers (quad_builder, terminal_window, ghostwin_engine). Iteration is
/// always bounded by these.
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
/// storage, shrink becomes a metadata-only update and the subsequent grow
/// simply re-exposes the still-present cells.
///
/// See commit `6141005` for the regression test
/// (`test_resize_shrink_then_grow_preserves_content`).
struct RenderFrame {
    std::vector<CellData> cell_buffer;  // cap_rows * cap_cols
    uint16_t cols = 0;         // logical view width (visible cells per row)
    uint16_t rows_count = 0;   // logical view height (visible rows)
    uint16_t cap_cols = 0;     // physical storage stride (monotonic grow)
    uint16_t cap_rows = 0;     // physical storage height (monotonic grow)

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

    /// Initial allocation: sets both logical and physical dimensions to
    /// (c, r). Intended for TerminalRenderState ctor only — subsequent
    /// resizes MUST go through `reshape()` so that content and capacity
    /// are preserved across shrink/grow cycles.
    void allocate(uint16_t c, uint16_t r) {
        cols = c;
        rows_count = r;
        cap_cols = c;
        cap_rows = r;
        cell_buffer.assign(static_cast<size_t>(c) * r, CellData{});
        dirty_rows.reset();
    }

    /// Content-preserving reshape:
    ///   - If `(new_c, new_r)` fits within `(cap_cols, cap_rows)`:
    ///       metadata-only (no memcpy, no reallocation). The existing cell
    ///       data at offsets `[0, new_r) × [0, new_c)` within the backing
    ///       storage is implicitly preserved because `row(r)` uses the
    ///       physical `cap_cols` stride.
    ///   - Otherwise:
    ///       grow capacity to `max(current, new)`, allocate a new backing
    ///       buffer, and row-by-row memcpy the overlap from the old
    ///       storage (bounded by logical `rows_count × cols`) to the new
    ///       storage. The old backing buffer is dropped.
    ///
    /// Dirty rows are NOT modified here — the caller
    /// (`TerminalRenderState::resize`) is responsible for marking rows
    /// dirty so the next `start_paint()` propagates `_api → _p`.
    void reshape(uint16_t new_c, uint16_t new_r);
};

/// _api/_p dual-state manager.
class TerminalRenderState {
public:
    TerminalRenderState(uint16_t cols, uint16_t rows);

    /// Render thread: update _api from VtCore, then copy dirty rows to _p.
    /// Locks vt_mutex internally (minimal hold time).
    /// Returns true if there are dirty rows to render.
    bool start_paint(std::mutex& vt_mutex, VtCore& vt);

    /// Render thread: read-only access to current frame (valid after start_paint).
    [[nodiscard]] const RenderFrame& frame() const { return _p; }

    /// Force all rows dirty (for IME composition overlay).
    void force_all_dirty() { _api.dirty_rows.set(); }

    /// Resize (caller must hold vt_mutex).
    void resize(uint16_t cols, uint16_t rows);

private:
    RenderFrame _api;
    RenderFrame _p;
};

} // namespace ghostwin
