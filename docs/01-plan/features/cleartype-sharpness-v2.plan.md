# ClearType Sharpness v2 Planning Document

> **Summary**: WT 동등 ClearType 텍스트 선명도 달성 (현재 blur 잔존 해결)
>
> **Project**: GhostWin Terminal
> **Author**: Solit
> **Date**: 2026-04-03
> **Status**: Draft

---

## Executive Summary (v3 — 리서치 기반 재수립)

| Perspective | Content |
|-------------|---------|
| **Problem** | D2D DrawGlyphRun이 premultiplied alpha 텍스처에서 **linear 공간 AA**를 수행하여 에지가 소프트. 동일 위치 에지 coverage: D2D=85(0.33) vs CreateAlphaTexture=139(0.54) — 실측 확인(FACT) |
| **Solution** | D2D 제거 → CreateAlphaTexture(system gamma AA, ClearType 3x1) + raw coverage + Dual Source Blending. Alacritty 정확 경로 |
| **Function/UX Effect** | gamma 공간 AA로 에지가 급격하게 전이 → 시각적으로 굵고 선명. Alacritty/WezTerm 동등 |
| **Core Value** | 경쟁 터미널 동등 렌더링 품질 + 코드 단순화 (D2D 의존성 제거) |

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

### 2.0 근본 원인 확정 (리서치 에이전트 3명 검증, 2026-04-03)

#### 핵심 발견 1: D2D premultiplied AA = Linear 공간 (FACT — atlas 덤프 실측)

| | D2D DrawGlyphRun (현재) | CreateAlphaTexture (Alacritty) |
|---|:---:|:---:|
| AA 수행 공간 | **Linear (gamma=1.0)** | **Gamma (~1.8)** |
| 에지 coverage (1/3 위치) | **85 (0.333)** | **~139 (0.543)** |
| ClearType 동작 | R≠G≠B 확인 (90.9%) | R≠G≠B (100%) |
| 에지 전이 | **완만 → 소프트** | **급격 → 선명** |

- Atlas 픽셀 덤프에서 D2D가 ClearType(R≠G≠B) 데이터를 생성하는 것은 확인 (90.9%)
- **하지만 linear 공간 AA는 에지 coverage가 낮아** (85 vs 139) → 가늘고 소프트한 텍스트
- 셰이더 감마 보정(EnhanceContrast)이 85→~139로 근사하지만 **AA 샘플링 공간 차이는 보정 불가**
  - Linear AA: 서브픽셀 샘플을 linear 공간에서 평균 → 결과가 완만
  - Gamma AA: 서브픽셀 샘플을 gamma 공간에서 평균 → 결과가 급격

근거: Agent 2 (이론), Agent 3 (atlas 덤프 실측 85 vs 139)

#### 핵심 발견 2: D2D premultiplied에서 ClearType "unpredictable" (MSDN FACT)

> "If you specify an alpha mode other than D2D1_ALPHA_MODE_IGNORE, the text antialiasing mode
> automatically changes from CLEARTYPE to GRAYSCALE." — [MSDN D2D1_ALPHA_MODE](https://learn.microsoft.com/en-us/windows/win32/api/dcommon/ne-dcommon-d2d1_alpha_mode)
> "Rendering ClearType text to a transparent surface can create unpredictable results." — MSDN

- SetTextAntialiasMode(CLEARTYPE) 강제 전환은 가능하나 "unpredictable results"
- WT도 동일 구조(premultiplied + CLEARTYPE 강제)지만, WT의 선명도는 DWrite 감마 보정 + Dual Source로 보상
- GhostWin에서는 이 보상이 불완전 → 여전히 소프트

근거: Agent 1 (MSDN 문서), WT 코드 확인

#### 핵심 발견 3: CreateAlphaTexture가 더 선명한 이유 (FACT — 이론 + 코드 + 실측)

| 요인 | CreateAlphaTexture | D2D DrawGlyphRun |
|------|:---:|:---:|
| AA 공간 | gamma (~1.8) — **선명** | linear (1.0) — **소프트** |
| Contrast | system enhanced contrast **baked** | 0.0 (비활성, 셰이더 보상) |
| Thin font boost | **Baked** | 내부적으로 남을 수 있으나 불확실 |
| 보정 | 불필요 (이미 display-ready) | 셰이더 근사치 → 완벽하지 않음 |

근거: Agent 2 (lhecker dwrite-hlsl 주석: "don't need shader correction for CreateAlphaTexture"), Agent 3 (실측값 비교)

#### 이전 "감마=소프트" 재분석

| 이전 결론 | 재분석 | 근거 |
|----------|--------|------|
| "DWrite 감마 보정이 소프트" | **CreateAlphaTexture(gamma baked) + 셰이더 감마 = 이중 감마 → 소프트** | 작업일지 타임라인 + API 분석 |
| "Raw coverage가 최선" | **CreateAlphaTexture raw = 이미 감마 보정됨 → 정상** | Agent 2: "시스템 감마가 baked" |
| "bgColor 불일치" | **반증됨** — quad_builder에서 bg/text 동일 bg_packed | 코드 확인 |

#### 해결 방향: CreateAlphaTexture + raw + Dual Source (경로 B + Dual Source)

| 구성 요소 | 현재 (경로 A) | 목표 (경로 B + DS) | 근거 |
|----------|:-----------:|:----------------:|------|
| 래스터 | D2D DrawGlyphRun (linear) | **CreateAlphaTexture (gamma baked)** | Agent 2,3: gamma AA가 선명 |
| 셰이더 감마 | EnhanceContrast + AlphaCorrection | **없음 (raw coverage)** | 이미 baked → 이중 감마 방지 |
| 블렌딩 | Dual Source (유지) | **Dual Source (유지)** | per-channel 필수 |
| Atlas 포맷 | B8G8R8A8 (D2D) | **B8G8R8A8 (유지, CPU 업로드)** | 호환성 |

**이 조합은 한 번도 시도하지 않았습니다.** 이전 시도: CreateAlphaTexture + raw + per-channel lerp (Dual Source 없음)

---

### 2.1 In Scope (사실 기반 차이)

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

#### 차이 4 (병행): 코드 품질 저하 — 17+ 반복 수정의 기술 부채

**현상**: 코드 분석 점수 72/100. 3-pass 렌더링 잔재, 모순된 주석, 죽은 코드가 산재.

**구체적 문제 (코드 분석기 확인)**:

| 심각도 | 파일 | 문제 | 영향 |
|:---:|------|------|------|
| CRITICAL | dx11_renderer.cpp:44,252 | `bg_copy_tex`/`bg_copy_srv` — 3-pass 잔재, 매 리사이즈 GPU 메모리 낭비 | 리소스 낭비 |
| CRITICAL | shader_ps.hlsl:15 | `bgTexture (t1)` — 미사용 텍스처 선언 | 코드 혼동 |
| WARNING | dx11_renderer.cpp:527 | `bg_count` 파라미터 — 3-pass 잔재, 미사용 | 불필요 복잡성 |
| WARNING | dx11_renderer.cpp:252 | 주석 "3-pass approach" — 현재 Dual Source와 모순 | 오도 |
| WARNING | shader_common.hlsl | 전체 파일 미사용 — PSInput이 VS/PS에서 중복 정의 | 죽은 코드 |
| WARNING | glyph_atlas.cpp:106 | `recommended_rendering_mode` — 저장만 하고 미사용 | 불필요 |
| WARNING | winui_app.cpp:706-1442 | `RunImeTest` 737줄 — 제품 코드와 동일 파일 | 파일 비대 |
| WARNING | winui_app.cpp:1664 | `atlas_dump` — Release 빌드에서 실행 | 디버그 잔재 |

**수정**: FR-01~03과 병행하여 코드 정리. 선명도 수정과 함께 정리하여 최종 코드 품질 90+ 목표.

### 2.2 Out of Scope

- HWND child 방식 전환 (아키텍처 변경 — 별도 Phase)
- 폰트 변경/배경색 변경 (테마 문제)
- Phase 5 (멀티세션 UI)
- RunImeTest 분리 (별도 리팩토링 — 이번 scope 외)

---

## 3. Approach

### 3.1 핵심 원칙

1. **한 번에 하나씩 변경** — 각 변경의 효과를 독립적으로 측정
2. **작업일지 확인** — 이미 시도한 것 반복 금지
3. **스크린샷 비교** — 정량적 측정보다 사용자 시각 확인 우선
4. **WT 코드 정확 복제** — 추측 기반 수정 금지

### 3.2 실행 순서 (리서치 3명 + 10명 교차검증 기반)

| 순서 | 변경 | 파일 | 근거 | 검증 |
|:----:|------|------|------|------|
| **1** | **D2D DrawGlyphRun 제거 → CreateAlphaTexture 복원** | glyph_atlas.cpp | Agent 1,2,3: D2D linear AA가 소프트의 근본 원인. Atlas 실측 85 vs 139 | 4분면 스크린샷 |
| **2** | **셰이더: DWrite 감마 제거 → raw coverage** | shader_ps.hlsl | CreateAlphaTexture에 system gamma baked → 셰이더 감마 불필요 (이중 감마 방지) | 스크린샷 |
| **3** | **Dual Source Blending 유지** | dx11_renderer.cpp | per-channel ClearType 필수. 이 조합(CreateAlphaTexture+raw+DualSource)은 미시도 | 스크린샷 |
| **4** | **3-pass 잔재 + D2D 코드 정리** | 다수 | Step 1 완료 분 + D2D 관련 멤버/include 제거 | 빌드+테스트 |
| **5** | **미사용 코드 정리** | 다수 | 코드 분석기 72→90+ 목표 | 빌드+테스트 |

### 3.3 이전 시도와의 정확한 차이 (FACT 기반)

| 이전 시도 | 래스터 | 셰이더 | 블렌딩 | 결과 | 왜 실패 |
|----------|:---:|:---:|:---:|:---:|---------|
| 시도 1-2 | CreateAlphaTexture | raw/linear lerp | **per-channel lerp** | blur | per-channel은 맞지만 max(R,G,B) alpha 잔재 |
| 시도 6-8 | CreateAlphaTexture | DWrite 감마 | per-channel lerp | bold/soft | **이중 감마** (baked + 셰이더) |
| 시도 13-14 | **D2D linear** | DWrite 감마 | per-channel lerp | soft | D2D linear AA + 이중 보정 |
| 현재 | D2D linear | DWrite 감마 | **Dual Source** | 개선되었으나 부족 | **linear AA 근본 한계** |
| **이번 (미시도)** | **CreateAlphaTexture** | **raw** | **Dual Source** | **?** | 없음 (미시도) |

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

### 선명도
- [ ] 사용자가 4분면 비교에서 "blur 없음" 또는 "WT와 동등" 확인
- [ ] 에이전트 평가에서 GhostWin 점수 >= WT 점수 - 0.5
- [ ] ClearType 프린지가 WT 수준으로 억제됨

### 코드 품질
- [ ] 3-pass 잔재 코드 완전 제거 (bg_copy_tex, bgTexture, bg_count)
- [ ] 모순된 주석 전부 수정 (3-pass 관련 주석 → Dual Source)
- [ ] 코드 분석기 점수 72 → 90+ 달성
- [ ] 미사용 파일/멤버/파라미터 제거
- [ ] Release 빌드에서 디버그 코드 미실행
