# dpi-scaling-integration — Gap Analysis

> **Feature**: dpi-scaling-integration
> **Phase**: Check (PDCA)
> **Date**: 2026-04-15
> **Analyzer**: rkit:gap-detector (opus)
> **Design v2**: [dpi-scaling-integration.design.md](../02-design/features/dpi-scaling-integration.design.md)

---

## Executive Summary

| 항목 | 값 |
|------|:--:|
| **Match Rate** | **100%** (8/8) |
| Gap | **0** |
| 빌드 | ✅ 성공 |
| 경고 | ✅ **0 건** |
| vt_core_test | ✅ 10/10 |
| 결론 | **Report 진입 권장** |

---

## 1. 요구사항 대조표

| # | Design 요구 | 실제 위치 | 결과 |
|:-:|------------|-----------|:----:|
| 4.1 | `gw_update_cell_metrics` native (render stop/start + atlas 재생성 + SurfaceManager broadcast + resize_pty_only + vt_resize_locked) | `ghostwin_engine.cpp:443-524` | ✅ |
| 4.1 header | 선언 + 교훈 주석 | `ghostwin_engine.h:72-85` | ✅ |
| 4.5 | DPI 복구 (`dpi_scale > 0 ? ... : 1.0f`) | `ghostwin_engine.cpp:357` | ✅ |
| 4.6 | `FontSettings { Size, Family, CellWidthScale, CellHeightScale }` | `AppSettings.cs:23-29` | ✅ |
| 4.7 | `IEngineService.UpdateCellMetrics` + 구현 + P/Invoke | 3 파일 | ✅ |
| 4.5 | 하드코딩 제거 → `FontSettings` 사용 | `MainWindow.xaml.cs:222-227` | ✅ |
| 4.5 | `OnDpiChanged` override | `MainWindow.xaml.cs:51-73` | ✅ |
| 4.9 | Observer 인프라 (C# primary, `OnDpiChanged` 직접 호출) | `MainWindow.OnDpiChanged` | ✅ |
| — | ghostty surface API | N/A (구조상 해당 없음, Design §1 비목표) | — |

## 2. 코드 품질 검증

| 항목 | 결과 |
|------|:----:|
| render thread stop/start 대칭성 | ✅ `was_running` 캡처 → stop → swap → start (실패 경로도 복원) |
| GlyphAtlas 생성 실패 시 복원 | ✅ render thread 복원 후 `GW_ERR_INTERNAL` 반환 |
| PTY/VT/RenderState 원자성 | ✅ vt-mutex-redesign 분할 패턴 일치 (`resize_pty_only` + `vt_mutex` lock + `vt_resize_locked` + `state->resize`) |
| 입력 검증 | ✅ `font_size/dpi/zoom ≤ 0` 가드 |
| 폰트 폴백 | ✅ NULL/빈 → `L"Cascadia Mono"` |
| 구조화 로그 | ✅ `update_cell_metrics: font=... dpi=... zoom=... cell=WxH` |

## 3. Gap 목록

**없음**. Design 외 추가/누락/변경 0.

## 4. 리스크 실현 (Design §8)

| 리스크 | 실현 |
|--------|:----:|
| text overflow 재발 | ❌ broadcast 패턴으로 원천 차단 |
| Atlas 재빌드 race | ❌ render thread stop/start 로 회피 |
| DpiChanged ↔ SizeChanged 경쟁 | ❌ base.OnDpiChanged 우선 호출 |
| C# ↔ C++ 설정 이원화 혼선 | ❌ C# primary 확정 |
| 모니터 이동 rect 누락 | ❌ base.OnDpiChanged 수용 |

## 5. 결론

Match Rate **100%**. Design v2 전 요구사항 충족, Gap 0, 리스크 실현 0.

**다음 단계**: `/pdca report dpi-scaling-integration`.

선택적 F5 수동 검증 (멀티 DPI 모니터 이동, `AppSettings.json` 수동 폰트 변경) 후 Report 에 PASS 반영.

---

## 참조

- `src/engine-api/ghostwin_engine.h`, `.cpp`
- `src/GhostWin.Core/Models/AppSettings.cs`
- `src/GhostWin.Core/Interfaces/IEngineService.cs`
- `src/GhostWin.Interop/EngineService.cs`, `NativeEngine.cs`
- `src/GhostWin.App/MainWindow.xaml.cs`
