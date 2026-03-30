# cleartype-composition Design

> **Feature**: ClearType 선명도 — WT 동등 품질
> **Project**: GhostWin Terminal
> **Phase**: 4-F
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/cleartype-composition.plan.md` (v2.0)
> **Revision**: 2.0 (WT 아키텍처 리서치 기반)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Composition swapchain PREMULTIPLIED에서 ClearType 무력화 (70/100) |
| **Solution** | CompositionSurfaceHandle + ALPHA_MODE_IGNORE + D2D Atlas + Dual Source |
| **Function/UX** | WT 동등 ClearType. 네이티브 서브픽셀 품질 |
| **Core Value** | 타협 없는 텍스트 품질 |

---

## 1. SwapChain: CompositionSurfaceHandle

### 1.1 현재 → 변경

```cpp
// 현재 (PREMULTIPLIED — ClearType 불가)
factory->CreateSwapChainForComposition(device, &desc, nullptr, &sc1);
panel.as<ISwapChainPanelNative>()->SetSwapChain(sc.Get());

// 변경 (IGNORE — ClearType 가능 + independent flip)
HANDLE surfaceHandle = nullptr;
DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS, nullptr, &surfaceHandle);

desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
ComPtr<IDXGIFactoryMedia> factoryMedia;
factory.As(&factoryMedia);
factoryMedia->CreateSwapChainForCompositionSurfaceHandle(
    device, surfaceHandle, &desc, nullptr, &sc1);

panel.as<ISwapChainPanelNative2>()->SetSwapChainHandle(surfaceHandle);
```

### 1.2 링크 라이브러리

- `dcomp.lib` — `DCompositionCreateSurfaceHandle`
- `dxgi.lib` — `IDXGIFactoryMedia` (기존)

---

## 2. Atlas: D2D DrawGlyphRun

### 2.1 D2D 디바이스 생성 (DX11 디바이스 공유)

```cpp
// glyph_atlas.cpp 초기화
ComPtr<IDXGIDevice> dxgiDevice;
d3d_device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));

ComPtr<ID2D1Factory1> d2d_factory;
D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
                  IID_PPV_ARGS(&d2d_factory));

ComPtr<ID2D1Device> d2d_device;
d2d_factory->CreateDevice(dxgiDevice.Get(), &d2d_device);

ComPtr<ID2D1DeviceContext> d2d_ctx;
d2d_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2d_ctx);
```

### 2.2 Atlas 텍스처에 D2D RT 바인딩

```cpp
// Atlas 텍스처 생성 (SRV + RT 플래그)
D3D11_TEXTURE2D_DESC tex_desc = {};
tex_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
tex_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
// ...기존 atlas 크기 설정

// D2D RT를 atlas 위에 생성
ComPtr<IDXGISurface> atlas_surface;
atlas_texture->QueryInterface(IID_PPV_ARGS(&atlas_surface));

ComPtr<ID2D1Bitmap1> d2d_atlas;
D2D1_BITMAP_PROPERTIES1 bmp_props = {};
bmp_props.pixelFormat = { DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED };
bmp_props.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
d2d_ctx->CreateBitmapFromDxgiSurface(atlas_surface.Get(), &bmp_props, &d2d_atlas);

d2d_ctx->SetTarget(d2d_atlas.Get());
d2d_ctx->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
```

### 2.3 글리프 래스터라이즈 (DrawGlyphRun)

```cpp
GlyphEntry GlyphAtlas::rasterize_glyph(uint32_t codepoint, uint8_t style) {
    // ... glyph_run 준비 (기존 코드)

    // D2D로 래스터라이즈 (ClearType — D2D가 감마/서브픽셀 완벽 처리)
    d2d_ctx->BeginDraw();
    d2d_ctx->DrawGlyphRun(
        D2D1::Point2F(offset_x, offset_y),
        &glyph_run,
        white_brush.Get(),     // 흰색 브러시 → RGB = 서브픽셀 weights
        DWRITE_MEASURING_MODE_NATURAL);
    d2d_ctx->EndDraw();

    // SRV로 사용 가능 (EndDraw 후 GPU 동기화 완료)
}
```

**핵심**: `CreateGlyphRunAnalysis` + 수동 RGB→RGBA 변환 코드가 전부 불필요. D2D가 ClearType를 완벽 처리.

---

## 3. Shader: Dual Source ClearType

### 3.1 블렌드 스테이트

```cpp
blend_desc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
blend_desc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC1_COLOR;
blend_desc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
blend_desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC1_ALPHA;
```

### 3.2 Pixel Shader

```hlsl
struct PSOutput {
    float4 color   : SV_Target0;
    float4 weights : SV_Target1;
};

PSOutput main(PSInput input) {
    PSOutput out;

    if (input.shadingType == 0) {
        // 배경: 완전 덮어쓰기
        out.color   = float4(input.bgColor.rgb, 1.0);
        out.weights = float4(1, 1, 1, 1);
        return out;
    }

    if (input.shadingType == 1) {
        // ClearType 텍스트
        float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);
        float k = DWrite_ApplyLightOnDarkContrastAdjustment(
            enhancedContrast, input.fgColor.rgb);
        float3 contrasted = DWrite_EnhanceContrast3(glyph.rgb, k);
        float3 alphaCorrected = DWrite_ApplyAlphaCorrection3(
            contrasted, input.fgColor.rgb, gammaRatios);

        out.weights = float4(alphaCorrected * input.fgColor.a, 1.0);
        out.color   = float4(alphaCorrected * input.fgColor.rgb * input.fgColor.a, 1.0);
        return out;
    }

    // 커서/기타
    out.color   = input.fgColor;
    out.weights = input.fgColor.aaaa;
    return out;
}
```

---

## 4. 파일 변경 목록

| File | Change |
|------|--------|
| `dx11_renderer.h` | `create_for_composition_handle()` 팩토리 추가 |
| `dx11_renderer.cpp` | CompositionSurfaceHandle 스왑체인 + Dual Source blend |
| `glyph_atlas.h` | D2D 멤버 추가 |
| `glyph_atlas.cpp` | D2D 초기화 + DrawGlyphRun 래스터라이즈 (CreateGlyphRunAnalysis 제거) |
| `shader_ps.hlsl` | PSOutput 이중 출력 (Dual Source) |
| `winui_app.cpp` | SwapChainPanelNative2::SetSwapChainHandle |
| `CMakeLists.txt` | `dcomp.lib` 링크 추가 |

---

## 5. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | CompositionSurfaceHandle 스왑체인 | `dx11_renderer.cpp`, `winui_app.cpp`, `CMakeLists.txt` | 스왑체인 생성 + Panel 연결 + 렌더 정상 |
| S2 | Dual Source Blending + 셰이더 이중 출력 | `dx11_renderer.cpp`, `shader_ps.hlsl` | blend state + PSOutput 이중 출력 |
| S3 | D2D 디바이스 + Atlas RT | `glyph_atlas.cpp` | D2D ctx + atlas에 RT 바인딩 |
| S4 | DrawGlyphRun 래스터라이즈 | `glyph_atlas.cpp` | D2D ClearType 글리프 → atlas RGB |
| S5 | CreateGlyphRunAnalysis 제거 + 정리 | `glyph_atlas.cpp` | 수동 감마 코드 제거 |
| S6 | 스크린샷 비교 검증 | — | WT 동등 선명도 (85+/100) |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial — ALPHA_MODE_IGNORE + 셰이더 lerp |
| 2.0 | 2026-03-30 | Solit | WT 아키텍처 — SurfaceHandle + D2D Atlas + Dual Source |
