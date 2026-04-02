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

### 1.2 Design Principles

- **WT 코드 정확 복제**: 추측 금지, WT 소스에서 확인된 코드만 사용
- **작업일지 참조 필수**: 이미 시도한 것 반복 금지 (17+ 실패 교훈)
- **맥락 구분**: 이전 "감마=소프트"는 per-channel lerp 맥락. Dual Source에서는 다름

---

## 2. Architecture

### 2.1 현재 파이프라인 vs 목표 파이프라인

```
현재 GhostWin:
D2D DrawGlyphRun(linearParams) → raw coverage → Dual Source Blend
                                  ↑ 감마 보정 없음 → 얇고 연한 텍스트

목표 (WT 동등):
D2D DrawGlyphRun(linearParams) → EnhanceContrast → AlphaCorrection → Dual Source Blend
                                  ↑ DWrite 감마 보정 → 적정 두께/대비
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

**이전 시도와 다른 점**:
- 이전: per-channel lerp `lerp(bgColor, fgColor, alphaCorrected)` → bgColor 불일치 → 소프트
- 이번: Dual Source `color + dest * (1-weights)` → **실제 framebuffer dest** → 정확

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
Step 1: FR-01 (셰이더 감마 보정)
  → 빌드 → 4분면 스크린샷 비교 → 사용자 확인
  → 개선 확인 시 커밋

Step 2: FR-02 (D2D bounds 통일)
  → 빌드 → 글리프 정렬 확인 → 사용자 확인
  → 개선 확인 시 커밋

Step 3: FR-03 (SetMatrixTransform)
  → 빌드 → 고DPI 테스트 → 커밋
```

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
