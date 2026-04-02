# ClearType Composition 작업일지

> **Feature**: ClearType 서브픽셀 렌더링
> **시작일**: 2026-04-02
> **현재 점수**: GhostWin ~78-80 vs Alacritty ~82-85 (블라인드 실측)
> **상태**: 진행 중 — sRGB 감마 + NATURAL 모드로 개선. per-channel blend 한계 잔존. HWND child 시도 예정

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

### 근본 원인 발견: sRGB 감마 커브 미적용

**실험**: sRGB RTV (B8G8R8A8_UNORM_SRGB) 적용 → 색상은 깨졌지만 글리프 가장자리 선명
**원인**: raw coverage 값이 linear space에서 너무 flat → 가장자리 전환이 완만 → 블러 인상
**해결**: `pow(coverage, 1/2.2)` = sRGB 감마 인코딩을 coverage에만 적용 → 가장자리 steepen

| 감마 | 효과 | 평가 |
|------|------|:----:|
| 1.0 (raw) | 평탄, 블러 | 72-74 |
| 0.6 | 약간 개선 | ~76? |
| 0.5 (sqrt) | 두껍지만 선명 | ~80? |
| **0.4545 (sRGB)** | **Alacritty 감마와 동일** | 사용자: "많이 개선" |

**커밋**: `3622941` — pow(coverage, 0.4545) 적용

---

## Atlas 텍스처 덤프 결과 (FACT 확정)

Atlas에 저장된 글리프: **선명**. ClearType RGB 프린지 확인됨.
BBox: (0,0)-(600,14), 989 non-zero pixels.

→ **글리프 데이터 자체에 블러 없음**
→ **블렌딩 단계에서 per-channel 정보 손실이 남은 블러의 원인**

### 최종 진단

| 단계 | 상태 | 증거 |
|------|:----:|------|
| ① 래스터 | 정상 | 바이트 덤프 R≠G≠B |
| ② Atlas 업로드 | **정상** | Atlas 덤프에서 글리프 선명 |
| ③ 셰이더 샘플링 | 정상 | point sampling, UV 정확 |
| ④ 블렌딩 | **문제** | `ONE/INV_SRC_ALPHA` 단일 alpha → per-channel 손실 |
| ⑤ Composition | 정상 | stripe 테스트 선명 |

**결론**: Dual Source Blending(per-channel hardware blend) 없이는 Alacritty와 완전 동등 불가.
**다음 시도**: HWND child 방식 (Alacritty와 동일 구조)

**커밋**: `3622941` — pow(coverage, 0.4545) 적용
**블라인드 평가**: 진행 중

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

---

## 실험: per-channel lerp + 선형 공간 블렌딩 (2026-04-02 계속)

**가설 1**: 단일 alpha premultiplied → per-channel lerp로 변환하면 blur 해소
**가설 2**: sRGB 공간 lerp → 선형 공간 lerp (GL_FRAMEBUFFER_SRGB 동등)

### 시도 1: per-channel lerp + sRGB 감마 제거
```hlsl
float3 coverage = glyph.rgb;  // raw, no pow(0.4545)
float3 result = lerp(input.bgColor.rgb, input.fgColor.rgb, coverage);
return float4(result, 1.0);
```
**결과**: blur 변화 없음

### 시도 2: per-channel lerp + 선형 공간 블렌딩
```hlsl
float3 bgLinear = pow(bgColor.rgb, 2.2);
float3 fgLinear = pow(fgColor.rgb, 2.2);
float3 resultLinear = lerp(bgLinear, fgLinear, coverage);
float3 result = pow(resultLinear, 1.0/2.2);
return float4(result, 1.0);
```
**결과**: blur 변화 없음

### 관찰
- 3번의 셰이더 변경(단일alpha, per-channel sRGB, per-channel linear)에서 시각적 차이 없음
- **가능성**: 셰이더가 실제로 로드되지 않고 있거나, blur 원인이 셰이더 이전/이후 단계
- **진단**: 텍스트를 solid RED로 출력하는 테스트 추가 → 셰이더 로딩 확인

### 런타임 로그 확인 (DPI/스케일링)
```
SwapChainPanel: 1136x574
Swapchain: 1136x574 (IGNORE)
DPI scale: 1.00
Cell: 9x20
```
→ DPI 불일치 없음, 스왑체인 크기 일치 확인

### 시도 3: 셰이더 로딩 진단 (solid RED)
- `if (coverage > 0.01) return float4(1,0,0,1)` → **빨간색 텍스트 확인**
- **셰이더 정상 로딩/컴파일 확인**

### 시도 4: 이진 coverage (step 0.5) — BLUR 원인 확정
- `step(0.5, glyph.rgb)` → AA 완전 제거, 0 또는 1만 출력
- **결과: 텍스트 매우 선명!** ClearType 컬러 프린지 정상, 가장자리 깨끗
- **결론**: blur는 AA 가장자리 픽셀의 블렌딩에서 발생
- 글리프 코어, 위치, Atlas, Composition 모두 정상 확인

### BLUR 원인 최종 확정 (FACT)

| 단계 | 상태 | 증거 |
|------|:----:|------|
| ① 래스터 | ✅ | 이진 coverage에서 sharp |
| ② Atlas | ✅ | 덤프 확인 |
| ③ UV/위치 | ✅ | 이진에서 위치 정확 |
| ④ AA 가장자리 블렌딩 | **❌ 원인** | 이진→sharp, 연속→blurry |
| ⑤ Composition | ✅ | stripe + 이진 테스트 |

**근본 원인**: DWrite coverage 값의 가장자리 gradients가 너무 완만 → 가장자리 픽셀이 bg와 fg의 중간색으로 넓게 퍼짐 → blur 인상

### 시도 5: smoothstep 가장자리 sharpening
- `smoothstep(0.1, 0.7, coverage)` + per-channel lerp in linear space
- coverage를 급격하게 만들어 가장자리를 steepen하면서 AA 유지
- **결과**: blur 약간 개선, 하지만 색상 아티팩트 발생 (ClearType 채널별 차이 증폭)

### 시도 6: NATURAL 래스터 모드 + raw per-channel lerp
- `DWRITE_RENDERING_MODE_DEFAULT` → `DWRITE_RENDERING_MODE_NATURAL` (tighter hinting)
- 셰이더: raw coverage, 단순 per-channel lerp (감마/보정 없음)
- **결과**: blur 개선, 하지만 **색상 대비(contrast) 부족** — 텍스트가 씻겨나간 듯 옅음

### 시도 7: DWrite 3단계 보정 + per-channel lerp
- EnhanceContrast + AlphaCorrection + LightOnDarkAdjustment (WT 패턴)
- **결과**: 텍스트 **과도하게 bold** — 뭉개진 느낌

### 시도 8: EnhanceContrast만 적용
- AlphaCorrection, LightOnDark 제거. EnhanceContrast(k=0.50)만 유지
- **결과**: 아직 blur 가장 심함 (3종 비교: WezTerm > Alacritty > GhostWin)

### 백버퍼 덤프 결과 (FACT 확정)
- Present() 직전 백버퍼를 BMP로 덤프
- **백버퍼 자체가 blurry → DWM/Composition은 원인이 아님 (FACT)**
- 이전 "DWM이 중간값 처리" 가설은 **반증됨**

### 백버퍼 픽셀 분석 (FACT)
```
x=45: (30, 30, 46)  ← 배경
x=46: (142,210,255)  ← ClearType 좌 프린지 (R=0.50, G=0.80, B=1.00)
x=47: (255,210,150)  ← ClearType 우 프린지 (R=1.00, G=0.80, B=0.50)
x=48: (30, 30, 46)  ← 배경
```
- 가장자리 transition은 **2픽셀** — coverage 자체는 sharp
- "blur"로 인식되는 것은 **ClearType 컬러 프린지**가 어두운 배경에서 눈에 띄는 현상
- **수학적 검증**: sRGB lerp → 프린지 (142,210,255), linear lerp → (188,231,255)
- 선형 공간 블렌딩이 프린지를 덜 눈에 띄게 만듦 (흰색에 더 가까움)

### 시도 9: NATURAL + 선형 공간 per-channel lerp
- 가설: NATURAL 모드(sharp hinting) + linear space(fringe 감소) 조합이 최적
- **결과**: blur 변화 없음. NATURAL 모드가 아이콘 일그러짐 유발 → DEFAULT로 복원

### 시도 10: Grayscale AA 전환
- ClearType 3x1 → ALIASED_1x1 (단일 alpha) 강제
- **결과**: blur 변화 없음. ClearType 프린지가 원인이 아님 확인

### 시도 11: DXGI_SCALING_NONE
- 스왑체인 desc.Scaling 기본값 STRETCH → NONE 변경
- **결과**: blur 변화 없음

### 시도 12: IDXGISwapChain2::SetMatrixTransform (WT 패턴)
- XAML RenderTransform 대신 DXGI 레벨 역스케일 변환
- WT 소스 (AtlasEngine.r.cpp:427-431)에서 확인한 패턴
- **결과**: blur 변화 없음

### 시도 13: 텍스처 Load + DWrite 감마 파이프라인 (WT 패턴)
- `Sample(pointSamp, uv)` → `Load(int3(uv, 0))` (정수 텍셀 로드)
- VS: 정규화 UV → 픽셀 좌표 전달
- DWrite EnhanceContrast + ApplyAlphaCorrection + gammaRatios 적용
- **결과**: blur 변화 없음. **시도 7과 동일하게 과도 bold → 이중 감마 의심**

---

## 근본 원인 확정: 이중 감마 (Double Gamma) — WT 코드 비교로 확정

### WT 소스 분석 (microsoft/terminal, 직접 clone하여 확인)

**WT 래스터라이즈 (dwrite_helpers.cpp:37)**:
```cpp
factory->CreateCustomRenderingParams(
    1.0f,   // gamma = 1.0 (LINEAR) ← 핵심
    0.0f,   // enhancedContrast = 0.0
    0.0f,   // grayscaleEnhancedContrast = 0.0
    defaultParams->GetClearTypeLevel(),
    defaultParams->GetPixelGeometry(),
    defaultParams->GetRenderingMode(),
    &linearParams);
// → D2D DrawGlyphRun에 linearParams 적용
// → 결과: gamma 미적용 raw linear coverage
```

**WT 셰이더 (shader_ps.hlsl:59-69)**:
```hlsl
case SHADING_TYPE_TEXT_CLEARTYPE:
    glyph = glyphAtlas[data.texcoord];  // integer Load
    contrasted = DWrite_EnhanceContrast3(glyph.rgb, blendEnhancedContrast);
    alphaCorrected = DWrite_ApplyAlphaCorrection3(contrasted, data.color.rgb, gammaRatios);
    weights = float4(alphaCorrected * data.color.a, 1);
    color = weights * data.color;
    // → Dual Source Blending: dest * (1 - weights.rgb)
```

**WT 파이프라인**:
```
linear coverage (gamma=1.0) → EnhanceContrast → AlphaCorrection(gammaRatios) → Dual Source Blend
```

### GhostWin 파이프라인 (현재):
```
gamma=1.8 baked coverage (Factory v1, system defaults)
→ EnhanceContrast → AlphaCorrection(gammaRatios) → per-channel lerp
= 이중 감마! (시스템 gamma 1.8 + 셰이더 gamma 보정)
```

### 핵심 차이

| 항목 | WT | GhostWin |
|------|-----|---------|
| 래스터 API | **D2D DrawGlyphRun** | CreateGlyphRunAnalysis (Factory v1) |
| 래스터 감마 | **1.0 (linear)** via linearParams | 1.8 (system) — 파라미터 전달 불가 |
| 셰이더 감마 | DWrite 보정 (올바름) | DWrite 보정 (**이중 감마!**) |
| 블렌딩 | **Dual Source** (GPU per-channel) | 셰이더 lerp |

### 왜 Factory v1에서는 해결 불가능한가

`IDWriteFactory::CreateGlyphRunAnalysis`의 시그니처:
```cpp
HRESULT CreateGlyphRunAnalysis(
    DWRITE_GLYPH_RUN const*,  // glyph run
    FLOAT,                     // pixelsPerDip
    DWRITE_MATRIX const*,      // transform
    DWRITE_RENDERING_MODE,     // rendering mode
    DWRITE_MEASURING_MODE,     // measuring mode
    FLOAT, FLOAT,              // baseline origin
    IDWriteGlyphRunAnalysis**  // output
);
```
**IDWriteRenderingParams 파라미터가 없음** → gamma=1.0 linearParams 전달 불가

### 해결책: D2D DrawGlyphRun 전환

WT와 동일하게:
1. D2D 렌더 타겟을 Atlas 텍스처 위에 생성
2. `SetTextRenderingParams(linearParams)` 적용
3. `DrawGlyphRun()` 호출 → gamma=1.0 linear coverage 생성
4. 셰이더에서 DWrite 감마 파이프라인 정상 적용 (이중 감마 없음)

### 시도 14: D2D DrawGlyphRun + linearParams (WT 패턴 완전 구현)

**변경 사항:**
1. Atlas 텍스처: `R8G8B8A8_UNORM` → `B8G8R8A8_UNORM` + `D3D11_BIND_RENDER_TARGET`
2. D2D 렌더 타겟: `CreateDxgiSurfaceRenderTarget` (Atlas 텍스처 위)
3. `SetTextRenderingParams(linearParams)` — gamma=1.0, enhancedContrast=0.0 (WT 동일)
4. `DrawGlyphRun()` — CreateGlyphRunAnalysis + CreateAlphaTexture 대체
5. `SetTextAntialiasMode(CLEARTYPE)`
6. linearParams: `gamma=1.0, contrast=0.0, gsContrast=0.0, ctLevel=system` (WT dwrite_helpers.cpp:37 동일)
7. 셰이더: `Load(int3(uv,0))` + DWrite EnhanceContrast + ApplyAlphaCorrection + per-channel lerp

**참고 소스:** microsoft/terminal (직접 clone하여 분석)
- `src/renderer/atlas/dwrite_helpers.cpp:26-38` — DWrite_GetRenderParams
- `src/renderer/atlas/BackendD3D.cpp:841-871` — D2D RT 생성
- `src/renderer/atlas/BackendD3D.cpp:1541-1553` — DrawGlyphRun
- `src/renderer/atlas/shader_ps.hlsl:59-69` — ClearType 셰이더

**결과:** 텍스트 렌더링 정상 동작. **하지만 blur 개선 없음.**

**이중 감마 가설 → 반증됨.** linearParams(gamma=1.0)로 D2D 래스터라이즈해도 blur가 동일하게 발생.

---

## 세션 종합 결론 (2026-04-02)

### 확정된 FACT

| 항목 | 결과 | 증거 |
|------|------|------|
| 셰이더 정상 동작 | ✅ | 빨간색 진단 테스트 |
| 이진 coverage → sharp | ✅ | step(0.5) 출력에서 선명 |
| 백버퍼 픽셀 transition 2px | ✅ | BMP 덤프 분석 |
| DWM/Composition blur 아님 | ✅ | 백버퍼 자체가 blurry |
| DPI/스케일링 불일치 없음 | ✅ | 로그: DPI=1.0, 크기 일치 |
| ClearType/Grayscale 무관 | ✅ | 둘 다 동일 blur |
| 이중 감마 아님 | ✅ | D2D linearParams 적용해도 동일 |

### 반증된 가설

| 가설 | 반증 근거 |
|------|----------|
| 셰이더 블렌딩이 blur 원인 | 8종 블렌딩 변경 모두 효과 없음 |
| ClearType 컬러 프린지가 원인 | Grayscale 전환해도 동일 |
| DXGI_SCALING_STRETCH가 원인 | NONE으로 변경해도 동일 |
| SwapChainPanel RenderTransform 누락 | SetMatrixTransform 추가해도 동일 |
| 이중 감마 (Factory v1 gamma=1.8) | D2D linearParams (gamma=1.0) 적용해도 동일 |
| 텍스처 UV 정밀도 문제 | Load(int3) 정수 로드로 변경해도 동일 |

### 미해결: Blur 근본 원인

blur의 근본 원인은 **확정되지 않았음.** 모든 glyph 래스터라이즈/셰이더/스왑체인 변경이 blur에 영향을 주지 않음.

**남은 미검증 가설:**
1. **Dual Source Blending 부재** — WT는 `D3D11_BLEND_INV_SRC1_COLOR`로 GPU 하드웨어 per-channel 블렌딩. GhostWin은 셰이더 lerp. 이 GPU에서 Dual Source 미동작 확인됨. 수학적으로 동등해야 하나, 실제로 차이가 있는지 pixel-level 비교 필요.
2. **WinUI3 Composition 파이프라인 고유 특성** — WT도 SwapChainPanel 사용하므로 동일 경로여야 하나, WT의 내부 구현이 다를 가능성. WT 디버그 모드로 실행하여 비교 필요.

### 다음 세션 작업 (우선순위)

| 순위 | 작업 | 목적 |
|:----:|------|------|
| 1 | WT vs GhostWin 동일 글리프 pixel-level 비교 | blur 차이의 정확한 수치 확인 |
| 2 | WT 디버그 빌드 실행, atlas 덤프 비교 | 래스터라이즈 결과 차이 확인 |
| 3 | Dual Source Blending 재시도 (다른 GPU?) | 하드웨어 per-channel 블렌딩 테스트 |
| 4 | HWND child PoC | Composition 우회 테스트 |

### 현재 코드 상태

**적용된 개선 (유지):**
- D2D DrawGlyphRun 래스터라이즈 (WT 패턴)
- linearParams (gamma=1.0, contrast=0.0)
- Atlas B8G8R8A8 + D2D RT
- DXGI_SCALING_NONE
- SetMatrixTransform (DXGI 레벨 역보상)
- Load(int3) 정수 텍셀 로드
- DWrite 감마 파이프라인 (EnhanceContrast + AlphaCorrection)
- per-channel lerp with bgColor

### 시도 전체 목록

| # | 시도 | 결과 |
|---|------|:----:|
| 1 | per-channel lerp (sRGB) | ✗ |
| 2 | per-channel lerp (linear) | ✗ |
| 3 | smoothstep sharpening | ✗ 색상 아티팩트 |
| 4 | NATURAL 래스터 모드 | ✗ 아이콘 일그러짐 |
| 5 | raw coverage (감마 제거) | ✗ 대비 부족 |
| 6 | DWrite 3단계 보정 | ✗ 과도 bold |
| 7 | EnhanceContrast만 | ✗ |
| 8 | Grayscale AA 전환 | ✗ |
| 9 | DXGI_SCALING_NONE | ✗ |
| 10 | SetMatrixTransform (WT 패턴) | ✗ |
| 11 | Load(int3) + DWrite 감마 | ✗ |
| 12 | DEFAULT → NATURAL_SYMMETRIC 폴백 | ✗ |
| 13 | D2D DrawGlyphRun + linearParams | ✗ |
| — | 이진 coverage (step 0.5) | ✓ sharp! (AA 제거) |
| — | solid RED 진단 | ✓ 셰이더 동작 확인 |
| — | 백버퍼 BMP 덤프 | ✓ 진단 도구 |
| 14 | D2D DrawGlyphRun + linearParams | ✗ blur 동일 |
| 15 | Alacritty 렌더링 모드 (GetRecommendedRenderingMode v1) | ✗ |
| 16 | glyphAdvances = 0.0 (Alacritty 패턴) | ✗ |
| 17 | Mica 배경 비활성화 | ✗ |

---

## 코드 복원 (세션 중간 정리)

16+ 회 변경이 모두 blur에 영향 없었으므로 **원래 코드로 복원** (`git checkout -- src/`).
작업일지만 보존. 다른 접근 방식으로 재시도.

### 복원된 코드 상태 (세션 시작 전과 동일)
- 셰이더: `pow(coverage, 0.4545)` + premultiplied alpha (단일 alpha)
- Atlas: R8G8B8A8_UNORM, CreateGlyphRunAnalysis + CreateAlphaTexture (CPU)
- 스왑체인: FLIP_SEQUENTIAL, ALPHA_MODE_IGNORE, Mica 활성
- 텍스처 샘플링: `Sample(pointSamp, uv)` (float UV)

### Alacritty/WT repo 분석으로 확인된 FACT (코드 직접 확인)
- **WT**: D2D DrawGlyphRun + linearParams(gamma=1.0) + Dual Source Blending + `glyphAtlas[texcoord]` (Load)
- **Alacritty**: CreateGlyphRunAnalysis + `glyphAdvances=0` + `GetRecommendedRenderingMode(v1, null params)` + WGL per-channel blend
- **WT/Alacritty 모두 SwapChainPanel이 아닌 방식** 사용 → WT는 SwapChainPanel 사용 (확인됨)

### 스크린샷 픽셀 비교 FACT (image 23)
| | Alacritty stem | GhostWin stem |
|---|:-:|:-:|
| 폭 | 3px | 7px |
| 패턴 | bg→fringe→core→fringe→bg | bg→gradual→fringe→core→core→fringe→gradual→bg |

**근본 미해결 질문**: DWrite 파라미터가 동일한데 왜 stem 폭이 2배 차이나는가?

---

## 해결: per-channel lerp + 선형 공간 블렌딩 (2026-04-02)

### 근본 원인 확정

원래 셰이더의 `alpha = max(R,G,B)` premultiplied 블렌딩이 **낮은 coverage 채널의 배경을 과도하게 억제**:

```
coverage = (0.1, 0.3, 0.5)
alpha = max = 0.5
final.r = fg.r * 0.1 + dest.r * (1 - 0.5)  ← dest 0.5 (정답: 0.9)
  → R 채널 배경이 58% 부족 → 가장자리 밝아짐 → blur 인상
```

### 해결 코드 (shader_ps.hlsl)

```hlsl
float3 bgL = pow(max(input.bgColor.rgb, 0.001), 2.2);  // sRGB → linear
float3 fgL = pow(max(input.fgColor.rgb, 0.001), 2.2);
float3 resultL = lerp(bgL, fgL, glyph.rgb);             // per-channel lerp
float3 result = pow(max(resultL, 0.0), 1.0 / 2.2);      // linear → sRGB
return float4(result, 1.0);
```

### 정량적 결과 (스크린샷 픽셀 비교)

| 상태 | GhostWin Latin stem | Alacritty | 격차 |
|------|:---:|:---:|:---:|
| 원래 (max alpha premul) | 7.0px | 2.8px | -4.2px |
| per-channel lerp (sRGB) | 4.0px | 2.8px | -1.2px |
| **per-channel lerp (linear)** | **3.0px** | **3.2px** | **+0.2px** |

**Latin 글리프: 원래 대비 95% 격차 해소. Alacritty와 동등 수준 달성.**

### 왜 이전 per-channel lerp 시도가 효과 없어 보였는가

이전에도 per-channel lerp를 시도했지만 (시도 1-2) 효과 없다고 판단했음. 그러나:
1. 당시에는 정량적 픽셀 비교를 하지 않아 개선을 감지하지 못함
2. D2D/atlas 포맷/스왑체인 등 다른 변경이 동시에 적용되어 효과가 혼재됨
3. 원래 코드 복원 후 **딱 하나만** 변경해서 비교했을 때 개선 확인

### 미해결: CJK 선명도

CJK stem 폭: GhostWin 10-16px vs Alacritty 6-7px. 별도 개선 필요.
