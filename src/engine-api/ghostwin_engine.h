#pragma once

// ghostwin_engine.h — WPF Hybrid PoC C API
// All functions are exception-guarded (try-catch at boundary)
// All types are blittable (P/Invoke compatible)

#include <stdint.h>
#include <stdbool.h>

#ifdef _WIN32
#include <windows.h>
#endif

#ifdef GHOSTWIN_ENGINE_EXPORTS
#define GWAPI __declspec(dllexport)
#else
#define GWAPI __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ── Opaque handle ──
typedef void* GwEngine;
typedef uint32_t GwSessionId;

// ── Error codes ──
#define GW_OK            0
#define GW_ERR_INVALID  -1
#define GW_ERR_INTERNAL -2
#define GW_ERR_NOT_FOUND -3
#define GW_MOUSE_NOT_REPORTED 2

// ── Callbacks (blittable function pointers) ──
// Thread safety: on_child_exit is called from I/O thread.
// on_render_done is called from render thread.
// Others are called from the thread that triggered the event.
typedef void (*GwSessionFn)(void* ctx, GwSessionId id);
typedef void (*GwExitFn)(void* ctx, GwSessionId id, uint32_t exit_code);
typedef void (*GwTitleFn)(void* ctx, GwSessionId id, const wchar_t* title, uint32_t len);
typedef void (*GwCwdFn)(void* ctx, GwSessionId id, const wchar_t* cwd, uint32_t len);
typedef void (*GwRenderDoneFn)(void* ctx);

typedef struct {
    void* context;
    GwSessionFn      on_created;
    GwSessionFn      on_closed;
    GwSessionFn      on_activated;
    GwTitleFn        on_title_changed;
    GwCwdFn          on_cwd_changed;
    GwExitFn         on_child_exit;
    GwRenderDoneFn   on_render_done;
} GwCallbacks;

// ── Engine lifecycle ──
GWAPI GwEngine gw_engine_create(const GwCallbacks* callbacks);
GWAPI void     gw_engine_destroy(GwEngine engine);

// ── Render init (HWND-based for HwndHost) ──
GWAPI int  gw_render_init(GwEngine engine, HWND hwnd,
                           uint32_t width_px, uint32_t height_px,
                           float font_size_pt, const wchar_t* font_family);
GWAPI int  gw_render_resize(GwEngine engine, uint32_t width_px, uint32_t height_px);
GWAPI int  gw_render_set_clear_color(GwEngine engine, uint32_t rgb);
GWAPI int  gw_render_start(GwEngine engine);
GWAPI void gw_render_stop(GwEngine engine);

// ── Session lifecycle ──
GWAPI GwSessionId gw_session_create(GwEngine engine,
                                     const wchar_t* shell_path,
                                     const wchar_t* initial_dir,
                                     uint16_t cols, uint16_t rows);
GWAPI int  gw_session_close(GwEngine engine, GwSessionId id);
GWAPI void gw_session_activate(GwEngine engine, GwSessionId id);

// ── I/O ──
GWAPI int  gw_session_write(GwEngine engine, GwSessionId id,
                             const uint8_t* data, uint32_t len);
GWAPI int  gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                   float x_px, float y_px,
                                   uint32_t button, uint32_t action,
                                   uint32_t mods);
GWAPI int  gw_session_resize(GwEngine engine, GwSessionId id,
                              uint16_t cols, uint16_t rows);

// Scroll viewport (scrollback) by delta rows. Negative=up, positive=down.
// Used for mouse wheel scrollback when mouse tracking is inactive.
GWAPI int  gw_scroll_viewport(GwEngine engine, GwSessionId id,
                               int32_t delta_rows);

// ── TSF/IME ──
GWAPI int  gw_tsf_attach(GwEngine engine, HWND hidden_hwnd);
GWAPI int  gw_tsf_focus(GwEngine engine, GwSessionId id);
GWAPI int  gw_tsf_unfocus(GwEngine engine);
GWAPI int  gw_tsf_send_pending(GwEngine engine);

// ── Query ──
GWAPI uint32_t    gw_session_count(GwEngine engine);
GWAPI GwSessionId gw_active_session_id(GwEngine engine);
GWAPI void        gw_poll_titles(GwEngine engine);

// ── Surface management (Phase 5-E pane split) ──
typedef uint32_t GwSurfaceId;

// Create a render surface (SwapChain) for a pane HWND, bound to a session.
GWAPI GwSurfaceId gw_surface_create(GwEngine engine, HWND hwnd,
                                     GwSessionId session_id,
                                     uint32_t width_px, uint32_t height_px);

// Destroy a render surface.
GWAPI int gw_surface_destroy(GwEngine engine, GwSurfaceId id);

// Resize a render surface (SwapChain + session cols/rows).
GWAPI int gw_surface_resize(GwEngine engine, GwSurfaceId id,
                             uint32_t width_px, uint32_t height_px);

// Set focused surface (switches TSF focus to the surface's session).
GWAPI int gw_surface_focus(GwEngine engine, GwSurfaceId id);

#ifdef __cplusplus
}
#endif
