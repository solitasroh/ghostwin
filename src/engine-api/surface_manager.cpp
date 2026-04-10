/// @file surface_manager.cpp
/// Thread-safe surface lifecycle with deferred destroy.

#include "surface_manager.h"
#include "common/log.h"

#include <algorithm>

static constexpr const char* kTag = "surface-mgr";

SurfaceManager::SurfaceManager(ID3D11Device* device, ComPtr<IDXGIFactory2> factory)
    : device_(device), factory_(std::move(factory)) {}

bool SurfaceManager::create_swapchain(RenderSurface* surf) {
    if (!device_ || !factory_) return false;

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = surf->width_px > 0 ? surf->width_px : 1;
    desc.Height = surf->height_px > 0 ? surf->height_px : 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;

    ComPtr<IDXGISwapChain1> sc1;
    HRESULT hr = factory_->CreateSwapChainForHwnd(
        device_, surf->hwnd, &desc, nullptr, nullptr, &sc1);
    if (FAILED(hr)) {
        LOG_E(kTag, "SwapChain creation failed: 0x%08lX", (unsigned long)hr);
        return false;
    }
    // HC-1 (first-pane-render-failure): IDXGISwapChain1 -> IDXGISwapChain2 cast
    // can fail on environments where the Windows 8.1+ interface is unavailable
    // (some VM GPU pass-through configs, WARP-only drivers). Previously this
    // was a silent failure — surf->swapchain would be null, gw_surface_create
    // would return 0, and the pane would render nothing with no diagnostic.
    //
    // DO NOT remove the LOG_E below. It is the *only* native diagnostic signal
    // for IDXGISwapChain1 -> IDXGISwapChain2 cast failures and was added to
    // close bisect-mode-termination R3 (silent failure path) — see
    // first-pane-render-failure design.md §0.1 C-8 / HC-1 lock-in. Removing it
    // restores the silent failure mode and re-opens R3.
    hr = sc1.As(&surf->swapchain);
    if (FAILED(hr)) {
        LOG_E(kTag, "IDXGISwapChain1->IDXGISwapChain2 cast failed: 0x%08lX (Win 8.1+ interface unavailable?)",
              (unsigned long)hr);
        return false;
    }

    factory_->MakeWindowAssociation(surf->hwnd, DXGI_MWA_NO_ALT_ENTER);
    return true;
}

bool SurfaceManager::create_rtv(RenderSurface* surf) {
    if (!device_ || !surf->swapchain) return false;
    surf->rtv.Reset();
    ComPtr<ID3D11Texture2D> backbuffer;
    HRESULT hr = surf->swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer));
    if (FAILED(hr)) return false;
    hr = device_->CreateRenderTargetView(backbuffer.Get(), nullptr, &surf->rtv);
    return SUCCEEDED(hr);
}

GwSurfaceId SurfaceManager::create(HWND hwnd, GwSessionId session_id,
                                    uint32_t w, uint32_t h) {
    auto surf = std::make_unique<RenderSurface>();
    surf->id = next_id_.fetch_add(1);
    surf->session_id = session_id;
    surf->hwnd = hwnd;
    surf->width_px = w > 0 ? w : 1;
    surf->height_px = h > 0 ? h : 1;

    if (!create_swapchain(surf.get())) return 0;
    if (!create_rtv(surf.get())) return 0;

    auto id = surf->id;

    {
        std::lock_guard lk(mutex_);
        surfaces_.push_back(std::move(surf));
    }

    LOG_I(kTag, "Surface %u created for session %u (%ux%u)", id, session_id, w, h);
    return id;
}

void SurfaceManager::destroy(GwSurfaceId id) {
    std::lock_guard lk(mutex_);
    auto it = std::find_if(surfaces_.begin(), surfaces_.end(),
        [id](const auto& s) { return s->id == id; });
    if (it == surfaces_.end()) return;

    LOG_I(kTag, "Surface %u deferred destroy", id);
    pending_destroy_.push_back(std::move(*it));
    surfaces_.erase(it);
}

void SurfaceManager::resize(GwSurfaceId id, uint32_t w, uint32_t h) {
    std::lock_guard lk(mutex_);
    auto* surf = find(id);
    if (!surf) return;
    surf->pending_w = w > 0 ? w : 1;
    surf->pending_h = h > 0 ? h : 1;
    surf->needs_resize.store(true, std::memory_order_release);
}

std::vector<RenderSurface*> SurfaceManager::active_surfaces() {
    std::lock_guard lk(mutex_);
    std::vector<RenderSurface*> result;
    result.reserve(surfaces_.size());
    for (auto& s : surfaces_)
        result.push_back(s.get());
    return result;
}

void SurfaceManager::flush_pending_destroys() {
    std::lock_guard lk(mutex_);
    pending_destroy_.clear();
}

RenderSurface* SurfaceManager::find(GwSurfaceId id) {
    for (auto& s : surfaces_)
        if (s->id == id) return s.get();
    return nullptr;
}

RenderSurface* SurfaceManager::find_locked(GwSurfaceId id) {
    std::lock_guard lk(mutex_);
    return find(id);
}

RenderSurface* SurfaceManager::find_by_session(GwSessionId session_id) {
    std::lock_guard lk(mutex_);
    for (auto& s : surfaces_)
        if (s->session_id == session_id) return s.get();
    return nullptr;
}

bool SurfaceManager::empty() {
    std::lock_guard lk(mutex_);
    return surfaces_.empty();
}
