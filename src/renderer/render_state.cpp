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
    // Content-preserving resize.
    //
    // Previously this called `_api.allocate(cols, rows)` + `_p.allocate(...)`
    // which reset the 1D row-major cell_buffer to a fresh (zero) buffer.
    // Because ghostty VT's `for_each_row` only reports cell data on rows the
    // VT marks as dirty, and a bare terminal-size change does NOT mark every
    // row dirty (PowerShell, for example, only redraws on the next prompt),
    // the render loop would run for many frames against an empty _api/_p and
    // the surface would Present nothing but clear color.
    //
    // This was the "split 시 처음 열린 세션이 사라지고 분할된 것만 나와"
    // symptom reported after the first-pane-render-failure archive: splitting
    // triggers `gw_surface_resize` -> `session_mgr::resize_session` ->
    // `state->resize()` on the oldLeaf's existing session, which wiped the
    // preserved-session content even though the session itself (conpty + vt)
    // still held the real text.
    //
    // Fix: snapshot the old buffers, allocate the new ones, and manually
    // row-by-row memcpy `min(old_cols, new_cols)` cells for each of the
    // `min(old_rows, new_rows)` rows into the new row-major layout. New area
    // (when growing) stays zero. All rows are still marked dirty so the next
    // start_paint will push the preserved state through to _p without waiting
    // for ghostty to report row-level dirty bits.
    //
    // See: docs/04-report/first-pane-render-failure.report.md §8.5 follow-ups
    // — this closes the split-content-loss regression that evaluator 17:04
    // MQ-2 PASS (medium) falsely classified as "좌측 pane 어두운 배경".

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
        _api.cursor = old_api.cursor;
    }

    {
        const uint16_t copy_rows = std::min<uint16_t>(old_p.rows_count, rows);
        const uint16_t copy_cols = std::min<uint16_t>(old_p.cols, cols);
        for (uint16_t r = 0; r < copy_rows; r++) {
            auto src = old_p.row(r);
            auto dst = _p.row(r);
            std::memcpy(dst.data(), src.data(), copy_cols * sizeof(CellData));
        }
        _p.cursor = old_p.cursor;
    }

    // Mark all rows dirty so full redraw happens after resize
    for (uint16_t r = 0; r < rows; r++) {
        _api.set_row_dirty(r);
    }
}

} // namespace ghostwin
