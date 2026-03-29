# ADR-007: R32 포맷 QuadInstance (68B) 채택

- **상태**: 채택
- **날짜**: 2026-03-30
- **관련**: Phase 3 dx11-rendering, Design Section 4.4.2

## 배경

Design 문서에서 QuadInstance를 32바이트(R16 포맷)로 설계했으나, D3D11 `CreateInputLayout`에서 `E_INVALIDARG`가 발생하여 구현할 수 없었다.

## 문제

- `R16_UINT`(UINT 타입)와 셰이더의 `uint`(32bit) 시그니처 간 타입 불일치
- `R16G16_SINT`와 셰이더의 `float2` 시그니처 간 타입 불일치
- D3D11은 `CreateInputLayout` 시점에서 포맷과 셰이더 시그니처 타입을 엄격 검증

## 결정

모든 입력 요소를 R32 포맷으로 변경하여 68바이트 QuadInstance를 사용한다.

```
R32_UINT (shading_type), R32G32_FLOAT (position, size, texcoord, texsize),
R32G32B32A32_FLOAT (fg_color, bg_color)
```

## 근거

| 방안 | 판정 | 이유 |
|------|:----:|------|
| R16 포맷 (32B) | 기각 | CreateInputLayout E_INVALIDARG |
| R32 포맷 (68B) | **채택** | 타입 매칭 보장, 즉시 동작 |
| StructuredBuffer + SRV | 보류 | 구조 복잡, Phase 4에서 검토 |

## 영향

- 인스턴스 버퍼 크기 2.1x 증가 (32B → 68B)
- 80x24 터미널 기준: ~130KB/프레임 (허용 범위)
- MAP_WRITE_DISCARD 프레임당 1회 패턴에서 성능 영향 미미

## Phase 4 최적화 경로

StructuredBuffer + ByteAddressBuffer 방식으로 32B 인스턴스 복원 가능. 셰이더에서 `StructuredBuffer<QuadInstance>` SRV로 데이터를 읽으면 입력 레이아웃 제약 없이 임의 포맷 사용 가능.
