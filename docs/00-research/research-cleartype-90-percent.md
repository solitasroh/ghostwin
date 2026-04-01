# ClearType 90%+ 텍스트 선명도 달성 리서치

> **Date**: 2026-04-01
> **Method**: 10개 병렬 리서치 에이전트, WT/Alacritty/Ghostty/WezTerm/Chrome 소스 분석
> **Goal**: ADR-010 Grayscale AA 83/100 → 90%+ 달성 방안 도출

---

## 1. 업계 텍스트 품질 비교 (사실 기반)

| 터미널 | 래스터라이저 | AA 모드 | GPU | 추정 점수 | 비고 |
|--------|------------|---------|-----|:---------:|------|
| WezTerm | FreeType | Grayscale | ANGLE(DX11) | 62-68 | 감마 보정 미적용 보고 |
| Alacritty | DirectWrite | Grayscale (서브픽셀 불완전) | OpenGL | 68-72 | dual-source 구현 불완전 |
| Ghostty | CoreText/FreeType | Grayscale 전용 | Metal/OpenGL | 75-80 | 의도적 서브픽셀 미지원 |
| **GhostWin** | DirectWrite | Grayscale | DX11 | **83** | ADR-010, dwrite-hlsl 감마 |
| WT (Grayscale) | DirectWrite | Grayscale | DX11 | 85-88 | 투명 배경 시 |
| WT (ClearType) | DirectWrite | ClearType 3x1 | DX11 | 90-95 | 불투명 배경 시 |

**GhostWin은 WT를 제외한 모든 터미널보다 이미 높은 품질.**

---

## 2. 핵심 발견 사항

### 2.1 ADR-010 실패의 진짜 원인

ADR-010에서 "검은 화면"으로 실패한 5개 대안 중, **CompositionSurfaceHandle 경로는 테스트하지 않았다.**

| ADR-010 시도 | 실패 원인 | 재검토 결과 |
|-------------|----------|-----------|
| ALPHA_MODE_IGNORE (SwapChainPanel) | SwapChainPanel 미지원 | `SetSwapChain` (v1) API 사용이 원인 |
| CompositionSurfaceHandle + IGNORE | 검은 화면 | **`SetSwapChainHandle` (v2) API 미사용** |
| HWND Child Window | XAML 레이어가 위에 그려짐 | 예상대로 airspace 문제 |
| Dual Source Blending (PREMULTIPLIED) | 검은 화면 | PREMULTIPLIED에서 원리적 불가 |
| 셰이더 내부 ClearType lerp | 70/100 | PSSetConstantBuffers 누락 + 이중 감마가 원인 |

**핵심**: WT PR #10023에서 도입된 `ISwapChainPanelNative2::SetSwapChainHandle` (v2)이 정답이었으나, ADR-010 시점에 이 API를 사용하지 않았다.

### 2.2 WT의 실제 ClearType 구현 (확인됨)

```
DCompositionCreateSurfaceHandle()
→ IDXGIFactoryMedia::CreateSwapChainForCompositionSurfaceHandle(ALPHA_MODE_IGNORE)
→ ISwapChainPanelNative2::SetSwapChainHandle()
→ ClearType 3x1 래스터라이즈 + Dual Source Blending
```

- **SwapChainPanel 유지** — XAML 아키텍처 변경 불필요
- **불투명 배경 한정** — WT도 투명 배경 시 Grayscale 강제 전환
- WT 코드 주석: "independent flips로 display latency 대폭 감소"

### 2.3 PREMULTIPLIED에서 ClearType가 수학적으로 불가능한 이유

ClearType: 3개 독립 alpha (R, G, B 채널별 커버리지)
Premultiplied: 1개 공유 alpha → 3→1 축소 시 서브픽셀 정보 소실

**이것은 우회 불가능한 수학적 사실.**
Chrome, Edge, WT 모두 이 제약을 인정하고 불투명 영역에서만 ClearType 사용.

### 2.4 이전 셰이더 ClearType 70/100의 진짜 원인

| 문제 | 영향 | 현재 상태 |
|------|------|----------|
| PSSetConstantBuffers 누락 | 감마 보정 완전 무작동 | **수정됨** (dx11_renderer.cpp:484) |
| 이중 감마 보정 | DWrite 시스템 감마 + 셰이더 감마 | gamma=1.0 linear params **수정됨** |
| NATURAL vs NATURAL_SYMMETRIC | 글리프 대칭성 저하 | **이미 NATURAL_SYMMETRIC 사용** |

→ 수정된 상태에서 ClearType 셰이더를 재적용하면 88-92/100 가능 (추정)

### 2.5 Grayscale 추가 개선 경로

현재 `IDWriteFactory` (v1) → `IDWriteFactory3` 업그레이드 시:
- `grayscaleEnhancedContrast` 독립 파라미터 (Grayscale 전용 대비)
- `DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC_DOWNSAMPLED` (작은 폰트 대비 유지)
- `IDWriteFontFace3::GetRecommendedRenderingMode` (폰트별 최적 모드)
- `DWRITE_GRID_FIT_MODE_ENABLED` (stem 픽셀 정렬)

---

## 3. 확정된 2단계 전략

### Phase A: Grayscale 최적화 (83→88~90)

**목표**: ClearType 없이 WT Grayscale 수준 도달
**변경 범위**: `glyph_atlas.cpp` (DWrite Factory3 업그레이드)
**아키텍처 변경**: 없음

| # | 작업 | 파일 | 효과 |
|---|------|------|------|
| A1 | `dwrite_2.h` → `dwrite_3.h` 전환 | glyph_atlas.cpp | Factory3 API 접근 |
| A2 | `IDWriteFactory3::CreateCustomRenderingParams` | glyph_atlas.cpp | grayscaleEnhancedContrast 독립 설정 |
| A3 | `IDWriteFontFace3::GetRecommendedRenderingMode` | glyph_atlas.cpp | 폰트별 최적 모드 자동 선택 |
| A4 | `DWRITE_GRID_FIT_MODE_ENABLED` 실험 | glyph_atlas.cpp | stem 정렬 선명도 |
| A5 | Contrast 값 최적화 (Chromium 1.0 참고) | glyph_atlas.cpp | 대비 강화 |

### Phase B: ClearType 전환 (88→95+)

**목표**: WT ClearType 동등 품질
**변경 범위**: `dx11_renderer.cpp` (스왑체인 생성), `shader_ps.hlsl` (Dual Source), `glyph_atlas.cpp` (3x1 래스터)
**아키텍처 변경**: 스왑체인 생성 API만 변경 (SwapChainPanel 유지)

| # | 작업 | 파일 | 효과 |
|---|------|------|------|
| B1 | `DCompositionCreateSurfaceHandle` | dx11_renderer.cpp | Composition surface handle 생성 |
| B2 | `CreateSwapChainForCompositionSurfaceHandle(IGNORE)` | dx11_renderer.cpp | 불투명 스왑체인 |
| B3 | `ISwapChainPanelNative2::SetSwapChainHandle` | winui_app.cpp | SwapChainPanel 연결 (v2 API) |
| B4 | ClearType 3x1 글리프 래스터라이즈 | glyph_atlas.cpp | per-channel RGB 커버리지 |
| B5 | Dual Source Blending 셰이더 | shader_ps.hlsl | SV_Target0 + SV_Target1 |
| B6 | Blend state: ONE / INV_SRC1_COLOR | dx11_renderer.cpp | per-channel 블렌딩 |
| B7 | Grayscale 폴백 유지 | shader_ps.hlsl | 투명 배경 요청 시 |

### Trade-off

| 항목 | Phase A 후 | Phase B 후 |
|------|:---------:|:---------:|
| 텍스트 품질 | 88~90 | **95+** |
| 투명 배경 | 가능 | **불가** (WT도 동일) |
| SwapChainPanel | 유지 | 유지 |
| XAML UI | 유지 | 유지 |
| 코드 변경량 | ~30줄 | ~150줄 |

---

## 4. 참조 소스

| 소스 | URL/경로 |
|------|---------|
| WT AtlasEngine.r.cpp | github.com/microsoft/terminal (swapchain 생성) |
| WT AtlasEngine.api.cpp | github.com/microsoft/terminal (ClearType/Grayscale 전환) |
| WT BackendD3D.cpp | github.com/microsoft/terminal (Dual Source blend state) |
| WT shader_ps.hlsl | github.com/microsoft/terminal (ClearType HLSL) |
| lhecker/dwrite-hlsl | github.com/lhecker/dwrite-hlsl (MIT, 감마 보정 참조) |
| WT PR #10023 | CompositionSurfaceHandle 도입 |
| WT PR #12242 | ClearType blending 구현 |
| WT Issue #15957 | ClearType + 투명 배경 충돌 공식 확인 |
| D2D Alpha Modes | learn.microsoft.com (ClearType IGNORE 필수 근거) |
| Chromium contrast 개선 | Chrome 132 (2025-01, contrast=1.0) |
| IDWriteFactory3 | learn.microsoft.com (grayscaleEnhancedContrast) |

---

*10 agents, ~3000s total research time*
