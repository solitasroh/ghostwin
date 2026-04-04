#pragma once

// GhostWin Terminal — TitleBar Manager
// Presentation layer: AppWindowTitleBar + InputNonClientPointerSource
//
// cpp.md compliance:
//   - enum class WindowState (type safe)
//   - constexpr TitlebarParams[] lookup table (zero branches)
//   - Public API ≤ 6 (common.md ≤ 7)
//   - TitleBarConfig struct (params ≤ 3)
//   - Function pointer DI for sidebar width (no std::function, no TabSidebar dependency)
//   - Hybrid OCP: update_regions(span<RectInt32>) — new components don't require internal edits
//   - Rule of Zero (WinRT value-type, copy deleted)
//   - Functions ≤ 40 lines (compute helpers split)
//
// Thread ownership: all methods UI thread only.
// Include order: standard → third-party → project

#include <cstdint>
#include <span>
#include <vector>

#include <windows.h>

#undef GetCurrentTime
#include <winrt/base.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Microsoft.UI.h>
#include <winrt/Microsoft.UI.Input.h>
#include <winrt/Microsoft.UI.Windowing.h>

namespace ghostwin {

// ─── Window state (cpp.md: enum class over enum) ───

enum class WindowState : uint8_t { Normal, Maximized, Fullscreen };

// ─── State-dependent titlebar parameters (constexpr lookup, zero branches) ───

struct TitlebarParams {
    double height_dip;
    double top_padding_dip;
    bool   caption_visible;
    bool   drag_enabled;
    bool   sidebar_visible;
};

inline constexpr TitlebarParams kTitlebarParams[] = {
    /* Normal     */ { 48.0, 0.0, true,  true,  true  },
    /* Maximized  */ { 48.0, 7.0, true,  true,  true  },
    /* Fullscreen */ {  0.0, 0.0, false, false, false },
};

inline constexpr double kTitleBarHeightDip = 48.0;

// ─── Sidebar width query — function pointer DI (cpp.md: no std::function) ───

using SidebarWidthFn = double(*)(void* ctx);

// ─── Config (cpp.md: params ≤ 3 → struct) ───

struct TitleBarConfig {
    HWND hwnd = nullptr;                       // Window handle (from IWindowNative)
    SidebarWidthFn sidebar_width_fn = nullptr;  // Sidebar width query (DIP)
    void* sidebar_ctx = nullptr;               // non-owning
};

/// Custom titlebar manager — drag/passthrough regions, caption buttons, DPI.
///
/// SRP: titlebar region management only.
/// OCP: update_regions(span) — new components register passthrough rects externally.
///      GhostWinApp collects rects from all components and passes them in.
///      TitleBarManager never needs modification for new titlebar elements.
///      (10-agent vote: D 7:3 confirmed)
class TitleBarManager {
public:
    // ─── Public API (7 — common.md ≤ 7) ───

    void initialize(const TitleBarConfig& config);

    /// Recompute drag + passthrough regions.
    /// extra_passthrough: clickable rects from external components (OCP).
    /// Coordinates: physical pixels, window client area origin.
    void update_regions(std::span<const winrt::Windows::Graphics::RectInt32>
                        extra_passthrough = {});

    void update_caption_colors(bool dark_theme);
    void on_state_changed(WindowState new_state);
    void update_dpi(double new_scale);
    [[nodiscard]] double height_dip() const;
    [[nodiscard]] WindowState state() const;

    TitleBarManager() = default;
    ~TitleBarManager();
    TitleBarManager(const TitleBarManager&) = delete;
    TitleBarManager& operator=(const TitleBarManager&) = delete;

private:
    // WinRT projection types (COM RAII automatic)
    winrt::Microsoft::UI::Windowing::AppWindow app_window_{nullptr};
    winrt::Microsoft::UI::Input::InputNonClientPointerSource nonclient_src_{nullptr};

    WindowState state_ = WindowState::Normal;
    double scale_ = 1.0;

    SidebarWidthFn sidebar_width_fn_ = nullptr;
    void* sidebar_ctx_ = nullptr;
    winrt::event_token changed_token_{};

    // ─── Internal helpers (≤ 40 lines each) ───

    void setup_titlebar_properties();  // Tall, transparent buttons
    void apply_state();                // constexpr table lookup → apply

    /// DIP → physical pixel (inline, 1 line)
    [[nodiscard]] int32_t to_px(double dip) const {
        return static_cast<int32_t>(dip * scale_);
    }
};

} // namespace ghostwin
