# ADR-010: Composition Swapchain ClearType 렌더링

- **상태**: ~~채택~~ → **해결** (2026-04-03 CreateAlphaTexture + Dual Source Blending)
- **날짜**: 2026-03-31 (개정: 2026-04-02, **최종 해결: 2026-04-03**)
- **관련**: Phase 4-C cleartype-composition, ADR-009, WT AtlasEngine, PDCA archive 2026-04

## 배경

WinUI3 SwapChainPanel은 `DXGI_ALPHA_MODE_PREMULTIPLIED` 합성 스왑체인을 사용한다. ClearType 서브픽셀 렌더링은 **불투명 배경에서만 동작**하며, premultiplied alpha 합성 환경에서는 원리적으로 사용할 수 없다.

## 문제

Phase 4-C에서 구현한 ClearType 3x1 서브픽셀 렌더링이 SwapChainPanel에서 무력화되어 텍스트가 흐릿하고 RGB 색번짐(fringing)이 발생했다. 10회 반복 평가에서 초기 62/100 → ClearType 시도 최대 70/100에 머물렀다.

## 시도한 대안들 (Phase 4-C, 2026-03)

| 대안 | 결과 | 실패 원인 |
|------|------|-----------|
| `ALPHA_MODE_IGNORE` (SwapChainPanel) | 검은 화면 | `ISwapChainPanelNative` v1 API 사용 |
| `CompositionSurfaceHandle` + IGNORE | 검은 화면 | `SetSwapChain` v1 사용 (v2 미사용) |
| HWND Child Window | 검은 화면 | XAML 레이어가 HWND 위에 그려짐 |
| Dual Source Blending (PREMULTIPLIED) | 검은 화면 | `INV_SRC1_COLOR`가 PREMULTIPLIED 합성 비호환 |
| 셰이더 내부 ClearType lerp | 70/100 | PSSetConstantBuffers 누락 + 이중 감마 |

## 결정 (2026-03)

**Grayscale AA**를 채택한다. ClearType를 포기하는 대신 RGB 프린징을 완전 제거하고, DWrite 감마 보정으로 대비를 최적화한다.

## 결과 (2026-03)

- 텍스트 렌더링 자체 평가: 62/100 → **83/100** (+21점)
- 블라인드 실측 (2026-04): **74/100** (자체 평가 83은 과대였음)

---

## 개정: ClearType 파이프라인 재시도 (2026-04-02)

### 새로운 발견

10-agent 병렬 리서치와 4-agent 팩트체크를 통해 다음을 발견:

1. **ADR-010의 "검은 화면" 실패 원인**: `ISwapChainPanelNative` (v1) API 사용이 원인. `ISwapChainPanelNative2::SetSwapChainHandle` (v2)로 전환하면 **ALPHA_MODE_IGNORE가 SwapChainPanel에서 동작** (WT PR #10023과 동일 패턴)

2. **PoC 검증 성공**: `DCompositionCreateSurfaceHandle` + `CreateSwapChainForCompositionSurfaceHandle(IGNORE)` + `SetSwapChainHandle(v2)` — 화면 정상 표시 확인

3. **Dual Source Blending**: 이 GPU에서 `INV_SRC1_COLOR`가 동작하지 않음 (ZERO 테스트로 확정). 원인 미확정 (드라이버 이슈 가능)

### 현재 파이프라인 (개정 후)

```
ALPHA_MODE_IGNORE 스왑체인 (CompositionSurfaceHandle + SetSwapChainHandle v2)
→ ClearType 3x1 래스터 (Factory v1 API, DWRITE_RENDERING_MODE_DEFAULT)
→ sRGB 감마 커브: pow(coverage, 0.4545)
→ premultiplied alpha blend (ONE / INV_SRC_ALPHA)
```

### 파이프라인 진단 결과 (모두 FACT)

| 단계 | 상태 | 증거 |
|------|:----:|------|
| Composition 레이어 | **블러 없음** | 4px stripe + 1px line 테스트 선명 |
| ClearType 래스터 | **정상** | 60-84% 픽셀 R≠G≠B, 바이트 덤프 확인 |
| Atlas 텍스처 | **선명** | Atlas 덤프에서 글리프 + RGB 프린지 확인 |
| **블렌딩** | **per-channel 손실** | `INV_SRC_ALPHA` 단일 alpha → ClearType RGB 정보가 dest 블렌딩에서 미활용 |

### 개선 효과

| 변경 | 효과 |
|------|------|
| ALPHA_MODE_IGNORE (PoC) | 검은 화면 → 정상 표시 |
| sRGB 감마 pow(0.4545) | 가장자리 전환 급격 → 두께/선명도 개선 |
| NATURAL_SYMMETRIC → DEFAULT | 수직 AA 감소 → edge 약간 좁아짐 |

---

## 최종 해결 (2026-04-03)

### 근본 원인 (13명 에이전트 + 3명 리서치로 확정)

1. **D2D DrawGlyphRun은 premultiplied RT에서 linear AA 수행** → 에지 85 (소프트)
   - MSDN: "ClearType on premultiplied = unpredictable results"
2. **CreateAlphaTexture는 gamma 공간 AA 수행** → 에지 139 (선명)
3. **Dual Source Blending "미동작"은 셰이더 구현 버그** → D3D11 FL 11_0은 스펙 필수 지원

### 최종 파이프라인

```
CreateAlphaTexture(system gamma ~1.8, ClearType 3x1)
→ BGRA 패킹 (B8G8R8A8_UNORM atlas)
→ raw coverage (셰이더 감마 보정 없음 — 이중 감마 방지)
→ Dual Source Blending (INV_SRC1_COLOR, per-channel GPU 블렌딩)
→ result = color + dest * (1 - weights.rgb)
```

### 사용자 확인

> "확실히 거의 비슷한 수준까지 올라왔어. 글자의 폭이나 너비 그리고 글자간 간격만 조절하면 굉장히 수준 높은 터미널이 될것 같아."

### 이전 결정들의 수정

| 이전 결정 | 수정 | 근거 |
|----------|------|------|
| "Dual Source 미동작" | **동작 확인** | 셰이더 구현 버그였음. D3D11 FL 11_0 필수 |
| "Grayscale AA 채택" | **ClearType 복원** | CreateAlphaTexture + Dual Source로 per-channel 가능 |
| "HWND child 필요" | **불필요** | SwapChainPanel + Dual Source로 충분 |
| "D2D DrawGlyphRun 사용" | **제거** | premultiplied linear AA가 소프트의 근본 원인 |

### 향후 작업

1. 글자 폭/높이/간격 조정 (사용자 피드백)
2. ADR-012 관련 CJK advance-centering 개선
