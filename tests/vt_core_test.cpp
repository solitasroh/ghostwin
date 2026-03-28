#include "vt_core.h"
#include <cstdio>
#include <cstring>

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

    auto info = vt->update_render_state();
    // After writing data, state should be dirty
    if (info.dirty == ghostwin::DirtyState::Clean) return false;
    if (info.cols != 80 || info.rows != 24) return false;
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
        vt->update_render_state();
    }
    return true;
}

// ─── T7: dirty resets after read ───
static bool test_dirty_reset() {
    auto vt = ghostwin::VtCore::create(80, 24);
    if (!vt) return false;

    const char* text = "data";
    vt->write({reinterpret_cast<const uint8_t*>(text), 4});

    auto info1 = vt->update_render_state();
    if (info1.dirty == ghostwin::DirtyState::Clean) return false;

    // Second call without new writes — should be clean
    auto info2 = vt->update_render_state();
    if (info2.dirty != ghostwin::DirtyState::Clean) return false;

    return true;
}

int main() {
    printf("=== GhostWin VtCore Test Suite ===\n\n");

    report("create_destroy",   test_create_destroy());
    report("write_plain",      test_write_plain());
    report("write_ansi",       test_write_ansi());
    report("render_state",     test_render_state());
    report("resize",           test_resize());
    report("lifecycle_cycle",  test_lifecycle_cycle());
    report("dirty_reset",      test_dirty_reset());

    printf("\n=== Results: %d passed, %d failed ===\n", g_passed, g_failed);
    return g_failed > 0 ? 1 : 0;
}
