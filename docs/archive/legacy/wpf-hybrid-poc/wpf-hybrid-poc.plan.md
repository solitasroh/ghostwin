# WPF Hybrid PoC Plan

> **Summary**: WinUI3 Code-only C++ → WPF C# Hybrid 전환 가능성을 48시간(3일) PoC로 검증. ClearType 렌더링, P/Invoke 지연, TSF IME, wpf-ui 테마, 대량 출력 스루풋 6개 항목의 Go/No-Go 판정.
>
> **Project**: GhostWin Terminal
> **Phase**: Pre-Migration (Architecture Decision)
> **Author**: 노수장 (Refined by AI Agent)
> **Date**: 2026-04-05
> **Status**: Approved & Refined
> **Previous Phase**: Phase 5-D (settings-system) — 98% 완료

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 엔진(7K lines)은 완성되었으나, 향후 10K+ UI 코드를 WinUI3 Code-only C++로 작성하면 생산성이 2.5~3배 저하되고 유지보수 부채가 급증함. `winui_app.cpp` 1,919줄 God Class가 이미 한계를 보여주고 있음. |
| **Solution** | 엔진을 CMake native DLL로 격리하고, UI를 WPF C# + CommunityToolkit.Mvvm + wpf-ui로 전환. 이 결정을 확정하기 전에 6개 기술 항목을 PoC로 검증. |
| **Function/UX Effect** | PoC 통과 시: Settings UI, Command Palette, Theme Editor 등 10K+ UI를 XAML 선언형으로 3배 빠르게 개발. Hot Reload로 UI 반복 속도 5~10배 개선. wpf-ui로 Windows 11 네이티브 UX 달성. |
| **Core Value** | "동작하는 시스템을 추측으로 바꾸지 않는다" — 10인 전문가 합의(WPF 7표)를 PoC 실증으로 확정하여, GhostWin의 엔진 품질을 보존하면서 UI 개발 속도를 극대화하는 아키텍처 전환의 안전한 경로 확보. |

---

## 1. User Intent Discovery

### 1.1 Core Purpose
WPF Hybrid 아키텍처로의 **전환 가능성 검증**. 3일 PoC에서 6개 기술 리스크를 실증적으로 검증하여 Go/No-Go를 확정한다.

### 1.2 Target Users
- 프로젝트 개발자 (아키텍처 의사결정 근거 확보)
- 향후 기여자 (C#/WPF 생태계 접근성)

### 1.3 Success Criteria
- [ ] V1: Engine DLL C API 빌드 성공 (C++ 예외 방어막 포함, UTF-16/UTF-8 마샬링 규약 확립)
- [ ] V2: HwndHost + HWND SwapChain에서 ClearType 품질 유지 및 리사이즈 시 깜빡임(Flickering) 동기화 검증
- [ ] V3: P/Invoke 키입력→출력 왕복 지연 < 1ms
- [ ] V4: WPF 내 Hidden HWND + TSF 한글 조합/확정 정상
- [ ] V5: wpf-ui로 Mica + 다크모드 + NavigationView 동작 및 Airspace 제약(오버레이 팝업) 우회 검증
- [ ] V6: `cat large_file` 스루풋 ≥ WinUI3 현행의 90% (WPF Dispatcher 병목 방지를 위한 쓰로틀링/디바운싱 포함)

### 1.4 Go/No-Go 판정 규칙
- V1~V4 **전부 통과**: Go (WPF 전환 확정)
- V1~V4 중 **하나라도 실패**: No-Go (WinUI3 유지)
- V5 실패: Go 유지, 단 테마 자체 구현 또는 다른 라이브러리 검토
- V6 실패: Go 유지, 단 성능 최적화 로드맵 수립

---

## 2. Alternatives Explored

### Approach A: 단계적 PoC 3일 — ✅ 채택
검증 항목을 독립 모듈로 구현. 항목별 Go/No-Go 판정 가능.
- Day 1: Engine DLL + P/Invoke + 빌드 파이프라인
- Day 2: HwndHost + ClearType + TSF IME
- Day 3: wpf-ui 테마 + 스루풋 벤치마크 + 종합 판정

### Approach B: 올인원 프로토타입 — 기각
한꺼번에 구현. 실패 시 원인 격리 어려움.

### Approach C: 최소 핵심 1일 — 기각
ClearType만 검증. 나머지 리스크가 미해결 상태로 남음.

---

## 3. YAGNI Review

### In Scope (PoC)
- [x] Engine DLL C API 설계 + 예외 방어막(try-catch) + CMake SHARED 빌드
- [x] HwndHost 서브클래스 + HWND SwapChain + 리사이즈 동기화
- [x] P/Invoke 레이어 (create_session, write, resize, callbacks)
- [x] Hidden HWND + TSF 한글 IME 검증
- [x] wpf-ui 테마 (Mica, 다크모드, NavigationView) 및 오버레이 팝업 검증
- [x] 대량 출력 스루풋 벤치마크 및 WPF 스레드 쓰로틀링 로직

### Out of Scope (PoC 이후, Design 단계)
- Settings UI 전체 구현 (XAML 페이지)
- Command Palette, Search Overlay
- TabSidebar/TitleBar WPF 재작성
- Pane Split (Phase 5-E)
- Session Restore (Phase 5-F)
- 기존 WinUI3 코드 삭제/아카이브

---

## 4. Architecture

### 4.1 PoC 아키텍처 다이어그램

```text
┌─────────────────────────────────────────────────┐
│              GhostWinPoC.exe (C# WPF .NET 9)    │
│                                                 │
│  ┌──────────────────┐  ┌─────────────────────┐  │
│  │ wpf-ui Theme     │  │ TerminalHostControl │  │
│  │ FluentWindow     │  │ (HwndHost subclass) │  │
│  │ Mica + DarkMode  │  │  ┌───────────────┐  │  │
│  └──────────────────┘  │  │ Child HWND    │  │  │
│                        │  │ HWND SwapChain│  │  │
│  ┌──────────────────┐  │  │ DX11 Render   │  │  │
│  │ NavigationView   │  │  └───────────────┘  │  │
│  │ (테마 확인용)     │  └─────────────────────┘  │
│  └──────────────────┘                           │
│  ┌──────────────────┐  ┌─────────────────────┐  │
│  │ P/Invoke Layer   │  │ HwndSource          │  │
│  │ NativeEngine.cs  │  │ (Hidden HWND → TSF) │  │
│  └────────┬─────────┘  └─────────────────────┘  │
│           │ LibraryImport                        │
├───────────┼──────────────────────────────────────┤
│           ▼                                      │
│  ghostwin_engine.dll (C++ Native, CMake)         │
│  ┌──────────┐ ┌──────────┐ ┌──────────────────┐ │
│  │DX11      │ │ConPTY    │ │VtCore            │ │
│  │Renderer  │ │Session   │ │(→ ghostty-vt.dll)│ │
│  └──────────┘ └──────────┘ └──────────────────┘ │
│  ┌──────────┐                                    │
│  │TSF/IME   │  ← Hidden HWND로 attach           │
│  └──────────┘                                    │
├──────────────────────────────────────────────────┤
│  ghostty-vt.dll (Zig GNU target)                 │
└──────────────────────────────────────────────────┘
```

### 4.2 Settings 경계 변경 (Source of Truth)

10인 합의 + 사용자 결정에 따라 SettingsManager는 **C# 측으로 이동**:

```text
기존: ghostwin_engine.dll 내 settings_manager.cpp (C++)
변경: GhostWin.exe 내 SettingsService.cs (C#)
     → System.Text.Json + ObservableObject + FileSystemWatcher
     → C#이 설정의 진실의 원천(Source of Truth) 역할을 수행
     → 엔진에는 P/Invoke로 파싱된 구조체를 전달 (gw_apply_config)
```

근거: JSON 파싱, Observer 패턴, 데이터 바인딩이 C#에서 압도적으로 생산적. 기존 Phase 5-C에서 C++로 구상했던 `SettingsManager`의 역할은 C# 기반 `SettingsService`로 대체되며, C++ 엔진은 전달받은 설정값(DTO)을 적용하는 수동적인 역할만 담당함.

### 4.3 Engine DLL C API 설계 (핵심)

```c
// ghostwin_engine.h — PoC 최소 API
// 주의: 모든 API는 내부적으로 C++ 예외를 catch하여 C#으로 전파되지 않게 방어해야 함

typedef void* GwEngine;
typedef uint32_t GwSessionId;

// Callbacks (문자열 마샬링 규약: UTF-16 윈도우 네이티브 우선)
typedef void (*GwOutputFn)(void* ctx, const uint8_t* data, uint32_t len);
typedef void (*GwTitleFn)(void* ctx, GwSessionId id, const wchar_t* title);

typedef struct {
    void* context;
    GwOutputFn on_output;
    GwTitleFn  on_title_changed;
} GwCallbacks;

// Lifecycle
GWAPI GwEngine gw_create(const GwCallbacks* callbacks);
GWAPI void     gw_destroy(GwEngine engine);

// Session
GWAPI GwSessionId gw_session_create(GwEngine engine, HWND render_hwnd,
                                     uint16_t cols, uint16_t rows);
GWAPI void gw_session_write(GwEngine engine, GwSessionId id,
                             const uint8_t* data, uint32_t len);
GWAPI void gw_session_resize(GwEngine engine, GwSessionId id,
                              uint16_t cols, uint16_t rows);

// Render
GWAPI void gw_render_frame(GwEngine engine);
GWAPI void gw_render_resize(GwEngine engine, uint32_t w, uint32_t h);

// TSF
GWAPI void gw_tsf_attach(GwEngine engine, HWND hidden_hwnd);
```

---

## 5. Implementation Plan (3-Day PoC)

### Day 1: Engine DLL + P/Invoke + 빌드 파이프라인

| # | 작업 | 예상 시간 | 산출물 |
|---|------|----------|--------|
| 1.1 | CMakeLists.txt에 `ghostwin_engine` SHARED 타깃 추가 | 1h | `ghostwin_engine.dll` + `.lib` |
| 1.2 | `ghostwin_engine.h/cpp` C API 래퍼 및 예외 방어막 작성 | 3h | Engine C API |
| 1.3 | WPF .csproj 프로젝트 생성 (.NET 9, wpf-ui NuGet) | 1h | `GhostWinPoC.csproj` |
| 1.4 | `NativeEngine.cs` P/Invoke 선언 (LibraryImport) | 2h | P/Invoke 레이어 |
| 1.5 | 빌드 파이프라인 스크립트 (CMake → dotnet build) | 1h | `scripts/build_wpf_poc.ps1` |

**V1 검증**: DLL 빌드 성공 + P/Invoke 호출 및 예외 방어 확인

### Day 2: HwndHost + ClearType + TSF

| # | 작업 | 예상 시간 | 산출물 |
|---|------|----------|--------|
| 2.1 | `TerminalHostControl` (HwndHost 서브클래스) 구현 | 3h | HWND 생성 + WPF 호스팅 |
| 2.2 | DX11 HWND SwapChain 연결 및 리사이즈 이벤트 동기화 | 2h | 터미널 렌더링 화면 |
| 2.3 | ClearType 시각 비교 (현행 vs PoC 스크린샷) | 1h | 비교 이미지 |
| 2.4 | `HwndSource`로 Hidden HWND + TSF attach | 2h | 한글 IME 동작 |

**V2 검증**: ClearType 시각적 동등 및 깜빡임 없음
**V4 검증**: 한글 조합/확정/백스페이스

### Day 3: 테마 + 벤치마크 + 종합 판정

| # | 작업 | 예상 시간 | 산출물 |
|---|------|----------|--------|
| 3.1 | wpf-ui FluentWindow + Mica + DarkMode 적용 | 2h | 테마 동작 확인 |
| 3.2 | NavigationView 및 오버레이 팝업(Airspace 우회) 테스트 | 1h | 네비게이션 및 팝업 확인 |
| 3.3 | P/Invoke 키입력 왕복 지연 측정 | 1h | 지연 수치 (μs) |
| 3.4 | 대량 출력 벤치마크 및 WPF UI 쓰로틀링 로직 적용 | 2h | 스루풋 비율 및 CPU 점유율 |
| 3.5 | 종합 판정 문서 작성 | 2h | Go/No-Go ADR |

**V3 검증**: 키입력 지연 < 1ms
**V5 검증**: wpf-ui 테마 및 Airspace 우회 팝업 동작
**V6 검증**: 스루풋 ≥ 90%

---

## 6. Go/No-Go Decision Matrix

| 시나리오 | V1 | V2 | V3 | V4 | V5 | V6 | 결정 |
|----------|:---:|:---:|:---:|:---:|:---:|:---:|------|
| 전부 통과 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **Go — WPF 전환 확정** |
| V5만 실패 | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | Go — wpf-ui 대신 자체 테마 (Airspace 문제 치명적일 경우) |
| V6만 실패 | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | Go — 성능 최적화 로드맵 추가 (쓰로틀링 강화) |
| V2 실패 | ✅ | ❌ | — | — | — | — | **No-Go — ClearType 불가** |
| V4 실패 | ✅ | ✅ | ✅ | ❌ | — | — | **No-Go — IME 불가** |
| V1 실패 | ❌ | — | — | — | — | — | **No-Go — DLL 빌드 및 예외 관리 불가** |

---

## 7. Risk Register

| 리스크 | 확률 | 영향 | 완화 |
|--------|------|------|------|
| HWND SwapChain에서 ClearType 품질 저하 | Low | Critical | Phase 3의 HWND SwapChain 코드가 이미 동작 확인됨 |
| C++ 예외의 C# 누수로 인한 런타임 크래시 | Med | Critical | C API 경계(gw_*)의 모든 진입점에 최상위 `try-catch(...)` 블록 적용 |
| HwndHost Airspace 문제 (오버레이 불가) | High | Med | 검색/팔레트는 별도 Popup Window로 구현하여 Z-order 강제 우위 확보 |
| 대량 출력 시 WPF Dispatcher 병목 | Med | High | C# `on_output` 콜백을 워커 스레드에서 처리하고, UI 갱신(스크롤 등)은 일정 주기로 쓰로틀링(Throttling) |
| .NET 런타임 배포 크기 (+30~60MB) | Certain | Low | Framework-dependent 모드 사용 |

---

## 8. Project Structure (PoC)

```text
ghostwin/
├── CMakeLists.txt              (기존 + ghostwin_engine SHARED 추가)
├── src/
│   ├── engine-api/             (신규 — C API 래퍼 및 예외 방어막)
│   │   ├── ghostwin_engine.h
│   │   └── ghostwin_engine.cpp
│   ├── renderer/               (기존 유지)
│   ├── vt-core/                (기존 유지)
│   ├── conpty/                 (기존 유지)
│   ├── tsf/                    (기존 유지)
│   ├── session/                (기존 유지)
│   └── settings/               (삭제 대상, SettingsService.cs로 이전)
│
├── wpf-poc/                    (신규 — WPF PoC 프로젝트)
│   ├── GhostWinPoC.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── Controls/
│   │   └── TerminalHostControl.cs  (HwndHost 서브클래스)
│   ├── Interop/
│   │   └── NativeEngine.cs         (P/Invoke 선언)
│   └── Pages/
│       └── DummySettingsPage.xaml   (wpf-ui NavigationView 테스트)
│
├── scripts/
│   ├── build_wpf_poc.ps1       (신규 — CMake + dotnet 2단계 빌드)
│   └── (기존 스크립트 유지)
│
└── external/
    ├── ghostty/                (기존 유지)
    └── winui/                  (기존 유지, PoC와 독립)
```

---

## 9. Brainstorming Log

| Phase | 질문 | 결정 | 근거 |
|-------|------|------|------|
| 1-Q1 | 핵심 목표 | 전환 가능성 검증 | 10인 합의 결과를 실증으로 확정 |
| 1-Q2 | 다음 단계 | PoC → Design 문서 | 체계적 PDCA 프로세스 유지 |
| 1-Q3 | 추가 검증 | 전부 포함 (5→6항목) | wpf-ui 테마 + 빌드 파이프라인 + 스루풋 벤치마크 |
| 2 | 접근 방식 | A. 단계적 3일 PoC | 실패 시 원인 격리 가능, 항목별 Go/No-Go |
| 3 | YAGNI | 6개 검증 항목 전부 포함 | 부분 검증은 의사결정 근거 불충분 |
| 4-1 | 아키텍처 | Settings는 C#으로 이동 | JSON + Observer + 바인딩이 C#에서 압도적으로 생산적 (C++ SettingsManager 폐기) |
| 4-2 | Go/No-Go 기준 | V1~V4 필수, V5~V6 권고 | ClearType/P/Invoke/TSF가 핵심 리스크 |

---

## 10. Dependencies & Prerequisites

- [ ] Zig 0.15.2 (ghostty-vt.dll 빌드)
- [ ] .NET 9 SDK 설치
- [ ] wpf-ui NuGet 패키지 (Wpf.Ui)
- [ ] CommunityToolkit.Mvvm NuGet 패키지
- [ ] Visual Studio 2026 (WPF 디자이너 + 디버거)

---

*Plan created by Plan Plus (Brainstorming-Enhanced PDCA Planning)*
