# glyph-metrics Planning Document

> **Summary**: 글리프 폭/높이/간격 조정 기능 — Alacritty·WezTerm·WT 수준의 사용자 제어 가능한 셀 메트릭 시스템
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-03
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 현재 GhostWin은 셀 크기가 폰트 메트릭에 고정되어 있어 글자 간격·줄 높이·글리프 위치를 사용자가 조정할 수 없음. 사용자 피드백: "글자간 간격만 조절하면 수준 높은 터미널" |
| **Solution** | 3개 참조 터미널(Alacritty·WezTerm·WT) 코드리뷰 결과를 기반으로 cell_width/cell_height 스케일, glyph_offset, window_padding, baseline 보정, CJK advance 강제 보정 등 6개 조정 축 구현 |
| **Function/UX Effect** | 폰트별 최적 간격 세팅, 코딩 환경 가독성 향상, CJK 혼합 텍스트 정렬 개선, DPI 변경 시에도 일관된 메트릭 |
| **Core Value** | "내 눈에 맞는 터미널" — 3대 터미널과 동등한 메트릭 조정 능력으로 사용자 만족도 극대화 |

---

## 1. Overview

### 1.1 Purpose

GhostWin의 셀 메트릭 시스템을 개선하여:
1. 사용자가 글자 폭·높이·간격·글리프 위치를 미세 조정할 수 있게 함
2. Baseline 계산을 WT 수준의 lineGap 균등 분배로 개선
3. CJK wide character의 advance 강제 보정으로 정렬 품질 향상
4. Fallback 폰트의 cap-height 기반 스케일링으로 시각적 일관성 확보

### 1.2 Background

Phase 4-C ClearType 선명도(95%) 완료 후 사용자 피드백:
> "글자간 간격만 조절하면 수준 높은 터미널"

현재 GhostWin은 DirectWrite `DWRITE_FONT_METRICS`에서 ascent/descent/lineGap을 읽어 셀 크기를 고정 계산하며, 사용자 조정 파라미터가 없음.

### 1.3 Related Documents

- ClearType 완료 보고서: `docs/archive/2026-04/cleartype-sharpness-v2/`
- DPI-aware 렌더링: `docs/archive/2026-04/dpi-aware-rendering/`
- Phase 4 Master Plan: `docs/01-plan/features/winui3-integration.plan.md`
- ADR-012: CJK Advance-Centering

### 1.4 Reference Terminal Code Review Summary

아래는 3개 터미널의 핵심 구현 비교 (실제 코드리뷰 기반):

---

## 2. 참조 터미널 코드리뷰 결과

### 2.1 셀 크기 계산 비교

| 항목 | Alacritty | WezTerm | Windows Terminal | GhostWin (현재) |
|------|-----------|---------|------------------|-----------------|
| **셀 폭 기준 문자** | `'!'` advance | ASCII 32~127 중 max horiAdvance | `'0'` advance (CSS "ch" 단위) | `'M'` advance |
| **셀 높이 공식** | ascent - descent + lineGap | FT height (y_scale × face.height) | ascent + descent + lineGap | ascent + descent + lineGap |
| **반올림** | `floor()` | 없음 (f64 유지, 정수 변환은 렌더러) | `roundf()` → `lrintf()` | `round(+0.5)` (반올림) |
| **DPI 적용** | font_size × scale_factor → px | pt × dpi / 72 → px | pt / 72 × dpi → px | pt × 96/72 → DIP × dpi_scale |
| **출처** | `crossfont/directwrite/mod.rs:147` | `ftwrap.rs:1088-1136` | `AtlasEngine.api.cpp:734-810` | `glyph_atlas.cpp:208-232` |

### 2.2 사용자 조정 파라미터 비교

| 파라미터 | Alacritty | WezTerm | Windows Terminal | GhostWin (현재) |
|----------|-----------|---------|------------------|-----------------|
| **셀 폭 스케일** | `font.offset.x` (i8 delta px) | `cell_width` (f64 배율, 기본 1.0) | `GetCellWidth().Resolve()` | **없음** |
| **셀 높이 스케일** | `font.offset.y` (i8 delta px) | `line_height` (f64 배율, 기본 1.0) | `GetCellHeight().Resolve()` | **없음** |
| **글리프 X 오프셋** | `font.glyph_offset.x` (i8) | — | — | **없음** |
| **글리프 Y 오프셋** | `font.glyph_offset.y` (i8) | — | — | **없음** |
| **창 패딩** | `window.padding` (x,y) + `dynamic_padding` | `window_padding` (L/T/R/B, Dimension) | 내장 (WinUI3 margin) | **없음** |
| **Underline 위치** | 자동 (descent 기반) | `underline_position` (Dimension) | 자동 (roundf) | 자동 (cell_h - 1) |
| **Underline 두께** | 자동 | `underline_thickness` (Dimension) | 자동 | 1px 고정 |
| **CJK 폭 오버라이드** | — | `cell_widths` [{first, last, width}] | `IsGlyphWideByFont()` 1.2x 임계 | — |
| **Ambiguous Width** | — | `treat_east_asian_ambiguous_width_as_wide` | — | — |
| **Fallback 스케일** | 없음 (동일 size) | `use_cap_height_to_scale_fallback_fonts` | 없음 (advance 보정) | 높이 축소 (em_size 조정) |

### 2.3 Baseline 계산 비교

| 터미널 | Baseline 공식 | lineGap 처리 |
|--------|---------------|-------------|
| **Alacritty** | `cell_bottom - glyph.top` (descent를 top에서 빼서 bottom 기준) | lineGap 전체를 셀 높이에 포함, 균등 분배 없음 |
| **WezTerm** | FreeType descender (f26d6) 직접 사용 | FT face.height에 내포 |
| **Windows Terminal** | `roundf(ascent + (lineGap + adjustedHeight - advanceHeight) / 2)` | **lineGap 상하 균등 분배** ← 핵심 |
| **GhostWin** | `ascent × dpi_scale + 0.5` | lineGap을 셀 높이에만 포함, 분배 없음 |

**WT의 baseline 보정이 가장 정교**: lineGap과 사용자 조정분을 합산하여 상하 절반씩 배분.

### 2.4 CJK Advance 보정 비교

| 터미널 | 방식 | 장점 |
|--------|------|------|
| **Alacritty** | 보정 없음 — 글리프 bearing 그대로 사용 | 단순, 폰트 의존 |
| **WezTerm** | unicode_column_width로 셀 수 결정 + weighted_cell_width 비례 분배 | 클러스터 단위 정확 |
| **Windows Terminal** | `expectedAdvance = (col2-col1) × cellWidth` 강제, 차이를 마지막 글리프에 보정 | 그리드 정렬 보장 |
| **GhostWin** | `(2×cell_w - advance_x) × 0.5` 센터링 (ADR-012) | 대칭 배치 |

---

## 3. Scope

### 3.1 In Scope

- [ ] FR-01: `cell_width_scale` / `cell_height_scale` (f64 배율, 기본 1.0)
- [ ] FR-02: `glyph_offset_x` / `glyph_offset_y` (f32 px delta)
- [ ] FR-03: Baseline lineGap 균등 분배 (WT 패턴)
- [ ] FR-04: CJK advance 강제 보정 (WT 패턴: 논리적 셀 수 × cellWidth)
- [ ] FR-05: `window_padding` (L/T/R/B, px 단위)
- [ ] FR-06: Fallback 폰트 cap-height 기반 스케일링 (WezTerm 패턴)
- [ ] FR-07: 셀 폭 기준 문자 변경 (`'M'` → `'0'` 또는 ASCII max advance)

### 3.2 Out of Scope

- 폰트 선택 UI / 설정 패널 (Phase 5에서 처리)
- HarfBuzz text shaping 통합 (별도 feature로 분리)
- Variable font axis 지원 (별도 feature)
- Ligature 지원 (별도 feature)

---

## 4. Requirements

### 4.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | `cell_width_scale` (f64, 0.5~2.0) / `cell_height_scale` (f64, 0.5~2.0) 배율 파라미터로 셀 크기 조정 | High | Pending |
| FR-02 | `glyph_offset_x` / `glyph_offset_y` (f32 px) 글리프 위치 미세 조정. 셀 크기 불변, 글리프만 이동 | High | Pending |
| FR-03 | Baseline 계산을 `ascent + (lineGap + adjustedH - naturalH) / 2`로 변경 (lineGap 균등 분배) | High | Pending |
| FR-04 | CJK wide char advance를 `셀 수 × cellWidth`로 강제하고, 차이를 센터링 오프셋에 반영 | Medium | Pending |
| FR-05 | `window_padding` (left/top/right/bottom, px) 창 가장자리 패딩 + dynamic padding (나머지 균등 분배) | Medium | Pending |
| FR-06 | Fallback 폰트의 cap-height 비율로 em_size 스케일링 (primary의 cap_height / fallback의 cap_height) | Medium | Pending |
| FR-07 | 셀 폭 기준 문자를 `'0'` (CSS ch 단위)으로 변경하고, 폴백으로 ASCII 32~127 max advance | Low | Pending |

### 4.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| Performance | 메트릭 재계산 < 1ms (DPI 변경, 폰트 변경 시) | 프로파일링 |
| Compatibility | 기존 렌더링 품질 유지 (ClearType, DPI-aware) | 시각 비교 |
| Correctness | 반올림 일관성: 모든 셀 메트릭에 동일한 반올림 전략 적용 | 코드 리뷰 |

---

## 5. Success Criteria

### 5.1 Definition of Done

- [ ] 7개 FR 모두 구현
- [ ] DPI 100%/125%/150%/200%에서 글리프 정렬 정상
- [ ] CJK 혼합 텍스트 (한글+영문+일본어) 그리드 정렬 정상
- [ ] scale 1.0에서 기존과 동일한 렌더링 결과 (regression 없음)
- [ ] 사용자 시각 확인: "간격 조절 가능하고 정렬이 깔끔함"

### 5.2 Quality Criteria

- [ ] Gap Analysis Match Rate ≥ 90%
- [ ] 빌드 성공 (Release + Debug)
- [ ] 코드 리뷰 완료

---

## 6. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Baseline 변경으로 기존 글리프 위치 regression | High | Medium | scale=1.0 기본값에서 기존과 동일 결과 보장하는 단위 테스트 |
| 셀 폭 기준 문자 변경 (`'M'`→`'0'`)으로 일부 폰트에서 레이아웃 깨짐 | Medium | Low | 폴백 로직 (ASCII max advance) 구현, 복수 폰트 검증 |
| Fallback cap-height 스케일링에서 OS/2 cap_height 없는 폰트 | Medium | Medium | cap_height 없으면 스케일링 스킵 (현재 방식 유지) |
| CJK advance 강제 보정과 기존 ADR-012 센터링 충돌 | Medium | Low | ADR-012 로직을 강제 보정 후 적용하도록 순서 조정 |

---

## 7. Architecture Considerations

### 7.1 수정 대상 파일

| 파일 | 변경 내용 |
|------|-----------|
| `src/renderer/glyph_atlas.h` | `AtlasConfig`에 scale/offset 파라미터 추가 |
| `src/renderer/glyph_atlas.cpp` | cell_metrics 계산 로직 변경 (baseline, 셀 폭 기준, scale) |
| `src/renderer/quad_builder.h/cpp` | glyph_offset 적용, CJK advance 강제 보정, window_padding |
| `src/renderer/terminal_window.cpp` | padding 반영한 viewport 계산, dynamic padding |
| `src/app/winui_app.cpp` | 설정 파라미터 전달, grid 계산에 padding 반영 |
| `src/common/render_constants.h` | 기본값 상수 추가 |

### 7.2 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| 반올림 전략 | floor (Alacritty) / round (WT) / none (WezTerm) | `roundf()` (WT 패턴) | 정수 픽셀 경계에 가장 가까운 값, WT가 가장 정교 |
| 셀 폭 기준 | `'M'` / `'0'` / ASCII max | `'0'` + ASCII max 폴백 | CSS ch 단위 표준, WT와 동일 |
| Fallback 스케일 | 동일 size (Alacritty) / cap-height (WezTerm) / advance 보정 (WT) | cap-height + advance 보정 병합 | 시각적 일관성 + 그리드 정확성 |
| 설정 형태 | i8 delta (Alacritty) / f64 배율 (WezTerm) | f64 배율 (기본 1.0) + f32 px offset | 배율이 직관적, offset은 미세 조정용 |

### 7.3 구현 순서

```
Phase 1: 핵심 메트릭 변경 (FR-03, FR-07)
  └─ baseline lineGap 균등 분배 + 셀 폭 기준 변경
  └─ scale=1.0에서 regression 없음 확인

Phase 2: 사용자 조정 파라미터 (FR-01, FR-02)
  └─ cell_width_scale, cell_height_scale
  └─ glyph_offset_x, glyph_offset_y

Phase 3: CJK 보정 + Fallback (FR-04, FR-06)
  └─ CJK advance 강제 보정
  └─ cap-height 기반 fallback 스케일링

Phase 4: Window Padding (FR-05)
  └─ L/T/R/B padding + dynamic padding
```

---

## 8. 기술 이론 참고

### 8.1 OpenType Font Metrics (DirectWrite 경유)

```
DWRITE_FONT_METRICS:
  designUnitsPerEm  — 디자인 단위/em (보통 1000 또는 2048)
  ascent             — baseline 위 (OS/2 sTypoAscender 또는 hhea ascent)
  descent            — baseline 아래 (양수 값)
  lineGap            — 줄 간격 (OS/2 sTypoLineGap)
  capHeight          — 대문자 높이 (OS/2 sCapHeight)

Design units → Pixels 변환:
  pixels = designUnits × (fontSize_px / designUnitsPerEm)
  fontSize_px = fontSize_pt × dpi / 72
```

### 8.2 Baseline 이론

현재 GhostWin:
```
baseline = ascent × dpi_scale  (lineGap 미반영)
cell_h = (ascent + descent + lineGap) × dpi_scale
→ lineGap이 셀 하단에만 몰림 → 글리프가 위로 치우침
```

WT 패턴 (채택):
```
naturalH = ascent + descent + lineGap
adjustedH = naturalH × cell_height_scale  (사용자 조정 후)
baseline = ascent + (lineGap + adjustedH - naturalH) / 2
→ lineGap + 추가 공간을 상하 균등 분배 → 글리프가 셀 중앙에 위치
```

### 8.3 셀 폭 측정 문자

| 터미널 | 문자 | 근거 |
|--------|------|------|
| Alacritty | `'!'` (DW) / `'0'` (FT) | 모노스페이스 가정, 아무 글리프나 동일 |
| WezTerm | ASCII 32~127 max advance | 비정상 폰트 대응 (심볼 폰트 등) |
| WT | `'0'` | CSS `ch` 단위 표준 (W3C 권고) |

**결정**: `'0'` 기준 + ASCII max advance 폴백 (WT + WezTerm 장점 병합)

### 8.4 반올림 전략

| 전략 | 사용처 | 효과 |
|------|--------|------|
| `floor()` | Alacritty 셀 크기 | 글리프가 셀을 초과하지 않음 보장 |
| `roundf()` | WT 모든 메트릭 | 가장 가까운 정수, 시각적 오차 최소 |
| f64 유지 | WezTerm 내부 계산 | 정밀도 유지, 최종 렌더링에서만 정수화 |

**결정**: 내부 계산은 f64, 최종 셀 크기와 baseline은 `roundf()` (WT 패턴)

---

## 9. Next Steps

1. [ ] Design 문서 작성 (`glyph-metrics.design.md`)
2. [ ] 구현 Phase 1: baseline + 셀 폭 기준 변경
3. [ ] 구현 Phase 2~4: 순차 진행
4. [ ] Gap Analysis

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-03 | Initial draft — 3-terminal code review + technical theory | 노수장 |
