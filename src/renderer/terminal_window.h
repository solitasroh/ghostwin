#pragma once

/// @file terminal_window.h
/// Win32 HWND terminal window with message loop, keyboard input, and resize.
/// Phase 3 PoC -- replaced by WinUI3 in Phase 4.

#include "common/render_constants.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstdint>
#include <memory>

namespace ghostwin {

class ConPtySession;
class DX11Renderer;
class TerminalRenderState;
class GlyphAtlas;
class VtCore;

struct WindowConfig {
    uint16_t cols = constants::kDefaultCols;
    uint16_t rows = constants::kDefaultRows;
    const wchar_t* title = L"GhostWin Terminal";
};

class TerminalWindow {
public:
    [[nodiscard]] static std::unique_ptr<TerminalWindow> create(const WindowConfig& config);
    ~TerminalWindow();

    /// Run the message loop (blocking). Returns exit code.
    int run(ConPtySession& session, DX11Renderer& renderer,
            TerminalRenderState& state, GlyphAtlas& atlas);

    [[nodiscard]] HWND hwnd() const;

private:
    TerminalWindow();
    struct Impl;
    std::unique_ptr<Impl> impl_;
    static LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);
};

} // namespace ghostwin
