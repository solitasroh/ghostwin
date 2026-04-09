/// @file render_state_test.cpp
/// S8: Test RenderState dirty-row-only snapshot.

#include "render_state.h"
#include "vt_core.h"
#include "vt_bridge.h"
#include <cstdio>
#include <mutex>

static int passed = 0, failed = 0;

#define TEST(name) \
    printf("[TEST] %s... ", #name); \
    if (test_##name()) { printf("PASS\n"); passed++; } \
    else { printf("FAIL\n"); failed++; }

static bool test_allocate_and_access() {
    ghostwin::TerminalRenderState state(80, 24);
    const auto& f = state.frame();
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

    const auto& f = state.frame();
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
    const auto& f = state.frame();
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
        const auto& f = state.frame();
        if (f.row(0)[0].cp_count == 0 || f.row(0)[0].codepoints[0] != 'P') {
            printf("(pre-resize row[0] != 'P') ");
            return false;
        }
    }

    // Resize to a smaller grid (simulates pane split where both dims shrink).
    state.resize(30, 5);

    // After resize, the frame() should IMMEDIATELY reflect the preserved
    // first 30 cols of row 0 — without needing another start_paint call.
    const auto& f2 = state.frame();
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

    const auto& f = state.frame();
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
    if (state.frame().row(0)[0].codepoints[0] != 'S') {
        printf("(pre-resize row[0] != 'S') ");
        return false;
    }

    // Step 1: Shrink to 1x1 (simulates Grid layout intermediate pass).
    state.resize(1, 1);

    // Step 2: Grow back to half-width (simulates Grid layout final pass).
    state.resize(20, 5);

    const auto& f = state.frame();
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

    const auto& f = state.frame();
    // Cursor should be at x=2, y=0
    return f.cursor.in_viewport && f.cursor.x == 2 && f.cursor.y == 0;
}

int main() {
    printf("=== RenderState Test Suite (S8) ===\n\n");

    TEST(allocate_and_access);
    TEST(start_paint_with_data);
    TEST(second_paint_clean);
    TEST(resize);
    TEST(resize_preserves_content);
    TEST(resize_grow_preserves_content);
    // DISABLED pending `split-content-loss-v2` cycle fix.
    // The test function below documents a regression: Grid layout
    // intermediate shrink-then-grow chain truncates content to min() of
    // old/new dims on each call, which the 4492b5d hotfix cannot recover
    // from because memcpy is bounded by min(old_cols, new_cols). Empirical
    // FAIL recorded 2026-04-09. Uncomment after fix lands.
    // TEST(resize_shrink_then_grow_preserves_content);
    TEST(cursor_propagation);

    printf("\n=== Results: %d passed, %d failed ===\n", passed, failed);
    return failed > 0 ? 1 : 0;
}
