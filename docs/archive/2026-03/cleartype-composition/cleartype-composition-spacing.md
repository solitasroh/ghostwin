# ClearType Composition Spacing Analysis

## Analysis Target
- **Files**: `glyph_atlas.cpp`, `quad_builder.cpp`, `render_state.h`, `shader_vs.hlsl`, `shader_ps.hlsl`, `render_constants.h`, `winui_app.cpp`, `dx11_renderer.cpp`
- **Date**: 2026-03-30
- **Scope**: Font metrics accuracy, subpixel positioning, DPI handling, grid consistency

---

## Overall Score: 72/100

| Category | Score | Max |
|----------|-------|-----|
| Cell Metrics Accuracy | 18 | 25 |
| Subpixel Positioning | 17 | 25 |
| DPI Handling | 15 | 25 |
| Grid Consistency | 22 | 25 |

---

## 1. Cell Metrics Accuracy (18/25)

### How cell_width/cell_height is calculated

`glyph_atlas.cpp:251-274` (`compute_cell_metrics`):

```
scale = dip_size / designUnitsPerEm
ascent_px  = round(ascent * scale)
cell_h     = round((ascent + descent + lineGap) * scale)
cell_w     = round(advanceWidth('M') * scale)
```

| Metric | Method | Evaluation |
|--------|--------|------------|
| cell_h | `ascent + descent + lineGap`, rounded | Correct standard formula |
| cell_w | `advanceWidth('M')` via `GetDesignGlyphMetrics` | Acceptable but non-standard |
| ascent_px | `ascent * scale + 0.5f` (round-to-nearest) | Correct |
| scale | `dip_size / designUnitsPerEm` | Correct DIP-based scaling |

### Issues Found

| Severity | Issue | Detail |
|----------|-------|--------|
| Warning | cell_w measured from 'M' advance, not average advance | Most terminal emulators use `GetDesignGlyphMetrics` for space (U+0020) or average char width from `DWRITE_FONT_METRICS`. 'M' is the widest Latin glyph; for proportional fallback fonts this doesn't matter, but for the primary monospace font, all glyphs share the same advance width, so 'M' happens to be correct. However, the safer canonical method is to use IDWriteTextLayout::GetMetrics() with "X" or the font's OS/2 xAvgCharWidth. Risk: minimal for true monospace fonts like Cascadia Mono, but could cause misalignment with fonts like Menlo where hinting may adjust 'M' differently. |
| Warning | `lineGap` included in cell_h | Some terminals (Alacritty, kitty) exclude `lineGap` from cell_h and apply it as inter-line spacing. Including it is a valid choice (Windows Terminal does the same) but produces taller cells. Not a bug, but a design choice that differs from Ghostty upstream which uses `line_gap` separately. |
| OK | No CJK-specific cell width adjustment | CJK wide characters are handled at the QuadBuilder level (2x cell_w), not at the metrics level. This is correct. |

### Baseline (ascent) calculation

`ascent_px = (uint32_t)(ascent + 0.5f)` -- simple round-to-nearest, which is correct. The baseline is passed to QuadBuilder and used as `py + baseline_ + glyph.offset_y` in the glyph position calculation (quad_builder.cpp:99). The glyph `offset_y` comes from `bounds.top` from DirectWrite's `GetAlphaTextureBounds`, which is relative to the (0,0) baseline origin passed to `CreateGlyphRunAnalysis`. This is a correct chain: cell_top + ascent + bounds.top = final glyph Y.

### CJK wide characters

`quad_builder.cpp:20-29` has a hardcoded `is_wide_codepoint()` function covering:
- Hangul Jamo (0x1100-0x115F)
- CJK Radicals through CJK Unified (0x2E80-0x9FFF)
- Hangul Syllables (0xAC00-0xD7AF)
- CJK Compatibility (0xF900-0xFAFF)
- Fullwidth Forms (0xFF01-0xFF60)
- CJK Extension B+ (0x20000-0x2FA1F)

Missing ranges: CJK Unified Extension A (0x3400-0x4DBF is covered), but some edge cases like Enclosed CJK (0x3200-0x33FF) are partially covered. Emoji with East Asian Width property "W" are not handled. This is acceptable for initial implementation but should eventually use ICU or libghostty's width data.

---

## 2. Subpixel Positioning (17/25)

### Glyph position quantization

All positions in QuadInstance are `uint16_t` (integer pixels):

```cpp
// quad_builder.cpp:64-65
uint16_t px = (uint16_t)(c * cell_w_);
uint16_t py = (uint16_t)(r * cell_h_);
```

Text glyph positioning (quad_builder.cpp:98-99):
```cpp
q.pos_x = (uint16_t)(px + (wide ? center_x : glyph.offset_x));
q.pos_y = (uint16_t)(py + (float)baseline_ + glyph.offset_y);
```

| Property | Value | Assessment |
|----------|-------|------------|
| Position storage | uint16_t (integer pixel) | No subpixel precision |
| offset_x/offset_y | float -> truncated to uint16_t | Loses fractional pixel offset |
| Grid positions | Integer multiples of cell_w/cell_h | Pixel-perfect grid |
| Glyph-within-cell | Truncated float | Up to 1px positioning error |

### Vertex shader positioning

```hlsl
// shader_vs.hlsl:50-53
float2 pixelPos = position + corner * size;
output.pos = float4(pixelPos * positionScale + float2(-1.0, 1.0), 0.0, 1.0);
```

Where `positionScale = float2(2.0/bb_width, -2.0/bb_height)` (dx11_renderer.cpp:451).

This maps pixel coordinates directly to NDC [-1,1]. The transform is:
- `NDC_x = pixelPos.x * 2/W - 1`
- `NDC_y = -pixelPos.y * 2/H + 1`

**Half-pixel offset**: There is NO half-pixel correction. In D3D11, pixel centers are at (x+0.5, y+0.5). When a quad starts at integer pixel position `px`, the vertex is at NDC `(px*2/W - 1)`, which maps to pixel edge, not pixel center. For quads that are always axis-aligned and integer-sized, this means the rasterizer's top-left rule will consistently include the correct pixels. However, for ClearType subpixel rendering, this can cause a 1-subpixel shift on certain display configurations.

**Comparison with Alacritty**: Alacritty uses `glyph_cache.rs` with `f32` positions throughout and applies subpixel quantization at rasterization time (8 subpixel positions per pixel). GhostWin truncates to integer at the QuadInstance level, losing subpixel information entirely. This is a quality trade-off for the 32B packed format.

### Issues Found

| Severity | Issue | Detail |
|----------|-------|--------|
| Warning | Float-to-uint16 truncation in glyph offset | `glyph.offset_x` and `glyph.offset_y` are floats from DirectWrite bounds, cast to uint16_t without rounding. `(uint16_t)(px + glyph.offset_x)` truncates; should use `(uint16_t)(px + glyph.offset_x + 0.5f)` for correct rounding. Can cause 1px leftward/upward drift. |
| Warning | No subpixel glyph positioning | Alacritty, Windows Terminal, and Ghostty all support subpixel glyph positioning (typically 4-8 subpixel steps). GhostWin quantizes to integer pixels. This is visible as slightly uneven character spacing with certain font sizes. Acceptable trade-off for 32B QuadInstance format. |
| Info | No half-pixel offset in VS | D3D11 uses pixel-center-at-half convention. The current approach works because all quads are integer-aligned rectangles, but adding `+ 0.5` to pixelPos before the NDC transform would be more mathematically correct and prevent potential rasterization ambiguity at quad edges. |

---

## 3. DPI Handling (15/25)

### CompositionScaleX/Y in InitializeD3D11

`winui_app.cpp:169-176`:
```cpp
float scaleX = panel.CompositionScaleX();
float scaleY = panel.CompositionScaleY();
float w = (float)(panel.ActualWidth()) * scaleX;
float h = (float)(panel.ActualHeight()) * scaleY;
```

The swapchain is created at physical pixel dimensions. This is correct.

### DPI change handling

`winui_app.cpp:67-71`:
```cpp
m_panel.CompositionScaleChanged([self = get_strong()](...) {
    self->m_resize_timer.Stop();
    self->m_resize_timer.Start();
});
```

DPI change triggers the same debounce path as resize (100ms). The resize handler (winui_app.cpp:150-160) re-reads `CompositionScaleX/Y` and stores physical pixel dimensions. The render thread then calls `resize_swapchain()`.

### Critical Gap: Font NOT re-rasterized on DPI change

| Component | DPI-Aware? | Detail |
|-----------|------------|--------|
| SwapChainPanel size | Yes | Physical pixels via CompositionScaleX/Y |
| Swapchain buffers | Yes | ResizeBuffers with physical pixel dimensions |
| Viewport | Yes | D3D11_VIEWPORT uses bb_width/bb_height |
| positionScale constant | Yes | Updated via update_constant_buffer() |
| Font rasterization | **NO** | GlyphAtlas uses `dip_size` fixed at creation, never updated |
| Cell metrics | **NO** | cell_w/cell_h computed once, never recalculated |

This is the most significant issue. The font size is computed as:

```cpp
dip_size = config.font_size_pt * (96.0f / 72.0f);  // line 164
```

This is always in DIP (96 DPI reference). DirectWrite's `CreateGlyphRunAnalysis` is called with `pixelsPerDip = 1.0f` (line 514). On a 150% DPI display (144 DPI), the glyphs are rasterized at 96 DPI size and then stretched by the SwapChainPanel's composition scaling. This means:

1. **Glyphs are blurry at non-100% DPI** -- rasterized at logical pixel size, stretched by GPU
2. **ClearType subpixel color fringing** -- subpixel boundaries don't align after scaling
3. **Cell metrics are in logical pixels** -- grid positions multiply logical cell size by integer row/col, but the swapchain is in physical pixels

### What should happen (Windows Terminal / Alacritty pattern)

1. `dip_size` should incorporate DPI: `font_size_pt * (dpi / 72.0f)` or pass `CompositionScaleX` as `pixelsPerDip`
2. On DPI change: destroy atlas, re-create with new DPI, recompute cell metrics
3. Cell grid should be in physical pixels

### Severity

| Severity | Issue | Impact |
|----------|-------|--------|
| **Critical** | Font not re-rasterized at display DPI | Blurry text on HiDPI, ClearType fringing. On a 4K display at 200% scaling, 12pt text is rasterized at 16px but displayed at 32px -- visibly blurry. |
| **Critical** | `pixelsPerDip = 1.0f` hardcoded | DirectWrite rasterizes at 96 DPI regardless of display DPI. Should be `CompositionScaleX` or `dpi/96.0f`. |
| Warning | No atlas invalidation on DPI change | Even if DPI is incorporated, the existing cached glyphs would be wrong after a DPI change. Need atlas clear + rebuild. |

---

## 4. Grid Consistency (22/25)

### Monospace grid alignment

Grid positions are computed as strict integer multiples:

```cpp
// quad_builder.cpp:64-65 (background)
uint16_t px = (uint16_t)(c * cell_w_);
uint16_t py = (uint16_t)(r * cell_h_);

// quad_builder.cpp:88-89 (text)
uint16_t px = (uint16_t)(c * cell_w_);
uint16_t py = (uint16_t)(r * cell_h_);
```

This guarantees pixel-perfect grid alignment for backgrounds. All background quads tile exactly with no gaps or overlaps.

### Inter-character gap consistency

Background quads are sized exactly `cell_w x cell_h` (or `2*cell_w x cell_h` for wide). Since positions are `c * cell_w` and sizes are `cell_w`, adjacent cells share an edge precisely. No inter-cell gaps.

### Pixel bleeding between cells

The atlas uses `D3D11_FILTER_MIN_MAG_MIP_POINT` sampling (dx11_renderer.cpp:400), which eliminates texture bleeding between packed glyphs. Additionally, `stb_rect_pack` allocates with `+1` padding:

```cpp
rect.w = gw + 1;
rect.h = gh + 1;
```

This 1-pixel padding in the atlas prevents adjacent glyph texels from bleeding into each other.

### Atlas packing impact on alignment

| Property | Value | Assessment |
|----------|-------|------------|
| Atlas format | R8G8B8A8_UNORM | 4 bytes/texel, ClearType RGB + A |
| Packing | stb_rect_pack with 1px padding | No bleeding |
| Sampling | Point (nearest-neighbor) | Pixel-perfect, no filtering artifacts |
| UV coordinates | Integer pixels, scaled by atlasScale | Exact texel alignment |
| Atlas size | 1024x1024 initial, 4096 max | Adequate for terminal workload |

### Issues Found

| Severity | Issue | Detail |
|----------|-------|--------|
| Info | Glyph quads may extend beyond cell bounds | Text glyph quads use `glyph.width x glyph.height` from DirectWrite bounds, which can exceed cell_w for italic or large descenders. The 2-pass rendering (backgrounds first, text on top) handles this correctly -- glyph overflows blend onto adjacent cell backgrounds. This is the intended behavior (ADR-008). |
| Warning | Wide char spacer detection is fragile | `quad_builder.cpp:57-60` skips the 2nd cell of a wide char by checking `cp_count == 0` and the previous cell's codepoint. If VtCore doesn't set cp_count=0 for spacer cells, backgrounds could double-render. This depends on upstream libghostty behavior. |
| OK | Cursor aligns to grid | `frame.cursor.x * cell_w_` and `frame.cursor.y * cell_h_` -- consistent with cell grid. |

---

## Summary of Issues

### Critical (Immediate Fix Required)

| # | File | Line | Issue | Recommended Action |
|---|------|------|-------|--------------------|
| 1 | glyph_atlas.cpp | 164, 514 | Font rasterized at 96 DPI regardless of display scaling | Pass `CompositionScaleX` to `CreateGlyphRunAnalysis` as `pixelsPerDip`, or compute `dip_size = pt * (dpi / 72)` |
| 2 | glyph_atlas.cpp | -- | No atlas rebuild on DPI change | Add `GlyphAtlas::rebuild(float new_dpi)` or recreate atlas on CompositionScaleChanged |
| 3 | winui_app.cpp | 265-286 | Resize handler doesn't re-rasterize fonts | On DPI change: destroy + recreate atlas with new scale factor |

### Warning (Improvement Recommended)

| # | File | Line | Issue | Recommended Action |
|---|------|------|-------|--------------------|
| 4 | quad_builder.cpp | 98-99 | Float-to-uint16 truncation (no rounding) | Use `+ 0.5f` before cast: `(uint16_t)(px + glyph.offset_x + 0.5f)` |
| 5 | glyph_atlas.cpp | 268 | cell_w from 'M' advance | Consider `IDWriteTextLayout::GetMetrics()` or OS/2 avgCharWidth |
| 6 | quad_builder.cpp | 57-60 | Fragile wide-char spacer detection | Add explicit `is_spacer` flag to CellData |
| 7 | shader_vs.hlsl | 53 | No half-pixel offset correction | Consider `pixelPos + 0.5` before NDC transform for exact pixel center alignment |

### Info (Reference)

- 2-pass rendering (ADR-008) correctly handles glyph overflow across cell boundaries
- ClearType dual-source blending pipeline is complete and correct (Windows Terminal compatible)
- Gamma correction from `lhecker/dwrite-hlsl` is properly integrated
- Atlas point sampling eliminates texture bleeding
- 32B QuadInstance packing is an acceptable trade-off vs. subpixel precision

---

## Improvement Recommendations

### 1. DPI-Aware Font Rasterization (Critical)

```cpp
// AtlasConfig should include DPI scale
struct AtlasConfig {
    float font_size_pt = 12.0f;
    float dpi_scale = 1.0f;  // CompositionScaleX
    // ...
};

// compute_cell_metrics should use physical pixels
void compute_cell_metrics() {
    float physical_size = dip_size * dpi_scale;  // or: pt * (dpi / 72)
    float scale = physical_size / metrics.designUnitsPerEm;
    // ...
}

// CreateGlyphRunAnalysis should use dpi_scale
dwrite_factory->CreateGlyphRunAnalysis(
    &glyph_run,
    dpi_scale,  // pixelsPerDip -- NOT 1.0f
    nullptr, ...);
```

### 2. Atlas Rebuild on DPI Change

```cpp
// In RenderLoop resize handler:
if (dpi_changed) {
    m_atlas = GlyphAtlas::create(device, config_with_new_dpi);
    m_renderer->set_atlas_srv(m_atlas->srv());
    builder = QuadBuilder(m_atlas->cell_width(), ...);
}
```

### 3. Subpixel Rounding Fix

```cpp
// quad_builder.cpp:98-99
q.pos_x = (uint16_t)(px + (wide ? center_x : glyph.offset_x) + 0.5f);
q.pos_y = (uint16_t)(py + (float)baseline_ + glyph.offset_y + 0.5f);
```

### 4. Future: Subpixel Positioning (if quality demands it)

Would require changing QuadInstance pos_x/pos_y from uint16_t to a fixed-point format (e.g., 12.4 fixed point in the same 16 bits), and updating the vertex shader unpacking accordingly. This is a larger change that trades GPU bandwidth for positioning accuracy.

---

## Reference Implementations

| Terminal | Cell Width Source | Subpixel Positioning | DPI Handling |
|----------|------------------|---------------------|--------------|
| Windows Terminal | IDWriteTextLayout metrics | Integer pixel + ClearType subpixel AA | Full DPI-aware, re-rasterize on change |
| Alacritty | FreeType advance width | 8 subpixel positions per pixel | DPI from winit, re-rasterize |
| Ghostty (macOS) | Core Text advance | Subpixel via Core Text | Retina-aware |
| GhostWin (current) | GetDesignGlyphMetrics('M') | Integer pixel only | **96 DPI fixed -- blurry on HiDPI** |
