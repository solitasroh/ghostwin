#ifndef GHOSTWIN_VT_BRIDGE_H
#define GHOSTWIN_VT_BRIDGE_H

/* Pure C bridge to libghostty-vt.
 * This header is safe to include from both C and C++ code.
 * All ghostty opaque types are hidden behind typed handles. */

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ─── Result codes (mirrors GhosttyResult) ─── */
#define VT_OK          0
#define VT_OOM        -1
#define VT_INVALID    -2
#define VT_NO_VALUE   -4

/* ─── Dirty state ─── */
#define VT_DIRTY_CLEAN   0
#define VT_DIRTY_PARTIAL 1
#define VT_DIRTY_FULL    2

/* ─── Cursor style ─── */
#define VT_CURSOR_BAR         0
#define VT_CURSOR_BLOCK       1
#define VT_CURSOR_UNDERLINE   2
#define VT_CURSOR_BLOCK_HOLLOW 3

/* ─── Style flags (bitmask) ─── */
#define VT_STYLE_BOLD          (1 << 0)
#define VT_STYLE_ITALIC        (1 << 1)
#define VT_STYLE_UNDERLINE     (1 << 2)
#define VT_STYLE_STRIKETHROUGH (1 << 3)
#define VT_STYLE_INVERSE       (1 << 4)
#define VT_STYLE_FAINT         (1 << 5)
#define VT_STYLE_BLINK         (1 << 6)
#define VT_STYLE_OVERLINE      (1 << 7)

/* ─── Type-safe opaque handles ─── */
/* BC-11 (pre-m11-backlog-cleanup): terminal / render_state promoted from
 * raw void* to typed opaque-struct pointers so that accidental mix-ups
 * (passing a render_state to a function expecting a terminal) fail at
 * C++ compile time. C code converts implicitly; C++ callers must pass
 * the correct type. Row/Cell iterators already used this pattern. */
typedef struct VtTerminalImpl*     VtTerminal;
typedef struct VtRenderStateImpl*  VtRenderState;
typedef struct VtRowIteratorImpl*  VtRowIterator;
typedef struct VtCellIteratorImpl* VtCellIterator;

/* ─── Color (RGB + alpha) ─── */
typedef struct {
    uint8_t r, g, b, a;
} VtColor;

/* ─── Cursor info ─── */
typedef struct {
    uint16_t x;
    uint16_t y;
    int      style;      /* VT_CURSOR_* */
    bool     visible;
    bool     blink;
    bool     in_viewport;
} VtCursorInfo;

/* ═══════════════════════════════════════════════════
 *  Phase 1/2 API (unchanged)
 * ═══════════════════════════════════════════════════ */

VtTerminal    vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback);
void          vt_bridge_terminal_free(VtTerminal terminal);

VtRenderState vt_bridge_render_state_new(void);
void          vt_bridge_render_state_free(VtRenderState render_state);

void  vt_bridge_write(VtTerminal terminal, const uint8_t* data, size_t len);
int   vt_bridge_resize(VtTerminal terminal, uint16_t cols, uint16_t rows);

/* ═══════════════════════════════════════════════════
 *  Phase 3: Row/Cell iteration API
 * ═══════════════════════════════════════════════════ */

/* ─── Row iterator ─── */
VtRowIterator vt_bridge_row_iterator_new(void);
void          vt_bridge_row_iterator_free(VtRowIterator iter);

/* Initialize from render_state. Must call update_render_state first. */
int  vt_bridge_row_iterator_init(VtRowIterator iter, VtRenderState render_state);

/* Advance to next row. Returns true if valid. */
bool vt_bridge_row_iterator_next(VtRowIterator iter);

/* Check/set dirty state for current row. */
bool vt_bridge_row_is_dirty(VtRowIterator iter);
void vt_bridge_row_set_clean(VtRowIterator iter);

/* ─── Cell iterator ─── */
VtCellIterator vt_bridge_cell_iterator_new(void);
void           vt_bridge_cell_iterator_free(VtCellIterator iter);

/* Initialize from current row. */
int  vt_bridge_cell_iterator_init(VtCellIterator iter, VtRowIterator row);

/* Advance to next cell. Returns true if valid. */
bool vt_bridge_cell_iterator_next(VtCellIterator iter);

/* ─── Cell data access ─── */

/* Number of codepoints (0 = empty cell). */
uint32_t vt_bridge_cell_grapheme_count(VtCellIterator iter);

/* Copy codepoints into buf. Returns number written. */
uint32_t vt_bridge_cell_graphemes(VtCellIterator iter,
                                  uint32_t* buf, uint32_t buf_len);

/* Style flags bitmask (VT_STYLE_*). */
uint8_t vt_bridge_cell_style_flags(VtCellIterator iter);

/* Resolved foreground color. Uses default_fg if cell has no explicit fg.
 * ghostty API already resolves palette indices. */
VtColor vt_bridge_cell_fg_color(VtCellIterator iter, VtRenderState render_state);

/* Resolved background color. Uses default_bg if cell has no explicit bg. */
VtColor vt_bridge_cell_bg_color(VtCellIterator iter, VtRenderState render_state);

/* ─── Cursor ─── */
VtCursorInfo vt_bridge_get_cursor(VtRenderState render_state);

/* ─── Update render state without resetting dirty (Phase 3) ─── */
void vt_bridge_update_render_state_no_reset(VtRenderState render_state, VtTerminal terminal);

/* ─── Global dirty reset ─── */
void vt_bridge_reset_dirty(VtRenderState render_state);

/* ═══════════════════════════════════════════════════
 *  Phase 4-B: Terminal mode query API
 * ═══════════════════════════════════════════════════ */

/* Scroll viewport by delta rows. Negative=up, positive=down. */
void vt_bridge_scroll_viewport(VtTerminal terminal, int32_t delta_rows);

/* DEC Private Mode values (ghostty_mode_new(value, false)) */
#define VT_MODE_DECCKM          1     /* Application Cursor Keys */
#define VT_MODE_BRACKETED_PASTE 2004  /* Bracketed Paste Mode */

/* Query a DEC Private Mode state.
 * Returns VT_OK and sets *out_value, or VT_INVALID on error. */
int vt_bridge_mode_get(VtTerminal terminal, uint16_t mode_value, bool* out_value);

/* ═══════════════════════════════════════════════════
 *  Phase 5-B: OSC title/CWD callback + query API
 * ═══════════════════════════════════════════════════ */

/* Title changed callback (called from write() context — I/O thread).
 * terminal: raw ghostty terminal handle
 * userdata: pointer set via vt_bridge_set_title_callback */
typedef void (*VtTitleChangedFn)(VtTerminal terminal, void* userdata);

/* Register title changed callback on a terminal.
 * Pass NULL to disable. */
void vt_bridge_set_title_callback(VtTerminal terminal, VtTitleChangedFn fn, void* userdata);

/* Get current terminal title (OSC 0/2). Returns UTF-8 string.
 * out_ptr receives pointer to internal buffer (valid until next write()).
 * out_len receives byte length.
 * Returns VT_OK on success, VT_NO_VALUE if no title set. */
int vt_bridge_get_title(VtTerminal terminal, const char** out_ptr, size_t* out_len);

/* Get current terminal PWD (OSC 7). Returns UTF-8 string.
 * out_ptr receives pointer to internal buffer (valid until next write()).
 * out_len receives byte length.
 * Returns VT_OK on success, VT_NO_VALUE if no pwd set. */
int vt_bridge_get_pwd(VtTerminal terminal, const char** out_ptr, size_t* out_len);

/* ═══════════════════════════════════════════════════
 *  Phase 6-A: OSC 9/99/777 desktop notification callback
 * ═══════════════════════════════════════════════════ */

/* Desktop notification callback (called from write() context — I/O thread).
 * kind: reserved (always 0 for now; future: distinguish OSC 9/99/777).
 * title/body: UTF-8. body may be empty (len=0). */
typedef void (*VtDesktopNotifyFn)(VtTerminal terminal, void* userdata,
                                  const char* title, size_t title_len,
                                  const char* body, size_t body_len);

/* Register desktop notification callback on a terminal.
 * Pass NULL fn to disable. Sets GHOSTTY_TERMINAL_OPT_USERDATA to userdata. */
void vt_bridge_set_desktop_notify_callback(VtTerminal terminal,
                                           VtDesktopNotifyFn fn,
                                           void* userdata);

/* Mouse shape callback (called from write() context — I/O thread).
 * shape value matches ghostty_action_mouse_shape_e. */
typedef void (*VtMouseShapeFn)(VtTerminal terminal, void* userdata, int32_t shape);

/* Register mouse shape callback on a terminal.
 * Pass NULL fn to disable. Sets GHOSTTY_TERMINAL_OPT_USERDATA to userdata. */
void vt_bridge_set_mouse_shape_callback(VtTerminal terminal,
                                        VtMouseShapeFn fn,
                                        void* userdata);

#ifdef __cplusplus
}
#endif

#endif /* GHOSTWIN_VT_BRIDGE_H */
