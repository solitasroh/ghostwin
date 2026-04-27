#pragma once

/// @file render_state.h
/// _api/_p dual-state pattern for thread-safe render state management.
/// Flat buffer + dirty-row-only copy via bitset.
///
/// M-14 W2 (2026-04-21): `_p` reader safety via `shared_mutex` +
/// `FrameReadGuard` contract. Readers MUST go through `acquire_frame()`
/// (short hold) or `acquire_frame_copy()` (long scans). Writers
/// (`start_paint`, `resize`) acquire `unique_lock(frame_mutex_)` around
/// `_p` mutation. See `docs/02-design/features/m14-render-thread-safety.design.md`
/// §5.1 for the contract and lock ordering invariant.
///
/// Lock order (invariant, Design 5.1):
///   1. `vt_mutex` first, `frame_mutex_` second — never reverse
///   2. Readers hold ONLY `frame_mutex_` (NEVER `vt_mutex`)
///   3. Writers acquire `frame_mutex_` while holding `vt_mutex`

#include "common/render_constants.h"
#include "vt_core.h"
#include <cstdint>
#include <vector>
#include <bitset>
#include <mutex>
#include <shared_mutex>

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
        //
        // M-14 W2-d (2026-04-21): the empty-span defensive guard for
        // resize race has been removed. Reader safety is now guaranteed
        // by TerminalRenderState::frame_mutex_ — writers (start_paint /
        // resize) hold unique_lock while mutating _p, and readers hold
        // shared_lock via FrameReadGuard for the full read window.
        // Callers MUST obtain the enclosing RenderFrame reference through
        // acquire_frame() or acquire_frame_copy() per Design 5.1.
        size_t offset = static_cast<size_t>(r) * cap_cols;
        return { cell_buffer.data() + offset, cols };
    }
    std::span<const CellData> row(uint16_t r) const {
        size_t offset = static_cast<size_t>(r) * cap_cols;
        return { cell_buffer.data() + offset, cols };
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

/// M-14 W2: value-semantic snapshot of `_p`. Safe to read without any lock
/// — caller owns the copy. Use `TerminalRenderState::acquire_frame_copy()`
/// for long readers (multi-row scans, string building) to avoid blocking
/// writers with an extended shared_lock.
using RenderFrameCopy = RenderFrame;

/// M-14 W2: RAII shared_lock guard returned by
/// `TerminalRenderState::acquire_frame()`. Reader holds a shared_lock on
/// `frame_mutex_` for the lifetime of the guard; writers (`start_paint`,
/// `resize`) are blocked during this window.
///
/// Use for short iterations only (render hot path, single-cell query,
/// metadata read). For long scans use `acquire_frame_copy()` instead to
/// avoid writer starvation.
///
/// Lock order: the reader must NOT acquire `vt_mutex` while holding this
/// guard (Design 5.1 invariant).
class FrameReadGuard {
public:
    FrameReadGuard(std::shared_lock<std::shared_mutex> lock,
                   const RenderFrame& frame) noexcept
        : lock_(std::move(lock)), frame_(&frame) {}

    FrameReadGuard(FrameReadGuard&&) noexcept = default;
    FrameReadGuard& operator=(FrameReadGuard&&) noexcept = default;
    FrameReadGuard(const FrameReadGuard&) = delete;
    FrameReadGuard& operator=(const FrameReadGuard&) = delete;

    [[nodiscard]] const RenderFrame& get() const & noexcept { return *frame_; }
    [[nodiscard]] const RenderFrame& get() const && = delete;

private:
    std::shared_lock<std::shared_mutex> lock_;
    const RenderFrame* frame_;
};

/// _api/_p dual-state manager.
class TerminalRenderState {
public:
    TerminalRenderState(uint16_t cols, uint16_t rows);

    /// Render thread: update _api from VtCore, then copy dirty rows to _p.
    /// Locks the passed-in vt_mutex internally (minimal hold time).
    /// Caller passes ConPtySession::vt_mutex() — the single VT lock (ADR-006).
    ///
    /// M-14 W2: additionally acquires `unique_lock(frame_mutex_)` around the
    /// `_p` write block. Lock order: `vt_mutex` -> `frame_mutex_`.
    ///
    /// Returns true if there are dirty rows to render.
    bool start_paint(std::mutex& vt_mutex, VtCore& vt);

    /// M-14 W2: short-read accessor. Holds `shared_lock(frame_mutex_)` for
    /// the lifetime of the returned guard. Use for render hot path and
    /// short metadata queries. For long scans use `acquire_frame_copy()`.
    [[nodiscard]] FrameReadGuard acquire_frame() const;

    /// M-14 W2: copy-first snapshot. Takes `shared_lock` briefly, copies
    /// `_p` by value, releases the lock. Caller iterates the copy lock-free.
    /// Use for long row scans / string building to avoid writer starvation.
    [[nodiscard]] RenderFrameCopy acquire_frame_copy() const;

    // M-14 W3 (2026-04-21): force_all_dirty() removed. Non-VT visual
    // changes now use SessionVisualState snapshots (selection / IME /
    // activate); VT cell dirtiness is tracked by VtCore's own
    // for_each_row flags.
    // The per-frame "mark everything dirty" call in render_surface()
    // is gone — that was the dominant start_us cost per W1 baseline
    // (see docs/03-analysis/performance/m14-w1-baseline-idle.md §F2).

    /// Resize.
    /// PRECONDITION: caller MUST hold the same mutex used by start_paint for this
    /// instance (i.e. ConPtySession::vt_mutex()). This guarantees start_paint does
    /// not observe partially-reshaped _api state (cap_cols / cell_buffer non-atomic).
    ///
    /// M-14 W2: additionally acquires `unique_lock(frame_mutex_)` to block
    /// concurrent readers while `_p` is being reshaped. Lock order:
    /// `vt_mutex` -> `frame_mutex_`.
    ///
    /// Slow path may allocate memory + memcpy — fast path only touches metadata
    /// (RenderFrame::reshape is high-water-mark).
    void resize(uint16_t cols, uint16_t rows);

private:
    /// M-14 W2: guards `_p`. Multiple readers via `shared_lock`; writers
    /// (`start_paint`, `resize`) via `unique_lock`. `mutable` so const
    /// accessors (`acquire_frame*`) can take shared_lock.
    mutable std::shared_mutex frame_mutex_;

    RenderFrame _api;
    RenderFrame _p;
};

} // namespace ghostwin
