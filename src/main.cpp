/// @file main.cpp
/// GhostWin Terminal — Phase 3 PoC entry point.
/// Integrates ConPTY + VtCore + D3D11 Renderer + Win32 Window.

#include "renderer/terminal_window.h"
#include "renderer/dx11_renderer.h"
#include "renderer/render_state.h"
#include "renderer/glyph_atlas.h"
#include "conpty/conpty_session.h"
#include "common/log.h"

#include <cstdio>

int main() {
    LOG_I("main", "GhostWin Terminal starting...");

    // 1. Create Win32 window first (to get HWND + client size)
    ghostwin::WindowConfig wcfg;
    wcfg.title = L"GhostWin Terminal";
    auto window = ghostwin::TerminalWindow::create(wcfg);
    if (!window) {
        LOG_E("main", "Failed to create window");
        return 1;
    }

    // 2. Create D3D11 renderer (needs HWND)
    ghostwin::RendererConfig rcfg;
    rcfg.hwnd = window->hwnd();
    ghostwin::Error err{};
    auto renderer = ghostwin::DX11Renderer::create(rcfg, &err);
    if (!renderer) {
        LOG_E("main", "Failed to create renderer: %s", err.message);
        return 1;
    }

    // 3. Create glyph atlas (needs D3D11 device) -> get cell metrics
    ghostwin::AtlasConfig acfg;
    acfg.font_family = L"Cascadia Mono";
    acfg.font_size_pt = 12.0f;
    auto atlas = ghostwin::GlyphAtlas::create(renderer->device(), acfg, &err);
    if (!atlas) {
        LOG_E("main", "Failed to create glyph atlas: %s", err.message);
        return 1;
    }
    renderer->set_atlas_srv(atlas->srv());

    // 4. Calculate cols/rows from actual window size + cell metrics
    RECT rc;
    GetClientRect(window->hwnd(), &rc);
    uint32_t win_w = rc.right - rc.left;
    uint32_t win_h = rc.bottom - rc.top;
    uint16_t cols = (uint16_t)(win_w / atlas->cell_width());
    uint16_t rows = (uint16_t)(win_h / atlas->cell_height());
    if (cols < 1) cols = 1;
    if (rows < 1) rows = 1;

    LOG_I("main", "Window %ux%u -> %ux%u cells (cell=%ux%u)",
           win_w, win_h, cols, rows, atlas->cell_width(), atlas->cell_height());

    // 5. Create ConPTY session with correct dimensions (no resize needed)
    ghostwin::SessionConfig scfg;
    scfg.cols = cols;
    scfg.rows = rows;
    scfg.on_exit = [](uint32_t code) {
        LOG_I("main", "Child process exited with code %u", code);
        PostQuitMessage(0);
    };
    auto session = ghostwin::ConPtySession::create(scfg);
    if (!session) {
        LOG_E("main", "Failed to create ConPTY session");
        return 1;
    }

    // 6. Create render state with matching dimensions
    ghostwin::TerminalRenderState state(cols, rows);

    LOG_I("main", "All components created. Starting terminal...");

    // 7. Run (blocking)
    int exit_code = window->run(*session, *renderer, state, *atlas);

    LOG_I("main", "Terminal closed (exit_code=%d)", exit_code);
    return exit_code;
}
