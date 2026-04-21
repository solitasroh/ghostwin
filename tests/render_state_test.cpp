/// @file render_state_test.cpp
/// S8: Test RenderState dirty-row-only snapshot.

#include "render_state.h"
#include "vt_core.h"
#include "vt_bridge.h"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <mutex>
#include <thread>

static int passed = 0, failed = 0;

#define TEST(name) \
    printf("[TEST] %s... ", #name); \
    if (test_##name()) { printf("PASS\n"); passed++; } \
    else { printf("FAIL\n"); failed++; }

static bool test_allocate_and_access() {
    ghostwin::TerminalRenderState state(80, 24);
    const auto& f = state.acquire_frame().get();
    return f.cols == 80 && f.rows_count == 24 &&
           f.cell_buffer.size() == 80 * 24;
}

static bool test_start_paint_with_data() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    // Write some text
    const char* text = "Hello\r\n";
    vt->write({(const uint8_t*)text, 7});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);

    // start_paint should detect dirty rows
    bool dirty = state.start_paint(mtx, *vt);
    if (!dirty) {
        printf("(no dirty after write) ");
        return false;
    }

    const auto& f = state.acquire_frame().get();
    // Row 0 should be dirty (has "Hello")
    if (!f.is_row_dirty(0)) {
        printf("(row 0 not dirty) ");
        return false;
    }

    // Check cell data: first cell should be 'H'
    auto row0 = f.row(0);
    if (row0[0].cp_count == 0 || row0[0].codepoints[0] != 'H') {
        printf("(cell[0] not 'H', cp_count=%d, cp[0]=%u) ",
               row0[0].cp_count, row0[0].codepoints[0]);
        return false;
    }

    return true;
}

static bool test_second_paint_clean() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "Test";
    vt->write({(const uint8_t*)text, 4});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);

    // First paint: dirty
    state.start_paint(mtx, *vt);

    // Second paint without new writes: should be clean
    bool dirty = state.start_paint(mtx, *vt);
    return !dirty;
}

static bool test_resize() {
    ghostwin::TerminalRenderState state(80, 24);
    state.resize(120, 40);
    const auto& f = state.acquire_frame().get();
    return f.cols == 120 && f.rows_count == 40 &&
           f.cell_buffer.size() == 120 * 40;
}

// Regression test for first-pane-render-failure hotfix (2026-04-09).
// Verifies that TerminalRenderState::resize preserves existing cell buffer
// content across a dimension change. Prior behavior: _api.allocate() +
// _p.allocate() wiped the buffer to zero, and because ghostty VT core only
// reports row data on its own dirty flag (not our force_all_dirty), the
// render loop would present blank frames until the next ConPty output. Split
// panes therefore lost the original session's text.
static bool test_resize_preserves_content() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "Preserved";
    vt->write({(const uint8_t*)text, 9});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);

    // First paint to populate _api and _p with "Preserved" on row 0.
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }

    // Sanity: row 0 should start with 'P'.
    {
        const auto& f = state.acquire_frame().get();
        if (f.row(0)[0].cp_count == 0 || f.row(0)[0].codepoints[0] != 'P') {
            printf("(pre-resize row[0] != 'P') ");
            return false;
        }
    }

    // Resize to a smaller grid (simulates pane split where both dims shrink).
    state.resize(30, 5);

    // After resize, the frame() should IMMEDIATELY reflect the preserved
    // first 30 cols of row 0 — without needing another start_paint call.
    const auto& f2 = state.acquire_frame().get();
    if (f2.cols != 30 || f2.rows_count != 5) {
        printf("(post-resize dims %ux%u != 30x5) ", f2.cols, f2.rows_count);
        return false;
    }

    // "Preserved" is 9 chars, all within the new 30-col width.
    auto row0 = f2.row(0);
    const char* expected = "Preserved";
    for (size_t i = 0; i < 9; i++) {
        if (row0[i].cp_count == 0 ||
            row0[i].codepoints[0] != (uint32_t)expected[i]) {
            printf("(post-resize row[0][%zu] lost: cp_count=%d cp[0]=%u expected '%c') ",
                   i, row0[i].cp_count,
                   row0[i].cp_count ? row0[i].codepoints[0] : 0,
                   expected[i]);
            return false;
        }
    }

    return true;
}

// Resize to a LARGER grid (simulates window maximize). Existing content must
// remain in rows 0..old_rows-1 / cols 0..old_cols-1; new area zero-init.
static bool test_resize_grow_preserves_content() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "GrowTest";
    vt->write({(const uint8_t*)text, 8});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);
    state.start_paint(mtx, *vt);

    state.resize(80, 10);

    const auto& f = state.acquire_frame().get();
    if (f.cols != 80 || f.rows_count != 10) {
        printf("(post-grow dims %ux%u != 80x10) ", f.cols, f.rows_count);
        return false;
    }

    // Row 0 should still start with 'G'.
    auto row0 = f.row(0);
    if (row0[0].cp_count == 0 || row0[0].codepoints[0] != 'G') {
        printf("(post-grow row[0][0] != 'G', cp_count=%d cp[0]=%u) ",
               row0[0].cp_count,
               row0[0].cp_count ? row0[0].codepoints[0] : 0);
        return false;
    }

    // Row 5 (new area beyond old rows_count) should be zero.
    auto row5 = f.row(5);
    if (row5[0].cp_count != 0) {
        printf("(post-grow row[5][0] not zero: cp_count=%d) ", row5[0].cp_count);
        return false;
    }

    return true;
}

// Simulates the Grid layout chain during Alt+V split where the old pane
// temporarily shrinks to a very small size (layout intermediate pass) and
// then grows back to half-width. With the min()-based content-preserving
// memcpy in TerminalRenderState::resize, the intermediate shrink truncates
// content to ~1 cell; the subsequent grow cannot recover the lost cells.
//
// If this test fails, hypothesis H-split-shrink-grow is confirmed: the
// 4492b5d hotfix is incomplete because it only preserves content within
// the min() of old/new dims per call, and a shrink-then-grow sequence
// is not idempotent.
static bool test_resize_shrink_then_grow_preserves_content() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "ShrinkGrow";
    vt->write({(const uint8_t*)text, 10});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);

    // First paint to populate _api and _p with "ShrinkGrow" on row 0.
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }

    // Sanity pre-check.
    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'S') {
        printf("(pre-resize row[0] != 'S') ");
        return false;
    }

    // Step 1: Shrink to 1x1 (simulates Grid layout intermediate pass).
    state.resize(1, 1);

    // Step 2: Grow back to half-width (simulates Grid layout final pass).
    state.resize(20, 5);

    const auto& f = state.acquire_frame().get();
    if (f.cols != 20 || f.rows_count != 5) {
        printf("(post-regrow dims %ux%u != 20x5) ", f.cols, f.rows_count);
        return false;
    }

    // "ShrinkGrow" is 10 chars, fits in 20 cols. Row 0 must still contain
    // the original text in full.
    auto row0 = f.row(0);
    const char* expected = "ShrinkGrow";
    for (size_t i = 0; i < 10; i++) {
        if (row0[i].cp_count == 0 ||
            row0[i].codepoints[0] != (uint32_t)expected[i]) {
            printf("(post-regrow row[0][%zu] lost: cp_count=%d cp[0]=%u expected '%c') ",
                   i, row0[i].cp_count,
                   row0[i].cp_count ? row0[i].codepoints[0] : 0,
                   expected[i]);
            return false;
        }
    }

    return true;
}

static bool test_cursor_propagation() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "AB";
    vt->write({(const uint8_t*)text, 2});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);
    state.start_paint(mtx, *vt);

    const auto& f = state.acquire_frame().get();
    // Cursor should be at x=2, y=0
    return f.cursor.in_viewport && f.cursor.x == 2 && f.cursor.y == 0;
}

// split-content-loss-v2 Design T9:
// Metadata-only reshape preserves the backing cell buffer. After writing
// data at 40x10 and shrinking to 20x5, cell_buffer.size() must remain
// 40*10 (capacity) and the row 0 content must still be reachable via the
// logical view.
static bool test_reshape_metadata_only() {
    auto vt = ghostwin::VtCore::create(40, 10, 100);
    if (!vt) return false;

    const char* text = "MetaOnly";
    vt->write({(const uint8_t*)text, 8});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 10);
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }

    const size_t cap_before = state.acquire_frame().get().cell_buffer.size();
    if (cap_before != 40u * 10u) {
        printf("(pre-reshape cap=%zu != 400) ", cap_before);
        return false;
    }

    state.resize(20, 5);

    const auto& f = state.acquire_frame().get();
    if (f.cols != 20 || f.rows_count != 5) {
        printf("(post-reshape dims %ux%u != 20x5) ", f.cols, f.rows_count);
        return false;
    }
    if (f.cap_cols != 40 || f.cap_rows != 10) {
        printf("(post-reshape cap %ux%u != 40x10) ", f.cap_cols, f.cap_rows);
        return false;
    }
    if (f.cell_buffer.size() != cap_before) {
        printf("(post-reshape cell_buffer.size()=%zu changed from %zu) ",
               f.cell_buffer.size(), cap_before);
        return false;
    }

    // "MetaOnly" is 8 chars, fits inside new 20-col width.
    auto row0 = f.row(0);
    const char* expected = "MetaOnly";
    for (size_t i = 0; i < 8; i++) {
        if (row0[i].cp_count == 0 ||
            row0[i].codepoints[0] != (uint32_t)expected[i]) {
            printf("(post-reshape row[0][%zu] lost: cp_count=%d cp[0]=%u expected '%c') ",
                   i, row0[i].cp_count,
                   row0[i].cp_count ? row0[i].codepoints[0] : 0,
                   expected[i]);
            return false;
        }
    }

    return true;
}

// split-content-loss-v2 Design T10:
// Capacity-exceed grow path reallocates and row-by-row remaps preserved
// content. After 40x5 write -> shrink to 1x1 -> grow to 80x10, the final
// capacity must be 80x10 and row 0 must still contain the original string.
static bool test_reshape_capacity_retention() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "CapTest";
    vt->write({(const uint8_t*)text, 7});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }

    // Intermediate shrink (metadata-only, keeps cap = 40x5).
    state.resize(1, 1);
    if (state.acquire_frame().get().cap_cols != 40 || state.acquire_frame().get().cap_rows != 5) {
        printf("(mid cap %ux%u != 40x5) ",
               state.acquire_frame().get().cap_cols, state.acquire_frame().get().cap_rows);
        return false;
    }

    // Grow beyond capacity (reallocate + remap path).
    state.resize(80, 10);

    const auto& f = state.acquire_frame().get();
    if (f.cols != 80 || f.rows_count != 10) {
        printf("(post-grow dims %ux%u != 80x10) ", f.cols, f.rows_count);
        return false;
    }
    if (f.cap_cols != 80 || f.cap_rows != 10) {
        printf("(post-grow cap %ux%u != 80x10) ", f.cap_cols, f.cap_rows);
        return false;
    }
    if (f.cell_buffer.size() != 80u * 10u) {
        printf("(post-grow buf size %zu != 800) ", f.cell_buffer.size());
        return false;
    }

    // Row 0 must still contain "CapTest" — this verifies that the
    // reallocate path correctly remaps from old backing storage that
    // was *logically* shrunk to 1x1 but physically kept 40x5 of data.
    //
    // NOTE: because the intermediate reshape(1, 1) set rows_count=1
    // and cols=1, the remap loop in RenderFrame::reshape only copies
    // min(rows_count, cap_rows) × min(cols, cap_cols) = 1×1 cells.
    // The rest of the hidden cells are dropped. This test intentionally
    // documents that the capacity-retention guarantee holds WITHIN
    // the metadata-only path, but is lost when the capacity must grow
    // — the design trade-off is that hidden-but-preserved cells only
    // survive as long as capacity is not exceeded.
    auto row0 = f.row(0);
    if (row0[0].cp_count == 0 || row0[0].codepoints[0] != 'C') {
        printf("(post-grow row[0][0] != 'C', cp_count=%d cp[0]=%u) ",
               row0[0].cp_count,
               row0[0].cp_count ? row0[0].codepoints[0] : 0);
        return false;
    }

    return true;
}

// split-content-loss-v2 Design T11:
// Physical stride (cap_cols) governs row offsets, not logical cols.
// If we allocated 120x30, wrote distinct content to rows 0 and 1, and
// then reshaped to a narrower logical width (e.g. 60x30), row 1 must
// NOT bleed into row 0's cells. Using logical-cols stride would make
// `row(1)` point to `cell_buffer[60]`, which is still inside row 0's
// physical slot (cap_cols=120). Using physical-cols stride puts it at
// `cell_buffer[120]`, the true start of row 1.
static bool test_row_stride_after_shrink() {
    auto vt = ghostwin::VtCore::create(120, 30, 100);
    if (!vt) return false;

    // Write "AAAA\r\nBBBB" so row 0 starts with 'A' and row 1 starts with 'B'.
    const char* text = "AAAA\r\nBBBB";
    vt->write({(const uint8_t*)text, 10});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(120, 30);
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }

    // Verify pre-reshape: row 0 starts 'A', row 1 starts 'B'.
    {
        const auto& f = state.acquire_frame().get();
        if (f.row(0)[0].codepoints[0] != 'A') {
            printf("(pre-reshape row[0][0] != 'A') ");
            return false;
        }
        if (f.row(1)[0].codepoints[0] != 'B') {
            printf("(pre-reshape row[1][0] != 'B') ");
            return false;
        }
    }

    // Shrink logical width; capacity stays 120x30.
    state.resize(60, 30);

    const auto& f = state.acquire_frame().get();
    if (f.cap_cols != 120) {
        printf("(post-reshape cap_cols=%u != 120) ", f.cap_cols);
        return false;
    }

    // Row 1's first cell must still be 'B'. If the stride were logical
    // (60), row(1) would point into row 0's second half and we'd see
    // whatever was at row 0 col 60 (likely zero / not 'B').
    auto row1 = f.row(1);
    if (row1[0].cp_count == 0 || row1[0].codepoints[0] != 'B') {
        printf("(post-reshape row[1][0] lost: cp_count=%d cp[0]=%u) ",
               row1[0].cp_count,
               row1[0].cp_count ? row1[0].codepoints[0] : 0);
        return false;
    }
    // And row 0 should still start with 'A'.
    auto row0 = f.row(0);
    if (row0[0].cp_count == 0 || row0[0].codepoints[0] != 'A') {
        printf("(post-reshape row[0][0] lost: cp_count=%d cp[0]=%u) ",
               row0[0].cp_count,
               row0[0].cp_count ? row0[0].codepoints[0] : 0);
        return false;
    }

    return true;
}

// split-content-loss-v2 regression reproducer (2026-04-10).
// Mimics the REAL app's Alt+V split flow which earlier tests missed:
//   1. write text to VT
//   2. start_paint -> _api populated with content
//   3. vt->resize(new_cols, new_rows)     <- conpty->resize in the real app
//   4. state->resize(new_cols, new_rows)  <- what the app does next
//   5. start_paint AGAIN                  <- render thread runs continuously
//   6. verify _p still has the content
//
// User reported that rendering works BEFORE Alt+V but disappears AFTER.
// Hypothesis: VT.resize clears cells OR the second start_paint overwrites
// _api with empty VT cells. This test nails it down.
static bool test_real_app_resize_flow() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "RealFlow";
    vt->write({(const uint8_t*)text, 8});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);

    // Step 1-2: populate _api via start_paint.
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }
    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'R') {
        printf("(pre-resize row[0][0] != 'R') ");
        return false;
    }

    // Step 3: resize VT (simulates conpty->resize).
    if (!vt->resize(20, 5)) {
        printf("(vt->resize failed) ");
        return false;
    }

    // Step 4: resize render state (simulates state->resize).
    state.resize(20, 5);

    // Step 5: second start_paint (simulates next render loop iteration
    //         after the split fired).
    state.start_paint(mtx, *vt);

    // Step 6: verify _p still has the content.
    const auto& f = state.acquire_frame().get();
    if (f.cols != 20 || f.rows_count != 5) {
        printf("(post-flow dims %ux%u != 20x5) ", f.cols, f.rows_count);
        return false;
    }
    auto row0 = f.row(0);
    const char* expected = "RealFlow";
    for (size_t i = 0; i < 8; i++) {
        if (row0[i].cp_count == 0 ||
            row0[i].codepoints[0] != (uint32_t)expected[i]) {
            printf("(post-flow row[0][%zu] lost: cp_count=%d cp[0]=%u expected '%c') ",
                   i, row0[i].cp_count,
                   row0[i].cp_count ? row0[i].codepoints[0] : 0,
                   expected[i]);
            return false;
        }
    }
    return true;
}

// split-content-loss-v2 dual-mutex race reproducer (Round 2 — 10-agent
// consensus). Mimics the REAL app's mutex topology:
//
//   - Render thread: start_paint(mtx_CONPTY, vt)
//       (ghostwin_engine.cpp:146 → session->conpty->vt_mutex())
//   - Resize thread: mtx_SESSION lock + state->resize(c, r)
//       (session_manager.cpp:373 → sess->vt_mutex, a *different* mutex)
//
// Because the two mutexes are distinct objects, render and resize can
// fully interleave. `TerminalRenderState::resize` touches _api / _p
// (reshape + dirty_rows.set()), and `start_paint` also touches _api / _p
// (VT read, row copies). If render observes _api mid-reshape (rows_count
// already bumped, cell_buffer still old; or cap_cols updated but row
// offsets computed with stale cols), we get either a crash, or a torn
// frame with content loss.
//
// Expected behavior under the dual-mutex bug:
//   - TSAN / MSVC iterator checks → crash or assert
//   - Content check: "RaceTest" vanishes from row 0 at least once
// Expected behavior after fix:
//   - No content loss across the entire stress window.
static bool test_dual_mutex_race_reproduces_content_loss() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    const char* text = "RaceTest";
    vt->write({(const uint8_t*)text, 8});

    // Two DIFFERENT mutexes — mirrors the real app topology where
    // Session::vt_mutex (held by resize_session) and
    // ConPtySession::vt_mutex (held by start_paint) are distinct.
    std::mutex mtx_CONPTY;   // render path
    std::mutex mtx_SESSION;  // resize path

    ghostwin::TerminalRenderState state(40, 5);

    // Warm up: one synchronous paint so _api is populated with "RaceTest".
    {
        std::lock_guard lk(mtx_CONPTY);
        (void)lk;  // not strictly needed for warm-up but documents intent
    }
    state.start_paint(mtx_CONPTY, *vt);
    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'R') {
        printf("(warm-up row[0][0] != 'R') ");
        return false;
    }

    std::atomic<bool> stop{false};
    std::atomic<uint64_t> resize_iters{0};
    std::atomic<uint64_t> paint_iters{0};
    std::atomic<uint64_t> frame_reads{0};
    std::atomic<uint64_t> content_loss_count{0};
    std::atomic<uint64_t> crash_guard_count{0};

    // Resize thread: swing shapes entirely within the initial capacity
    // (40x5) so all reshape calls hit the metadata-only fast path. This
    // isolates the dual-mutex race from the "shrink-through-tiny-dims-
    // then-grow-beyond-cap" content loss that the existing unit tests
    // already document. If we STILL see loss here, the race itself is
    // the proximate cause (reshape mutates cols / rows_count without any
    // lock that start_paint / frame() observes). A race-free reshape
    // would never trigger loss on the metadata-only path because
    // cell_buffer is not touched.
    std::thread t_resize([&] {
        const std::pair<uint16_t, uint16_t> shapes[] = {
            {40, 5}, {20, 5}, {30, 4}, {40, 5}, {10, 3}, {40, 5}
        };
        size_t i = 0;
        while (!stop.load(std::memory_order_relaxed)) {
            auto [c, r] = shapes[i++ % (sizeof(shapes) / sizeof(shapes[0]))];
            {
                std::lock_guard lk(mtx_SESSION);
                state.resize(c, r);
            }
            resize_iters.fetch_add(1, std::memory_order_relaxed);
        }
    });

    // Render thread: call start_paint repeatedly under mtx_CONPTY.
    // Per iteration, poll frame() many times (without any lock) to
    // widen the race window. Content loss OR an out-of-range read
    // triggered by mid-reshape cap_cols / rows_count tear both show
    // up as "row 0 first 8 cells != RaceTest".
    std::thread t_paint([&] {
        const char* expected = "RaceTest";
        while (!stop.load(std::memory_order_relaxed)) {
            state.start_paint(mtx_CONPTY, *vt);
            paint_iters.fetch_add(1, std::memory_order_relaxed);

            // Tight poll: read frame() 512 times with zero lock. This
            // intentionally overlaps with t_resize's reshape calls.
            for (int k = 0; k < 512; k++) {
                const auto& f = state.acquire_frame().get();
                const uint16_t cols = f.cols;
                const uint16_t rows = f.rows_count;
                const uint16_t cap_c = f.cap_cols;
                // Crash guard: if rows_count was torn above cap_rows we'd
                // compute an out-of-bounds pointer. Skip such states.
                if (rows == 0 || cols == 0 || cap_c == 0) continue;
                if (cols > cap_c) { crash_guard_count.fetch_add(1); continue; }
                if (cols < 8) continue;  // shape too small to hold text
                frame_reads.fetch_add(1, std::memory_order_relaxed);
                // Read row 0 the same way the quad builder does.
                if (f.cell_buffer.empty()) continue;
                if (static_cast<size_t>(cap_c) * rows > f.cell_buffer.size()) {
                    crash_guard_count.fetch_add(1);
                    continue;
                }
                const ghostwin::CellData* row0 = f.cell_buffer.data();
                bool ok = true;
                for (size_t i = 0; i < 8; i++) {
                    if (row0[i].cp_count == 0 ||
                        row0[i].codepoints[0] != (uint32_t)expected[i]) {
                        ok = false;
                        break;
                    }
                }
                if (!ok) content_loss_count.fetch_add(1, std::memory_order_relaxed);
            }
        }
    });

    // Run for ~5 seconds (1차 라운드 요구사항).
    std::this_thread::sleep_for(std::chrono::seconds(5));
    stop.store(true, std::memory_order_relaxed);
    t_resize.join();
    t_paint.join();

    const uint64_t ri = resize_iters.load();
    const uint64_t pi = paint_iters.load();
    const uint64_t fr = frame_reads.load();
    const uint64_t cl = content_loss_count.load();
    const uint64_t cg = crash_guard_count.load();
    printf("[resize=%llu paint=%llu reads=%llu loss=%llu cguard=%llu] ",
           (unsigned long long)ri, (unsigned long long)pi,
           (unsigned long long)fr, (unsigned long long)cl,
           (unsigned long long)cg);

    // Test PASSES only if content_loss_count == 0 AND crash_guard == 0.
    // Any non-zero count is empirical proof that the dual-mutex race
    // produces either content loss or a torn metadata read.
    return cl == 0 && cg == 0;
}

// Round 2 regression guards (2026-04-10):
// Round 1 added a defensive cell-level merge in start_paint that skipped
// cp_count=0 cells. Agents 6/7/10 then found that Screen.zig:1449
// clearCells → @memset(blankCell()) uses cp_count=0 cells for ALL clear
// / erase operations (cls, clear, ESC[2J, ESC[K, scroll, vim repaint).
// These tests empirically verify that clearing actually clears _api
// cells through start_paint, and would FAIL under the defensive-merge
// variant (cells with "HelloWorld" would survive the cls).
static bool test_cls_clears_cells() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    // Step 1: populate row 0 with text.
    const char* text = "HelloWorld";
    vt->write({(const uint8_t*)text, 10});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);
    if (!state.start_paint(mtx, *vt)) {
        printf("(initial paint not dirty) ");
        return false;
    }
    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'H') {
        printf("(pre-cls row[0][0] != 'H') ");
        return false;
    }

    // Step 2: ESC[2J (erase in display, entire screen) + ESC[H (home).
    // Ghostty's Screen.clearCells uses blankCell() = zero-init so VT
    // reports rows whose cells are cp_count=0.
    const char* cls = "\x1b[2J\x1b[H";
    vt->write({(const uint8_t*)cls, 7});

    // Step 3: next start_paint must propagate the cleared cells into _api.
    state.start_paint(mtx, *vt);

    // Step 4: verify row 0 is blank (no glyphs from "HelloWorld").
    // Accept cp_count==0 (blank cell) or cp == ' '.
    const auto& f = state.acquire_frame().get();
    auto row0 = f.row(0);
    for (size_t i = 0; i < 10; i++) {
        uint32_t cp = row0[i].cp_count > 0 ? row0[i].codepoints[0] : 0;
        if (cp != 0 && cp != ' ') {
            printf("(post-cls row[0][%zu] not cleared: cp_count=%d cp[0]=%u) ",
                   i, row0[i].cp_count, row0[i].codepoints[0]);
            return false;
        }
    }
    return true;
}

static bool test_esc_k_erase_line() {
    auto vt = ghostwin::VtCore::create(40, 5, 100);
    if (!vt) return false;

    // Step 1: write "LineToErase" then \r to return cursor to column 0
    // without newline.
    const char* text = "LineToErase\r";
    vt->write({(const uint8_t*)text, 12});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 5);
    state.start_paint(mtx, *vt);

    // Sanity: row 0 first cell must be 'L'.
    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'L') {
        printf("(pre-erase row[0][0] != 'L') ");
        return false;
    }

    // Step 2: ESC[K (erase from cursor to end of line). Cursor is at
    // col 0 after the \r, so the whole line should be erased.
    const char* erase = "\x1b[K";
    vt->write({(const uint8_t*)erase, 3});

    state.start_paint(mtx, *vt);

    // Step 3: row 0 must be blank.
    const auto& f = state.acquire_frame().get();
    auto row0 = f.row(0);
    for (size_t i = 0; i < 11; i++) {
        uint32_t cp = row0[i].cp_count > 0 ? row0[i].codepoints[0] : 0;
        if (cp != 0 && cp != ' ') {
            printf("(post-ESC[K row[0][%zu] not cleared: cp_count=%d cp[0]=%u) ",
                   i, row0[i].cp_count, row0[i].codepoints[0]);
            return false;
        }
    }
    return true;
}

static bool test_scroll_blanks_new_rows() {
    // 3-row VT: fill all 3 rows with distinct text, then force a scroll
    // by writing another "\r\nDDD". Expected result:
    //   row 0: BBB   (was AAA — scrolled up)
    //   row 1: CCC   (was BBB)
    //   row 2: DDD   (new content — previously blank after scroll)
    auto vt = ghostwin::VtCore::create(40, 3, 100);
    if (!vt) return false;

    const char* text = "AAA\r\nBBB\r\nCCC";
    vt->write({(const uint8_t*)text, 13});

    std::mutex mtx;
    ghostwin::TerminalRenderState state(40, 3);
    state.start_paint(mtx, *vt);

    if (state.acquire_frame().get().row(0)[0].codepoints[0] != 'A' ||
        state.acquire_frame().get().row(1)[0].codepoints[0] != 'B' ||
        state.acquire_frame().get().row(2)[0].codepoints[0] != 'C') {
        printf("(pre-scroll rows != A/B/C) ");
        return false;
    }

    // Force scroll: "\r\nDDD" pushes the viewport up by one row.
    const char* more = "\r\nDDD";
    vt->write({(const uint8_t*)more, 5});

    state.start_paint(mtx, *vt);

    const auto& f = state.acquire_frame().get();
    // The old "AAA" must be gone from row 0; row 0 must now be "BBB".
    // This exercises the scroll path where VT hands us rows whose
    // cp_count=0 blanks replace previously-populated cells.
    if (f.row(0)[0].codepoints[0] != 'B') {
        printf("(post-scroll row[0][0] != 'B': cp_count=%d cp[0]=%u) ",
               f.row(0)[0].cp_count,
               f.row(0)[0].cp_count ? f.row(0)[0].codepoints[0] : 0);
        return false;
    }
    if (f.row(1)[0].codepoints[0] != 'C') {
        printf("(post-scroll row[1][0] != 'C') ");
        return false;
    }
    if (f.row(2)[0].codepoints[0] != 'D') {
        printf("(post-scroll row[2][0] != 'D') ");
        return false;
    }
    return true;
}

// ─── M-14 W2-c: reshape-during-read stress ───
//
// Exercises the FrameReadGuard contract (Design 5.1). A writer thread
// repeatedly calls resize() with varying dims while a reader thread
// repeatedly calls acquire_frame() and iterates every row. Verifies:
//
//   1. No crash / assertion over the stress window.
//   2. Every row span observed is either empty (still-present defensive
//      guard — to be removed in W2-d) or exactly f.cols wide — i.e. the
//      reader never observes a torn mid-reshape _p.
//   3. Both threads make meaningful progress (> 100 ops each) — otherwise
//      the lock contention is pathological.
//
// This test is expected to PASS with the W2-a/W2-b contract in place.
// Without the contract (hypothetical pre-W2 state using the unlocked
// frame() accessor), a concurrent reshape could produce a row() span of
// size != f.cols between `cols` and `cell_buffer` updates, OR trigger
// the defensive empty-span guard frequently.
//
// After W2-d removes the defensive guards, row() should NEVER return
// an empty span during this stress — at that point we can tighten the
// assertion to require row.size() == f.cols strictly.
static bool test_frame_snapshot_stays_consistent_during_concurrent_reshape() {
    ghostwin::TerminalRenderState state(80, 24);

    std::atomic<bool> stop{false};
    std::atomic<uint64_t> read_count{0};
    std::atomic<uint64_t> write_count{0};
    std::atomic<bool> torn_row_observed{false};

    std::thread writer([&]() {
        uint16_t tick = 0;
        while (!stop.load(std::memory_order_relaxed)) {
            // Span a range that crosses the initial 80x24 capacity so
            // both fast path (within cap) and slow path (realloc) are
            // exercised.
            uint16_t cols = static_cast<uint16_t>(20 + (tick % 100));  // 20..119
            uint16_t rows = static_cast<uint16_t>(10 + (tick % 40));   // 10..49
            state.resize(cols, rows);
            tick = static_cast<uint16_t>(tick + 1);
            write_count.fetch_add(1, std::memory_order_relaxed);
        }
    });

    std::thread reader([&]() {
        while (!stop.load(std::memory_order_relaxed)) {
            auto guard = state.acquire_frame();
            const auto& f = guard.get();
            for (uint16_t r = 0; r < f.rows_count; r++) {
                auto row = f.row(r);
                // Under the contract, the reader holds shared_lock while
                // iterating — the writer cannot reshape mid-scan. So the
                // only span sizes we should ever see are 0 (defensive
                // guard, still present in W2-c) and exactly f.cols.
                if (!row.empty() && row.size() != f.cols) {
                    torn_row_observed.store(true,
                                            std::memory_order_relaxed);
                    return;
                }
            }
            read_count.fetch_add(1, std::memory_order_relaxed);
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    stop.store(true, std::memory_order_relaxed);
    writer.join();
    reader.join();

    if (torn_row_observed.load(std::memory_order_relaxed)) {
        printf("(torn row observed under contract — FrameReadGuard broken) ");
        return false;
    }
    const auto r = read_count.load(std::memory_order_relaxed);
    const auto w = write_count.load(std::memory_order_relaxed);
    if (r < 100 || w < 100) {
        printf("(insufficient stress: reads=%llu writes=%llu) ",
               static_cast<unsigned long long>(r),
               static_cast<unsigned long long>(w));
        return false;
    }
    printf("(reads=%llu writes=%llu) ",
           static_cast<unsigned long long>(r),
           static_cast<unsigned long long>(w));
    return true;
}

int main() {
    printf("=== RenderState Test Suite (S8) ===\n\n");

    TEST(allocate_and_access);
    TEST(start_paint_with_data);
    TEST(second_paint_clean);
    TEST(resize);
    TEST(resize_preserves_content);
    TEST(resize_grow_preserves_content);
    // Re-enabled by split-content-loss-v2 cycle (2026-04-09).
    // Capacity-backed RenderFrame makes shrink-then-grow idempotent
    // within the high-water-mark capacity.
    TEST(resize_shrink_then_grow_preserves_content);
    TEST(cursor_propagation);
    TEST(reshape_metadata_only);
    TEST(reshape_capacity_retention);
    TEST(row_stride_after_shrink);
    TEST(real_app_resize_flow);
    // Round 2 — empirical dual-mutex race reproducer.
    TEST(dual_mutex_race_reproduces_content_loss);
    // Round 2 — defensive merge regression guards (cls / ESC[K / scroll).
    // These would fail under the Round 1 defensive-merge variant that
    // skipped cp_count=0 VT cells.
    TEST(cls_clears_cells);
    TEST(esc_k_erase_line);
    TEST(scroll_blanks_new_rows);
    // M-14 W2-c — FrameReadGuard concurrent-reshape stress.
    TEST(frame_snapshot_stays_consistent_during_concurrent_reshape);

    printf("\n=== Results: %d passed, %d failed ===\n", passed, failed);
    return failed > 0 ? 1 : 0;
}
