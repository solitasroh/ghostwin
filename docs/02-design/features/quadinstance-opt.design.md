# quadinstance-opt Design

> **Feature**: QuadInstance StructuredBuffer 32B 최적화
> **Project**: GhostWin Terminal
> **Phase**: 4-E (Master: winui3-integration FR-11)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/quadinstance-opt.plan.md`

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3 QuadInstance가 R32 Input Layout 제약으로 68B/instance. GPU 대역폭 비효율 |
| **Solution** | uint16 패킹 + StructuredBuffer로 32B/instance 달성. Input Layout 제거 |
| **Function/UX** | 렌더링 결과 동일. 내부 GPU 대역폭 ~53% 절감 |
| **Core Value** | 4K + 200x50 셀에서 GPU 병목 방지. 멀티 Pane 성능 확보 |

---

## 1. Current State Analysis

### 1.1 현재 QuadInstance (68B)

```cpp
// quad_builder.h — 68B
struct QuadInstance {
    uint32_t shading_type;                  //  4B
    float    pos_x, pos_y;                  //  8B
    float    size_x, size_y;                //  8B
    float    tex_u, tex_v;                  //  8B
    float    tex_w, tex_h;                  //  8B
    float    fg_r, fg_g, fg_b, fg_a;        // 16B
    float    bg_r, bg_g, bg_b, bg_a;        // 16B
};                                          // = 68B
```

### 1.2 현재 GPU 파이프라인

```
quad_builder.cpp → QuadInstance 68B 배열
    → DX11Renderer: D3D11_BIND_VERTEX_BUFFER (동적)
    → ID3D11InputLayout (7 elements)
    → shader_vs.hlsl: VSInput semantics (POSITION, COLOR, TEXCOORD...)
    → DrawIndexedInstanced
```

### 1.3 변경 대상

| File | Change |
|------|--------|
| `quad_builder.h` | QuadInstance 68B → 32B 구조체 |
| `quad_builder.cpp` | float 필드 → uint16/uint32 패킹 |
| `dx11_renderer.cpp` | vertex buffer + input layout → StructuredBuffer + SRV |
| `shader_vs.hlsl` | VSInput semantics → `StructuredBuffer<T>[SV_InstanceID]` |
| `shader_ps.hlsl` | 변경 없음 |
| `render_constants.h` | `kQuadInstanceSize` 68 → 32 |

---

## 2. Detailed Design

### 2.1 PackedQuadInstance 32B 구조체

```cpp
#pragma pack(push, 1)
struct QuadInstance {
    uint16_t pos_x, pos_y;       //  4B — 픽셀 위치 (max 65535, 4K 충분)
    uint16_t size_x, size_y;     //  4B — 픽셀 크기
    uint16_t tex_u, tex_v;       //  4B — 아틀라스 픽셀 좌표
    uint16_t tex_w, tex_h;       //  4B — 글리프 픽셀 크기
    uint32_t fg_packed;          //  4B — RGBA8 (동일 포맷, CellData에서 직접 복사)
    uint32_t bg_packed;          //  4B — RGBA8
    uint32_t shading_type;       //  4B
    uint32_t reserved;           //  4B — 향후 확장용 (0으로 초기화)
};                               // = 32B
#pragma pack(pop)
static_assert(sizeof(QuadInstance) == 32);
```

**패킹 전략:**
- `pos`, `size`, `tex`: float → uint16. 최대값 65535px로 4K(3840) + 8K(7680) 충분
- `fg/bg`: float×4 → uint32 packed RGBA8. CellData의 `fg_packed`/`bg_packed`를 그대로 복사 (unpack_color 제거)
- `shading_type`: uint32 유지 (HLSL 정렬 편의)
- `reserved`: 32B 정렬용 + 향후 확장

### 2.2 QuadBuilder 변경

**Before** (`quad_builder.cpp`):
```cpp
q.pos_x = px;             // float
q.fg_r = ...; q.fg_g = ...; // unpack_color
```

**After**:
```cpp
q.pos_x = (uint16_t)px;   // float → uint16 truncation
q.fg_packed = cell.fg_packed;  // direct copy, no unpack
q.bg_packed = cell.bg_packed;
q.reserved = 0;
```

`unpack_color()` 헬퍼 함수 제거 — fg/bg를 uint32 그대로 복사.

### 2.3 DX11Renderer — StructuredBuffer 전환

**Before** (`dx11_renderer.cpp:363-371`):
```cpp
// Instance buffer (vertex buffer)
inst_desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
```

**After**:
```cpp
// Instance buffer (structured buffer)
inst_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
inst_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
inst_desc.StructureByteStride = sizeof(QuadInstance);  // 32
```

**SRV 생성** (신규):
```cpp
ComPtr<ID3D11ShaderResourceView> instance_srv;

D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc = {};
srv_desc.Format = DXGI_FORMAT_UNKNOWN;
srv_desc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
srv_desc.Buffer.NumElements = instance_capacity;
device->CreateShaderResourceView(instance_buffer.Get(), &srv_desc, &instance_srv);
```

**Input Layout 제거**: `input_layout` ComPtr 삭제. `CreateInputLayout` 호출 제거.

**draw_instances 변경**:
```cpp
// Before:
context->IASetInputLayout(input_layout.Get());
context->IASetVertexBuffers(0, 1, instance_buffer.GetAddressOf(), &stride, &offset);

// After:
context->IASetInputLayout(nullptr);  // no input layout
context->VSSetShaderResources(1, 1, instance_srv.GetAddressOf());  // t1 register
```

### 2.4 HLSL Vertex Shader 전환

**Before** (`shader_vs.hlsl`):
```hlsl
struct VSInput {
    uint   shadingType : BLENDINDICES0;
    float2 position    : POSITION;
    float2 size        : TEXCOORD1;
    // ... input semantics
};
PSInput main(VSInput input) { ... }
```

**After**:
```hlsl
struct PackedQuad {
    uint2  pos_size;       // xy = pos (uint16×2), zw = size (uint16×2)
    uint2  tex_pos_size;   // xy = uv (uint16×2), zw = wh (uint16×2)
    uint   fg_packed;
    uint   bg_packed;
    uint   shading_type;
    uint   reserved;
};

StructuredBuffer<PackedQuad> g_instances : register(t1);

cbuffer ConstBuffer : register(b0) {
    float2 positionScale;
    float2 atlasScale;
};

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

float4 unpackColor(uint packed) {
    return float4(
        (packed & 0xFF) / 255.0,
        ((packed >> 8) & 0xFF) / 255.0,
        ((packed >> 16) & 0xFF) / 255.0,
        ((packed >> 24) & 0xFF) / 255.0
    );
}

PSInput main(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID) {
    PackedQuad q = g_instances[instanceId];

    // Unpack uint16 pairs from uint32
    float2 position = float2(q.pos_size.x & 0xFFFF, q.pos_size.x >> 16);
    float2 size     = float2(q.pos_size.y & 0xFFFF, q.pos_size.y >> 16);
    float2 texcoord = float2(q.tex_pos_size.x & 0xFFFF, q.tex_pos_size.x >> 16);
    float2 texsize  = float2(q.tex_pos_size.y & 0xFFFF, q.tex_pos_size.y >> 16);

    float2 corner = float2(
        (vertexId == 1 || vertexId == 2) ? 1.0 : 0.0,
        (vertexId == 2 || vertexId == 3) ? 1.0 : 0.0);

    float2 pixelPos = position + corner * size;

    PSInput output;
    output.pos = float4(pixelPos * positionScale + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = (texcoord + corner * texsize) * atlasScale;
    output.fgColor = unpackColor(q.fg_packed);
    output.bgColor = unpackColor(q.bg_packed);
    output.shadingType = q.shading_type;

    return output;
}
```

**핵심 포인트:**
- `PSInput` 구조체 불변 → `shader_ps.hlsl` 수정 불필요
- `unpackColor()`: GPU에서 RGBA8 → float4 변환 (CPU 부담 제거)
- `SV_VertexID` + `SV_InstanceID`만 입력 → Input Layout 불필요

### 2.5 CPU 구조체와 HLSL 구조체의 바이트 매핑

```
C++ QuadInstance (32B)              HLSL PackedQuad (32B)
─────────────────────               ─────────────────────
[0-1]  pos_x  (uint16)   ┐         pos_size.x (uint) [0-3]
[2-3]  pos_y  (uint16)   ┘             lo16 = pos_x, hi16 = pos_y
[4-5]  size_x (uint16)   ┐         pos_size.y (uint) [4-7]
[6-7]  size_y (uint16)   ┘             lo16 = size_x, hi16 = size_y
[8-9]  tex_u  (uint16)   ┐         tex_pos_size.x (uint) [8-11]
[10-11] tex_v (uint16)   ┘             lo16 = tex_u, hi16 = tex_v
[12-13] tex_w (uint16)   ┐         tex_pos_size.y (uint) [12-15]
[14-15] tex_h (uint16)   ┘             lo16 = tex_w, hi16 = tex_h
[16-19] fg_packed (uint32)          fg_packed (uint) [16-19]
[20-23] bg_packed (uint32)          bg_packed (uint) [20-23]
[24-27] shading_type (uint32)       shading_type (uint) [24-27]
[28-31] reserved (uint32)          reserved (uint) [28-31]
```

---

## 3. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | QuadInstance 32B 구조체 정의 | `quad_builder.h` | `static_assert(sizeof == 32)` |
| S2 | QuadBuilder: uint16/uint32 패킹 | `quad_builder.cpp` | `unpack_color` 제거, 직접 복사 |
| S3 | DX11Renderer: StructuredBuffer + SRV | `dx11_renderer.cpp` | SRV 생성 + Input Layout 제거 |
| S4 | HLSL VS: StructuredBuffer 읽기 | `shader_vs.hlsl` | 렌더링 결과 동일 육안 확인 |
| S5 | render_constants.h 업데이트 | `render_constants.h` | `kQuadInstanceSize = 32` |
| S6 | 성능 벤치마크 | 테스트 | 68B vs 32B fps 비교 |

### 의존 관계

```
S1 → S2 (구조체 먼저, 빌더 적용)
S1 → S3 (구조체 크기 필요)
S3 → S4 (SRV 바인딩 후 셰이더)
S4 → S5 (동작 확인 후 상수 정리)
S5 → S6 (벤치마크)
```

---

## 4. Test Plan

| # | Test | Expected |
|---|------|----------|
| T1 | 기존 23/23 테스트 PASS | 회귀 없음 |
| T2 | cmd.exe/pwsh.exe 렌더링 | 동일 화면 출력 |
| T3 | ANSI 색상 + 한글 + Nerd Font | 전체 기능 유지 |
| T4 | 2-pass 렌더링 (ADR-008) | 배경→텍스트 순서 불변 |
| T5 | dx11_render_test fps | 68B 대비 동등 이상 |

---

## 5. QC Criteria

| # | Criteria | Target |
|---|----------|--------|
| QC-01 | 32B StructuredBuffer 렌더링 | 기존과 동일 화면 |
| QC-02 | ANSI + CJK + Nerd Font + ClearType | 전체 기능 유지 |
| QC-03 | 2-pass (ADR-008) 호환 | 배경→텍스트 불변 |
| QC-04 | Input Layout 제거 | `input_layout` ComPtr 삭제 |
| QC-05 | 기존 테스트 23/23 PASS | 회귀 없음 |

---

## 6. Rollback Strategy

S4 완료 후 문제 발생 시:
- `shader_vs.hlsl`의 기존 VSInput 코드를 `#ifdef` 분기로 유지 가능
- DX11Renderer에서 vertex buffer 경로를 조건부 유지

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design |
