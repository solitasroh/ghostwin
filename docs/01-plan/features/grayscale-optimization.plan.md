# grayscale-optimization Plan

> **Feature**: DirectWrite Factory3 Grayscale AA 최적화
> **Project**: GhostWin Terminal
> **Phase**: 4 품질 개선 (ClearType Phase A)
> **Date**: 2026-04-01
> **Author**: Solit
> **Research**: `docs/00-research/research-cleartype-90-percent.md`

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | IDWriteFactory (v1) 사용으로 `grayscaleEnhancedContrast` 독립 제어 불가, Grayscale 전용 대비 최적화 미적용. 83/100에서 정체 |
| **Solution** | IDWriteFactory3 업그레이드 + `grayscaleEnhancedContrast` 독립 설정 + `GetRecommendedRenderingMode` + `GRID_FIT_MODE_ENABLED` 실험 |
| **Function/UX** | WT Grayscale 모드(85-88)와 동등한 텍스트 선명도. 특히 stem 정렬과 대비 강화로 가독성 향상 |
| **Core Value** | ClearType 없이도 업계 최고 Grayscale 품질 달성. Phase B (ClearType) 전 즉시 적용 가능한 개선 |

---

## 1. Background

### 1.1 현재 상태

| 항목 | 현재 값 | 문제 |
|------|--------|------|
| DWrite 헤더 | `dwrite_2.h` | Factory3 API 미접근 |
| Factory | `IDWriteFactory` (v1 cast) | `grayscaleEnhancedContrast` 파라미터 없음 |
| RenderingParams | `IDWriteFactory::CreateCustomRenderingParams` | 5-param 버전 (Grayscale 전용 contrast 미분리) |
| Contrast | `GetEnhancedContrast() + 0.25f` | ClearType용 contrast를 Grayscale에 그대로 사용 |
| Rendering Mode | `DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC` (하드코딩) | 폰트별 최적 모드 무시 |
| Grid Fit | `DWRITE_GRID_FIT_MODE_DEFAULT` | gasp 테이블 없는 폰트에서 grid fitting 비활성화 가능 |

### 1.2 개선 근거

| 근거 | 출처 |
|------|------|
| IDWriteFactory3에 `grayscaleEnhancedContrast` 독립 파라미터 존재 | Microsoft Learn, SDK dwrite_3.h |
| Chromium이 contrast=1.0으로 네이티브 Windows 앱 동등 품질 달성 | Chrome 132 (2025-01), Edge Blog |
| `GetRecommendedRenderingMode`가 폰트/크기별 최적 모드 반환 | IDWriteFontFace3 API |
| `GRID_FIT_MODE_ENABLED`가 stem 픽셀 정렬으로 선명도 향상 | Microsoft Learn |

---

## 2. Goal

ClearType 전환 없이 Grayscale AA 품질을 83/100 → 88~90/100으로 향상.

**핵심 목표:**
1. `IDWriteFactory3::CreateCustomRenderingParams`로 `grayscaleEnhancedContrast` 독립 설정
2. `IDWriteFontFace3::GetRecommendedRenderingMode`로 폰트별 최적 모드 자동 선택
3. `GRID_FIT_MODE_ENABLED` 적용으로 stem 정렬 선명도 향상
4. Contrast 값 최적화 (Chromium 1.0 참고)

---

## 3. Functional Requirements (FR)

### FR-01: IDWriteFactory3 업그레이드
- `#include <dwrite_2.h>` → `#include <dwrite_3.h>` 전환
- `IDWriteFactory3` QI (QueryInterface)로 Factory3 접근
- 기존 `IDWriteFactory2` 사용 코드 유지 (폴백 체인, CreateGlyphRunAnalysis)

### FR-02: grayscaleEnhancedContrast 독립 설정
- `IDWriteFactory3::CreateCustomRenderingParams` (7-param 버전) 사용
- `grayscaleEnhancedContrast`를 ClearType `enhancedContrast`와 분리
- 시스템 `IDWriteRenderingParams1::GetGrayscaleEnhancedContrast()` 값 조회 + 보정
- 셰이더에 전달하는 `enhancedContrast`는 Grayscale 전용 값 사용

### FR-03: GetRecommendedRenderingMode 활용
- `IDWriteFontFace` → `IDWriteFontFace3` QI
- `GetRecommendedRenderingMode(emSize, dpiX, dpiY, transform, ...)` 호출
- 반환된 `DWRITE_RENDERING_MODE1` + `DWRITE_GRID_FIT_MODE`를 `CreateGlyphRunAnalysis`에 전달
- 폴백: QI 실패 시 기존 `NATURAL_SYMMETRIC` + `DEFAULT` 유지

### FR-04: GRID_FIT_MODE_ENABLED 적용
- `GetRecommendedRenderingMode`가 반환하는 gridFitMode 사용
- 반환값이 `DEFAULT`이면 `ENABLED`로 오버라이드 (선명도 우선)
- 사용자 설정으로 제어 가능하도록 AtlasConfig에 옵션 추가 고려 (Phase 5)

### FR-05: Contrast 값 최적화
- 시스템 `GetGrayscaleEnhancedContrast()` 값 + 보정값
- Chromium 참고: contrast 1.0이 네이티브 수준
- 현재 `+0.25f` 보정을 `grayscaleEnhancedContrast` 전용으로 재조정
- 로그에 최종 contrast 값 출력하여 튜닝 가능

---

## 4. Non-Functional Requirements (NFR)

| # | Requirement | Target |
|---|-------------|--------|
| NFR-01 | 기존 렌더링 회귀 없음 | Factory3 QI 실패 시 기존 경로 유지 |
| NFR-02 | 빌드 호환성 | SDK 22621 (기존) — dwrite_3.h 포함됨 |
| NFR-03 | 성능 영향 없음 | GetRecommendedRenderingMode는 atlas 초기화 시 1회 호출 |
| NFR-04 | 기존 테스트 유지 | 10/10 PASS 보존 |

---

## 5. Implementation Steps

| # | Task | File | DoD |
|---|------|------|-----|
| S1 | `dwrite_2.h` → `dwrite_3.h` 헤더 전환 | glyph_atlas.cpp | 컴파일 성공 |
| S2 | `IDWriteFactory3` QI + `CreateCustomRenderingParams` (7-param) | glyph_atlas.cpp | `grayscaleEnhancedContrast` 값 로그 출력 |
| S3 | `IDWriteRenderingParams1::GetGrayscaleEnhancedContrast()` 조회 | glyph_atlas.cpp | 시스템 Grayscale contrast 값 획득 |
| S4 | 셰이더 `enhancedContrast`에 Grayscale 전용 값 전달 | glyph_atlas.cpp | 기존 ClearType contrast와 분리 |
| S5 | `IDWriteFontFace3` QI + `GetRecommendedRenderingMode` | glyph_atlas.cpp | 반환된 모드 로그 출력 |
| S6 | `CreateGlyphRunAnalysis`에 추천 모드 적용 | glyph_atlas.cpp | DOWNSAMPLED/ENABLED 등 자동 선택 |
| S7 | GridFitMode DEFAULT → ENABLED 오버라이드 | glyph_atlas.cpp | stem 정렬 활성화 |
| S8 | 빌드 + 테스트 검증 | — | 0 errors, 10/10 PASS |

**변경 파일**: `src/renderer/glyph_atlas.cpp` (1개 파일)

---

## 6. Definition of Done (DoD)

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | IDWriteFactory3 QI 성공 | 로그: `Factory3 available` |
| 2 | grayscaleEnhancedContrast 값 로그 출력 | 로그: `Grayscale contrast: X.XX` |
| 3 | GetRecommendedRenderingMode 결과 로그 | 로그: `Recommended mode: X, gridFit: Y` |
| 4 | 기존 렌더링 회귀 없음 | 100% DPI 스크린샷 비교 |
| 5 | 선명도 향상 육안 확인 | 이전/이후 스크린샷 비교 |
| 6 | 빌드 + 테스트 | 0 errors, 10/10 PASS |

---

## 7. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | IDWriteFactory3 QI 실패 | 낮음 | SDK 22621에 dwrite_3.h 포함 확인됨. 실패 시 기존 경로 유지 |
| R2 | grayscaleEnhancedContrast 값이 기존보다 나쁠 수 있음 | 중간 | A/B 비교로 최적값 도출, 기존 값 폴백 |
| R3 | GRID_FIT_MODE_ENABLED가 CJK에서 부작용 | 중간 | CJK advance-centering(ADR-012) 재검증 |
| R4 | GetRecommendedRenderingMode가 기대와 다른 모드 반환 | 낮음 | 로그로 확인 후 필요 시 오버라이드 |

---

## 8. References

| Document | Path |
|----------|------|
| ClearType 90%+ 리서치 | `docs/00-research/research-cleartype-90-percent.md` |
| ADR-010 Grayscale AA | `docs/adr/010-grayscale-aa-composition.md` |
| IDWriteFactory3 API | learn.microsoft.com/windows/win32/api/dwrite_3/ |
| Chromium contrast 개선 | Chrome 132, Edge Blog (2025-01) |
| lhecker/dwrite-hlsl | github.com/lhecker/dwrite-hlsl |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-04-01 | Solit | Initial plan — 10-agent research 기반 |
