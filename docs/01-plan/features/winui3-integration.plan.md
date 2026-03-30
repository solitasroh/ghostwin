# winui3-integration Master Plan

> **Feature**: Phase 4 — WinUI3 통합 + Phase 3 Known Limitations 해소
> **Project**: GhostWin Terminal
> **Phase**: 4
> **Date**: 2026-03-30
> **Author**: Solit
> **Previous Phase**: Phase 3 (dx11-rendering) — 96.6% match, 23/23 tests PASS
> **Structure**: 5개 독립 PDCA로 분리 실행 (아래 Sub-Feature Map 참조)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 DX11 렌더러가 Win32 HWND 위에서만 동작하여 탭, 사이드바, DPI 처리 등 현대적 UI를 제공할 수 없음 |
| **Solution** | WinUI3 C++/WinRT 앱으로 전환하고, SwapChainPanel에 DX11 렌더러를 연결. Phase 3 Known Limitations 6개 항목 전부 해결 (IME, ClearType, Nerd Font, GPU 유휴, QuadInstance 최적화, DPI) |
| **Function/UX** | XAML 기반 창에서 한글 IME 조합 입력, ClearType 서브픽셀 선명도, Nerd Font 아이콘이 동작하는 완성도 높은 터미널 렌더링 |
| **Core Value** | Phase 3의 기술 부채 전부 해소 + XAML UI 기반 확보로 Phase 5+(탭, AI 에이전트 UX) 진입 준비 완료 |

---

## 1. Background

### 1.1 Phase 3 완료 상태

Phase 3에서 DX11 GPU 렌더링 코어가 완성되었다:
- D3D11 인스턴싱 + DirectWrite 글리프 아틀라스 + _api/_p 이중 상태 패턴
- 2-pass 렌더링 (배경→텍스트), 한글 폰트 폴백
- Win32 HWND 기반 메시지 루프 (`terminal_window.cpp`)

### 1.2 Phase 3 Known Limitations (이번 Phase에서 전부 해결)

Phase 3 보고서 Section 5에서 "Phase 4 Target"으로 지정된 6개 항목 전체를 이번 Phase에서 해결한다.

| Item | Phase 3 상태 | Phase 4 목표 |
|------|-------------|-------------|
| 한글 IME 입력 | 미지원 (jamo 분리) | TSF ITfContextOwner 구현 |
| ClearType 서브픽셀 | 그레이스케일 AA | dwrite-hlsl HLSL 서브픽셀 렌더링 |
| Nerd Font 아이콘 | 미표시 | 폰트 폴백 체인 확장 |
| 유휴 GPU 0% 실측 | 미측정 | waitable swapchain + FrameStats 프로파일링 |
| QuadInstance 크기 | 68B (R32 포맷) | StructuredBuffer 32B 전환 |
| 다중 모니터 DPI | 미구현 | CompositionScaleChanged 이벤트 처리 |
| Win32 HWND 종속 | terminal_window.cpp | SwapChainPanel로 교체 |

### 1.3 Phase 4에서 다루지 않는 항목 (Phase 5+ 이후)

| Item | 사유 |
|------|------|
| 탭 멀티플렉싱 (다중 세션) | UI 셸 확보 후 Phase 5에서 구현 |
| AI 에이전트 알림 시스템 | OSC hooks + Toast, Phase 5+ |

---

## 2. Goal

Phase 3의 Win32 HWND 기반 PoC 윈도우를 WinUI3 C++/WinRT 앱으로 교체한다.

**핵심 목표:**
1. WinUI3 SwapChainPanel에 기존 DX11 렌더러의 스왑체인을 연결
2. Phase 3의 모든 기능(렌더링, 키입력, 리사이즈)이 WinUI3 위에서 동일하게 동작
3. Phase 3 Known Limitations 6개 항목 전부 해결 (IME, ClearType, Nerd Font, GPU 유휴, QuadInstance 최적화, DPI)
4. 향후 탭/Pane/알림 UI를 붙일 수 있는 XAML 레이아웃 스켈레톤 구성

---

## 3. Functional Requirements (FR)

### FR-01: WinUI3 프로젝트 구조 전환
- Windows App SDK 1.8.x (Stable) + C++/WinRT 프로젝트 생성
- NuGet: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.CppWinRT`
- 기존 CMake 빌드에 WinUI3 타겟 추가 (또는 `.vcxproj` 병행)
- Unpackaged 앱 모드 (MSIX 없이 실행 가능)

### FR-02: SwapChainPanel + DX11 연결
- `DX11Renderer`의 스왑체인 생성을 `CreateSwapChainForComposition`으로 변경
- `ISwapChainPanelNative::SetSwapChain()` 연결 (Loaded 이벤트 타이밍)
- C++/WinRT `.as<ISwapChainPanelNative>()` 패턴 사용 (reinterpret_cast 금지)
- Phase 3 `CreateSwapChainForHwnd` 코드 경로 제거 또는 조건부 유지

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

### FR-08: 한글 IME (TSF) 지원
- TSF `ITfContextOwner` + `ITfContextOwnerCompositionSink` 구현
- 조합 문자(composing) 실시간 렌더링 (글리프 아틀라스에 조합 중 문자 표시)
- 조합 완료(committed) → ConPTY UTF-8 전송
- Windows Terminal `TsfDataProvider` 참고 구현

### FR-09: ClearType 서브픽셀 렌더링
- DirectWrite 그레이스케일 → 서브픽셀 안티앨리어싱 전환
- HLSL 픽셀 셰이더에서 RGB 서브픽셀 가중 블렌딩 구현
- LCD 모니터에서 글자 선명도 향상 검증

### FR-10: Nerd Font 폰트 폴백 체인
- 폰트 폴백 체인 확장: Primary → CJK → Nerd Font → Emoji 순서
- DirectWrite `IDWriteFontFallback` 커스텀 빌더 사용
- Nerd Font 심볼 (U+E000~U+F8FF, PUA 영역) 표시 검증

### FR-11: QuadInstance 32B 최적화
- R32 포맷 (68B) → StructuredBuffer (32B) 전환
- HLSL `StructuredBuffer<QuadInstance>` + `SV_InstanceID`로 읽기
- Input Layout 제거 → 셰이더 단순화 + GPU 대역폭 절감

---

## 4. Non-Functional Requirements (NFR)

| # | Requirement | Target |
|---|-------------|--------|
| NFR-01 | Phase 3 테스트 유지 | 기존 23/23 PASS 보존 |
| NFR-02 | 첫 프레임 렌더 시간 | < 500ms (SwapChainPanel Loaded → Present) |
| NFR-03 | 유휴 GPU 사용률 | < 1% (waitable swapchain) |
| NFR-04 | 빌드 시스템 | CMake + vcpkg 또는 CMake + NuGet 통합 |
| NFR-05 | Windows 최소 지원 | Windows 10 1809+ (WinAppSDK 1.8 요구) |
| NFR-06 | Unpackaged 실행 | MSIX 패키징 없이 실행 가능 |

---

## 5. Architecture Changes

### 5.1 Before (Phase 3)

```
main.cpp → TerminalWindow (Win32 HWND)
              ├── WndProc (WM_CHAR, WM_KEYDOWN, WM_SIZE)
              ├── DX11Renderer (CreateSwapChainForHwnd)
              ├── ConPtySession
              └── RenderState + GlyphAtlas + QuadBuilder
```

### 5.2 After (Phase 4)

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

### 5.3 변경 범위

| Module | Change Type | Detail |
|--------|-------------|--------|
| `DX11Renderer` | **수정** | HWND swapchain → Composition swapchain 전환. `create()` 시그니처 변경 |
| `TerminalWindow` | **교체** | `terminal_window.h/cpp` → `MainWindow.xaml.h/cpp` |
| `main.cpp` | **교체** | Win32 진입점 → WinUI3 `App::OnLaunched` |
| `ConPtySession` | 변경 없음 | |
| `RenderState` | 변경 없음 | |
| `GlyphAtlas` | **수정** | Nerd Font 폴백 체인 확장 + ClearType 서브픽셀 출력 |
| `QuadBuilder` | **수정** | StructuredBuffer 32B 포맷 전환 |
| `vt_core` / `vt_bridge` | 변경 없음 | |
| `TsfProvider` (신규) | **추가** | TSF ITfContextOwner IME 입력 처리 |
| `CMakeLists.txt` | **수정** | WinUI3 빌드 타겟 추가, NuGet/vcpkg 패키지 |
| shaders (hlsl) | **수정** | StructuredBuffer 읽기 + ClearType 서브픽셀 블렌딩 |

---

## 6. Implementation Steps

### Step 1: WinUI3 프로젝트 기반 설정 (S1-S3)

| # | Task | DoD |
|---|------|-----|
| S1 | Windows App SDK 1.8 NuGet 패키지 + C++/WinRT 설정 | `cppwinrt.exe` 코드 생성 성공 |
| S2 | `App.xaml` + `MainWindow.xaml` 스캐폴딩 | 빈 WinUI3 창 실행 확인 |
| S3 | Unpackaged 실행 설정 (자동 초기화) | MSIX 없이 `ghostwin_terminal.exe` 실행 |

### Step 2: SwapChainPanel DX11 연결 (S4-S6)

| # | Task | DoD |
|---|------|-----|
| S4 | `DX11Renderer` 수정: `CreateSwapChainForComposition` 경로 추가 | Composition 스왑체인 생성 성공 |
| S5 | `SwapChainPanel.Loaded` → `ISwapChainPanelNative::SetSwapChain()` | clear 색상 렌더 확인 |
| S6 | 렌더 스레드 ThreadPool 분리 + 기존 렌더 루프 연결 | cmd.exe 출력 화면 표시 |

### Step 3: 입력 + 리사이즈 (S7-S9)

| # | Task | DoD |
|---|------|-----|
| S7 | `KeyDown`/`CharacterReceived` → ConPTY 입력 | 키보드 타이핑 + Backspace/Tab/Enter 동작 |
| S8 | `SizeChanged` → 스왑체인 + ConPTY 리사이즈 | 창 크기 변경 시 터미널 재배치 |
| S9 | `CompositionScaleChanged` → DPI 대응 | 다중 모니터 이동 시 선명도 유지 |

### Step 4: XAML UI 스켈레톤 (S10-S12)

| # | Task | DoD |
|---|------|-----|
| S10 | Grid 2-컬럼 레이아웃 (사이드바 + 터미널) | 사이드바 영역 표시 (빈 ListView) |
| S11 | 커스텀 타이틀바 (`ExtendsContentIntoTitleBar`) | 타이틀바 드래그 + 최소화/최대화/닫기 동작 |
| S12 | Mica 배경 (조건부) | 지원 환경에서 Mica 적용, 미지원 시 단색 폴백 |

### Step 5: 한글 IME + TSF (S13-S15)

| # | Task | DoD |
|---|------|-----|
| S13 | `TsfProvider` 클래스: ITfContextOwner + ITfContextOwnerCompositionSink | TSF 컨텍스트 등록 성공 |
| S14 | 조합 문자 실시간 렌더링 (composing → GlyphAtlas) | 한글 입력 시 조합 중 글리프 표시 |
| S15 | 조합 완료 → ConPTY UTF-8 전송 | 한글 완성 문자 셸 입력 동작 |

### Step 6: ClearType ���브픽셀 렌더링 (S16-S17)

| # | Task | DoD |
|---|------|-----|
| S16 | DirectWrite 래스터화: 그레이스케일 → ���브픽셀 전환 | RGB 서브픽셀 텍스처 생성 |
| S17 | HLSL ���셀 셰이더 서브픽셀 블렌딩 | LCD 모니터에서 선명도 향상 육안 확인 |

### Step 7: Nerd Font 폴백 + QuadInstance 최적화 (S18-S20)

| # | Task | DoD |
|---|------|-----|
| S18 | IDWriteFontFallback 커스텀 체인 (Primary → CJK → Nerd Font → Emoji) | Nerd Font 심볼 (U+E000~) 표시 |
| S19 | QuadInstance R32 68B → StructuredBuffer 32B 전환 | 셰이더에서 SV_InstanceID 기반 읽기 |
| S20 | Input Layout 제거 + 벤치마크 비교 | GPU 대역폭 절감 확인 (68B→32B) |

### Step 8: 통합 검증 (S21-S23)

| # | Task | DoD |
|---|------|-----|
| S21 | Phase 3 전체 ���능 회귀 테스트 | cmd/pwsh 렌더링, ANSI 색��, 한글, 커서, 리사이즈 |
| S22 | 유휴 GPU 0% 검증 + FrameStats 프로파일링 | GPU-Z 등으로 유휴 시 < 1% ���인 |
| S23 | 한글 IME + Nerd Font + ClearType 통��� 검증 | 한글 조합 입력 + 아이콘 + 서브픽셀 동시 동작 |

---

## 7. Definition of Done (DoD)

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | WinUI3 창에서 cmd.exe/pwsh.exe 실시�� 렌더링 | 육안 확인 |
| 2 | 키보��� 입력으로 셸 대화 가능 | echo, dir, Backspace 등 동작 |
| 3 | ANSI 16/256/TrueColor 색상 표시 | Starship 프롬프트 등 |
| 4 | 창 리사이즈 시 터미널 재배치 | 깨짐 없음, 100ms 디바운스 |
| 5 | DPI 변경 ��� 선명도 유지 | 모니터 이동 또는 배율 변경 |
| 6 | 유휴 시 GPU < 1% | GPU-Z + FrameStats 측정 |
| 7 | 커스텀 타이틀바 + 사이드바 스켈레톤 | XAML 레이아웃 렌더링 |
| 8 | 한글 IME 조합 입력 동작 | 한글 조합→완성 → 셸 전달 |
| 9 | ClearType 서브픽셀 렌더링 | LCD에서 그레이스케일 대비 선명도 향상 |
| 10 | Nerd Font 심볼 표시 | U+E000~U+F8FF PUA 아이콘 ��더링 |
| 11 | QuadInstance 32B StructuredBuffer | 68B→32B 전환 + 기존 렌더링 유지 |
| 12 | 기존 Phase 3 테스트 23/23 PASS | CI/로컬 빌드 |

---

## 8. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | CMake + WinUI3 XAML 빌드 통합 복잡도 | 상 | `.vcxproj` 병행 또는 cmake-winui3 generator 패턴 조사 |
| R2 | Unpackaged WinUI3 앱의 제약 (일부 API 미지원) | 중 | `WindowsPackageType=None` + Bootstrap API 초기화 |
| R3 | SwapChainPanel 키보드 포커스 이슈 | 중 | `IsTabStop=true` + `Focus(FocusState.Programmatic)` |
| R4 | 렌더 스레드 vs UI 스레드 교착 | 상 | Phase 3 vt_mutex 패턴 유지 + DispatcherQueue 비동기 |
| R5 | Composition 스왑체인 성능 차이 | 하 | FLIP_SEQUENTIAL + waitable 유지, 벤치마크 비교 |
| R6 | TSF IME 구현 난이도 | 상 | Windows Terminal `TsfDataProvider` 패턴 참고. 최소 인터페이스(ITfContextOwner)부터 구현 |
| R7 | ClearType 서브픽셀 HLSL 복잡도 | 중 | 그레이스케일 폴백 유지하면서 점진적 전환. Windows Terminal dwrite-hlsl 참고 |
| R8 | StructuredBuffer 전환 시 기존 렌더링 깨짐 | 중 | R32 Input Layout 경로를 조건부 유지하여 A/B 비교 가능 |

---

## 9. Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| Windows App SDK | 1.8.x (Stable) | WinUI3 런타임 |
| Microsoft.Windows.CppWinRT | 2.0.x | C++/WinRT 헤더 생성 |
| Visual Studio 2022 | 17.6+ | XAML 디자이너 + vcpkg 내장 |
| Windows SDK | 10.0.22621.0 | ADR-005 고정 (변경 없음) |
| libghostty-vt | DLL (변경 없음) | VT 파싱 |
| DirectX 11 | SDK 내장 (변경 없음) | GPU 렌더링 |

---

## 10. References

| Document | Path |
|----------|------|
| WinUI3 + DX11 리서치 | `docs/00-research/research-winui3-dx11.md` |
| cmux AI 에이전트 UX 리서치 | `docs/00-research/cmux-ai-agent-ux-research.md` |
| DX11 렌더링 리서치 | `docs/00-research/research-dx11-gpu-rendering.md` |
| Phase 3 완료 보고서 | `docs/archive/2026-03/dx11-rendering/dx11-rendering.report.md` |
| ADR-002 C 브릿지 패턴 | `docs/adr/002-c-bridge-pattern.md` |
| ADR-005 SDK 버전 고정 | `docs/adr/005-sdk-version-pinning.md` |

---

## 11. Sub-Feature Map (5개 독립 PDCA)

```
winui3-integration (Master Plan)
├── A: winui3-shell         FR-01~07  [독립]     WinUI3 SwapChainPanel + XAML 셸
├── B: tsf-ime              FR-08     [A 이후]   한글 IME TSF 구현
├── C: cleartype-subpixel   FR-09     [독립]     ClearType 서브��셀 렌더링
├── D: nerd-font-fallback   FR-10     [독립]     Nerd Font 폰트 폴백 체인
└── E: quadinstance-opt     FR-11     [독립]     StructuredBuffer 32B 최적화
```

| ID | Feature Name | FRs | Steps | Dependency | 병행 가능 |
|----|-------------|-----|-------|------------|:---------:|
| A | `winui3-shell` | FR-01~07 | S1~S12 | 없음 | — |
| B | `tsf-ime` | FR-08 | S13~S15 | A 완료 후 | ❌ |
| C | `cleartype-subpixel` | FR-09 | S16~S17 | 없음 | ✅ (A와 병행) |
| D | `nerd-font-fallback` | FR-10 | S18 | 없음 | ✅ (A와 병행) |
| E | `quadinstance-opt` | FR-11 | S19~S20 | 없음 | ✅ (A와 병행) |

**실행 순서 권장:**
1. C, D, E를 먼저 (현재 Win32 HWND 위에서 검증 가능)
2. A (WinUI3 Shell 전환) — C/D/E 결과물이 자동 반영
3. B (TSF IME) — A 완료 후

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial plan |
| 1.1 | 2026-03-30 | Solit | Phase 3 Known Limitations 전체 포함 (IME, ClearType, Nerd Font, QuadInstance) |
| 1.2 | 2026-03-30 | Solit | 5개 독립 PDCA로 분리 (Sub-Feature Map 추가) |
