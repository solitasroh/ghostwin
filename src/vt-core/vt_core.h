#pragma once

/// @file vt_core.h
/// VtCore — libghostty-vt C API wrapper for GhostWin.
/// This is the ONLY public interface to the VT parser.
/// All libghostty-vt types are opaque behind this header.

#include <cstdint>
#include <cstddef>
#include <memory>
#include <span>

namespace ghostwin {

/// Dirty state after render update.
enum class DirtyState : int {
    Clean   = 0,  // No changes, skip render
    Partial = 1,  // Some rows changed
    Full    = 2,  // Full redraw needed
};

/// Cursor visual style.
enum class CursorStyle : int {
    Bar        = 0,
    Block      = 1,
    Underline  = 2,
    BlockHollow = 3,
};

/// Render state snapshot from the terminal.
struct RenderInfo {
    DirtyState dirty;
    uint16_t cols;
    uint16_t rows;
    uint16_t cursor_x;
    uint16_t cursor_y;
    bool cursor_visible;
    CursorStyle cursor_style;
};

/// libghostty-vt C API wrapper.
/// Only vt_core.cpp includes the actual ghostty headers.
class VtCore {
public:
    /// Create a terminal instance (cols x rows).
    static std::unique_ptr<VtCore> create(uint16_t cols, uint16_t rows,
                                          size_t max_scrollback = 10000);

    ~VtCore();

    VtCore(const VtCore&) = delete;
    VtCore& operator=(const VtCore&) = delete;
    VtCore(VtCore&&) noexcept;
    VtCore& operator=(VtCore&&) noexcept;

    /// Feed VT data from ConPTY output into the parser.
    void write(std::span<const uint8_t> data);

    /// Update render state and return dirty/cursor info.
    RenderInfo update_render_state();

    /// Resize the terminal.
    bool resize(uint16_t cols, uint16_t rows);

    uint16_t cols() const;
    uint16_t rows() const;

private:
    VtCore();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
