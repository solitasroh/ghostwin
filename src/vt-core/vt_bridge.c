/* vt_bridge.c — Pure C bridge to libghostty-vt.
 * This is the ONLY file that includes ghostty C headers.
 * Compiled as C (not C++) to avoid MSVC typedef struct X* X collision. */

#include "vt_bridge.h"
#include <ghostty/vt/types.h>
#include <ghostty/vt/allocator.h>
#include <ghostty/vt/terminal.h>
#include <ghostty/vt/render.h>
#include <stdio.h>

void* vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback) {
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
    return term;
}

void vt_bridge_terminal_free(void* terminal) {
    if (terminal) ghostty_terminal_free((GhosttyTerminal)terminal);
}

void* vt_bridge_render_state_new(void) {
    GhosttyRenderState rs = NULL;
    GhosttyResult rc = ghostty_render_state_new(NULL, &rs);
    if (rc != GHOSTTY_SUCCESS || !rs) {
        fprintf(stderr, "[vt_bridge] ghostty_render_state_new failed: %d\n", rc);
        return NULL;
    }
    return rs;
}

void vt_bridge_render_state_free(void* render_state) {
    if (render_state) ghostty_render_state_free((GhosttyRenderState)render_state);
}

void vt_bridge_write(void* terminal, const uint8_t* data, size_t len) {
    if (terminal && data && len > 0) {
        ghostty_terminal_vt_write((GhosttyTerminal)terminal, data, len);
    }
}

VtRenderInfo vt_bridge_update_render_state(void* render_state, void* terminal) {
    VtRenderInfo info = {0};
    if (!render_state || !terminal) return info;

    GhosttyRenderState rs = (GhosttyRenderState)render_state;
    GhosttyTerminal term = (GhosttyTerminal)terminal;

    ghostty_render_state_update(rs, term);

    GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
    info.dirty = (int)dirty;

    uint16_t val = 0;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_COLS, &val);
    info.cols = val;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_ROWS, &val);
    info.rows = val;

    bool has_cursor = false;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &has_cursor);
    if (has_cursor) {
        ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &info.cursor_x);
        ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &info.cursor_y);
    }

    bool visible = false;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE, &visible);
    info.cursor_visible = visible ? 1 : 0;

    GhosttyRenderStateCursorVisualStyle style = 0;
    ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISUAL_STYLE, &style);
    info.cursor_style = (int)style;

    /* Reset dirty after reading */
    GhosttyRenderStateDirty clean = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
    ghostty_render_state_set(rs, GHOSTTY_RENDER_STATE_OPTION_DIRTY, &clean);

    return info;
}

int vt_bridge_resize(void* terminal, uint16_t cols, uint16_t rows) {
    if (!terminal) return VT_INVALID;
    GhosttyResult rc = ghostty_terminal_resize((GhosttyTerminal)terminal, cols, rows, 0, 0);
    return (int)rc;
}
