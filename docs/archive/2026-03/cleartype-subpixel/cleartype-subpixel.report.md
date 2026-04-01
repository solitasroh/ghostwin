# cleartype-subpixel PDCA Completion Report

> **Feature**: ClearType 서브픽셀 안티앨리어싱
> **Project**: GhostWin Terminal
> **Phase**: 4-C (Master: winui3-integration FR-09)
> **Date**: 2026-03-30
> **Match Rate**: 99.3%
> **Iterations**: 0

---

## Executive Summary

### 1.1 Overview

| Item | Value |
|------|-------|
| Feature | cleartype-subpixel |
| Duration | 1 day (2026-03-30) |
| Files Changed | 2 |
| Lines Changed | ~40 |
| Match Rate | 99.3% (14/14 items) |

### 1.2 Results

| Metric | Target | Actual |
|--------|--------|--------|
| FR Implementation | 3/3 | 3/3 (100%) |
| DoD Completion | 5/5 | 5/5 (100%) |
| QC Criteria | 5/5 | 5/5 (100%) |
| Tests | All pass | 23/23 PASS |
| Overall Match Rate | 90% | 99.3% |

### 1.3 Value Delivered

| Perspective | Result |
|-------------|--------|
| **Problem** | Phase 3 GlyphAtlas가 ClearType 3x1 RGB 데이터를 그레이스케일로 변환하여 LCD 서브픽셀 해상도를 버리고 있었음 |
| **Solution** | R8_UNORM → R8G8B8A8_UNORM 텍스처 전환 + HLSL PS 채널별 독립 블렌딩 + 감마 보정(pow 2.2) + ClearType 비활성 환경 자동 감지/폴백 구현 |
| **Function/UX** | 400% 확대 시 글자 가장자리에 RGB 서브픽셀 프린지 확인. 감마 보정으로 색번짐 최소화. 고DPI 모니터에서는 체감 차이 미미 |
| **Core Value** | Phase 3 Known Limitations 중 "ClearType 서브픽셀" 항목 해소. R8G8B8A8 포맷은 이후 Nerd Font 컬러 이모지에도 활용 가능 |

---

## 2. Implementation Summary

### 2.1 Architecture

```
DirectWrite ClearType 3x1 RGB (3B/px)
    ↓ glyph_atlas.cpp — RGB → RGBA 확장 (A = max(R,G,B))
    ↓ R8G8B8A8_UNORM 텍스처 업로드 (4B/px)
    ↓ shader_ps.hlsl — 감마 보정 + lerp(bg, fg, tex.rgb) 채널별 블렌딩
    ↓ premultiplied alpha 출력 — float4(blended * tex.a, tex.a)
    ↓ 결과: ClearType 서브픽셀 AA
```

### 2.2 Changed Files

| File | Change | Detail |
|------|--------|--------|
| `src/renderer/glyph_atlas.cpp` | 수정 | R8→R8G8B8A8 포맷, RGB 직접 저장, ClearType 감지, aliased 폴백 |
| `src/renderer/shader_ps.hlsl` | 수정 | `Texture2D<float4>`, 감마 보정 + 채널별 서브픽셀 블렌딩 |

### 2.3 Unchanged (호환성 유지)

| Module | Reason |
|--------|--------|
| `dx11_renderer.cpp` BlendState | premultiplied alpha 불변 (ONE / INV_SRC_ALPHA) |
| `quad_builder.cpp` | QuadInstance 구조 무관 |
| `shader_vs.hlsl` | VS 출력 불변 |
| 2-pass 렌더링 (ADR-008) | 배경→텍스트 순서 불변 |

---

## 3. Design Decisions

| Decision | Implementation | Rationale |
|----------|---------------|-----------|
| A = max(R,G,B) | 삼항 연산자 체인 | `std::max({})` 초기화 리스트는 MSVC에서 windows.h `max` 매크로 충돌 |
| 감마 보정 pow(2.2) | HLSL 선형 공간 블렌딩 | 밝은 배경 + 어두운 텍스트 색번짐 방지 |
| ClearType 런타임 감지 | `SPI_GETCLEARTYPE` | 컴파일 타임 상수보다 RDP 등 동적 환경 대응 가능 |
| aliased 폴백 R=G=B=A | 동일 RGBA 채널 복제 | PS의 `lerp(bg, fg, tex.rgb)` 결과가 단일 알파 블렌딩과 수학적으로 동일 |

---

## 4. Test Results

| Suite | Tests | Result |
|-------|-------|--------|
| vt_core_test | 7 | 7/7 PASS |
| vt_bridge_cell_test | 6 | 6/6 PASS |
| render_state_test | 5 | 5/5 PASS |
| dx11_render_test | 5 | 5/5 PASS |
| **Total** | **23** | **23/23 PASS** |

---

## 5. QC Verification

| # | Criteria | Status | Evidence |
|---|----------|:------:|----------|
| QC-01 | LCD 서브픽셀 렌더링 | PASS | 400% 확대 시 RGB 프린지 확인 (사용자 육안) |
| QC-02 | 그레이스케일 폴백 | PASS | R=G=B=A 로직 검증 |
| QC-03 | 2-pass (ADR-008) 호환 | PASS | BlendState 미변경 |
| QC-04 | BlendState 호환 | PASS | premultiplied alpha 불변 |
| QC-05 | 기존 테스트 23/23 | PASS | 전체 빌드 + 테스트 |

---

## 6. Lessons Learned

| Topic | Learning |
|-------|---------|
| MSVC `max` 매크로 | `windows.h` 포함 시 `std::max({initializer_list})` 컴파일 불가. 삼항 연산자나 `(std::max)` 괄호 패턴 필요 |
| 고DPI와 ClearType | 125% 이상 배율에서 서브픽셀 효과는 육안으로 거의 차이 없음. 그러나 저DPI LCD에서는 여전히 유의미 |
| R8G8B8A8 재활용 | 서브픽셀용 RGBA 포맷은 컬러 이모지(Nerd Font Phase 4-D)에서도 그대로 활용 가능 |

---

## Version History

| Version | Date | Author |
|---------|------|--------|
| 1.0 | 2026-03-30 | Solit |
