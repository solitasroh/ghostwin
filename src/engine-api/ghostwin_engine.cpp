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
#include "common/string_util.h"

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
        LOG_E(kTag, "EXCEPTION: %s", e.what()); \
        return GW_ERR_INTERNAL; \
    } catch (...) { \
        LOG_E(kTag, "UNKNOWN EXCEPTION"); \
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

    // M-14 W1 perf instrumentation. Both fields are render-thread only —
    // incremented by render_loop() and read by render_surface(). No external
    // synchronization needed. See src/renderer/render_perf.h.
    uint64_t perf_frame_id_ = 0;
    size_t perf_pane_count_ = 0;

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
        ev.on_mouse_shape = [](void* ctx, SessionId id, int32_t shape) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_mouse_shape)
                eng->callbacks.on_mouse_shape(eng->callbacks.context, id, shape);
        };
        ev.on_child_exit = [](void* ctx, SessionId id, uint32_t exit_code) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (eng->callbacks.on_child_exit)
                eng->callbacks.on_child_exit(eng->callbacks.context, id, exit_code);
        };
        ev.on_osc_notify = [](void* ctx, SessionId id,
                              const char* title, size_t title_len,
                              const char* body, size_t body_len) {
            auto* eng = static_cast<EngineImpl*>(ctx);
            if (!eng->callbacks.on_osc_notify) return;
            auto wtitle = Utf8ToWide(std::string(title, title_len));
            auto wbody  = Utf8ToWide(std::string(body, body_len));
            eng->callbacks.on_osc_notify(eng->callbacks.context, id,
                wtitle.c_str(), static_cast<uint32_t>(wtitle.size()),
                wbody.c_str(),  static_cast<uint32_t>(wbody.size()));
        };
        return ev;
    }

    void render_surface(RenderSurface* surf, QuadBuilder& builder) {
        auto session = session_mgr->get(surf->session_id);
        if (!session || !session->conpty || !session->is_live()) return;

        // M-14 W1 perf instrumentation. Flag is read once at process startup;
        // disabled path is a single predictable branch + no extra work.
        const bool perf = perf_enabled();
        LARGE_INTEGER qpc_freq{}, t_enter{}, t_after_paint{},
                      t_after_build{}, t_after_draw{};
        if (perf) {
            QueryPerformanceFrequency(&qpc_freq);
            QueryPerformanceCounter(&t_enter);
        }

        // Deferred resize: render thread applies ResizeBuffers (C-7)
        const bool resize_applied =
            surf->needs_resize.load(std::memory_order_acquire);
        if (resize_applied) {
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
        // Render uses the single VT lock (ConPtySession::vt_mutex) — same mutex
        // as the I/O thread's write() and SessionManager's resize path (ADR-006
        // revision, 2026-04-15). Session::vt_mutex no longer exists.
        //
        // M-14 W3 (2026-04-21): state.force_all_dirty() removed. VT's own
        // dirty tracking via for_each_row is sufficient for cell content;
        // non-VT visual changes (selection / IME / activate) are signaled
        // through SessionVisualState's epoch snapshot (Design 5.2 intent).
        const bool vt_dirty = state.start_paint(session->conpty->vt_mutex(), vt);
        if (perf) QueryPerformanceCounter(&t_after_paint);

        // Snapshot non-VT visual state (selection + IME + epoch) as one
        // coherent copy. This avoids consuming a new epoch with stale
        // overlay payload.
        const auto visual = session->visual_state.snapshot();
        const bool composition_visible =
            visual.composition.active && !visual.composition.text.empty();
        const bool visual_dirty = (surf->last_visual_epoch != visual.epoch);

        // Skip draw + present if nothing changed. `resize_applied` is the
        // third reason — the surface geometry changed, so we must repaint
        // at least once to avoid a stretched last frame during DWM
        // composition.
        if (!vt_dirty && !visual_dirty && !resize_applied) {
            return;
        }

        // M-14 W2: FrameReadGuard holds shared_lock(frame_mutex_) for
        // build + selection + IME overlay. Released before upload_and_draw
        // so the write-path (start_paint / resize) is not blocked during
        // GPU upload. See Design 5.1 "짧은 순회 hot path" policy.
        uint32_t bg_count = 0;
        uint32_t count = 0;
        {
            auto frame_guard = state.acquire_frame();
            const auto& frame = frame_guard.get();
            count = builder.build(frame, *atlas, renderer->context(),
                std::span<QuadInstance>(staging), &bg_count, !composition_visible);

        // ── Selection highlight overlay (M-10c) ──
        // Append semi-transparent blue quads for each selected cell.
        // Uses shading_type=2 (cursor/underline path) which blends via
        // fgColor.a — matching the standard terminal selection pattern.
        if (visual.selection.active) {
            int32_t sr = visual.selection.start_row;
            int32_t sc = visual.selection.start_col;
            int32_t er = visual.selection.end_row;
            int32_t ec = visual.selection.end_col;

            // Clamp to visible frame
            if (sr < 0) sr = 0;
            if (er >= (int32_t)frame.rows_count) er = (int32_t)frame.rows_count - 1;

            uint32_t cell_w = builder.cell_width();
            uint32_t cell_h = builder.cell_height();
            uint32_t max_cols = surf->width_px / cell_w;

            // Selection color: RGBA(0x44, 0x88, 0xFF, 0x60) — semi-transparent blue
            uint32_t sel_color = 0x60FF8844; // packed as R|G<<8|B<<16|A<<24

            for (int32_t r = sr; r <= er && count < staging.size(); ++r) {
                int32_t c_start = (r == sr) ? sc : 0;
                int32_t c_end   = (r == er) ? ec : (int32_t)max_cols - 1;
                if (c_start < 0) c_start = 0;
                if (c_end >= (int32_t)max_cols) c_end = (int32_t)max_cols - 1;

                for (int32_t c = c_start; c <= c_end && count < staging.size(); ++c) {
                    auto& q = staging[count++];
                    q.shading_type = 2;  // cursor/underline alpha-blend path
                    q.pos_x = (uint16_t)(c * cell_w);
                    q.pos_y = (uint16_t)(r * cell_h);
                    q.size_x = (uint16_t)cell_w;
                    q.size_y = (uint16_t)cell_h;
                    q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
                    q.fg_packed = sel_color;
                    q.bg_packed = 0;
                    q.reserved = 0;
                }
            }
        }

        // ── IME composition overlay (M-13 FR-01, analysis §4.4 권장 구현) ──
        // Edge-triggered logging avoids LOG flooding on every frame.
        {
            // Edge-triggered diagnostic: log only when composition state changes.
            // Per-surface state to support multi-pane IME independence.
            if (visual.composition.text != surf->last_composition_text ||
                visual.composition.caret_offset != surf->last_composition_caret_offset ||
                visual.composition.active != surf->last_composition_active) {
                if (!composition_visible) {
                    LOG_I(kTag, "IME composition cleared: sid=%u", surf->session_id);
                } else {
                    LOG_I(kTag, "IME composition update: sid=%u text='%ls' (%zu chars) caret=%u cursor=(%d,%d)",
                          surf->session_id, visual.composition.text.c_str(),
                          visual.composition.text.size(),
                          visual.composition.caret_offset,
                          (int)frame.cursor.x, (int)frame.cursor.y);
                }
                surf->last_composition_text = visual.composition.text;
                surf->last_composition_caret_offset = visual.composition.caret_offset;
                surf->last_composition_active = visual.composition.active;
            }

            if (composition_visible) {
                // Reserve worst-case: each char may emit (2 bg + 1 glyph + 1 underline) plus caret.
                const uint32_t reserve =
                    static_cast<uint32_t>(visual.composition.text.size()) * 4u + 1u;
                if (count + reserve <= staging.size()) {
                    count = builder.build_composition(
                        visual.composition.text,
                        visual.composition.caret_offset,
                        static_cast<uint16_t>(frame.cursor.x),
                        static_cast<uint16_t>(frame.cursor.y),
                        *atlas, renderer->context(),
                        std::span<QuadInstance>(staging),
                        count);
                } else {
                    LOG_W(kTag, "IME overlay skipped: staging full (count=%u, need=%u, cap=%zu)",
                          count, reserve, staging.size());
                }
            }
        }
        } // end frame_guard scope — shared_lock(frame_mutex_) released

        // ── M-16-C Phase A (D-02/D-03/D-06): per-surface dim overlay ──
        // Read-only load of dim_factor (UI thread writes inside gw_surface_focus
        // under the SurfaceManager lock; render thread never writes). When > 0
        // we append one full-surface quad with packed RGBA(0,0,0,A) using
        // shading_type=2, same alpha-blend path as the selection overlay.
        // Added last so it composites on top of all other content.
        // Render thread is read-only here, so M-14's reader safety contract
        // (FrameReadGuard / SessionVisualState) is preserved.
        {
            const float dim = surf->dim_factor.load(std::memory_order_acquire);
            if (dim > 0.0f && count < staging.size()) {
                const uint8_t alpha = static_cast<uint8_t>(dim * 255.0f);
                const uint32_t dim_color = static_cast<uint32_t>(alpha) << 24;
                auto& q = staging[count++];
                q.shading_type = 2;
                q.pos_x = 0;
                q.pos_y = 0;
                q.size_x = static_cast<uint16_t>(
                    surf->width_px > 65535u ? 65535u : surf->width_px);
                q.size_y = static_cast<uint16_t>(
                    surf->height_px > 65535u ? 65535u : surf->height_px);
                q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
                q.fg_packed = dim_color;
                q.bg_packed = 0;
                q.reserved = 0;
            }
        }

        // DIAG: log quad counts every second
        static uint32_t diag_frame = 0;
        diag_frame++;
        if (diag_frame % 60 == 0 || (count - bg_count) > 5) {
            LOG_I(kTag, "frame %u: total=%u bg=%u text=%u size=%ux%u",
                  diag_frame, count, bg_count, count - bg_count,
                  surf->width_px, surf->height_px);
        }

        if (perf) QueryPerformanceCounter(&t_after_build);

        DrawPerfResult draw_timing{};
        bool presented = false;
        if (count > 0) {
            renderer->bind_surface(
                static_cast<void*>(surf->rtv.Get()),
                static_cast<void*>(surf->swapchain.Get()),
                surf->width_px, surf->height_px);
            if (perf) {
                draw_timing = renderer->upload_and_draw_timed(
                    staging.data(), count, bg_count);
                presented = draw_timing.presented;
            } else {
                presented = renderer->upload_and_draw(
                    staging.data(), count, bg_count);
            }
            renderer->unbind_surface();
            if (presented) {
                surf->last_visual_epoch = visual.epoch;
            }
        }

        if (perf) {
            QueryPerformanceCounter(&t_after_draw);
            auto span_us = [&](const LARGE_INTEGER& from,
                               const LARGE_INTEGER& to) -> double {
                return double(to.QuadPart - from.QuadPart) * 1'000'000.0 /
                       double(qpc_freq.QuadPart);
            };
            const double start_us = span_us(t_enter, t_after_paint);
            const double build_us = span_us(t_after_paint, t_after_build);
            const double total_us = span_us(t_enter, t_after_draw);
            // Single-line schema — see src/renderer/render_perf.h.
            // M-14 W3: visual_dirty now reflects the real visual_epoch
            // comparison (was hardcoded 0 in W1).
            LOG_I("render-perf",
                  "frame=%llu sid=%u panes=%zu vt_dirty=%d visual_dirty=%d "
                  "resize=%d start_us=%.1f build_us=%.1f draw_us=%.1f "
                  "present_us=%.1f total_us=%.1f quads=%u",
                  static_cast<unsigned long long>(perf_frame_id_),
                  surf->session_id,
                  perf_pane_count_,
                  vt_dirty ? 1 : 0,
                  visual_dirty ? 1 : 0,
                  resize_applied ? 1 : 0,
                  start_us, build_us,
                  draw_timing.upload_draw_us, draw_timing.present_us,
                  total_us, count);
        }
    }

    void render_loop() {
        QuadBuilder builder(
            atlas->cell_width(), atlas->cell_height(), atlas->baseline(),
            0, 0,  // glyph_offset_x/y
            0, 0); // padding

        while (render_running.load(std::memory_order_acquire)) {
            Sleep(16); // ~60fps

            if (!renderer || !surface_mgr) continue;

            // Surface path is the only path (Phase 5-E.5 P0-2: legacy fallback removed).
            // During the warm-up window between engine init and the first
            // SurfaceCreate, active_surfaces() is empty and we simply skip
            // rendering — WPF chrome continues to render the window; the
            // HwndHost child area stays dark until the first bind.
            auto active = surface_mgr->active_surfaces();
            if (active.empty()) {
                Sleep(1);
                continue;
            }

            // M-14 W1: per-iteration perf counters. frame_id is monotonic
            // across the engine lifetime; pane_count is the current active
            // surface snapshot size. Both read by render_surface() below.
            ++perf_frame_id_;
            perf_pane_count_ = active.size();

            for (auto& surf : active) {
                // surf is a shared_ptr<RenderSurface>; pass raw for API
                // compatibility. Lifetime is guaranteed by our local vector.
                render_surface(surf.get(), builder);
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

GWAPI void gw_engine_detach_callbacks(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return;

        // Zero all callback pointers so that in-flight I/O thread events
        // (on_child_exit, on_title_changed, etc.) become no-ops.
        // SessionManager::fire_*_event checks fn != nullptr before calling.
        eng->callbacks = GwCallbacks{};

        // TSF shutdown은 DetachCallbacks에서 하지 않음.
        // OnClosing에서 Application.Shutdown() → WPF Deactivate(count-1) 후
        // engine.Dispose() → gw_engine_destroy → TSF Deactivate(count-1)
        // 순서로 정확히 2회 Deactivate됨.
        LOG_I(kTag, "callbacks detached");
    GW_CATCH_VOID
}

GWAPI void gw_engine_destroy(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return;

        // Defensive: ensure callbacks are detached even if caller forgot.
        eng->callbacks = GwCallbacks{};

        // Stop render thread first
        eng->render_running.store(false, std::memory_order_release);
        if (eng->render_thread.joinable())
            eng->render_thread.join();

        // Destroy surfaces before renderer (surfaces hold D3D resources)
        eng->surface_mgr.reset();
        // SessionManager destructor handles cleanup thread + sessions.
        // ConPtySession::~ConPtySession → I/O thread on_exit → fire_exit_event
        // → callbacks.on_child_exit is NULL → safe skip.
        eng->session_mgr.reset();
        eng->atlas.reset();
        eng->renderer.reset();
        delete eng;
    GW_CATCH_VOID
}

// ── Render init ──

GWAPI int gw_render_init(GwEngine engine, HWND hwnd,
                          uint32_t width_px, uint32_t height_px,
                          float font_size_pt, const wchar_t* font_family,
                          float dpi_scale) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;

        RendererConfig config;
        config.hwnd = hwnd;
        // Phase 2 Option B (first-pane-render-failure): allow null hwnd so that
        // MainWindow no longer needs to pre-create a TerminalHostControl HWND
        // before workspace setup. SurfaceManager creates per-pane swapchains
        // later via bind_surface(). The bootstrap swapchain is still created
        // when hwnd != null (legacy callers) but is released immediately below.
        config.allow_null_hwnd = true;
        // Use dummy width/height for cols/rows when called with zero size in
        // hwnd-less mode — atlas cell size is font-dependent and recomputed at
        // line ~310 using the real atlas->cell_width/height().
        uint32_t safe_w = width_px > 0 ? width_px : 100;
        uint32_t safe_h = height_px > 0 ? height_px : 100;
        config.cols = static_cast<uint16_t>(safe_w / 8);  // approximate
        config.rows = static_cast<uint16_t>(safe_h / 16);
        config.font_size_pt = font_size_pt;
        if (font_family) config.font_family = font_family;

        LOG_I(kTag, "render_init: hwnd=%p size=%ux%u font=%.1f",
              (void*)hwnd, width_px, height_px, font_size_pt);

        Error err;
        eng->renderer = DX11Renderer::create(config, &err);
        if (!eng->renderer) {
            LOG_E(kTag, "DX11Renderer::create FAILED: %s",
                  err.message ? err.message : "unknown");
            return GW_ERR_INTERNAL;
        }

        LOG_I(kTag, "DX11Renderer::create OK");

        AtlasConfig acfg;
        acfg.font_size_pt = font_size_pt;
        acfg.font_family = font_family ? font_family : L"Cascadia Mono";
        // Initial DPI applied here. Runtime DPI / font / zoom changes flow
        // through gw_update_cell_metrics(), which atomically coordinates
        // atlas rebuild + per-surface cols/rows recompute + per-session
        // resize_pty_only + vt_resize_locked — resolving the "text overflow"
        // concern that led to the 31a2235 → 3a28730 revert.
        acfg.dpi_scale = (dpi_scale > 0.0f) ? dpi_scale : 1.0f;
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

        // Renderer's HWND swapchain was created by DX11Renderer::create() for
        // bootstrap diagnostics. SurfaceManager now owns per-pane swapchains on
        // pane HWNDs, so release the bootstrap swapchain here. All subsequent
        // rendering goes through bind_surface() with surface-owned targets.
        // (Phase 5-E.5 P0-2: legacy fallback removal / Surface-only mode.)
        eng->renderer->release_swapchain();

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

// Deprecated (Phase 5-E.5 P0-2 / 2026-04-07): pane resizes are now routed
// through gw_surface_resize per-pane via PaneContainerControl.OnPaneResized.
// The previous implementation called renderer->resize_swapchain (which would
// NPE after release_swapchain) and session_mgr->resize_all (which forced a
// uniform pane size incompatible with pane-split). Kept as a no-op for ABI
// compatibility with any external callers.
GWAPI int gw_render_resize(GwEngine engine, uint32_t /*width_px*/, uint32_t /*height_px*/) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
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

GWAPI int gw_update_cell_metrics(GwEngine engine,
                                  float font_size_pt,
                                  const wchar_t* font_family,
                                  float dpi_scale,
                                  float cell_width_scale,
                                  float cell_height_scale,
                                  float zoom) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->renderer) return GW_ERR_INVALID;
        if (font_size_pt <= 0.0f || dpi_scale <= 0.0f || zoom <= 0.0f)
            return GW_ERR_INVALID;

        // Stop the render thread around the atlas/QuadBuilder swap. The render
        // loop captures atlas->cell_width/height into a stack-local QuadBuilder
        // at loop entry (see render_loop), so the safe window to replace
        // `eng->atlas` is when no thread is reading it. DPI/font changes are
        // user-triggered and infrequent, so the restart cost is acceptable.
        bool was_running = eng->render_running.load(std::memory_order_acquire);
        if (was_running) {
            eng->render_running.store(false, std::memory_order_release);
            if (eng->render_thread.joinable())
                eng->render_thread.join();
        }

        // Rebuild the atlas with new metrics. Effective font size includes zoom;
        // dpi_scale is passed through to DirectWrite via GlyphAtlas.
        AtlasConfig acfg;
        acfg.font_size_pt = font_size_pt * zoom;
        acfg.font_family = (font_family && *font_family) ? font_family : L"Cascadia Mono";
        acfg.dpi_scale = dpi_scale;
        acfg.cell_width_scale = cell_width_scale;
        acfg.cell_height_scale = cell_height_scale;

        Error atlas_err;
        auto new_atlas = GlyphAtlas::create(eng->renderer->device(), acfg, &atlas_err);
        if (!new_atlas) {
            LOG_E(kTag, "GlyphAtlas::create failed in update_cell_metrics: %s",
                  atlas_err.message ? atlas_err.message : "unknown");
            // Restart render thread even on failure so the UI doesn't freeze
            // on the old atlas (still valid).
            if (was_running) {
                eng->render_running.store(true, std::memory_order_release);
                eng->render_thread = std::thread([eng] { eng->render_loop(); });
            }
            return GW_ERR_INTERNAL;
        }

        eng->atlas = std::move(new_atlas);
        eng->renderer->set_atlas_srv(eng->atlas->srv());

        // Broadcast new cell metrics to all active surfaces+sessions. The
        // resize_pty_only + vt_resize_locked split mirrors vt-mutex-redesign
        // cycle's pattern so PTY/VT/RenderState stay atomically consistent.
        const uint32_t cell_w = eng->atlas->cell_width();
        const uint32_t cell_h = eng->atlas->cell_height();
        if (cell_w > 0 && cell_h > 0 && eng->surface_mgr && eng->session_mgr) {
            for (auto& surf : eng->surface_mgr->active_surfaces()) {
                if (!surf || surf->width_px == 0 || surf->height_px == 0) continue;
                uint16_t cols = static_cast<uint16_t>(
                    std::max<uint32_t>(1, surf->width_px / cell_w));
                uint16_t rows = static_cast<uint16_t>(
                    std::max<uint32_t>(1, surf->height_px / cell_h));
                auto session = eng->session_mgr->get(surf->session_id);
                if (!session || !session->conpty || !session->is_live()) continue;
                (void)session->conpty->resize_pty_only(cols, rows);
                std::lock_guard lock(session->conpty->vt_mutex());
                session->conpty->vt_resize_locked(cols, rows);
                if (session->state) session->state->resize(cols, rows);
            }
        }

        LOG_I(kTag, "update_cell_metrics: font=%.1f dpi=%.2f zoom=%.2f cell=%ux%u",
              font_size_pt, dpi_scale, zoom, cell_w, cell_h);

        if (was_running) {
            eng->render_running.store(true, std::memory_order_release);
            eng->render_thread = std::thread([eng] { eng->render_loop(); });
        }
        return GW_OK;
    GW_CATCH_INT
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

        LOG_I(kTag, "session_create: tsf_hwnd=%p cols=%u rows=%u",
              (void*)eng->tsf_hwnd, cols, rows);

        // TSF adapter functions — simple placeholders for PoC
        auto viewport_fn = [](void*) -> RECT { return RECT{0, 0, 800, 600}; };
        auto cursor_fn = [](void*) -> RECT { return RECT{0, 0, 10, 20}; };

        auto id = eng->session_mgr->create_session(
            params, eng->tsf_hwnd, viewport_fn, cursor_fn, nullptr);

        LOG_I(kTag, "session_create: OK id=%u", id);

        return id;
    } catch (const std::exception& e) {
        LOG_E(kTag, "session_create EXCEPTION: %s", e.what());
        return 0;
    } catch (...) {
        LOG_E(kTag, "session_create UNKNOWN EXCEPTION");
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

        auto session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;

        // send_input failure = pipe closed (logged inside ConPtySession);
        // caller learns via child-exit callback, not return value.
        (void)session->conpty->send_input({data, len});
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_session_test_inject_vt(GwEngine engine, GwSessionId id,
                                     const uint8_t* data, uint32_t len) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !data || len == 0) return GW_ERR_INVALID;

        auto session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;

        return session->conpty->inject_vt_for_test({data, len}) ? GW_OK : GW_ERR_INTERNAL;
    GW_CATCH_INT
}

GWAPI int gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                  float x_px, float y_px,
                                  uint32_t button, uint32_t action,
                                  uint32_t mods) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;
        if (!session->mouse_encoder || !session->mouse_event) return GW_ERR_INVALID;

        auto& vt = session->conpty->vt_core();

        // 1. Sync tracking mode/format from terminal state
        ghostty_mouse_encoder_setopt_from_terminal(
            session->mouse_encoder,
            (GhosttyTerminal)vt.raw_terminal());

        // 2. Set surface size (pixel->cell conversion)
        auto surf = eng->surface_mgr ? eng->surface_mgr->find_by_session(id) : nullptr;
        if (surf && eng->atlas) {
            GhosttyMouseEncoderSize sz{};
            sz.size = sizeof(sz);
            sz.screen_width  = surf->width_px;
            sz.screen_height = surf->height_px;
            sz.cell_width    = eng->atlas->cell_width();
            sz.cell_height   = eng->atlas->cell_height();
            ghostty_mouse_encoder_setopt(session->mouse_encoder,
                GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &sz);
        }

        // 3. Set event fields (reuse cached instance)
        ghostty_mouse_event_set_action(session->mouse_event,
            (GhosttyMouseAction)action);
        if (button > 0)
            ghostty_mouse_event_set_button(session->mouse_event,
                (GhosttyMouseButton)button);
        else
            ghostty_mouse_event_clear_button(session->mouse_event);
        ghostty_mouse_event_set_position(session->mouse_event,
            GhosttyMousePosition{x_px, y_px});
        ghostty_mouse_event_set_mods(session->mouse_event,
            (GhosttyMods)mods);

        // 4. Encode (stack buffer, 0 heap alloc)
        char buf[128];
        size_t written = 0;
        ghostty_mouse_encoder_encode(session->mouse_encoder,
            session->mouse_event, buf, sizeof(buf), &written);

        // 5. Send (written==0 means cell dedup or mode inactive)
        if (written > 0) {
            (void)session->conpty->send_input(
                {(const uint8_t*)buf, (uint32_t)written});
            return GW_OK;
        }

        return GW_MOUSE_NOT_REPORTED;
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

GWAPI int gw_scroll_viewport(GwEngine engine, GwSessionId id, int32_t delta_rows) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;
        auto& vt = session->conpty->vt_core();
        vt.scroll_viewport(delta_rows);
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
        auto session = eng->session_mgr->get(id);
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
        auto session = eng->session_mgr->active_session();
        if (session && session->tsf)
            session->tsf.Unfocus(&session->tsf_data);
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_tsf_send_pending(GwEngine engine) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto session = eng->session_mgr->active_session();
        if (session && session->tsf)
            session->tsf.SendPendingDirectSend();
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_session_set_composition(GwEngine engine, GwSessionId id,
                                      const wchar_t* text, uint32_t len,
                                      uint32_t caret_offset, int32_t active) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;

        auto session = eng->session_mgr->get(id);
        if (!session) return GW_ERR_NOT_FOUND;

        if (text && len > 0 && active) {
            session->visual_state.set_composition(std::wstring(text, len),
                                                  caret_offset, true);
        } else {
            session->visual_state.clear_composition();
        }

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

        auto session = eng->session_mgr->get(session_id);
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

        auto surf = eng->surface_mgr->find_locked(id);
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

// ── Selection set API (M-10c) ──

GWAPI int gw_session_set_selection(GwEngine engine, GwSessionId id,
                                    int32_t start_row, int32_t start_col,
                                    int32_t end_row, int32_t end_col,
                                    int32_t active) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;

        auto session = eng->session_mgr->get(id);
        if (!session) return GW_ERR_NOT_FOUND;

        if (active) {
            // Normalize: ensure start <= end in reading order
            if (start_row > end_row || (start_row == end_row && start_col > end_col)) {
                std::swap(start_row, end_row);
                std::swap(start_col, end_col);
            }
            session->visual_state.set_selection(start_row, start_col,
                                                end_row, end_col);
        } else {
            session->visual_state.clear_selection();
        }

        return GW_OK;
    GW_CATCH_INT
}

// ── Selection support helpers (M-10c) ──

// Encode a single Unicode codepoint as UTF-8 into buf.
// Returns number of bytes written (1-4), or 0 if invalid.
static int utf8_encode(uint32_t cp, char* buf, int buf_remaining) {
    if (cp <= 0x7F && buf_remaining >= 1) {
        buf[0] = static_cast<char>(cp);
        return 1;
    } else if (cp <= 0x7FF && buf_remaining >= 2) {
        buf[0] = static_cast<char>(0xC0 | (cp >> 6));
        buf[1] = static_cast<char>(0x80 | (cp & 0x3F));
        return 2;
    } else if (cp <= 0xFFFF && buf_remaining >= 3) {
        buf[0] = static_cast<char>(0xE0 | (cp >> 12));
        buf[1] = static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        buf[2] = static_cast<char>(0x80 | (cp & 0x3F));
        return 3;
    } else if (cp <= 0x10FFFF && buf_remaining >= 4) {
        buf[0] = static_cast<char>(0xF0 | (cp >> 18));
        buf[1] = static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        buf[2] = static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        buf[3] = static_cast<char>(0x80 | (cp & 0x3F));
        return 4;
    }
    return 0;
}

// Encode all codepoints from a CellData into UTF-8.
// Returns total bytes written.
static int cell_to_utf8(const ghostwin::CellData& cell, char* buf, int buf_size) {
    int total = 0;
    for (uint8_t i = 0; i < cell.cp_count && i < 4; ++i) {
        if (cell.codepoints[i] == 0) break;
        int n = utf8_encode(cell.codepoints[i], buf + total, buf_size - total);
        if (n == 0) break;
        total += n;
    }
    return total;
}

GWAPI int gw_surface_focus(GwEngine engine, GwSurfaceId id) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->surface_mgr) return GW_ERR_INVALID;

        auto surf = eng->surface_mgr->find_locked(id);
        if (!surf) return GW_ERR_NOT_FOUND;

        // Switch TSF focus to this surface's session
        auto old_surf = eng->surface_mgr->find_locked(eng->focused_surface_id);
        if (old_surf && old_surf->session_id != surf->session_id) {
            auto old_session = eng->session_mgr->get(old_surf->session_id);
            if (old_session && old_session->tsf)
                old_session->tsf.Unfocus(&old_session->tsf_data);
        }

        auto session = eng->session_mgr->get(surf->session_id);
        if (session && session->tsf)
            session->tsf.Focus(&session->tsf_data);

        eng->focused_surface_id = id;
        eng->session_mgr->activate(surf->session_id);

        // M-16-C Phase A (D-03/D-06): dim every other surface so the user
        // can tell which pane is focused without relying on the constant
        // BorderThickness fixed in Phase A1. UI thread holds the surface_mgr
        // lock here, render thread will pick up the new dim_factor on the
        // next frame via atomic load. cmux uses 0.4 as the unfocused split
        // opacity; we match that constant.
        constexpr float DIM_ALPHA = 0.4f;
        for (auto& s : eng->surface_mgr->active_surfaces()) {
            const float target = (s->id == id) ? 0.0f : DIM_ALPHA;
            s->dim_factor.store(target, std::memory_order_release);
        }

        return GW_OK;
    GW_CATCH_INT
}

// ── Selection support (M-10c) ──

GWAPI int gw_get_cell_size(GwEngine engine,
                            uint32_t* cell_width, uint32_t* cell_height) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->atlas) return GW_ERR_INVALID;
        if (!cell_width || !cell_height) return GW_ERR_INVALID;
        *cell_width = eng->atlas->cell_width();
        *cell_height = eng->atlas->cell_height();
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_session_get_cell_text(GwEngine engine, GwSessionId id,
                                    int32_t row, int32_t col,
                                    char* buf, uint32_t buf_size) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !buf || buf_size < 2) return GW_ERR_INVALID;

        auto session = eng->session_mgr->get(id);
        if (!session || !session->state) return GW_ERR_NOT_FOUND;

        auto& state = *session->state;
        // M-14 W2: short reader (single cell) — acquire_frame() shared_lock
        // is cheap and held for a few hundred nanoseconds.
        auto frame_guard = state.acquire_frame();
        const auto& frame = frame_guard.get();

        if (row < 0 || row >= frame.rows_count || col < 0 || col >= frame.cols)
        {
            buf[0] = '\0';
            return 0;
        }

        auto cells = frame.row(static_cast<uint16_t>(row));
        const auto& cell = cells[col];

        int written = cell_to_utf8(cell, buf, static_cast<int>(buf_size) - 1);
        buf[written] = '\0';
        return written;
    GW_CATCH_INT
}

GWAPI int gw_session_get_selected_text(GwEngine engine, GwSessionId id,
                                        int32_t start_row, int32_t start_col,
                                        int32_t end_row, int32_t end_col,
                                        char* buf, uint32_t buf_size,
                                        uint32_t* written) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !buf || buf_size < 2 || !written) return GW_ERR_INVALID;

        auto session = eng->session_mgr->get(id);
        if (!session || !session->state) return GW_ERR_NOT_FOUND;

        auto& state = *session->state;
        // M-14 W2: long reader (multi-row string building, up to full
        // selection range). Copy once under brief shared_lock, then
        // iterate the copy lock-free — avoids writer starvation when user
        // drags selection over many rows (Design 5.1 "긴 reader" 정책).
        const auto frame = state.acquire_frame_copy();

        // Normalize: ensure start <= end in reading order
        if (start_row > end_row || (start_row == end_row && start_col > end_col)) {
            std::swap(start_row, end_row);
            std::swap(start_col, end_col);
        }

        // Clamp to valid range
        if (start_row < 0) start_row = 0;
        if (end_row >= frame.rows_count) end_row = frame.rows_count - 1;
        if (start_row > end_row) { buf[0] = '\0'; *written = 0; return GW_OK; }

        uint32_t pos = 0;
        uint32_t limit = buf_size - 1;  // leave room for null terminator

        for (int32_t r = start_row; r <= end_row && pos < limit; ++r) {
            auto cells = frame.row(static_cast<uint16_t>(r));
            int32_t c_start = (r == start_row) ? start_col : 0;
            int32_t c_end   = (r == end_row)   ? end_col   : frame.cols - 1;

            if (c_start < 0) c_start = 0;
            if (c_end >= frame.cols) c_end = frame.cols - 1;

            // Track last non-blank column for trimming trailing whitespace
            int32_t last_nonblank = c_start - 1;
            for (int32_t c = c_start; c <= c_end; ++c) {
                const auto& cell = cells[c];
                if (cell.cp_count > 0 && cell.codepoints[0] != 0 &&
                    cell.codepoints[0] != ' ')
                {
                    last_nonblank = c;
                }
            }

            for (int32_t c = c_start; c <= c_end && pos < limit; ++c) {
                const auto& cell = cells[c];
                if (cell.cp_count == 0 || cell.codepoints[0] == 0) {
                    // Emit space for blank cells (only up to last non-blank)
                    if (c <= last_nonblank && pos < limit) {
                        buf[pos++] = ' ';
                    }
                    continue;
                }
                int n = cell_to_utf8(cell, buf + pos,
                                     static_cast<int>(limit - pos));
                pos += static_cast<uint32_t>(n);
            }

            // Add newline between rows (not after last row)
            if (r < end_row && pos < limit) {
                buf[pos++] = '\n';
            }
        }

        buf[pos] = '\0';
        *written = pos;
        return GW_OK;
    GW_CATCH_INT
}

// ── Word/Line boundary (grid-native) ──

static bool is_word_codepoint(uint32_t cp) {
    if (cp == 0 || cp == ' ' || cp == '\t') return false;
    if (cp < 0x80) {
        return (cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z') ||
               (cp >= '0' && cp <= '9') || cp == '_' || cp == '-' || cp == '.';
    }
    if (cp >= 0xAC00 && cp <= 0xD7AF) return true;  // Hangul Syllables
    if (cp >= 0x4E00 && cp <= 0x9FFF) return true;  // CJK Unified
    if (cp >= 0x3400 && cp <= 0x4DBF) return true;  // CJK Ext A
    if (cp >= 0x3040 && cp <= 0x30FF) return true;  // Hiragana + Katakana
    if (cp >= 0x1100 && cp <= 0x11FF) return true;  // Hangul Jamo
    if (cp >= 0x3130 && cp <= 0x318F) return true;  // Hangul Compat Jamo
    if (cp >= 0x00C0 && cp <= 0x024F) return true;  // Latin Extended
    if (cp >= 0x0400 && cp <= 0x04FF) return true;  // Cyrillic
    return false;
}

GWAPI int gw_session_find_word_bounds(GwEngine engine, GwSessionId id,
                                       int32_t row, int32_t col,
                                       int32_t* out_start, int32_t* out_end) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !out_start || !out_end) return GW_ERR_INVALID;
        auto session = eng->session_mgr->get(id);
        if (!session || !session->state) return GW_ERR_NOT_FOUND;

        // M-14 W2: Design 5.1 categorizes this as "긴 row scan" — bidirectional
        // scan across the row for word boundaries. Copy-first to avoid
        // blocking resize writers (drag-select + resize race).
        const auto frame = session->state->acquire_frame_copy();
        if (row < 0 || row >= (int32_t)frame.rows_count) return GW_ERR_INVALID;

        auto cells = frame.row((uint16_t)row);
        int32_t ncols = frame.cols;
        if (col < 0 || col >= ncols) return GW_ERR_INVALID;

        // If on a wide-char spacer (cp_count==0), step to the real char
        if (cells[col].cp_count == 0 && col > 0) col--;

        uint32_t anchor_cp = (cells[col].cp_count > 0) ? cells[col].codepoints[0] : 0;
        bool anchor_is_word = is_word_codepoint(anchor_cp);

        // Scan left
        int32_t left = col;
        while (left > 0) {
            int32_t prev = left - 1;
            if (cells[prev].cp_count == 0 && prev > 0) prev--;
            uint32_t cp = (cells[prev].cp_count > 0) ? cells[prev].codepoints[0] : 0;
            if (is_word_codepoint(cp) != anchor_is_word) break;
            left = prev;
        }

        // Scan right
        int32_t right = col;
        while (right < ncols - 1) {
            int32_t next = right + 1;
            if (cells[next].cp_count == 0 && next < ncols - 1) next++;
            if (next > ncols - 1) break;
            uint32_t cp = (cells[next].cp_count > 0) ? cells[next].codepoints[0] : 0;
            if (is_word_codepoint(cp) != anchor_is_word) break;
            right = next;
        }
        // Include trailing spacer of wide char
        if (right < ncols - 1 && cells[right + 1].cp_count == 0) right++;

        *out_start = left;
        *out_end = right;
        return GW_OK;
    GW_CATCH_INT
}

GWAPI int gw_session_find_line_bounds(GwEngine engine, GwSessionId id,
                                       int32_t row,
                                       int32_t* out_start, int32_t* out_end) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !out_start || !out_end) return GW_ERR_INVALID;
        auto session = eng->session_mgr->get(id);
        if (!session || !session->state) return GW_ERR_NOT_FOUND;

        // M-14 W2: short metadata reader (only reads frame.rows_count +
        // frame.cols). acquire_frame() guard is held briefly for the 4
        // field reads below (Design 5.1 "짧은 메타 조회" 정책).
        auto frame_guard = session->state->acquire_frame();
        const auto& frame = frame_guard.get();
        if (row < 0 || row >= (int32_t)frame.rows_count) return GW_ERR_INVALID;

        *out_start = 0;
        *out_end = frame.cols - 1;
        return GW_OK;
    GW_CATCH_INT
}

GWAPI bool gw_session_mode_get(GwEngine engine, GwSessionId id, uint16_t mode_value) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return false;
        auto session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return false;
        return session->conpty->vt_core().mode_get(mode_value);
    } catch (...) {
        return false;
    }
}
