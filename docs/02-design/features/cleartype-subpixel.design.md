# cleartype-subpixel Design

> **Feature**: ClearType 서브픽셀 안티앨리어싱
> **Project**: GhostWin Terminal
> **Phase**: 4-C (Master: winui3-integration FR-09)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/cleartype-subpixel.plan.md`

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3 GlyphAtlas가 ClearType 3x1 RGB 데이터를 그레이스케일로 변환하여 LCD 서브픽셀 해상도를 버림 (`glyph_atlas.cpp:372`) |
| **Solution** | RGB 데이터를 그대로 R8G8B8A8 텍스처에 저장하고, HLSL PS에서 채널별 독립 블렌딩으로 서브픽셀 렌더링 |
| **Function/UX** | LCD 모니터에서 글자 가장자리의 수평 해상도가 ~3배 향상되어 선명도 증가 |
| **Core Value** | 그레이스케일 → 서브픽셀 전환. 장시간 코딩 시 눈의 피로 감소 |

---

## 1. Current State Analysis

### 1.1 현재 렌더링 파이프라인 (Phase 3)

```
DirectWrite ClearType 3x1 RGB (3B/px)
    ↓ glyph_atlas.cpp:372 — 그레이스케일 변환 (R+G+B)/3
    ↓ R8_UNORM 텍스처 업로드 (1B/px)
    ↓ shader_ps.hlsl:18 — float alpha = glyphAtlas.Sample(...)
    ↓ premultiplied alpha 블렌딩 — float4(fg.rgb * alpha, alpha)
    ↓ 결과: 그레이스케일 AA
```

### 1.2 변경 대상 코드 위치

| File | Line | Current | Change |
|------|------|---------|--------|
| `glyph_atlas.cpp` | 176 | `DXGI_FORMAT_R8_UNORM` | → `DXGI_FORMAT_R8G8B8A8_UNORM` |
| `glyph_atlas.cpp` | 362-382 | RGB→grayscale 변환 | → RGB 직접 저장 |
| `glyph_atlas.cpp` | 385-392 | `UpdateSubresource` rowPitch=gw | → rowPitch=gw*4 |
| `shader_ps.hlsl` | 11 | `Texture2D<float>` | → `Texture2D<float4>` |
| `shader_ps.hlsl` | 18 | `float alpha` 단일 채널 | → 채널별 독립 블렌딩 |
| `render_constants.h` | — | — | + `kCleartypeEnabled` 상수 (선택) |

---

## 2. Detailed Design

### 2.1 아틀라스 텍스처 포맷 전환

**Before:**
```cpp
// glyph_atlas.cpp:176
desc.Format = DXGI_FORMAT_R8_UNORM;  // 1B/pixel grayscale
```

**After:**
```cpp
desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;  // 4B/pixel RGBA
```

**메모리 영향:**
| Atlas Size | R8 (Before) | R8G8B8A8 (After) | 증가율 |
|:---:|:---:|:---:|:---:|
| 1024×1024 | 1 MB | 4 MB | 4× |
| 2048×2048 | 4 MB | 16 MB | 4× |

4MB는 GPU VRAM에서 무시할 수 있는 수준이다 (일반 GPU 1GB+ 기준).

### 2.2 DirectWrite 래스터화 — RGB 직접 저장

**Before** (`glyph_atlas.cpp:362-382`):
```cpp
// ClearType: 3 bytes per pixel (RGB), average to grayscale
std::vector<uint8_t> rgb(gw * gh * 3);
analysis->CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds, rgb.data(), ...);
for (int i = 0; i < gw * gh; i++) {
    alpha[i] = (uint8_t)((rgb[i*3] + rgb[i*3+1] + rgb[i*3+2] + 1) / 3);  // ← 정보 손실
}
```

**After:**
```cpp
if (is_cleartype) {
    // ClearType: 3 bytes per pixel (RGB) → expand to RGBA
    std::vector<uint8_t> rgb(gw * gh * 3);
    hr = analysis->CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds,
                                       rgb.data(), (UINT32)(gw * gh * 3));
    if (FAILED(hr)) { ... return entry; }

    std::vector<uint8_t> rgba(gw * gh * 4);
    for (int i = 0; i < gw * gh; i++) {
        rgba[i * 4 + 0] = rgb[i * 3 + 0];  // R
        rgba[i * 4 + 1] = rgb[i * 3 + 1];  // G
        rgba[i * 4 + 2] = rgb[i * 3 + 2];  // B
        // A = max(R,G,B) for correct premultiplied alpha compositing
        rgba[i * 4 + 3] = std::max({rgb[i*3], rgb[i*3+1], rgb[i*3+2]});
    }
    // Upload RGBA data
    ctx->UpdateSubresource(atlas_tex.Get(), 0, &box,
                           rgba.data(), gw * 4, 0);
} else {
    // Aliased 1x1: single channel → replicate to RGBA
    std::vector<uint8_t> alpha_1x1(gw * gh);
    hr = analysis->CreateAlphaTexture(DWRITE_TEXTURE_ALIASED_1x1, &bounds,
                                       alpha_1x1.data(), (UINT32)(gw * gh));
    if (FAILED(hr)) { ... return entry; }

    std::vector<uint8_t> rgba(gw * gh * 4);
    for (int i = 0; i < gw * gh; i++) {
        rgba[i * 4 + 0] = alpha_1x1[i];  // R = G = B = A
        rgba[i * 4 + 1] = alpha_1x1[i];
        rgba[i * 4 + 2] = alpha_1x1[i];
        rgba[i * 4 + 3] = alpha_1x1[i];
    }
    ctx->UpdateSubresource(atlas_tex.Get(), 0, &box,
                           rgba.data(), gw * 4, 0);
}
```

**핵심 포인트:**
- `A = max(R,G,B)`: premultiplied alpha 합성에서 배경 블렌딩을 올바르게 하기 위해 필요
- aliased 1x1 폴백: R=G=B=A로 복제하면 기존 그레이스케일 동작과 동일

### 2.3 HLSL 픽셀 셰이더 — 서브픽셀 블렌딩

**Before** (`shader_ps.hlsl:11-19`):
```hlsl
Texture2D<float> glyphAtlas : register(t0);

// shadingType == 1 (text)
float alpha = glyphAtlas.Sample(pointSamp, input.uv);
return float4(input.fgColor.rgb * alpha, alpha);
```

**After:**
```hlsl
Texture2D<float4> glyphAtlas : register(t0);

// shadingType == 1 (text) — ClearType subpixel blending
float4 tex = glyphAtlas.Sample(pointSamp, input.uv);

// Per-channel alpha: tex.rgb contains subpixel coverage for R/G/B
float3 subpixel_alpha = tex.rgb;
float  max_alpha = tex.a;  // max(R,G,B) stored in atlas

// Subpixel blend: each color channel uses its own alpha
float3 blended_rgb = lerp(input.bgColor.rgb, input.fgColor.rgb, subpixel_alpha);

return float4(blended_rgb * max_alpha, max_alpha);
```

**블렌딩 수학 해설:**
1. `tex.rgb` = 서브픽셀별 커버리지 (R 서브픽셀, G 서브픽셀, B 서브픽셀)
2. `lerp(bg, fg, coverage)` = 각 채널별로 bg와 fg를 독립 보간
3. `* max_alpha` = premultiplied alpha 출력 유지 (기존 BlendState 호환)

### 2.4 블렌드 스테이트 — 변경 없음

현재 블렌드 스테이트 (`dx11_renderer.cpp:384-391`):
```cpp
SrcBlend  = D3D11_BLEND_ONE;
DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
```

이는 premultiplied alpha 표준 합성이다. PS에서 `blended_rgb * max_alpha`로 출력하면 기존 BlendState와 호환된다.

**단, 2-pass 렌더링(ADR-008) 고려:**
- Pass 1 (배경, shadingType=0): `return bgColor` — 변경 없음
- Pass 2 (텍스트, shadingType=1): 서브픽셀 블렌딩 — 이미 배경이 렌더타겟에 있으므로 PS에서 `bgColor`를 사용한 `lerp`가 올바름

> **중요**: 현재 PS가 `input.bgColor`(버텍스에서 전달된 배경색)로 lerp하므로, 렌더타겟의 실제 배경과 동일하다고 가정. 2-pass에서 Pass 1이 먼저 배경을 그리므로 이 가정은 성립한다. 투명도가 필요해지면 렌더타겟 샘플링으로 전환해야 하나, 현재 터미널에서는 불필요.

### 2.5 그레이스케일 폴백

ClearType가 비활성인 환경 (원격 데스크톱, 특정 접근성 설정):

```cpp
// glyph_atlas.cpp — init_dwrite() 에 추가
bool cleartype_enabled = true;

// SystemParametersInfo로 ClearType 상태 확인
BOOL ct = FALSE;
SystemParametersInfoW(SPI_GETCLEARTYPE, 0, &ct, 0);
cleartype_enabled = (ct != FALSE);
```

**폴백 동작:**
- `cleartype_enabled == false` → `DWRITE_TEXTURE_ALIASED_1x1` 사용 → R=G=B=A 복제
- 이 경우 PS의 `tex.r == tex.g == tex.b`이므로 `lerp` 결과가 단일 알파 블렌딩과 동일
- 즉, 폴백 시 기존 그레이스케일 렌더링과 **수학적으로 동일**한 결과

### 2.6 감마 보정 (선택적 개선)

서브픽셀 블렌딩에서 색번짐(color fringing)을 줄이기 위한 감마 보정:

```hlsl
// 감마 보정 적용 (선형 공간에서 블렌딩)
float3 fg_linear = pow(input.fgColor.rgb, 2.2);
float3 bg_linear = pow(input.bgColor.rgb, 2.2);
float3 blended_linear = lerp(bg_linear, fg_linear, subpixel_alpha);
float3 blended_rgb = pow(blended_linear, 1.0 / 2.2);
```

> **판단**: 감마 보정 없이도 동작하지만, 밝은 배경 + 어두운 텍스트에서 색번짐이 눈에 띌 수 있다. S3 구현 후 육안 확인하여 필요 시 S4에서 추가.

---

## 3. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | 아틀라스 포맷 R8 → R8G8B8A8 전환 | `glyph_atlas.cpp:176` | 텍스처 생성 성공, 빌드 통과 |
| S2 | RGB 직접 저장 (grayscale 변환 제거) + aliased 폴백 | `glyph_atlas.cpp:362-392` | ClearType RGB → RGBA 업로드 |
| S3 | PS 서브픽셀 블렌딩 구현 | `shader_ps.hlsl:11,17-19` | LCD에서 RGB 프린지 확인 |
| S4 | ClearType 비활성 감지 + 그레이스케일 폴백 | `glyph_atlas.cpp` (init_dwrite) | 원격 데스크톱 정상 |
| S5 | 감마 보정 (색번짐 확인 후 조건부) | `shader_ps.hlsl` | 밝은 배경 색번짐 없음 |

### 의존 관계

```
S1 → S2 → S3 (핵심 경로, 순차)
            ↘ S5 (S3 결과 확인 후 조건부)
S4 (S1 이후 언제든 가능, S2와 병행 가능)
```

---

## 4. Test Plan

### 4.1 단위 검증

| # | Test | Method | Expected |
|---|------|--------|----------|
| T1 | R8G8B8A8 텍스처 생성 | 기존 `dx11_render_test` | PASS (포맷 변경만) |
| T2 | ASCII 글리프 래스터화 | `render_state_test` 내 셀 렌더 | 기존과 동일 외형 |
| T3 | 한글 폴백 글리프 | Malgun Gothic 글리프 | 서브픽셀 적용 |

### 4.2 육안 검증

| # | Test | Method | Expected |
|---|------|--------|----------|
| V1 | LCD 서브픽셀 | 흰 배경 + 검정 텍스트 확대 스크린샷 | 글자 가장자리 RGB 프린지 |
| V2 | 어두운 배경 + 밝은 텍스트 | 터미널 기본 테마 | 색번짐 없음 (또는 S5로 보정) |
| V3 | ANSI 색상 조합 | Starship 프롬프트 | 색상 + 선명도 정상 |
| V4 | 원격 데스크톱 | RDP 연결 | 그레이스케일 폴백 동작 |

### 4.3 회귀 테스트

- 기존 23/23 PASS 유지
- 특히 `dx11_render_test` (셰이더 컴파일 + 렌더 + 아틀라스)

---

## 5. Rollback Strategy

S3까지 구현 후 문제 발생 시:
1. `shader_ps.hlsl`에서 `float4 tex` → `float alpha = tex.r` (= tex.g = tex.b)로 단일 채널 폴백
2. 또는 `glyph_atlas.cpp`에서 조건부 그레이스케일 변환 복원

R8G8B8A8 포맷은 유지해도 문제없다 (aliased 폴백이 R=G=B=A로 동작하므로).

---

## 6. QC Criteria

| # | Criteria | Target |
|---|----------|--------|
| QC-01 | LCD 서브픽셀 렌더링 동작 | 확대 시 RGB 프린지 확인 |
| QC-02 | 그레이스케일 폴백 동작 | ClearType 비활성 시 기존과 동일 |
| QC-03 | 2-pass 렌더링(ADR-008) 호환 | 배경→텍스트 순서 불변 |
| QC-04 | 기존 BlendState 호환 | premultiplied alpha 불변 |
| QC-05 | 기존 테스트 23/23 PASS | 회귀 없음 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design |
