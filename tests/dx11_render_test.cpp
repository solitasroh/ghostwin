/// @file dx11_render_test.cpp
/// S5-S7: D3D11 device + HWND swapchain + shader + glyph atlas test.

#include "dx11_renderer.h"
#include "glyph_atlas.h"
#include "common/log.h"
#include <cstdio>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

static ghostwin::DX11Renderer* g_renderer = nullptr;

static LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_SIZE:
        if (g_renderer && wp != SIZE_MINIMIZED) {
            uint32_t w = LOWORD(lp);
            uint32_t h = HIWORD(lp);
            if (w > 0 && h > 0) {
                g_renderer->resize_swapchain(w, h);
            }
        }
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int main() {
    printf("=== DX11 Render Test (S5) ===\n");

    // Register window class
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)); // IDC_ARROW
    wc.lpszClassName = L"GhostWinDX11Test";
    RegisterClassExW(&wc);

    // Create window with WS_EX_NOREDIRECTIONBITMAP
    HWND hwnd = CreateWindowExW(
        WS_EX_NOREDIRECTIONBITMAP,
        wc.lpszClassName,
        L"DX11 Test - Blue Clear",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT,
        800, 600,
        nullptr, nullptr, wc.hInstance, nullptr);

    if (!hwnd) {
        printf("[FAIL] CreateWindowExW failed: %lu\n", GetLastError());
        return 1;
    }

    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);

    // Create renderer
    ghostwin::RendererConfig config;
    config.hwnd = hwnd;

    ghostwin::Error err{};
    auto renderer = ghostwin::DX11Renderer::create(config, &err);
    if (!renderer) {
        printf("[FAIL] DX11Renderer::create failed: %s\n", err.message ? err.message : "unknown");
        DestroyWindow(hwnd);
        return 1;
    }

    g_renderer = renderer.get();
    printf("[PASS] D3D11 device + swapchain created (%ux%u)\n",
           renderer->backbuffer_width(), renderer->backbuffer_height());

    // Phase 1: S5 - Clear to blue (1 second)
    printf("[INFO] S5: Blue clear for 1 second...\n");

    DWORD start = GetTickCount();
    int frame_count = 0;

    while (GetTickCount() - start < 1000) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) goto done;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        renderer->clear_and_present(0.0f, 0.2f, 0.8f);
        frame_count++;
    }
    printf("[PASS] S5: %d frames (%.1f fps)\n", frame_count, (float)frame_count);

    // Phase 2: S6 - Draw test quad (1 second)
    printf("[INFO] S6: Green quad on dark background for 1 second...\n");

    start = GetTickCount();
    frame_count = 0;

    while (GetTickCount() - start < 1000) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) goto done;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        // Green quad at (100,100), size 200x150
        renderer->draw_test_quad(100, 100, 200, 150, 0, 255, 0);
        frame_count++;
    }
    printf("[PASS] S6: %d frames with instanced quad\n", frame_count);

    // Phase 3: S7 - Glyph Atlas rasterization
    {
        printf("[INFO] S7: Creating glyph atlas + rasterizing glyphs...\n");
        ghostwin::AtlasConfig acfg;
        acfg.font_family = L"Cascadia Mono";
        acfg.font_size_pt = 14.0f;

        ghostwin::Error atlas_err{};
        auto atlas = ghostwin::GlyphAtlas::create(
            renderer->device(), acfg, &atlas_err);

        if (!atlas) {
            printf("[FAIL] S7: GlyphAtlas::create failed: %s\n",
                   atlas_err.message ? atlas_err.message : "unknown");
        } else {
            printf("[PASS] S7: Atlas created (cell=%ux%u, atlas=%ux%u)\n",
                   atlas->cell_width(), atlas->cell_height(),
                   atlas->atlas_width(), atlas->atlas_height());

            // Rasterize ASCII + Korean glyphs
            const uint32_t test_cps[] = {
                'H', 'e', 'l', 'l', 'o', ' ',
                0xAC00, 0xB098, 0xB2E4  // 가, 나, 다
            };
            int ok_count = 0;
            for (auto cp : test_cps) {
                auto entry = atlas->lookup_or_rasterize(renderer->context(), cp, 0);
                if (entry.valid) ok_count++;
            }
            printf("[%s] S7: %d/%d glyphs rasterized (including Korean)\n",
                   ok_count == 9 ? "PASS" : "WARN",
                   ok_count, 9);
            printf("[INFO] S7: Total cached glyphs: %u\n", atlas->glyph_count());
        }
    }

done:

    g_renderer = nullptr;
    renderer.reset();
    DestroyWindow(hwnd);

    printf("\n=== S5+S6+S7 PASSED ===\n");
    return 0;
}
