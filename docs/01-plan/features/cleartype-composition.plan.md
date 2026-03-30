# cleartype-composition Plan

> **Feature**: ClearType 선명도 — WT 동등 품질 달성
> **Project**: GhostWin Terminal
> **Phase**: 4-F (독립 — winui3-shell 이후)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: winui3-shell (A) 완료
> **Revision**: 2.0 (WT 아키텍처 리서치 기반 전면 개정)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Composition swapchain에서 ClearType 무력화 → WT/Alacritty 대비 텍스트가 흐릿 (평가 70/100) |
| **Solution** | WT 동일 아키텍처: CompositionSurfaceHandle + ALPHA_MODE_IGNORE + D2D DrawGlyphRun 아틀라스 + Dual Source Blending |
| **Function/UX** | WT와 동등한 ClearType 서브픽셀 렌더링. 100% 네이티브 품질 |
| **Core Value** | 터미널 핵심 UX의 근본적 해결. 타협 없는 텍스트 품질 |

---

## 1. Background

### 1.1 이전 시도와 한계 (Iter 1~4)

| Iter | 변경 | 결과 | 한계 |
|------|------|------|------|
| 1 | ALPHA_MODE_IGNORE | 렌더링 깨짐 | SwapChainPanel 비호환 |
| 2 | gamma=1.0 래스터 | 67/100 | 이중 감마 제거 |
| 3 | sRGB lerp | 69/100 | pow 감마 제거 |
| 4 | NATURAL_SYMMETRIC + contrast boost | 70/100 | **근본 한계 도달** |

**근본 한계**: 셰이더 내부 `lerp(bgColor, fgColor, coverage)`는 QuadInstance의 정적 배경색으로 블렌딩. 렌더 타겟의 **실제 배경 픽셀**이 아님. 하드웨어 Dual Source Blending만이 진짜 배경과 per-channel 블렌딩 가능.

### 1.2 WT 아키텍처 분석 (확인된 사실)

**스왑체인** (`AtlasEngine.r.cpp:322-370`):
- `DCompositionCreateSurfaceHandle` → `CreateSwapChainForCompositionSurfaceHandle`
- 불투명 배경: `DXGI_ALPHA_MODE_IGNORE` (ClearType + independent flip)
- 투명 배경: `DXGI_ALPHA_MODE_PREMULTIPLIED` (Grayscale AA 강제 전환)

**아틀라스 래스터라이즈** (`BackendD3D.cpp:842-863, 1527-1543`):
- Atlas 텍스처 = `D3D11_TEXTURE2D (B8G8R8A8_UNORM, SRV|RT)`
- D2D1 RenderTarget을 아틀라스 위에 생성
- `DrawGlyphRun(흰색 브러시)` → D2D가 ClearType 감마/서브픽셀을 완벽 처리
- RGB 채널 = 서브픽셀 weights

**렌더링** (`BackendD3D.cpp:139-177`, `shader_ps.hlsl`):
- Dual Source Blending: `SRC=ONE, DEST=INV_SRC1_COLOR`
- PS 출력: `SV_Target0(color) = weights * fg_color`, `SV_Target1(weights) = alphaCorrected`
- GPU 하드웨어가 실제 배경과 per-channel 블렌딩

**감마 보정** (`shader_ps.hlsl`, `dwrite.hlsl`):
- `DWrite_EnhanceContrast3` + `DWrite_ApplyAlphaCorrection3`
- 감마 비율: `DWrite_GetGammaRatios` 13-entry 룩업 테이블

---

## 2. Functional Requirements

### FR-01: CompositionSurfaceHandle 스왑체인
- `DCompositionCreateSurfaceHandle` + `CreateSwapChainForCompositionSurfaceHandle`
- `DXGI_ALPHA_MODE_IGNORE` (불투명 배경)
- `ISwapChainPanelNative2::SetSwapChainHandle`로 WinUI3 연결

### FR-02: D2D DrawGlyphRun 아틀라스
- Atlas 텍스처에 D2D1 RenderTarget 바인딩
- `DrawGlyphRun(흰색 브러시, ClearType AA)` → RGB = 서브픽셀 weights
- `CreateGlyphRunAnalysis` 코드 제거 (D2D가 대체)
- 캐시 히트 시 D2D 미호출 (성능 영향 없음)

### FR-03: Dual Source Blending
- 블렌드 스테이트: `SRC=ONE, DEST=INV_SRC1_COLOR`
- 셰이더 이중 출력: `SV_Target0(color) + SV_Target1(weights)`
- 하드웨어가 렌더 타겟의 실제 배경과 per-channel 블렌딩

### FR-04: DWrite 감마 보정 셰이더
- `DWrite_EnhanceContrast3` + `DWrite_ApplyAlphaCorrection3`
- `DWrite_GetGammaRatios` 13-entry 룩업 테이블 (구현 완료)
- `DWrite_ApplyLightOnDarkContrastAdjustment` (구현 완료)

---

## 3. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | `CompositionSurfaceHandle` 스왑체인 생성 경로 | 스왑체인 생성 + SwapChainPanel 연결 |
| S2 | Atlas 텍스처에 D2D1 RT 바인딩 + `DrawGlyphRun` 래스터라이즈 | 글리프가 ClearType RGB weights로 아틀라스에 기록 |
| S3 | `CreateGlyphRunAnalysis` → D2D 경로 전환 | 기존 래스터라이즈 코드 교체 |
| S4 | Dual Source Blending 블렌드 스테이트 | `INV_SRC1_COLOR` 블렌드 |
| S5 | 셰이더 이중 출력 (SV_Target0 + SV_Target1) | ClearType 감마 보정 + 이중 출력 |
| S6 | 스크린샷 비교 검증 (WT vs GhostWin) | 동등 선명도 |

---

## 4. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | ClearType RGB 서브픽셀 프린지 확대 스크린샷에서 확인 | 색번짐 없는 깨끗한 RGB 프린지 |
| 2 | WT 동일 폰트/크기 비교 스크린샷 | 동등 선명도 (평가 85+/100) |
| 3 | 수동 감마 보정 불필요 | D2D가 래스터라이즈 처리 |
| 4 | Dual Source Blending 동작 | 실제 배경과 per-channel 블렌딩 |
| 5 | 기존 테스트 PASS | 7/7 PASS |
| 6 | Phase 3 빌드 유지 | ghostwin_terminal 영향 없음 |

---

## 5. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | `ISwapChainPanelNative2` WinUI3 호환성 | 상 | WT가 동일 API 사용. 실패 시 HWND child window 폴백 |
| R2 | D2D + DX11 디바이스 공유 복잡도 | 중 | `ID3D11Device` → `IDXGIDevice` → `ID2D1Device` 표준 경로 |
| R3 | Atlas RT + SRV 동시 바인딩 | 중 | D2D EndDraw 후 SRV 바인딩 (렌더/래스터 분리) |
| R4 | `DCompositionCreateSurfaceHandle` 가용성 | 하 | Win10 1803+ 지원. 실패 시 기존 `CreateSwapChainForComposition` 폴백 |

---

## 6. References

| Source | Location |
|--------|----------|
| WT AtlasEngine.r.cpp | `github.com/microsoft/terminal/src/renderer/atlas/AtlasEngine.r.cpp:322-370` |
| WT BackendD3D.cpp | `github.com/microsoft/terminal/src/renderer/atlas/BackendD3D.cpp:842-863, 1527-1543` |
| WT shader_ps.hlsl | `github.com/microsoft/terminal/src/renderer/atlas/shader_ps.hlsl` |
| lhecker/dwrite-hlsl | `github.com/lhecker/dwrite-hlsl` |
| WT ClearType issue #5098 | ClearType + Acrylic 양립 불가 |
| WinUI3 ClearType #7115 | NOT_PLANNED |
| Phase 4-C ClearType 보고서 | `docs/archive/2026-03/cleartype-subpixel/` |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial plan |
| 1.1 | 2026-03-30 | Solit | WT/Alacritty 리서치 반영 |
| 2.0 | 2026-03-30 | Solit | 전면 개정 — CompositionSurfaceHandle + D2D Atlas + Dual Source (WT 동일 아키텍처) |
