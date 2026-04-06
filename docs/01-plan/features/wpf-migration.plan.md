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

<!-- [수정근거] winui_app.cpp 실측 1,706줄 (기존 1,919는 오류), C API 실측 19개 (기존 18은 오류), 생산성 수치는 정량 근거 부재로 표현 완화 -->

| Perspective | Content |
|-------------|---------|
| **Problem** | WinUI3 Code-only C++로 대규모 UI 확장 시 XAML 대비 생산성 저하 (정량 측정 미실시, 체감 기준). `winui_app.cpp` 1,706줄 + 테스트 코드 포함 단일 파일이 유지보수 한계. Phase 5-E/F 구현 전에 아키텍처 전환이 필수. |
| **Solution** | 검증된 엔진 DLL(C API 19개, `ghostwin_engine.h` 기준)을 유지하고, UI를 WPF C# Clean Architecture(4프로젝트)로 전환. CommunityToolkit.Mvvm + MS DI + WPF-UI 스택으로 MVVM 기반 구조 확립. |
| **Function/UX Effect** | XAML 선언형 UI + Hot Reload로 개발 속도 향상 기대 (XAML vs Code-only 비교, 정량 벤치마크는 M-1 완료 후 측정). 다중 탭/세션, 설정 핫 리로드, Fluent Design 타이틀바를 Clean Architecture에서 구현. WinUI3 의존성 완전 제거. |
| **Core Value** | PoC 실증(V1~V6 Pass, 상세: `wpf-hybrid-poc.plan.md`)을 기반으로 한 안전한 전환. 테스트 가능한 구조로 버그 가능성 감소. Phase 6 AI 에이전트 기능을 수용할 수 있는 확장 가능한 기반 확보. |

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
- [ ] 엔진 C API 변경 최소화 (기존 19개 API 유지, `ghostwin_engine.h` 기준)
- [ ] 엔진 네이티브 콜백 (7종) WPF Dispatcher 마셜링 검증

### 1.4 Constraints
- Engine DLL C API (`ghostwin_engine.h`) 변경 최소화
- .NET 10 + WPF-UI 4.x + CommunityToolkit.Mvvm 8.x 스택
  <!-- [수정근거] PoC는 WPF-UI 3.x로 검증됨. M-1에서 4.x 호환성 재검증 필요 -->
  - ⚠️ PoC는 WPF-UI **3.x**로 검증 — 4.x 전환 시 breaking change 확인 필요 (M-1 게이트)
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

## 4. UI 디자인 철학 (cmux 계승)

> 출처: [cmux.com](https://cmux.com), [cmux.com/docs/configuration](https://cmux.com/docs/configuration), [github.com/manaflow-ai/cmux](https://github.com/manaflow-ai/cmux)

### 4.1 핵심 원칙: "Primitive, not a Solution"

cmux 공식: *"cmux is a primitive, not a solution. It gives you composable pieces and your workflow is up to you."*

GhostWin이 계승하는 원칙:
- **조합 가능한 빌딩 블록**: 터미널, 사이드바, 설정, 알림을 독립 서비스로 제공
- **워크플로 강제하지 않음**: 키바인딩/테마/레이아웃을 JSON 설정으로 완전 커스터마이징
- **네이티브 성능 우선**: Electron이 아닌 WPF + DX11 HwndHost 네이티브 렌더링
- **기존 설정 존중**: 엔진 터미널 설정과 앱 설정 분리

### 4.2 레이아웃 계층 (cmux 4단계 → GhostWin 적용)

| cmux | GhostWin WPF | 설명 |
|------|-------------|------|
| Window | MainWindow (FluentWindow) | Mica 배경 + 커스텀 타이틀바 |
| Workspace | TerminalTabViewModel | 사이드바 탭 항목 = 워크스페이스 |
| Pane | PaneViewModel (2차) | 수평/수직 분할 (Phase 5-E) |
| Surface | TerminalHostControl (HwndHost) | DX11 렌더링 영역 |

### 4.3 사이드바 정보 밀도 — Opt-out 원칙

cmux는 **기본 최대 정보 표시 + 개별 끄기(opt-out)** 방식:

| 정보 항목 | cmux 설정 | GhostWin 1차 | 2차+ |
|-----------|----------|:----------:|:----:|
| CWD (작업 디렉토리) | `sidebar.showBranchDirectory` | ✅ | — |
| Git 브랜치 | `sidebar.showBranchDirectory` | ✅ | — |
| PR 상태 | `sidebar.showPullRequests` | — | ✅ |
| 리스닝 포트 | `sidebar.showPorts` | — | ✅ |
| 알림 배지 | `sidebar.showNotificationMessage` | — | ✅ |
| 전체 숨김 | `sidebar.hideAllDetails` | ✅ | — |

### 4.4 4계층 알림 시스템 (Phase 6 대비 설계)

cmux의 알림 UX를 GhostWin Windows 환경으로 매핑:

| cmux 계층 | GhostWin 매핑 | 구현 시점 |
|-----------|-------------|----------|
| Pane Ring (파란 테두리) | WPF Border Glow Effect | 2차 |
| 사이드바 배지 (미읽음 도트) | TabItem Badge (DataTemplate) | 2차 |
| 알림 패널 (시간순 리스트) | WPF Flyout / Popup Panel | Phase 6 |
| 데스크톱 알림 | Win32 Toast Notification | Phase 6 |

**알림 억제 조건 (cmux 패턴 채택)**:
- GhostWin 윈도우가 포커스 상태 → Toast 미표시
- 알림을 보낸 탭이 현재 활성 → Toast 미표시
- 알림 패널이 열려있음 → Toast 미표시

### 4.5 색상/테마 철학

| 원칙 | cmux | GhostWin 적용 |
|------|------|-------------|
| 앱 테마 | system / light / dark | `app.appearance` 설정 (WPF-UI 지원) |
| 터미널 테마 | Ghostty 설정 상속 | `ghostwin.json`의 `terminal.colors.theme` |
| 탭별 색상 구분 | 16색 워크스페이스 색상 | 탭 accent color (2차) |
| Indicator 스타일 | 9가지 (leftRail, solidFill 등) | leftRail 기본, 설정으로 변경 가능 |
| 사이드바 배경 | `matchTerminalBackground` 옵션 | 사이드바 투명도/틴트 설정 |

### 4.6 UI/터미널 폰트 분리

| 영역 | cmux | GhostWin |
|------|------|---------|
| 앱 UI 크롬 | San Francisco (macOS 시스템) | Segoe UI Variable (Windows 11 시스템) |
| 터미널 렌더링 | 사용자 지정 모노스페이스 | 사용자 지정 (ghostwin.json `terminal.font`) |

### 4.7 키바인딩 철학

cmux 원칙을 Windows 환경으로 변환:

| cmux 원칙 | GhostWin 적용 |
|-----------|-------------|
| 모든 단축키 사용자 재정의 가능 | `keybindings` JSON 섹션 (Phase 5-D 구현 완료) |
| 2단계 코드 바인딩 (prefix) | KeyMap 시스템에 chord 지원 (2차) |
| 수정자 힌트 필(pill) 표시 | WPF Popup/Adorner로 구현 (2차) |
| 앱 키 / 터미널 키 분리 | ghostwin.json keybindings / 엔진 내부 VT 키 매핑 |

### 4.8 접근성 — 이중 신호 원칙

cmux의 **색상 + 형태 병행** 패턴 채택:
- 알림: **색상(파란 링)** + **형태(배지 도트)** 병행
- 활성 탭: **색상(하이라이트)** + **형태(indicator rail/border)** 병행
- 색약 사용자도 형태만으로 상태 구분 가능

---

## 5. Architecture (Clean Architecture)

<!-- [수정근거] 섹션 번호 4.x→5.x로 수정 (§4 UI 디자인 철학과 번호 충돌 해소) -->

### 5.1 기술 스택

| 구성요소 | 선택 | 근거 |
|---------|------|------|
| 프레임워크 | .NET 10 + WPF | PoC 검증 완료, XAML 생산성 |
| MVVM | CommunityToolkit.Mvvm 8.x | Source Generator, 경량, MS 공식 |
| DI | Microsoft.Extensions.DependencyInjection | .NET 표준, CommunityToolkit 통합 |
| UI 테마 | WPF-UI (Wpf.Ui) 4.x | Fluent Design, Mica, 다크모드. ⚠️ PoC는 3.x 검증 → M-1에서 4.x 재검증 |
| JSON | System.Text.Json | .NET 내장, 추가 의존성 없음 |
| 메시징 | WeakReferenceMessenger | CommunityToolkit 내장, 메모리 안전 |

### 5.2 솔루션 구조

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
│   │   ├── NativeEngine.cs          ← gw_engine_* P/Invoke 선언 (19개 API)
│   │   ├── NativeCallbacks.cs       ← 콜백 델리게이트 + Dispatcher 마셜링 (§5.7 참조)
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

### 5.3 의존성 다이어그램

<!-- [수정근거] Services→Interop 경로를 DI 주입으로 명시. 엔진 내장 settings 의존성 표기 추가 -->

```text
GhostWin.App (.NET 10, WPF)
  ├── GhostWin.Services
  │     └── GhostWin.Core (인터페이스만)
  ├── GhostWin.Interop
  │     └── GhostWin.Core (인터페이스만)
  ├── WPF-UI (NuGet)
  └── CommunityToolkit.Mvvm (NuGet)

  DI 조합: App에서 Interop 구현체를 Services에 주입
    SessionManager(IEngineService) ← EngineService 주입 (App.xaml.cs)

ghostwin_engine.dll (C++ Native)
  ├── DX11Renderer, ConPTY, VTCore, TSF, GlyphAtlas
  ├── settings (C++ 내장 — JSON 로드, FileWatcher, 테마)
  └── ghostty-vt.dll (Zig)
```

### 5.4 DI 구성 패턴

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

### 5.5 탭/세션 관리 데이터 흐름

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

### 5.6 설정 시스템 데이터 흐름

<!-- [수정근거] gw_apply_config()는 ghostwin_engine.h에 존재하지 않음. 실제 엔진 내부
     settings 라이브러리는 C++에서 직접 JSON을 로드하며, C API를 통한 설정 전달 경로가 없음.
     설정 이중화 전략을 명시: 엔진은 기존 C++ settings 유지, C#은 앱 UI 설정만 관리. -->

**설정 이중화 전략 (엔진 설정 vs 앱 설정)**

현재 엔진 DLL은 자체 `settings` 라이브러리(C++)를 내장하여 `ghostwin.json`을 직접 파싱합니다.
`gw_apply_config()` 같은 설정 전달 C API는 존재하지 않으므로, 다음 전략을 채택합니다:

| 설정 영역 | 관리 주체 | 동작 방식 |
|-----------|-----------|----------|
| 터미널 렌더링 (폰트, 색상, ClearType) | 엔진 C++ `settings` | 엔진이 직접 JSON 로드 + FileWatcher |
| 앱 UI (테마, 사이드바, 타이틀바) | C# `SettingsService` | WPF 앱이 JSON 로드 + FileSystemWatcher |
| 키바인딩 | C# `KeyBindingService` | WPF 앱이 JSON 로드, 엔진에는 VT 시퀀스만 전달 |

```text
ghostwin.json 파일 변경 감지 (이중 감시)

경로 A — 앱 UI 설정 (C#):
  → FileSystemWatcher.Changed
    → SettingsService.Reload()
      → System.Text.Json.Deserialize<AppSettings>()
      → WeakReferenceMessenger.Send(SettingsChangedMessage)
  → ThemeService.OnSettingsChanged() → WPF 테마 적용
  → MainWindowViewModel.OnSettingsChanged() → UI 갱신

경로 B — 터미널 렌더링 설정 (C++ 엔진 내부):
  → 엔진 내부 FileWatcher.Changed
    → SettingsManager::reload()
      → ISettingsObserver::on_settings_changed()
  → Renderer, GlyphAtlas, KeyMap 자동 갱신
```

> **향후 개선 (2차)**: 설정 통합 — 엔진에 `gw_apply_config()` C API를 추가하고
> C# 측에서 일원화 관리. 1차에서는 기존 이중 감시를 유지하여 리스크 최소화.

### 5.7 네이티브 콜백 스레드 안전 설계

<!-- [수정근거] PoC에서 콜백이 전혀 연결되지 않음 (GwCallbacks 함수 포인터 모두 nint.Zero).
     ghostwin_engine.h의 콜백은 I/O 스레드에서 호출되므로 WPF Dispatcher 마셜링 필수.
     GC가 델리게이트를 수거하지 않도록 핀닝도 필요. -->

엔진 콜백 7종 (`GwCallbacks` 구조체)은 **I/O 스레드 또는 렌더 스레드**에서 호출됩니다.
WPF에서는 UI 스레드가 아닌 스레드에서 UI를 수정할 수 없으므로, 다음 패턴을 적용합니다:

```text
NativeCallbacks.cs 설계:

1. 정적 메서드 + [UnmanagedCallersOnly] 어트리뷰트
   → P/Invoke 마셜링 비용 제거 (blittable 직접 호출)

2. 콜백 수신 → Dispatcher.BeginInvoke(DispatcherPriority.Normal)
   → UI 스레드에서 ViewModel/Service 업데이트

3. GCHandle 핀닝: App.OnStartup에서 콜백 컨텍스트를 GCHandle.Alloc
   → App.OnExit에서 Free (PoC에서 이미 App 인스턴스 핀닝 확인)

4. on_render_done 콜백: UI 스레드 마셜링 불필요 (렌더 상태만 업데이트)
   → Interlocked 또는 volatile flag로 처리
```

| 콜백 | 호출 스레드 | 마셜링 | 용도 |
|------|-----------|--------|------|
| `on_created` | I/O | Dispatcher | 탭 추가 UI 갱신 |
| `on_closed` | I/O | Dispatcher | 탭 제거 UI 갱신 |
| `on_activated` | caller | Dispatcher | 탭 전환 UI 갱신 |
| `on_title_changed` | I/O | Dispatcher | 탭 제목 갱신 |
| `on_cwd_changed` | I/O | Dispatcher | 사이드바 CWD 갱신 |
| `on_child_exit` | I/O | Dispatcher | 세션 종료 처리 |
| `on_render_done` | render | 불필요 | Interlocked flag |

---

## 6. Sub-Feature Map (6단계)

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

## 7. Functional Requirements

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
- `src/app/main_winui.cpp` 삭제
- `src/ui/tab_sidebar.cpp`, `src/ui/titlebar_manager.cpp` 삭제 (WPF MVVM으로 대체됨)
- CMakeLists.txt에서 `ghostwin_winui` 타깃 및 관련 조건부 블록 (line 184~237) 제거
- WinAppSDK NuGet/패키지 참조 제거
- `external/winui/` 디렉토리 제거 (존재하는 경우 — `.gitignore` 또는 `setup_winui.ps1`로 생성됨)
  <!-- [수정근거] 실제 파일시스템에 external/winui/ 미존재 확인. CMakeLists.txt의 조건부 처리로 이미 스킵됨 -->
- `resources.pri` 루트 파일 제거 (WinAppSDK 1.8 MRM fix용)
- 빌드 검증: 엔진 DLL + WPF 앱만으로 빌드 성공

---

## 8. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 탭 전환 < 50ms | 시각 지연 없음 |
| NFR-02 | P/Invoke 왕복 < 1ms | V3 벤치마크 유지 |
| NFR-03 | 설정 리로드 < 100ms | JSON 변경 후 반영 시간 |
| NFR-04 | 메모리: 탭당 < 20MB 추가 | Task Manager 측정 |
| NFR-05 | 아키텍처 결합도 | Core 프로젝트가 WPF/UI 참조 없음 |
| NFR-06 | 대량 출력 스루풋 | V6 기준 유지 (프리징 없음) |

---

## 9. Risks

<!-- [수정근거] Airspace PoC 증거 없음 → "미검증" 명시. 콜백/설정 이중화/WPF-UI 버전 리스크 추가 -->

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| HwndHost Airspace 문제 (팝업 가려짐) | 중 | 높음 | Popup Window로 Z-order 강제 — **M-2에서 검증 필요** (PoC 미검증) |
| WPF Dispatcher 병목 (대량 출력) | 높음 | 중 | PoC V6에서 프리징 없음 확인. 필요 시 쓰로틀링 |
| .NET 10 Preview 안정성 | 중 | 중 | .NET 9 fallback 가능. 단, WPF-UI 4.x가 .NET 9 호환인지 확인 필요 |
| 4프로젝트 초기 설정 비용 | 낮음 | 확실 | M-1에서 1회만 발생, 이후 생산성 회수 |
| TSF 조합 미리보기 누락 | 낮음 | 확실 | 2차에서 구현 (1차는 확정 텍스트만) |
| 네이티브 콜백 스레드 안전성 | 높음 | 중 | PoC에서 콜백 미연결 상태. M-2에서 Dispatcher 마셜링 구현+검증 (§5.7) |
| WPF-UI 3.x→4.x breaking change | 중 | 중 | PoC는 3.x 검증. M-1에서 4.x 업그레이드 후 FluentWindow/Mica 동작 재확인 |
| 설정 이중 감시 경합 | 낮음 | 중 | C++/C# 양쪽 FileWatcher가 동일 JSON 감시 → 순서 보장 안됨. debounce로 완화 |
| CMake+MSBuild 빌드 통합 | 중 | 높음 | 엔진(CMake)과 WPF(dotnet) 빌드 순서/DLL 복사 자동화 필요 (§13.1) |

---

## 10. WPF 프레임워크 조사 결과 요약

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

## 11. Brainstorming Log

| Phase | 질문 | 결정 | 근거 |
|-------|------|------|------|
| 1-Q1 | 핵심 목표 | 전체 전환 | God Class 제거, WinUI3 의존성 탈피 |
| 1-Q2 | 성공 기준 | 기능 동등성 + WinUI3 제거 + 프레임워크 적용 | 사용자 요청: jamesnet.wpf 등 조사 |
| 1-Q3 | 제약 | 엔진 API 변경 최소화 | 검증된 C API 19개 유지 (`ghostwin_engine.h` 실측) |
| 2 | 접근 방식 | A: Clean Architecture 4프로젝트 | WT/Files 패턴 차용, 테스트 용이 |
| 3 | YAGNI | 6개 항목 전부 In Scope | Pane Split/Restore는 2차로 |
| 4-1 | 아키텍처 | Core/Interop/Services/App | WT 3계층 + DI 확장 |
| 4-2 | 구현 순서 | M-1→M-2→M-3→M-5→M-6 (M-4 병렬) | 의존성 기반 순서 |
| — | 프레임워크 | WPF-UI + CommunityToolkit.Mvvm | PoC 검증 + 커뮤니티 규모 + .NET 10 지원 |
| — | 구조 참고 | WT, Files, Fluent Terminal | base 구조화 목적, 성공 사례 기반 |

---

## 12. Dependencies & Prerequisites

- [x] .NET 10 SDK (설치 완료)
- [x] WPF-UI (Wpf.Ui) NuGet (PoC에서 3.x 확인)
- [ ] WPF-UI 4.x 호환성 확인 (M-1 게이트)
- [x] CommunityToolkit.Mvvm NuGet (PoC에서 확인)
- [x] ghostwin_engine.dll 빌드 (PoC에서 확인)
- [x] Zig 0.15.2 (설치 완료)
- [x] MSVC 14.51 (설치 완료)
- [ ] Microsoft.Extensions.DependencyInjection NuGet (M-1에서 추가)
- [ ] 네이티브 콜백 연결 PoC (M-2 전제조건 — PoC에서 미구현)

---

## 13. 빌드 통합, 일정, 롤백 전략

<!-- [수정근거] 분석에서 누락 항목으로 식별: 빌드 통합, 일정/공수, 롤백, 병행 운용, 성능 회귀 -->

### 13.1 빌드 통합 (CMake + dotnet)

엔진 DLL(CMake)과 WPF 앱(dotnet)은 별도 빌드 시스템을 사용합니다.

```text
build_all.ps1 (신규):
  1. scripts/build_libghostty.ps1    ← Zig 빌드 (변경 시)
  2. scripts/build_ghostwin.ps1      ← CMake 빌드 → ghostwin_engine.dll
  3. Copy ghostwin_engine.dll + ghostty-vt.dll → src/GhostWin.App/lib/
  4. dotnet build src/GhostWin.App/  ← WPF 빌드
```

### 13.2 병행 운용 전략

M-1~M-5 기간 동안 WinUI3 앱과 WPF 앱이 공존합니다:
- 기존 `ghostwin_winui` 타깃은 유지 (CMakeLists.txt 변경 없음)
- WPF 앱은 별도 솔루션 (`GhostWin.sln`)으로 독립 빌드
- M-6에서만 WinUI3 코드 제거 — **기능 동등성 확인 후**

### 13.3 성능 회귀 테스트

PoC V3/V6 벤치마크를 WPF 앱에서 재실행:
- M-2 완료 후: V3 (P/Invoke < 1ms), V6 (1MB 스루풋 프리징 없음)
- 자동화: `scripts/run_benchmarks.ps1` (신규)

### 13.4 예상 일정

| 단계 | 예상 공수 | 비고 |
|------|----------|------|
| M-1 솔루션 구조 | 2~3일 | WPF-UI 4.x 호환성 확인 포함 |
| M-2 엔진 Interop | 5~7일 | 콜백 구현 + Airspace 검증이 핵심 |
| M-3 세션/탭 관리 | 5~7일 | MVVM 바인딩 + 사이드바 |
| M-4 설정 시스템 | 3~5일 | M-2와 병렬 가능 |
| M-5 타이틀바 | 2~3일 | FluentWindow 기본 지원 |
| M-6 WinUI3 제거 | 1~2일 | 기능 동등성 확인 후 |
| **합계** | **18~27일** | 1인 기준, 버퍼 포함 |

### 13.5 롤백 전략

- M-1~M-5: WinUI3 앱이 유지되므로 롤백 비용 없음
- M-6 실행 전: Git tag `pre-winui3-removal` 생성
- M-6 실행 후 문제 발생: tag에서 WinUI3 코드 복원

### 13.6 디버깅 워크플로

- Visual Studio **Mixed Mode Debugging** 활용: C# WPF + C++ 엔진 DLL 동시 디버깅
- `ghostwin_engine.dll`에 PDB 포함 빌드 (CMake: `RelWithDebInfo`)
- WPF 앱에서 `[DllImport]` 호출 시 native exception을 `SEHException`으로 catch 가능

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-06 | Plan Plus brainstorming + 프레임워크 조사 | 노수장 |
| 0.2 | 2026-04-06 | 코드베이스 대조 분석 결과 반영 — 사실 오류 수정, 콜백 설계 추가, 설정 이중화 전략, 빌드/일정/롤백 섹션 추가 | AI Agent |
