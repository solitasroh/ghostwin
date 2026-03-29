# dx11-rendering Planning Document

> **Summary**: D3D11 GPU 가속 렌더링 엔진을 구현하여 VtCore의 터미널 상태를 화면에 그린다
>
> **Project**: GhostWin Terminal
> **Version**: 0.2.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Draft (Research-Enhanced)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | Phase 2에서 ConPTY -> VtCore 파이프라인이 완성되었으나, 파싱된 터미널 상태를 화면에 표시하는 렌더러가 없다 |
| **Solution** | D3D11 GPU 인스턴싱 + DirectWrite 글리프 아틀라스 + `_api`/`_p` 이중 상태 분리 패턴으로 렌더링 엔진을 구현하고, 전용 렌더 스레드에서 60fps로 화면에 그린다 |
| **Function/UX Effect** | cmd.exe/pwsh.exe 출력이 GPU 가속으로 실시간 렌더링되어 시각적으로 확인 가능한 터미널 화면이 생성된다 |
| **Core Value** | 4-스레드 모델의 3번째 단계(렌더)를 실현하여, Phase 4(WinUI3) 통합 전 렌더링 코어를 독립 검증한다 |

### Research Confidence: High

> 3개 에이전트 병렬 리서치 완료:
> - libghostty-vt 셀 데이터 API 완전 확인 (행/셀 반복자, 코드포인트, 색상, 속성)
> - Windows Terminal AtlasEngine 소스 레벨 분석 (2026년 최신, D3D11 유지 확인)
> - 성능 최적화 패턴 6개 확인 (Present1 dirty rect, waitable object, MAP_WRITE_DISCARD 등)
> 상세: `docs/00-research/research-dx11-gpu-rendering.md`, `docs/00-research/research-winui3-dx11.md`

---

## 1. Overview

### 1.1 Purpose

Phase 2에서 구축된 ConPTY -> VtCore 파이프라인의 출력(셀 데이터: 문자, 색상, 속성)을 D3D11 GPU 가속 렌더링으로 화면에 표시한다. Win32 HWND 기반 창을 사용하며, WinUI3 SwapChainPanel 통합은 Phase 4로 분리한다.

### 1.2 Background

- Phase 1: libghostty-vt Windows 빌드 + C API 검증 (7/7 PASS)
- Phase 2: ConPTY 세션 + I/O 스레드 + VtCore 연동 (8/8 PASS, ADR-004~006)
- Phase 2 Migration Notes (M-1~M-7): vt_mutex 확장, render thread, 리사이즈 디바운스
- Windows Terminal AtlasEngine: D3D11 GPU 인스턴싱 기반 렌더링 (2026년 현재 기본 렌더러)
- libghostty-vt: 행/셀 반복자 API로 완전한 셀 데이터 접근 가능 (R1 선결 조건 해결)

### 1.3 Related Documents

| 문서 | 역할 |
|------|------|
| `docs/00-research/research-dx11-gpu-rendering.md` | AtlasEngine, GPU 인스턴싱, HLSL 셰이더 |
| `docs/00-research/research-winui3-dx11.md` | SwapChainPanel + D3D11 (Phase 4 참고) |
| `docs/archive/2026-03/conpty-integration/conpty-integration.design.md` Section 12 | Phase 3 Migration Notes |
| `docs/adr/006-vt-mutex-thread-safety.md` | vt_mutex 스레드 안전성 |
| `external/ghostty/example/c-vt-render/src/main.c` | 셀 데이터 추출 레퍼런스 코드 |

---

## 2. Scope

### 2.1 In Scope

- [ ] **vt_bridge 확장**: 행/셀 반복자, 셀 데이터(코드포인트, 색상, 속성) 접근 API (~15개 함수)
- [ ] D3D11 디바이스 + HWND 스왑체인 생성 (FLIP_SEQUENTIAL + Present1)
- [ ] HLSL 버텍스/픽셀 셰이더 (QuadInstance 기반, fxc.exe 사전 컴파일)
- [ ] DirectWrite 폰트 래스터화 + 글리프 아틀라스 (stb_rect_pack, grow-only, USAGE_DEFAULT)
- [ ] GPU 인스턴싱 -- 단일 DrawIndexedInstanced, USAGE_DYNAMIC + MAP_WRITE_DISCARD
- [ ] 전용 렌더 스레드 (`_api`/`_p` 이중 상태 분리, StartPaint 스냅샷 복사)
- [ ] VtCore 행/셀 반복자 연동 -- dirty row 기반 증분 렌더
- [ ] 배경색 + 전경색 (ANSI 16색 + 256색 + TrueColor)
- [ ] 커서 렌더링 (Block/Bar/Underline, 블링킹)
- [ ] 리사이즈 (100ms 디바운스 + 스왑체인 + VtCore + ConPTY 동기화)
- [ ] 프레임 페이싱 (SetMaximumFrameLatency(1) + waitable object, 유휴 시 Present 생략)
- [ ] Win32 HWND 창 + 메시지 루프 + 키 입력 -> ConPTY 전달
- [ ] D3D11 Debug Layer 통합 (리소스 네이밍, ReportLiveDeviceObjects)
- [ ] Phase 2 Migration Notes M-1~M-3, M-7 해결

### 2.2 Out of Scope

- WinUI3 SwapChainPanel 통합 (Phase 4)
- 한국어 IME / TSF (Phase 4)
- 선택 영역(selection) 렌더링 (Phase 4)
- ClearType 서브픽셀 렌더링 (Phase 3은 그레이스케일 AA, Phase 4에서 ClearType)
- Powerline / 박스 드로잉 내장 글리프 (Phase 4, 프로시저럴 셰이더)
- 커스텀 셰이더 후처리 (Phase 5)
- 다중 모니터 DPI (Phase 4)
- COLRv1 컬러 이모지 (Phase 4+)

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Research Note |
|----|-------------|----------|---------------|
| FR-01 | vt_bridge 확장 -- 행/셀 반복자 + 셀 데이터 접근 API | High | ghostty example/c-vt-render 레퍼런스 |
| FR-02 | D3D11 디바이스 + HWND 스왑체인 (FLIP_SEQUENTIAL) | High | AtlasEngine 초기화 패턴 |
| FR-03 | HLSL 셰이더 (VS + PS, QuadInstance 입력 레이아웃) | High | fxc.exe 사전 컴파일 + Debug 런타임 |
| FR-04 | DirectWrite 글리프 래스터화 + 아틀라스 (stb_rect_pack) | High | grow-only, USAGE_DEFAULT |
| FR-05 | GPU 인스턴싱 (USAGE_DYNAMIC + MAP_WRITE_DISCARD) | High | 프레임당 1회 업로드, 32B 정렬 |
| FR-06 | 렌더 스레드 (_api/_p 분리, 60fps, waitable object) | High | Phase 2 M-1 해결 |
| FR-07 | dirty row 기반 증분 렌더 + 유휴 시 Present 생략 | High | invalidatedRows 패턴 |
| FR-08 | ANSI 색상 (16/256/TrueColor) + 기본 배경/전경 | High | GhosttyRenderStateColors 팔레트 |
| FR-09 | 커서 렌더링 (Block/Bar/Underline, 블링킹) | Medium | GhosttyRenderStateCursorVisualStyle |
| FR-10 | 리사이즈 (100ms 디바운스 + 스왑체인 리사이즈) | Medium | Phase 2 M-7, KL-3 방어 |
| FR-11 | Win32 HWND 창 + 키 입력 -> ConPTY | High | WM_CHAR/WM_KEYDOWN 처리 |
| FR-12 | D3D11 Debug Layer (리소스 네이밍, 누수 감지) | Medium | ReportLiveDeviceObjects |

### 3.2 R1 선결 조건 해결: vt_bridge 확장 API

libghostty-vt의 렌더 상태 API 조사 완료. 필요한 C 브릿지 함수:

| Category | Function | ghostty API |
|----------|----------|-------------|
| 행 반복자 | `vt_bridge_row_iterator_new/free` | `ghostty_render_state_row_iterator_new/free` |
| | `vt_bridge_row_iterator_init` | `ghostty_render_state_get(ROW_ITERATOR)` |
| | `vt_bridge_row_iterator_next` | `ghostty_render_state_row_iterator_next` |
| | `vt_bridge_row_is_dirty` | `ghostty_render_state_row_get(DIRTY)` |
| 셀 반복자 | `vt_bridge_row_cells_new/free` | `ghostty_render_state_row_cells_new/free` |
| | `vt_bridge_row_cells_init` | `ghostty_render_state_row_get(CELLS)` |
| | `vt_bridge_row_cells_next` | `ghostty_render_state_row_cells_next` |
| 셀 데이터 | `vt_bridge_cell_grapheme_count` | `get(GRAPHEMES_LEN)` |
| | `vt_bridge_cell_graphemes` | `get(GRAPHEMES_BUF)` |
| | `vt_bridge_cell_style` | `get(STYLE)` + 색상 해석 |
| | `vt_bridge_cell_fg_color` | `get(FG_COLOR)` |
| | `vt_bridge_cell_bg_color` | `get(BG_COLOR)` |
| 팔레트 | `vt_bridge_get_colors` | `ghostty_render_state_colors_get` |
| 커서 | `vt_bridge_get_cursor` | 기존 + 블링킹/뷰포트 추가 |
| dirty | `vt_bridge_row_set_clean` | `ghostty_render_state_row_set(DIRTY, false)` |

### 3.3 Phase 2 Migration Notes 해결 계획

| Migration | Solution | Pattern |
|-----------|----------|---------|
| M-1: write/render 동시 접근 | `_api`/`_p` 이중 상태 분리. StartPaint()에서 스냅샷 복사 후 렌더 | Windows Terminal 패턴 |
| M-2: vt_core() 뮤터블 참조 | ConPtySession에 `snapshot_render_state()` 메서드 추가. 직접 참조 제거 | 스냅샷 기반 |
| M-3: update_render_state() 빈도 | 16ms 주기 호출. 뮤텍스 보유 시간 최소화 (스냅샷 복사만) | 최소 락 패턴 |
| M-7: 리사이즈 디바운스 | 100ms 디바운스 타이머. WM_SIZE -> 타이머 리셋 -> 만료 시 리사이즈 | 표준 Win32 타이머 |

### 3.4 Non-Functional Requirements

| Category | Criteria | Measurement |
|----------|----------|-------------|
| 프레임 레이트 | 유휴 시 0 GPU 사용, 활성 시 60fps | GPU 프로파일러 |
| 렌더 레이턴시 | dirty -> 화면 < 16ms | 타이밍 측정 |
| 입력 레이턴시 | 키 입력 -> 화면 표시 < 32ms (2프레임 이내) | 측정 |
| 글리프 아틀라스 | 128MB 이하 | 메모리 측정 |
| 인스턴스 버퍼 | 프레임당 1회 MAP_WRITE_DISCARD | D3D11 Debug Layer |
| 리소스 누수 | ReportLiveDeviceObjects 클린 | Debug 빌드 종료 시 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] Win32 HWND 창에 cmd.exe/pwsh.exe 출력이 실시간 렌더링됨
- [ ] 키보드 입력으로 셸과 대화 가능 (echo, dir, cd 등)
- [ ] ANSI 색상(16/256/TrueColor) 정상 표시
- [ ] 커서가 올바른 위치에 표시되고 블링킹
- [ ] 유휴 시 GPU 사용률 ~0%
- [ ] 리사이즈 시 화면 정상 갱신 (디바운스 적용)
- [ ] Phase 2 테스트 8/8 PASS 유지

### 4.2 Quality Criteria

- [ ] D3D11 Debug Layer 클린 (WARNING/ERROR 0)
- [ ] ReportLiveDeviceObjects 클린 (누수 0)
- [ ] 렌더 스레드 ↔ I/O 스레드 data race 없음
- [ ] 한글 글리프 렌더링 정상 (CJK 폰트 fallback)
- [ ] MSVC Debug/Release 빌드 성공

---

## 5. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|------|--------|------------|------------|
| R1 | ~~셀 데이터 API 부족~~ | ~~High~~ | ~~Medium~~ | **해결됨**: 행/셀 반복자 + 전체 속성 접근 확인 |
| R2 | CJK DirectWrite 폰트 fallback 성능 | Medium | High | 폰트 페이스별 글리프 캐시 분리, grow-only 아틀라스 |
| R3 | _api/_p 분리 구현 복잡도 | Medium | Medium | Windows Terminal StartPaint() 패턴 참조, 단계적 구현 |
| R4 | HLSL 셰이더 디버깅 | Low | Medium | fxc.exe /Zi, PIX 또는 RenderDoc 사용 |
| R5 | 스왑체인 리사이즈 깜빡임 | Low | Medium | FLIP_SEQUENTIAL + 디바운스 + 리사이즈 중 렌더 일시정지 |

---

## 6. Architecture

### 6.1 Key Decisions (리서치 반영)

| Decision | Selected | Rationale | Source |
|----------|----------|-----------|--------|
| 렌더링 API | D3D11 FL 10.0+ | 2026년 WT 유지, 싱글스레드 렌더에 적합, D3D12 불필요 | Agent 2 |
| 스레드 동기화 | `_api`/`_p` 이중 상태 + StartPaint 스냅샷 | GPU 작업 중 락 불필요, WT 검증 패턴 | Agent 3 |
| 스왑 효과 | `FLIP_SEQUENTIAL` + `Present1()` dirty rect | DWM PSR 지원, 전력 절감, 유휴 시 Present 생략 | Agent 3 |
| 프레임 페이싱 | `SetMaximumFrameLatency(1)` + waitable object | 입력 레이턴시 최소화, WT 동일 패턴 | Agent 3 |
| 인스턴스 버퍼 | `USAGE_DYNAMIC` + 프레임당 1회 MAP_WRITE_DISCARD | 4ms+ -> 0.267ms, NVIDIA 벤치마크 검증 | Agent 3 |
| QuadInstance | 32바이트, 16비트 타입, 32바이트 정렬 | memcpy 1.5~40x 가속 (Intel/AMD) | Agent 3 |
| 아틀라스 텍스처 | `USAGE_DEFAULT` + D2D 래스터라이즈 + grow-only | GPU 최적 메모리 배치, 축소 불필요 | Agent 3 |
| 아틀라스 패킹 | stb_rect_pack (Skyline) | C/C++ 최선, etagere는 Rust only, Slug 터미널 부적합 | Agent 2 |
| 셰이더 컴파일 | Debug: D3DCompileFromFile, Release: fxc.exe -> .h | WT 동일, CMake add_custom_command 패턴 | Agent 2 |
| CJK 최적화 | 폰트 페이스별 글리프 캐시 + Zero-bin | WT AtlasFontFaceEntry 패턴 | Agent 3 |

### 6.2 4-스레드 모델 (Phase 3 완성)

```
I/O 스레드          메인 스레드 (Win32)     렌더 스레드
──────────          ──────────────────     ──────────
ReadFile 루프       WM_CHAR -> send_input  WaitForVBlank
    │               WM_SIZE -> 디바운스         │
    │               WM_PAINT -> 무시        StartPaint():
    │                                       vt_mutex lock
vt_mutex lock                               _api -> _p 복사
VtCore.write()                              vt_mutex unlock
vt_mutex unlock                                 │
    │                                      dirty rows 없음?
    │                                       → Present 생략
    │                                           │
    │                                      행/셀 반복자 순회
    │                                      글리프 아틀라스 조회
    │                                      QuadInstance 구성
    │                                      MAP_WRITE_DISCARD
    │                                      DrawIndexedInstanced
    │                                      Present1(dirty rect)
```

### 6.3 폴더 구조

```
ghostwin/
├── src/
│   ├── vt-core/                    # Phase 1 + Phase 3 확장
│   │   ├── vt_bridge.h             # 행/셀 반복자 API 추가
│   │   ├── vt_bridge.c             # 행/셀 반복자 구현 추가
│   │   ├── vt_core.h              # 셀 데이터 접근 C++ API 추가
│   │   └── vt_core.cpp
│   ├── conpty/                     # Phase 2 (기존)
│   └── renderer/                   # Phase 3 (신규)
│       ├── dx11_renderer.h         # 렌더러 공개 인터페이스
│       ├── dx11_renderer.cpp       # D3D11 디바이스, 스왑체인, 렌더 루프
│       ├── glyph_atlas.h           # 글리프 아틀라스 관리
│       ├── glyph_atlas.cpp         # DirectWrite + stb_rect_pack
│       ├── render_state.h          # _api/_p 이중 상태 + 스냅샷
│       ├── render_state.cpp        # StartPaint/EndPaint 패턴
│       ├── terminal_window.h       # Win32 HWND 래퍼 (Phase 3 PoC)
│       ├── terminal_window.cpp     # 메시지 루프, 키 입력, 리사이즈
│       ├── shader_vs.hlsl          # 버텍스 셰이더
│       └── shader_ps.hlsl          # 픽셀 셰이더
├── third_party/
│   └── stb_rect_pack.h            # 글리프 아틀라스 패킹
├── tests/
│   └── dx11_render_test.cpp        # Win32 창 기반 통합 테스트
└── CMakeLists.txt                  # renderer + d3d11/dwrite/fxc
```

---

## 7. Implementation Guide

### 7.1 구현 순서

```
 1. vt_bridge 확장 -- 행/셀 반복자 + 셀 데이터 접근 API (FR-01)
 2. VtCore C++ API 확장 -- CellIterator, RowIterator 래퍼
 3. D3D11 디바이스 + HWND 스왑체인 초기화 (FR-02)
 4. HLSL 셰이더 작성 + CMake fxc 컴파일 설정 (FR-03)
 5. DirectWrite + 글리프 아틀라스 (FR-04)
 6. render_state.h -- _api/_p 이중 상태 + StartPaint 스냅샷 (FR-06)
 7. 렌더 스레드 -- 60fps 루프, waitable object, 유휴 시 생략 (FR-06, FR-07)
 8. 셀 데이터 -> QuadInstance 변환 + GPU 인스턴싱 (FR-05, FR-08)
 9. 커서 렌더링 (FR-09)
10. Win32 창 -- HWND, 메시지 루프, 키 입력 -> ConPTY (FR-11)
11. 리사이즈 -- 100ms 디바운스 + 스왑체인 + VtCore + ConPTY (FR-10)
12. D3D11 Debug Layer 통합 (FR-12)
13. 통합 테스트 + 성능 측정
```

### 7.2 빌드 요구사항 (Phase 3 추가)

| Item | Purpose |
|------|---------|
| d3d11.lib, dxgi.lib | D3D11/DXGI API |
| d3dcompiler.lib | Debug 시 런타임 셰이더 컴파일 |
| dwrite.lib, d2d1.lib | DirectWrite + D2D (글리프 래스터화) |
| fxc.exe | Release 셰이더 사전 컴파일 (Windows SDK 포함) |
| stb_rect_pack.h | 헤더 only 라이브러리 |

---

## 8. Next Steps

1. [ ] Design 문서 작성 (`dx11-rendering.design.md`)
2. [ ] vt_bridge 행/셀 반복자 API 구현
3. [ ] D3D11 디바이스 초기화 프로토타이핑

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-29 | Initial draft | Solit |
| **0.2** | **2026-03-29** | **3개 에이전트 리서치 반영: R1 해결(셀 API 확인), _api/_p 패턴, Present1 dirty rect, MAP_WRITE_DISCARD, waitable object, CJK 최적화** | **Solit** |
