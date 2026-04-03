// GhostWin Terminal — TitleBar Manager implementation
// Include order (cpp.md): standard → third-party → project

// Include order: project header first (brings <windows.h>), then WinRT extensions
#include "ui/titlebar_manager.h"
#include "common/log.h"

#include <vector>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.h>
#include <winrt/Microsoft.UI.Interop.h>

namespace ghostwin {

// ─── initialize (cpp.md: ≤ 40 lines) ───

void TitleBarManager::initialize(const TitleBarConfig& config) {
    sidebar_width_fn_ = config.sidebar_width_fn;
    sidebar_ctx_ = config.sidebar_ctx;

    // HWND → WindowId → AppWindow (via cppwinrt/winrt/Microsoft.UI.Interop.h)
    auto window_id = winrt::Microsoft::UI::GetWindowIdFromWindow(config.hwnd);
    app_window_ = winrt::Microsoft::UI::Windowing::AppWindow::GetFromWindowId(window_id);

    // AppWindowTitleBar: Tall (48 DIP), transparent caption buttons
    setup_titlebar_properties();

    // InputNonClientPointerSource for drag/passthrough regions
    try {
        nonclient_src_ = winrt::Microsoft::UI::Input::InputNonClientPointerSource
            ::GetForWindowId(window_id);
        LOG_I("titlebar", "InputNonClientPointerSource acquired");
    } catch (const winrt::hresult_error& e) {
        LOG_E("titlebar", "InputNonClientPointerSource FAILED: 0x%08X", static_cast<uint32_t>(e.code()));
        nonclient_src_ = nullptr;
    }

    // Initial DPI scale
    scale_ = GetDpiForWindow(config.hwnd) / 96.0;

    LOG_I("titlebar", "Initialized (height=%g dip, scale=%.2f)", kTitleBarHeightDip, scale_);
}

// ─── setup_titlebar_properties (cpp.md: ≤ 40 lines) ───

void TitleBarManager::setup_titlebar_properties() {
    if (!app_window_) return;
    try {
        auto tb = app_window_.TitleBar();
        tb.ExtendsContentIntoTitleBar(true);
        tb.PreferredHeightOption(
            winrt::Microsoft::UI::Windowing::TitleBarHeightOption::Tall);
        tb.ButtonBackgroundColor(winrt::Windows::UI::Colors::Transparent());
        tb.ButtonInactiveBackgroundColor(winrt::Windows::UI::Colors::Transparent());
    } catch (const winrt::hresult_error& e) {
        LOG_E("titlebar", "setup_titlebar_properties FAILED: 0x%08X",
              static_cast<uint32_t>(e.code()));
    }
}

// ─── update_regions: Hybrid OCP (cpp.md: ≤ 40 lines) ───

void TitleBarManager::update_regions(
        std::span<const winrt::Windows::Graphics::RectInt32> extra_passthrough) {
    if (!app_window_ || !nonclient_src_) return;

    const auto& p = kTitlebarParams[static_cast<int>(state_)];
    if (!p.drag_enabled) {
        // Fullscreen: clear all regions (empty array)
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Caption, {});
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough, {});
        return;
    }

    auto tb = app_window_.TitleBar();
    int32_t right_inset = tb.RightInset();
    int32_t height = tb.Height();
    auto size = app_window_.Size();

    // Sidebar width (DIP → physical px)
    double sidebar_dip = sidebar_width_fn_ ? sidebar_width_fn_(sidebar_ctx_) : 0.0;
    int32_t sidebar_px = to_px(sidebar_dip);

    // Caption (drag) region: Col 1 top, excluding caption buttons
    int32_t drag_w = size.Width - sidebar_px - right_inset;
    if (drag_w <= 0) drag_w = 1;  // prevent zero/negative width
    winrt::Windows::Graphics::RectInt32 drag_rect{
        sidebar_px, 0, drag_w, height
    };

    LOG_I("titlebar", "drag_rect={%d,%d,%d,%d} sidebar_px=%d rightInset=%d",
          drag_rect.X, drag_rect.Y, drag_rect.Width, drag_rect.Height,
          sidebar_px, right_inset);

    try {
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Caption, {&drag_rect, 1});
    } catch (const winrt::hresult_error& e) {
        LOG_E("titlebar", "SetRegionRects(Caption) FAILED: 0x%08X", static_cast<uint32_t>(e.code()));
        return;
    }

    // Passthrough: sidebar area + external component rects (OCP)
    std::vector<winrt::Windows::Graphics::RectInt32> passthrough;
    passthrough.reserve(1 + extra_passthrough.size());
    passthrough.push_back({0, 0, sidebar_px, size.Height});
    for (const auto& r : extra_passthrough)
        passthrough.push_back(r);

    try {
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough, passthrough);
    } catch (const winrt::hresult_error& e) {
        LOG_E("titlebar", "SetRegionRects(Passthrough) FAILED: 0x%08X", static_cast<uint32_t>(e.code()));
    }
}

// ─── update_caption_colors ───

void TitleBarManager::update_caption_colors(bool dark_theme) {
    if (!app_window_) return;
    try {
        auto tb = app_window_.TitleBar();
        auto fg = dark_theme ? winrt::Windows::UI::Colors::White()
                             : winrt::Windows::UI::Colors::Black();
        tb.ButtonForegroundColor(fg);
        tb.ButtonHoverForegroundColor(fg);
    } catch (const winrt::hresult_error& e) {
        LOG_E("titlebar", "update_caption_colors FAILED: 0x%08X",
              static_cast<uint32_t>(e.code()));
    }
}

// ─── on_state_changed ───

void TitleBarManager::on_state_changed(WindowState new_state) {
    if (state_ == new_state) return;
    state_ = new_state;
    apply_state();
    LOG_I("titlebar", "State → %d", static_cast<int>(state_));
}

// ─── apply_state: constexpr table lookup ───

void TitleBarManager::apply_state() {
    if (!app_window_ || !nonclient_src_) return;  // null guard (agent consensus fix)

    const auto& p = kTitlebarParams[static_cast<int>(state_)];
    if (!p.drag_enabled) {
        try {
            nonclient_src_.SetRegionRects(
                winrt::Microsoft::UI::Input::NonClientRegionKind::Caption, {});
            nonclient_src_.SetRegionRects(
                winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough, {});
        } catch (const winrt::hresult_error& e) {
            LOG_E("titlebar", "apply_state SetRegionRects FAILED: 0x%08X",
                  static_cast<uint32_t>(e.code()));
        }
    } else {
        update_regions();
    }
}

// ─── accessors ───

double TitleBarManager::height_dip() const {
    const auto& p = kTitlebarParams[static_cast<int>(state_)];
    return p.height_dip + p.top_padding_dip;
}

WindowState TitleBarManager::state() const { return state_; }

} // namespace ghostwin
