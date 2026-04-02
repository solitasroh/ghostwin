# ClearType Sharpness v2 Design Document

> **Summary**: WT 동등 ClearType 선명도 달성을 위한 3단계 구현 설계
>
> **Project**: GhostWin Terminal
> **Author**: Solit
> **Date**: 2026-04-03
> **Status**: Draft
> **Planning Doc**: [cleartype-sharpness-v2.plan.md](../../01-plan/features/cleartype-sharpness-v2.plan.md)
> **10-Agent Consensus**: 전원 합의 기반 설계

---

## 1. Overview

### 1.1 Design Goals

1. WT와 동등한 ClearType 텍스트 선명도 달성
2. 10명 에이전트 교차검증으로 확인된 3가지 정확한 차이 해소
3. 한 번에 하나씩 변경하여 각 효과를 독립 검증

### 1.2 근본 원인 (Plan 2.0에서 확정)

GhostWin이 **WT 경로도 Alacritty 경로도 아닌 불완전한 파이프라인**을 사용 중:
- D2D linearParams로 **linear coverage** 생성 (WT처럼)
- 하지만 셰이더에서 **감마 보정 안 함** (Alacritty처럼)
- 경로 A(linear+감마)도 B(system+raw)도 아님 → **보정 누락 → blur**

이전 "감마=소프트" 결론은 **이중 감마** (CreateAlphaTexture gamma=1.8 baked + 셰이더 감마) 때문이었으며, 현재 D2D linearParams(gamma=1.0)에서는 이중 감마가 발생하지 않음.

### 1.3 Design Principles

- **WT 코드 정확 복제**: 추측 금지, WT 소스에서 확인된 코드만 사용
- **작업일지 참조 필수**: 이미 시도한 것 반복 금지 (17+ 실패 교훈)
- **사실/추측 구분**: 검증 안 된 주장에 "추측" 명시

---

## 2. Architecture

### 2.1 선명한 경로 비교 + 현재 GhostWin 위치

```
경로 A (WT — 선명):
D2D DrawGlyphRun(gamma=1.0) → linear coverage → EnhanceContrast → AlphaCorrection → Dual Source
                                                  ↑ 셰이더 감마 보정 = 올바른 단일 보정

경로 B (Alacritty — 선명):
CreateAlphaTexture(gamma=1.8 baked) → corrected coverage → raw → Dual Source / GL blend
                                                            ↑ 래스터 단계에서 이미 보정됨

현재 GhostWin (blur):
D2D DrawGlyphRun(gamma=1.0) → linear coverage → raw → Dual Source
                                                  ↑ 보정 누락! (A도 B도 아님)

목표: 경로 A 완성
D2D DrawGlyphRun(gamma=1.0) → linear coverage → EnhanceContrast → AlphaCorrection → Dual Source
```

### 2.2 Data Flow (WT 패턴)

```
D2D DrawGlyphRun (gamma=1.0, contrast=0.0)
  → Atlas 텍스처 (B8G8R8A8_UNORM, linear coverage)
  → Pixel Shader:
      coverage = glyphAtlas.Sample(pointSamp, uv).rgb
      k' = ApplyLightOnDarkContrastAdjustment(enhancedContrast, fgColor)
      contrasted = EnhanceContrast3(coverage, k')
      alphaCorrected = ApplyAlphaCorrection3(contrasted, fgColor, gammaRatios)
      weights = alphaCorrected * fgColor.a
      color = weights * fgColor
  → Dual Source Blend:
      result = color + dest * (1 - weights.rgb)
```

### 2.3 Dependencies

| Component | Depends On | Purpose |
|-----------|-----------|---------|
| shader_ps.hlsl | DWrite helpers (이미 구현됨) | 감마 보정 함수 |
| glyph_atlas.cpp | D2D API, IDWriteGlyphRunAnalysis | 글리프 bounds + 래스터 |
| winui_app.cpp | IDXGISwapChain2 | SetMatrixTransform |

---

## 3. Functional Requirements

### FR-01: DWrite 감마 보정 복원 (1순위)

**변경 파일**: `src/renderer/shader_ps.hlsl`

**현재 코드** (raw coverage):
```hlsl
float3 coverage = glyph.rgb;
o.weights = float4(coverage * input.fgColor.a, 1);
o.color = o.weights * input.fgColor;
```

**목표 코드** (WT 패턴 — shader_ps.hlsl:59-69):
```hlsl
float blendK = DWrite_ApplyLightOnDarkContrastAdjustment(
    enhancedContrast, input.fgColor.rgb);
float3 contrasted = DWrite_EnhanceContrast3(glyph.rgb, blendK);
float3 alphaCorrected = DWrite_ApplyAlphaCorrection3(
    contrasted, input.fgColor.rgb, gammaRatios);
o.weights = float4(alphaCorrected * input.fgColor.a, 1);
o.color = o.weights * input.fgColor;
```

**이전 시도와 다른 점 (사실 기반)**:
- 이전 (작업일지 "감마=소프트" 시점):
  - 래스터: **CreateAlphaTexture** (system gamma=1.8 **baked-in**)
  - 셰이더: EnhanceContrast + AlphaCorrection 적용
  - → **이중 감마** (baked 1.8 + 셰이더 보정) → 과보정 → 소프트
  - 참고: bgColor 불일치 가설은 **반증됨** (quad_builder에서 bg/text 동일 bg_packed 사용 확인)
- 이번:
  - 래스터: **D2D DrawGlyphRun** (linearParams **gamma=1.0**, linear coverage)
  - 셰이더: EnhanceContrast + AlphaCorrection 적용
  - → **단일 감마** (linear + 셰이더 보정) → 정상 보정 → WT와 동일

**수학적 근거** (Agent 3,6 합의):
- `EnhanceContrast(0.5, 0.5) = 0.6` → 에지 coverage 20% 증가 → 더 진한 글리프
- `AlphaCorrection` → 전경색 밝기에 따라 적응적 두께 보정
- WT와 수학적으로 동일한 blend 결과: `lerp(dest, fg, alphaCorrected * fg_a)`

### FR-02: Bounds 통일 — D2D bounds 사용 (2순위)

**변경 파일**: `src/renderer/glyph_atlas.cpp`

**현재 문제** (Agent 5,10):
- bounds: `CreateGlyphRunAnalysis(Factory1, RENDERING_MODE_DEFAULT, system gamma)` 
- 렌더: `D2D DrawGlyphRun(linearParams gamma=1.0)`
- **렌더링 파라미터 불일치** → D2D 글리프가 bounds 밖으로 나갈 수 있음

**목표**: WT처럼 bounds와 렌더링을 동일한 D2D 경로로 통일

**방법 A**: `ID2D1DeviceContext::GetGlyphRunWorldBounds()` 사용 (WT 패턴)
- D2D1.1+ 필요 (ID2D1DeviceContext)
- 현재 ID2D1RenderTarget → ID2D1DeviceContext로 업그레이드 필요

**방법 B**: D2D 렌더링 후 atlas에서 실제 non-zero bounds 스캔
- 추가 GPU→CPU 전송 필요 → 느림

**방법 C**: CreateGlyphRunAnalysis bounds에 padding 추가
- 간단하지만 정확하지 않음

**선택**: 방법 A (WT 정확 복제). 불가능하면 방법 C (임시).

### FR-03: SetMatrixTransform (3순위)

**변경 파일**: `src/app/winui_app.cpp`

**WT 패턴** (AtlasEngine.r.cpp:427-431):
```cpp
DXGI_MATRIX_3X2_F matrix{
    ._11 = 96.0f / font_dpi,
    ._22 = 96.0f / font_dpi,
};
swapChain->SetMatrixTransform(&matrix);
```

**현재 GhostWin**: SetMatrixTransform 미사용 (이전에 추가했다가 revert)

**영향**: DPI=1.0에서는 무관하지만, 고DPI에서 bilinear filtering 방지 필수

---

### FR-04: 3-pass 잔재 제거 (Step 1 — 선명도 수정 전 정리)

**이유**: 3-pass 렌더링 코드가 현재 Dual Source 아키텍처와 모순. 정리 후 수정해야 혼동 방지.

**삭제 대상 (동작 변경 없음)**:

| 파일 | 라인 | 삭제 대상 |
|------|------|----------|
| shader_ps.hlsl | 15 | `Texture2D<float4> bgTexture : register(t1)` — 미사용 |
| dx11_renderer.cpp | 44-45 | `bg_copy_tex`, `bg_copy_srv` 멤버 |
| dx11_renderer.cpp | 252-267 | `create_rtv()` 내 bg_copy 텍스처 생성 코드 |
| dx11_renderer.cpp | 437 | 주석 "ClearType per-channel blending is done inside the shader via bgTexture lerp" → Dual Source로 수정 |
| dx11_renderer.cpp | 527 | `bg_count` 파라미터 — draw_instances, upload_and_draw에서 제거 |
| dx11_renderer.h | 65 | `bg_count` 파라미터 — upload_and_draw 선언에서 제거 |

**유지 (FR-01에서 사용 예정)**:
- shader_ps.hlsl:26-57 DWrite 감마 함수 6개 — FR-01에서 활성화
- shader_ps.hlsl:18-24 cbuffer (enhancedContrast, gammaRatios) — FR-01에서 사용

### FR-05: 미사용 코드 정리 (Step 4)

| 파일 | 삭제/수정 대상 |
|------|-------------|
| shader_common.hlsl | 파일 전체 삭제 (VS/PS 각각 PSInput 인라인 정의) |
| dx11_renderer.cpp:283-311 | ShaderInclude 핸들러 간소화 (include 없으므로 nullptr 전달) |
| glyph_atlas.cpp:106-107 | `recommended_rendering_mode/grid_fit_mode` 멤버 제거 또는 실제 사용 |
| dx11_renderer.h:28-33 | `RendererConfig` 미사용 멤버 (cols, rows, font_size_pt, font_family) 제거 |
| winui_app.cpp:1664-1668 | `atlas_dump` → `#ifdef _DEBUG` 감싸기 |
| winui_app.cpp:465 | "EXPERIMENT" 주석 → 의도 명확화 또는 제거 |

---

## 4. Non-Functional Requirements

### NFR-01: 성능
- 셰이더 감마 보정 추가로 인한 GPU 비용: 미미 (ALU 연산 4줄)
- D2D bounds 전환: CreateGlyphRunAnalysis 호출 제거 → 오히려 성능 개선 가능

### NFR-02: 호환성
- Dual Source Blending: D3D11 FL 10_0+ 스펙 필수 지원
- ID2D1DeviceContext: D2D1.1 (Windows 8+)

---

## 5. Implementation Order

```
Step 1: FR-04 (3-pass 잔재 제거)
  → 빌드 → 10/10 테스트 PASS → 기존 동작 유지 확인 → 커밋
  ↓ 깨끗한 코드 베이스에서 시작

Step 2: FR-01 (DWrite 감마 보정 복원)
  → 빌드 → 4분면 스크린샷 비교 → 사용자 확인
  → 개선 확인 시 커밋. 악화 시 revert

Step 3: FR-02 (D2D bounds 통일)
  → 빌드 → 글리프 정렬/클리핑 확인 → 사용자 확인
  → 커밋

Step 4: FR-05 (미사용 코드 정리)
  → 빌드 → 10/10 테스트 → 커밋

Step 5: FR-03 (SetMatrixTransform)
  → 빌드 → 커밋

Step 6: 디버그 잔재 정리
  → #ifdef _DEBUG, static 변수 → 커밋
```

**원칙**: Step 1(정리) → Step 2(핵심 수정) → Step 3-6(추가 개선). 정리 먼저, 기능 수정 후.

---

## 6. Testing Strategy

### 6.1 각 단계 검증

| Step | 검증 방법 | 성공 기준 |
|:----:|----------|----------|
| FR-01 | 4분면 스크린샷 + 사용자 시각 확인 | blur 감소, WT와 유사한 텍스트 두께 |
| FR-02 | atlas 덤프 + 글리프 클리핑 없음 확인 | 글리프 가장자리 잘림 없음 |
| FR-03 | 고DPI 모니터 또는 DPI 에뮬레이션 | 1:1 픽셀 매핑 |

### 6.2 회귀 방지

- 각 변경 후 10/10 테스트 PASS 확인
- 이전 시도 결과와 비교 (작업일지 참조)
- 악화 시 즉시 `git checkout` 복원

---

## 7. Risks

| 위험 | 가능성 | 대응 |
|------|:------:|------|
| DWrite 감마가 Dual Source에서도 blur | 낮음 (이전과 다른 맥락) | 즉시 revert, 파라미터 튜닝 시도 |
| ID2D1DeviceContext 업그레이드 실패 | 중간 | 방법 C (padding) 폴백 |
| SetMatrixTransform이 DPI=1.0에서 부작용 | 낮음 | 조건부 적용 (DPI>1.0일 때만) |
