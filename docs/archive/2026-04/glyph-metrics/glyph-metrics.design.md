# glyph-metrics Design Document

> **Summary**: 셀 메트릭 조정 시스템 — AtlasConfig 확장, baseline 균등 분배, CJK advance 강제 보정, window padding
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-03
> **Status**: Draft
> **Planning Doc**: [glyph-metrics.plan.md](../../01-plan/features/glyph-metrics.plan.md)

---

## 1. Overview

### 1.1 Design Goals

1. `AtlasConfig`에 6개 조정 파라미터를 추가하되, 기본값(1.0/0.0)에서 **기존과 동일한 렌더링 결과** 보장
2. Baseline 계산을 WT 패턴(lineGap 균등 분배)으로 개선
3. CJK advance 강제 보정으로 그리드 정렬 보장
4. 모든 변경은 `glyph_atlas.cpp`와 `quad_builder.cpp` 내부에 국한 — 외부 API 최소 변경

### 1.2 Design Principles

- **Zero-regression at defaults**: scale=1.0, offset=0에서 기존 렌더링과 pixel-perfect 동일
- **참조 구현 추종**: WT(baseline), WezTerm(cap-height), Alacritty(glyph_offset) 검증된 패턴
- **단일 진실 원천**: 셀 메트릭은 `GlyphAtlas`에서만 계산, QuadBuilder는 소비자

---

## 2. Architecture

### 2.1 Component Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                      GhostWinApp                            │
│  ┌──────────┐  AtlasConfig(+6 params)  ┌──────────────┐    │
│  │ Settings │─────────────────────────▶│  GlyphAtlas   │    │
│  │ (future) │                          │  - metrics    │    │
│  └──────────┘                          │  - rasterize  │    │
│                                        └──────┬───────┘    │
│                                               │ cell_w/h   │
│                                               │ baseline    │
│                        ┌──────────────────────┘             │
│                        ▼                                    │
│  ┌──────────────────────────────────┐                       │
│  │          QuadBuilder             │                       │
│  │  - glyph_offset_x/y 적용        │                       │
│  │  - CJK advance 강제 보정        │                       │
│  │  - window_padding 반영           │                       │
│  └──────────────┬───────────────────┘                       │
│                 │ QuadInstance[]                             │
│                 ▼                                            │
│  ┌──────────────────────────────────┐                       │
│  │        DX11Renderer              │                       │
│  │  - viewport (padding 반영)       │                       │
│  └──────────────────────────────────┘                       │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 Data Flow

```
AtlasConfig (파라미터)
  → GlyphAtlas::compute_cell_metrics()
    → cell_w = roundf('0' advance × scale × dpi × cell_width_scale)
    → cell_h = roundf((asc+desc+gap) × dpi × cell_height_scale)
    → baseline = roundf(asc_px + (gap_px + adjusted_h - natural_h) / 2)
  → QuadBuilder::build()
    → pos_x += padding_left + glyph_offset_x
    → pos_y += padding_top + glyph_offset_y
    → CJK: advance = 셀수 × cell_w (강제 보정)
  → DX11Renderer::render()
    → viewport offset by padding
```

---

## 3. Data Model

### 3.1 AtlasConfig 확장

```cpp
// src/renderer/glyph_atlas.h — AtlasConfig struct
struct AtlasConfig {
    // ── 기존 필드 (변경 없음) ──
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
    const wchar_t* nerd_font_family = nullptr;
    uint32_t initial_size = constants::kInitialAtlasSize;
    uint32_t max_size = constants::kMaxAtlasSize;
    float dpi_scale = 1.0f;

    // ── FR-01: 셀 크기 스케일 ──
    float cell_width_scale  = 1.0f;  // 0.5 ~ 2.0, 셀 폭 배율
    float cell_height_scale = 1.0f;  // 0.5 ~ 2.0, 셀 높이(줄 간격) 배율

    // ── FR-02: 글리프 오프셋 ──
    float glyph_offset_x = 0.0f;    // px, 글리프 수평 이동
    float glyph_offset_y = 0.0f;    // px, 글리프 수직 이동

    // ── FR-06: Fallback cap-height 스케일링 ──
    bool  use_cap_height_scaling = true;  // fallback 폰트 cap-height 비율 스케일
};
```

### 3.2 WindowPadding 구조체 (신규)

```cpp
// src/common/render_constants.h 또는 별도 헤더
struct WindowPadding {
    float left   = 0.0f;  // px
    float top    = 0.0f;
    float right  = 0.0f;
    float bottom = 0.0f;
    bool  dynamic = false;  // 나머지 공간 균등 분배
};
```

### 3.3 Impl 내부 상태 추가

```cpp
// glyph_atlas.cpp — GlyphAtlas::Impl 추가 필드
float    natural_cell_h = 0;  // 스케일 적용 전 자연 셀 높이 (baseline 계산용)
float    cap_height_ratio = 0; // primary 폰트의 capHeight/cell_height 비율
```

---

## 4. 핵심 알고리즘 상세 설계

### 4.1 FR-03: Baseline lineGap 균등 분배

**현재 코드** (`glyph_atlas.cpp:208-232`):
```cpp
ascent_px = static_cast<uint32_t>(ascent * dpi_scale + 0.5f);
cell_h = static_cast<uint32_t>((ascent + descent + gap) * dpi_scale + 0.5f);
```
문제: lineGap이 cell_h에만 포함되고 baseline(ascent_px)에 반영 안 됨 → 글리프가 위로 치우침.

**변경 후** (WT 패턴):
```cpp
void GlyphAtlas::Impl::compute_cell_metrics() {
    DWRITE_FONT_METRICS metrics;
    font_face->GetMetrics(&metrics);

    float scale = dip_size / metrics.designUnitsPerEm;
    float ascent  = metrics.ascent  * scale * dpi_scale;  // physical px
    float descent = metrics.descent * scale * dpi_scale;
    float gap     = metrics.lineGap * scale * dpi_scale;

    // FR-07: 셀 폭 — '0' 글리프 기준 (CSS ch 단위, WT 패턴)
    float advance_w = measure_cell_advance('0');
    if (advance_w <= 0.0f) {
        advance_w = measure_max_ascii_advance();  // WezTerm 폴백
    }

    // Natural cell dimensions (스케일 적용 전)
    float natural_h = ascent + descent + gap;
    natural_cell_h = natural_h;

    // FR-01: 사용자 스케일 적용
    float adjusted_w = advance_w * cell_width_scale;
    float adjusted_h = natural_h * cell_height_scale;

    // 반올림: roundf() (WT 패턴 — floor보다 시각 오차 최소)
    cell_w = static_cast<uint32_t>(std::max(1.0f, std::roundf(adjusted_w)));
    cell_h = static_cast<uint32_t>(std::max(1.0f, std::roundf(adjusted_h)));

    // FR-03: Baseline — lineGap + 스케일 여유분을 상하 균등 분배 (WT 패턴)
    // baseline = ascent + (gap + adjustedH - naturalH) / 2
    float extra = gap + adjusted_h - natural_h;
    ascent_px = static_cast<uint32_t>(std::roundf(ascent + extra / 2.0f));

    // FR-06: cap_height 비율 저장 (fallback 스케일링용)
    cap_height_ratio = compute_cap_height_ratio(font_face.Get(), scale, dpi_scale);
}
```

**검증**: `cell_width_scale=1.0`, `cell_height_scale=1.0` 일 때:
- `adjusted_h == natural_h` → `extra = gap` → `ascent_px = roundf(ascent + gap/2)`
- 기존: `ascent_px = roundf(ascent)` → **차이 발생** (lineGap/2 만큼)
- 이 차이가 의도된 개선임 (글리프가 셀 중앙으로 이동). 기존 동작과 완전 동일은 아니지만 시각적으로 더 나은 결과.

### 4.2 FR-07: 셀 폭 기준 문자 변경

```cpp
// glyph_atlas.cpp — 새 헬퍼 함수
float GlyphAtlas::Impl::measure_cell_advance(uint32_t codepoint) {
    uint16_t glyph_index = 0;
    font_face->GetGlyphIndices(&codepoint, 1, &glyph_index);
    if (glyph_index == 0) return 0.0f;  // 글리프 없음

    DWRITE_GLYPH_METRICS gm;
    font_face->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);
    float scale = dip_size / fm_designUnitsPerEm;
    return gm.advanceWidth * scale * dpi_scale;
}

float GlyphAtlas::Impl::measure_max_ascii_advance() {
    float max_advance = 0.0f;
    for (uint32_t cp = 0x21; cp < 0x7F; cp++) {  // '!' to '~'
        float adv = measure_cell_advance(cp);
        if (adv > max_advance) max_advance = adv;
    }
    return max_advance;
}
```

### 4.3 FR-02: 글리프 오프셋 (QuadBuilder)

```cpp
// quad_builder.cpp — build() 내 텍스트 패스에서 적용
// Alacritty 패턴: 셀 크기 불변, 글리프 위치만 이동

float glyph_x;
if (wide && glyph.advance_x > 0.0f) {
    float cell_span = (float)(cell_w_ * 2);
    float centering = (cell_span - glyph.advance_x) * 0.5f;
    if (centering < 0.0f) centering = 0.0f;
    glyph_x = (float)px + centering + glyph.offset_x + glyph_offset_x_;
} else {
    glyph_x = (float)px + glyph.offset_x + glyph_offset_x_;
}

float gy = (float)py + (float)baseline_ + glyph.offset_y + glyph_offset_y_;
```

변경 최소: 기존 glyph_x, gy 계산에 `+ glyph_offset_x_`, `+ glyph_offset_y_`만 추가.

### 4.4 FR-04: CJK Advance 강제 보정

```cpp
// quad_builder.cpp — WT 패턴 적용
bool wide = is_wide_codepoint(cell.codepoints[0]);
float glyph_x;
if (wide) {
    // WT 패턴: 논리적 셀 수 × cellWidth를 기대 advance로 강제
    float expected_advance = (float)(cell_w_ * 2);
    // 기대 advance 내에서 센터링 (기존 ADR-012 로직 유지)
    float centering = (expected_advance - glyph.advance_x) * 0.5f;
    if (centering < 0.0f) centering = 0.0f;
    glyph_x = (float)px + centering + glyph.offset_x + glyph_offset_x_;
} else {
    glyph_x = (float)px + glyph.offset_x + glyph_offset_x_;
}
```

Note: 현재 구현과 거의 동일. WT의 "마지막 글리프에 차이 보정"은 text shaping(HarfBuzz) 통합 시 필요하며, 현재 단일 글리프 렌더링에서는 센터링으로 충분.

### 4.5 FR-05: Window Padding

```cpp
// quad_builder.cpp — build() 전체에 패딩 오프셋 적용
uint16_t px = (uint16_t)(c * cell_w_ + padding_left_);
uint16_t py = (uint16_t)(r * cell_h_ + padding_top_);
```

```cpp
// winui_app.cpp — grid 계산에 패딩 반영
uint32_t usable_w = width_px - padding.left - padding.right;
uint32_t usable_h = height_px - padding.top - padding.bottom;
uint16_t cols = static_cast<uint16_t>(usable_w / atlas->cell_width());
uint16_t rows = static_cast<uint16_t>(usable_h / atlas->cell_height());

// Dynamic padding: 나머지 공간 균등 분배 (Alacritty 패턴)
if (padding.dynamic) {
    float remainder_x = (float)(usable_w - cols * atlas->cell_width());
    float remainder_y = (float)(usable_h - rows * atlas->cell_height());
    actual_pad_left = padding.left + remainder_x / 2.0f;
    actual_pad_top  = padding.top  + remainder_y / 2.0f;
}
```

### 4.6 FR-06: Fallback Cap-Height 스케일링

```cpp
// glyph_atlas.cpp — rasterize_glyph() 내

float em_size = dip_size;
if (face_to_use != font_face.Get()) {
    if (use_cap_height_scaling && cap_height_ratio > 0.0f) {
        // WezTerm 패턴: primary/fallback cap_height 비율로 스케일
        float fb_cap_ratio = compute_cap_height_ratio(face_to_use, ...);
        if (fb_cap_ratio > 0.0f) {
            float cap_scale = cap_height_ratio / fb_cap_ratio;
            em_size *= cap_scale;
        }
    }
    // 기존 높이 축소 로직 유지 (non-CJK만, cap-height 미적용 시)
    if (!is_cjk_wide && em_size == dip_size) {
        float fb_cell_dip = (float)(fm.ascent + fm.descent) * dip_size / fm.designUnitsPerEm;
        float target_cell_dip = (float)cell_h / dpi_scale;
        if (fb_cell_dip > target_cell_dip) {
            em_size = dip_size * target_cell_dip / fb_cell_dip;
        }
    }
}
```

**cap_height 계산 헬퍼:**
```cpp
float GlyphAtlas::Impl::compute_cap_height_ratio(
    IDWriteFontFace* face, float scale, float dpi) {
    DWRITE_FONT_METRICS1 metrics1;
    ComPtr<IDWriteFontFace1> face1;
    if (SUCCEEDED(face->QueryInterface(IID_PPV_ARGS(&face1)))) {
        face1->GetMetrics(&metrics1);
        if (metrics1.capHeight > 0) {
            return metrics1.capHeight * scale * dpi;
        }
    }
    return 0.0f;  // capHeight 없는 폰트 → 스케일링 스킵
}
```

---

## 5. 파일 변경 명세

### 5.1 수정 파일

| 파일 | 변경 | FR |
|------|------|----|
| `src/renderer/glyph_atlas.h` | AtlasConfig에 5필드 추가, cap-height API 추가 | FR-01,02,06 |
| `src/renderer/glyph_atlas.cpp` | `compute_cell_metrics()` 재작성, `measure_cell_advance()` 추가, `compute_cap_height_ratio()` 추가, fallback 스케일링 변경 | FR-01,03,06,07 |
| `src/renderer/quad_builder.h` | 생성자에 `glyph_offset_x/y`, `padding_left/top` 추가 | FR-02,05 |
| `src/renderer/quad_builder.cpp` | glyph_offset 적용, padding 오프셋, CJK 보정 개선 | FR-02,04,05 |
| `src/common/render_constants.h` | `WindowPadding` 구조체 추가 | FR-05 |
| `src/app/winui_app.cpp` | AtlasConfig 파라미터 전달, grid 계산 패딩 반영, dynamic padding | FR-01,02,05 |

### 5.2 신규 파일

없음. 모든 변경은 기존 파일 내부에서 처리.

---

## 6. 구현 순서

### Phase 1: 핵심 메트릭 변경 (FR-03, FR-07) — 영향 범위 최대, 먼저 안정화

1. [ ] `glyph_atlas.h`: AtlasConfig에 `cell_width_scale`, `cell_height_scale` 추가
2. [ ] `glyph_atlas.cpp`: `compute_cell_metrics()` 재작성
   - `measure_cell_advance('0')` + ASCII max 폴백
   - baseline lineGap 균등 분배
   - `roundf()` 반올림
3. [ ] `winui_app.cpp`: AtlasConfig에 scale 파라미터 전달 (기본값 1.0)
4. [ ] **빌드 + 시각 검증**: scale=1.0에서 기존과 비교 (baseline 위치 차이 확인)

### Phase 2: 글리프 오프셋 (FR-02) — 독립적, 안전

5. [ ] `glyph_atlas.h`: AtlasConfig에 `glyph_offset_x`, `glyph_offset_y` 추가
6. [ ] `quad_builder.h/cpp`: 생성자에 offset 파라미터 추가, build()에서 적용
7. [ ] `winui_app.cpp`: offset 파라미터 전달
8. [ ] **빌드 + 시각 검증**: offset=0에서 변화 없음 확인

### Phase 3: CJK 보정 + Fallback (FR-04, FR-06) — 부분 독립적

9. [ ] `glyph_atlas.cpp`: `compute_cap_height_ratio()` 헬퍼 추가
10. [ ] `glyph_atlas.cpp`: rasterize_glyph() fallback 스케일링에 cap-height 로직 추가
11. [ ] **빌드 + 시각 검증**: CJK + 영문 혼합, Nerd Font 아이콘 크기 비교

### Phase 4: Window Padding (FR-05) — 독립적

12. [ ] `render_constants.h`: `WindowPadding` 구조체 추가
13. [ ] `quad_builder.h/cpp`: padding 파라미터 추가, build()에서 오프셋 적용
14. [ ] `winui_app.cpp`: grid 계산에 padding 반영, dynamic padding 구현
15. [ ] **빌드 + 시각 검증**: padding 설정 + dynamic padding 동작 확인

---

## 7. Test Plan

### 7.1 시각 검증 체크리스트

| # | 항목 | 검증 방법 |
|---|------|-----------|
| T-01 | scale=1.0에서 기존 대비 baseline 위치 차이 확인 | 스크린샷 비교 |
| T-02 | cell_width_scale=1.2에서 글자 간격 넓어짐 | 시각 확인 |
| T-03 | cell_height_scale=1.3에서 줄 높이 증가 | 시각 확인 |
| T-04 | glyph_offset_y=-1에서 글리프 1px 위로 이동 | 시각 확인 |
| T-05 | CJK + 영문 혼합에서 그리드 정렬 | 한글+영문 교차 입력 |
| T-06 | DPI 100%/150%/200%에서 정상 렌더링 | DPI 변경 후 확인 |
| T-07 | Nerd Font 아이콘 cap-height 스케일링 | 프롬프트 아이콘 크기 |
| T-08 | window_padding 20px 설정 | 가장자리 여백 확인 |
| T-09 | dynamic_padding 활성화 | 창 리사이즈 시 균등 배분 |
| T-10 | 셀 폭 기준 `'0'` vs `'M'` 차이 | JetBrainsMono, Cascadia 비교 |

### 7.2 Regression 검증

| # | 항목 | 기대 |
|---|------|------|
| R-01 | ClearType 렌더링 품질 유지 | Dual Source Blending 정상 |
| R-02 | IME 조합 오버레이 위치 | 셀 좌표 × cell_w/h 정상 |
| R-03 | DPI 변경 시 atlas 재생성 | 새 메트릭으로 정상 렌더링 |
| R-04 | 커서 위치 정확성 | 셀 그리드에 정확히 정렬 |

---

## 8. 반올림 전략 통합 규칙

모든 메트릭에 동일한 반올림 전략을 적용하여 일관성 보장:

| 메트릭 | 반올림 | 근거 |
|--------|--------|------|
| cell_w, cell_h | `roundf()` | WT 패턴, 가장 가까운 정수 |
| ascent_px (baseline) | `roundf()` | WT 패턴 |
| glyph position (pos_x, pos_y) | `roundf()` (= +0.5f 후 truncate) | 기존 유지 |
| glyph advance_x | f32 유지 (정수화 안 함) | 센터링 계산 정밀도 |
| padding | `roundf()` | 정수 픽셀 정렬 |

**기존 `+0.5f` cast를 `roundf()` 로 통일**: 음수 값에서 동작이 다르므로 (offset_y가 음수일 수 있음) roundf가 더 정확.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-03 | Initial draft — 7 FR 상세 설계, 4-phase 구현 순서 | 노수장 |
