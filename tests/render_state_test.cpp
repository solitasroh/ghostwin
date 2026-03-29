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
    TEST(cursor_propagation);

    printf("\n=== Results: %d passed, %d failed ===\n", passed, failed);
    return failed > 0 ? 1 : 0;
}
