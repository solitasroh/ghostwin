#ifndef GHOSTWIN_VT_BRIDGE_H
#define GHOSTWIN_VT_BRIDGE_H

/* Pure C bridge to libghostty-vt.
 * This header is safe to include from both C and C++ code.
 * All ghostty opaque types are hidden behind void*. */

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Result codes (mirrors GhosttyResult) */
#define VT_OK          0
#define VT_OOM        -1
#define VT_INVALID    -2

/* Dirty state */
#define VT_DIRTY_CLEAN   0
#define VT_DIRTY_PARTIAL 1
#define VT_DIRTY_FULL    2

/* Cursor style */
#define VT_CURSOR_BAR         0
#define VT_CURSOR_BLOCK       1
#define VT_CURSOR_UNDERLINE   2
#define VT_CURSOR_BLOCK_HOLLOW 3

typedef struct {
    int dirty;
    uint16_t cols;
    uint16_t rows;
    uint16_t cursor_x;
    uint16_t cursor_y;
    int cursor_visible;
    int cursor_style;
} VtRenderInfo;

/* Create terminal. Returns opaque handle, NULL on failure. */
void* vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback);

/* Free terminal. */
void vt_bridge_terminal_free(void* terminal);

/* Create render state. Returns opaque handle, NULL on failure. */
void* vt_bridge_render_state_new(void);

/* Free render state. */
void vt_bridge_render_state_free(void* render_state);

/* Write VT data. */
void vt_bridge_write(void* terminal, const uint8_t* data, size_t len);

/* Update render state from terminal. */
VtRenderInfo vt_bridge_update_render_state(void* render_state, void* terminal);

/* Resize terminal. Returns 0 on success. */
int vt_bridge_resize(void* terminal, uint16_t cols, uint16_t rows);

#ifdef __cplusplus
}
#endif

#endif /* GHOSTWIN_VT_BRIDGE_H */
