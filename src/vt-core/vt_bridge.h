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

/* ─── Render info (Phase 1/2 compat) ─── */
typedef struct {
    int dirty;
    uint16_t cols;
    uint16_t rows;
    uint16_t cursor_x;
    uint16_t cursor_y;
    int cursor_visible;
    int cursor_style;
} VtRenderInfo;

/* ═══════════════════════════════════════════════════
 *  Phase 1/2 API (unchanged)
 * ═══════════════════════════════════════════════════ */

void* vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback);
void  vt_bridge_terminal_free(void* terminal);

void* vt_bridge_render_state_new(void);
void  vt_bridge_render_state_free(void* render_state);

void  vt_bridge_write(void* terminal, const uint8_t* data, size_t len);
VtRenderInfo vt_bridge_update_render_state(void* render_state, void* terminal);
int   vt_bridge_resize(void* terminal, uint16_t cols, uint16_t rows);

/* ═══════════════════════════════════════════════════
 *  Phase 3: Row/Cell iteration API
 * ═══════════════════════════════════════════════════ */

/* ─── Row iterator ─── */
VtRowIterator vt_bridge_row_iterator_new(void);
void          vt_bridge_row_iterator_free(VtRowIterator iter);

/* Initialize from render_state. Must call update_render_state first. */
int  vt_bridge_row_iterator_init(VtRowIterator iter, void* render_state);

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
VtColor vt_bridge_cell_fg_color(VtCellIterator iter, void* render_state);

/* Resolved background color. Uses default_bg if cell has no explicit bg. */
VtColor vt_bridge_cell_bg_color(VtCellIterator iter, void* render_state);

/* ─── Cursor ─── */
VtCursorInfo vt_bridge_get_cursor(void* render_state);

/* ─── Update render state without resetting dirty (Phase 3) ─── */
void vt_bridge_update_render_state_no_reset(void* render_state, void* terminal);

/* ─── Global dirty reset ─── */
void vt_bridge_reset_dirty(void* render_state);

/* ═══════════════════════════════════════════════════
 *  Phase 4-B: Terminal mode query API
 * ═══════════════════════════════════════════════════ */

/* DEC Private Mode values (ghostty_mode_new(value, false)) */
#define VT_MODE_DECCKM          1     /* Application Cursor Keys */
#define VT_MODE_BRACKETED_PASTE 2004  /* Bracketed Paste Mode */

/* Query a DEC Private Mode state.
 * Returns VT_OK and sets *out_value, or VT_INVALID on error. */
int vt_bridge_mode_get(void* terminal, uint16_t mode_value, bool* out_value);

#ifdef __cplusplus
}
#endif

#endif /* GHOSTWIN_VT_BRIDGE_H */
