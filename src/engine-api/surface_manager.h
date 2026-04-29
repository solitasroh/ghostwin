#pragma once

/// @file surface_manager.h
/// Thread-safe surface lifecycle management for pane-split rendering.
/// Phase 5-E: one SwapChain per pane, deferred destroy pattern.

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
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

    // ── M-13 IME composition overlay state (render thread only) ──
    // Edge-trigger source for diagnostic LOG_I (avoid per-frame log flooding).
    // Compared against Session::composition each frame; updated when state changes.
    std::wstring last_composition_text;
    uint32_t last_composition_caret_offset = 0;
    bool last_composition_active = false;

    // ── M-14 W3 non-VT visual invalidation tracker (render thread only) ──
    // Compared against SessionVisualState::snapshot().epoch each frame to
    // compute visual_dirty. Mutated only after a successful draw/present.
    // Default 0 so the first render (visual snapshot epoch starts at 1)
    // observes visual_dirty = true and issues one initial paint.
    uint32_t last_visual_epoch = 0;

    // ── M-16-C Phase A (D-03/D-06): per-surface dim overlay factor ──
    // 0.0 = active (no dim), 0.4 = unfocused (cmux unfocused-split-opacity).
    // UI thread writes inside gw_surface_focus (under SurfaceManager lock).
    // Render thread reads only (alpha-only blend pass), so M-14's reader
    // safety contract (FrameReadGuard / SessionVisualState) is preserved.
    std::atomic<float> dim_factor{0.0f};

    // ── M-16-C Phase C (D-12/D-13): cell-snap residual padding offset ──
    // gw_surface_resize computes (width_px % cell_width) and distributes
    // it evenly across the four sides (ghostty `window-padding-balance`).
    // QuadBuilder + cursor + IME + mouse hit-test all consume the same
    // 4-tuple so coordinate spaces stay aligned. Default 0 keeps existing
    // behavior until Phase C lands.
    std::atomic<uint32_t> pad_left{0};
    std::atomic<uint32_t> pad_top{0};
    std::atomic<uint32_t> pad_right{0};
    std::atomic<uint32_t> pad_bottom{0};
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
    /// Returns shared_ptr copies so the caller can hold a surface across a
    /// frame even if destroy() + flush happens concurrently. Mirrors the
    /// SessionManager ownership model (Tech Debt #16 defense-in-depth).
    std::vector<std::shared_ptr<RenderSurface>> active_surfaces();

    /// Render thread: release deferred-destroyed surfaces (after frame).
    void flush_pending_destroys();

    /// Find surface by ID (caller must hold mutex or use from known-safe context).
    RenderSurface* find(GwSurfaceId id);

    /// Thread-safe find (acquires mutex). Returns shared_ptr — caller can
    /// safely retain across lock-free regions.
    std::shared_ptr<RenderSurface> find_locked(GwSurfaceId id);

    /// Find surface by session ID (acquires mutex). Returns first match as
    /// shared_ptr.
    std::shared_ptr<RenderSurface> find_by_session(GwSessionId session_id);

    bool empty();

private:
    bool create_swapchain(RenderSurface* surf);
    bool create_rtv(RenderSurface* surf);

    ID3D11Device* device_;              // non-owning (renderer owns)
    ComPtr<IDXGIFactory2> factory_;     // ref-counted, cached (W-10)
    std::vector<std::shared_ptr<RenderSurface>> surfaces_;
    std::vector<std::shared_ptr<RenderSurface>> pending_destroy_;
    std::mutex mutex_;
    std::atomic<uint32_t> next_id_{1};
};
