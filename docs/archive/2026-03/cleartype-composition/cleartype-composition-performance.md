# GhostWin Rendering Pipeline — Quantitative Performance Analysis

**Date:** 2026-03-30
**Analyzer:** Code Analysis Agent (bkit-code-analyzer)
**Files analyzed:** 7 source files + 2 HLSL shaders

---

## Quality Score: 82/100

| Category | Score | Max |
|----------|-------|-----|
| Draw Call Efficiency | 22 | 25 |
| Memory Efficiency | 21 | 25 |
| Frame Pacing | 17 | 25 |
| Shader Complexity | 22 | 25 |

---

## 1. Draw Call Efficiency — 22/25

### Draw Call Count per Frame

**1 draw call per frame** (`DrawIndexedInstanced` at `dx11_renderer.cpp:494`).

All quad types (background, text, cursor, underline) are batched into a single StructuredBuffer upload + single indexed instanced draw. This is optimal.

```
draw_instances() -> ClearRenderTargetView (1) + DrawIndexedInstanced (1) + Present (1)
Total GPU commands per frame: 3 (clear + draw + present)
```

### GPU Instancing Effectiveness

| Aspect | Implementation | Assessment |
|--------|---------------|------------|
| Draw method | `DrawIndexedInstanced(6, count, 0, 0, 0)` | Optimal — single call |
| Index buffer | 6 indices `[0,1,2,0,2,3]`, IMMUTABLE | Optimal — never re-uploaded |
| Instance data | StructuredBuffer, DYNAMIC, MAP_WRITE_DISCARD | Optimal pattern |
| Input Layout | `nullptr` (VS reads from SRV) | Optimal — no IA overhead |

### StructuredBuffer vs Input Layout Overhead

Phase 4-E migrated from 68B Input Layout to 32B StructuredBuffer. Quantitative comparison:

| Metric | Input Layout (68B, old) | StructuredBuffer (32B, current) |
|--------|-------------------------|--------------------------------|
| Per-instance bandwidth | 68 bytes | 32 bytes |
| 80x24 (1920 inst) upload | 127.5 KB | 60.0 KB |
| 200x60 (12000 inst) upload | 796.9 KB | 375.0 KB |
| IA overhead | CreateInputLayout + per-vertex fetch | None (SV_InstanceID fetch) |
| VS unpacking | None | 4 bitwise ops per vertex |

The VS unpacking cost (4 bitwise AND/shift per instance) is negligible compared to the 53% bandwidth reduction. Net positive.

### Buffer Growth Strategy

```cpp
// dx11_renderer.cpp:615-632
if (count > instance_capacity) {
    instance_capacity = count * 2;  // 2x growth
    // Recreate buffer + SRV
}
```

| Aspect | Value | Assessment |
|--------|-------|------------|
| Initial capacity | 1024 instances (32 KB) | Good for 80x24 |
| Growth factor | 2x | Standard amortized O(1) |
| Shrink policy | Never shrinks | Acceptable (avoids thrash) |
| SRV recreation | On every grow | Necessary (view tied to buffer) |

**Deduction (-3):** Buffer growth calls `CreateBuffer` + `CreateShaderResourceView` which are GPU-stalling operations. For typical terminal use this is rare (happens once or twice on first resize), but a pre-computed maximum based on `kMaxRows * cols * kInstanceMultiplier` would eliminate runtime allocation entirely.

---

## 2. Memory Efficiency — 21/25

### Instance Buffer Size per Frame

Formula: `count * 32 bytes`, where count = backgrounds + glyphs + decorations + cursor.

| Terminal Size | Cells | Max Instances (3x+1) | Buffer Size | Staging Buffer (CPU) |
|---------------|-------|----------------------|-------------|---------------------|
| 80x24 | 1,920 | 5,761 | 180.0 KB | 180.0 KB |
| 120x40 | 4,800 | 14,401 | 450.0 KB | 450.0 KB |
| 200x60 | 12,000 | 36,001 | 1,125.0 KB | 1,125.0 KB |

Staging buffer (`m_staging`) duplicates this on the CPU side (`winui_app.cpp:243-244`):
```cpp
m_staging.resize(cols * rows * kInstanceMultiplier + 1);
```

### Atlas Texture Memory

| Parameter | Value | Source |
|-----------|-------|--------|
| Initial size | 1024 x 1024 | `kInitialAtlasSize = 1024` |
| Max size | 4096 x 4096 | `kMaxAtlasSize = 4096` (constant defined but not enforced in code) |
| Format | `DXGI_FORMAT_R8G8B8A8_UNORM` | 4 bytes/pixel (ClearType RGBA) |
| Initial memory | 4.0 MB | 1024 * 1024 * 4 |
| Max memory | 64.0 MB | 4096 * 4096 * 4 |
| Packing | stb_rect_pack (skyline) | Efficient bin packing |

**Observation:** Atlas does not dynamically grow. If `stbrp_pack_rects` fails, the glyph is silently dropped (`glyph_atlas.cpp:552-554`). No resize/rebuild mechanism exists.

### Constant Buffer Size

| Field | Size | Purpose |
|-------|------|---------|
| positionScale | 8B | NDC transform (2/w, -2/h) |
| atlasScale | 8B | UV normalization (1/aw, 1/ah) |
| enhancedContrast | 4B | ClearType contrast |
| _pad0 | 4B | 16-byte alignment |
| gammaRatios[4] | 16B | DWrite gamma correction LUT |
| _pad1[2] | 8B | 48B total, 16-byte aligned |
| **Total** | **48B** | Minimum possible for this data |

48 bytes is exactly 3 float4 vectors. This is the minimum constant buffer size for the data conveyed — correct and tight.

### Total GPU Memory Estimate

| Resource | 80x24 | 120x40 | 200x60 |
|----------|-------|--------|--------|
| Instance buffer | 64 KB (cap=2048) | 512 KB (cap=16384) | 2.25 MB (cap=36001*2) |
| Atlas texture | 4.0 MB | 4.0 MB | 4.0 MB |
| Constant buffer | 48 B | 48 B | 48 B |
| Index buffer | 12 B | 12 B | 12 B |
| Backbuffer (2x flip) | ~4.0 MB | ~7.4 MB | ~18.4 MB |
| Staging (CPU) | 180 KB | 450 KB | 1.1 MB |
| **Total (GPU)** | **~8.2 MB** | **~11.9 MB** | **~24.7 MB** |

Backbuffer estimate: `width * height * 4 * 2` (double-buffered BGRA8). Assumes 1920x1080 for 200x60 at ~9px cell width.

**Deduction (-4):**
- Atlas never grows past 1024x1024 (no resize mechanism) — CJK-heavy or Nerd Font-heavy usage will silently lose glyphs.
- `kMaxAtlasSize` constant (4096) is defined but never used in code — the atlas growth path is missing.
- CPU staging buffer is allocated alongside GPU instance buffer (double memory for instance data).

---

## 3. Frame Pacing — 17/25

### Waitable Swapchain Object

```cpp
// dx11_renderer.cpp:129
desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
// dx11_renderer.cpp:146-147
swapchain->SetMaximumFrameLatency(1);
frame_latency_waitable = swapchain->GetFrameLatencyWaitableObject();
```

The waitable object is **created and stored** but **never waited on** in the render loop.

```cpp
// winui_app.cpp:258-302 (RenderLoop)
while (m_render_running.load(...)) {
    // ... resize check ...
    bool dirty = m_state->start_paint(...);
    if (!dirty) {
        Sleep(1);     // <-- fallback, not waitable object
        continue;
    }
    // build + upload + draw
}
```

`WaitForSingleObject(frame_latency_waitable, ...)` is never called anywhere.

### Sleep(1) Fallback vs Waitable Object: CPU Impact

| Approach | CPU Impact | Latency | Assessment |
|----------|-----------|---------|------------|
| `Sleep(1)` (current) | ~1ms timer resolution, ~1% CPU core idle | 0-15ms granularity (Windows timer) | Poor |
| `WaitForSingleObject` (ideal) | Near-zero CPU when idle | GPU-driven, sub-ms precision | Optimal |
| Busy spin (worst) | 100% CPU core | Lowest latency | Unacceptable |

**Current behavior:** When terminal is idle (no dirty rows), the render thread burns `Sleep(1)` in a tight loop — approximately 1000 wakeups/sec. On battery-powered devices this is measurable.

When the terminal IS dirty, `Present(1, 0)` with VSync blocks synchronously, providing implicit frame pacing. But the idle case is the problem.

### Dirty-Row Optimization

```cpp
// render_state.cpp:47
if (!_api.any_dirty()) return false;  // skip if no rows changed
```

Yes, unchanged frames are skipped via the `start_paint` dirty check. This is correct. However:

- The dirty check occurs **after** acquiring `vt_mutex` and calling `vt_bridge_update_render_state_no_reset` — the mutex lock + bridge call happen every iteration regardless of dirtiness.
- QuadBuilder (`quad_builder.cpp:50-78`) rebuilds **ALL** backgrounds every frame, not just dirty rows. The 2-pass rendering iterates `frame.rows_count * frame.cols` for every dirty frame, even if only 1 row changed.

### Resize Debounce

```cpp
// render_constants.h:19
constexpr uint32_t kResizeDebounceMs = 100;
```

| Value | Trade-off |
|-------|-----------|
| 50ms | More responsive, more resize thrash |
| **100ms (current)** | **Good balance, standard Windows debounce** |
| 200ms | Sluggish feel on fast drag-resize |

100ms is the same value used by Windows Terminal. This is appropriate.

**Deduction (-8):**
- **Critical (-5):** Frame latency waitable object is created but never used. This defeats the purpose of `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` and wastes CPU in the idle loop.
- **Warning (-2):** QuadBuilder does full-screen rebuild even when 1 row is dirty. Dirty-row information is available but not used to scope the build.
- **Minor (-1):** `vt_mutex` acquisition happens on every loop iteration (including idle cycles) — could be avoided with an event/signal.

---

## 4. Shader Complexity — 22/25

### Vertex Shader ALU Estimate

```hlsl
// shader_vs.hlsl — main()
PackedQuad q = g_instances[instanceId];     // 1 SRV fetch (32B structured)
// Unpack: 4x AND + 4x shift                   8 ALU
float2 corner = ...;                        // 2 conditional moves
float2 pixelPos = position + corner * size; // 1 MAD
output.pos = float4(pixelPos * scale + bias, 0, 1); // 1 MAD + 1 MOV
output.uv = (texcoord + corner * texsize) * atlasScale; // 1 MAD + 1 MUL
unpackColor(fg) + unpackColor(bg);          // 2x (4 AND + 4 shift + 4 mul) = 24 ALU
```

| VS Operation | Instruction Count (est.) |
|-------------|------------------------|
| Structured buffer fetch | 1 fetch |
| Bitwise unpack (pos/size/tex) | 8 ALU |
| Position transform | 4 ALU |
| UV computation | 3 ALU |
| Color unpack (2x) | 24 ALU |
| **Total** | **~39 ALU + 1 fetch** |

This is lightweight. For comparison, a typical VS with skeletal animation is 100+ ALU.

### Pixel Shader ALU Estimate

```hlsl
// shader_ps.hlsl — main() per shading type
```

**Background (type 0):** 0 ALU, 0 fetches — pass-through.

**ClearType text (type 1):**

| PS Operation | Instruction Count (est.) |
|-------------|------------------------|
| Texture sample | 1 fetch |
| EnhanceContrast3 | 6 ALU (3x mul, 3x div via rcp+mul) |
| LightOnDarkContrastAdjustment | 4 ALU (dot3 + mad + saturate) |
| ApplyAlphaCorrection3 | 12 ALU (3x polynomial: mul, sub, mad, mad, add) |
| Premultiply output | 6 ALU (2x float3 mul) |
| **Total** | **~28 ALU + 1 fetch** |

**Cursor/Underline (type 2, 3):** 2 ALU (swizzle + assign).

### Texture Fetch Count per Pixel

| Shading Type | Texture Fetches | Notes |
|-------------|----------------|-------|
| 0 (Background) | 0 | Solid color, no sampling |
| 1 (Text) | 1 | `glyphAtlas.Sample(pointSamp, uv)` |
| 2 (Cursor) | 0 | Solid color |
| 3 (Underline) | 0 | Solid color |
| **Weighted average** | **~0.3-0.5** | Most pixels are background |

Point sampling (`D3D11_FILTER_MIN_MAG_MIP_POINT`) is the cheapest texture filter — no bilinear interpolation overhead.

### Dual Source Output Overhead

```hlsl
struct PSOutput {
    float4 color   : SV_Target0;  // premultiplied foreground
    float4 weights : SV_Target1;  // per-channel blend weights
};
```

| Aspect | Impact |
|--------|--------|
| ROP throughput | Halved (2 outputs vs 1) |
| Register pressure | +4 float registers |
| Blend state cost | Equivalent to standard blend |
| Fill rate impact | 2x pixel export bandwidth |

Dual Source Blending halves the ROP (Render Output Unit) throughput on most GPUs. However, for a terminal with ~2 million pixels at most, this is negligible — even integrated GPUs can fill 100M+ pixels/sec.

### Gamma Correction Cost

The ClearType correction chain (`EnhanceContrast` + `AlphaCorrection`) adds ~16 ALU per text pixel. Analysis:

```
EnhanceContrast:     alpha * (k+1) / (alpha*k + 1)  →  3 ALU per channel × 3 = 9
AlphaCorrection:     polynomial a*(1-a)*((gx*f+gy)*a + (gz*f+gw))  →  7 ALU per channel × 3 = 21
LightOnDarkAdjust:   dot(color, weight) + saturate  →  4 ALU
Total gamma chain:   ~34 ALU
```

This is the most expensive part of the PS. However, this is identical to Windows Terminal's implementation (both derived from `lhecker/dwrite-hlsl`).

### Comparison Against Windows Terminal's Shader

| Aspect | GhostWin | Windows Terminal |
|--------|----------|-----------------|
| ClearType method | Dual Source Blending | Dual Source Blending |
| Gamma correction | DWrite_EnhanceContrast + AlphaCorrection | Identical (same source) |
| Branching | 4 sequential `if` statements | `switch` + function calls |
| Shading types | 4 (bg, text, cursor, underline) | 4+ (bg, text, cursor, selection, ...) |
| Atlas format | R8G8B8A8_UNORM | R8G8B8A8_UNORM |
| Point sampling | Yes | Yes |
| PS complexity (text) | ~28 ALU + 1 fetch | ~30 ALU + 1 fetch |

GhostWin's shader is slightly simpler (fewer shading types, no selection highlight). Performance parity with Windows Terminal.

**Deduction (-3):**
- **Warning (-2):** 4 sequential `if` statements in PS create divergent branches for all instances in the same wave. Using `switch(input.shadingType)` or factoring background quads into a separate draw call would improve wavefront occupancy.
- **Minor (-1):** `shading_type` is `uint32_t` (4 bytes) in the instance struct but only uses values 0-3. This could be packed into the `reserved` field's bits, saving 4 bytes per instance (28B total), but the 32B alignment is convenient.

---

## Issues Found

### Critical (Immediate Fix Required)

| File | Line | Issue | Recommended Action |
|------|------|-------|-------------------|
| `winui_app.cpp` | 258-302 | Frame latency waitable object created but never used — `Sleep(1)` busy-idle instead | Add `WaitForSingleObject(waitable, INFINITE)` before `Present()` or when idle |
| `glyph_atlas.cpp` | 552-554 | Atlas packing failure silently drops glyphs, no resize/rebuild | Implement atlas growth (recreate 2x texture, re-rasterize cached glyphs) |

### Warning (Improvement Recommended)

| File | Line | Issue | Recommended Action |
|------|------|-------|-------------------|
| `winui_app.cpp` | 291 | `Sleep(1)` in idle loop (~1000 wakeups/sec, battery drain) | Use `WaitForSingleObjectEx` or event-driven wake |
| `quad_builder.cpp` | 50-78 | Full-screen background rebuild even for 1 dirty row | Pass `dirty_rows` bitset, skip clean rows in Pass 1 & 2 |
| `shader_ps.hlsl` | 61-100 | Sequential `if` chain causes wave divergence | Use `switch` or split bg/text into separate draw calls |
| `dx11_renderer.cpp` | 615 | Buffer growth with `CreateBuffer` stalls GPU pipeline | Pre-allocate max buffer at init or use ring buffer |

### Info (Reference)

- Single draw call per frame — optimal GPU utilization
- 32B packed QuadInstance — 53% bandwidth reduction from Phase 3 (68B)
- ClearType gamma correction is identical to Windows Terminal (lhecker/dwrite-hlsl)
- 100ms resize debounce matches Windows Terminal
- Constant buffer at 48B is minimal
- 2-tier glyph cache (ASCII direct + hashmap) avoids hash overhead for common case
- Dual Source Blending ROP halving is irrelevant at terminal pixel counts

---

## Improvement Recommendations

### Priority 1: Use Frame Latency Waitable Object

The infrastructure is already in place (`frame_latency_waitable` handle exists). Add to `RenderLoop()`:

```cpp
// Before Present or when idle:
DWORD result = WaitForSingleObjectEx(
    renderer->frame_latency_waitable(), 100, TRUE);
```

Expected impact: **CPU idle usage drops from ~1% to ~0.01%**, proper GPU-driven frame pacing.

### Priority 2: Atlas Dynamic Growth

When `stbrp_pack_rects` fails:
1. Create new texture at `min(atlas_w * 2, kMaxAtlasSize)`
2. Copy old texture contents via `CopySubresourceRegion`
3. Re-initialize stb_rect_pack with new dimensions
4. Retry packing

Expected impact: Eliminates silent glyph loss for CJK/Nerd Font heavy usage.

### Priority 3: Dirty-Row Scoped QuadBuilder

Pass `dirty_rows` bitset to `QuadBuilder::build()`. Skip clean rows in both pass 1 and pass 2. This requires maintaining a persistent quad buffer (append/replace dirty row ranges instead of full rebuild).

Expected impact: For typical scrolling (1-2 dirty rows), **instance count drops from N*cols to 2*cols** — ~95% reduction.

### Priority 4: Shader Branch Optimization

Replace sequential `if` chain with `switch`:

```hlsl
switch (input.shadingType) {
    case 0: /* bg */     break;
    case 1: /* text */   break;
    case 2: /* cursor */ break;
    case 3: /* line */   break;
    default: /* error */ break;
}
```

Or split into 2 draw calls (backgrounds, then text+decorations) to eliminate per-pixel branching entirely — trades 1 extra draw call for 0 branch divergence.

Expected impact: Marginal for terminal workloads, but architecturally cleaner.
