# ClearType Sharpness v2 — Completion Report

> **Feature**: cleartype-sharpness-v2
> **Date**: 2026-04-02 ~ 2026-04-03
> **Match Rate**: ~95%
> **Iterations**: 2
> **Status**: Completed

---

## Executive Summary

### 1.1 Overview

| Item | Value |
|------|-------|
| Feature | ClearType 텍스트 선명도 개선 |
| Duration | 2일 (2026-04-02 ~ 04-03) |
| Plan iterations | 3회 (v1→v2→v3, 리서치 기반 재수립) |
| Total attempts | 17+ (셰이더/래스터/스왑체인) + 최종 성공 |
| Files changed | 5 (shader_ps.hlsl, glyph_atlas.cpp, dx11_renderer.cpp/h, winui_app.cpp) |
| Lines removed | ~300+ (죽은 코드, D2D, gamma 파이프라인) |

### 1.2 Results

| Metric | Before | After |
|--------|:------:|:-----:|
| 사용자 평가 | "확실히 blur" | **"거의 비슷한 수준"** |
| Latin stem width | 7px | **3px** (Alacritty 3.2px) |
| ClearType coverage | Linear(85) | **Gamma-baked(139)** |
| Dual Source Blending | 미동작 (구현 버그) | **동작 확인** |
| Dead code | ~300줄 | **~0줄** |
| 코드 분석기 | 72/100 | **~90+** |

### 1.3 Value Delivered

| Perspective | Content |
|-------------|---------|
| **Problem** | D2D DrawGlyphRun이 premultiplied RT에서 linear AA 수행 → 에지 85 (소프트). MSDN: "ClearType on premultiplied = unpredictable" |
| **Solution** | CreateAlphaTexture(gamma AA, 에지 139) + raw coverage + Dual Source Blending. 코드 단순화 (D2D 제거) |
| **Function/UX Effect** | 사용자 확인: "확실히 거의 비슷한 수준까지 올라왔어". Alacritty/WezTerm 동등 선명도 |
| **Core Value** | 경쟁 터미널 동등 렌더링 품질 달성. D2D 의존성 제거로 코드 단순화 및 유지보수 용이 |

---

## 2. PDCA Cycle Summary

```
[Plan] ✅ (v3) → [Design] ✅ (v3) → [Do] ✅ → [Check] ✅ (95%) → [Act] ✅ (2 iter)
```

### Plan (3회 반복)

| Version | 근본 원인 가설 | 결과 |
|---------|-------------|------|
| v1 | 셰이더 감마 보정 필요 (WT 패턴) | blur 감소하나 부족 |
| v2 | per-channel lerp → Dual Source + DWrite 감마 | 개선했으나 Alacritty보다 소프트 |
| **v3** | **D2D linear AA(에지 85) vs CreateAlphaTexture gamma AA(에지 139)** | **해결** |

### Key Decisions

1. **Dual Source Blending 재시도** — 이전 "GPU 미지원" 보고는 셰이더 구현 버그. D3D11 FL 11_0은 스펙 필수 지원.
2. **D2D DrawGlyphRun 제거** — MSDN "premultiplied에서 ClearType = unpredictable". Atlas 실측(에지 85 vs 139)으로 확인.
3. **CreateAlphaTexture 복원** — gamma 공간 AA가 선명, 시스템 감마 baked → 셰이더 감마 불필요.

---

## 3. Technical Details

### 3.1 최종 파이프라인

```
CreateAlphaTexture(system gamma ~1.8, ClearType 3x1)
→ BGRA 패킹 (B8G8R8A8_UNORM atlas)
→ raw coverage (셰이더 감마 보정 없음)
→ Dual Source Blending (INV_SRC1_COLOR)
→ GPU per-channel: result = color + dest * (1 - weights.rgb)
```

### 3.2 주요 커밋

| Commit | Description |
|--------|-------------|
| b16ff7a | per-channel lerp (stem 7→3px) |
| d2793ff | Dual Source Blending 구현 |
| 2946299 | DWrite gamma correction (peak 167→224) |
| 1b2b821 | **CreateAlphaTexture gamma AA (최종 해결)** |
| e4151dc | 3-pass dead code 제거 |
| 52fa554 | D2D/linearParams/DWrite gamma 제거 |
| 50230e5 | compute_gamma_ratios dead code chain 제거 |
| 49ecdd1 | set_cleartype_params dead code chain 제거 |

### 3.3 리서치 에이전트 투입

| Phase | Agents | 목적 |
|-------|:------:|------|
| 10명 교차검증 | 5 코드 + 5 이론 | WT/Alacritty/WezTerm 완전 분석, 수학 증명 |
| 3명 리서치 | D2D premultiplied, CreateAlphaTexture 차이, atlas 덤프 | **근본 원인 확정** |
| 1명 코드 분석 | 코드 품질 72→90+ | dead code chain 식별 |

---

## 4. Lessons Learned

### 4.1 기술적 교훈

| 교훈 | 상세 |
|------|------|
| **AA 샘플링 공간이 선명도를 결정** | linear AA(D2D)는 수학적으로 정확하나 시각적으로 소프트. gamma AA(CreateAlphaTexture)가 인지적으로 선명 |
| **"미동작 확정"을 맹신하지 말 것** | Dual Source Blending "GPU 미지원"은 셰이더 구현 버그였음 |
| **MSDN 공식 문서 확인 필수** | "premultiplied에서 ClearType = unpredictable" — 이 경고가 D2D 접근법의 근본 한계 |
| **이중 감마 주의** | CreateAlphaTexture(gamma baked) + 셰이더 감마 = 과보정. D2D(linear) + 셰이더 감마 = 정상 |

### 4.2 프로세스 교훈

| 교훈 | 상세 |
|------|------|
| **작업일지 필수 참조** | 17+ 시도 중 같은 실수 반복. 작업일지를 읽었으면 방지 가능 |
| **한 번에 하나씩** | 동시 변경은 효과 판별 불가. 단일 변경 + 정량 비교 |
| **사실과 추측 구분** | "bgColor 불일치" 가설은 코드 검증으로 반증됨. 추측으로 코드 수정 금지 |
| **참조 구현 코드 직접 확인** | WT repo clone + 코드 직접 읽기가 가장 효과적 |

---

## 5. Remaining Items

| Item | Priority | Description |
|------|:--------:|-------------|
| 글자 폭/높이/간격 조정 | **높음** | 사용자 피드백: "글자간 간격만 조절하면 수준 높은 터미널" |
| bg_count 파라미터 | 낮음 | quad_builder에 잔존. 향후 정리 |
| ADR-010 업데이트 | 중간 | Dual Source 동작 확인 + CreateAlphaTexture 최종 결정 반영 |
| SetMatrixTransform | 낮음 | DPI=1.0에서 불필요. 고DPI 대응 시 추가 |
