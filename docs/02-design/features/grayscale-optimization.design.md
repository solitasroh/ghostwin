# grayscale-optimization Design

> **Feature**: DirectWrite Factory3 Grayscale AA 최적화
> **Plan**: `docs/01-plan/features/grayscale-optimization.plan.md`
> **Date**: 2026-04-01
> **Author**: Solit

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | IDWriteFactory (v1)로 `grayscaleEnhancedContrast` 독립 제어 불가, 83/100 정체 |
| **Solution** | Factory3 + grayscaleEnhancedContrast + GetRecommendedRenderingMode + GRID_FIT_MODE_ENABLED |
| **Function/UX** | stem 정렬 + Grayscale 전용 대비 강화로 WT Grayscale(85-88) 수준 도달 |
| **Core Value** | 1개 파일 ~30줄 변경, ClearType 없이 최대 Grayscale 품질 |

---

## 1. Design Decisions

### DD-01: Factory3 QI + 폴백 전략

`IDWriteFactory3` QI 실패 시 기존 경로 유지. dwrite_3.h는 SDK 22621에 포함되어 있으므로 컴파일 실패 리스크 없음.

### DD-02: grayscaleEnhancedContrast 분리

현재 `GetEnhancedContrast() + 0.25f`는 ClearType용 contrast를 Grayscale에 전용. Factory3의 7-param `CreateCustomRenderingParams`로 Grayscale 전용 contrast를 독립 설정.

### DD-03: GetRecommendedRenderingMode 결과 활용

폰트별 최적 `DWRITE_RENDERING_MODE1` + `DWRITE_GRID_FIT_MODE`를 자동 선택. 단, Grayscale AA 모드는 유지 (ClearType 모드 반환 시 무시).

---

## 2. Detailed Design

### 2.1 헤더 변경 (line 8)

```cpp
// BEFORE
#include <dwrite_2.h>

// AFTER
#include <dwrite_3.h>
```

`dwrite_3.h`는 `dwrite_2.h`를 포함하므로 기존 IDWriteFactory2 코드 영향 없음.

### 2.2 Impl 멤버 추가

```cpp
// 추가 멤버 (line ~102 근처)
DWRITE_RENDERING_MODE1 recommended_rendering_mode = DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC;
DWRITE_GRID_FIT_MODE   recommended_grid_fit_mode = DWRITE_GRID_FIT_MODE_DEFAULT;
```

### 2.3 init_dwrite() 변경 (line 239~260)

```cpp
// === 현재 코드 ===
ComPtr<IDWriteRenderingParams> params;
hr = dwrite_factory->CreateRenderingParams(&params);
if (SUCCEEDED(hr)) {
    dwrite_gamma = params->GetGamma();
    dwrite_enhanced_contrast = params->GetEnhancedContrast() + 0.25f;
    compute_gamma_ratios();
    hr = dwrite_factory->CreateCustomRenderingParams(
        1.0f, 0.0f, 0.0f,
        params->GetPixelGeometry(),
        params->GetRenderingMode(),
        &linear_params);
}

// === 변경 후 ===
ComPtr<IDWriteRenderingParams> params;
hr = dwrite_factory->CreateRenderingParams(&params);
if (SUCCEEDED(hr)) {
    dwrite_gamma = params->GetGamma();

    // Grayscale 전용 contrast: Factory3가 있으면 독립 값 사용
    ComPtr<IDWriteRenderingParams1> params1;
    if (SUCCEEDED(params.As(&params1))) {
        dwrite_enhanced_contrast = params1->GetGrayscaleEnhancedContrast();
    } else {
        dwrite_enhanced_contrast = params->GetEnhancedContrast() + 0.25f;
    }
    compute_gamma_ratios();

    // Factory3: 7-param CreateCustomRenderingParams
    ComPtr<IDWriteFactory3> factory3;
    if (SUCCEEDED(dwrite_factory.As(&factory3))) {
        ComPtr<IDWriteRenderingParams3> linear3;
        hr = factory3->CreateCustomRenderingParams(
            1.0f,                              // gamma = 1.0 (linear)
            0.0f,                              // enhancedContrast (ClearType용, 미사용)
            dwrite_enhanced_contrast,          // grayscaleEnhancedContrast (핵심!)
            0.0f,                              // clearTypeLevel
            params->GetPixelGeometry(),
            DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
            DWRITE_GRID_FIT_MODE_ENABLED,      // stem 정렬 강제
            &linear3);
        if (SUCCEEDED(hr)) {
            linear_params = linear3;
            LOG_I("atlas", "Factory3 RenderingParams: gsContrast=%.2f, gridFit=ENABLED",
                  dwrite_enhanced_contrast);
        }
    }

    // Factory3 실패 시 기존 Factory1 경로 폴백
    if (!linear_params) {
        hr = dwrite_factory->CreateCustomRenderingParams(
            1.0f, 0.0f, 0.0f,
            params->GetPixelGeometry(),
            params->GetRenderingMode(),
            &linear_params);
    }
}
```

### 2.4 GetRecommendedRenderingMode 활용

`init_dwrite()`에서 `compute_cell_metrics()` 이후, font_face에서 추천 모드를 조회:

```cpp
// compute_cell_metrics() 호출 후 추가
ComPtr<IDWriteFontFace3> face3;
if (SUCCEEDED(font_face.As(&face3))) {
    DWRITE_RENDERING_MODE1 recMode;
    DWRITE_GRID_FIT_MODE recGrid;
    hr = face3->GetRecommendedRenderingMode(
        dip_size,                    // fontEmSize
        96.0f * dpi_scale,           // dpiX
        96.0f * dpi_scale,           // dpiY
        nullptr,                     // transform
        FALSE,                       // isSideways
        DWRITE_OUTLINE_THRESHOLD_ANTIALIASED,
        DWRITE_MEASURING_MODE_NATURAL,
        linear_params.Get(),         // renderingParams
        &recMode,
        &recGrid);
    if (SUCCEEDED(hr)) {
        recommended_rendering_mode = recMode;
        recommended_grid_fit_mode = (recGrid == DWRITE_GRID_FIT_MODE_DEFAULT)
            ? DWRITE_GRID_FIT_MODE_ENABLED : recGrid;
        LOG_I("atlas", "Recommended mode: %d, gridFit: %d", recMode, recommended_grid_fit_mode);
    }
}
```

### 2.5 rasterize_glyph() 변경 (line 544~558)

추천 모드를 `CreateGlyphRunAnalysis`에 전달:

```cpp
// BEFORE (하드코딩)
hr = factory2->CreateGlyphRunAnalysis(
    &glyph_run, &dpi_transform,
    DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,    // 하드코딩
    DWRITE_MEASURING_MODE_NATURAL,
    DWRITE_GRID_FIT_MODE_DEFAULT,               // 하드코딩
    DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
    0.0f, 0.0f, &analysis);

// AFTER (추천 모드 사용)
// DWRITE_RENDERING_MODE1 → DWRITE_RENDERING_MODE 변환
DWRITE_RENDERING_MODE compat_mode = static_cast<DWRITE_RENDERING_MODE>(
    recommended_rendering_mode);
hr = factory2->CreateGlyphRunAnalysis(
    &glyph_run, &dpi_transform,
    compat_mode,
    DWRITE_MEASURING_MODE_NATURAL,
    recommended_grid_fit_mode,
    DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
    0.0f, 0.0f, &analysis);
```

**주의**: `DWRITE_RENDERING_MODE1` 값은 `DWRITE_RENDERING_MODE`와 0~6 범위에서 동일 (SDK 확인). `NATURAL_SYMMETRIC_DOWNSAMPLED` (7)만 v2 API에 없으므로 `NATURAL_SYMMETRIC`으로 폴백.

```cpp
if (recommended_rendering_mode == DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC_DOWNSAMPLED) {
    compat_mode = DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC;
}
```

---

## 3. Data Flow

```
init_dwrite()
├── CreateRenderingParams() → GetGamma, GetGrayscaleEnhancedContrast (params1)
├── Factory3::CreateCustomRenderingParams(7-param)
│   └── grayscaleEnhancedContrast 독립 설정 + GRID_FIT_MODE_ENABLED
├── compute_cell_metrics()
└── FontFace3::GetRecommendedRenderingMode()
    └── recommended_rendering_mode, recommended_grid_fit_mode 저장

rasterize_glyph()
└── Factory2::CreateGlyphRunAnalysis(recommended_mode, recommended_gridFit, GRAYSCALE)
    └── DPI 변환 행렬 + 추천 모드로 글리프 래스터라이즈
```

---

## 4. Implementation Order

```
S1  dwrite_2.h → dwrite_3.h
 ↓
S2  Impl 멤버 추가 (recommended_rendering_mode, recommended_grid_fit_mode)
 ↓
S3  init_dwrite: RenderingParams1 QI + GetGrayscaleEnhancedContrast
 ↓
S4  init_dwrite: Factory3 QI + CreateCustomRenderingParams (7-param)
 ↓
S5  init_dwrite: FontFace3 QI + GetRecommendedRenderingMode
 ↓
S6  rasterize_glyph: recommended mode 적용 + DOWNSAMPLED 폴백
 ↓
S7  빌드 + 테스트
```

**변경 파일**: `src/renderer/glyph_atlas.cpp` (1개)

---

## 5. Test Plan

| # | Test | Method | Pass Criteria |
|---|------|--------|--------------|
| T1 | 컴파일 성공 | build_ghostwin.ps1 | 0 errors |
| T2 | 기존 테스트 | 전체 실행 | 10/10 PASS |
| T3 | Factory3 QI | 로그 확인 | `Factory3 RenderingParams: gsContrast=X.XX` |
| T4 | GetRecommendedRenderingMode | 로그 확인 | `Recommended mode: N, gridFit: M` |
| T5 | 렌더링 회귀 없음 | 100% DPI 실행 | 기존과 동일하거나 더 선명 |

---

## 6. Risks

| # | Risk | Mitigation |
|---|------|------------|
| R1 | Factory3 QI 실패 | 기존 Factory1 경로 폴백 (코드에 명시적 분기) |
| R2 | GetGrayscaleEnhancedContrast가 0 반환 | 0이면 기존 `GetEnhancedContrast() + 0.25f` 사용 |
| R3 | DOWNSAMPLED 모드가 v2 API에 없음 | `NATURAL_SYMMETRIC`으로 폴백 (코드에 명시적 처리) |
| R4 | GRID_FIT_MODE_ENABLED가 CJK에서 부작용 | 한글 렌더링 검증 후 필요 시 DEFAULT로 복귀 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-04-01 | Solit | Initial design |
