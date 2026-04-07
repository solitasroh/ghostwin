// ghostwin_engine.cpp — C API wrapper for WPF Hybrid PoC
// All entry points are exception-guarded to prevent C++ exceptions from leaking to C#.

#include "ghostwin_engine.h"
#include "surface_manager.h"

#include "session/session_manager.h"
#include "renderer/dx11_renderer.h"
#include "renderer/glyph_atlas.h"
#include "renderer/quad_builder.h"
#include "renderer/render_state.h"
#include "vt-core/vt_core.h"
#include "common/log.h"
#include "common/render_constants.h"

#include <cstdio>
#include <d3d11.h>

#include <dxgi1_3.h>
#include <wrl/client.h>

#include <atomic>
#include <memory>
#include <mutex>
#include <span>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

static constexpr const char* kTag = "engine-api";

// ── Exception guard macros ──
#define GW_TRY try {
#define GW_CATCH_INT \
    } catch (const std::exception& e) { \
        FILE* _ef = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a"); \
        if (_ef) { fprintf(_ef, "EXCEPTION: %s\n", e.what()); fclose(_ef); } \
        return GW_ERR_INTERNAL; \
    } catch (...) { \
        FILE* _ef = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a"); \
        if (_ef) { fprintf(_ef, "UNKNOWN EXCEPTION\n"); fclose(_ef); } \
        return GW_ERR_INTERNAL; \
    }
#define GW_CATCH_VOID \
    } catch (const std::exception& e) { \
        LOG_E(kTag, "%s", e.what()); \
    } catch (...) { \
        LOG_E(kTag, "unknown exception"); \
    }

using namespace ghostwin;

// ── Internal engine state ──
struct EngineImpl {
    GwCallbacks callbacks{};

    std::unique_ptr<SessionManager> session_mgr;
    std::unique_ptr<DX11Renderer> renderer;
    std::unique_ptr<GlyphAtlas> atlas;
    std::vector<QuadInstance> staging;
    HANDLE frame_waitable = nullptr;

    // Render thread
    std::atomic<bool> render_running{false};
    std::thread render_thread;
    uint32_t renderer_clear_color = 0x1E1E2E;

    // TSF state
    HWND tsf_hwnd = nullptr;

    // Surface management (Phase 5-E pane split)
    std::unique_ptr<SurfaceManager> surface_mgr;
    GwSurfaceId focused_surface_id{0};

    // Bridge callbacks from SessionEvents to GwCallbacks
    SessionEvents make_session_events() {
        SessionEvents ev;
        ev.context = this;
        ev.on_created = [](void* ctx, SessionId id) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_created)
                eng->callbacks.on_created(eng->callbacks.context, id);
        };
        ev.on_closed = [](void* ctx, SessionId id) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_closed)
                eng->callbacks.on_closed(eng->callbacks.context, id);
        };
        ev.on_activated = [](void* ctx, SessionId id) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_activated)
                eng->callbacks.on_activated(eng->callbacks.context, id);
        };
        ev.on_title_changed = [](void* ctx, SessionId id, const std::wstring& title) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_title_changed)
                eng->callbacks.on_title_changed(eng->callbacks.context, id,
                    title.c_str(), static_cast<uint32_t>(title.size()));
        };
        ev.on_cwd_changed = [](void* ctx, SessionId id, const std::wstring& cwd) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_cwd_changed)
                eng->callbacks.on_cwd_changed(eng->callbacks.context, id,
                    cwd.c_str(), static_cast<uint32_t>(cwd.size()));
        };
        ev.on_child_exit = [](void* ctx, SessionId id, uint32_t exit_code) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_child_exit)
                eng->callbacks.on_child_exit(eng->callbacks.context, id, exit_code);
        };
        return ev;
    }

    void render_surface(RenderSurface* surf, QuadBuilder& builder) {
        auto* session = session_mgr->get(surf->session_id);
        if (!session || !session->conpty || !session->is_live()) return;

        // Deferred resize: render thread applies ResizeBuffers (C-7)
        if (surf->needs_resize.load(std::memory_order_acquire)) {
            surf->rtv.Reset();
            surf->swapchain->ResizeBuffers(0, surf->pending_w, surf->pending_h,
                DXGI_FORMAT_UNKNOWN, 0);
            ComPtr<ID3D11Texture2D> bb;
            surf->swapchain->GetBuffer(0, IID_PPV_ARGS(&bb));
            renderer->device()->CreateRenderTargetView(bb.Get(), nullptr, &surf->rtv);
            surf->width_px = surf->pending_w;
            surf->height_px = surf->pending_h;
            surf->needs_resize.store(false);
        }
        if (!surf->rtv) return;

        // Staging dynamic expansion (C-3)
        uint32_t needed = (surf->width_px / atlas->cell_width() + 1)
                        * (surf->height_px / atlas->cell_height() + 1)
                        * constants::kInstanceMultiplier + 16;
        if (staging.size() < needed) staging.resize(needed);

        auto& vt = session->conpty->vt_core();
        auto& state = *session->state;

        // Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex).
        // I/O thread writes to VT under ConPty mutex; render must use the SAME
        // mutex for visibility (design §4.5 — dual-mutex bug fix).
        state.force_all_dirty();
        bool dirty = state.start_paint(session->conpty->vt_mutex(), vt);
        if (!dirty) return;

        const auto& frame = state.frame();
        uint32_t bg_count = 0;
        uint32_t count = builder.build(frame, *atlas, renderer->context(),
            std::span<QuadInstance>(staging), &bg_count);

        // DIAG: log quad counts every second
        static uint32_t diag_frame = 0;
        diag_frame++;
        if (diag_frame % 60 == 0 || (count - bg_count) > 5) {
            LOG_I(kTag, "frame %u: total=%u bg=%u text=%u size=%ux%u",
                  diag_frame, count, bg_count, count - bg_count,
                  surf->width_px, surf->height_px);
        }

        if (count > 0) {
            renderer->bind_surface(
                static_cast<void*>(surf->rtv.Get()),
                static_cast<void*>(surf->swapchain.Get()),
                surf->width_px, surf->height_px);
            renderer->upload_and_draw(staging.data(), count, bg_count);
            renderer->unbind_surface();
        }
    }

    void render_loop() {
        QuadBuilder builder(
            atlas->cell_width(), atlas->cell_height(), atlas->baseline(),
            0, 0,  // glyph_offset_x/y
            0, 0); // padding

        while (render_running.load(std::memory_order_acquire)) {
            Sleep(16); // ~60fps

            if (!renderer) continue;

            // Surface path (Phase 5-E pane split)
            auto active = surface_mgr ? surface_mgr->active_surfaces()
                                      : std::vector<RenderSurface*>{};
            if (!active.empty()) {
                for (auto* surf : active) {
                    render_surface(surf, builder);
                }
            } else {
                // Legacy single-surface path (eed320d compatible)
                auto* session = session_mgr->active_session();
                if (!session || !session->conpty || !session->is_live()) {
                    Sleep(1); continue;
                }
                auto& vt = session->conpty->vt_core();
                auto& state = *session->state;
                static uint32_t leg_iter = 0;
                static uint32_t leg_dirty = 0;
                leg_iter++;
                bool dirty = state.start_paint(session->vt_mutex, vt);
                if (dirty) leg_dirty++;
                if (leg_iter % 60 == 0) {
                    LOG_I(kTag, "[LEGACY] iter %u: dirty_count=%u", leg_iter, leg_dirty);
                }
                if (!dirty) { Sleep(1); continue; }
                const auto& frame = state.frame();
                uint32_t bg_count = 0;
                uint32_t count = builder.build(frame, *atlas, renderer->context(),
                    std::span<QuadInstance>(staging), &bg_count);
                LOG_I(kTag, "[LEGACY] DRAW iter=%u: total=%u text=%u",
                      leg_iter, count, count - bg_count);
                if (count > 0)
                    renderer->upload_and_draw(staging.data(), count, bg_count);
            }

            // Deferred destroy: safe after snapshot usage complete
            surface_mgr->flush_pending_destroys();

            if (callbacks.on_render_done)
                callbacks.on_render_done(callbacks.context);
        }
    }
};

// ── Helpers ──
static EngineImpl* as_impl(GwEngine engine) {
    return static_cast<EngineImpl*>(engine);
}

// ── Engine lifecycle ──

GWAPI GwEngine gw_engine_create(const GwCallbacks* callbacks) {
    GW_TRY
        auto* eng = new EngineImpl();
        if (callbacks) eng->callbacks = *callbacks;
        eng->session_mgr = std::make_unique<SessionManager>(eng->make_session_events());
        return eng;
    GW_CATCH_VOID
    return nullptr;
}

GWAPI void gw_engine_destroy(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return;

        // Stop render thread first
        eng->render_running.store(false, std::memory_order_release);
        if (eng->render_thread.joinable())
            eng->render_thread.join();

        // Destroy surfaces before renderer (surfaces hold D3D resources)
        eng->surface_mgr.reset();
        // SessionManager destructor handles cleanup thread + sessions
        eng->session_mgr.reset();
        eng->atlas.reset();
        eng->renderer.reset();
        delete eng;
    GW_CATCH_VOID
}

// ── Render init ──

GWAPI int gw_render_init(GwEngine engine, HWND hwnd,
                          uint32_t width_px, uint32_t height_px,
                          float font_size_pt, const wchar_t* font_family) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;

        RendererConfig config;
        config.hwnd = hwnd;
        config.cols = static_cast<uint16_t>(width_px / 8);  // approximate
        config.rows = static_cast<uint16_t>(height_px / 16);
        config.font_size_pt = font_size_pt;
        if (font_family) config.font_family = font_family;

        // File-based debug log for PoC diagnostics
        FILE* dbg = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
        if (dbg) { fprintf(dbg, "render_init: hwnd=%p size=%ux%u font=%.1f\n",
            (void*)hwnd, width_px, height_px, font_size_pt); fclose(dbg); }

        Error err;
        eng->renderer = DX11Renderer::create(config, &err);
        if (!eng->renderer) {
            FILE* dbg2 = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
            if (dbg2) { fprintf(dbg2, "DX11Renderer::create FAILED: %s\n",
                err.message ? err.message : "unknown"); fclose(dbg2); }
            return GW_ERR_INTERNAL;
        }

        { FILE* dbg3 = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
          if (dbg3) { fprintf(dbg3, "DX11Renderer::create OK\n"); fclose(dbg3); } }

        AtlasConfig acfg;
        acfg.font_size_pt = font_size_pt;
        acfg.font_family = font_family ? font_family : L"Cascadia Mono";
        Error atlas_err;
        eng->atlas = GlyphAtlas::create(eng->renderer->device(), acfg, &atlas_err);
        if (!eng->atlas) {
            LOG_E(kTag, "GlyphAtlas::create failed: %s", atlas_err.message ? atlas_err.message : "unknown");
            return GW_ERR_INTERNAL;
        }

        eng->renderer->set_atlas_srv(eng->atlas->srv());

        // Initialize SurfaceManager with cached DXGI Factory (W-10)
        {
            ComPtr<IDXGIDevice1> dxgi_device;
            eng->renderer->device()->QueryInterface(IID_PPV_ARGS(&dxgi_device));
            ComPtr<IDXGIAdapter> adapter;
            dxgi_device->GetAdapter(&adapter);
            ComPtr<IDXGIFactory2> factory;
            adapter->GetParent(IID_PPV_ARGS(&factory));
            eng->surface_mgr = std::make_unique<SurfaceManager>(
                eng->renderer->device(), factory.Get());
        }

        // BISECT: keep renderer's SwapChain for legacy path
        // eng->renderer->release_swapchain();

        // Pre-allocate staging buffer
        uint16_t cols = static_cast<uint16_t>(width_px / eng->atlas->cell_width());
        uint16_t rows_count = static_cast<uint16_t>(height_px / eng->atlas->cell_height());
        if (cols < 1) cols = 1;
        if (rows_count < 1) rows_count = 1;
        eng->staging.resize(
            static_cast<size_t>(cols) * rows_count * constants::kInstanceMultiplier + 16);

        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_render_resize(GwEngine engine, uint32_t width_px, uint32_t height_px) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->renderer) return GW_ERR_INVALID;
        eng->renderer->resize_swapchain(width_px, height_px);
        if (eng->session_mgr && eng->atlas) {
            uint16_t cols = static_cast<uint16_t>(width_px / eng->atlas->cell_width());
            uint16_t rows_count = static_cast<uint16_t>(height_px / eng->atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows_count < 1) rows_count = 1;
            eng->session_mgr->resize_all(cols, rows_count);
        }
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_render_set_clear_color(GwEngine engine, uint32_t rgb) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->renderer) return GW_ERR_INVALID;
        eng->renderer->set_clear_color(rgb);
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_render_start(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->renderer) return GW_ERR_INVALID;
        if (eng->render_running.load()) return GW_OK; // already running

        eng->render_running.store(true, std::memory_order_release);
        eng->render_thread = std::thread([eng] { eng->render_loop(); });
        return GW_OK;
    GW_CATCH_INT
}

GWAPI void gw_render_stop(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return;
        eng->render_running.store(false, std::memory_order_release);
        if (eng->render_thread.joinable())
            eng->render_thread.join();
    GW_CATCH_VOID
}

// ── Session lifecycle ──

GWAPI GwSessionId gw_session_create(GwEngine engine,
                                     const wchar_t* shell_path,
                                     const wchar_t* initial_dir,
                                     uint16_t cols, uint16_t rows) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return 0;

        SessionCreateParams params;
        if (shell_path) params.shell_path = shell_path;
        if (initial_dir) params.initial_dir = initial_dir;
        params.cols = cols;
        params.rows = rows;

        { FILE* f = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
          if (f) { fprintf(f, "session_create: tsf_hwnd=%p cols=%u rows=%u\n",
              (void*)eng->tsf_hwnd, cols, rows); fclose(f); } }

        // TSF adapter functions — simple placeholders for PoC
        auto viewport_fn = [](void*) -> RECT { return RECT{0, 0, 800, 600}; };
        auto cursor_fn = [](void*) -> RECT { return RECT{0, 0, 10, 20}; };

        auto id = eng->session_mgr->create_session(
            params, eng->tsf_hwnd, viewport_fn, cursor_fn, nullptr);

        { FILE* f = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
          if (f) { fprintf(f, "session_create: OK id=%u\n", id); fclose(f); } }

        return id;
    } catch (const std::exception& e) {
        FILE* f = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
        if (f) { fprintf(f, "session_create EXCEPTION: %s\n", e.what()); fclose(f); }
        return 0;
    } catch (...) {
        FILE* f = fopen("C:\\Users\\Solit\\AppData\\Local\\Temp\\ghostwin_engine_debug.log", "a");
        if (f) { fprintf(f, "session_create UNKNOWN EXCEPTION\n"); fclose(f); }
        return 0;
    }
}

GWAPI int gw_session_close(GwEngine engine, GwSessionId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        bool has_more = eng->session_mgr->close_session(id);
        return has_more ? GW_OK : 1; // 1 = last session closed
    GW_CATCH_INT
}

GWAPI void gw_session_activate(GwEngine engine, GwSessionId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (eng) eng->session_mgr->activate(id);
    GW_CATCH_VOID
}

// ── I/O ──

GWAPI int gw_session_write(GwEngine engine, GwSessionId id,
                            const uint8_t* data, uint32_t len) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;

        auto* session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;

        session->conpty->send_input({data, len});
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_session_resize(GwEngine engine, GwSessionId id,
                             uint16_t cols, uint16_t rows) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        eng->session_mgr->resize_session(id, cols, rows);
        return GW_OK;
    GW_CATCH_INT
}

// ── TSF/IME ──

GWAPI int gw_tsf_attach(GwEngine engine, HWND hidden_hwnd) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        eng->tsf_hwnd = hidden_hwnd;
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_tsf_focus(GwEngine engine, GwSessionId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto* session = eng->session_mgr->get(id);
        if (!session) return GW_ERR_NOT_FOUND;
        if (session->tsf)
            session->tsf.Focus(&session->tsf_data);
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_tsf_unfocus(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto* session = eng->session_mgr->active_session();
        if (session && session->tsf)
            session->tsf.Unfocus(&session->tsf_data);
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_tsf_send_pending(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto* session = eng->session_mgr->active_session();
        if (session && session->tsf)
            session->tsf.SendPendingDirectSend();
        return GW_OK;
    GW_CATCH_INT
}

// ── Query ──

GWAPI uint32_t gw_session_count(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return 0;
        return static_cast<uint32_t>(eng->session_mgr->count());
    GW_CATCH_VOID
    return 0;
}

GWAPI GwSessionId gw_active_session_id(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return 0;
        return eng->session_mgr->active_id();
    GW_CATCH_VOID
    return 0;
}

GWAPI void gw_poll_titles(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (eng) eng->session_mgr->poll_titles_and_cwd();
    GW_CATCH_VOID
}

// ── Surface management (Phase 5-E pane split) ──

GWAPI GwSurfaceId gw_surface_create(GwEngine engine, HWND hwnd,
                                     GwSessionId session_id,
                                     uint32_t width_px, uint32_t height_px) {
    GW_TRY
        auto* eng = as_impl(engine);
        LOG_I(kTag, "gw_surface_create: eng=%p surface_mgr=%p hwnd=%p session=%u %ux%u",
              (void*)eng, eng ? (void*)eng->surface_mgr.get() : nullptr,
              (void*)hwnd, session_id, width_px, height_px);
        if (!eng || !eng->surface_mgr || !hwnd) return 0;

        auto* session = eng->session_mgr->get(session_id);
        if (!session) { LOG_E(kTag, "gw_surface_create: session %u not found", session_id); return 0; }

        auto id = eng->surface_mgr->create(hwnd, session_id, width_px, height_px);
        if (id == 0) { LOG_E(kTag, "gw_surface_create: SurfaceManager::create failed"); return 0; }

        // Resize session to match pane dimensions
        if (eng->atlas) {
            uint32_t w = width_px > 0 ? width_px : 1;
            uint32_t h = height_px > 0 ? height_px : 1;
            uint16_t cols = static_cast<uint16_t>(w / eng->atlas->cell_width());
            uint16_t rows = static_cast<uint16_t>(h / eng->atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            eng->session_mgr->resize_session(session_id, cols, rows);
        }

        return id;
    GW_CATCH_VOID
    return 0;
}

GWAPI int gw_surface_destroy(GwEngine engine, GwSurfaceId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->surface_mgr) return GW_ERR_INVALID;

        eng->surface_mgr->destroy(id);

        if (eng->focused_surface_id == id)
            eng->focused_surface_id = 0;

        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_surface_resize(GwEngine engine, GwSurfaceId id,
                             uint32_t width_px, uint32_t height_px) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->surface_mgr) return GW_ERR_INVALID;

        auto* surf = eng->surface_mgr->find_locked(id);
        if (!surf) return GW_ERR_NOT_FOUND;

        // Deferred resize: sets pending_w/h + needs_resize flag
        eng->surface_mgr->resize(id, width_px, height_px);

        // Resize session cols/rows to match new pane size
        if (eng->atlas) {
            uint32_t w = width_px > 0 ? width_px : 1;
            uint32_t h = height_px > 0 ? height_px : 1;
            uint16_t cols = static_cast<uint16_t>(w / eng->atlas->cell_width());
            uint16_t rows = static_cast<uint16_t>(h / eng->atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            eng->session_mgr->resize_session(surf->session_id, cols, rows);
        }

        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_surface_focus(GwEngine engine, GwSurfaceId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->surface_mgr) return GW_ERR_INVALID;

        auto* surf = eng->surface_mgr->find_locked(id);
        if (!surf) return GW_ERR_NOT_FOUND;

        // Switch TSF focus to this surface's session
        auto* old_surf = eng->surface_mgr->find_locked(eng->focused_surface_id);
        if (old_surf && old_surf->session_id != surf->session_id) {
            auto* old_session = eng->session_mgr->get(old_surf->session_id);
            if (old_session && old_session->tsf)
                old_session->tsf.Unfocus(&old_session->tsf_data);
        }

        auto* session = eng->session_mgr->get(surf->session_id);
        if (session && session->tsf)
            session->tsf.Focus(&session->tsf_data);

        eng->focused_surface_id = id;
        eng->session_mgr->activate(surf->session_id);

        return GW_OK;
    GW_CATCH_INT
}
