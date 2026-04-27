/// @file session_visual_state_test.cpp
/// M-14 follow-up: visual snapshot contract for selection + IME + epoch.

#include "session_visual_state.h"

#include <cstdio>

static int passed = 0, failed = 0;

#define TEST(name) \
    do { \
        std::printf("[TEST] %s... ", #name); \
        if (test_##name()) { std::printf("PASS\n"); ++passed; } \
        else { std::printf("FAIL\n"); ++failed; } \
    } while (0)

static bool test_snapshot_captures_composition_and_selection() {
    ghostwin::SessionVisualState state;
    state.set_selection(3, 4, 5, 6);
    state.set_composition(std::wstring(L"한글"), 1, true);

    const auto snap = state.snapshot();
    if (snap.epoch != 3) {
        std::printf("(epoch=%u != 3) ", snap.epoch);
        return false;
    }
    if (!snap.selection.active ||
        snap.selection.start_row != 3 || snap.selection.start_col != 4 ||
        snap.selection.end_row != 5 || snap.selection.end_col != 6) {
        std::printf("(selection mismatch) ");
        return false;
    }
    if (!snap.composition.active || snap.composition.text != L"한글" ||
        snap.composition.caret_offset != 1) {
        std::printf("(composition mismatch) ");
        return false;
    }
    return true;
}

static bool test_clear_operations_bump_epoch_and_reset_state() {
    ghostwin::SessionVisualState state;
    state.set_selection(1, 2, 3, 4);
    state.set_composition(std::wstring(L"abc"), 2, true);
    state.clear_selection();
    state.clear_composition();

    const auto snap = state.snapshot();
    if (snap.epoch != 5) {
        std::printf("(epoch=%u != 5) ", snap.epoch);
        return false;
    }
    if (snap.selection.active ||
        snap.selection.start_row != 0 || snap.selection.start_col != 0 ||
        snap.selection.end_row != 0 || snap.selection.end_col != 0) {
        std::printf("(selection not cleared) ");
        return false;
    }
    if (snap.composition.active || !snap.composition.text.empty() ||
        snap.composition.caret_offset != 0) {
        std::printf("(composition not cleared) ");
        return false;
    }
    return true;
}

static bool test_snapshot_is_value_copy() {
    ghostwin::SessionVisualState state;
    state.set_selection(10, 11, 12, 13);
    state.set_composition(std::wstring(L"old"), 1, true);

    const auto snap_before = state.snapshot();

    state.set_selection(20, 21, 22, 23);
    state.set_composition(std::wstring(L"new"), 2, true);

    if (snap_before.epoch != 3) {
        std::printf("(before.epoch=%u != 3) ", snap_before.epoch);
        return false;
    }
    if (snap_before.selection.start_row != 10 || snap_before.selection.end_row != 12) {
        std::printf("(before selection mutated) ");
        return false;
    }
    if (snap_before.composition.text != L"old" || snap_before.composition.caret_offset != 1) {
        std::printf("(before composition mutated) ");
        return false;
    }

    const auto snap_after = state.snapshot();
    if (snap_after.epoch != 5 || snap_after.selection.start_row != 20 ||
        snap_after.composition.text != L"new" || snap_after.composition.caret_offset != 2) {
        std::printf("(after snapshot mismatch) ");
        return false;
    }
    return true;
}

int main() {
    std::printf("=== SessionVisualState Test Suite ===\n\n");

    TEST(snapshot_captures_composition_and_selection);
    TEST(clear_operations_bump_epoch_and_reset_state);
    TEST(snapshot_is_value_copy);

    std::printf("\n=== Results: %d passed, %d failed ===\n", passed, failed);
    return failed > 0 ? 1 : 0;
}
