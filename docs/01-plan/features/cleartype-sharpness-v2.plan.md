# ClearType Sharpness v2 Planning Document

> **Summary**: WT 동등 ClearType 텍스트 선명도 달성 (현재 blur 잔존 해결)
>
> **Project**: GhostWin Terminal
> **Author**: Solit
> **Date**: 2026-04-03
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | GhostWin 텍스트가 WT/Alacritty/WezTerm 대비 blur. 17+ 시도 중 근본 해결 안 됨 |
| **Solution** | WT 코드 완전 분석 기반 3가지 정확한 차이점 수정 |
| **Function/UX Effect** | 터미널 텍스트 선명도가 WT 수준에 도달, 사용자 가독성 향상 |
| **Core Value** | 텍스트 렌더링 품질이 경쟁 터미널과 동등하여 제품 완성도 확보 |

---

## 1. Overview

### 1.1 Purpose

GhostWin의 ClearType 텍스트 렌더링 선명도를 Windows Terminal과 동등하게 만든다.

### 1.2 Background

- 세션 전체에서 17+ 시도, 대부분 실패
- Dual Source Blending 구현 완료 (WT와 동일)
- D2D DrawGlyphRun 구현 완료 (WT와 동일)
- **하지만 blur가 여전히 잔존** — 근본 원인 미해결

### 1.3 Related Documents

- ADR-010: `docs/adr/010-grayscale-aa-composition.md`
- 작업일지: `docs/03-analysis/cleartype-composition-worklog.md`
- WT 소스: `external/wt-ref/src/renderer/atlas/`
- Alacritty 소스: `external/al-ref/external/crossfont-ref/src/directwrite/mod.rs`

---

## 2. Scope

### 2.1 In Scope (WT 코드 리뷰 + 10명 에이전트 합의로 확인된 차이)

#### 차이 1 (1순위): DWrite 감마 보정 미적용 → blur 원인

**현상**: GhostWin 셰이더가 raw coverage를 그대로 Dual Source weights로 사용
**WT**: `EnhanceContrast3()` + `ApplyAlphaCorrection3()` + `gammaRatios` 적용

**왜 blur가 발생하는가 (수학적 증명, Agent 3,6,9)**:

1. **D2D linearParams(gamma=1.0)는 linear coverage를 생성**. linear coverage에서 에지 전이(transition)는 완만하게 분포:
   ```
   linear:  0.1 → 0.3 → 0.5 → 0.7 → 0.9  (균일 분포, 넓은 에지)
   gamma=1.8: 0.016 → 0.097 → 0.287 → 0.497 → 0.827  (급격 전이, 좁은 에지)
   ```
   linear coverage의 중간값(0.3~0.7)이 넓게 퍼져 **에지가 더 완만** → 시각적으로 blur

2. **sRGB 렌더타겟(B8G8R8A8_UNORM)에서 linear coverage로 블렌딩**하면, 감마 곡선 비선형성으로 인해 어두운 배경+밝은 텍스트에서 **중간 톤이 과도하게 밝아짐** (blooming):
   ```
   sRGB 블렌딩: fg=1.0, bg=0.1, α=0.5 → result = 0.55
   linear 블렌딩: → result = 0.72 (훨씬 밝음 = 에지가 두꺼워 보임 = 번짐)
   ```

3. **EnhanceContrast S-커브**가 이 문제를 해결하는 이유:
   ```
   EnhanceContrast(a, k) = a*(k+1) / (a*k+1)    // 위로 볼록한 단조증가 함수
   k=0.5일 때: 0.5 → 0.6 (에지 coverage 20% 증가 → 더 진한 글리프)
   ```
   낮은 coverage를 끌어올려 에지를 **더 급격하게 전이** → 시각적 선명도 향상

4. **ApplyAlphaCorrection**이 필요한 이유:
   ```
   correction = a*(1-a) * ((g.x*f + g.y)*a + (g.z*f + g.w))
   ```
   `a*(1-a)` 가중: **에지 영역(0<a<1)에서만** 보정 적용. 전경 밝기(f)에 따라 적응적으로 획 두께를 조정하여, 어두운/밝은 텍스트 모두에서 일관된 가독성 제공

5. **이전 시도에서 "감마=소프트"였던 이유와 이번이 다른 이유**:
   - 이전: `per-channel lerp(bgColor, fgColor, alphaCorrected)` → bgColor가 **정적 vertex 데이터**이므로 실제 framebuffer와 불일치 → 잘못된 기준으로 보정 → 소프트
   - 이번: `Dual Source: color + dest * (1-weights)` → **GPU가 실제 framebuffer dest를 읽음** → 정확한 보정 → 선명

**수정**: WT shader_ps.hlsl:59-69와 동일한 DWrite 감마 파이프라인 적용

---

#### 차이 2 (2순위): Bounds 불일치 → 글리프 클리핑/오프셋 blur

**현상**: GhostWin은 `CreateGlyphRunAnalysis(Factory1)`로 bounds를 구하고, `D2D DrawGlyphRun`으로 렌더링. 두 API가 **서로 다른 렌더링 파라미터**를 사용.

**왜 blur가 발생하는가 (Agent 5,10)**:

1. **Analysis** (bounds 계산): Factory1은 rendering params를 받지 않음 → **시스템 기본값 (gamma=1.8, contrast=0.5)** 사용
2. **D2D** (렌더링): `SetTextRenderingParams(linearParams)` → **gamma=1.0, contrast=0.0** 사용
3. gamma=1.0과 gamma=1.8은 **안티앨리어싱 범위가 다름**. gamma=1.0(linear)은 에지가 더 넓게 퍼짐
4. D2D가 analysis bounds보다 **더 넓은 글리프**를 생성하면 → atlas rect 밖으로 overflow → `PushAxisAlignedClip`에 의해 **가장자리 클리핑**
5. 클리핑된 글리프는 가장자리 정보가 손실되어 **불완전한 에지** → blur 인상

**WT가 이 문제를 회피하는 방법** (Agent 1 확인):
- WT의 `CreateGlyphRunAnalysis`는 `#if 0`으로 비활성화 (죽은 코드)
- WT는 **D2D 자체의 bounds 계산** (`GetGlyphRunWorldBounds`)을 사용
- 같은 API(D2D)로 bounds와 렌더링을 모두 수행 → **파라미터 일치 보장**

**수정**: CreateGlyphRunAnalysis 제거, D2D `GetGlyphRunWorldBounds` 또는 padding으로 bounds 통일

---

#### 차이 3 (3순위): SetMatrixTransform 미적용 → 고DPI bilinear filtering

**현상**: SwapChainPanel은 **항상 DPI scale transform을 자동 적용** (Agent 8 확인). 이를 상쇄하지 않으면 DWM이 bilinear filtering을 적용.

**왜 blur가 발생하는가 (Agent 8)**:

1. SwapChainPanel의 implicit DPI transform: `{CompositionScaleX, CompositionScaleY}`
2. 이 transform이 정수가 아니면 (예: 125% = 1.25) → **bilinear interpolation** → 모든 픽셀이 2x2 이웃 평균으로 변환 → blur
3. DPI=1.0 (100%)에서는 identity transform → blur 없음. **고DPI에서만 영향**
4. WT 패턴: `SetMatrixTransform(96/dpi, 96/dpi)` → implicit transform과 정확히 상쇄 → 1:1 pixel mapping

**현재 영향**: 사용자 DPI=1.0이므로 **현재는 blur 원인이 아님**. 하지만 고DPI 호환을 위해 적용 필요.

**수정**: WT와 동일한 `IDXGISwapChain2::SetMatrixTransform` 적용

### 2.2 Out of Scope

- HWND child 방식 전환 (아키텍처 변경 — 별도 Phase)
- 폰트 변경/배경색 변경 (테마 문제)
- Phase 5 (멀티세션 UI)

---

## 3. Approach

### 3.1 핵심 원칙

1. **한 번에 하나씩 변경** — 각 변경의 효과를 독립적으로 측정
2. **작업일지 확인** — 이미 시도한 것 반복 금지
3. **스크린샷 비교** — 정량적 측정보다 사용자 시각 확인 우선
4. **WT 코드 정확 복제** — 추측 기반 수정 금지

### 3.2 실행 순서 (10명 에이전트 합의 기반)

| 순서 | 변경 | 파일 | 근거 | 검증 |
|:----:|------|------|------|------|
| **1** | **DWrite 감마 보정 복원** (EnhanceContrast + AlphaCorrection) | shader_ps.hlsl | 10명 전원 합의, 수학 증명. 이전 실패는 per-channel lerp 맥락 | 스크린샷 |
| **2** | **Bounds 통일**: CreateGlyphRunAnalysis 제거, D2D bounds 사용 | glyph_atlas.cpp | Agent 5,10: WT는 D2D 자체 bounds 사용 | 글리프 정렬 확인 |
| **3** | SetMatrixTransform 추가 (DPI scale 상쇄) | winui_app.cpp | Agent 8: WT 필수 패턴 | 고DPI 대응 |

각 단계 후:
- 빌드 → 실행 → 4분면 스크린샷 → 사용자 확인
- 개선되면 커밋, 악화되면 revert

### 3.3 이전 시도와의 차이

| 이전 시도 | 왜 실패 | 이번에 다른 점 |
|----------|---------|-------------|
| DWrite 감마 + per-channel lerp | 정적 bgColor 불일치 | **Dual Source (실제 dest)** |
| D2D DrawGlyphRun 단독 | per-channel lerp 한계 | **Dual Source 병행** |
| Factory1 + DEFAULT 모드 | gridFitMode/antialiasMode 누락 | **Factory3 + 파라미터 전달** |

---

## 4. Risk

| 위험 | 대응 |
|------|------|
| DWrite 감마가 Dual Source에서도 소프트 | 즉시 revert, raw coverage로 복원 |
| Factory3 CreateGlyphRunAnalysis 실패 | Factory1 폴백 유지 |
| D2D bounds vs Analysis bounds 불일치 | Analysis를 Factory3로 통일 |

---

## 5. Success Criteria

- [ ] 사용자가 4분면 비교에서 "blur 없음" 또는 "WT와 동등" 확인
- [ ] 에이전트 평가에서 GhostWin 점수 >= WT 점수 - 0.5
- [ ] ClearType 프린지가 WT 수준으로 억제됨
