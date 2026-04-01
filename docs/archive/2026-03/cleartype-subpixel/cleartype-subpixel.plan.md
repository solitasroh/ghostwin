# cleartype-subpixel Plan

> **Feature**: ClearType 서브픽셀 안티앨리어싱
> **Project**: GhostWin Terminal
> **Phase**: 4-C (Master: winui3-integration FR-09)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: 없음 (현재 Win32 HWND에서 독립 구현 가능)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 GlyphAtlas가 그레이스케일 AA만 지원하여 LCD 모니터에서 글자 선명도가 떨어짐 |
| **Solution** | DirectWrite 서브픽셀 래스터화 + HLSL 픽셀 셰이더 RGB 가중 블렌딩으로 ClearType 구현 |
| **Function/UX** | LCD 모니터에서 글자 가장자리가 RGB 서브픽셀 단위로 렌더링되어 선명도 향상 |
| **Core Value** | 그레이스케일 대비 ~3배 수평 해상도 향상. 장시간 코딩 시 가독성 개선 |

---

## 1. Background

Phase 3 `GlyphAtlas`는 DirectWrite의 ClearType 3x1 비트맵을 그레이스케일로 변환하여 사용한다.
이는 D3D11 텍스처 위에서 ClearType을 직접 처리하기 어렵기 때문이었다.

Windows Terminal의 AtlasEngine은 `dwrite-hlsl` 패턴으로 이를 해결한다:
1. DirectWrite에서 RGB 서브픽셀 비트맵을 그대로 유지
2. HLSL 픽셀 셰이더에서 R/G/B 채널별로 독립 블렌딩

---

## 2. Functional Requirements

### FR-01: DirectWrite 서브픽셀 래스터화
- `DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC` + `DWRITE_MEASURING_MODE_NATURAL`
- 글리프 비트맵을 RGB 3채널 텍스처로 아틀라스에 저장
- 아틀라스 포맷: `DXGI_FORMAT_R8G8B8A8_UNORM` (현재 `R8_UNORM`에서 전환)

### FR-02: HLSL 서브픽셀 블렌딩
- 픽셀 셰이더에서 텍스처 R/G/B 각 채널을 fg 색상 채널별 알파로 사용
- `output.r = lerp(bg.r, fg.r, tex.r)` 패턴
- 배경색 위에 서브픽셀 블렌딩 적용

### FR-03: 그레이스케일 폴백
- ClearType 비활성 환경 (원격 데스크톱 등) 감지
- 폴백 시 기존 그레이스케일 경로 유지

---

## 3. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | GlyphAtlas 아틀라스 포맷 R8 → R8G8B8A8 전환 | RGB 서브픽셀 텍스처 생성 확인 |
| S2 | DirectWrite 래스터화 모드 변경 | 서브픽셀 비트맵 획득 |
| S3 | HLSL 픽셀 셰이더 서브픽셀 블렌딩 구현 | RGB 채널별 독립 블렌딩 |
| S4 | ClearType 비활성 환경 감지 + 그레이스케일 폴백 | 원격 데스크톱에서 정상 동작 |
| S5 | LCD 모니터 선명도 A/B 비교 테스트 | 그레이스케일 대비 향상 육안 확인 |

---

## 4. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | LCD 모니터에서 서브픽셀 렌더링 동작 | 글자 가장자리 RGB 프린지 확인 |
| 2 | ANSI 색상 + 서브픽셀 정상 조합 | Starship 프롬프트 색상 + 선명도 |
| 3 | 한글 폴백 폰트에도 서브픽셀 적용 | Malgun Gothic 서브픽셀 확인 |
| 4 | 원격 데스크톱에서 그레이스케일 폴백 | ClearType 비활성 환경 정상 |
| 5 | 기존 테스트 PASS 유지 | 23/23 PASS |

---

## 5. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | 아틀라스 메모리 4배 증가 (R8→R8G8B8A8) | 중 | 아틀라스 크기 1024→2048 허용, 또는 R8G8B8 packed 포맷 조사 |
| R2 | 배경색 위 서브픽셀 블렌딩 색번짐 | 중 | Windows Terminal dwrite-hlsl 감마 보정 패턴 참고 |

---

## 6. References

| Document | Path |
|----------|------|
| DX11 GPU 렌더링 리서치 | `docs/00-research/research-dx11-gpu-rendering.md` |
| Phase 3 GlyphAtlas 구현 | `src/renderer/glyph_atlas.h/cpp` |
| Phase 3 픽셀 셰이더 | `src/renderer/shader_ps.hlsl` |
