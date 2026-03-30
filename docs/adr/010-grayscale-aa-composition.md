# ADR-010: Composition Swapchain에서 Grayscale AA 채택

- **상태**: 채택
- **날짜**: 2026-03-31
- **관련**: Phase 4-F cleartype-composition, ADR-009, WT AtlasEngine.api.cpp:516

## 배경

WinUI3 SwapChainPanel은 `DXGI_ALPHA_MODE_PREMULTIPLIED` 합성 스왑체인을 사용한다. ClearType 서브픽셀 렌더링은 **불투명 배경에서만 동작**하며, premultiplied alpha 합성 환경에서는 원리적으로 사용할 수 없다.

## 문제

Phase 4-C에서 구현한 ClearType 3x1 서브픽셀 렌더링이 SwapChainPanel에서 무력화되어 텍스트가 흐릿하고 RGB 색번짐(fringing)이 발생했다. 10회 반복 평가에서 초기 62/100 → ClearType 시도 최대 70/100에 머물렀다.

## 시도한 대안들

| 대안 | 결과 | 실패 원인 |
|------|------|-----------|
| `ALPHA_MODE_IGNORE` (SwapChainPanel) | 검은 화면 | SwapChainPanel이 IGNORE 미지원 |
| `CompositionSurfaceHandle` + IGNORE | 검은 화면 | 동일 — WinUI3 제약 |
| HWND Child Window | 검은 화면 | XAML 레이어가 HWND 위에 그려짐 |
| Dual Source Blending (PREMULTIPLIED) | 검은 화면 | `INV_SRC1_COLOR`가 PREMULTIPLIED 합성 비호환 |
| 셰이더 내부 ClearType lerp | 70/100 | bgColor가 실제 framebuffer 배경이 아님 |

## 결정

**Grayscale AA**를 채택한다. ClearType를 포기하는 대신 RGB 프린징을 완전 제거하고, DWrite 감마 보정으로 대비를 최적화한다.

### 최적 파라미터 (10회 반복 평가로 도출)

```
AA 모드:       DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE
렌더링 모드:   DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC
Contrast:      시스템값 + 0.25 (0.5=too thick, 0.15=too thin)
감마 보정:     lhecker/dwrite-hlsl 13-entry 룩업 테이블
셰이더:        Single output, premultiplied alpha
```

### 셰이더 핵심 로직

```hlsl
float alpha = glyph.a;  // Grayscale: 단일 채널
float contrasted = DWrite_EnhanceContrast(alpha, k);
float corrected = DWrite_ApplyAlphaCorrection(contrasted, intensity, gammaRatios);
float3 color = fgColor.rgb * corrected;
return float4(color, corrected);  // premultiplied
```

## 근거

1. **Windows Terminal도 동일**: 투명 배경 시 ClearType → Grayscale 강제 전환 (`AtlasEngine.api.cpp:516-518`)
2. **Alacritty도 Grayscale**: 기본 렌더링 모드가 Grayscale AA
3. **RGB 프린징 완전 제거**: ClearType의 per-channel 블렌딩 없이 단일 alpha로 깨끗한 가장자리
4. **평가 결과**: 83/100 (WT 비교 88/100), Alacritty 대비 76/100 (동등 이상)

## 결과

- 텍스트 렌더링 평가: 62/100 → **83/100** (+21점)
- WT Grayscale 대비: **22/25** ("매우 유사한 수준")
- Alacritty 대비: **20/25** ("동등하거나 약간 나은 수준")
- RGB 프린징: **완전 제거**

## 향후 참고

ClearType가 필요한 경우 `CreateSwapChainForHwnd` + `ALPHA_MODE_IGNORE` 경로(Phase 3)를 사용해야 한다. SwapChainPanel 안에서는 Grayscale이 유일한 선택.
