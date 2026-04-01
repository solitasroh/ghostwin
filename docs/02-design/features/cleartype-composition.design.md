# cleartype-composition Design

> **Feature**: ClearType 서브픽셀 렌더링 — WT 동등 텍스트 선명도
> **Plan**: `docs/01-plan/features/cleartype-composition.plan.md` (v3.1)
> **Date**: 2026-04-01
> **Author**: Solit

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | GhostWin 74/100 (블라인드), PREMULTIPLIED → ClearType 불가 |
| **Solution** | CompositionSurfaceHandle + ALPHA_MODE_IGNORE + SetSwapChainHandle (v2) + Dual Source |
| **Function/UX** | ClearType per-channel로 선명도 대폭 향상 |
| **Core Value** | PoC 성공 시 WT 수준 텍스트 품질. 실패 시 대안 경로 |

---

## 0. PoC Design (최우선)

### 0.1 스왑체인 생성 변경

**파일**: `src/renderer/dx11_renderer.cpp` — `create_swapchain_composition()`

```cpp
// Impl 멤버 추가
HANDLE composition_surface_handle = nullptr;

// 변경: CreateSwapChainForComposition → CompositionSurfaceHandle 경로
HANDLE surface_handle = nullptr;
HRESULT hr = DCompositionCreateSurfaceHandle(
    COMPOSITIONOBJECT_ALL_ACCESS, nullptr, &surface_handle);

ComPtr<IDXGIFactoryMedia> factory_media;
factory.As(&factory_media);

desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;  // ClearType 핵심

ComPtr<IDXGISwapChain1> sc1;
factory_media->CreateSwapChainForCompositionSurfaceHandle(
    device.Get(), surface_handle, &desc, nullptr, &sc1);

composition_surface_handle = surface_handle;
```

**폴백**: 각 단계 실패 시 기존 `CreateSwapChainForComposition(PREMULTIPLIED)` 경로로 자동 전환.

**필요 include**: `#include <dcomp.h>` (link: `dcomp.lib` — CMakeLists.txt에 이미 존재)

### 0.2 SwapChainPanel 연결 (v2 API)

**파일**: `src/app/winui_app.cpp` — `InitializeD3D11()`

```cpp
// 변경: SetSwapChain → SetSwapChainHandle
HANDLE handle = m_renderer->composition_surface_handle();
if (handle) {
    auto panelNative2 = panel.as<ISwapChainPanelNative2>();
    panelNative2->SetSwapChainHandle(handle);
} else {
    // v1 폴백 (기존 코드)
    auto panelNative = panel.as<ISwapChainPanelNative>();
    ComPtr<IDXGISwapChain> sc;
    m_renderer->composition_swapchain()->QueryInterface(IID_PPV_ARGS(&sc));
    panelNative->SetSwapChain(sc.Get());
}
```

### 0.3 공개 접근자

**파일**: `src/renderer/dx11_renderer.h`

```cpp
[[nodiscard]] HANDLE composition_surface_handle() const;
```

### 0.4 HANDLE 정리

**파일**: `src/renderer/dx11_renderer.cpp` — 소멸자

```cpp
if (impl_->composition_surface_handle) {
    CloseHandle(impl_->composition_surface_handle);
}
```

### 0.5 PoC 성공 기준

| # | 체크 | 로그/육안 |
|---|------|----------|
| 1 | DCompositionCreateSurfaceHandle 성공 | HANDLE ≠ nullptr |
| 2 | IDXGIFactoryMedia QI 성공 | 로그 |
| 3 | CreateSwapChainForCompositionSurfaceHandle 성공 | `Composition swapchain (IGNORE)` |
| 4 | ISwapChainPanelNative2 QI 성공 | `SetSwapChainHandle (v2)` |
| 5 | **화면에 clear color 표시** | 검은 화면 아님 |
| 6 | 기존 터미널 렌더링 동작 | cmd.exe 출력 표시 |

---

## 1. 본 구현 (PoC 성공 후)

### 1.1 ClearType 3x1 글리프 래스터

**파일**: `src/renderer/glyph_atlas.cpp` — `rasterize_glyph()`

```cpp
// 현재 (line 604~610): ALIASED_1x1 Grayscale
hr = factory2->CreateGlyphRunAnalysis(
    &glyph_run, &dpi_transform,
    compat_mode, DWRITE_MEASURING_MODE_NATURAL,
    recommended_grid_fit_mode,
    DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,  // ← 여기를 변경
    0.0f, 0.0f, &analysis);

// 변경: CLEARTYPE + 3x1
DWRITE_TEXT_ANTIALIAS_MODE aa_mode = use_cleartype
    ? DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE
    : DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE;

hr = factory2->CreateGlyphRunAnalysis(
    &glyph_run, &dpi_transform,
    compat_mode, DWRITE_MEASURING_MODE_NATURAL,
    recommended_grid_fit_mode,
    aa_mode,
    0.0f, 0.0f, &analysis);
```

**텍스처 변환** (현재 line 651~668):

```cpp
// ClearType 3x1: 3바이트/픽셀 → R8G8B8A8 4바이트
if (use_cleartype) {
    DWRITE_TEXTURE_TYPE tex_type = DWRITE_TEXTURE_CLEARTYPE_3x1;
    hr = analysis->GetAlphaTextureBounds(tex_type, &bounds);
    // ...
    std::vector<uint8_t> ct_3x1(gw * gh * 3);
    hr = analysis->CreateAlphaTexture(tex_type, &bounds, ct_3x1.data(), (UINT32)(gw * gh * 3));
    std::vector<uint8_t> rgba(gw * gh * 4);
    for (int i = 0; i < gw * gh; i++) {
        uint8_t r = ct_3x1[i*3 + 0];
        uint8_t g = ct_3x1[i*3 + 1];
        uint8_t b = ct_3x1[i*3 + 2];
        rgba[i*4 + 0] = r;
        rgba[i*4 + 1] = g;
        rgba[i*4 + 2] = b;
        rgba[i*4 + 3] = (r > g) ? ((r > b) ? r : b) : ((g > b) ? g : b);  // max(R,G,B)
    }
} else {
    // 기존 ALIASED_1x1 Grayscale 경로 (현재 코드 유지)
}
```

**아틀라스 텍스처**: 이미 `DXGI_FORMAT_R8G8B8A8_UNORM` — 변경 불필요.

### 1.2 Dual Source Blend State

**파일**: `src/renderer/dx11_renderer.cpp` — `create_pipeline()`

```cpp
// line 385-389 변경
blend_desc.RenderTarget[0].SrcBlend       = D3D11_BLEND_ONE;
blend_desc.RenderTarget[0].DestBlend      = D3D11_BLEND_INV_SRC1_COLOR;  // WAS: INV_SRC_ALPHA
blend_desc.RenderTarget[0].BlendOp        = D3D11_BLEND_OP_ADD;
blend_desc.RenderTarget[0].SrcBlendAlpha  = D3D11_BLEND_ONE;
blend_desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC1_ALPHA;  // WAS: INV_SRC_ALPHA
```

### 1.3 PS 셰이더 모델 업그레이드

**파일**: `src/renderer/dx11_renderer.cpp` — `create_pipeline()`

```cpp
// line 335 변경
auto ps_blob = compile_shader(ps_src.data(), ps_src.size(), "main", "ps_5_0", ps_path);
//                                                                   ^^^^^^ WAS: ps_4_0
```

### 1.4 Dual Source 셰이더

**파일**: `src/renderer/shader_ps.hlsl` — 전면 재작성

PS 출력 구조체: `SV_Target0` (color) + `SV_Target1` (weights)

| shadingType | color (SV_Target0) | weights (SV_Target1) | blend 결과 |
|:-----------:|-------------------|---------------------|-----------|
| 0 (bg) | `(bg.rgb, 1)` | `(1,1,1,1)` | `bg` (불투명) |
| 1 (text) | `corrected_rgb * fg.rgb * fg.a` | `corrected_rgb * fg.a` | per-channel ClearType |
| 2,3 (cursor) | `(fg.rgb * a, a)` | `(a,a,a,a)` | 단일 alpha |

**float3 감마 함수 추가 필요**:
- `DWrite_EnhanceContrast3(float3 alpha, float3 k)` — per-channel
- `DWrite_ApplyAlphaCorrection3(float3 a, float f, float4 g)` — per-channel
- `DWrite_ApplyLightOnDarkContrastAdjustment3(float k, float3 color)` → `float3`
- 소스: lhecker/dwrite-hlsl (MIT)

### 1.5 렌더 순서 확인

`QuadBuilder::build()` (quad_builder.cpp):
- Pass 1: 모든 행 배경 quad (shadingType=0) 먼저 배치
- Pass 2: 모든 행 텍스트 quad (shadingType=1,2,3) 나중 배치

단일 `DrawIndexedInstanced`에서 인스턴스는 버퍼 순서대로 처리됨 → **배경→텍스트 순서 보장됨.**

---

## 2. Implementation Order

```
[PoC Phase]
PoC-1  dcomp.h include + HANDLE 멤버          dx11_renderer.cpp
PoC-2  DCompositionCreateSurfaceHandle         dx11_renderer.cpp
PoC-3  IDXGIFactoryMedia + CreateSwapChain     dx11_renderer.cpp
PoC-4  composition_surface_handle() 접근자     dx11_renderer.h
PoC-5  SetSwapChainHandle (v2) + v1 폴백       winui_app.cpp
PoC-6  HANDLE CloseHandle 정리                 dx11_renderer.cpp
PoC-7  빌드 + 실행 → 화면 표시 확인

===== PoC GATE =====

[본 구현 Phase]
S1  Blend state: INV_SRC_ALPHA → INV_SRC1_COLOR   dx11_renderer.cpp
S2  PS: ps_4_0 → ps_5_0                           dx11_renderer.cpp
S3  shader_ps.hlsl: Dual Source PSOutput + float3  shader_ps.hlsl
S4  glyph_atlas: CLEARTYPE_3x1 + A=max(R,G,B)     glyph_atlas.cpp
S5  Grayscale 폴백 조건 분기                        glyph_atlas.cpp
S6  빌드 + 테스트 (10/10 PASS)
S7  육안 확인 + 블라인드 재평가
```

---

## 3. Risks

| # | Risk | Mitigation |
|---|------|------------|
| R1 | PoC 실패 (검은 화면) | 폴백 자동 전환, 대안 검토 |
| R2 | SM 5.0 컴파일 실패 (FL 10_0) | Grayscale 폴백 |
| R3 | 렌더 순서 보장 | QuadBuilder 2-pass 확인됨 |
| R4 | CJK + ClearType 상호작용 | ADR-012는 AA 무관 |
| R5 | DPI 변환 행렬 + ClearType | DPI 행렬은 ClearType에서도 동작 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-31 | Solit | Initial design (pre-factcheck) |
| 2.0 | 2026-04-01 | Solit | PoC-first, 4-agent 팩트체크 반영, FACT/ASSUMPTION 분류 |
