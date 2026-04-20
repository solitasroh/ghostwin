/* vt_bridge.c — Pure C bridge to libghostty-vt.
 * This is the ONLY file that includes ghostty C headers.
 * Compiled as C (not C++) to avoid MSVC typedef struct X* X collision. */

#include "vt_bridge.h"
#include <ghostty/vt/types.h>
#include <ghostty/vt/allocator.h>
#include <ghostty/vt/terminal.h>
#include <ghostty/vt/render.h>
#include <ghostty/vt/style.h>
#include <ghostty/vt/color.h>
#include <ghostty/vt/modes.h>
#include <stdio.h>
#include <string.h>

/* ═══════════════════════════════════════════════════
 *  Phase 1/2 API (unchanged)
 * ═══════════════════════════════════════════════════ */

VtTerminal vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback) {
    GhosttyTerminalOptions opts = {0};
    opts.cols = cols;
    opts.rows = rows;
    opts.max_scrollback = max_scrollback;

    GhosttyTerminal term = NULL;
    GhosttyResult rc = ghostty_terminal_new(NULL, &term, opts);
    if (rc != GHOSTTY_SUCCESS || !term) {
        fprintf(stderr, "[vt_bridge] ghostty_terminal_new failed: %d\n", rc);
        return NULL;
    }
    return (VtTerminal)term;  /* same opaque pointer, different typedef */
}

void vt_bridge_terminal_free(VtTerminal terminal) {
    if (terminal) ghostty_terminal_free((GhosttyTerminal)terminal);
}

VtRenderState vt_bridge_render_state_new(void) {
    GhosttyRenderState rs = NULL;
    GhosttyResult rc = ghostty_render_state_new(NULL, &rs);
    if (rc != GHOSTTY_SUCCESS || !rs) {
        fprintf(stderr, "[vt_bridge] ghostty_render_state_new failed: %d\n", rc);
        return NULL;
    }
    return (VtRenderState)rs;  /* same opaque pointer, different typedef */
}

void vt_bridge_render_state_free(VtRenderState render_state) {
    if (render_state) ghostty_render_state_free((GhosttyRenderState)render_state);
}

void vt_bridge_write(VtTerminal terminal, const uint8_t* data, size_t len) {
    if (terminal && data && len > 0) {
        ghostty_terminal_vt_write((GhosttyTerminal)terminal, data, len);
    }
}

/* NOTE: vt_bridge_update_render_state() removed — dead code since Phase 3.
 * Use vt_bridge_update_render_state_no_reset() + vt_bridge_reset_dirty() instead. */

int vt_bridge_resize(VtTerminal terminal, uint16_t cols, uint16_t rows) {
    if (!terminal) return VT_INVALID;
    GhosttyResult rc = ghostty_terminal_resize((GhosttyTerminal)terminal, cols, rows, 0, 0);
    return (int)rc;
}

/* ═══════════════════════════════════════════════════
 *  Phase 3: Row iterator
 * ═══════════════════════════════════════════════════ */

VtRowIterator vt_bridge_row_iterator_new(void) {
    GhosttyRenderStateRowIterator iter = NULL;
    GhosttyResult rc = ghostty_render_state_row_iterator_new(NULL, &iter);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] row_iterator_new failed: %d\n", rc);
        return NULL;
    }
    return (VtRowIterator)iter;
}

void vt_bridge_row_iterator_free(VtRowIterator iter) {
    if (iter) ghostty_render_state_row_iterator_free((GhosttyRenderStateRowIterator)iter);
}

int vt_bridge_row_iterator_init(VtRowIterator iter, VtRenderState render_state) {
    if (!iter || !render_state) return VT_INVALID;
    GhosttyResult rc = ghostty_render_state_get(
        (GhosttyRenderState)render_state,
        GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR,
        &iter);
    return (int)rc;
}

bool vt_bridge_row_iterator_next(VtRowIterator iter) {
    if (!iter) return false;
    return ghostty_render_state_row_iterator_next((GhosttyRenderStateRowIterator)iter);
}

bool vt_bridge_row_is_dirty(VtRowIterator iter) {
    if (!iter) return false;
    bool dirty = false;
    ghostty_render_state_row_get(
        (GhosttyRenderStateRowIterator)iter,
        GHOSTTY_RENDER_STATE_ROW_DATA_DIRTY,
        &dirty);
    return dirty;
}

void vt_bridge_row_set_clean(VtRowIterator iter) {
    if (!iter) return;
    bool clean = false;
    ghostty_render_state_row_set(
        (GhosttyRenderStateRowIterator)iter,
        GHOSTTY_RENDER_STATE_ROW_OPTION_DIRTY,
        &clean);
}

/* ═══════════════════════════════════════════════════
 *  Phase 3: Cell iterator
 * ═══════════════════════════════════════════════════ */

VtCellIterator vt_bridge_cell_iterator_new(void) {
    GhosttyRenderStateRowCells cells = NULL;
    GhosttyResult rc = ghostty_render_state_row_cells_new(NULL, &cells);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] cell_iterator_new failed: %d\n", rc);
        return NULL;
    }
    return (VtCellIterator)cells;
}

void vt_bridge_cell_iterator_free(VtCellIterator iter) {
    if (iter) ghostty_render_state_row_cells_free((GhosttyRenderStateRowCells)iter);
}

int vt_bridge_cell_iterator_init(VtCellIterator iter, VtRowIterator row) {
    if (!iter || !row) return VT_INVALID;
    GhosttyResult rc = ghostty_render_state_row_get(
        (GhosttyRenderStateRowIterator)row,
        GHOSTTY_RENDER_STATE_ROW_DATA_CELLS,
        &iter);
    return (int)rc;
}

bool vt_bridge_cell_iterator_next(VtCellIterator iter) {
    if (!iter) return false;
    return ghostty_render_state_row_cells_next((GhosttyRenderStateRowCells)iter);
}

/* ═══════════════════════════════════════════════════
 *  Phase 3: Cell data access
 * ═══════════════════════════════════════════════════ */

uint32_t vt_bridge_cell_grapheme_count(VtCellIterator iter) {
    if (!iter) return 0;
    uint32_t len = 0;
    ghostty_render_state_row_cells_get(
        (GhosttyRenderStateRowCells)iter,
        GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN,
        &len);
    return len;
}

uint32_t vt_bridge_cell_graphemes(VtCellIterator iter,
                                  uint32_t* buf, uint32_t buf_len) {
    if (!iter || !buf || buf_len == 0) return 0;
    uint32_t len = vt_bridge_cell_grapheme_count(iter);
    if (len == 0) return 0;
    uint32_t to_copy = len < buf_len ? len : buf_len;

    /* ghostty writes all grapheme codepoints into the provided buffer */
    uint32_t temp[16];
    uint32_t* target = (to_copy <= 16) ? temp : buf;
    ghostty_render_state_row_cells_get(
        (GhosttyRenderStateRowCells)iter,
        GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_BUF,
        target);
    if (target == temp) {
        memcpy(buf, temp, to_copy * sizeof(uint32_t));
    }
    return to_copy;
}

uint8_t vt_bridge_cell_style_flags(VtCellIterator iter) {
    if (!iter) return 0;
    GhosttyStyle style = GHOSTTY_INIT_SIZED(GhosttyStyle);
    GhosttyResult rc = ghostty_render_state_row_cells_get(
        (GhosttyRenderStateRowCells)iter,
        GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE,
        &style);
    if (rc != GHOSTTY_SUCCESS) return 0;

    uint8_t flags = 0;
    if (style.bold)          flags |= VT_STYLE_BOLD;
    if (style.italic)        flags |= VT_STYLE_ITALIC;
    if (style.underline)     flags |= VT_STYLE_UNDERLINE;
    if (style.strikethrough) flags |= VT_STYLE_STRIKETHROUGH;
    if (style.inverse)       flags |= VT_STYLE_INVERSE;
    if (style.faint)         flags |= VT_STYLE_FAINT;
    if (style.blink)         flags |= VT_STYLE_BLINK;
    if (style.overline)      flags |= VT_STYLE_OVERLINE;
    return flags;
}

/* Helper: get default colors from render_state */
static GhosttyRenderStateColors get_colors(VtRenderState render_state) {
    GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
    if (render_state) {
        ghostty_render_state_colors_get((GhosttyRenderState)render_state, &colors);
    }
    return colors;
}

static VtColor rgb_to_vtcolor(GhosttyColorRgb rgb) {
    VtColor c;
    c.r = rgb.r;
    c.g = rgb.g;
    c.b = rgb.b;
    c.a = 255;
    return c;
}

VtColor vt_bridge_cell_fg_color(VtCellIterator iter, VtRenderState render_state) {
    VtColor fallback = {255, 255, 255, 255};
    if (!iter) return fallback;

    GhosttyColorRgb rgb;
    GhosttyResult rc = ghostty_render_state_row_cells_get(
        (GhosttyRenderStateRowCells)iter,
        GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR,
        &rgb);

    if (rc == GHOSTTY_SUCCESS) {
        return rgb_to_vtcolor(rgb);
    }

    /* No explicit fg — use default foreground from render state */
    GhosttyRenderStateColors colors = get_colors(render_state);
    return rgb_to_vtcolor(colors.foreground);
}

VtColor vt_bridge_cell_bg_color(VtCellIterator iter, VtRenderState render_state) {
    VtColor fallback = {0, 0, 0, 255};
    if (!iter) return fallback;

    GhosttyColorRgb rgb;
    GhosttyResult rc = ghostty_render_state_row_cells_get(
        (GhosttyRenderStateRowCells)iter,
        GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR,
        &rgb);

    if (rc == GHOSTTY_SUCCESS) {
        return rgb_to_vtcolor(rgb);
    }

    /* No explicit bg — use default background from render state */
    GhosttyRenderStateColors colors = get_colors(render_state);
    return rgb_to_vtcolor(colors.background);
}

/* ═══════════════════════════════════════════════════
 *  Phase 3: Cursor
 * ═══════════════════════════════════════════════════ */

VtCursorInfo vt_bridge_get_cursor(VtRenderState render_state) {
    VtCursorInfo info = {0};
    if (!render_state) return info;
    GhosttyRenderState rs = (GhosttyRenderState)render_state;

    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE, &info.visible);
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_BLINKING, &info.blink);
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &info.in_viewport);

    if (info.in_viewport) {
        ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &info.x);
        ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &info.y);
    }

    GhosttyRenderStateCursorVisualStyle vs = 0;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISUAL_STYLE, &vs);
    info.style = (int)vs;

    return info;
}

/* ═══════════════════════════════════════════════════
 *  Phase 3: Dirty reset
 * ═══════════════════════════════════════════════════ */

void vt_bridge_update_render_state_no_reset(VtRenderState render_state, VtTerminal terminal) {
    if (!render_state || !terminal) return;
    GhosttyResult rc = ghostty_render_state_update((GhosttyRenderState)render_state, (GhosttyTerminal)terminal);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_render_state_update failed: %d\n", rc);
    }
}

void vt_bridge_reset_dirty(VtRenderState render_state) {
    if (!render_state) return;
    GhosttyRenderStateDirty clean = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
    GhosttyResult rc = ghostty_render_state_set(
        (GhosttyRenderState)render_state,
        GHOSTTY_RENDER_STATE_OPTION_DIRTY,
        &clean);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_render_state_set(DIRTY) failed: %d\n", rc);
    }
}

/* ═══════════════════════════════════════════════════
 *  M-10b: Scroll viewport
 * ═══════════════════════════════════════════════════ */

void vt_bridge_scroll_viewport(VtTerminal terminal, int32_t delta_rows) {
    if (!terminal) return;
    GhosttyTerminalScrollViewport sv;
    sv.tag = GHOSTTY_SCROLL_VIEWPORT_DELTA;
    sv.value.delta = (intptr_t)delta_rows;
    ghostty_terminal_scroll_viewport((GhosttyTerminal)terminal, sv);
}

/* ═══════════════════════════════════════════════════
 *  Phase 4-B: Terminal mode query
 * ═══════════════════════════════════════════════════ */

int vt_bridge_mode_get(VtTerminal terminal, uint16_t mode_value, bool* out_value) {
    if (!terminal || !out_value) return VT_INVALID;
    GhosttyMode mode = ghostty_mode_new(mode_value, false);  /* DEC Private Mode */
    GhosttyResult rc = ghostty_terminal_mode_get(
        (GhosttyTerminal)terminal, mode, out_value);
    return (rc == GHOSTTY_SUCCESS) ? VT_OK : VT_INVALID;
}

/* ═══════════════════════════════════════════════════
 *  Phase 5-B: OSC title/CWD callback + query
 * ═══════════════════════════════════════════════════ */

void vt_bridge_set_title_callback(VtTerminal terminal, VtTitleChangedFn fn, void* userdata) {
    if (!terminal) return;
    GhosttyTerminal t = (GhosttyTerminal)terminal;

    /* ghostty_terminal_set expects the VALUE itself as const void*, not a pointer TO it.
     * Zig side: @ptrCast(@alignCast(value)) reinterprets the pointer bit pattern.
     * Passing &fn (stack address) would store a dangling pointer → use-after-free.
     * Passing (void*)fn stores the actual function pointer value. */
    GhosttyResult rc = ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_USERDATA, userdata);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_terminal_set(USERDATA) failed: %d\n", rc);
    }

    if (fn) {
        rc = ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_TITLE_CHANGED, (const void*)fn);
    } else {
        rc = ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_TITLE_CHANGED, NULL);
    }
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_terminal_set(TITLE_CHANGED) failed: %d\n", rc);
    }
}

int vt_bridge_get_title(VtTerminal terminal, const char** out_ptr, size_t* out_len) {
    if (!terminal || !out_ptr || !out_len) return VT_INVALID;
    GhosttyString str = {0};
    GhosttyResult rc = ghostty_terminal_get(
        (GhosttyTerminal)terminal, GHOSTTY_TERMINAL_DATA_TITLE, &str);
    if (rc != GHOSTTY_SUCCESS || !str.ptr || str.len == 0) return VT_NO_VALUE;
    *out_ptr = (const char*)str.ptr;
    *out_len = str.len;
    return VT_OK;
}

int vt_bridge_get_pwd(VtTerminal terminal, const char** out_ptr, size_t* out_len) {
    if (!terminal || !out_ptr || !out_len) return VT_INVALID;
    GhosttyString str = {0};
    GhosttyResult rc = ghostty_terminal_get(
        (GhosttyTerminal)terminal, GHOSTTY_TERMINAL_DATA_PWD, &str);
    if (rc != GHOSTTY_SUCCESS || !str.ptr || str.len == 0) return VT_NO_VALUE;
    *out_ptr = (const char*)str.ptr;
    *out_len = str.len;
    return VT_OK;
}

/* ═══════════════════════════════════════════════════
 *  Phase 6-A: OSC 9/99/777 desktop notification callback
 * ═══════════════════════════════════════════════════ */

void vt_bridge_set_desktop_notify_callback(VtTerminal terminal,
                                           VtDesktopNotifyFn fn,
                                           void* userdata) {
    if (!terminal) return;
    GhosttyTerminal t = (GhosttyTerminal)terminal;

    GhosttyResult rc = ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_USERDATA, userdata);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_terminal_set(USERDATA) failed: %d\n", rc);
    }

    if (fn) {
        rc = ghostty_terminal_set(
            t, GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION, (const void*)fn);
        if (rc != GHOSTTY_SUCCESS) {
            fprintf(stderr, "[vt_bridge] ghostty_terminal_set(DESKTOP_NOTIFICATION) failed: %d\n", rc);
        }
    } else {
        ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION, NULL);
    }
}

void vt_bridge_set_mouse_shape_callback(VtTerminal terminal,
                                        VtMouseShapeFn fn,
                                        void* userdata) {
    if (!terminal) return;
    GhosttyTerminal t = (GhosttyTerminal)terminal;

    GhosttyResult rc = ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_USERDATA, userdata);
    if (rc != GHOSTTY_SUCCESS) {
        fprintf(stderr, "[vt_bridge] ghostty_terminal_set(USERDATA) failed: %d\n", rc);
    }

    if (fn) {
        rc = ghostty_terminal_set(
            t, GHOSTTY_TERMINAL_OPT_MOUSE_SHAPE, (const void*)fn);
        if (rc != GHOSTTY_SUCCESS) {
            fprintf(stderr, "[vt_bridge] ghostty_terminal_set(MOUSE_SHAPE) failed: %d\n", rc);
        }
    } else {
        ghostty_terminal_set(t, GHOSTTY_TERMINAL_OPT_MOUSE_SHAPE, NULL);
    }
}
