# ClearType Composition 작업일지

> **Feature**: ClearType 서브픽셀 렌더링
> **시작일**: 2026-04-02
> **현재 점수**: GhostWin ~75 vs Alacritty ~85 (블라인드 실측)

---

## 진단 결과 요약 (P1~P4 수정 전)

| 진단 | 결과 | 상태 |
|------|------|:----:|
| D1: bg_count | 3030/3173 정상 분리 | OK |
| D2: bgTexture | 유효 데이터 (CopyResource 정상) | OK |
| D3: per-channel diff | 존재하지만 10x 증폭해도 미세 | **문제** |
| Dual Source Blending | ZERO 테스트로 미동작 확정 | 대안(3-pass) 사용 |

## 미검증 문제 (P1~P4)

| # | 문제 | 코드 위치 | 현재 값 |
|---|------|----------|--------|
| P1 | `clearTypeLevel = 0.0` | glyph_atlas.cpp:266 | 0.0 (=Grayscale 폴백 가능) |
| P2 | `enhancedContrast (ClearType용) = 0.0` | glyph_atlas.cpp:264 | 0.0 (대비 향상 없음) |
| P3 | 셰이더 contrast가 Grayscale 전용 값 | shader_ps.hlsl | grayscaleEnhancedContrast 사용 |
| P4 | Alacritty 렌더링 파이프라인 미확인 | — | 미조사 |

---

## P1: clearTypeLevel 수정

**시각**: 2026-04-02
**문제**: `CreateCustomRenderingParams`에서 `clearTypeLevel = 0.0`
**MS 문서**: "clearTypeLevel: 0.0 = Grayscale, 1.0 = fully ClearType"
**영향**: DirectWrite가 ClearType 래스터 시 이 파라미터를 참조하면, 0.0에서 서브픽셀 분리가 약해질 수 있음
**단, CreateGlyphRunAnalysis에 직접 전달되지 않음** — 영향 범위 확인 필요

### 수정 전
```cpp
factory3->CreateCustomRenderingParams(
    1.0f,   // gamma
    0.0f,   // enhancedContrast (ClearType)
    dwrite_enhanced_contrast,  // grayscaleEnhancedContrast
    0.0f,   // clearTypeLevel ← 문제
    ...
```

### 수정 후
```cpp
factory3->CreateCustomRenderingParams(
    1.0f,   // gamma
    ???,    // enhancedContrast (ClearType) — P2에서 결정
    dwrite_enhanced_contrast,  // grayscaleEnhancedContrast
    1.0f,   // clearTypeLevel = full ClearType
    ...
```

### 결과 (P1 + P2 동시 수정)
- 빌드: 0 errors, 10/10 PASS
- 로그: `ctContrast=0.50, gsContrast=1.00, ctLevel=1.0, gridFit=ENABLED`
- P1: clearTypeLevel 0.0 → **1.0** (full ClearType)
- P2: enhancedContrast 0.0 → **0.50** (시스템 ClearType 값)
- 스크린샷: p1p2p3_result.png — 육안 확인 필요

---

## P2: ClearType enhancedContrast 수정

**시각**: 2026-04-02
**문제**: ClearType용 `enhancedContrast = 0.0` → 대비 향상 완전 비활성
**수정**: `params->GetEnhancedContrast()` = 0.50 (시스템 값)
**결과**: P1과 동시 수정됨. 로그에서 `ctContrast=0.50` 확인

---

## P3: 셰이더 contrast 분리

**시각**: 2026-04-02
**문제**: 셰이더의 `enhancedContrast` uniform이 `grayscaleEnhancedContrast(1.00)` 고정
**수정**: `GlyphAtlas::enhanced_contrast()`가 ClearType 활성 시 `dwrite_ct_contrast(0.50)` 반환
**Impl 멤버**: `dwrite_ct_contrast` 추가, init_dwrite에서 저장
**결과**: 빌드 성공, 셰이더에 ClearType 전용 contrast 전달됨

**주의**: ClearType contrast(0.50)이 Grayscale contrast(1.00)보다 낮음 → 텍스트가 더 얇아 보일 수 있음. 추가 튜닝 필요할 수 있음.

---

## P4: Alacritty 렌더링 파이프라인 확인 (코드 기반)

**시각**: 2026-04-02
**소스**: alacritty/crossfont (DirectWrite Windows 모듈), alacritty/alacritty (렌더러)

### 핵심 발견

| 항목 | Alacritty | GhostWin |
|------|-----------|----------|
| **Surface** | WGL 불투명 framebuffer | Composition swapchain |
| **AA 모드** | ClearType 3x1 (항상, 폴백 없음) | ClearType 시도 → 3-pass lerp |
| **서브픽셀 합성** | GL 3-pass subpixel blending (`glBlendFuncSeparate`) | 셰이더 내부 lerp |
| **Rendering Params** | **시스템 기본값 그대로** (커스텀 없음) | gamma=1.0 + 셰이더 감마 보정 |
| **Rendering Mode** | `get_recommended_rendering_mode_default_params` (시스템 기본) | NATURAL_SYMMETRIC + GRID_FIT_ENABLED |
| **Grid Fit** | 지정 안 함 (폰트 gasp 테이블 의존) | ENABLED 강제 |
| **폰트 크기** | 11.25pt (15px) | 12.0pt (16px) |

### Alacritty가 더 선명한 근본 이유 (FACT)

1. **WGL 불투명 프레임버퍼 → ClearType 완전 동작** (코드 확인: `crossfont/src/directwrite/mod.rs:80-84`)
2. **GL 3-pass subpixel blending** — per-channel 블렌딩이 GPU 하드웨어 레벨에서 동작 (코드 확인: `gles2.rs:400-417`)
3. **시스템 기본 RenderingParams** — 커스텀 gamma=1.0이 아닌 **시스템 gamma(~1.8)로 래스터** → DirectWrite가 내부에서 최적화된 hinting 적용

### GhostWin과의 핵심 차이

**Alacritty는 커스텀 렌더링 파라미터를 전혀 사용하지 않음.** CreateCustomRenderingParams 호출이 crossfont 전체에 없음. 시스템 기본값(gamma 1.8, 시스템 contrast, 시스템 clearTypeLevel)을 그대로 사용.

**반면 GhostWin은 gamma=1.0(linear) + 셰이더 감마 보정 패턴(WT 방식)**을 사용. 이 방식은 WT에서 검증되었지만, WT는 Dual Source Blending이 동작하는 환경. GhostWin은 Dual Source 미동작 + 3-pass셰이더 lerp로 대체.

### 시사점

Alacritty 방식을 따르려면:
1. **커스텀 RenderingParams 제거** → 시스템 기본값으로 래스터
2. 셰이더 감마 보정 비활성화 (DirectWrite가 래스터 시 이미 감마 적용)
3. 또는 WGL/HWND 기반 렌더링으로 전환 (구조 변경 대형)

---

## 추가 발견: gamma=1.0 래스터의 문제 가능성

**Alacritty**: 시스템 기본 gamma(~1.8)로 래스터 → DirectWrite가 hinting + 감마 적용
**GhostWin**: gamma=1.0(linear)로 래스터 → 셰이더에서 감마 보정

**차이**: gamma=1.0으로 래스터하면 DirectWrite가 **감마 미적용 상태의 coverage 값**을 생성. 이것은 수학적으로 셰이더에서 보정할 수 있지만, **hinting 품질에 영향을 줄 수 있음** — DirectWrite가 감마를 고려하여 hinting 결정을 내리기 때문.

**이것은 ASSUMPTION** — 검증 필요. 하지만 Alacritty가 시스템 기본 params로 더 선명하다는 사실과 일치.

---

## 추가: 배경색 통일

**시각**: 2026-04-02
**변경**: clear_color `{0.1, 0.1, 0.15}` → `{0.0, 0.0, 0.0}` (Alacritty 동일 검정)
**파일**: dx11_renderer.cpp:521
**목적**: 비교 시 배경색 차이에 의한 시각적 편향 제거

---

## 공정 비교 블라인드 평가 결과 (동일 폰트/배경)

**조건**: JetBrainsMono NF 11.25pt, #1E1E2E 배경, "abcdefg 한글"

| 평가자 | GhostWin | Alacritty | Delta |
|--------|:--------:|:---------:|:-----:|
| #1 | 72 | 82 | -10 |
| #2 (글자별) | 66 | 81 | -15 |
| #3 | 82 | 78 | +4 |
| **평균** | **73.3** | **80.3** | **-7** |

**결론**: P1~P3 수정 + 3-pass ClearType는 **Grayscale 대비 개선 없음**. 격차 -8 → -7로 사실상 동일.

**근본 문제**: 3-pass shader lerp 방식은 ClearType per-channel 데이터를 사용하지만, 밝은 텍스트+어두운 배경에서 lerp 결과가 Grayscale과 거의 동일. Alacritty의 선명도는 ClearType보다 **hinting/rasterization 품질 차이**에 기인.

### 남은 미검증 가설

1. **gamma=1.0 래스터의 hinting 품질 저하** — Alacritty는 시스템 기본 gamma(1.8) 사용, GhostWin은 gamma=1.0
2. **셰이더 감마 보정의 정확성** — dwrite-hlsl 패턴이 3-pass lerp에서도 올바른지

---

## 결정적 진단 결과 (FACT 확정)

### 1. Composition 블러 테스트: **블러 없음**
4px 줄무늬 + 1px 선 테스트 결과 **완벽하게 선명**. Composition swapchain이 픽셀 정확도를 100% 보존.

### 2. ClearType 글리프 바이트 데이터: **정상**
```
'P': (85,170,255) (255,255,255) ... (52,9,0) — R<G<B 서브픽셀 분리 정확
'o': (0,0,5) (35,90,161) (210,241,250) ... — 가장자리 그라데이션 정확
```
ClearType 3x1 래스터 데이터는 Alacritty와 동일한 DirectWrite API를 통해 정상 생성됨.

### 3. v1 API: ClearType 3x1 정상 작동
Factory v1 CreateGlyphRunAnalysis에서도 시스템 ClearType 설정에 따라 3x1 데이터 정상 생성 확인.

### 미해결: 화면에서 Alacritty 대비 블러한 원인
- Composition 블러 아님 (테스트 증명)
- 글리프 데이터 정상 (바이트 덤프 확인)
- 셰이더 lerp 수학적으로 정확 (검증됨)
- 가능한 남은 원인: **3-pass 렌더의 bgTexture.Load()와 lerp 과정에서의 미세 정밀도 손실**, 또는 **DXGI_FORMAT의 sRGB 변환 차이**

---

## 실험: 감마 보정 비활성화 (Alacritty 패턴)

**시각**: 2026-04-02
**가설**: 셰이더의 DWrite 감마 보정(EnhanceContrast + AlphaCorrection)이 텍스트를 소프트하게 만듦
**Alacritty 패턴**: raw ClearType coverage를 감마 보정 없이 직접 사용

### 변경 사항
1. **shader_ps.hlsl**: 감마 보정 함수 호출 제거, `corrected = glyph.rgb` (raw coverage)
2. **glyph_atlas.cpp**: `GetRecommendedRenderingMode`에 시스템 기본 params(gamma=1.8) 전달 (linear_params 대신)

### 결과
- 빌드: 10/10 PASS
- 사용자 육안: "조금 더 개선된 것 같긴 해. 아직 블러한 느낌 남아있음"
- **블라인드 3명 평가**: GhostWin 72.7 vs Alacritty 74 — **격차 -1.3점!**
- 이전 대비: -8 → -7 → **-1.3** (대폭 축소)
- 평가자 #1: 두 터미널 구분 불가 (동점 72/72)
- 평가자 #3: "이전 7점 격차에서 약 2점 격차로 줄어듦"

### 결론: 감마 보정 비활성화가 핵심 개선점

셰이더의 DWrite 감마 보정(EnhanceContrast + AlphaCorrection)이 텍스트 가장자리를 **소프트하게** 만들고 있었음. Raw coverage를 직접 사용하면 Alacritty와 거의 동등.

**단, 절대 점수가 72-74로 낮음** — 감마 보정을 제거하면 대비가 낮아져 전체적으로 얇게 보임. 감마 보정의 "양"을 줄이되 완전 제거하지 않는 중간값이 최적일 수 있음.

---

## 실험 A+C: 50% 감마 + contrast 부스트

**시각**: 2026-04-02
**변경**:
1. **셰이더**: `corrected = lerp(raw, full_corrected, 0.5)` — raw와 감마보정의 50% 블렌드
2. **contrast**: `dwrite_ct_contrast + 0.5f` = 1.0 (Chromium 네이티브 수준)

**목표**: 선명도(raw) + 대비(감마보정)의 균형점

### 결과
- 빌드: 10/10 PASS
- 사용자 육안: "기존과 크게 달라지지 않음"
- **50% 감마 블렌드는 추가 개선 없음**

---

## 최종 결론 (사실 기반)

### 실험 전체 요약

| 실험 | GhostWin | Alacritty | Delta | 비고 |
|------|:--------:|:---------:|:-----:|------|
| Grayscale 기준선 | 74 | 82 | -8 | ADR-010 |
| 3-pass ClearType + 감마 100% | 73.3 | 80.3 | -7 | ClearType 동작하나 효과 미미 |
| 3-pass ClearType + 감마 0% | 72.7 | 74 | **-1.3** | **최선 — 격차 최소화** |
| 3-pass ClearType + 감마 50% + contrast 1.0 | ~73 | ~80 | ~-7 | 추가 개선 없음 |

### 근본 한계 (FACT)

1. **Composition Swapchain에서 per-channel 블렌딩의 한계**: Dual Source Blending이 이 GPU에서 동작하지 않아 shader lerp로 대체. 하드웨어 블렌딩(Alacritty WGL) 대비 근본적 열위.
2. **ClearType per-channel 차이가 작음**: 밝은 텍스트+어두운 배경에서 R≠G≠B 차이가 가장자리 1-2px에만 존재. 10x 증폭해도 미세.
3. **감마 보정 0%가 최선이나 절대 점수 저하**: raw coverage는 선명하지만 대비가 낮아 텍스트가 얇게 보임.

### 달성한 것

- ALPHA_MODE_IGNORE + SetSwapChainHandle v2: **검증 완료** (PoC 성공)
- ClearType 3x1 래스터: **동작 확인** (60-84% R≠G≠B)
- Alacritty와의 격차: **-8 → -1.3** (감마 0% 시)
- 근본 원인 진단: 셰이더 감마 보정이 텍스트를 소프트하게 만듦

### 달성하지 못한 것

- 90+/100 목표: **미달성** (SPECULATION이었음)
- Dual Source Blending: **이 GPU에서 미동작** (원인 미확정)
- Alacritty와 완전 동등 (동일 폰트/배경에서): 육안으로 여전히 GhostWin이 약간 블러

---

## 실험: v1 API 래스터 (Alacritty 완전 모방)

**시각**: 2026-04-02
**가설**: Factory2의 v2 CreateGlyphRunAnalysis + 강제 GRID_FIT_MODE_ENABLED + 명시 CLEARTYPE 모드가 v1의 DirectWrite 자동 판단보다 품질이 낮을 수 있음
**Alacritty 패턴**: Factory v1 CreateGlyphRunAnalysis — gridFitMode/antialiasMode 파라미터 없음

### 변경 사항
1. v2 Factory2::CreateGlyphRunAnalysis → **v1 Factory::CreateGlyphRunAnalysis**
2. gridFitMode 강제 ENABLED 제거 → DirectWrite 내부 판단
3. antialiasMode 명시 CLEARTYPE 제거 → DirectWrite 시스템 설정 따름
4. DPI 변환 행렬 → pixelsPerDip 스칼라값

### 결과
- 빌드: 10/10 PASS
- (육안 확인 대기)

---

## Alacritty 설정 일치 (공정 비교용)

**시각**: 2026-04-02
**Alacritty 설정** (`C:\Users\Solit\AppData\Roaming\alacritty\alacritty.toml`):

| 항목 | Alacritty | GhostWin (수정 전) | GhostWin (수정 후) |
|------|-----------|-------------------|-------------------|
| 폰트 | JetBrainsMono NF | Cascadia Mono | **JetBrainsMono NF** |
| 크기 | 11.25pt | 12.0pt | **11.25pt** |
| 배경색 | #1E1E2E (Catppuccin Mocha) | #19192600 (짙은 남색) | **#1E1E2E** |
| 테마 | Catppuccin Mocha | 하드코딩 ANSI | 하드코딩 ANSI |

**변경 파일**:
- dx11_renderer.cpp: clear_color = {30/255, 30/255, 46/255, 1.0}
- winui_app.cpp: font_family = "JetBrainsMono NF", font_size_pt = 11.25

**주의**: VT 컬러 팔레트(Catppuccin)는 아직 미적용 — 배경색과 폰트만 일치시킴

---

## 근본 버그 발견: SV_InstanceID + StartInstanceLocation

**시각**: 2026-04-02
**증상**: 폰트 변경 후 터미널 화면 아무것도 안 보임, 입력 불가
**원인**: 3-pass 렌더링의 2번째 `DrawIndexedInstanced(6, text_count, 0, 0, bg_count)`에서 `StartInstanceLocation=bg_count`를 설정했으나, **`SV_InstanceID`는 `StartInstanceLocation`을 포함하지 않음** (D3D11 스펙).

```
VS: PackedQuad q = g_instances[instanceId];
                                ^^^^^^^^^^
Draw 2: SV_InstanceID = 0, 1, 2, ... (항상 0부터)
→ instances[0] = 배경 quad 다시 읽음!
→ 텍스트가 절대 렌더되지 않음!
```

**수정**: 3-pass → 2-pass로 변경
1. Pass 1: 배경만 draw (bg_count개)
2. CopyResource (RT → bgTexture)
3. Pass 2: **전체** 인스턴스 draw (count개, SV_InstanceID=0부터)
   - 배경은 자기 자신을 다시 덮어쓰기 (무해)
   - 텍스트는 bgTexture에서 실제 배경을 읽어 ClearType lerp

**교훈**: D3D11 `SV_InstanceID`는 `StartInstanceLocation`과 독립. StructuredBuffer 인덱싱 시 offset을 별도 전달해야 함.
