#pragma once

/// @file session_visual_state.h
/// M-14 follow-up: consistent snapshot contract for non-VT visual state.

#include <cstdint>
#include <mutex>
#include <string>
#include <utility>

namespace ghostwin {

/// Selection range for DX11 render-time overlay (M-10c).
/// Mutated by UI/WndProc, snapshotted by render thread through
/// SessionVisualState::snapshot().
struct SelectionRange {
    int32_t start_row = 0, start_col = 0;
    int32_t end_row = 0, end_col = 0;
    bool active = false;
};

/// Renderer-facing IME composition state.
/// Mutated by UI/TSF, snapshotted by render thread through
/// SessionVisualState::snapshot().
struct ImeCompositionState {
    std::wstring text;
    uint32_t caret_offset = 0;
    bool active = false;

    void set(std::wstring value, uint32_t caret, bool is_active) {
        if (!is_active || value.empty()) {
            clear();
            return;
        }

        caret_offset = caret > value.size()
            ? static_cast<uint32_t>(value.size())
            : caret;
        text = std::move(value);
        active = true;
    }

    void clear() {
        text.clear();
        caret_offset = 0;
        active = false;
    }
};

/// Value-semantic copy returned to render/query code.
/// `epoch` and payload are captured under the same mutex, so they describe
/// one coherent visual state.
struct VisualStateSnapshot {
    ImeCompositionState composition;
    SelectionRange selection;
    uint32_t epoch = 1;
};

/// Owns non-VT visual state that can trigger redraw without VT dirtiness.
/// The render thread MUST consume this state through `snapshot()` so
/// payload + epoch stay consistent.
class SessionVisualState {
public:
    [[nodiscard]] VisualStateSnapshot snapshot() const {
        std::lock_guard lock(mutex_);
        return {composition_, selection_, epoch_};
    }

    void set_composition(std::wstring value, uint32_t caret, bool is_active) {
        std::lock_guard lock(mutex_);
        composition_.set(std::move(value), caret, is_active);
        ++epoch_;
    }

    void clear_composition() {
        std::lock_guard lock(mutex_);
        composition_.clear();
        ++epoch_;
    }

    void set_selection(int32_t start_row, int32_t start_col,
                       int32_t end_row, int32_t end_col) {
        std::lock_guard lock(mutex_);
        selection_.start_row = start_row;
        selection_.start_col = start_col;
        selection_.end_row = end_row;
        selection_.end_col = end_col;
        selection_.active = true;
        ++epoch_;
    }

    void clear_selection() {
        std::lock_guard lock(mutex_);
        selection_ = {};
        ++epoch_;
    }

    void bump_epoch() {
        std::lock_guard lock(mutex_);
        ++epoch_;
    }

private:
    mutable std::mutex mutex_;
    ImeCompositionState composition_;
    SelectionRange selection_;
    uint32_t epoch_ = 1;
};

} // namespace ghostwin
