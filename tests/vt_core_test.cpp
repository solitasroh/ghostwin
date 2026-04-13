#include "vt_core.h"
#include "vt_bridge.h"
#include <cstdio>
#include <cstring>
#include <cstdint>

static int g_passed = 0;
static int g_failed = 0;

static void report(const char* name, bool ok) {
    printf("[%s] %s\n", ok ? "PASS" : "FAIL", name);
    ok ? ++g_passed : ++g_failed;
}

// ─── T1: create and destroy ───
static bool test_create_destroy() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;
    if (vt->cols() != 80 || vt->rows() != 24) return false;
    return true;
}

// ─── T2: write plain text ───
static bool test_write_plain() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;
    const char* text = "Hello, GhostWin!";
    vt->write({reinterpret_cast<const uint8_t*>(text), strlen(text)});
    return true; // vt_write is void, no crash = pass
}

// ─── T3: write ANSI escape ───
static bool test_write_ansi() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;
    const char* text = "\x1b[31mHello\x1b[0m";
    vt->write({reinterpret_cast<const uint8_t*>(text), strlen(text)});
    return true;
}

// ─── T4: render state update returns dirty ───
static bool test_render_state() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    const char* text = "\x1b[31mHello\x1b[0m World";
    vt->write({reinterpret_cast<const uint8_t*>(text), strlen(text)});

    // Use no_reset API + check dirty through for_each_row
    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());
    bool any_dirty = false;
    vt->for_each_row([&](uint16_t, bool d, std::span<const ghostwin::CellData>) {
        if (d) any_dirty = true;
    });
    if (!any_dirty) return false;
    return true;
}

// ─── T5: resize ───
static bool test_resize() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;
    if (!vt->resize(120, 40)) return false;
    if (vt->cols() != 120 || vt->rows() != 40) return false;
    return true;
}

// ─── T6: create/destroy cycle (leak check) ───
static bool test_lifecycle_cycle() {
    for (int i = 0; i < 50; ++i) {
        auto vt = ghostwin::VtCore::create(80, 24, 0);
        if (!vt) return false;
        const char* text = "cycle";
        vt->write({reinterpret_cast<const uint8_t*>(text), 5});
        vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());
    }
    return true;
}

// ─── T7: dirty resets after read ───
static bool test_dirty_reset() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    const char* text = "data";
    vt->write({reinterpret_cast<const uint8_t*>(text), 4});

    // First update — should have dirty rows
    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());
    bool first_dirty = false;
    vt->for_each_row([&](uint16_t, bool d, std::span<const ghostwin::CellData>) {
        if (d) first_dirty = true;
    });
    if (!first_dirty) return false;

    // for_each_row resets row dirty; second update should be clean
    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());
    bool second_dirty = false;
    vt->for_each_row([&](uint16_t, bool d, std::span<const ghostwin::CellData>) {
        if (d) second_dirty = true;
    });
    if (second_dirty) return false;

    return true;
}

// ─── T8: korean_utf8_cell ───
// Write "한" (U+D55C) and verify CellData via for_each_row
static bool test_korean_utf8_cell() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    // "한" = U+D55C = UTF-8 {0xED, 0x95, 0x9C}
    const uint8_t han[] = {0xED, 0x95, 0x9C};
    vt->write({han, sizeof(han)});
    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());

    bool col0_ok = false;
    bool col1_spacer = false;
    bool checked = false;

    vt->for_each_row([&](uint16_t row_index, bool /*dirty*/,
                         std::span<const ghostwin::CellData> cells) {
        if (row_index != 0 || checked) return;
        checked = true;

        // col 0: should contain U+D55C with cp_count >= 1
        if (cells[0].cp_count >= 1 && cells[0].codepoints[0] == 0xD55C) {
            col0_ok = true;
        } else {
            printf("  (col0: cp_count=%d, cp[0]=0x%X) ",
                   cells[0].cp_count, cells[0].codepoints[0]);
        }

        // col 1: wide-char spacer — cp_count == 0
        if (cells[1].cp_count == 0) {
            col1_spacer = true;
        } else {
            printf("  (col1: cp_count=%d, cp[0]=0x%X) ",
                   cells[1].cp_count, cells[1].codepoints[0]);
        }
    });

    return col0_ok && col1_spacer;
}

// ─── T9: korean_backspace_vt ───
// Write "한", then BS+Space+BS, verify cell is cleared
static bool test_korean_backspace_vt() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    // Write "한" (U+D55C)
    const uint8_t han[] = {0xED, 0x95, 0x9C};
    vt->write({han, sizeof(han)});

    // Backspace sequence: BS SP BS (0x08 0x20 0x08)
    // Wide char occupies 2 columns, so we need two rounds of BS+SP+BS
    const uint8_t bs_seq[] = {0x08, 0x20, 0x08, 0x08, 0x20, 0x08};
    vt->write({bs_seq, sizeof(bs_seq)});

    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());

    bool cleared = false;
    bool checked = false;

    vt->for_each_row([&](uint16_t row_index, bool /*dirty*/,
                         std::span<const ghostwin::CellData> cells) {
        if (row_index != 0 || checked) return;
        checked = true;

        // After backspace, col 0 should be empty or space (cp_count==0 or codepoint==0x20)
        bool col0_empty = (cells[0].cp_count == 0) ||
                          (cells[0].cp_count == 1 && cells[0].codepoints[0] == 0x20);
        bool col1_empty = (cells[1].cp_count == 0) ||
                          (cells[1].cp_count == 1 && cells[1].codepoints[0] == 0x20);

        if (col0_empty && col1_empty) {
            cleared = true;
        } else {
            printf("  (col0: cp_count=%d cp[0]=0x%X, col1: cp_count=%d cp[0]=0x%X) ",
                   cells[0].cp_count, cells[0].codepoints[0],
                   cells[1].cp_count, cells[1].codepoints[0]);
        }
    });

    return cleared;
}

// ─── T10: korean_multiple_syllables ───
// Write "한글" and verify both characters occupy correct cell positions
static bool test_korean_multiple_syllables() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    // "한글" = U+D55C U+AE00
    // UTF-8: {0xED,0x95,0x9C, 0xEA,0xB8,0x80}
    const uint8_t hangul[] = {0xED, 0x95, 0x9C, 0xEA, 0xB8, 0x80};
    vt->write({hangul, sizeof(hangul)});
    vt_bridge_update_render_state_no_reset(vt->raw_render_state(), vt->raw_terminal());

    bool han_ok = false;   // col 0: U+D55C
    bool han_sp = false;   // col 1: spacer
    bool geul_ok = false;  // col 2: U+AE00
    bool geul_sp = false;  // col 3: spacer
    bool checked = false;

    vt->for_each_row([&](uint16_t row_index, bool /*dirty*/,
                         std::span<const ghostwin::CellData> cells) {
        if (row_index != 0 || checked) return;
        checked = true;

        // col 0: "한" (U+D55C)
        if (cells[0].cp_count >= 1 && cells[0].codepoints[0] == 0xD55C) {
            han_ok = true;
        } else {
            printf("  (col0: cp_count=%d, cp[0]=0x%X) ",
                   cells[0].cp_count, cells[0].codepoints[0]);
        }

        // col 1: spacer for "한"
        if (cells[1].cp_count == 0) {
            han_sp = true;
        } else {
            printf("  (col1: cp_count=%d) ", cells[1].cp_count);
        }

        // col 2: "글" (U+AE00)
        if (cells[2].cp_count >= 1 && cells[2].codepoints[0] == 0xAE00) {
            geul_ok = true;
        } else {
            printf("  (col2: cp_count=%d, cp[0]=0x%X) ",
                   cells[2].cp_count, cells[2].codepoints[0]);
        }

        // col 3: spacer for "글"
        if (cells[3].cp_count == 0) {
            geul_sp = true;
        } else {
            printf("  (col3: cp_count=%d) ", cells[3].cp_count);
        }
    });

    return han_ok && han_sp && geul_ok && geul_sp;
}

int main() {
    printf("=== GhostWin VtCore Test Suite ===\n\n");

    report("create_destroy",           test_create_destroy());
    report("write_plain",              test_write_plain());
    report("write_ansi",               test_write_ansi());
    report("render_state",             test_render_state());
    report("resize",                   test_resize());
    report("lifecycle_cycle",          test_lifecycle_cycle());
    report("dirty_reset",              test_dirty_reset());
    report("korean_utf8_cell",         test_korean_utf8_cell());
    report("korean_backspace_vt",      test_korean_backspace_vt());
    report("korean_multiple_syllables", test_korean_multiple_syllables());

    printf("\n=== Results: %d passed, %d failed ===\n", g_passed, g_failed);
    return g_failed > 0 ? 1 : 0;
}
