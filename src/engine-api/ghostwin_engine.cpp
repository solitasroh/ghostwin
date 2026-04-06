// ghostwin_engine.cpp — C API wrapper for WPF Hybrid PoC
// All entry points are exception-guarded to prevent C++ exceptions from leaking to C#.

#include "ghostwin_engine.h"

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

#include <atomic>
#include <memory>
#include <span>
#include <thread>
#include <vector>

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

    // TSF state
    HWND tsf_hwnd = nullptr;

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

    void render_loop() {
        // Create QuadBuilder with atlas metrics
        QuadBuilder builder(
            atlas->cell_width(), atlas->cell_height(), atlas->baseline(),
            0, 0,  // glyph_offset_x/y — PoC defaults
            0, 0); // padding — PoC defaults

        while (render_running.load(std::memory_order_acquire)) {
            Sleep(16); // ~60fps, HWND swapchain (no waitable)

            auto* session = session_mgr->active_session();
            if (!session || !session->conpty || !renderer) continue;
            if (!session->is_live()) continue;

            auto& vt = session->conpty->vt_core();
            auto& state = *session->state;

            bool dirty = state.start_paint(session->vt_mutex, vt);
            if (!dirty) { Sleep(1); continue; }

            const auto& frame = state.frame();
            uint32_t bg_count = 0;
            uint32_t count = builder.build(frame, *atlas, renderer->context(),
                std::span<QuadInstance>(staging), &bg_count);

            if (count > 0)
                renderer->upload_and_draw(staging.data(), count, bg_count);

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
