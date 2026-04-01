# dpi-aware-rendering Plan

> **Feature**: DPI-Aware 글리프 래스터라이즈 + 다중 모니터 DPI 대응
> **Project**: GhostWin Terminal
> **Phase**: 4 잔여 (FR-05)
> **Date**: 2026-04-01
> **Author**: Solit
> **Parent Plan**: `winui3-integration.plan.md` (Phase 4 Master Plan)
> **Previous**: Phase 4-A winui3-shell (94%), 4-B tsf-ime (99%)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | `pixelsPerDip=1.0` 고정으로 고DPI 모니터(125%~200%)에서 글리프가 96 DPI로 래스터라이즈 후 GPU 스케일업되어 텍스트가 흐릿하고, 그리드 셀 계산이 부정확함 |
| **Solution** | `CompositionScaleX/Y`를 GlyphAtlas에 전달하여 물리 DPI 기준 래스터라이즈하고, DPI 변경 시 atlas를 재생성하여 모니터 이동/배율 변경에 실시간 대응 |
| **Function/UX** | 150% DPI에서 12pt 폰트가 24px 물리 픽셀로 선명하게 래스터라이즈되고, 다중 모니터 간 창 이동 시 자동으로 글리프가 재래스터라이즈됨 |
| **Core Value** | 고DPI 모니터에서 Windows Terminal / Alacritty 동등 수준의 텍스트 선명도 확보. CJK 간격/선명도 근본 원인 해결 |

---

## 1. Background

### 1.1 현재 상태

Phase 4-A에서 WinUI3 SwapChainPanel 기반 셸이 완성되었다. `CompositionScaleChanged` 이벤트가 연결되어 있고, 스왑체인은 물리 픽셀 기준으로 올바르게 리사이즈된다.

그러나 **글리프 래스터라이즈는 여전히 96 DPI(pixelsPerDip=1.0)** 로 고정되어 있어, 고DPI 디스플레이에서 텍스트가 흐릿하다.

### 1.2 문제 분석

| 구성 요소 | 파일 | 상태 | 문제 |
|-----------|------|------|------|
| DPI 스케일 읽기 | `winui_app.cpp:519-523, 540-541` | **연결됨** | 스왑체인 리사이즈에만 사용, 폰트에 미전달 |
| pixelsPerDip | `glyph_atlas.cpp:544-561` | **1.0 고정** | CreateGlyphRunAnalysis에 항상 1.0 전달 |
| DIP 폰트 크기 | `glyph_atlas.cpp:165` | **96 DPI 고정** | `font_size_pt * (96/72)` — 물리 DPI 미반영 |
| 셀 메트릭 계산 | `glyph_atlas.cpp:266-289` | **DIP 기준** | 물리 픽셀이 아닌 DIP 단위로 계산 |
| AtlasConfig | `glyph_atlas.h:26-32` | **DPI 파라미터 없음** | dpi_scale 필드 부재 |
| Atlas 재생성 | `glyph_atlas.h:34-61` | **재생성 메서드 없음** | DPI 변경 시 atlas 갱신 불가 |
| 그리드 계산 | `winui_app.cpp:1507-1508` | **부정확** | 물리 픽셀 ÷ DIP 셀 크기 = 잘못된 행/열 수 |

### 1.3 DPI별 영향도

| DPI 배율 | 물리 DPI | 12pt 래스터 크기 (현재) | 12pt 래스터 크기 (목표) | 문제 |
|:--------:|:--------:|:---------------------:|:---------------------:|------|
| 100% | 96 | 16px | 16px | 없음 |
| 125% | 120 | 16px → 20px 스케일업 | 20px 직접 래스터 | 흐릿함 |
| 150% | 144 | 16px → 24px 스케일업 | 24px 직접 래스터 | 심각하게 흐릿함 |
| 200% | 192 | 16px → 32px 스케일업 | 32px 직접 래스터 | 매우 흐릿함 |

### 1.4 참조 구현

| 터미널 | DPI 처리 방식 |
|--------|-------------|
| **Windows Terminal** | `DxFontRenderData`에 DPI 저장, DPI 변경 시 폰트 메트릭 재계산 + atlas 무효화 |
| **Alacritty** | `dpr` (device pixel ratio) 저장, DPI 변경 시 font resize → atlas 완전 재생성 |
| **Ghostty (macOS)** | `backingScaleFactor` 기반, scale 변경 시 atlas 재생성 |

---

## 2. Goal

`GlyphAtlas`가 디스플레이의 실제 DPI에 맞춰 글리프를 래스터라이즈하고, 다중 모니터 간 이동 시 자동으로 atlas를 재생성한다.

**핵심 목표:**
1. `CompositionScaleX/Y`를 GlyphAtlas 생성 시 전달하여 물리 DPI 기준 래스터라이즈
2. DPI 변경 감지 시 atlas 전체 재생성 (글리프 캐시 무효화 + 셀 메트릭 재계산)
3. 그리드 행/열 계산이 DPI-aware 셀 크기 기준으로 정확하게 수행

---

## 3. Functional Requirements (FR)

### FR-01: AtlasConfig에 DPI 스케일 추가
- `AtlasConfig`에 `float dpi_scale = 1.0f` 필드 추가
- `GlyphAtlas::create()` 호출 시 `CompositionScaleX()` 값 전달
- DPI 스케일은 X축 기준 (수평/수직 동일 가정, 일반적 모니터 사양)

### FR-02: DPI-Aware 폰트 크기 계산
- `init_dwrite()`에서 DIP 크기를 물리 픽셀 크기로 변환
  - 현재: `dip_size = font_size_pt * (96.0f / 72.0f)` (항상 96 DPI)
  - 변경: `pixel_size = font_size_pt * (96.0f / 72.0f) * dpi_scale` (물리 DPI 반영)
- `CreateTextFormat` 호출 시 스케일된 폰트 크기 사용

### FR-03: DPI-Aware 글리프 래스터라이즈
- `CreateGlyphRunAnalysis` (IDWriteFactory2 v2 API)에서 변환 행렬에 DPI 스케일 반영
  - 현재: `DWRITE_MATRIX identity = {1,0,0,1,0,0}`
  - 변경: `DWRITE_MATRIX scale = {dpi_scale, 0, 0, dpi_scale, 0, 0}`
- v1 폴백 API의 `pixelsPerDip` 파라미터에도 `dpi_scale` 전달
- glyph_run의 `fontEmSize`는 DIP 기준 유지, 변환 행렬에서 스케일링

### FR-04: DPI-Aware 셀 메트릭 계산
- `compute_cell_metrics()`에서 DPI 스케일 반영
  - `cell_w`, `cell_h`, `ascent_px` 모두 물리 픽셀 단위로 계산
- `GlyphEntry`의 `offset_x`, `offset_y`, `advance_x`도 물리 픽셀 기준

### FR-05: DPI 변경 감지 및 Atlas 재생성
- `winui_app.cpp`에 `m_current_dpi_scale` (atomic<float>) 멤버 추가
- `CompositionScaleChanged` 이벤트에서 이전 DPI 스케일과 비교
- DPI 변경 감지 시 `m_dpi_change_requested` 플래그 설정
- 렌더 루프에서 DPI 변경 플래그 확인 → atlas 재생성 수행:
  1. 새 `AtlasConfig` (새 dpi_scale) 으로 `GlyphAtlas::create()` 재호출
  2. 렌더러에 새 atlas SRV 설정
  3. QuadBuilder 셀 크기 업데이트
  4. ConPTY + RenderState 그리드 리사이즈

### FR-06: 그리드 계산 수정
- 리사이즈 핸들러의 그리드 계산을 DPI-aware 셀 크기 기준으로 수정
  - 현재: `cols = physical_w / dip_cell_w` (부정확)
  - 변경: `cols = physical_w / physical_cell_w` (DPI-aware atlas의 셀 크기)
- 초기 StartTerminal에서도 동일하게 DPI-aware 셀 크기 사용

### FR-07: ADR-012 CJK Advance-Centering 호환성
- DPI 스케일 변경 후 CJK advance-centering이 올바르게 동작하는지 검증
- `quad_builder.cpp`의 centering 로직은 `cell_width` 기준이므로, DPI-aware cell_width가 전달되면 자동 적용
- 필요 시 CJK 글리프의 advance_x 재검증

---

## 4. Non-Functional Requirements (NFR)

| # | Requirement | Target |
|---|-------------|--------|
| NFR-01 | 100% DPI에서 기존 렌더링 유지 | `dpi_scale=1.0` 시 기존과 동일한 결과 (회귀 없음) |
| NFR-02 | Atlas 재생성 시간 | < 200ms (ASCII 128 + 자주 사용하는 CJK 프리캐시) |
| NFR-03 | DPI 변경 전환 시간 | 체감 지연 < 300ms (디바운스 100ms + atlas 재생성) |
| NFR-04 | 메모리 사용 | atlas 텍스처 하나만 유지 (구 atlas는 즉시 해제) |
| NFR-05 | 기존 Phase 3/4 테스트 유지 | 모든 기존 테스트 PASS 보존 |

---

## 5. Architecture Changes

### 5.1 변경 범위

| Module | File | Change Type | Detail |
|--------|------|-------------|--------|
| `AtlasConfig` | `glyph_atlas.h` | **수정** | `float dpi_scale = 1.0f` 필드 추가 |
| `GlyphAtlas::Impl` | `glyph_atlas.cpp` | **수정** | `dpi_scale` 저장, init_dwrite/compute_cell_metrics/rasterize_glyph에서 활용 |
| `GhostWinApp` | `winui_app.h` | **수정** | `m_current_dpi_scale`, `m_dpi_change_requested` 멤버 추가 |
| `GhostWinApp` | `winui_app.cpp` | **수정** | DPI 변경 감지 + atlas 재생성 로직 |
| `QuadBuilder` | — | 변경 없음 | `update_cell_size()` 호출로 자동 반영 |
| `DX11Renderer` | — | 변경 없음 | `set_atlas_srv()` 호출로 자동 반영 |
| `ConPtySession` | — | 변경 없음 | `resize()` 호출로 자동 반영 |
| `RenderState` | — | 변경 없음 | `resize()` 호출로 자동 반영 |
| HLSL shaders | — | 변경 없음 | constant buffer의 atlasScale이 새 atlas 크기 반영 |

### 5.2 데이터 흐름 (DPI 변경 시)

```
CompositionScaleChanged (UI Thread)
  ↓
m_dpi_change_requested = true, m_pending_dpi_scale = newScale
  ↓
RenderLoop (Render Thread) — 다음 프레임에서 감지
  ↓
┌─ GlyphAtlas::create(device, {dpi_scale=newScale})
│   ├── init_dwrite(): pixel_size = pt * (96/72) * dpi_scale
│   ├── compute_cell_metrics(): cell_w/h in physical pixels
│   └── init_atlas_texture(): 새 R8_UNORM 텍스처
├─ m_renderer->set_atlas_srv(new_atlas->srv())
├─ m_renderer->set_cleartype_params(...)
├─ builder.update_cell_size(new_cell_w, new_cell_h)
├─ cols = physical_w / new_cell_w, rows = physical_h / new_cell_h
├─ m_session->resize(cols, rows)
└─ m_state->resize(cols, rows)
```

### 5.3 스레드 안전성

| 연산 | 스레드 | 동기화 |
|------|--------|--------|
| `CompositionScaleChanged` 이벤트 | UI | atomic store (`m_pending_dpi_scale`) |
| DPI 변경 감지 + atlas 재생성 | Render | atomic load → 재생성 → `m_vt_mutex` lock for grid resize |
| atlas SRV 교체 | Render | 렌더 루프 내에서만 접근 (단일 스레드) |

기존 resize 패턴(`m_resize_requested` atomic flag)과 동일한 방식으로 `m_dpi_change_requested`를 처리한다. atlas 교체는 렌더 스레드에서만 수행하므로 추가 동기화 불필요.

---

## 6. Implementation Steps

### Step 1: AtlasConfig + GlyphAtlas DPI 지원 (S1-S3)

| # | Task | DoD |
|---|------|-----|
| S1 | `AtlasConfig`에 `float dpi_scale = 1.0f` 추가 | 컴파일 성공, 기존 코드 영향 없음 |
| S2 | `GlyphAtlas::Impl`에 `dpi_scale` 저장 + `init_dwrite()`에서 물리 픽셀 크기 계산 | `dpi_scale=1.5` 시 `pixel_size = 24px` (12pt 기준) |
| S3 | `compute_cell_metrics()`에서 DPI-aware 셀 크기 계산 | `dpi_scale=1.5` 시 `cell_w=14`, `cell_h=24` (근사치) |

### Step 2: 글리프 래스터라이즈 DPI 적용 (S4-S5)

| # | Task | DoD |
|---|------|-----|
| S4 | `rasterize_glyph()`에서 `CreateGlyphRunAnalysis` 변환 행렬에 dpi_scale 반영 | 고DPI에서 물리 픽셀 크기로 래스터라이즈 |
| S5 | v1 폴백 API `pixelsPerDip` 파라미터에 dpi_scale 전달 | IDWriteFactory2 없는 환경에서도 DPI 반영 |

### Step 3: WinUI App DPI 변경 감지 + Atlas 재생성 (S6-S8)

| # | Task | DoD |
|---|------|-----|
| S6 | `winui_app.h`에 `m_current_dpi_scale`, `m_pending_dpi_scale`, `m_dpi_change_requested` 추가 | 선언 완료 |
| S7 | `CompositionScaleChanged` 핸들러에서 DPI 변경 감지 + 플래그 설정 | DPI 변경 시 `m_dpi_change_requested=true` |
| S8 | `RenderLoop`에서 DPI 변경 플래그 확인 → atlas 재생성 + 그리드 리사이즈 | 모니터 이동 시 atlas 재생성 로그 출력 |

### Step 4: 초기 생성 DPI 적용 + 그리드 수정 (S9-S10)

| # | Task | DoD |
|---|------|-----|
| S9 | `InitializeD3D11()`에서 `CompositionScaleX()` 읽어 `StartTerminal()`에 전달 | 초기 atlas가 실제 DPI로 생성됨 |
| S10 | `StartTerminal()`에서 DPI-aware 셀 크기로 그리드 계산 | 150% DPI에서 정확한 cols/rows |

### Step 5: 검증 (S11-S13)

| # | Task | DoD |
|---|------|-----|
| S11 | 100% DPI 회귀 테스트 | 기존과 동일한 렌더링 결과 |
| S12 | 고DPI(150%) 스크린샷 비교 | 흐릿함 해소, 선명한 글리프 |
| S13 | 다중 모니터 DPI 전환 테스트 (가능 시) | 모니터 이동 시 자동 재래스터라이즈 |

---

## 7. Definition of Done (DoD)

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | 100% DPI에서 기존과 동일한 렌더링 | 스크린샷 비교 |
| 2 | 150%+ DPI에서 텍스트 선명도 향상 | 육안 + 스크린샷 비교 (이전/이후) |
| 3 | DPI 변경 시 atlas 자동 재생성 | 로그 확인 + 글리프 선명도 즉시 반영 |
| 4 | 그리드 행/열 계산 정확 | 고DPI에서 올바른 터미널 크기 |
| 5 | CJK 한글/한자 advance-centering 유지 | ADR-012 호환성 검증 |
| 6 | 커서 위치/크기 DPI-aware | 커서가 정확한 셀에 표시 |
| 7 | 기존 Phase 3/4 테스트 모두 PASS | 전체 테스트 실행 |
| 8 | atlas 재생성 시 메모리 누수 없음 | 구 atlas 해제 확인 |

---

## 8. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | Atlas 재생성 중 1-2 프레임 깜빡임 | 중 | 새 atlas 완성 후 atomic 교체 (더블 버퍼링). 100ms 디바운스로 잦은 재생성 방지 |
| R2 | CJK advance-centering 비율 변경 | 하 | ADR-012 로직은 cell_width 비례이므로 DPI 스케일 반영 자동. S13에서 검증 |
| R3 | 분수 DPI(125%, 175%)에서 서브픽셀 정렬 | 중 | DirectWrite NATURAL_SYMMETRIC이 분수 픽셀 처리. 래스터 결과 직접 검증 |
| R4 | atlas 텍스처 크기 증가 (고DPI → 더 큰 글리프) | 하 | 2x grow 전략 유지. 200% DPI에서도 4096x4096 한도 내 (ASCII 128자 기준 충분) |
| R5 | 스케일 행렬 vs emSize 스케일 방식 선택 | 중 | WT는 emSize 스케일, Alacritty는 font-size 스케일. 두 방식 모두 유효 — 스케일 행렬 방식 채택 (기존 코드 최소 변경) |

---

## 9. Dependencies

| Dependency | Status | Purpose |
|------------|--------|---------|
| Phase 4-A winui3-shell | ✅ 완료 (94%) | SwapChainPanel + CompositionScaleChanged 인프라 |
| Phase 4-B tsf-ime | ✅ 완료 (99%) | 조합 문자 래스터라이즈도 DPI 반영 필요 |
| ADR-010 Grayscale AA | ✅ 적용됨 | AA 모드 유지, DPI 스케일만 추가 |
| ADR-012 CJK Advance-Centering | ✅ 적용됨 | cell_width 비례 centering, DPI 자동 반영 |
| DirectWrite IDWriteFactory2 | ✅ 사용 중 | v2 CreateGlyphRunAnalysis 변환 행렬 지원 |

---

## 10. References

| Document | Path |
|----------|------|
| Phase 4 Master Plan | `docs/01-plan/features/winui3-integration.plan.md` |
| ADR-010 Grayscale AA | `docs/adr/010-grayscale-aa-composition.md` |
| ADR-012 CJK Advance-Centering | `docs/adr/012-cjk-advance-centering.md` |
| ClearType Composition 분석 | `docs/03-analysis/cleartype-composition-spacing.md` |
| Phase 3 DX11 Design | `docs/archive/2026-03/dx11-rendering/dx11-rendering.design.md` |
| Phase 4-B TSF IME 보고서 | `docs/archive/2026-04/tsf-ime/tsf-ime.report.md` |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-04-01 | Solit | Initial plan — FR-05 DPI-aware 래스터라이즈 |
