/// @file render_state.cpp
/// _api/_p dual-state with dirty-row-only snapshot.

#include "render_state.h"
#include "vt_bridge.h"
#include "common/log.h"
#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <string>

namespace ghostwin {

// split-content-loss-v2 smoke diagnostic.
//
// Gated by the GHOSTWIN_RESIZE_DIAG env var so it is completely silent
// in production unless explicitly enabled:
//   unset / "0" → no logging (one env-var read per process then cached)
//   "1"         → log a single line per reshape call with the cell
//                 content stats of _api / _p before and after
//
// Format:
//   [resize-diag] before=CxR cap=CxR row0_nz=N "..." → after=CxR cap=CxR row0_nz=N "..."
//
// row0_nz = number of cells in row 0 with cp_count > 0 (rough proxy for
// whether the PowerShell prompt is present). The "..." portion dumps
// the first up-to-16 codepoints of row 0 for direct text inspection.
static int resize_diag_level() {
    static int level = [] {
        const char* s = std::getenv("GHOSTWIN_RESIZE_DIAG");
        if (!s || !*s) return 0;
        if (*s == '0') return 0;
        return 1;
    }();
    return level;
}

// Total non-empty cell count across the entire logical view. If ANY row
// has rendered content, this will be > 0. Useful for detecting whether
// the cell buffer is holding prompt/output text at the time of a resize.
static uint32_t total_nonzero_count(const RenderFrame& f) {
    if (f.rows_count == 0 || f.cell_buffer.empty()) return 0;
    uint32_t n = 0;
    for (uint16_t r = 0; r < f.rows_count; r++) {
        const CellData* row = f.cell_buffer.data() +
                              static_cast<size_t>(r) * f.cap_cols;
        for (uint16_t c = 0; c < f.cols; c++) {
            if (row[c].cp_count > 0) n++;
        }
    }
    return n;
}

// Find the first row with any non-empty cell, and return its
// first-16-char text. If no row has content, returns row=-1.
static int first_text_row(const RenderFrame& f, std::string& out_text,
                          uint16_t max_chars = 24) {
    out_text.clear();
    if (f.rows_count == 0 || f.cell_buffer.empty()) {
        out_text = "(empty)";
        return -1;
    }
    for (uint16_t r = 0; r < f.rows_count; r++) {
        const CellData* row = f.cell_buffer.data() +
                              static_cast<size_t>(r) * f.cap_cols;
        uint16_t nz = 0;
        for (uint16_t c = 0; c < f.cols; c++) {
            if (row[c].cp_count > 0) { nz++; break; }
        }
        if (nz == 0) continue;
        // Dump up to max_chars codepoints of this row.
        out_text += '"';
        uint16_t limit = std::min<uint16_t>(f.cols, max_chars);
        for (uint16_t c = 0; c < limit; c++) {
            uint32_t cp = row[c].cp_count > 0 ? row[c].codepoints[0] : 0;
            if (cp == 0) out_text += '.';
            else if (cp < 0x20 || cp > 0x7e) out_text += '?';
            else out_text += static_cast<char>(cp);
        }
        out_text += '"';
        return r;
    }
    out_text = "(all zero)";
    return -1;
}

void RenderFrame::reshape(uint16_t new_c, uint16_t new_r) {
    // Fast path: new dims fit inside existing capacity. Just update the
    // logical view — the backing storage already holds the cell data at
    // offsets [0, new_r) × [0, new_c) (implicitly preserved because
    // row(r) uses the physical cap_cols stride, not the logical cols).
    if (new_c <= cap_cols && new_r <= cap_rows) {
        cols = new_c;
        rows_count = new_r;
        return;
    }

    // Slow path: at least one dim exceeds capacity. Grow capacity
    // monotonically (high-water mark), allocate a new backing buffer,
    // and row-by-row memcpy the overlap from the old storage.
    const uint16_t new_cap_c = std::max(cap_cols, new_c);
    const uint16_t new_cap_r = std::max(cap_rows, new_r);

    std::vector<CellData> new_buffer(
        static_cast<size_t>(new_cap_c) * new_cap_r, CellData{});

    // Copy overlap: bounded by min(logical, old_cap) so we never read
    // past the old storage. `rows_count` and `cols` here are the
    // pre-reshape logical view, which is always <= cap by invariant.
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

TerminalRenderState::TerminalRenderState(uint16_t cols, uint16_t rows) {
    _api.allocate(cols, rows);
    _p.allocate(cols, rows);
}

bool TerminalRenderState::start_paint(std::mutex& vt_mutex, VtCore& vt) {
    std::lock_guard lock(vt_mutex);

    // 1. Update ghostty render state from terminal (sets dirty flags)
    //    Do NOT call vt.update_render_state() -- it resets global dirty.
    //    Instead, call the raw C bridge directly.
    VtRenderState rs = vt.raw_render_state();
    VtTerminal    term = vt.raw_terminal();
    if (!rs || !term) return false;

    vt_bridge_update_render_state_no_reset(rs, term);

    // 2. Read row/cell data into _api via for_each_row
    //    for_each_row reads row-level dirty and resets each row after reading.
    int vt_dirty_reports = 0;
    int vt_rows_seen = 0;
    uint32_t vt_raw_nonzero = 0;       // VT's own cells with cp_count>0 (regardless of dirty flag)
    uint32_t api_written_nonzero = 0;  // non-zero cells that survived memcpy into _api
    size_t first_cells_size = 0;
    vt.for_each_row([this, &vt_dirty_reports, &vt_rows_seen,
                     &vt_raw_nonzero, &api_written_nonzero, &first_cells_size]
                    (uint16_t row_idx, bool dirty,
                     std::span<const CellData> cells) {
        vt_rows_seen++;
        if (row_idx == 0) first_cells_size = cells.size();
        // Count non-zero cells VT gave us regardless of dirty flag.
        for (const auto& c : cells) {
            if (c.cp_count > 0) vt_raw_nonzero++;
        }
        if (row_idx >= _api.rows_count) return;
        if (dirty) {
            vt_dirty_reports++;
            _api.set_row_dirty(row_idx);
            auto dst = _api.row(row_idx);
            size_t copy_cols = std::min<size_t>(cells.size(), dst.size());
            // defensive merge 제거됨 (2026-04-10, Round 2 합의).
            //
            // Round 1 (2026-04-10) 에서 cell-level merge 를 넣었던 이유는
            // 분할 직후 VT 가 cp_count=0 인 빈 row 를 돌려준다는 가정이었으나,
            // Agent 6/7/10 이 Screen.zig:1449 clearCells → @memset(blankCell())
            // 을 근거로 cls/clear/ESC[K/scroll/vim/tmux 의 정상 경로 또한 동일한
            // cp_count=0 cell 을 사용한다는 것을 empirical 로 확인했다.
            // 따라서 defensive merge 는 정상 clear/erase 경로를 깨뜨린다.
            //
            // 원래의 straight memcpy 로 복귀. 진단 카운터는 유지하여
            // 후속 디버깅에서 VT 가 무엇을 돌려주는지 관측 가능하게 한다.
            std::memcpy(dst.data(), cells.data(),
                        copy_cols * sizeof(CellData));
            for (size_t i = 0; i < copy_cols; i++) {
                if (cells[i].cp_count > 0) api_written_nonzero++;
            }
        }
    });

    if (resize_diag_level() > 0) {
        // One-shot summary log every ~60 calls when diag enabled.
        static thread_local int call_count = 0;
        call_count++;
        if (call_count <= 5 || call_count % 60 == 0 ||
            vt_raw_nonzero > 0 || api_written_nonzero > 0) {
            uint32_t api_total = total_nonzero_count(_api);
            LOG_I("start-paint-diag",
                  "call=%d vt_seen=%d vt_dirty=%d vt_raw_nz=%u api_written_nz=%u "
                  "_api_total=%u cells[0].size=%zu dims=%ux%u(cap %ux%u)",
                  call_count, vt_rows_seen, vt_dirty_reports,
                  vt_raw_nonzero, api_written_nonzero, api_total,
                  first_cells_size,
                  (unsigned)_api.cols, (unsigned)_api.rows_count,
                  (unsigned)_api.cap_cols, (unsigned)_api.cap_rows);
        }
    }

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
    // Content-preserving resize via RenderFrame::reshape (Option A:
    // backing-capacity pattern). Shrinks become metadata-only; grows
    // within capacity become metadata-only; grows beyond capacity grow
    // the backing storage monotonically (high-water mark) and remap
    // existing content row-by-row with the new stride.
    //
    // Fixes split-content-loss-v2: the WPF Grid layout's shrink-then-grow
    // chain during Alt+V split would drop the old buffer on the
    // intermediate ~1x1 resize when storage was bound to logical dims
    // (4492b5d hotfix). With capacity-backed storage, the backing buffer
    // outlives shrink/grow sequences as long as no dim exceeds the
    // historical maximum. See commit 6141005 for the regression test
    // (`test_resize_shrink_then_grow_preserves_content`).
    //
    // Caller contract (unchanged): must hold vt_mutex.

    // Diagnostic snapshot: record _api and _p state before reshape
    // (GHOSTWIN_RESIZE_DIAG=1).
    const bool diag = resize_diag_level() > 0;
    uint16_t before_cols = 0, before_rows = 0, before_cap_c = 0, before_cap_r = 0;
    uint32_t before_api_total = 0, before_p_total = 0;
    std::string before_api_text, before_p_text;
    int before_api_row = -1, before_p_row = -1;
    if (diag) {
        before_cols = _api.cols;
        before_rows = _api.rows_count;
        before_cap_c = _api.cap_cols;
        before_cap_r = _api.cap_rows;
        before_api_total = total_nonzero_count(_api);
        before_p_total = total_nonzero_count(_p);
        before_api_row = first_text_row(_api, before_api_text);
        before_p_row = first_text_row(_p, before_p_text);
    }

    _api.reshape(cols, rows);
    _p.reshape(cols, rows);

    // Mark all logical rows dirty so the next start_paint() propagates
    // the preserved cell data through to _p. Without this, ghostty VT's
    // for_each_row would only report rows it considers dirty — and a
    // bare terminal resize does NOT mark every row dirty (PowerShell
    // only redraws on the next prompt).
    for (uint16_t r = 0; r < rows; r++) {
        _api.set_row_dirty(r);
    }

    if (diag) {
        uint32_t after_api_total = total_nonzero_count(_api);
        uint32_t after_p_total = total_nonzero_count(_p);
        std::string after_api_text, after_p_text;
        int after_api_row = first_text_row(_api, after_api_text);
        int after_p_row = first_text_row(_p, after_p_text);
        LOG_I("resize-diag",
              "reshape %ux%u(cap %ux%u) -> %ux%u(cap %ux%u) | "
              "_api[total=%u->%u, first=r%d %s -> r%d %s] | "
              "_p[total=%u->%u, first=r%d %s -> r%d %s]",
              (unsigned)before_cols, (unsigned)before_rows,
              (unsigned)before_cap_c, (unsigned)before_cap_r,
              (unsigned)_api.cols, (unsigned)_api.rows_count,
              (unsigned)_api.cap_cols, (unsigned)_api.cap_rows,
              before_api_total, after_api_total,
              before_api_row, before_api_text.c_str(),
              after_api_row, after_api_text.c_str(),
              before_p_total, after_p_total,
              before_p_row, before_p_text.c_str(),
              after_p_row, after_p_text.c_str());
    }
}

} // namespace ghostwin
