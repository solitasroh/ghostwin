# quadinstance-opt PDCA Completion Report

> **Feature**: QuadInstance StructuredBuffer 32B 최적화
> **Project**: GhostWin Terminal
> **Phase**: 4-E (Master: winui3-integration FR-11)
> **Date**: 2026-03-30
> **Match Rate**: 100%
> **Iterations**: 0

---

## Executive Summary

### 1.1 Overview

| Item | Value |
|------|-------|
| Feature | quadinstance-opt |
| Duration | 1 day (2026-03-30) |
| Files Changed | 5 |
| Lines Added | 141 |
| Lines Removed | 162 |
| Match Rate | 100% (14/14 items) |

### 1.2 Results

| Metric | Target | Actual |
|--------|--------|--------|
| Instance Size | 32B | 32B (static_assert) |
| Bandwidth Reduction | ~53% | 53% (68→32) |
| FPS | >= 기존 | 145 fps (기존 144) |
| Tests | All pass | 23/23 PASS |

### 1.3 Value Delivered

| Perspective | Result |
|-------------|--------|
| **Problem** | R32 Input Layout 제약으로 QuadInstance 68B. GPU 대역폭 비효율 (ADR-007) |
| **Solution** | uint16 패킹 + StructuredBuffer + SV_InstanceID 기반 VS 읽기. Input Layout 완전 제거 |
| **Function/UX** | 렌더링 결과 동일 (ANSI 색상 + CJK + Nerd Font + ClearType). 육안 확인 완료 |
| **Core Value** | 80×24 기준 프레임당 130KB→61KB. 4K + 멀티 Pane 확장성 확보. 코드량 -21줄 (141 추가, 162 제거) |

---

## 2. Key Changes

| Before (68B R32) | After (32B StructuredBuffer) |
|:---:|:---:|
| float pos/size/tex (24B) | uint16 pos/size/tex (16B) |
| float fg/bg RGBA (32B) | uint32 packed fg/bg (8B) |
| D3D11_BIND_VERTEX_BUFFER | D3D11_BIND_SHADER_RESOURCE + STRUCTURED |
| ID3D11InputLayout (7 elements) | nullptr (제거) |
| VSInput semantics | StructuredBuffer\<PackedQuad\>[SV_InstanceID] |
| CPU unpack_color() | GPU unpackColor() (셰이더 내) |

---

## 3. Lessons Learned

| Topic | Learning |
|-------|---------|
| StructuredBuffer + Dynamic | `D3D11_USAGE_DYNAMIC` + `BIND_SHADER_RESOURCE` + `MISC_BUFFER_STRUCTURED` 조합 가능. CPU_ACCESS_WRITE와 호환 |
| SRV 재생성 | buffer regrow 시 SRV도 반드시 재생성 필요 (기존 SRV가 이전 버퍼를 참조) |
| uint16 정밀도 | 65535px까지 지원하므로 8K (7680px) 해상도까지 충분 |

---

## Version History

| Version | Date | Author |
|---------|------|--------|
| 1.0 | 2026-03-30 | Solit |
