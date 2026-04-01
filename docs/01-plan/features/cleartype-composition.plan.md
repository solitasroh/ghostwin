# cleartype-composition Plan

> **Feature**: ClearType 서브픽셀 렌더링 — WT 동등 텍스트 선명도
> **Project**: GhostWin Terminal
> **Phase**: 4 품질 개선 (Phase B)
> **Date**: 2026-04-01
> **Author**: Solit
> **Revision**: 3.1 (4-agent 팩트체크 반영)
> **Research**: `docs/00-research/research-cleartype-90-percent.md`

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | GhostWin 텍스트 선명도 74/100 (블라인드 실측), Alacritty(82)보다 8점 열위. 원인: `CreateSwapChainForComposition` + `PREMULTIPLIED` → ClearType 원리적 불가 (FACT: MS 공식 문서) |
| **Solution** | WT 참조 아키텍처: `DCompositionCreateSurfaceHandle` + `CreateSwapChainForCompositionSurfaceHandle(ALPHA_MODE_IGNORE)` + `ISwapChainPanelNative2::SetSwapChainHandle` + ClearType 3x1 + Dual Source Blending. **단, PoC 선검증 필수** |
| **Function/UX** | ClearType per-channel 서브픽셀 렌더링으로 글리프 선명도 대폭 향상. WT 불투명 배경 수준 목표 |
| **Core Value** | Onboarding 핵심 가치 "WT 이상의 렌더링 품질" 달성 시도. 투명 배경 trade-off |

---

## 0. 선행 조건: PoC (Proof of Concept)

> **전체 Plan 실행 전에 반드시 PoC를 수행해야 함.**
> ADR-010에서 "CompositionSurfaceHandle + IGNORE → 검은 화면"으로 실패한 기록이 있으며,
> "v1 API 사용이 원인"이라는 진단은 **가설(ASSUMPTION)**이다 — GhostWin에서 v2를 시도한 적 없음.

### PoC 범위

| # | Task | 성공 기준 |
|---|------|----------|
| PoC-1 | `DCompositionCreateSurfaceHandle` 호출 성공 | HANDLE 반환, HRESULT 성공 |
| PoC-2 | `IDXGIFactoryMedia` QI 성공 | 인터페이스 획득 |
| PoC-3 | `CreateSwapChainForCompositionSurfaceHandle(ALPHA_MODE_IGNORE)` 성공 | 스왑체인 생성 |
| PoC-4 | `ISwapChainPanelNative2` QI 성공 (WinUI3 SwapChainPanel) | 인터페이스 획득 |
| PoC-5 | `SetSwapChainHandle(handle)` 호출 후 화면 표시 | **검은 화면이 아닌** clear color 표시 |

**PoC-5 실패 시**: 전체 Plan 폐기, 대안 아키텍처 검토 (HWND child 또는 XAML Islands)

---

## 1. Background

### 1.1 블라인드 실측 결과 (FACT)

| 터미널 | 평가자#1 | #2 | #3 | 평균 |
|--------|:-------:|:--:|:--:|:----:|
| GhostWin (Grayscale+Factory3) | 72 | 72 | 78 | **74** |
| Alacritty (HWND+WGL) | 82 | 85 | 80 | **82** |

### 1.2 근본 원인 (FACT)

`DXGI_ALPHA_MODE_PREMULTIPLIED` 환경에서 ClearType는 수학적으로 불가능 (3개 독립 alpha → 1개 공유 alpha 축소 시 서브픽셀 정보 소실). MS 공식 문서: "alpha mode other than IGNORE → ClearType automatically changes to Grayscale."

### 1.3 ADR-010 재분석 (ASSUMPTION — 미검증)

ADR-010에서 "CompositionSurfaceHandle + IGNORE → 검은 화면"으로 기록됨. 이 실패가 `SetSwapChain` (v1) 사용 때문이라는 진단은 **가설**:
- ADR-010에 v1/v2 구분에 대한 기록 없음
- WT가 v2 (`SetSwapChainHandle`)로 성공한다는 사실은 강력한 정황 증거
- **그러나 GhostWin WinUI3 환경에서 v2를 시도한 적이 없음 → PoC 필수**

### 1.4 WT 참조 아키텍처 (FACT — WT 소스에서 확인됨)

```
DCompositionCreateSurfaceHandle()
→ IDXGIFactoryMedia::CreateSwapChainForCompositionSurfaceHandle(ALPHA_MODE_IGNORE)
→ ISwapChainPanelNative2::SetSwapChainHandle(handle)
→ ClearType 3x1 래스터 + Dual Source Blending
```

출처: WT PR #10023, #12242, AtlasEngine.r.cpp, BackendD3D.cpp (4-agent 검증 완료)

---

## 2. Goal

ClearType 서브픽셀 렌더링으로 텍스트 선명도 74/100 → **90/100 수준** (목표, 미검증).

---

## 3. Functional Requirements (FR)

### FR-01: CompositionSurfaceHandle 스왑체인 생성
- `DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS, nullptr, &handle)` 호출
  - 헤더: `dcomp.h`, 링크: `dcomp.lib` (CMakeLists.txt에 이미 존재)
- `IDXGIFactoryMedia::CreateSwapChainForCompositionSurfaceHandle(device, handle, &desc, nullptr, &swapchain)` 호출
  - 헤더: `dxgi1_3.h` (이미 include됨), 링크: `dxgi.lib` (이미 링크됨)
- `desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE`
- 기존 `CreateSwapChainForComposition` 경로: **ClearType 미지원 환경(RDP, Grayscale 요청)에서 폴백으로 유지**

### FR-02: SwapChainPanel 연결 (v2 API)
- `panel.as<ISwapChainPanelNative2>()` QI
- `SetSwapChainHandle(handle)` 호출
- 헤더: `<microsoft.ui.xaml.media.dxinterop.h>` (이미 include됨)
- **QI 실패 시**: 기존 v1 `SetSwapChain` 경로로 폴백 (Grayscale 유지)
- WinAppSDK 1.6에 `ISwapChainPanelNative2` 정의 존재 확인됨 (헤더 직접 확인)
  - 단, MS 공식 WinAppSDK API 문서에 별도 페이지 없음 — 런타임 QI 실패 가능성 대비 필수

### FR-03: ClearType 3x1 글리프 래스터라이즈
- `IDWriteFactory2::CreateGlyphRunAnalysis` (또는 Factory3) 오버로드 사용 — **v1 API에는 antialiasMode 파라미터 없음**
- AA 모드: `DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE` (Factory2+ 오버로드의 7번째 파라미터)
- `GetAlphaTextureBounds(DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds)` 사용
- `CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1, ...)` → 3바이트/픽셀 RGB 커버리지
- 아틀라스 텍스처: 현재 이미 `DXGI_FORMAT_R8G8B8A8_UNORM` (변경 불필요)
  - RGB 채널: 서브픽셀 커버리지, **A 채널: `max(R,G,B)`** (WT 패턴, Grayscale 폴백용)
- Grayscale 폴백: ClearType 비활성 환경에서 기존 `ALIASED_1x1` 경로 유지

### FR-04: Dual Source Blending 셰이더
- PS 컴파일 타겟: **`ps_5_0`** (현재 `ps_4_0` — Dual Source 출력은 SM 5.0 필요)
- PS 출력 2개: `SV_Target0` (color) + `SV_Target1` (weights)
- ClearType 경로: `weights.rgb = alphaCorrected_rgb * fg.a`, `color.rgb = weights.rgb * fg.rgb`
- Grayscale 경로: `weights = alphaCorrected.aaaa`, `color = alphaCorrected * fg.rgb`
- 배경 경로: `color = float4(bg.rgb, 1)`, `weights = float4(1,1,1,1)` (완전 불투명)

### FR-05: Dual Source Blend State
- `SrcBlend = D3D11_BLEND_ONE`
- `DestBlend = D3D11_BLEND_INV_SRC1_COLOR`
- `SrcBlendAlpha = D3D11_BLEND_ONE`
- `DestBlendAlpha = D3D11_BLEND_INV_SRC1_ALPHA`
- 기존 `INV_SRC_ALPHA` blend state 교체
- **렌더 순서 보장 검증 필요**: 단일 DrawCall에서 배경→텍스트 순서가 QuadBuilder에 의해 보장되는지 확인 (2-pass 분리 고려)

---

## 4. Non-Functional Requirements (NFR)

| # | Requirement | Target |
|---|-------------|--------|
| NFR-01 | 텍스트 선명도 | 블라인드 평가로 측정 (목표: 90 수준, 미보장) |
| NFR-02 | 기존 테스트 유지 | 10/10 PASS |
| NFR-03 | SM 5.0 호환 | FL 11_0 필수 (FL 10_0 폴백 불가 — Grayscale로 전환) |
| NFR-04 | 한글 IME / CJK / DPI 호환 | 기존 기능 유지 |

---

## 5. Implementation Steps

### Step 0: PoC (최우선)

| # | Task | File | DoD |
|---|------|------|-----|
| PoC-1~5 | 섹션 0 전체 수행 | dx11_renderer.cpp, winui_app.cpp | **화면에 clear color 표시** |

**PoC 실패 시 Plan 폐기.**

### Step 1: 스왑체인 전환 (S1~S3)

| # | Task | File | DoD |
|---|------|------|-----|
| S1 | `DCompositionCreateSurfaceHandle` + `CreateSwapChainForCompositionSurfaceHandle(IGNORE)` | dx11_renderer.cpp | 스왑체인 생성 성공 |
| S2 | HANDLE 멤버 + `composition_handle()` 접근자 | dx11_renderer.h/cpp | 컴파일 성공 |
| S3 | `ISwapChainPanelNative2::SetSwapChainHandle` + v1 폴백 | winui_app.cpp | 화면 표시 |

### Step 2: Dual Source (S4~S5)

| # | Task | File | DoD |
|---|------|------|-----|
| S4 | `INV_SRC_ALPHA` → `INV_SRC1_COLOR` blend state | dx11_renderer.cpp | 컴파일 성공 |
| S5 | PS `ps_4_0` → `ps_5_0`, Dual Source 출력 | shader_ps.hlsl, dx11_renderer.cpp | ClearType+Grayscale+bg 분기 동작 |

### Step 3: ClearType 래스터 (S6~S7)

| # | Task | File | DoD |
|---|------|------|-----|
| S6 | `CLEARTYPE_3x1` 래스터 + A=max(R,G,B) | glyph_atlas.cpp | ClearType 글리프 아틀라스 저장 |
| S7 | Grayscale 폴백 (ClearType 미지원 시) | glyph_atlas.cpp | RDP 환경 정상 |

### Step 4: 검증 (S8~S10)

| # | Task | DoD |
|---|------|-----|
| S8 | 빌드 + 기존 테스트 | 0 errors, 10/10 PASS |
| S9 | 육안 확인 (ClearType 선명도) | 스크린샷 |
| S10 | 블라인드 재평가 (Alacritty 비교) | 목표: Alacritty(82) 이상 |

---

## 6. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| **R1** | **PoC 실패 (SetSwapChainHandle 검은 화면)** | **치명** | PoC를 전체 구현 전에 수행. 실패 시 HWND child 또는 XAML Islands 대안 |
| R2 | `ISwapChainPanelNative2` QI 실패 | 상 | v1 `SetSwapChain` 폴백 (Grayscale 유지) |
| R3 | SM 5.0에서 FL 10_0 디바이스 미지원 | 중 | FL 10_0에서 Grayscale 폴백 |
| R4 | 렌더 순서: 단일 DrawCall에서 bg→text 미보장 | 중 | QuadBuilder 정렬 확인 or 2-pass DrawCall 분리 |
| R5 | 아틀라스 ClearType 3x1 → B8G8R8A8 A채널 처리 | 중 | A=max(R,G,B) (WT 패턴) |
| R6 | CJK advance-centering + ClearType 호환 | 낮 | ADR-012 로직은 AA 방식과 무관 |

---

## 7. Fact/Assumption 분류

| 주장 | 분류 | 비고 |
|------|:----:|------|
| PREMULTIPLIED에서 ClearType 불가 | **FACT** | MS 공식 문서 |
| GhostWin 74/100, Alacritty 82/100 | **FACT** | 블라인드 3인 실측 |
| WT가 CompositionSurfaceHandle + IGNORE 사용 | **FACT** | 4-agent 소스 검증 |
| WT blend state ONE/INV_SRC1_COLOR | **FACT** | BackendD3D.cpp 확인 |
| ISwapChainPanelNative2 헤더 존재 (WinAppSDK 1.6) | **FACT** | 헤더 파일 직접 확인 |
| ADR-010 실패 원인 = v1 API | **ASSUMPTION** | PoC로 검증 필요 |
| GhostWin에서 v2가 동작할 것 | **INFERENCE** | WT 성공 사례 기반 추론 |
| 90/100 달성 가능 | **SPECULATION** | 목표일 뿐, 보장 없음 |

---

## 8. Dependencies

| Dependency | Status | 비고 |
|------------|:------:|------|
| `dcomp.h` / `dcomp.lib` | 이미 링크됨 | CMakeLists.txt 확인 완료 |
| `dxgi1_3.h` / `dxgi.lib` | 이미 링크됨 | CMakeLists.txt 확인 완료 |
| `ISwapChainPanelNative2` | 헤더 존재 (WinAppSDK 1.6) | 런타임 QI는 PoC에서 검증 |
| `IDWriteFactory2+` | 이미 사용 중 | glyph_atlas.cpp에서 Factory2 QI |
| PS SM 5.0 | FL 11_0 필수 | 현재 `ps_4_0` → `ps_5_0` 변경 필요 |

---

## 9. References

| Document | Source |
|----------|--------|
| ClearType 90%+ 리서치 | `docs/00-research/research-cleartype-90-percent.md` |
| ADR-010 Grayscale AA | `docs/adr/010-grayscale-aa-composition.md` |
| WT PR #10023 | CompositionSurfaceHandle 도입 (VERIFIED) |
| WT PR #12242 | ClearType blending 구현 (VERIFIED) |
| WT AtlasEngine.r.cpp | 스왑체인 ALPHA_MODE 분기 (VERIFIED) |
| WT BackendD3D.cpp | Dual Source blend state (VERIFIED) |
| MS D2D Alpha Modes | ClearType IGNORE 필수 근거 (FACT) |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial plan |
| 2.0 | 2026-03-31 | Solit | WT 아키텍처 리서치 기반 전면 개정 |
| 3.0 | 2026-04-01 | Solit | 10-agent 리서치 + 블라인드 실측 반영 |
| 3.1 | 2026-04-01 | Solit | 4-agent 팩트체크: PoC 선행 추가, ASSUMPTION/FACT 분류, ps_5_0 필수, 아틀라스 현황 보정, A=max(R,G,B) 추가, WinAppSDK 1.6 보정, 상수명 보정, 렌더 순서 리스크 추가 |
