#pragma once

/// @file dx11_renderer.h
/// D3D11 GPU-accelerated renderer for GhostWin.
/// Phase 3: HWND swapchain, GPU instancing, glyph atlas.

#include "common/error.h"
#include "common/render_constants.h"
#include "render_perf.h"
#include <cstdint>
#include <memory>
#include <mutex>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11RenderTargetView;
struct ID3D11ShaderResourceView;
namespace ghostwin {

class TerminalRenderState;

struct RendererConfig {
    HWND hwnd = nullptr;
    // Allow create() to succeed with hwnd==nullptr. When true, device and
    // pipeline are created but the bootstrap swapchain/RTV are skipped —
    // SurfaceManager creates per-pane swapchains later via bind_surface().
    // Used by first-pane-render-failure Option B to eliminate the race where
    // MainWindow had to own an HWND just to initialize the renderer.
    bool allow_null_hwnd = false;
    uint16_t cols = constants::kDefaultCols;
    uint16_t rows = constants::kDefaultRows;
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
};

class DX11Renderer {
public:
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create(
        const RendererConfig& config, Error* out_error = nullptr);

    ~DX11Renderer();

    DX11Renderer(const DX11Renderer&) = delete;
    DX11Renderer& operator=(const DX11Renderer&) = delete;

    /// Clear the backbuffer and present (S5 validation).
    void clear_and_present(float r, float g, float b);

    /// Draw a single test quad at (x,y) with given size and color (S6 validation).
    void draw_test_quad(int16_t x, int16_t y, uint16_t w, uint16_t h,
                        uint8_t r, uint8_t g, uint8_t b);

    /// Upload QuadInstances and draw (called from render thread).
    /// Upload QuadInstances and draw with Dual Source Blending.
    [[nodiscard]] bool upload_and_draw(const void* instances, uint32_t count,
                                       uint32_t bg_count = 0);

    /// M-14 W1 perf hook: same as `upload_and_draw()` but splits the Present
    /// blocking time from the rest. Intended to be called by the render
    /// thread ONLY when `perf_enabled()` is true — the timing split uses
    /// QueryPerformanceCounter and is serialized via the single render
    /// thread (no external synchronization needed).
    DrawPerfResult upload_and_draw_timed(const void* instances, uint32_t count,
                                         uint32_t bg_count = 0);

    /// Bind an external surface for rendering — swaps internal RTV/SwapChain/dimensions.
    /// After bind, upload_and_draw() renders to this surface (including Present).
    /// rtv = ID3D11RenderTargetView*, swapchain = IDXGISwapChain2* (cast to void* to avoid header dep)
    void bind_surface(void* rtv, void* swapchain, uint32_t width_px, uint32_t height_px);
    /// Unbind external surface — restores null state (Surface-only mode).
    void unbind_surface();

    /// Set the glyph atlas SRV for text rendering.
    void set_atlas_srv(ID3D11ShaderResourceView* srv);


    /// Set background clear color (RGB, 0xRRGGBB). Thread-safe (atomic).
    void set_clear_color(uint32_t rgb);

    /// Swapchain resize (Main Thread).
    void resize_swapchain(uint32_t width_px, uint32_t height_px);

    /// Release internal swapchain so SurfaceManager can create one for the same HWND.
    void release_swapchain();

    /// Debug Layer report.
    void report_live_objects();

    [[nodiscard]] uint32_t backbuffer_width() const;
    [[nodiscard]] uint32_t backbuffer_height() const;

    /// Access internal D3D11 device/context (for GlyphAtlas creation).
    ID3D11Device* device() const;
    ID3D11DeviceContext* context() const;

private:
    DX11Renderer();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
