/// @file vt_bridge_cell_test.cpp
/// Phase 3 S2: Test row/cell iteration API.

#include "vt_bridge.h"
#include <cstdio>
#include <cstring>
#include <cstdlib>

static int tests_passed = 0;
static int tests_failed = 0;

#define TEST(name) \
    printf("[TEST] %s... ", #name); \
    if (test_##name()) { printf("PASS\n"); tests_passed++; } \
    else { printf("FAIL\n"); tests_failed++; }

/* Helper: create terminal, write content, update render state */
struct TestCtx {
    void* terminal;
    void* render_state;
};

static TestCtx setup(const char* content, uint16_t cols = 40, uint16_t rows = 5) {
    TestCtx ctx{};
    ctx.terminal = vt_bridge_terminal_new(cols, rows, 100);
    ctx.render_state = vt_bridge_render_state_new();
    if (!ctx.terminal || !ctx.render_state) {
        fprintf(stderr, "setup failed\n");
        exit(1);
    }
    if (content) {
        vt_bridge_write(ctx.terminal, (const uint8_t*)content, strlen(content));
    }
    vt_bridge_update_render_state_no_reset(ctx.render_state, ctx.terminal);
    return ctx;
}

static void teardown(TestCtx& ctx) {
    vt_bridge_render_state_free(ctx.render_state);
    vt_bridge_terminal_free(ctx.terminal);
}

/* ─── Tests ─── */

static bool test_row_iterator_basic() {
    auto ctx = setup("Hello\r\nWorld\r\n");
    VtRowIterator ri = vt_bridge_row_iterator_new();
    if (!ri) return false;
    int rc = vt_bridge_row_iterator_init(ri, ctx.render_state);
    if (rc != VT_OK) { vt_bridge_row_iterator_free(ri); teardown(ctx); return false; }

    int row_count = 0;
    while (vt_bridge_row_iterator_next(ri)) {
        row_count++;
    }

    vt_bridge_row_iterator_free(ri);
    teardown(ctx);
    return row_count == 5;  /* 5 rows terminal */
}

static bool test_cell_graphemes() {
    auto ctx = setup("ABC");
    VtRowIterator ri = vt_bridge_row_iterator_new();
    vt_bridge_row_iterator_init(ri, ctx.render_state);

    bool ok = false;
    if (vt_bridge_row_iterator_next(ri)) {  /* row 0 */
        VtCellIterator ci = vt_bridge_cell_iterator_new();
        vt_bridge_cell_iterator_init(ci, ri);

        /* First cell should be 'A' */
        if (vt_bridge_cell_iterator_next(ci)) {
            uint32_t count = vt_bridge_cell_grapheme_count(ci);
            if (count >= 1) {
                uint32_t cp[4];
                uint32_t n = vt_bridge_cell_graphemes(ci, cp, 4);
                ok = (n >= 1 && cp[0] == 'A');
            }
        }
        vt_bridge_cell_iterator_free(ci);
    }

    vt_bridge_row_iterator_free(ri);
    teardown(ctx);
    return ok;
}

static bool test_cell_style_flags() {
    /* Bold green text */
    auto ctx = setup("\033[1;32mBold\033[0m");
    VtRowIterator ri = vt_bridge_row_iterator_new();
    vt_bridge_row_iterator_init(ri, ctx.render_state);

    bool ok = false;
    if (vt_bridge_row_iterator_next(ri)) {
        VtCellIterator ci = vt_bridge_cell_iterator_new();
        vt_bridge_cell_iterator_init(ci, ri);

        if (vt_bridge_cell_iterator_next(ci)) {  /* 'B' cell */
            uint8_t flags = vt_bridge_cell_style_flags(ci);
            ok = (flags & VT_STYLE_BOLD) != 0;
        }
        vt_bridge_cell_iterator_free(ci);
    }

    vt_bridge_row_iterator_free(ri);
    teardown(ctx);
    return ok;
}

static bool test_cell_colors() {
    /* 24-bit orange foreground */
    auto ctx = setup("\033[38;2;255;128;0mX\033[0m");
    VtRowIterator ri = vt_bridge_row_iterator_new();
    vt_bridge_row_iterator_init(ri, ctx.render_state);

    bool ok = false;
    if (vt_bridge_row_iterator_next(ri)) {
        VtCellIterator ci = vt_bridge_cell_iterator_new();
        vt_bridge_cell_iterator_init(ci, ri);

        if (vt_bridge_cell_iterator_next(ci)) {
            VtColor fg = vt_bridge_cell_fg_color(ci, ctx.render_state);
            /* Should be orange: r=255, g=128, b=0 */
            ok = (fg.r == 255 && fg.g == 128 && fg.b == 0);
            if (!ok) {
                printf("(got r=%d g=%d b=%d) ", fg.r, fg.g, fg.b);
            }
        }
        vt_bridge_cell_iterator_free(ci);
    }

    vt_bridge_row_iterator_free(ri);
    teardown(ctx);
    return ok;
}

static bool test_dirty_tracking() {
    auto ctx = setup("Hello");
    VtRowIterator ri = vt_bridge_row_iterator_new();
    vt_bridge_row_iterator_init(ri, ctx.render_state);

    bool first_dirty = false;
    if (vt_bridge_row_iterator_next(ri)) {
        first_dirty = vt_bridge_row_is_dirty(ri);
    }

    vt_bridge_row_iterator_free(ri);
    teardown(ctx);
    return first_dirty;  /* first row should be dirty after write */
}

static bool test_cursor_info() {
    auto ctx = setup("Hi");
    VtCursorInfo cur = vt_bridge_get_cursor(ctx.render_state);

    bool ok = cur.visible && cur.in_viewport;
    /* After "Hi", cursor should be at x=2, y=0 */
    if (ok) {
        ok = (cur.x == 2 && cur.y == 0);
        if (!ok) printf("(cursor at %d,%d) ", cur.x, cur.y);
    }

    teardown(ctx);
    return ok;
}

int main() {
    printf("=== vt_bridge Cell Iteration Test Suite ===\n\n");

    TEST(row_iterator_basic);
    TEST(cell_graphemes);
    TEST(cell_style_flags);
    TEST(cell_colors);
    TEST(dirty_tracking);
    TEST(cursor_info);

    printf("\n=== Results: %d passed, %d failed ===\n",
           tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
