# glyph-metrics Completion Report

> **Feature**: glyph-metrics (글리프 폭/높이/간격 조정)
> **Date**: 2026-04-03
> **Status**: Completed
> **Match Rate**: 93%

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 셀 크기가 폰트 메트릭에 고정, baseline lineGap 미분배, 사용자 간격 조정 불가. 사용자 피드백: "글자간 간격만 조절하면 수준 높은 터미널" |
| **Solution** | Alacritty·WezTerm·WT 3개 터미널 코드리뷰 기반으로 6개 조정 축 구현: cell scale, glyph offset, baseline 균등 분배, CJK advance 보정, window padding, fallback cap-height 스케일링 |
| **Function/UX Effect** | 셀 폭/높이 배율 조정, 글리프 미세 위치 이동, lineGap 균등 분배로 자연스러운 수직 중앙 배치, CJK/영문 혼합 그리드 정렬 유지 |
| **Core Value** | 3대 참조 터미널과 동등한 메트릭 조정 능력. 기본값에서 regression 없이 시각 검증 통과 |

### 1.3 Value Delivered

| Metric | Value |
|--------|-------|
| Match Rate | **93%** (26항목 중 24 일치) |
| 수정 파일 | 4개 (glyph_atlas.h/cpp, quad_builder.h/cpp) |
| 추가 코드 | ~120줄 (헬퍼 함수 3개 + compute_cell_metrics 재작성) |
| 삭제 코드 | ~10줄 (기존 compute_cell_metrics) |
| 빌드 | Release 빌드 성공, 10/10 테스트 통과 |
| 시각 검증 | WT·Alacritty와 비교 — 동등 수준 확인 |

---

## 1. PDCA Cycle Summary

```
[Plan] ✅ → [Design] ✅ → [Do] ✅ → [Check] ✅ 93% → [Report] ✅
```

| Phase | 산출물 | 상태 |
|-------|--------|:----:|
| Plan | `docs/01-plan/features/glyph-metrics.plan.md` | 완료 |
| Design | `docs/02-design/features/glyph-metrics.design.md` | 완료 |
| Do | 4개 파일 수정 (4-Phase 구현) | 완료 |
| Check | `docs/03-analysis/glyph-metrics.analysis.md` — 93% | 완료 |
| Report | 본 문서 | 완료 |

---

## 2. Research Phase

### 2.1 3개 터미널 코드리뷰

| 터미널 | 소스 | 핵심 발견 |
|--------|------|-----------|
| **Alacritty** | `external/al-ref/` (로컬) | `floor()` 반올림, `font.offset`(셀 크기) vs `font.glyph_offset`(위치만) 분리, CJK 별도 센터링 없음 |
| **WezTerm** | `external/wez-ref/` (로컬 clone) | FreeType 기반, ASCII 32~127 max advance, `line_height`/`cell_width` f64 배율, cap-height 기반 fallback 스케일링 |
| **Windows Terminal** | `external/wt-ref/` (로컬) | `roundf()` 반올림, `'0'` 기준 셀 폭 (CSS ch), baseline lineGap 균등 분배, CJK advance 강제 보정 |

### 2.2 채택 결정

| 항목 | 채택 원천 | 근거 |
|------|-----------|------|
| 반올림 | WT (`roundf`) | 시각 오차 최소 |
| 셀 폭 기준 | WT (`'0'`) + WezTerm (ASCII max 폴백) | CSS 표준 + 비정상 폰트 대응 |
| Baseline | WT (lineGap 균등 분배) | 가장 정교한 수직 중앙 배치 |
| 사용자 설정 | WezTerm (f64 배율) + Alacritty (px offset) | 배율 직관 + 미세 조정 |
| Fallback 스케일 | WezTerm (cap-height 비율) | 시각적 일관성 |

---

## 3. Implementation Details

### 3.1 수정 파일

| 파일 | 변경 내용 | LOC |
|------|-----------|:---:|
| `src/renderer/glyph_atlas.h` | AtlasConfig에 5필드 추가 (scale, offset, cap-height) | +10 |
| `src/renderer/glyph_atlas.cpp` | `compute_cell_metrics()` 재작성, 헬퍼 3개 추가, fallback 로직 변경 | +80/-10 |
| `src/renderer/quad_builder.h` | 생성자에 offset/padding 파라미터 추가 | +8 |
| `src/renderer/quad_builder.cpp` | glyph_offset/padding 적용, CJK 주석 업데이트 | +12/-6 |

### 3.2 FR 구현 현황

| FR | 항목 | 구현 상태 | Match |
|----|------|:---------:|:-----:|
| FR-01 | cell_width_scale / cell_height_scale | 완료 | 100% |
| FR-02 | glyph_offset_x / glyph_offset_y | 완료 | 100% |
| FR-03 | Baseline lineGap 균등 분배 | 완료 | 100% |
| FR-04 | CJK advance 센터링 + offset | 완료 | 100% |
| FR-05 | Window Padding (파라미터) | 부분 | 50% |
| FR-06 | Fallback cap-height 스케일링 | 완료 | 100% |
| FR-07 | 셀 폭 기준 '0' + 폴백 | 완료 | 100% |

### 3.3 핵심 알고리즘

**Baseline (WT 패턴)**:
```
natural_h = ascent + descent + lineGap
adjusted_h = natural_h × cell_height_scale
extra = lineGap + adjusted_h - natural_h
baseline = roundf(ascent + extra / 2)
```

**셀 폭 (3단계 폴백)**:
```
1. '0' advance (CSS ch, WT 패턴)
2. ASCII 0x21~0x7E max advance (WezTerm 패턴)
3. 'M' advance (최후 폴백)
```

**Cap-height Fallback (WezTerm 패턴)**:
```
primary_cap = DWRITE_FONT_METRICS1.capHeight × scale × dpi
fallback_cap = same for fallback font
em_size × = primary_cap / fallback_cap
```

---

## 4. Gap Analysis Summary

| Category | Items | Matched | Rate |
|----------|:-----:|:-------:|:----:|
| FR (기능) | 23 | 21 | 91% |
| NFR (비기능) | 3 | 3 | 100% |
| **Total** | **26** | **24** | **93%** |

### 잔여 Gap (Phase 5로 이관)

| Gap | 설명 | 해결 시점 |
|-----|------|-----------|
| G-01 | scale clamp (0.5~2.0) | Phase 5 설정 패널 입력 검증 |
| G-02 | WindowPadding 구조체 | Phase 5 설정 구조 정의 시 |
| G-03 | winui_app grid에 padding 반영 | Phase 5 패딩 설정 활성화 시 |
| G-04 | Dynamic padding | Phase 5 창 리사이즈 핸들링 |

---

## 5. Verification

| # | 항목 | 결과 |
|---|------|:----:|
| V-01 | Release 빌드 | PASS |
| V-02 | 10/10 기존 테스트 | PASS |
| V-03 | 시각 검증 — 영문+한글+숫자 혼합 | PASS |
| V-04 | WT/Alacritty와 비교 — 동등 수준 | PASS |
| V-05 | Nerd Font 아이콘 렌더링 | PASS |
| V-06 | CJK 그리드 정렬 | PASS |

---

## 6. Lessons Learned

1. **에이전트 타임아웃 대응**: 병렬 에이전트가 6~8분 이상 걸릴 때 사용자에게 진행 보고가 없으면 중단됨. 로컬 파일이 있으면 직접 분석이 더 빠름.
2. **Windows `max` 매크로 충돌**: `std::max()` 대신 삼항 연산자로 우회. Windows 헤더의 `max/min` 매크로는 C++ 코드와 충돌.
3. **3개 터미널 비교의 가치**: 각 터미널이 다른 전략을 쓰는 이유를 이해하면 최적 조합을 선택할 수 있음. WT(정확성) + WezTerm(유연성) + Alacritty(단순성)의 장점 병합.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-03 | Completion report | 노수장 |
