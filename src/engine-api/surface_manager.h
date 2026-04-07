#pragma once

/// @file surface_manager.h
/// Thread-safe surface lifecycle management for pane-split rendering.
/// Phase 5-E: one SwapChain per pane, deferred destroy pattern.

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <d3d11.h>
#include <dxgi1_3.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

// Forward declarations from ghostwin_engine.h
using GwSurfaceId = uint32_t;
using GwSessionId = uint32_t;

// ── Render surface (one per pane) ──
struct RenderSurface {
    GwSurfaceId id = 0;
    GwSessionId session_id = 0;
    HWND hwnd = nullptr;
    ComPtr<IDXGISwapChain2> swapchain;
    ComPtr<ID3D11RenderTargetView> rtv;
    uint32_t width_px = 0;
    uint32_t height_px = 0;

    // Deferred resize (UI thread sets pending, render thread applies)
    uint32_t pending_w = 0;
    uint32_t pending_h = 0;
    std::atomic<bool> needs_resize{false};
};

/// Thread-safe surface manager with deferred destroy.
/// UI thread: create/destroy/resize (mutex-protected).
/// Render thread: active_surfaces() snapshot + flush_pending_destroys().
class SurfaceManager {
public:
    SurfaceManager(ID3D11Device* device, ComPtr<IDXGIFactory2> factory);

    /// Create a new surface with SwapChain + RTV. Returns 0 on failure.
    GwSurfaceId create(HWND hwnd, GwSessionId session_id,
                       uint32_t w, uint32_t h);

    /// Move surface to pending_destroy_ (deferred).
    void destroy(GwSurfaceId id);

    /// Set pending resize (render thread applies ResizeBuffers).
    void resize(GwSurfaceId id, uint32_t w, uint32_t h);

    /// Render thread: snapshot of active surface pointers (short lock).
    std::vector<RenderSurface*> active_surfaces();

    /// Render thread: release deferred-destroyed surfaces (after frame).
    void flush_pending_destroys();

    /// Find surface by ID (caller must hold mutex or use from known-safe context).
    RenderSurface* find(GwSurfaceId id);

    /// Thread-safe find (acquires mutex).
    RenderSurface* find_locked(GwSurfaceId id);

    bool empty();

private:
    bool create_swapchain(RenderSurface* surf);
    bool create_rtv(RenderSurface* surf);

    ID3D11Device* device_;              // non-owning (renderer owns)
    ComPtr<IDXGIFactory2> factory_;     // ref-counted, cached (W-10)
    std::vector<std::unique_ptr<RenderSurface>> surfaces_;
    std::vector<std::unique_ptr<RenderSurface>> pending_destroy_;
    std::mutex mutex_;
    std::atomic<uint32_t> next_id_{1};
};
