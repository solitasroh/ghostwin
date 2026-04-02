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

### 2.1 In Scope (WT 코드 리뷰로 확인된 3가지 정확한 차이)

- [x] **차이 1**: CreateGlyphRunAnalysis API 버전 (Factory1 vs Factory2/3)
  - WT: Factory2/3 사용 → `gridFitMode` + `antialiasMode` 파라미터 전달
  - GhostWin: Factory1 사용 → 파라미터 전달 불가
  - 영향: DWrite가 다른 coverage를 생성할 수 있음
  - **수정**: Factory3 `CreateGlyphRunAnalysis` 사용 + `gridFitMode`, `antialiasMode` 전달

- [x] **차이 2**: 셰이더 감마 보정
  - WT: `EnhanceContrast3()` + `ApplyAlphaCorrection3()` + `gammaRatios` 적용
  - GhostWin: Raw coverage (감마 보정 없음)
  - **주의**: 이전 "감마가 소프트" 결론은 per-channel lerp 맥락. Dual Source에서는 다를 수 있음
  - **수정**: WT와 동일한 DWrite 감마 파이프라인 적용 (Dual Source 블렌딩과 함께)

- [x] **차이 3**: GetRecommendedRenderingMode
  - WT: linearParams 전달하여 렌더링 모드 결정
  - GhostWin: 호출 안 함 (RENDERING_MODE_DEFAULT 하드코딩)
  - **수정**: WT와 동일하게 GetRecommendedRenderingMode 호출 + linearParams 전달

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

### 3.2 실행 순서

| 순서 | 변경 | 파일 | 검증 방법 |
|:----:|------|------|----------|
| 1 | Factory3 CreateGlyphRunAnalysis + gridFitMode/antialiasMode | glyph_atlas.cpp | 스크린샷 비교 |
| 2 | DWrite 감마 보정 복원 (Dual Source 맥락) | shader_ps.hlsl | 스크린샷 비교 |
| 3 | GetRecommendedRenderingMode + linearParams | glyph_atlas.cpp | 스크린샷 비교 |

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
