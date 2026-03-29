# dx11-rendering PDCA Completion Report

> **Feature**: D3D11 GPU Accelerated Rendering Engine
> **Project**: GhostWin Terminal
> **Phase**: 3 (Renderer Core)
> **Date**: 2026-03-29 ~ 2026-03-30
> **Match Rate**: 96.6%
> **Iterations**: 2

---

## Executive Summary

### 1.1 Overview

| Item | Value |
|------|-------|
| Feature | dx11-rendering |
| Duration | 2 days (2026-03-29 ~ 2026-03-30) |
| Commits | 17 (2 feat + 11 fix + 4 chore) |
| Files Changed | 26 |
| Lines Added | ~3,790 |
| New Files | 17 (renderer 14 + tests 3) |
| Match Rate | 96.6% (56/58 items) |

### 1.2 Results

| Metric | Target | Actual |
|--------|--------|--------|
| FR Implementation | 12/12 | 12/12 (100%) |
| DoD Completion | 7/7 | 6/7 (86%) |
| QC Criteria | 5/5 | 4/5 (80%) |
| NFR Compliance | 6/6 | 6/6 (100%) |
| Tests | All pass | 18/18 PASS |
| Overall Match Rate | 95% | 96.6% |

### 1.3 Value Delivered

| Perspective | Result |
|-------------|--------|
| **Problem** | Phase 2의 ConPTY->VtCore 파이프라인 출력을 화면에 표시할 수 없었음 |
| **Solution** | D3D11 GPU 인스턴싱 + DirectWrite 글리프 아틀라스 + _api/_p 이중 상태 패턴으로 렌더링 엔진 구현. 2-pass 렌더링(배경→텍스트)으로 글리프 오버플로 허용 |
| **Function/UX** | cmd.exe/pwsh.exe 출력이 Win32 HWND 창에 실시간 렌더링. ASCII + ANSI 색상 + 한글(폰트 폴백) + 키보드 입력 + 리사이즈 동작 확인 |
| **Core Value** | 4-스레드 모델의 렌더 단계 실현. Phase 4(WinUI3) 통합 전 렌더링 코어 독립 검증 완료 |

---

## 2. Implementation Summary

### 2.1 Architecture

```
ConPTY → I/O Thread → VtCore → RenderState(_api/_p) → QuadBuilder → GPU → Present
           (write)     (parse)    (dirty-row copy)     (2-pass)    (D3D11)
```

### 2.2 New Modules (14 files)

| Module | Files | Role |
|--------|-------|------|
| common | error.h, log.h, render_constants.h | 에러/로깅/상수 인프라 |
| dx11_renderer | .h/.cpp | D3D11 디바이스, 스왑체인, GPU 렌더 |
| glyph_atlas | .h/.cpp | DirectWrite 래스터화 + stb_rect_pack + 2-tier 캐시 |
| render_state | .h/.cpp | _api/_p 이중 상태 + flat buffer + bitset dirty |
| quad_builder | .h/.cpp | CellData → QuadInstance 변환 (2-pass) |
| terminal_window | .h/.cpp | Win32 HWND, 메시지 루프, 키 입력, 리사이즈 |
| shaders | shader_vs/ps/common.hlsl | GPU 인스턴싱 VS/PS |
| main | main.cpp | 전체 통합 실행 파일 |

### 2.3 Modified Modules (4 files)

| Module | Changes |
|--------|---------|
| vt_bridge.h/.c | +타입 안전 핸들, +행/셀 반복자 API 15개, +커서/dirty API |
| vt_core.h/.cpp | +CellData 32B, +for_each_row, +cursor_info, +raw_terminal |
| CMakeLists.txt | +renderer lib, +ghostwin_terminal, +3 test targets |

### 2.4 Key Design Decisions (Implemented)

| Decision | Implementation | Verified |
|----------|---------------|----------|
| D3D11 FL 10.0+ | D3D11CreateDevice HARDWARE + WARP 폴백 | ✅ FL 11.0 |
| FLIP_SEQUENTIAL + waitable | FRAME_LATENCY_WAITABLE_OBJECT 플래그 | ✅ |
| WS_EX_NOREDIRECTIONBITMAP | CreateWindowExW 적용 | ✅ |
| _api/_p 이중 상태 | dirty-row-only memcpy | ✅ 5/5 테스트 |
| 2-pass 렌더링 | 배경 전체 → 텍스트 전체 (WT/Alacritty/Ghostty 패턴) | ✅ |
| DirectWrite 그레이스케일 | ClearType 3x1 → 그레이스케일 변환 | ✅ |
| stb_rect_pack 아틀라스 | grow-only 1024x1024 | ✅ |
| 2-tier 글리프 캐시 | ASCII 직접 매핑 + 비-ASCII 해시맵 | ✅ |
| 한글 폰트 폴백 | Malgun Gothic 자동 감지 + 셀 크기 스케일링 | ✅ |
| Backspace 0x7F | xterm 표준 (WM_CHAR 단일 전송) | ✅ |

---

## 3. DoD Verification

| # | Criteria | Status | Evidence |
|---|----------|--------|----------|
| 1 | cmd.exe/pwsh.exe 실시간 렌더링 | ✅ | PowerShell 프롬프트 + 색상 표시 |
| 2 | 키보드 입력 셸 대화 | ✅ | echo, dir, ls, Backspace 동작 |
| 3 | ANSI 색상 16/256/TrueColor | ✅ | Starship 프롬프트 색상 |
| 4 | 커서 위치 + 블링킹 | ✅ | 블록 커서 표시 |
| 5 | 유휴 시 GPU ~0% | ⚠️ | start_paint false → Sleep(1), 미실측 |
| 6 | 리사이즈 정상 | ✅ | 100ms 디바운스 + 전체 리드로우 |
| 7 | Phase 2 테스트 유지 | ✅ | 7/7 PASS |

---

## 4. Test Results

| Suite | Tests | Result |
|-------|-------|--------|
| vt_core_test | 7 | 7/7 PASS |
| vt_bridge_cell_test | 6 | 6/6 PASS |
| render_state_test | 5 | 5/5 PASS |
| dx11_render_test | 5 | 5/5 PASS (D3D11 + 셰이더 + 아틀라스) |
| **Total** | **23** | **23/23 PASS** |

---

## 5. Known Limitations (Phase 4 Scope)

| Item | Current | Phase 4 Target |
|------|---------|---------------|
| 한글 IME 입력 | 미지원 (jamo 분리) | TSF ITfContextOwner |
| ClearType 서브픽셀 | 그레이스케일 AA | dwrite-hlsl HLSL 재구현 |
| Nerd Font 아이콘 | 미표시 | 폰트 폴백 체인 확장 |
| 유휴 GPU 0% 실측 | 미측정 | FrameStats 프로파일링 |
| QuadInstance 크기 | 68B (R32 포맷) | StructuredBuffer 32B |
| 다중 모니터 DPI | 미구현 | WM_DPICHANGED / CompositionScaleChanged |

---

## 6. Commit History

| # | Hash | Type | Summary |
|---|------|------|---------|
| 1 | f47dd07 | docs | dx11-rendering plan with research |
| 2 | b41e5e9 | feat | D3D11 renderer core (S1-S9) |
| 3 | f7c0fbc | feat | Terminal window with ConPTY (S10-S14) |
| 4 | 3d0b2d0 | fix | Korean wide char, dirty state, GPU instancing |
| 5 | d32e77c | fix | Full-screen redraw, initial dimensions |
| 6 | 9a755e4 | fix | Glyph baseline, font size from config |
| 7 | 646bc9b | fix | Gap analysis items G1-G5 |
| 8 | bc36ce6 | fix | Fallback font scaling (em-square) |
| 9 | d58a4fa | fix | Fallback scaling + cursor opacity |
| 10 | b396aec | fix | 2-pass rendering for CJK clipping |
| 11 | fa383b3 | fix | Backspace 0x08, wide char bg 2-cell |
| 12 | b8802a4 | fix | Remove duplicate BS/Tab/Enter sends |
| 13 | 63cdfeb | fix | Backspace 0x7F, font 12pt |
| 14 | 1c8db29 | fix | Proportional fallback font scaling |
| 15 | 32e5901 | fix | Center wide CJK glyphs |

---

## 7. Lessons Learned

| Topic | Learning |
|-------|---------|
| D3D11 Input Layout | R16 포맷은 셰이더 FLOAT 시그니처와 CreateInputLayout에서 불일치. R32 필요 |
| GPU 인스턴싱 | PER_VERTEX_DATA vs PER_INSTANCE_DATA 혼동 주의. 동일 위치 중첩 렌더 발생 |
| dirty 리셋 타이밍 | ghostty의 글로벌 dirty와 row-level dirty는 독립 관리. 행 읽기 전 리셋하면 데이터 유실 |
| 2-pass 렌더링 | 4개 주요 터미널 모두 배경→텍스트 순서. 셀 간 글리프 오버플로 허용이 표준 |
| WM_CHAR 이중 전송 | VK_BACK/TAB/RETURN은 WM_CHAR가 자동 생성. WM_KEYDOWN에서 중복 처리 금지 |
| 폴백 폰트 스케일링 | em_size=cell_h는 과대. 동일 dip_size에서 시작하여 초과 시만 축소 |

---

## Version History

| Version | Date | Author |
|---------|------|--------|
| 1.0 | 2026-03-30 | Solit |
