/// @file render_state.cpp
/// _api/_p dual-state with dirty-row-only snapshot.

#include "render_state.h"
#include "vt_bridge.h"
#include <cstring>

namespace ghostwin {

TerminalRenderState::TerminalRenderState(uint16_t cols, uint16_t rows) {
    _api.allocate(cols, rows);
    _p.allocate(cols, rows);
}

bool TerminalRenderState::start_paint(std::mutex& vt_mutex, VtCore& vt) {
    std::lock_guard lock(vt_mutex);

    // 1. Update ghostty render state from terminal (sets dirty flags)
    //    Do NOT call vt.update_render_state() -- it resets global dirty.
    //    Instead, call the raw C bridge directly.
    void* rs = vt.raw_render_state();
    void* term = vt.raw_terminal();
    if (!rs || !term) return false;

    vt_bridge_update_render_state_no_reset(rs, term);

    // 2. Read row/cell data into _api via for_each_row
    //    for_each_row reads row-level dirty and resets each row after reading.
    vt.for_each_row([this](uint16_t row_idx, bool dirty,
                           std::span<const CellData> cells) {
        if (row_idx >= _api.rows_count) return;
        if (dirty) {
            _api.set_row_dirty(row_idx);
            auto dst = _api.row(row_idx);
            size_t copy_cols = std::min<size_t>(cells.size(), dst.size());
            std::memcpy(dst.data(), cells.data(), copy_cols * sizeof(CellData));
        }
    });

    // 3. Update cursor
    _api.cursor = vt.cursor_info();

    // 4. Reset global dirty state AFTER reading all rows
    vt_bridge_reset_dirty(rs);

    // 5. If nothing dirty, skip render
    if (!_api.any_dirty()) return false;

    // 6. Copy only dirty rows from _api to _p
    _p.dirty_rows = _api.dirty_rows;
    _p.cursor = _api.cursor;

    for (uint16_t r = 0; r < _api.rows_count; r++) {
        if (_api.is_row_dirty(r)) {
            auto src = _api.row(r);
            auto dst = _p.row(r);
            std::memcpy(dst.data(), src.data(), _api.cols * sizeof(CellData));
        }
    }

    // 7. Clear _api dirty
    _api.clear_all_dirty();

    return true;
}

void TerminalRenderState::resize(uint16_t cols, uint16_t rows) {
    _api.allocate(cols, rows);
    _p.allocate(cols, rows);
    // Mark all rows dirty so full redraw happens after resize
    for (uint16_t r = 0; r < rows; r++) {
        _api.set_row_dirty(r);
    }
}

} // namespace ghostwin
