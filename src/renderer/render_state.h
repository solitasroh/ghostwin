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

/// Render frame data — flat buffer, contiguous memory.
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
