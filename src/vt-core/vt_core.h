#pragma once

/// @file vt_core.h
/// VtCore — libghostty-vt C API wrapper for GhostWin.
/// This is the ONLY public interface to the VT parser.
/// All libghostty-vt types are opaque behind this header.

#include <cstdint>
#include <cstddef>
#include <memory>
#include <span>
#include <functional>

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

/// Cell data for rendering (32 bytes).
struct CellData {
    uint32_t codepoints[4];  // 16B — max 4 codepoints per grapheme cluster
    uint32_t fg_packed;      //  4B — RGBA packed (r | g<<8 | b<<16 | a<<24)
    uint32_t bg_packed;      //  4B — RGBA packed
    uint8_t  cp_count;       //  1B — actual codepoint count
    uint8_t  style_flags;    //  1B — VT_STYLE_* bitmask
    uint8_t  _pad[6];        //  6B — padding to 32B
};
static_assert(sizeof(CellData) == 32, "CellData must be 32 bytes");

/// Cursor info for rendering.
struct CursorInfo {
    uint16_t x;
    uint16_t y;
    CursorStyle style;
    bool visible;
    bool blink;
    bool in_viewport;
};

/// Row iteration callback.
/// row_index: 0-based row number
/// dirty: whether this row has changed since last render
/// cells: span of CellData for this row's columns
using RowCallback = std::function<void(uint16_t row_index, bool dirty,
                                       std::span<const CellData> cells)>;

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

    // ─── Phase 3: Row/cell iteration ───

    /// Iterate all rows, calling callback for each.
    /// Caller must hold vt_mutex. update_render_state() must be called first.
    void for_each_row(RowCallback callback);

    /// Get cursor info from render state.
    [[nodiscard]] CursorInfo cursor_info() const;

    /// Raw render state handle (for start_paint).
    [[nodiscard]] void* raw_render_state() const;

private:
    VtCore();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
