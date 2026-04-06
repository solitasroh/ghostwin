# WPF Migration Plan

> **Summary**: WinUI3 Code-only C++ UI를 WPF C# Clean Architecture로 전체 전환. Engine DLL은 유지하고 UI 레이어를 4프로젝트 솔루션(Core/Interop/Services/App)으로 재구축.
>
> **Project**: GhostWin Terminal
> **Phase**: WPF Migration (Phase 5 후속)
> **Author**: 노수장 (Refined by AI Agent)
> **Date**: 2026-04-06
> **Status**: Approved & Refined
> **Previous**: WPF Hybrid PoC — Go 판정 (V1~V6 전부 통과)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | WinUI3 Code-only C++로 10K+ UI 작성 시 생산성 2.5~3배 저하. `winui_app.cpp` 1,919줄 God Class가 유지보수 한계. Phase 5-E/F 구현 전에 아키텍처 전환이 필수. |
| **Solution** | 검증된 엔진 DLL(C API 18개)을 유지하고, UI를 WPF C# Clean Architecture(4프로젝트)로 전환. CommunityToolkit.Mvvm + MS DI + WPF-UI 스택으로 MVVM 기반 구조 확립. |
| **Function/UX Effect** | XAML 선언형 UI + Hot Reload로 개발 속도 3~5배 향상. 다중 탭/세션, 설정 핫 리로드, Fluent Design 타이틀바를 Clean Architecture에서 구현. WinUI3 의존성 완전 제거. |
| **Core Value** | PoC 실증(V1~V6 Pass)을 기반으로 한 안전한 전환. 테스트 가능한 구조로 버그 가능성 감소. Phase 6 AI 에이전트 기능을 수용할 수 있는 확장 가능한 기반 확보. |

---

## 1. User Intent Discovery

### 1.1 Core Purpose
**WinUI3 → WPF 전체 전환** — winui_app.cpp God Class를 제거하고, 수정에 용이하고 버그 가능성을 줄이는 Clean Architecture 기반 WPF 앱으로 재구축.

### 1.2 Target Users
- 프로젝트 개발자 (아키텍처 개선으로 개발 생산성 향상)
- 일상 터미널 사용자 (기능 동등성 유지)

### 1.3 Success Criteria
- [ ] WinUI3 앱과 동일한 기능이 WPF에서 동작 (탭, 세션, 설정, TSF, ClearType)
- [ ] WinUI3 코드 및 WinAppSDK 의존성 완전 제거
- [ ] 4프로젝트 Clean Architecture 구조 확립
- [ ] WPF 프레임워크 (CommunityToolkit.Mvvm + WPF-UI) 적용
- [ ] 엔진 C API 변경 최소화 (기존 18개 API 유지)

### 1.4 Constraints
- Engine DLL C API (`ghostwin_engine.h`) 변경 최소화
- .NET 10 + WPF-UI 4.x + CommunityToolkit.Mvvm 8.x 스택
- P/Invoke 성능 유지 (V3 기준: < 1ms)

---

## 2. Alternatives Explored

### Approach A: Clean Architecture 4프로젝트 — ✅ 채택
Core/Interop/Services/App 분리. Windows Terminal의 Core/Control/App 계층 + Files 앱의 DI 패턴 차용.
- **장점**: 테스트 용이, 수정에 강함, 의존성 방향 명확
- **단점**: 초기 구조 설정 비용

### Approach B: 단일 프로젝트 + 폴더 분리 — 기각
빠르게 시작 가능하나 규모 확장 시 God Class 재발 위험. Interop Mock 불가로 테스트 어려움.

### Approach C: Prism 모듈화 — 기각
Region Navigation은 강력하나 터미널 앱에는 과도한 추상화. Prism 의존성이 불필요한 복잡성 추가.

---

## 3. YAGNI Review

### In Scope (1차 마이그레이션)
- [x] 4프로젝트 솔루션 구조 + DI 기반 (M-1)
- [x] 엔진 P/Invoke 레이어 이전 + IEngineService (M-2)
- [x] 다중 세션/탭 관리 MVVM (M-3)
- [x] 설정 시스템 C# 이전 (M-4)
- [x] TitleBar 커스터마이징 (M-5)
- [x] WinUI3 코드/의존성 제거 (M-6)

### Out of Scope (2차 이후)
- Pane Split (Phase 5-E) — 1차 완료 후 WPF 기반으로 구현
- Session Restore (Phase 5-F) — Pane Split 이후
- Command Palette / Search Overlay
- 조합 미리보기 오버레이 (TSF preedit)
- Phase 6 AI 에이전트 기능 (Named Pipe, Notification Panel)
- WebView2 인앱 브라우저

---

## 4. Architecture

### 4.1 기술 스택

| 구성요소 | 선택 | 근거 |
|---------|------|------|
| 프레임워크 | .NET 10 + WPF | PoC 검증 완료, XAML 생산성 |
| MVVM | CommunityToolkit.Mvvm 8.x | Source Generator, 경량, MS 공식 |
| DI | Microsoft.Extensions.DependencyInjection | .NET 표준, CommunityToolkit 통합 |
| UI 테마 | WPF-UI (Wpf.Ui) 4.x | Fluent Design, Mica, 다크모드, PoC 검증 |
| JSON | System.Text.Json | .NET 내장, 추가 의존성 없음 |
| 메시징 | WeakReferenceMessenger | CommunityToolkit 내장, 메모리 안전 |

### 4.2 솔루션 구조

```text
GhostWin.sln
├── src/
│   ├── GhostWin.Core/              ← 순수 .NET 클래스 라이브러리 (UI 무관)
│   │   ├── Models/
│   │   │   ├── SessionInfo.cs       ← 세션 데이터 모델
│   │   │   ├── TerminalProfile.cs   ← 터미널 프로필
│   │   │   └── AppSettings.cs       ← 설정 데이터 모델 (ghostwin.json 매핑)
│   │   ├── Interfaces/
│   │   │   ├── IEngineService.cs    ← 엔진 조작 추상화
│   │   │   ├── ISessionManager.cs   ← 세션 생명주기 관리
│   │   │   └── ISettingsService.cs  ← 설정 로드/저장/감시
│   │   └── Events/
│   │       ├── SessionEvents.cs     ← Created, Closed, Activated
│   │       └── SettingsEvents.cs    ← SettingsChanged 메시지
│   │
│   ├── GhostWin.Interop/           ← P/Invoke 격리 프로젝트
│   │   ├── NativeEngine.cs          ← gw_engine_* P/Invoke 선언
│   │   ├── NativeCallbacks.cs       ← 콜백 델리게이트 + UnmanagedCallersOnly
│   │   ├── TsfBridge.cs             ← HwndSource + WM_USER+50 핸들링
│   │   └── EngineService.cs         ← IEngineService 구현 (P/Invoke 래핑)
│   │
│   ├── GhostWin.Services/          ← 비즈니스 로직 (UI 무관)
│   │   ├── SessionManager.cs        ← ISessionManager 구현
│   │   ├── SettingsService.cs       ← ISettingsService (JSON + FileWatcher)
│   │   ├── ThemeService.cs          ← 테마 전환 관리
│   │   └── KeyBindingService.cs     ← 키바인딩 매핑/디스패치
│   │
│   └── GhostWin.App/               ← WPF 앱 (실행 프로젝트)
│       ├── App.xaml / App.xaml.cs    ← DI 구성, 서비스 등록
│       ├── Controls/
│       │   └── TerminalHostControl.cs ← HwndHost (DX11 HWND SwapChain)
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs ← 탭 컬렉션, 앱 상태
│       │   ├── TerminalTabViewModel.cs ← 개별 탭/세션 상태
│       │   └── SettingsViewModel.cs   ← 설정 UI 바인딩
│       ├── Views/
│       │   ├── MainWindow.xaml        ← FluentWindow + 사이드바 + 콘텐츠
│       │   └── SettingsView.xaml      ← 설정 페이지
│       ├── Converters/
│       └── Resources/
│           └── Themes/                ← 10 builtin 테마 JSON
│
├── tests/
│   ├── GhostWin.Core.Tests/
│   └── GhostWin.Services.Tests/
│
├── src/engine-api/                  ← 기존 C++ 엔진 (변경 최소화)
│   ├── ghostwin_engine.h
│   └── ghostwin_engine.cpp
│
└── scripts/
    ├── build_ghostwin.ps1           ← 엔진 빌드
    ├── build_libghostty.ps1         ← Zig 빌드
    └── build_wpf.ps1               ← dotnet build (신규)
```

### 4.3 의존성 다이어그램

```text
GhostWin.App (.NET 10, WPF)
  ├── GhostWin.Services
  │     └── GhostWin.Core
  ├── GhostWin.Interop
  │     └── GhostWin.Core
  ├── WPF-UI (NuGet)
  └── CommunityToolkit.Mvvm (NuGet)

ghostwin_engine.dll (C++ Native)
  ├── DX11Renderer, ConPTY, VTCore, TSF, GlyphAtlas
  └── ghostty-vt.dll (Zig)
```

### 4.4 DI 구성 패턴

```text
App.OnStartup:
  ServiceCollection:
    Singleton: IEngineService    → EngineService (P/Invoke 래퍼)
    Singleton: ISessionManager   → SessionManager
    Singleton: ISettingsService   → SettingsService
    Singleton: MainWindowViewModel
    Transient: TerminalTabViewModel

  Ioc.Default.ConfigureServices(provider)
```

### 4.5 탭/세션 관리 데이터 흐름

```text
사용자 Ctrl+T
  → MainWindowViewModel.NewTabCommand
    → ISessionManager.CreateSession()
      → IEngineService.CreateSession(cols, rows)
        → gw_session_create() [P/Invoke]
    ← SessionCreated 이벤트 (WeakReferenceMessenger)
  → MainWindowViewModel.Tabs.Add(new TerminalTabViewModel)
  → TabSidebar UI 자동 갱신 (ObservableCollection)
```

### 4.6 설정 시스템 데이터 흐름

```text
ghostwin.json 파일 변경 감지
  → FileSystemWatcher.Changed
    → SettingsService.Reload()
      → System.Text.Json.Deserialize<AppSettings>()
      → WeakReferenceMessenger.Send(SettingsChangedMessage)
  → ThemeService.OnSettingsChanged() → 테마 적용
  → MainWindowViewModel.OnSettingsChanged() → UI 갱신
  → IEngineService.ApplyConfig() → gw_apply_config() [P/Invoke]
```

---

## 5. Sub-Feature Map (6단계)

```text
wpf-migration (Master)
├── M-1: solution-structure      [독립]     4프로젝트 + DI + 빈 FluentWindow
├── M-2: engine-interop          [M-1 이후] P/Invoke 이전 + IEngineService + 1화면 렌더링
├── M-3: session-tab-management  [M-2 이후] SessionManager + TabSidebar MVVM
├── M-4: settings-system         [M-1 이후] SettingsService + JSON + 핫 리로드
├── M-5: titlebar-customization  [M-3 이후] FluentWindow + WindowChrome
└── M-6: winui3-removal          [전체 이후] winui_app.cpp 삭제, WinAppSDK 제거
```

| ID | Feature | 의존성 | 예상 규모 | 완료 기준 |
|----|---------|--------|-----------|-----------|
| M-1 | solution-structure | 없음 | 소 | dotnet build 성공 + 빈 FluentWindow 표시 |
| M-2 | engine-interop | M-1 | 중 | 터미널 1화면 렌더링 + 키입력 동작 |
| M-3 | session-tab-management | M-2 | 대 | 다중 탭 독립 세션, Ctrl+T/W 동작 |
| M-4 | settings-system | M-1 | 중 | JSON 변경 → 실시간 테마/폰트 반영 |
| M-5 | titlebar-customization | M-3 | 소 | 커스텀 타이틀바 + 드래그 이동 + 최소/최대/닫기 |
| M-6 | winui3-removal | 전체 | 소 | WinUI3 없이 빌드 성공 |

### 구현 순서

```text
M-1: Solution Structure (기반)
    ↓
M-2: Engine Interop (렌더링)      M-4: Settings (독립, DI 제공)
    ↓
M-3: Session/Tab (다중 세션)
    ↓
M-5: TitleBar (외관 완성)
    ↓
M-6: WinUI3 Removal (정리)
```

---

## 6. Functional Requirements

### FR-01: 솔루션 구조 (M-1)
- 4프로젝트 (Core/Interop/Services/App) 생성 및 의존성 설정
- Microsoft.Extensions.DependencyInjection + CommunityToolkit.Mvvm + WPF-UI NuGet 등록
- App.xaml.cs에서 DI 컨테이너 구성
- 빈 FluentWindow (Mica + 다크모드) 표시

### FR-02: 엔진 Interop (M-2)
- wpf-poc/Interop/ 코드를 GhostWin.Interop으로 이전
- IEngineService 인터페이스 정의 (Core) + EngineService 구현 (Interop)
- TerminalHostControl (HwndHost) → IEngineService 연동
- TSF 한글 입력 (TsfBridge + WM_USER+50 + 포커스 타이머)
- 키보드 입력 → VT 시퀀스 변환 → session_write

### FR-03: 세션/탭 관리 (M-3)
- ISessionManager 인터페이스 (Core) + SessionManager 구현 (Services)
- MainWindowViewModel: ObservableCollection<TerminalTabViewModel>
- TabSidebar: ItemsControl + DataTemplate 바인딩
- Ctrl+T (새 탭), Ctrl+W (닫기), Ctrl+Tab (전환)
- 마지막 탭 닫으면 앱 종료
- 탭에 CWD 표시

### FR-04: 설정 시스템 (M-4)
- ISettingsService (Core) + SettingsService (Services)
- `%APPDATA%/GhostWin/ghostwin.json` 파싱 (System.Text.Json)
- FileSystemWatcher 기반 핫 리로드 (< 100ms)
- WeakReferenceMessenger로 변경 전파
- 10 builtin 테마 지원 (catppuccin-mocha, dracula 등)
- 문법 오류 시 fallback 유지 + 로깅

### FR-05: TitleBar (M-5)
- FluentWindow 기반 커스텀 타이틀바
- 드래그 이동, 더블클릭 최대화
- 최소화/최대화/닫기 버튼
- 앱 타이틀 + 활성 세션 정보 표시

### FR-06: WinUI3 제거 (M-6)
- `src/app/winui_app.cpp`, `src/app/winui_app.h` 삭제
- CMakeLists.txt에서 `ghostwin_winui` 타깃 제거
- WinAppSDK NuGet/패키지 참조 제거
- `external/winui/` 디렉토리 제거
- 빌드 검증: 엔진 DLL + WPF 앱만으로 빌드 성공

---

## 7. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 탭 전환 < 50ms | 시각 지연 없음 |
| NFR-02 | P/Invoke 왕복 < 1ms | V3 벤치마크 유지 |
| NFR-03 | 설정 리로드 < 100ms | JSON 변경 후 반영 시간 |
| NFR-04 | 메모리: 탭당 < 20MB 추가 | Task Manager 측정 |
| NFR-05 | 아키텍처 결합도 | Core 프로젝트가 WPF/UI 참조 없음 |
| NFR-06 | 대량 출력 스루풋 | V6 기준 유지 (프리징 없음) |

---

## 8. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| HwndHost Airspace 문제 (팝업 가려짐) | 중 | 높음 | Popup Window로 Z-order 강제 (PoC에서 확인됨) |
| WPF Dispatcher 병목 (대량 출력) | 높음 | 중 | PoC V6에서 프리징 없음 확인. 필요 시 쓰로틀링 |
| .NET 10 Preview 안정성 | 중 | 낮음 | .NET 9 fallback 가능 (WPF-UI 4.x 호환) |
| 4프로젝트 초기 설정 비용 | 낮음 | 확실 | M-1에서 1회만 발생, 이후 생산성 회수 |
| TSF 조합 미리보기 누락 | 낮음 | 확실 | 2차에서 구현 (1차는 확정 텍스트만) |

---

## 9. WPF 프레임워크 조사 결과 요약

### 채택 스택: WPF-UI + CommunityToolkit.Mvvm + MS DI

| 라이브러리 | NuGet 다운로드 | .NET 10 | 역할 | 채택 |
|-----------|:-------------:|:-------:|------|:----:|
| WPF-UI (Wpf.Ui) | 752K | ✅ | Fluent 테마/컨트롤 | ✅ |
| CommunityToolkit.Mvvm | 19.6M | ✅ | MVVM 인프라 | ✅ |
| MS.Extensions.DI | 표준 | ✅ | DI 컨테이너 | ✅ |
| jamesnet.wpf | 38K | 미확인 | 아키텍처 프레임워크 | ❌ (Prism 의존, 커뮤니티 소규모) |
| MaterialDesign | 10.5M | ✅ | Material 테마 | ❌ (터미널과 방향 불일치) |
| MahApps.Metro | 12.2M | 미확인 | Metro 테마 | ❌ (유지보수 모드) |

### 참고한 성공 사례

| 프로젝트 | 차용 패턴 |
|---------|----------|
| **Windows Terminal** | Core/Control/App 3계층 분리, 설정 JSON cascading |
| **Files App** | CommunityToolkit.Mvvm + MS DI + Ioc.Default, 다중 탭 구조 |
| **Fluent Terminal** | ViewModel 프로젝트 분리, TerminalViewModel 세션 관리 |
| **DevToys** | Core/Platform 분리, 서비스 인터페이스 설계 |

---

## 10. Brainstorming Log

| Phase | 질문 | 결정 | 근거 |
|-------|------|------|------|
| 1-Q1 | 핵심 목표 | 전체 전환 | God Class 제거, WinUI3 의존성 탈피 |
| 1-Q2 | 성공 기준 | 기능 동등성 + WinUI3 제거 + 프레임워크 적용 | 사용자 요청: jamesnet.wpf 등 조사 |
| 1-Q3 | 제약 | 엔진 API 변경 최소화 | 검증된 C API 18개 유지 |
| 2 | 접근 방식 | A: Clean Architecture 4프로젝트 | WT/Files 패턴 차용, 테스트 용이 |
| 3 | YAGNI | 6개 항목 전부 In Scope | Pane Split/Restore는 2차로 |
| 4-1 | 아키텍처 | Core/Interop/Services/App | WT 3계층 + DI 확장 |
| 4-2 | 구현 순서 | M-1→M-2→M-3→M-5→M-6 (M-4 병렬) | 의존성 기반 순서 |
| — | 프레임워크 | WPF-UI + CommunityToolkit.Mvvm | PoC 검증 + 커뮤니티 규모 + .NET 10 지원 |
| — | 구조 참고 | WT, Files, Fluent Terminal | base 구조화 목적, 성공 사례 기반 |

---

## 11. Dependencies & Prerequisites

- [x] .NET 10 SDK (설치 완료)
- [x] WPF-UI (Wpf.Ui) NuGet (PoC에서 확인)
- [x] CommunityToolkit.Mvvm NuGet (PoC에서 확인)
- [x] ghostwin_engine.dll 빌드 (PoC에서 확인)
- [x] Zig 0.15.2 (설치 완료)
- [x] MSVC 14.51 (설치 완료)
- [ ] Microsoft.Extensions.DependencyInjection NuGet (M-1에서 추가)

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-06 | Plan Plus brainstorming + 프레임워크 조사 | 노수장 |
