# ADR-012: CJK Advance-Centering for Glyph Spacing

## Status
Accepted (2026-04-01)

## Context
CJK 한글 문자가 2-cell 배경 내에서 gap이 발생하는 문제.

### 진단 데이터
```
cell_w = 9 (from Cascadia Mono 'M' advance)
2 * cell_w = 18px
CJK raw_advance = 16.0px (Malgun Gothic at dip_size)
CJK actual_advance = 14.3px (after height-based fallback scaling x0.893)
gap per char = 18 - 14.3 = 3.7px
```

### 시도한 접근법

| 시도 | 결과 |
|------|------|
| em_size 확대 (x1.125) | 간격 OK but 글자 26% 너무 큼 |
| DWrite 수평 스케일 (DWRITE_MATRIX m11) | 비율 왜곡으로 품질 저하 |
| ink-bbox 센터링 | 양쪽 gap 균등하지만 부자연스러움 (revert됨) |

## Decision
두 가지 변경을 조합:

1. **CJK wide는 높이 축소 건너뛰기** (glyph_atlas.cpp)
   - fallback 폰트의 ascent+descent가 cell_h보다 커서 em_size를 축소하면
     advance도 같이 줄어듦 (16→14.3)
   - CJK wide에 한해 축소를 건너뛰어 advance = 16 유지
   - 세로 오버플로우는 quad_builder에서 cell-height clipping으로 처리

2. **Advance-centered positioning** (quad_builder.cpp)
   - 글리프의 advance width를 2-cell span 내에서 중앙 배치
   - `centering = (cell_span - advance_x) * 0.5f`
   - bearing 비율 보존: `glyph_x = px + centering + offset_x`
   - gap: 3.7px → 2px → 센터링 후 1px 좌/우

### 근거
- Alacritty도 동일한 gap 존재 (font metric 차이의 자연 현상)
- 비율 왜곡 없이 gap을 최소화하는 가장 안전한 방법
- ADR-010(Grayscale AA) 하에서 DPI-aware 렌더링 도입 전까지의 최선

## Consequences

### Positive
- CJK gap: 3.7px → 1px 좌/우 (Alacritty 동등)
- 글리프 비율 100% 보존 (스케일링/변형 없음)
- ASCII 렌더링 영향 없음

### Negative
- CJK 세로 약간 클리핑 (~2px)
- DPI-aware 렌더링 도입 시 재평가 필요

## Related
- ADR-008: 2-Pass 렌더링 (CJK 오버플로우 처리)
- ADR-010: Grayscale AA Composition
