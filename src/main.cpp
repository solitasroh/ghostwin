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

    // 1. Create Win32 window
    ghostwin::WindowConfig wcfg;
    wcfg.title = L"GhostWin Terminal";
    wcfg.cols = 80;
    wcfg.rows = 24;

    auto window = ghostwin::TerminalWindow::create(wcfg);
    if (!window) {
        LOG_E("main", "Failed to create window");
        return 1;
    }

    // 2. Create D3D11 renderer
    ghostwin::RendererConfig rcfg;
    rcfg.hwnd = window->hwnd();
    rcfg.cols = wcfg.cols;
    rcfg.rows = wcfg.rows;

    ghostwin::Error err{};
    auto renderer = ghostwin::DX11Renderer::create(rcfg, &err);
    if (!renderer) {
        LOG_E("main", "Failed to create renderer: %s", err.message);
        return 1;
    }

    // 3. Create glyph atlas
    ghostwin::AtlasConfig acfg;
    acfg.font_family = L"Cascadia Mono";
    acfg.font_size_pt = 14.0f;

    auto atlas = ghostwin::GlyphAtlas::create(renderer->device(), acfg, &err);
    if (!atlas) {
        LOG_E("main", "Failed to create glyph atlas: %s", err.message);
        return 1;
    }

    // Set atlas SRV on renderer
    renderer->set_atlas_srv(atlas->srv());

    // 4. Create ConPTY session
    ghostwin::SessionConfig scfg;
    scfg.cols = wcfg.cols;
    scfg.rows = wcfg.rows;
    scfg.on_exit = [](uint32_t code) {
        LOG_I("main", "Child process exited with code %u", code);
        PostQuitMessage(0);
    };

    auto session = ghostwin::ConPtySession::create(scfg);
    if (!session) {
        LOG_E("main", "Failed to create ConPTY session");
        return 1;
    }

    // 5. Create render state
    ghostwin::TerminalRenderState state(wcfg.cols, wcfg.rows);

    LOG_I("main", "All components created. Starting terminal (cell=%ux%u)...",
           atlas->cell_width(), atlas->cell_height());

    // 6. Run message loop (blocking)
    int exit_code = window->run(*session, *renderer, state, *atlas);

    LOG_I("main", "Terminal closed (exit_code=%d)", exit_code);
    return exit_code;
}
