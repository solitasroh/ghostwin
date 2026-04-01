# dpi-aware-rendering Gap Analysis

> **Feature**: DPI-Aware 글리프 래스터라이즈 + 다중 모니터 DPI 대응
> **Design**: `docs/02-design/features/dpi-aware-rendering.design.md`
> **Date**: 2026-04-01
> **Build**: 0 errors, 10/10 tests PASS

---

## Match Rate: 98.6% (70/71)

```
[Plan] -> [Design] -> [Do] -> [Check 98.6%] -> [Report]
```

---

## 1. Section-by-Section Results

| Section | Items | Matched | Rate | Status |
|---------|:-----:|:-------:|:----:|:------:|
| 2: API Changes (AtlasConfig, Impl, App) | 10 | 10 | 100% | PASS |
| 3.1.1: init_dwrite | 6 | 6 | 100% | PASS |
| 3.1.2: compute_cell_metrics | 6 | 6 | 100% | PASS |
| 3.1.3: rasterize_glyph (a-d) | 14 | 14 | 100% | PASS |
| 3.2.1: InitializeD3D11 | 4 | 4 | 100% | PASS |
| 3.2.2: StartTerminal | 2 | 2 | 100% | PASS |
| 3.2.3: CompositionScaleChanged | 6 | 6 | 100% | PASS |
| 3.2.4: RenderLoop DPI rebuild | 13 | 12 | 92% | PASS |
| 5: Thread Safety | 6 | 6 | 100% | PASS |
| 6: ADR-012 Compatibility | 4 | 4 | 100% | PASS |
| **Total** | **71** | **70** | **98.6%** | **PASS** |

---

## 2. Differences (2 items)

### 2.1 Intentional Improvements (Implementation > Design)

| # | Item | Design | Implementation | Impact |
|---|------|--------|----------------|--------|
| 1 | dpi_scale 방어적 검증 | `dpi_scale = config.dpi_scale` | `config.dpi_scale > 0.0f ? config.dpi_scale : 1.0f` | 런타임 안전성 향상 |
| 2 | QuadBuilder 갱신 | `update_cell_size()` + 재생성 (2단계) | 재생성만 (1단계) | 불필요한 중복 제거 |

### 2.2 Missing Features

없음.

### 2.3 Changed Variable Names

| Design | Implementation | Reason |
|--------|----------------|--------|
| `fb_cell_px` | `fb_cell_dip` | DIP 단위이므로 구현이 의미상 더 정확 |

---

## 3. Verification Checklist

| # | Check | Result |
|---|-------|:------:|
| T1 | 컴파일 0 errors | PASS |
| T2 | 기존 테스트 10/10 PASS | PASS |
| T3 | AtlasConfig.dpi_scale 기본값 1.0 | PASS |
| T4 | Impl.dpi_scale 저장 | PASS |
| T5 | compute_cell_metrics * dpi_scale | PASS |
| T6 | DWRITE_MATRIX {dpi_scale,0,0,dpi_scale,0,0} | PASS |
| T7 | v1 pixelsPerDip = dpi_scale | PASS |
| T8 | Fallback 축소: cell_h / dpi_scale 역변환 | PASS |
| T9 | advance_x * dpi_scale | PASS |
| T10 | atomic 멤버 3개 (winui_app.h) | PASS |
| T11 | InitializeD3D11 초기 DPI store | PASS |
| T12 | StartTerminal acfg.dpi_scale 전달 | PASS |
| T13 | CompositionScaleChanged 0.01 임계값 | PASS |
| T14 | RenderLoop DPI 변경 → atlas 재생성 | PASS |
| T15 | m_vt_mutex lock for grid resize | PASS |
| T16 | ADR-012 centering 비율 보존 | PASS |

---

## 4. Files Changed

| File | Lines | Description |
|------|:-----:|-------------|
| `src/renderer/glyph_atlas.h` | +1 | `AtlasConfig.dpi_scale` |
| `src/renderer/glyph_atlas.cpp` | ~20 | dpi_scale 저장, 메트릭 스케일, 변환 행렬, fallback 역변환 |
| `src/app/winui_app.h` | +4 | atomic 멤버 3개 + 주석 |
| `src/app/winui_app.cpp` | ~55 | 초기 DPI, 이벤트 핸들러, RenderLoop 재생성 |

---

## 5. Conclusion

Match Rate **98.6%** 달성. 발견된 2건의 차이는 모두 구현이 디자인보다 우수한 개선이며, 기능적 영향 없음. Report 생성 가능.
