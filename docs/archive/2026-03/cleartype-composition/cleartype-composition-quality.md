# ClearType Text Rendering Pipeline — Quantitative Quality Analysis

**Date:** 2026-03-30
**Analyzed files:**
- `src/renderer/glyph_atlas.cpp` (674 lines)
- `src/renderer/shader_ps.hlsl` (107 lines)
- `src/renderer/shader_vs.hlsl` (61 lines)
- `src/renderer/dx11_renderer.cpp` (678 lines)
- `src/renderer/quad_builder.cpp` (147 lines)

**Reference:** Windows Terminal AtlasEngine (`BackendD3D.cpp`, `shader_ps.hlsl`), lhecker/dwrite-hlsl

---

## A. ClearType Pipeline Correctness

### 1. Glyph Rasterization — 18/25

| Item | Status | Detail |
|------|--------|--------|
| `DWRITE_TEXTURE_CLEARTYPE_3x1` | PASS | Correctly used with fallback to `ALIASED_1x1` (lines 529-543) |
| RGB to RGBA conversion | PASS | RGB 3-byte expanded to RGBA 4-byte, alpha = max(R,G,B) (lines 568-574) |
| `DWRITE_RENDERING_MODE_NATURAL` | ACCEPTABLE | WT uses `NATURAL_SYMMETRIC` for better glyph symmetry; `NATURAL` is acceptable but suboptimal |
| Rendering params gamma | FAIL | System default `CreateRenderingParams()` used (line 238). WT creates **custom** params with gamma=1.0 via `CreateCustomRenderingParams()` to rasterize in linear space, then applies correction in shader. GhostWin rasterizes at system gamma (~1.8) AND applies shader correction = **double gamma correction** |
| ClearType availability check | PASS | `SPI_GETCLEARTYPE` check with graceful `ALIASED_1x1` fallback (lines 232-234) |

**Deductions:**
- -4: Not using `DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC` (minor quality difference)
- -3: Not using gamma=1.0 custom rendering params (double gamma correction bug)

### 2. Shader Blending — 14/25

| Item | Status | Detail |
|------|--------|--------|
| `DWrite_EnhanceContrast3` | PASS | Formula `a*(k+1)/(a*k+1)` matches lhecker/dwrite-hlsl exactly (lines 37-39) |
| `DWrite_ApplyAlphaCorrection3` | PASS | Polynomial `a + a*(1-a)*((g.x*f+g.y)*a + (g.z*f+g.w))` matches reference (lines 47-49) |
| `DWrite_ApplyLightOnDarkContrastAdjustment` | PASS | `k * saturate(dot(color, float3(0.30, 0.59, 0.11) * -4.0) + 3.0)` matches reference (lines 53-55) |
| Dual source output (SV_Target0 + SV_Target1) | PASS | Correct formula: `weights = alphaCorrected * fg.a`, `color = alphaCorrected * fg.rgb * fg.a` (lines 83-84) |
| Premultiplication | PASS | Both color and weights correctly premultiplied by `input.fgColor.a` |
| **CB binding to PS** | **CRITICAL FAIL** | `PSSetConstantBuffers` **never called** in `dx11_renderer.cpp`. The PS declares `cbuffer ConstBuffer : register(b0)` with `enhancedContrast` and `gammaRatios`, but these are only bound via `VSSetConstantBuffers` (line 482). The PS reads **zeroed/garbage values**, making all gamma correction and contrast enhancement **completely non-functional** |
| PS shader model | WARN | Compiled as `ps_4_0` (line 336). Dual source blending with `SV_Target1` technically works in SM4.0 on D3D11 hardware, but SM5.0 is recommended for explicit dual-source semantics and is what WT uses |

**Deductions:**
- -8: CB not bound to PS stage = gamma correction completely broken at runtime
- -3: PS compiled as SM4.0 instead of SM5.0

### 3. Blend State — 24/25

| Item | Status | Detail |
|------|--------|--------|
| `D3D11_BLEND_ONE` (SrcBlend) | PASS | Color output multiplied by 1 (line 388) |
| `D3D11_BLEND_INV_SRC1_COLOR` (DestBlend) | PASS | Destination modulated by per-channel inverse weights (line 389) |
| `D3D11_BLEND_OP_ADD` (BlendOp) | PASS | Additive blend = `Src*1 + Dest*(1-weights)` (line 390) |
| Alpha blend: `D3D11_BLEND_INV_SRC1_ALPHA` | PASS | Alpha channel also dual-sourced (line 392) |
| `RenderTargetWriteMask` | PASS | `D3D11_COLOR_WRITE_ENABLE_ALL` (line 394) |
| Swapchain format | PASS | `DXGI_FORMAT_B8G8R8A8_UNORM` — correct sRGB encoded target for ClearType (lines 124, 169) |

**Deductions:**
- -1: No separate blend state for background pass (backgrounds use shadingType=0 which outputs weights=1.0, so functionally correct but a wasted dual-source evaluation per background quad)

### 4. Gamma Correction — 11/25

| Item | Status | Detail |
|------|--------|--------|
| Lookup table from dwrite-hlsl | PASS | 13-entry table (gamma 1.0-2.2) matches lhecker reference exactly (lines 106-120) |
| norm13 constant | PASS | `0x10000 / (255.0*255.0) * 4.0` = correct (lines 122-123) |
| norm24 constant | PASS | `0x100 / 255.0 * 4.0` = correct (lines 124-125) |
| `enhancedContrast` from system params | PASS | `params->GetEnhancedContrast()` (line 241) |
| Gamma from system params | PARTIAL | Uses system `GetGamma()` but should use gamma=1.0 for rasterization (see item 1 above) |
| CB layout 16-byte alignment | PASS | C++ struct is 48 bytes with padding, HLSL layout matches: `float2(8) + float2(8) + float(4) + float(4) + float4(16) = 40`, padded to 48 with `_pad1[2]` |
| **CB data reaches PS** | **CRITICAL FAIL** | Same root cause as Section 2: `PSSetConstantBuffers` never called. All gamma correction math in the PS evaluates with `enhancedContrast=0` and `gammaRatios={0,0,0,0}`. This means: `DWrite_ApplyLightOnDarkContrastAdjustment` returns 0, `DWrite_EnhanceContrast3` becomes identity (`a*1/(a*0+1) = a`), `DWrite_ApplyAlphaCorrection3` becomes identity (all ratios are 0). Net effect: **raw ClearType coverage passes straight through** — no gamma correction, no contrast enhancement |

**Deductions:**
- -11: Gamma correction completely non-functional due to missing CB binding (same root cause)
- -3: System gamma used for rasterization instead of linear gamma=1.0

---

## B. Known Gaps vs Windows Terminal

| # | Gap | Severity | Description |
|---|-----|----------|-------------|
| 1 | **PSSetConstantBuffers missing** | CRITICAL | CB bound to VS only, not PS. `enhancedContrast` and `gammaRatios` are always zero in the pixel shader. All ClearType quality functions (`EnhanceContrast`, `AlphaCorrection`, `LightOnDarkAdjustment`) are effectively no-ops. **Fix: add `context->PSSetConstantBuffers(0, 1, constant_buffer.GetAddressOf());` after line 487 in `draw_instances()`.** |
| 2 | **Double gamma correction** | HIGH | WT rasterizes glyphs with `CreateCustomRenderingParams(gamma=1.0)` to get linear coverage values, then applies gamma correction in the shader. GhostWin uses `CreateRenderingParams()` (system gamma ~1.8), so DirectWrite already bakes gamma into the coverage, and the shader would apply it again (once the CB bug is fixed). **Fix: use `IDWriteFactory::CreateCustomRenderingParams(1.0f, enhancedContrast, clearTypeLevel, ...)` for rasterization.** |
| 3 | **NATURAL vs NATURAL_SYMMETRIC** | MEDIUM | WT uses `DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC` which gives better horizontal symmetry for ClearType glyphs. GhostWin uses `NATURAL` which allows only natural-width fitting. |
| 4 | **PS shader model 4.0** | LOW | WT compiles PS as SM5.0. While dual-source blending works on SM4.0 with D3D11 FL11_0, SM5.0 provides explicit guarantees. Risk: some older drivers may not correctly handle dual-source output in SM4.0. |
| 5 | **No grayscale AA mode** | LOW | WT supports both ClearType subpixel and grayscale AA modes (user configurable). GhostWin only falls back to grayscale when ClearType is system-disabled. |
| 6 | **Background pass dual-source overhead** | LOW | Background quads (shadingType=0) output `weights=float4(1,1,1,1)`, effectively `dest*(1-1)+bg*1 = bg`. Correct but wastes the dual-source evaluation. WT uses a separate pass or blend state. |
| 7 | **No atlas resize** | MEDIUM | `stb_rect_pack` atlas is fixed at init size. If it fills up, glyphs are silently dropped. WT dynamically resizes the atlas. |
| 8 | **shader_common.hlsl not used** | TRIVIAL | `shader_common.hlsl` defines `PSInput` but both VS and PS re-declare it inline. No functional impact but maintenance risk. |

---

## C. Overall Score

| Category | Score | Max | Notes |
|----------|-------|-----|-------|
| Glyph Rasterization | 18 | 25 | Correct core, wrong rendering mode + gamma |
| Shader Blending | 14 | 25 | Formulas correct but CB never reaches PS |
| Blend State | 24 | 25 | Nearly perfect dual-source setup |
| Gamma Correction | 11 | 25 | Tables correct but entirely non-functional |
| **Total** | **67** | **100** | |

### Interpretation

**67/100 — Structurally correct, runtime broken.**

The ClearType pipeline is architecturally sound: the dual-source blend state, the shader formulas, the gamma lookup table, and the atlas conversion are all correctly implemented per the lhecker/dwrite-hlsl reference. However, a single missing `PSSetConstantBuffers` call renders the entire gamma correction and contrast enhancement pipeline **non-functional at runtime**. Glyphs appear with raw DirectWrite ClearType coverage — no contrast enhancement, no gamma correction, no light-on-dark adjustment.

### Priority Fixes

1. **[CRITICAL] Add `PSSetConstantBuffers` to `draw_instances()`** — 1 line fix, instantly restores gamma correction
2. **[HIGH] Use gamma=1.0 custom rendering params** — prevents double gamma correction once fix #1 is applied
3. **[MEDIUM] Switch to `DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC`** — improves glyph symmetry
4. **[LOW] Upgrade PS to SM5.0** — explicit dual-source guarantee

With fixes #1 and #2 applied, estimated score would rise to **88-92/100**.

---

## Appendix: Fix #1 Code (1-line)

```cpp
// dx11_renderer.cpp, draw_instances(), after line 487:
context->PSSetConstantBuffers(0, 1, constant_buffer.GetAddressOf());
```

## Appendix: Fix #2 Code

```cpp
// glyph_atlas.cpp, init_dwrite(), replace CreateRenderingParams:
ComPtr<IDWriteRenderingParams> default_params;
hr = dwrite_factory->CreateRenderingParams(&default_params);
if (SUCCEEDED(hr)) {
    dwrite_enhanced_contrast = default_params->GetEnhancedContrast();
    dwrite_gamma = default_params->GetGamma();
    compute_gamma_ratios();

    // Create gamma=1.0 params for linear rasterization (WT pattern)
    ComPtr<IDWriteRenderingParams> linear_params;
    hr = dwrite_factory->CreateCustomRenderingParams(
        1.0f,                                    // gamma (linear)
        dwrite_enhanced_contrast,                // enhanced contrast (from system)
        default_params->GetClearTypeLevel(),      // ClearType level
        default_params->GetPixelGeometry(),       // pixel geometry
        DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,  // rendering mode (fix #3)
        &linear_params);
    // Use linear_params in CreateGlyphRunAnalysis
}
```
