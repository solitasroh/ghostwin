# dpi-aware-rendering Design

> **Feature**: DPI-Aware 글리프 래스터라이즈 + 다중 모니터 DPI 대응
> **Plan**: `docs/01-plan/features/dpi-aware-rendering.plan.md`
> **Date**: 2026-04-01
> **Author**: Solit

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | `pixelsPerDip=1.0` 고정으로 고DPI 모니터에서 글리프가 96 DPI 래스터 후 GPU 스케일업 → 흐릿함 |
| **Solution** | `AtlasConfig.dpi_scale` 추가, DirectWrite 변환 행렬에 DPI 반영, DPI 변경 시 atlas 재생성 |
| **Function/UX** | 150% DPI에서 12pt → 24px 물리 픽셀 직접 래스터, 모니터 이동 시 자동 재래스터 |
| **Core Value** | WT/Alacritty 동등 텍스트 선명도, CJK 간격 근본 해결 |

---

## 1. Design Decisions

### DD-01: DPI 스케일링 방식 — 변환 행렬 (Transform Matrix)

**선택**: `CreateGlyphRunAnalysis`의 변환 행렬에 DPI 스케일을 곱하는 방식

**대안 검토**:

| 방식 | 설명 | 장점 | 단점 |
|------|------|------|------|
| **A: emSize 스케일** | `fontEmSize = dip_size * dpi_scale` | 간단 | glyph_run의 advance, offset 모두 재계산 필요 |
| **B: 변환 행렬** (채택) | `DWRITE_MATRIX {s,0,0,s,0,0}` | 기존 glyph_run 로직 불변, DirectWrite가 내부 스케일 처리 | 행렬 전달 필요 |
| **C: 별도 DIP→물리 변환** | 래스터 후 결과만 스케일 | — | GPU 스케일업과 동일, 의미 없음 |

**근거**: 방식 B는 기존 `em_size`, `advance`, fallback 축소 로직을 전혀 변경하지 않고 DirectWrite에 스케일을 위임한다. WT도 유사하게 변환 파라미터로 DPI를 처리한다.

### DD-02: 셀 메트릭 계산 — DIP 기반 유지 + 스케일 적용

`compute_cell_metrics()`는 DIP 단위로 계산한 후 `dpi_scale`을 곱하여 물리 픽셀로 변환.

```
cell_w_physical = round(dip_cell_w * dpi_scale)
cell_h_physical = round(dip_cell_h * dpi_scale)
ascent_physical = round(dip_ascent * dpi_scale)
```

**근거**: DIP 기반 계산을 유지하면 폰트 메트릭 로직이 DPI와 독립적이며, 100% DPI에서 기존과 완전히 동일한 결과를 보장한다.

### DD-03: Atlas 재생성 전략 — 전체 재생성 (Full Rebuild)

DPI 변경 시 기존 atlas를 폐기하고 새 atlas를 처음부터 생성한다.

**대안 검토**:

| 방식 | 설명 | 장단점 |
|------|------|--------|
| **A: 전체 재생성** (채택) | `GlyphAtlas::create()` 재호출 | 간단, 안전, 일관성 보장 |
| **B: 인플레이스 갱신** | 기존 atlas에서 캐시 클리어 + 텍스처 교체 | 코드 복잡, Impl 내부 상태 노출 필요 |

**근거**: DPI 변경은 드문 이벤트(모니터 이동, 배율 변경)이다. 200ms 미만의 재생성 시간은 허용 범위. 글리프는 on-demand로 래스터하므로 프리캐시 불필요.

---

## 2. API Changes

### 2.1 AtlasConfig (glyph_atlas.h:26-32)

```cpp
// BEFORE
struct AtlasConfig {
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
    const wchar_t* nerd_font_family = nullptr;
    uint32_t initial_size = constants::kInitialAtlasSize;
    uint32_t max_size = constants::kMaxAtlasSize;
};

// AFTER
struct AtlasConfig {
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
    const wchar_t* nerd_font_family = nullptr;
    uint32_t initial_size = constants::kInitialAtlasSize;
    uint32_t max_size = constants::kMaxAtlasSize;
    float dpi_scale = 1.0f;  // CompositionScaleX (1.0 = 96 DPI)
};
```

**호환성**: 기존 호출부는 `dpi_scale=1.0` 기본값으로 동작 변경 없음.

### 2.2 GlyphAtlas::Impl 멤버 (glyph_atlas.cpp:78-160)

```cpp
// 추가 멤버
float dpi_scale = 1.0f;  // 현재 atlas의 DPI 스케일
```

### 2.3 GhostWinApp 멤버 (winui_app.h)

```cpp
// 추가 멤버
std::atomic<float> m_current_dpi_scale{1.0f};
std::atomic<float> m_pending_dpi_scale{1.0f};
std::atomic<bool>  m_dpi_change_requested{false};
```

---

## 3. Detailed Design

### 3.1 Module: GlyphAtlas — DPI-Aware 초기화

**파일**: `src/renderer/glyph_atlas.cpp`

#### 3.1.1 init_dwrite() 변경 (line 164~264)

```cpp
bool GlyphAtlas::Impl::init_dwrite(const AtlasConfig& config, Error* out_error) {
    dip_size = config.font_size_pt * (96.0f / 72.0f);  // DIP 기준 (불변)
    dpi_scale = config.dpi_scale;                        // NEW: DPI 스케일 저장

    // CreateTextFormat: DIP 크기 사용 (DirectWrite는 DIP 기반 API)
    // font_size_pt * (96/72) — 기존과 동일
    hr = dwrite_factory->CreateTextFormat(
        config.font_family, nullptr,
        DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        dip_size,  // DIP 기준 유지
        L"en-us", &text_format);

    // ... (font face 취득 — 변경 없음) ...

    compute_cell_metrics();  // DPI-aware 셀 메트릭 계산
    // ... 이후 동일 ...
}
```

#### 3.1.2 compute_cell_metrics() 변경 (line 266~289)

```cpp
void GlyphAtlas::Impl::compute_cell_metrics() {
    DWRITE_FONT_METRICS metrics;
    font_face->GetMetrics(&metrics);

    float scale = dip_size / metrics.designUnitsPerEm;

    float ascent  = metrics.ascent * scale;
    float descent = metrics.descent * scale;
    float gap     = metrics.lineGap * scale;

    // DIP → 물리 픽셀 변환
    ascent_px = static_cast<uint32_t>(ascent * dpi_scale + 0.5f);
    cell_h = static_cast<uint32_t>((ascent + descent + gap) * dpi_scale + 0.5f);
    if (cell_h < 1) cell_h = 1;

    // Cell width: 'M' advance
    uint32_t cp = 'M';
    uint16_t glyph_index = 0;
    font_face->GetGlyphIndices(&cp, 1, &glyph_index);

    DWRITE_GLYPH_METRICS gm;
    font_face->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);
    cell_w = static_cast<uint32_t>(gm.advanceWidth * scale * dpi_scale + 0.5f);
    if (cell_w < 1) cell_w = 1;
}
```

**핵심 변경**: 모든 픽셀 값에 `* dpi_scale` 곱셈 추가. `dpi_scale=1.0`이면 기존과 동일.

#### 3.1.3 rasterize_glyph() 변경 (line 460~641)

**a) Fallback 폰트 축소 (line 514~520)**

```cpp
// em_size 계산: DIP 기준 유지 (glyph_run.fontEmSize는 DIP 단위)
float em_size = dip_size;
if (face_to_use != font_face.Get() && !is_cjk_wide) {
    float fb_cell_px = (float)(fm.ascent + fm.descent) * dip_size / fm.designUnitsPerEm;
    float target_cell_dip = (float)cell_h / dpi_scale;  // 물리→DIP 역변환
    if (fb_cell_px > target_cell_dip) {
        em_size = dip_size * target_cell_dip / fb_cell_px;
    }
}
```

**b) advance 계산 (line 522~532)**

```cpp
float scale = em_size / fm.designUnitsPerEm;
// advance는 DIP 단위 (변환 행렬에서 스케일 적용됨)
float advance = gm.advanceWidth * scale;
```

**c) CreateGlyphRunAnalysis (line 537~561)**

```cpp
// v2 API: 변환 행렬에 DPI 스케일 적용
ComPtr<IDWriteGlyphRunAnalysis> analysis;
ComPtr<IDWriteFactory2> factory2;
HRESULT hr = E_FAIL;
if (SUCCEEDED(dwrite_factory.As(&factory2))) {
    DWRITE_MATRIX dpi_transform = {
        dpi_scale, 0,
        0, dpi_scale,
        0, 0
    };
    hr = factory2->CreateGlyphRunAnalysis(
        &glyph_run,
        &dpi_transform,  // WAS: &identity
        DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
        DWRITE_MEASURING_MODE_NATURAL,
        DWRITE_GRID_FIT_MODE_DEFAULT,
        DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
        0.0f, 0.0f,
        &analysis);
}
// v1 폴백
if (FAILED(hr)) {
    hr = dwrite_factory->CreateGlyphRunAnalysis(
        &glyph_run, dpi_scale, nullptr,  // WAS: 1.0f
        DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
        DWRITE_MEASURING_MODE_NATURAL,
        0.0f, 0.0f, &analysis);
}
```

**d) GlyphEntry offset 계산**

래스터 결과의 `bounds` 좌표와 glyph 크기는 이미 물리 픽셀 단위 (DirectWrite가 변환 행렬 적용 후 반환). offset 계산도 물리 픽셀 기준:

```cpp
// offset_x, offset_y: bounds.left, ascent_px - bounds.top
// advance_x: advance * dpi_scale (DIP→물리 변환)
entry.advance_x = advance * dpi_scale;
```

### 3.2 Module: GhostWinApp — DPI 변경 감지 + Atlas 재생성

**파일**: `src/app/winui_app.h`, `src/app/winui_app.cpp`

#### 3.2.1 InitializeD3D11() — 초기 DPI 적용 (winui_app.cpp)

```cpp
void GhostWinApp::InitializeD3D11(SwapChainPanel const& panel) {
    float scaleX = panel.CompositionScaleX();
    // ... 기존 렌더러 생성 ...

    m_current_dpi_scale.store(scaleX, std::memory_order_release);
    m_pending_dpi_scale.store(scaleX, std::memory_order_release);

    StartTerminal(cfg.width, cfg.height);
}
```

#### 3.2.2 StartTerminal() — DPI-Aware Atlas 생성 (winui_app.cpp:1448)

```cpp
void GhostWinApp::StartTerminal(uint32_t width_px, uint32_t height_px) {
    Error err{};
    AtlasConfig acfg;
    acfg.font_family = L"Cascadia Mono";
    acfg.font_size_pt = constants::kDefaultFontSizePt;
    acfg.dpi_scale = m_current_dpi_scale.load(std::memory_order_acquire);  // NEW

    m_atlas = GlyphAtlas::create(m_renderer->device(), acfg, &err);
    // ... 이후 동일 ...
}
```

#### 3.2.3 CompositionScaleChanged 핸들러 (winui_app.cpp:519)

```cpp
m_panel.CompositionScaleChanged([self = get_strong()](
        controls::SwapChainPanel const&, auto&&) {
    float newScale = self->m_panel.CompositionScaleX();
    float oldScale = self->m_current_dpi_scale.load(std::memory_order_acquire);

    // DPI 변경 임계값: 0.01 이상 차이 시 (부동소수점 비교)
    if (std::abs(newScale - oldScale) > 0.01f) {
        self->m_pending_dpi_scale.store(newScale, std::memory_order_release);
        self->m_dpi_change_requested.store(true, std::memory_order_release);
    }

    // 기존 리사이즈도 트리거 (스왑체인 크기도 변경됨)
    self->m_resize_timer.Stop();
    self->m_resize_timer.Start();
});
```

#### 3.2.4 RenderLoop DPI 변경 처리 (winui_app.cpp:1501)

```cpp
void GhostWinApp::RenderLoop() {
    QuadBuilder builder(
        m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

    while (m_render_running.load(std::memory_order_acquire)) {

        // ─── DPI 변경 처리 (리사이즈보다 먼저) ───
        if (m_dpi_change_requested.load(std::memory_order_acquire)) {
            float newScale = m_pending_dpi_scale.load(std::memory_order_acquire);
            LOG_I("winui", "DPI scale changed: %.2f -> %.2f",
                  m_current_dpi_scale.load(std::memory_order_relaxed), newScale);

            // 1. 새 atlas 생성
            Error err{};
            AtlasConfig acfg;
            acfg.font_family = L"Cascadia Mono";
            acfg.font_size_pt = constants::kDefaultFontSizePt;
            acfg.dpi_scale = newScale;
            auto new_atlas = GlyphAtlas::create(m_renderer->device(), acfg, &err);

            if (new_atlas) {
                // 2. 구 atlas 교체 (unique_ptr swap → 구 atlas 자동 해제)
                m_atlas = std::move(new_atlas);

                // 3. 렌더러에 새 SRV + ClearType 파라미터 설정
                m_renderer->set_atlas_srv(m_atlas->srv());
                m_renderer->set_cleartype_params(
                    m_atlas->enhanced_contrast(), m_atlas->gamma_ratios());

                // 4. QuadBuilder 셀 크기 업데이트
                builder.update_cell_size(m_atlas->cell_width(), m_atlas->cell_height());
                builder = QuadBuilder(
                    m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

                // 5. 그리드 리사이즈
                uint32_t w = m_pending_width.load(std::memory_order_acquire);
                uint32_t h = m_pending_height.load(std::memory_order_acquire);
                if (w == 0) w = 1; if (h == 0) h = 1;
                uint16_t cols = static_cast<uint16_t>(w / m_atlas->cell_width());
                uint16_t rows = static_cast<uint16_t>(h / m_atlas->cell_height());
                if (cols < 1) cols = 1; if (rows < 1) rows = 1;

                {
                    std::lock_guard lock(m_vt_mutex);
                    m_session->resize(cols, rows);
                    m_state->resize(cols, rows);
                }
                m_staging.resize(
                    static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1 + 8);

                m_current_dpi_scale.store(newScale, std::memory_order_release);
                LOG_I("winui", "DPI atlas rebuilt: cell=%ux%u, grid=%ux%u",
                      m_atlas->cell_width(), m_atlas->cell_height(), cols, rows);
            } else {
                LOG_E("winui", "DPI atlas rebuild failed: %s", err.message);
            }

            m_dpi_change_requested.store(false, std::memory_order_release);
        }

        // ─── 기존 리사이즈 처리 (변경 최소) ───
        if (m_resize_requested.load(std::memory_order_acquire)) {
            // ... 기존 코드 그대로 (atlas->cell_width/height는 이미 DPI-aware) ...
        }
        // ... 이하 렌더 루프 ...
    }
}
```

---

## 4. Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          UI Thread                                  │
│                                                                     │
│  CompositionScaleChanged                                            │
│       │                                                             │
│       ▼                                                             │
│  newScale = panel.CompositionScaleX()                               │
│  if |newScale - oldScale| > 0.01:                                   │
│      m_pending_dpi_scale.store(newScale)     ─────atomic────┐       │
│      m_dpi_change_requested.store(true)      ─────atomic────┤       │
│      m_resize_timer.Start()   // 스왑체인 리사이즈도          │       │
│                                                             │       │
└─────────────────────────────────────────────────────────────┤───────┘
                                                              │
┌─────────────────────────────────────────────────────────────▼───────┐
│                         Render Thread                               │
│                                                                     │
│  if m_dpi_change_requested:                                         │
│      newScale = m_pending_dpi_scale.load()                          │
│      ┌────────────────────────────────────────────┐                 │
│      │ GlyphAtlas::create({dpi_scale=newScale})   │                 │
│      │   ├── init_dwrite: dpi_scale 저장           │                 │
│      │   ├── compute_cell_metrics: * dpi_scale     │                 │
│      │   └── init_atlas_texture: 새 R8 텍스처      │                 │
│      └────────────────────────────────────────────┘                 │
│      m_atlas = new_atlas                                            │
│      renderer->set_atlas_srv(m_atlas->srv())                        │
│      builder = QuadBuilder(new_cell_w, new_cell_h, new_baseline)    │
│      ┌─ m_vt_mutex lock ──┐                                        │
│      │ session->resize()   │                                        │
│      │ state->resize()     │                                        │
│      └─────────────────────┘                                        │
│      m_current_dpi_scale = newScale                                 │
│                                                                     │
│  // 이후 프레임: rasterize_glyph()에서 dpi_transform 사용           │
│  //   CreateGlyphRunAnalysis({dpi_scale,0,0,dpi_scale,0,0})         │
│  //   → 물리 픽셀 크기 글리프 → atlas에 pack                        │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 5. Thread Safety Analysis

| 공유 리소스 | 접근 패턴 | 동기화 |
|------------|----------|--------|
| `m_pending_dpi_scale` | UI: store, Render: load | `std::atomic<float>` — lock-free |
| `m_dpi_change_requested` | UI: store, Render: load+store | `std::atomic<bool>` — lock-free |
| `m_current_dpi_scale` | UI: load (비교), Render: store | `std::atomic<float>` — lock-free |
| `m_atlas` (unique_ptr) | Render: read/write | 렌더 스레드 단독 소유, UI 접근 없음 |
| `m_session`, `m_state` | Render: write (resize) | `m_vt_mutex` lock — 기존 패턴 동일 |
| `m_renderer` SRV | Render: set | 렌더 스레드 단독 (upload_and_draw 전에 설정) |

**경합 가능 시나리오**: CompositionScaleChanged와 SizeChanged가 거의 동시에 발생 (모니터 이동 시). `m_resize_timer` 100ms 디바운스가 두 이벤트를 하나로 병합. 렌더 루프에서 DPI 변경을 먼저 처리한 후 리사이즈를 처리하면 정확한 셀 크기로 그리드가 계산됨.

---

## 6. ADR-012 호환성 분석

CJK advance-centering 로직 (`quad_builder.cpp`):

```cpp
float centering = (cell_span - advance_x) * 0.5f;
float glyph_x = px + centering + offset_x;
```

| 값 | 100% DPI (현재) | 150% DPI (변경 후) | 비율 유지 |
|----|:--------------:|:-----------------:|:---------:|
| cell_w | 9px | 14px | O |
| cell_span (2-cell) | 18px | 28px | O |
| CJK advance_x | 16px | 24px | O (16*1.5) |
| centering | 1px | 2px | O (비례) |

**결론**: DPI 스케일이 `cell_w`, `advance_x` 모두에 동일 비율로 적용되므로 centering 비율은 자동 보존됨. 추가 수정 불필요.

---

## 7. Implementation Order

```
S1  AtlasConfig에 dpi_scale 추가 (glyph_atlas.h)
 ↓
S2  Impl에 dpi_scale 저장 + init_dwrite에서 저장 (glyph_atlas.cpp)
 ↓
S3  compute_cell_metrics에 * dpi_scale 적용 (glyph_atlas.cpp)
 ↓
S4  rasterize_glyph: 변환 행렬 + v1 pixelsPerDip + advance_x 스케일 (glyph_atlas.cpp)
 ↓
S5  rasterize_glyph: fallback 축소 역변환 (glyph_atlas.cpp)
 ↓
S6  winui_app.h: DPI atomic 멤버 추가
 ↓
S7  InitializeD3D11: 초기 DPI 읽기 + m_current_dpi_scale 설정 (winui_app.cpp)
 ↓
S8  StartTerminal: acfg.dpi_scale 전달 (winui_app.cpp)
 ↓
S9  CompositionScaleChanged: DPI 변경 감지 + 플래그 설정 (winui_app.cpp)
 ↓
S10 RenderLoop: DPI 변경 시 atlas 재생성 + 그리드 리사이즈 (winui_app.cpp)
 ↓
S11 100% DPI 회귀 테스트
 ↓
S12 고DPI 선명도 검증
 ↓
S13 다중 모니터 전환 검증 (가능 시)
```

**파일별 변경 요약**:

| File | Lines Changed (approx) | Type |
|------|:---------------------:|------|
| `src/renderer/glyph_atlas.h` | +1 line | `AtlasConfig.dpi_scale` 추가 |
| `src/renderer/glyph_atlas.cpp` | ~20 lines | dpi_scale 저장, cell metrics 스케일, 변환 행렬, fallback 역변환 |
| `src/app/winui_app.h` | +3 lines | atomic 멤버 3개 추가 |
| `src/app/winui_app.cpp` | ~50 lines | 초기 DPI, 이벤트 핸들러, RenderLoop DPI 재생성 |
| **Total** | ~74 lines | 4개 파일 |

---

## 8. Test Plan

### 8.1 빌드 검증

| # | Test | Method | Pass Criteria |
|---|------|--------|--------------|
| T1 | 컴파일 성공 | `build_ghostwin.ps1 -Config Release` | 0 errors, 0 warnings (new) |
| T2 | 기존 테스트 PASS | 전체 테스트 실행 | All PASS 보존 |

### 8.2 100% DPI 회귀

| # | Test | Method | Pass Criteria |
|---|------|--------|--------------|
| T3 | 기본 셀 크기 | 로그 확인: `cell=NxN` | 기존과 동일 값 (예: 9x20) |
| T4 | ASCII 렌더링 | 육안 + 스크린샷 | 기존과 동일 |
| T5 | CJK 한글 렌더링 | "한글테스트" 입력 | advance-centering 유지, 간격 동일 |
| T6 | Nerd Font 아이콘 | PUA 문자 출력 | 기존과 동일 |

### 8.3 고DPI 검증

| # | Test | Method | Pass Criteria |
|---|------|--------|--------------|
| T7 | 150% DPI 셀 크기 | 로그: `DPI atlas rebuilt: cell=NxN` | cell_w ≈ 14, cell_h ≈ 30 (12pt 기준) |
| T8 | 150% DPI 텍스트 선명도 | 스크린샷 비교 (이전/이후) | 흐릿함 해소 |
| T9 | 150% DPI 그리드 정확도 | 터미널 크기 확인 (cols×rows) | 정확한 값 |
| T10 | 150% DPI 한글 IME | 한글 조합 입력 | 조합 글리프 선명, 커서 위치 정확 |
| T11 | 150% DPI 커서 크기/위치 | 커서 이동 + 점멸 | 셀에 정확히 맞춤 |

### 8.4 DPI 전환 검증

| # | Test | Method | Pass Criteria |
|---|------|--------|--------------|
| T12 | DPI 변경 감지 | 배율 변경 또는 모니터 이동 | 로그: `DPI scale changed` |
| T13 | Atlas 재생성 | DPI 변경 후 | 로그: `DPI atlas rebuilt` |
| T14 | 깜빡임 최소 | DPI 변경 시 화면 관찰 | 1-2 프레임 이내 복구 |
| T15 | 메모리 누수 없음 | DPI 반복 변경 | 구 atlas D3D 리소스 해제 확인 |

---

## 9. Risks and Mitigations

| # | Risk | Impact | Mitigation | 검증 방법 |
|---|------|--------|------------|----------|
| R1 | Atlas 재생성 중 깜빡임 | 중 | unique_ptr swap으로 atomic 교체; 구 atlas는 swap 후 자동 해제되므로 빈 프레임 없음 | T14 |
| R2 | 분수 DPI 반올림 오차 | 하 | `+ 0.5f` 반올림 유지; NATURAL_SYMMETRIC이 서브픽셀 힌팅 처리 | T8, T9 |
| R3 | fallback 축소 역변환 오류 | 중 | `target_cell_dip = cell_h / dpi_scale`로 DIP 복원 후 비교; 100% DPI에서 동일 결과 보장 | T5 |
| R4 | 200% DPI에서 atlas 텍스처 부족 | 하 | 글리프 크기 2배 → 동일 atlas에 절반 글리프; grow 전략(2x)으로 대응, max 4096 한도 충분 | T7 |
| R5 | CompositionScaleChanged 다중 발화 | 하 | 임계값 0.01 비교 + 리사이즈 타이머 100ms 디바운스 | T12 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-04-01 | Solit | Initial design |
