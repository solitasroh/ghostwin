# ClearType Sharpness v2 Design Document (v3)

> **Summary**: D2D DrawGlyphRun(linear AA) → CreateAlphaTexture(gamma AA) + raw coverage + Dual Source
>
> **Project**: GhostWin Terminal
> **Author**: Solit
> **Date**: 2026-04-03
> **Status**: Draft (v3 — 리서치 기반 재설계)
> **Planning Doc**: [cleartype-sharpness-v2.plan.md](../../01-plan/features/cleartype-sharpness-v2.plan.md)

---

## 1. Overview

### 1.1 Design Goals

D2D DrawGlyphRun의 linear 공간 AA(에지 85)를 CreateAlphaTexture의 gamma 공간 AA(에지 139)로 교체하여 Alacritty/WezTerm 수준의 선명도 달성.

### 1.2 근본 원인 (Plan 2.0 확정, FACT)

D2D premultiplied RT에서 linear AA 수행 → 에지 coverage 낮음(85) → 소프트.
CreateAlphaTexture는 gamma 공간 AA → 에지 coverage 높음(139) → 선명.
셰이더 감마 보정은 AA 샘플링 공간 차이를 완벽히 보정 불가.

### 1.3 Design Principles

1. **Alacritty 검증 패턴**: CreateAlphaTexture + raw + per-channel blend = 선명 (FACT)
2. **한 번에 하나씩**: 각 변경의 효과를 독립 측정
3. **이중 감마 방지**: CreateAlphaTexture(gamma baked) + raw coverage (셰이더 감마 없음)
4. **코드 단순화**: D2D 의존성 제거 → 코드 복잡도 감소

---

## 2. Architecture

### 2.1 파이프라인 비교

```
현재 (D2D linear AA — 소프트):
  D2D DrawGlyphRun(gamma=1.0)
  → linear coverage (에지=85)
  → EnhanceContrast + AlphaCorrection (셰이더 보정, 근사치)
  → Dual Source Blend

목표 (CreateAlphaTexture gamma AA — 선명):
  CreateAlphaTexture(system gamma=1.8)
  → gamma-baked coverage (에지=139)
  → raw coverage (보정 불필요)
  → Dual Source Blend
```

### 2.2 변경 범위

| 파일 | 변경 | 방향 |
|------|------|------|
| `glyph_atlas.cpp` | D2D DrawGlyphRun 제거, CreateAlphaTexture CPU 경로만 사용 | 삭제 + 복원 |
| `glyph_atlas.cpp` | D2D 멤버 (d2d_factory, d2d_rt, d2d_brush) 제거 | 삭제 |
| `glyph_atlas.cpp` | Atlas 포맷 B8G8R8A8 유지 (Dual Source 호환) | 유지 |
| `glyph_atlas.cpp` | ClearType 3x1 → BGRA 패킹 코드 복원 | 복원 |
| `shader_ps.hlsl` | DWrite 감마 함수 호출 제거, raw coverage | 단순화 |
| `shader_ps.hlsl` | Dual Source output 유지 | 유지 |
| `dx11_renderer.cpp` | Dual Source Blending 유지 | 유지 |

### 2.3 제거되는 코드 (D2D 관련)

```cpp
// glyph_atlas.cpp — Impl 멤버에서 제거
ComPtr<ID2D1Factory>          d2d_factory;    // 제거
ComPtr<ID2D1RenderTarget>     d2d_rt;         // 제거
ComPtr<ID2D1SolidColorBrush>  d2d_brush;      // 제거

// glyph_atlas.cpp — init_atlas_texture에서 제거
D2D1CreateFactory(...)                         // 제거
CreateDxgiSurfaceRenderTarget(...)             // 제거
SetTextRenderingParams(...)                    // 제거
SetTextAntialiasMode(...)                      // 제거
CreateSolidColorBrush(...)                     // 제거

// glyph_atlas.cpp — rasterize_glyph에서 제거
d2d_rt->BeginDraw()                            // 제거
d2d_rt->DrawGlyphRun(...)                      // 제거
d2d_rt->EndDraw()                              // 제거

// Atlas 텍스처 BindFlags
D3D11_BIND_RENDER_TARGET                       // 제거 (D2D 불필요)
```

### 2.4 유지되는 코드

```cpp
// dx11_renderer.cpp — Dual Source Blending (유지)
blend_desc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC1_COLOR;
blend_desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC1_ALPHA;

// shader_ps.hlsl — Dual Source Output (유지)
struct DualOutput {
    float4 color   : SV_Target0;
    float4 weights : SV_Target1;
};
```

---

## 3. Functional Requirements

### FR-01: D2D → CreateAlphaTexture 전환

**glyph_atlas.cpp rasterize_glyph() 변경:**

```cpp
// D2D 경로 제거. CPU CreateAlphaTexture만 사용.

// ClearType 3x1
std::vector<uint8_t> ct_3x1(gw * gh * 3);
hr = analysis->CreateAlphaTexture(
    DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds,
    ct_3x1.data(), (UINT32)(gw * gh * 3));

// BGRA 패킹 (B8G8R8A8 atlas 포맷)
for (int i = 0; i < gw * gh; i++) {
    uint8_t r = ct_3x1[i * 3 + 0];  // R subpixel coverage
    uint8_t g = ct_3x1[i * 3 + 1];  // G subpixel coverage
    uint8_t b = ct_3x1[i * 3 + 2];  // B subpixel coverage
    bgra[i * 4 + 0] = b;  // B (B8G8R8A8 byte 0)
    bgra[i * 4 + 1] = g;  // G
    bgra[i * 4 + 2] = r;  // R
    bgra[i * 4 + 3] = max(r, g, b); // A
}

ctx->UpdateSubresource(atlas_tex.Get(), 0, &box, bgra.data(), gw * 4, 0);
```

### FR-02: 셰이더 raw coverage (감마 보정 제거)

**shader_ps.hlsl ClearType 경로:**

```hlsl
// CreateAlphaTexture coverage는 system gamma(~1.8) baked.
// 셰이더 감마 보정 없음 → 이중 감마 방지.
// Dual Source가 per-channel 하드웨어 블렌딩 수행.
if (input.shadingType == 1) {
    float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);
    float3 coverage = glyph.rgb;  // gamma-baked, display-ready
    o.weights = float4(coverage * input.fgColor.a, 1);
    o.color = o.weights * input.fgColor;
    return o;
}
```

### FR-03: Atlas 텍스처 단순화

- `D3D11_BIND_RENDER_TARGET` 제거 (D2D 불필요)
- `D3D11_BIND_SHADER_RESOURCE`만 유지
- D2D include (`d2d1.h`, `dxgi.h`) 제거

### FR-04: 코드 품질 정리

- D2D 멤버/초기화/include 전부 제거
- 3-pass 잔재 (Step 1에서 일부 완료) 마무리
- DWrite 감마 함수 6개: 셰이더에서 미사용이므로 제거 또는 주석처리
- 모순된 주석 수정

---

## 4. Implementation Order

```
Step 1: FR-01 (CreateAlphaTexture 복원 + D2D 제거)
  → glyph_atlas.cpp 수정
  → 빌드 + 10/10 테스트

Step 2: FR-02 (셰이더 raw coverage)
  → shader_ps.hlsl에서 DWrite 감마 호출 제거
  → 빌드 (셰이더 런타임 컴파일)

Step 3: 4분면 스크린샷 비교
  → 사용자 선명도 확인
  → Alacritty/WezTerm과 비교

Step 4: FR-03 + FR-04 (코드 정리)
  → Atlas bind flags, D2D include, 죽은 코드 제거
  → 코드 분석기 재실행 → 90+ 목표

Step 5: 커밋
```

---

## 5. Testing Strategy

| Step | 검증 | 성공 기준 |
|:----:|------|----------|
| FR-01 | 빌드 + 10/10 테스트 | 텍스트 정상 렌더링 |
| FR-02 | 4분면 스크린샷 | **Alacritty/WezTerm과 동등한 선명도** |
| FR-03-04 | 빌드 + 코드 분석기 | 90+ 점수, 죽은 코드 0건 |

### 회귀 방지

- 각 Step 후 `git diff` 확인
- 악화 시 `git checkout -- <file>` 즉시 복원
- 이전 시도 목록 참조하여 반복 방지

---

## 6. Risks

| 위험 | 가능성 | 대응 |
|------|:------:|------|
| CreateAlphaTexture + raw + Dual Source가 per-channel lerp와 동일 결과 | 낮음 (Dual Source는 실제 dest 읽음) | 이전 per-channel lerp와 수학적 차이 확인 |
| ClearType 3x1 → BGRA 패킹에서 채널 순서 오류 | 중간 | atlas 덤프로 R≠G≠B 확인 |
| Atlas BIND_RENDER_TARGET 제거 시 WT dump 코드 영향 | 낮음 | dump는 staging texture 사용 (별도) |
