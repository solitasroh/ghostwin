# winui3-shell Plan

> **Feature**: WinUI3 UI Shell + SwapChainPanel DX11 연결
> **Project**: GhostWin Terminal
> **Phase**: 4-A (Master: winui3-integration FR-01~07)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: 없음 (메인 트랙, C/D/E 결과물 자동 반영)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 DX11 렌더러가 Win32 HWND 위에서만 동작하여 현대적 XAML UI를 제공할 수 없음 |
| **Solution** | WinUI3 C++/WinRT 앱으로 전환, SwapChainPanel에 기존 DX11 렌더러 스왑체인 연결 |
| **Function/UX** | XAML 기반 창 구조 (커스텀 타이틀바 + 사이드바 스켈레톤 + SwapChainPanel 터미널)에서 기존 렌더링 동일 동작 |
| **Core Value** | Win32 HWND 종속성 제거. Phase 5+(탭, AI UX) 구현을 위한 XAML 기반 확보 |

---

## 1. Background

Phase 3에서 DX11 렌더링 코어가 완성되었으나 `terminal_window.cpp`(Win32 HWND)에 종속되어 있다.
WinUI3 SwapChainPanel로 전환하면 XAML 비주얼 트리와 통합되어 탭, 사이드바, 알림 등을 선언적으로 구성할 수 있다.

---

## 2. Functional Requirements

### FR-01: WinUI3 프로젝트 구조 전환
- Windows App SDK 1.8.x (Stable) + C++/WinRT 프로젝트 생성
- NuGet: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.CppWinRT`
- 기존 CMake 빌드에 WinUI3 타겟 추가 (또는 `.vcxproj` 병행)
- Unpackaged 앱 모드 (MSIX 없이 실행 가능)

### FR-02: SwapChainPanel + DX11 연결
- `DX11Renderer`의 스왑체인 생성을 `CreateSwapChainForComposition`으로 변경
- `ISwapChainPanelNative::SetSwapChain()` 연결 (Loaded 이벤트 타이밍)
- C++/WinRT `.as<ISwapChainPanelNative>()` 패턴 사용 (reinterpret_cast 금지)

### FR-03: 렌더 스레드 분리
- `winrt::Windows::System::Threading::ThreadPool::RunAsync`로 렌더 루프
- UI 스레드에서 스왑체인 리사이즈 시 `DispatcherQueue` 사용
- 기존 4-스레드 모델 (UI/Render/Parse/IO) 유지

### FR-04: 키보드 입력 전달
- SwapChainPanel의 `KeyDown`/`CharacterReceived` XAML 이벤트 → ConPTY 전달
- Phase 3의 `WM_CHAR`/`WM_KEYDOWN` 로직을 XAML 이벤트 핸들러로 이전
- Backspace=0x7F, Tab, Enter 등 특수키 동작 유지 검증

### FR-05: 리사이즈 + DPI 처리
- `SwapChainPanel::SizeChanged` → 스왑체인 리사이즈 + ConPTY 리사이즈
- `CompositionScaleChanged` → DPI 변경 시 물리 픽셀 기준 리사이즈
- Phase 3의 100ms 디바운스 패턴 유지

### FR-06: XAML 레이아웃 스켈레톤
- `MainWindow.xaml`: Grid 2-컬럼 레이아웃 (사이드바 220px + 터미널 *)
- 사이드바: 빈 ListView (placeholder, Phase 5에서 탭 목록 바인딩)
- 터미널 영역: SwapChainPanel
- 커스텀 타이틀바: `ExtendsContentIntoTitleBar` + 드래그 영역

### FR-07: Mica 배경 (선택적)
- `MicaController` 적용 (지원 환경에서만, 폴백은 단색 배경)
- SwapChainPanel은 투명도 미지원이므로 창 배경에만 적용

---

## 3. Architecture

### 3.1 Before (Phase 3)

```
main.cpp → TerminalWindow (Win32 HWND)
              ├── WndProc (WM_CHAR, WM_KEYDOWN, WM_SIZE)
              ├── DX11Renderer (CreateSwapChainForHwnd)
              ├── ConPtySession
              └── RenderState + GlyphAtlas + QuadBuilder
```

### 3.2 After

```
App.xaml.cpp → MainWindow.xaml (WinUI3 C++/WinRT)
                 ├── CustomTitleBar (drag region)
                 ├── Grid
                 │   ├── ListView (sidebar placeholder, Col=0)
                 │   └── SwapChainPanel (Col=1)
                 │       ├── Loaded → InitD3D11 (CreateSwapChainForComposition)
                 │       ├── SizeChanged → Resize
                 │       ├── CompositionScaleChanged → DPI
                 │       └── KeyDown/CharacterReceived → ConPTY
                 ├── DX11Renderer (modified: Composition swapchain)
                 ├── ConPtySession (unchanged)
                 └── RenderState + GlyphAtlas + QuadBuilder (unchanged)
```

### 3.3 변경 범위

| Module | Change Type | Detail |
|--------|-------------|--------|
| `DX11Renderer` | **수정** | HWND swapchain → Composition swapchain |
| `TerminalWindow` | **교체** | `terminal_window.h/cpp` → `MainWindow.xaml.h/cpp` |
| `main.cpp` | **교체** | Win32 진입점 → WinUI3 `App::OnLaunched` |
| `CMakeLists.txt` | **수정** | WinUI3 빌드 타겟 추가 |
| 그 외 모듈 | 변경 없음 | |

---

## 4. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | Windows App SDK 1.8 NuGet + C++/WinRT 설정 | `cppwinrt.exe` 코드 생성 성공 |
| S2 | `App.xaml` + `MainWindow.xaml` 스캐폴딩 | 빈 WinUI3 창 실행 확인 |
| S3 | Unpackaged 실행 설정 (Bootstrap API) | MSIX 없이 exe 실행 |
| S4 | `DX11Renderer` 수정: `CreateSwapChainForComposition` | Composition 스왑체인 생성 성공 |
| S5 | `SwapChainPanel.Loaded` → `SetSwapChain()` | clear 색상 렌더 확인 |
| S6 | 렌더 스레드 ThreadPool 분리 + 기존 렌더 루프 | cmd.exe 출력 표시 |
| S7 | `KeyDown`/`CharacterReceived` → ConPTY 입력 | 키보드 + 특수키 동작 |
| S8 | `SizeChanged` → 스왑체인 + ConPTY 리사이즈 | 창 크기 변경 정상 |
| S9 | `CompositionScaleChanged` → DPI 대응 | 모니터 이동 시 선명도 유지 |
| S10 | Grid 2-컬럼 레이아웃 (사이드바 + 터미널) | 사이드바 영역 표시 |
| S11 | 커스텀 타이틀바 (`ExtendsContentIntoTitleBar`) | 드래그 + 창 버튼 동작 |
| S12 | Mica 배경 (조건부) | 지원 환경 Mica, 미지원 단색 |

---

## 5. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | WinUI3 창에서 cmd.exe/pwsh.exe 실시간 렌더링 | 육안 확인 |
| 2 | 키보드 입력으로 셸 대화 가능 | echo, dir, Backspace 등 |
| 3 | ANSI 16/256/TrueColor 색상 표시 | Starship 프롬프트 |
| 4 | 창 리사이즈 정상 | 100ms 디바운스, 깨짐 없음 |
| 5 | DPI 변경 시 선명도 유지 | 모니터 이동 또는 배율 변경 |
| 6 | 유휴 시 GPU < 1% | GPU-Z 측정 |
| 7 | 커스텀 타이틀바 + 사이드바 스켈레톤 | XAML 레이아웃 렌더링 |
| 8 | 기존 Phase 3 테스트 23/23 PASS | CI/로컬 빌드 |

---

## 6. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | CMake + WinUI3 XAML 빌드 통합 복잡도 | 상 | `.vcxproj` 병행 또는 cmake-winui3 generator 패턴 조사 |
| R2 | Unpackaged WinUI3 앱 제약 | 중 | `WindowsPackageType=None` + Bootstrap API 초기화 |
| R3 | SwapChainPanel 키보드 포커스 | 중 | `IsTabStop=true` + `Focus(FocusState.Programmatic)` |
| R4 | 렌더 스레드 vs UI 스레드 교착 | 상 | vt_mutex 패턴 유지 + DispatcherQueue 비동기 |
| R5 | Composition 스왑체인 성능 차이 | 하 | FLIP_SEQUENTIAL + waitable 유지 |

---

## 7. References

| Document | Path |
|----------|------|
| WinUI3 + DX11 리서치 | `docs/00-research/research-winui3-dx11.md` |
| cmux AI 에이전트 UX 리서치 | `docs/00-research/cmux-ai-agent-ux-research.md` |
| Phase 3 완료 보고서 | `docs/archive/2026-03/dx11-rendering/dx11-rendering.report.md` |
| Phase 3 TerminalWindow (교체 대상) | `src/renderer/terminal_window.h/cpp` |
