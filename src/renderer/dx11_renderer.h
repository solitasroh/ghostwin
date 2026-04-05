#pragma once

/// @file dx11_renderer.h
/// D3D11 GPU-accelerated renderer for GhostWin.
/// Phase 3: HWND swapchain, GPU instancing, glyph atlas.

#include "common/error.h"
#include "common/render_constants.h"
#include <cstdint>
#include <memory>
#include <mutex>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11ShaderResourceView;
struct IDXGISwapChain1;

namespace ghostwin {

class TerminalRenderState;

struct RendererConfig {
    HWND hwnd = nullptr;
    uint16_t cols = constants::kDefaultCols;
    uint16_t rows = constants::kDefaultRows;
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
};

struct CompositionConfig {
    uint32_t width = 800;
    uint32_t height = 600;
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
};

class DX11Renderer {
public:
    // Phase 3 PoC (HWND 기반)
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create(
        const RendererConfig& config, Error* out_error = nullptr);

    // Phase 4 WinUI3 (Composition 기반)
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create_for_composition(
        const CompositionConfig& config, Error* out_error = nullptr);

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
    void upload_and_draw(const void* instances, uint32_t count, uint32_t bg_count = 0);

    /// Set the glyph atlas SRV for text rendering.
    void set_atlas_srv(ID3D11ShaderResourceView* srv);


    /// Set background clear color (RGB, 0xRRGGBB). Thread-safe (atomic).
    void set_clear_color(uint32_t rgb);

    /// Swapchain resize (Main Thread).
    void resize_swapchain(uint32_t width_px, uint32_t height_px);

    /// Debug Layer report.
    void report_live_objects();

    [[nodiscard]] uint32_t backbuffer_width() const;
    [[nodiscard]] uint32_t backbuffer_height() const;

    /// Composition swapchain raw pointer.
    [[nodiscard]] IDXGISwapChain1* composition_swapchain() const;

    /// DComp surface handle (non-null = IGNORE mode, ClearType capable).
    [[nodiscard]] HANDLE composition_surface_handle() const;

    /// Access internal D3D11 device/context (for GlyphAtlas creation).
    ID3D11Device* device() const;
    ID3D11DeviceContext* context() const;

private:
    DX11Renderer();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
