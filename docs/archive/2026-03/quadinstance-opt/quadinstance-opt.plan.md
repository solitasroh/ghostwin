# quadinstance-opt Plan

> **Feature**: QuadInstance StructuredBuffer 32B 최적화
> **Project**: GhostWin Terminal
> **Phase**: 4-E (Master: winui3-integration FR-11)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: 없음 (현재 Win32 HWND에서 독립 구현 가능)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 QuadInstance가 R32 Input Layout 제약으로 68B/instance이며, GPU 대역폭 비효율 (ADR-007) |
| **Solution** | Input Layout 기반 버텍스 버퍼 → StructuredBuffer + SV_InstanceID 기반 읽기로 전환하여 32B/instance 달성 |
| **Function/UX** | 렌더링 결과 동일, 내부 GPU 대역폭 ~53% 절감 |
| **Core Value** | 고해상도(4K) + 대규모 셀(200x50+)에서 GPU 병목 방지. 향후 멀티 터미널 Pane 성능 확보 |

---

## 1. Background

Phase 3에서 ADR-007로 결정한 R32 QuadInstance (68B)는 `CreateInputLayout` 타입 불일치 해결을 위한 것이었다.
StructuredBuffer는 Input Layout을 우회하므로 이 제약이 없다. Windows Terminal AtlasEngine도 StructuredBuffer 패턴을 사용한다.

### 현재 vs 목표

| 항목 | Phase 3 (R32) | Phase 4 (StructuredBuffer) |
|------|:---:|:---:|
| Instance 크기 | 68B | 32B |
| Input Layout | 필요 (17 elements) | 불필요 |
| 셰이더 읽기 | VS input semantics | `StructuredBuffer<T>[SV_InstanceID]` |
| 80x24 총 대역폭 | ~130KB/frame | ~61KB/frame |

---

## 2. Functional Requirements

### FR-01: QuadInstance 32B 구조체 재설계
- 색상 RGBA float×8 → uint32 packed ×2 (fg, bg)
- 텍스처 좌표 float×4 → uint16 normalized ×4
- 위치/크기 → 셀 좌표 uint16 ×2 (셰이더에서 픽셀 변환)
- shading_type → 2비트 플래그로 패킹

### FR-02: StructuredBuffer GPU 파이프라인
- `ID3D11Buffer` with `D3D11_BIND_SHADER_RESOURCE` + `D3D11_RESOURCE_MISC_BUFFER_STRUCTURED`
- `ID3D11ShaderResourceView` (StructuredBuffer용) 생성
- 기존 `ID3D11InputLayout` 제거

### FR-03: HLSL 버텍스 셰이더 수정
- `StructuredBuffer<QuadInstance> g_instances : register(t1)`
- `SV_InstanceID` + `SV_VertexID`로 쿼드 4개 꼭짓점 생성
- 기존 input semantic 기반 코드 교체

---

## 3. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | QuadInstance 32B 구조체 정의 (packed format) | 구조체 크기 32B 확인 |
| S2 | QuadBuilder 수정: 32B 인스턴스 생성 | 기존 CellData → 32B QuadInstance 변환 |
| S3 | DX11Renderer: StructuredBuffer + SRV 생성 | GPU 바인딩 성공 |
| S4 | HLSL VS: StructuredBuffer 읽기 + 쿼드 생성 | 렌더링 결과 동일 확인 |
| S5 | Input Layout 제거 + 정리 | 빌드 성공, 불필요 코드 삭제 |
| S6 | 성능 벤치마크 (68B vs 32B) | GPU 대역폭 절감 확인 |

---

## 4. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | 32B StructuredBuffer 기반 렌더링 | 기존과 동일한 화면 출력 |
| 2 | ANSI 색상 + 한글 + 커서 정상 | 전체 기능 회귀 테스트 |
| 3 | 2-pass 렌더링 (배경→텍스트) 유지 | ADR-008 패턴 불변 |
| 4 | GPU 대역폭 절감 | 68B→32B (~53% 감소) |
| 5 | 기존 테스트 PASS 유지 | 23/23 PASS |

---

## 5. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | packed 포맷 정밀도 손실 (uint16 UV) | 중 | 4096×4096 아틀라스까지 uint16 충분 (65535 해상도) |
| R2 | StructuredBuffer FL 10.0 미지원 GPU | 하 | FL 11.0 이상 확인됨 (Phase 3 검증). WARP 폴백 |

---

## 6. References

| Document | Path |
|----------|------|
| ADR-007 R32 QuadInstance 결정 | `docs/adr/007-r32-quad-instance-format.md` |
| Phase 3 QuadBuilder | `src/renderer/quad_builder.h/cpp` |
| Phase 3 VS 셰이더 | `src/renderer/shader_vs.hlsl` |
| DX11 GPU 렌더링 리서치 (AtlasEngine) | `docs/00-research/research-dx11-gpu-rendering.md` |
