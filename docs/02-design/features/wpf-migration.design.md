# WPF Migration Design Document

> **Summary**: WinUI3 Code-only C++ UI를 WPF C# Clean Architecture(4프로젝트)로 전체 전환하는 상세 구현 명세. Engine DLL(C API 19개)을 유지하고 UI 레이어를 Core/Interop/Services/App으로 재구축.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-06
> **Status**: Draft
> **Planning Doc**: [wpf-migration.plan.md](../../01-plan/features/wpf-migration.plan.md)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | WinUI3 Code-only C++의 `winui_app.cpp` 1,706줄 God Class가 유지보수 한계. Phase 5-E/F 구현 전 아키텍처 전환 필수. |
| **Solution** | 검증된 Engine DLL(C API 19개, 콜백 7종)을 유지하고, UI를 WPF Clean Architecture 4프로젝트로 전환. CommunityToolkit.Mvvm + MS DI + WPF-UI 스택으로 MVVM 구조 확립. |
| **Function/UX Effect** | XAML 선언형 UI + Hot Reload 개발, 다중 탭/세션 MVVM, 설정 핫 리로드, Fluent Design 타이틀바. WinUI3 의존성 완전 제거. |
| **Core Value** | PoC 실증(V1~V6 Pass) 기반 안전한 전환. 테스트 가능한 Clean Architecture로 Phase 6 AI 에이전트 기능까지 수용하는 확장 기반 확보. |

---

## 1. Overview

### 1.1 Design Goals

1. **4프로젝트 Clean Architecture**: Core(인터페이스) → Interop(P/Invoke) → Services(비즈니스) → App(WPF UI) 의존성 방향 확립
2. **엔진 C API 무변경**: `ghostwin_engine.h` 19개 API + `GwCallbacks` 7종 콜백 그대로 유지
3. **콜백 완전 연결**: PoC에서 `nint.Zero`였던 7종 콜백을 `[UnmanagedCallersOnly]` + Dispatcher 마셜링으로 구현
4. **설정 이중화**: 엔진 C++ settings(터미널 렌더링) + C# SettingsService(앱 UI) 병행 — `gw_apply_config()` API 없음
5. **cmux 디자인 원칙 계승**: 4계층 레이아웃, opt-out 사이드바, 이중 신호 접근성, 폰트 분리

### 1.2 Design Principles

- **PoC 코드 활용**: `wpf-poc/` 코드를 4프로젝트에 분산 이전 (재작성 아님)
- **Blittable 타입만**: P/Invoke 마셜링 비용 0 유지 (`void*`, `uint32_t`, `wchar_t*`, 함수 포인터)
- **의존성 역전**: Services는 Core 인터페이스만 참조, Interop 구현체는 App에서 DI 주입
- **UI 스레드 안전**: 네이티브 콜백 → Dispatcher.BeginInvoke 필수 (on_render_done 제외)

### 1.3 Plan과의 차이점

| Plan 기술 | Design 결정 | 변경 근거 |
|-----------|------------|-----------|
| WPF-UI 4.x 가정 | M-1에서 4.x 게이트 평가 → 실패 시 3.x 유지 | PoC는 3.x 검증만 완료. 4.x breaking change 미확인 |
| `SettingsViewModel` (App 프로젝트) | 1차는 설정 UI 미구현, SettingsService만 | YAGNI — 설정 UI는 2차에서 구현 |
| `ThemeService` 독립 서비스 | SettingsService 내부 메서드로 통합 | 1차에서 테마 전환은 단순 ResourceDictionary 교체만 |
| `KeyBindingService` 독립 서비스 | SettingsService.KeyBindings 속성으로 통합 | 1차에서 키바인딩은 단순 Dictionary 조회만 |
| 10 builtin 테마 JSON (Resources/Themes/) | 엔진 C++ settings가 테마 관리 → C#은 앱 테마(Light/Dark)만 | 터미널 색상 테마는 엔진 내장. C#이 중복 관리할 필요 없음 |

---

## 2. Architecture

### 2.1 솔루션 구조

```text
GhostWin.sln
├── src/
│   ├── GhostWin.Core/              ← 순수 .NET 클래스 라이브러리 (UI 무관)
│   │   ├── Models/
│   │   │   ├── SessionInfo.cs       ← 세션 데이터 모델
│   │   │   └── AppSettings.cs       ← ghostwin.json 앱 UI 섹션 매핑
│   │   ├── Interfaces/
│   │   │   ├── IEngineService.cs    ← 엔진 조작 추상화 (19개 API)
│   │   │   ├── ISessionManager.cs   ← 세션 생명주기 관리
│   │   │   └── ISettingsService.cs  ← 설정 로드/저장/감시
│   │   └── Events/
│   │       ├── SessionEvents.cs     ← Created, Closed, Activated, TitleChanged, CwdChanged
│   │       └── SettingsEvents.cs    ← SettingsChanged 메시지
│   │
│   ├── GhostWin.Interop/           ← P/Invoke 격리 프로젝트
│   │   ├── NativeEngine.cs          ← 19개 API P/Invoke 선언 (LibraryImport)
│   │   ├── NativeCallbacks.cs       ← [UnmanagedCallersOnly] 7종 + Dispatcher 마셜링
│   │   ├── GwCallbacks.cs           ← 콜백 구조체 (LayoutKind.Sequential)
│   │   ├── TsfBridge.cs             ← HwndSource + WM_USER+50 핸들링
│   │   └── EngineService.cs         ← IEngineService 구현 (P/Invoke 래핑)
│   │
│   ├── GhostWin.Services/          ← 비즈니스 로직 (UI 무관)
│   │   ├── SessionManager.cs        ← ISessionManager 구현
│   │   └── SettingsService.cs       ← ISettingsService (JSON + FileSystemWatcher)
│   │
│   └── GhostWin.App/               ← WPF 앱 (실행 프로젝트)
│       ├── App.xaml / App.xaml.cs    ← DI 구성, 서비스 등록
│       ├── Controls/
│       │   └── TerminalHostControl.cs ← HwndHost (DX11 HWND SwapChain)
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs ← 탭 컬렉션, 앱 상태, 커맨드
│       │   └── TerminalTabViewModel.cs ← 개별 탭/세션 상태
│       ├── Views/
│       │   └── MainWindow.xaml        ← FluentWindow + 사이드바 + 콘텐츠
│       ├── Converters/
│       │   └── BoolToVisibilityConverter.cs
│       └── Resources/
│           └── Styles.xaml            ← 앱 전용 스타일 오버라이드
│
├── src/engine-api/                  ← 기존 C++ 엔진 (변경 없음)
│   ├── ghostwin_engine.h
│   └── ghostwin_engine.cpp
│
└── scripts/
    ├── build_ghostwin.ps1           ← 엔진 빌드 (기존)
    ├── build_libghostty.ps1         ← Zig 빌드 (기존)
    └── build_all.ps1                ← 통합 빌드 (신규)
```

### 2.2 의존성 다이어그램

```text
GhostWin.App (.NET 10, WPF)
  ├──► GhostWin.Services
  │      └──► GhostWin.Core (인터페이스만)
  ├──► GhostWin.Interop
  │      └──► GhostWin.Core (인터페이스만)
  ├──► GhostWin.Core
  ├──► WPF-UI (NuGet)
  └──► CommunityToolkit.Mvvm (NuGet)

GhostWin.Services
  └──► GhostWin.Core (인터페이스만)
  (Interop 직접 참조 없음 — DI를 통해 IEngineService 주입)

ghostwin_engine.dll (C++ Native, 변경 없음)
  ├── DX11Renderer, ConPTY, VTCore, TSF, GlyphAtlas
  ├── settings (C++ 내장 — JSON 로드, FileWatcher, 테마)
  └── ghostty-vt.dll (Zig)
```

### 2.3 데이터 흐름: 탭/세션 생성

```text
사용자 Ctrl+T
  → MainWindowViewModel.NewTabCommand (ICommand)
    → ISessionManager.CreateSession()
      → IEngineService.CreateSession(cols, rows)
        → NativeEngine.gw_session_create() [P/Invoke]
    ← SessionCreated 이벤트 (WeakReferenceMessenger)
  → MainWindowViewModel: Tabs.Add(new TerminalTabViewModel)
  → TabSidebar: ItemsControl 자동 갱신 (ObservableCollection 바인딩)
```

### 2.4 데이터 흐름: 설정 변경

```text
ghostwin.json 파일 변경 (이중 감시)

경로 A — 앱 UI 설정 (C# SettingsService):
  → FileSystemWatcher.Changed
    → SettingsService.Reload() [debounce 200ms]
      → System.Text.Json.Deserialize<AppSettings>()
      → WeakReferenceMessenger.Send(SettingsChangedMessage)
  → MainWindowViewModel.OnSettingsChanged() → 사이드바/테마 갱신

경로 B — 터미널 렌더링 설정 (C++ 엔진 내부):
  → 엔진 내부 FileWatcher.Changed
    → SettingsManager::reload()
      → ISettingsObserver::on_settings_changed()
  → Renderer, GlyphAtlas, KeyMap 자동 갱신
```

---

## 3. Sub-Feature 상세 설계

### 3.1 M-1: Solution Structure

**목표**: 4프로젝트 솔루션 생성, DI 구성, 빈 FluentWindow 표시

#### 3.1.1 .csproj 파일 구성

**GhostWin.Core.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
  </ItemGroup>
</Project>
```

- UI 프레임워크 의존성 없음 (net10.0, UseWPF 없음)
- CommunityToolkit.Mvvm: `ObservableObject`, `WeakReferenceMessenger` 사용을 위해 포함

**GhostWin.Interop.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\GhostWin.Core\GhostWin.Core.csproj" />
  </ItemGroup>
</Project>
```

- `UseWPF=true`: TsfBridge에서 `HwndSource`, `Dispatcher` 사용
- `AllowUnsafeBlocks`: P/Invoke `fixed` 블록

**GhostWin.Services.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\GhostWin.Core\GhostWin.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions"
                      Version="10.*" />
  </ItemGroup>
</Project>
```

- UI 프레임워크 의존성 없음
- DI.Abstractions만 참조 (런타임 DI 컨테이너는 App에서)

**GhostWin.App.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\GhostWin.Core\GhostWin.Core.csproj" />
    <ProjectReference Include="..\GhostWin.Interop\GhostWin.Interop.csproj" />
    <ProjectReference Include="..\GhostWin.Services\GhostWin.Services.csproj" />
    <PackageReference Include="WPF-UI" Version="3.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
  </ItemGroup>
</Project>
```

- WPF-UI 버전: M-1 게이트에서 4.x 평가 후 결정. 실패 시 3.x 유지
- 네이티브 DLL 복사: `build_all.ps1`에서 처리 (csproj 내 Copy 불필요)

#### 3.1.2 DI 등록 코드 (App.xaml.cs)

```csharp
public partial class App : Application
{
    private GCHandle _pinHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF-UI 다크 테마
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        // DI 컨테이너 구성
        var services = new ServiceCollection();

        // Core → Interop 구현체
        services.AddSingleton<IEngineService, EngineService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<TerminalTabViewModel>();

        // CommunityToolkit DI 통합
        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // GCHandle 핀닝 (네이티브 콜백용)
        _pinHandle = GCHandle.Alloc(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 엔진 정리는 EngineService.Dispose()가 담당
        var engine = Ioc.Default.GetService<IEngineService>();
        (engine as IDisposable)?.Dispose();

        if (_pinHandle.IsAllocated) _pinHandle.Free();
        base.OnExit(e);
    }
}
```

#### 3.1.3 WPF-UI 4.x 게이트 체크리스트

M-1 첫 단계에서 4.x로 업그레이드 시도 후 다음 항목을 검증:

| 항목 | 검증 방법 | Fallback |
|------|-----------|----------|
| `FluentWindow` 존재 여부 | 컴파일 | 3.x의 `FluentWindow` 유지 |
| `WindowBackdropType.Mica` | 런타임 Mica 동작 | `SystemBackdrop` API 변경 확인 |
| `ExtendsContentIntoTitleBar` | 타이틀바 확장 동작 | WindowChrome 직접 사용 |
| `ThemesDictionary` / `ControlsDictionary` | XAML 리소스 로드 | 네임스페이스 변경 확인 |
| `.NET 10 호환` | `dotnet build` 성공 | .NET 9 fallback |

**판정**: 3개 이상 실패 시 3.x 유지. 1~2개 실패 시 해당 기능만 우회.

---

### 3.2 M-2: Engine Interop

**목표**: P/Invoke 이전, IEngineService 구현, 7종 콜백 완전 연결, 터미널 1화면 렌더링

#### 3.2.1 IEngineService 인터페이스 (Core 프로젝트)

```csharp
namespace GhostWin.Core.Interfaces;

/// <summary>
/// 엔진 DLL(ghostwin_engine.dll)의 C API를 추상화.
/// 구현체: GhostWin.Interop.EngineService
/// </summary>
public interface IEngineService : IDisposable
{
    // ── Lifecycle ──
    bool IsInitialized { get; }
    void Initialize(GwCallbackContext callbackContext);
    void Shutdown();

    // ── Render ──
    int RenderInit(nint hwnd, uint widthPx, uint heightPx,
                   float fontSizePt, string fontFamily);
    int RenderResize(uint widthPx, uint heightPx);
    int RenderSetClearColor(uint rgb);
    int RenderStart();
    void RenderStop();

    // ── Session ──
    uint CreateSession(string? shellPath, string? initialDir,
                       ushort cols, ushort rows);
    int CloseSession(uint sessionId);
    void ActivateSession(uint sessionId);
    int WriteSession(uint sessionId, ReadOnlySpan<byte> data);
    int ResizeSession(uint sessionId, ushort cols, ushort rows);

    // ── TSF ──
    int TsfAttach(nint hiddenHwnd);
    int TsfFocus(uint sessionId);
    int TsfUnfocus();
    int TsfSendPending();

    // ── Query ──
    uint SessionCount { get; }
    uint ActiveSessionId { get; }
    void PollTitles();
}
```

`GwCallbackContext`는 콜백 수신을 위한 컨텍스트 객체:

```csharp
namespace GhostWin.Core.Interfaces;

/// <summary>
/// 네이티브 콜백의 UI 스레드 디스패처.
/// EngineService가 콜백 수신 시 이 컨텍스트를 통해 이벤트를 발행.
/// </summary>
public class GwCallbackContext
{
    public required Action<uint> OnSessionCreated { get; init; }
    public required Action<uint> OnSessionClosed { get; init; }
    public required Action<uint> OnSessionActivated { get; init; }
    public required Action<uint, string> OnTitleChanged { get; init; }
    public required Action<uint, string> OnCwdChanged { get; init; }
    public required Action<uint, uint> OnChildExit { get; init; }
    public Action? OnRenderDone { get; init; }
}
```

#### 3.2.2 NativeEngine.cs (Interop 프로젝트)

PoC `wpf-poc/Interop/NativeEngine.cs`에서 그대로 이전. `LibraryImport` 소스 생성기 기반 19개 API 선언.

변경점: 네임스페이스 `GhostWinPoC.Interop` → `GhostWin.Interop`

#### 3.2.3 NativeCallbacks.cs — 콜백 마셜링 상세 설계

PoC에서 `nint.Zero`였던 콜백을 완전 구현:

```csharp
namespace GhostWin.Interop;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

/// <summary>
/// 엔진 네이티브 콜백 7종의 수신부.
/// [UnmanagedCallersOnly]로 마셜링 비용 제거.
/// I/O 스레드 → Dispatcher.BeginInvoke로 UI 스레드 마셜링.
/// </summary>
internal static class NativeCallbacks
{
    // GCHandle에서 복원할 콜백 컨텍스트
    private static GwCallbackContext? _context;
    private static Dispatcher? _dispatcher;

    /// <summary>
    /// App.OnStartup에서 호출. 콜백 수신 전 반드시 초기화 필요.
    /// </summary>
    internal static void Initialize(GwCallbackContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    // ── 정적 콜백 메서드 (blittable, 네이티브에서 직접 호출) ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnCreated(nint ctx, uint sessionId)
    {
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnSessionCreated(sessionId));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnClosed(nint ctx, uint sessionId)
    {
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnSessionClosed(sessionId));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnActivated(nint ctx, uint sessionId)
    {
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnSessionActivated(sessionId));
    }

    // [Review Fix] len 단위 확인: ghostwin_engine.cpp L87에서
    // static_cast<uint32_t>(title.size()) — wchar_t 문자 수 (바이트 수 아님)
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnTitleChanged(nint ctx, uint sessionId,
                                                nint titlePtr, uint len)
    {
        // len = wchar_t 문자 수 (not bytes). ghostwin_engine.cpp: title.size()
        var title = new string((char*)titlePtr, 0, (int)len);
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnTitleChanged(sessionId, title));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnCwdChanged(nint ctx, uint sessionId,
                                              nint cwdPtr, uint len)
    {
        // len = wchar_t 문자 수 (not bytes). ghostwin_engine.cpp: cwd.size()
        var cwd = new string((char*)cwdPtr, 0, (int)len);
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnCwdChanged(sessionId, cwd));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnChildExit(nint ctx, uint sessionId, uint exitCode)
    {
        var context = _context;
        var disp = _dispatcher;
        if (context == null || disp == null) return;
        disp.BeginInvoke(() => context.OnChildExit(sessionId, exitCode));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnRenderDone(nint ctx)
    {
        // 렌더 스레드 — UI 마셜링 불필요
        // 필요 시 Interlocked flag 업데이트
        _context?.OnRenderDone?.Invoke();
    }
}
```

#### 3.2.4 GwCallbacks 구조체 + 함수 포인터 등록

```csharp
namespace GhostWin.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct GwCallbacks
{
    public nint Context;
    public nint OnCreated;       // GwSessionFn
    public nint OnClosed;        // GwSessionFn
    public nint OnActivated;     // GwSessionFn
    public nint OnTitleChanged;  // GwTitleFn
    public nint OnCwdChanged;    // GwCwdFn
    public nint OnChildExit;     // GwExitFn
    public nint OnRenderDone;    // GwRenderDoneFn
}
```

EngineService에서 함수 포인터 등록:

```csharp
// EngineService.Initialize() 내부
unsafe
{
    var callbacks = new GwCallbacks
    {
        Context     = GCHandle.ToIntPtr(_pinHandle),
        OnCreated   = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                      &NativeCallbacks.OnCreated,
        OnClosed    = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                      &NativeCallbacks.OnClosed,
        OnActivated = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                      &NativeCallbacks.OnActivated,
        OnTitleChanged = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)
                         &NativeCallbacks.OnTitleChanged,
        OnCwdChanged   = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)
                         &NativeCallbacks.OnCwdChanged,
        OnChildExit    = (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, void>)
                         &NativeCallbacks.OnChildExit,
        OnRenderDone   = (nint)(delegate* unmanaged[Cdecl]<nint, void>)
                         &NativeCallbacks.OnRenderDone,
    };
    _engineHandle = NativeEngine.gw_engine_create(in callbacks);
}
```

#### 3.2.5 콜백 스레드 안전 매트릭스

| 콜백 | 호출 스레드 | 마셜링 방식 | UI 동작 |
|------|-----------|------------|---------|
| `on_created` | I/O | `Dispatcher.BeginInvoke` | 탭 추가 |
| `on_closed` | I/O | `Dispatcher.BeginInvoke` | 탭 제거 |
| `on_activated` | caller | `Dispatcher.BeginInvoke` | 탭 전환 하이라이트 |
| `on_title_changed` | I/O | `Dispatcher.BeginInvoke` | 탭 제목 + 타이틀바 갱신 |
| `on_cwd_changed` | I/O | `Dispatcher.BeginInvoke` | 사이드바 CWD 텍스트 갱신 |
| `on_child_exit` | I/O | `Dispatcher.BeginInvoke` | 세션 종료 → 탭 제거/앱 종료 |
| `on_render_done` | render | 없음 (Interlocked) | 프레임 카운터 갱신 (디버그) |

#### 3.2.6 TerminalHostControl (App 프로젝트)

PoC `wpf-poc/Controls/TerminalHostControl.cs`에서 이전. 변경점:

- 네임스페이스: `GhostWinPoC.Controls` → `GhostWin.App.Controls`
- `RenderResizeRequested` 이벤트 → ViewModel의 `ICommand` 바인딩으로 전환 (M-3에서)
- DPI 처리 로직 유지 (`VisualTreeHelper.GetDpi`)

#### 3.2.7 TsfBridge (Interop 프로젝트)

PoC `wpf-poc/Interop/TsfBridge.cs`에서 이전. 변경점:

- 네임스페이스: `GhostWinPoC.Interop` → `GhostWin.Interop`
- `NativeEngine` **직접 호출 유지** (IEngineService 경유하지 않음)
  <!-- [Review Fix] TsfBridge는 Interop 프로젝트 내부이므로 NativeEngine 직접 호출이 자연스러움.
       HwndSource 생명주기와 DI 생명주기가 달라 IEngineService 주입이 복잡해짐. -->
- ADR-011 50ms 포커스 타이머 패턴 유지

#### 3.2.8 M-2 완료 기준

- 터미널 1화면 렌더링 동작 (DX11 SwapChain + ClearType)
- 키보드 입력 → VT 시퀀스 → session_write 동작
- 한글 TSF 입력 동작 (확정 텍스트)
- 7종 콜백 모두 연결 확인 (로그 출력)
- V3 벤치마크 < 1ms 유지

---

### 3.3 M-3: Session/Tab Management

**목표**: SessionManager MVVM, 다중 탭 사이드바, Ctrl+T/W/Tab 단축키

#### 3.3.1 ISessionManager 인터페이스 (Core 프로젝트)

```csharp
namespace GhostWin.Core.Interfaces;

/// <summary>
/// 세션 생명주기 관리. IEngineService를 DI로 주입받아 사용.
/// 구현체: GhostWin.Services.SessionManager
/// </summary>
public interface ISessionManager
{
    /// <summary>현재 활성 세션 ID. 없으면 null.</summary>
    uint? ActiveSessionId { get; }

    /// <summary>전체 세션 목록 (읽기 전용).</summary>
    IReadOnlyList<SessionInfo> Sessions { get; }

    /// <summary>새 세션 생성. 성공 시 SessionCreated 메시지 발행.</summary>
    uint CreateSession(ushort cols = 80, ushort rows = 24);

    /// <summary>세션 닫기. 성공 시 SessionClosed 메시지 발행.</summary>
    void CloseSession(uint sessionId);

    /// <summary>세션 활성화. SessionActivated 메시지 발행.</summary>
    void ActivateSession(uint sessionId);

    /// <summary>세션 제목 업데이트 (콜백에서 호출).</summary>
    void UpdateTitle(uint sessionId, string title);

    /// <summary>세션 CWD 업데이트 (콜백에서 호출).</summary>
    void UpdateCwd(uint sessionId, string cwd);
}
```

#### 3.3.2 SessionInfo 모델 (Core 프로젝트)

```csharp
namespace GhostWin.Core.Models;

public class SessionInfo : ObservableObject
{
    public uint Id { get; init; }

    private string _title = "Terminal";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _cwd = string.Empty;
    public string Cwd
    {
        get => _cwd;
        set => SetProperty(ref _cwd, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
```

#### 3.3.3 SessionEvents (Core 프로젝트)

```csharp
namespace GhostWin.Core.Events;

// WeakReferenceMessenger 메시지 타입
public sealed record SessionCreatedMessage(uint SessionId);
public sealed record SessionClosedMessage(uint SessionId);
public sealed record SessionActivatedMessage(uint SessionId);
public sealed record SessionTitleChangedMessage(uint SessionId, string Title);
public sealed record SessionCwdChangedMessage(uint SessionId, string Cwd);
public sealed record SessionChildExitMessage(uint SessionId, uint ExitCode);
```

#### 3.3.4 MainWindowViewModel (App 프로젝트)

```csharp
namespace GhostWin.App.ViewModels;

public partial class MainWindowViewModel : ObservableRecipient,
    IRecipient<SessionCreatedMessage>,
    IRecipient<SessionClosedMessage>,
    IRecipient<SessionTitleChangedMessage>,
    IRecipient<SessionCwdChangedMessage>
{
    private readonly ISessionManager _sessionManager;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private TerminalTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _windowTitle = "GhostWin";

    public MainWindowViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        IsActive = true; // 메시지 수신 활성화
    }

    [RelayCommand]
    private void NewTab()
    {
        _sessionManager.CreateSession();
    }

    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab == null) return;
        _sessionManager.CloseSession(tab.SessionId);
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count == 0) return;
        var idx = Tabs.IndexOf(SelectedTab!);
        SelectedTab = Tabs[(idx + 1) % Tabs.Count];
        _sessionManager.ActivateSession(SelectedTab.SessionId);
    }

    // 메시지 수신 핸들러
    public void Receive(SessionCreatedMessage msg)
    {
        var session = _sessionManager.Sessions
            .FirstOrDefault(s => s.Id == msg.SessionId);
        if (session == null) return;

        var tab = new TerminalTabViewModel(session);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void Receive(SessionClosedMessage msg)
    {
        var tab = Tabs.FirstOrDefault(t => t.SessionId == msg.SessionId);
        if (tab == null) return;
        Tabs.Remove(tab);
        tab.Dispose(); // [Review Fix] 이벤트 구독 해제로 GC leak 방지

        if (Tabs.Count == 0)
            Application.Current.Shutdown();
        else
            SelectedTab = Tabs[^1];
    }

    public void Receive(SessionTitleChangedMessage msg)
    {
        // SessionInfo.Title은 SessionManager에서 이미 업데이트됨
        // ObservableObject 바인딩으로 UI 자동 갱신
        if (SelectedTab?.SessionId == msg.SessionId)
            WindowTitle = $"GhostWin — {msg.Title}";
    }

    public void Receive(SessionCwdChangedMessage msg)
    {
        // SessionInfo.Cwd는 SessionManager에서 이미 업데이트됨
        // ObservableObject 바인딩으로 사이드바 자동 갱신
    }
}
```

#### 3.3.5 TerminalTabViewModel (App 프로젝트)

<!-- [Review Fix] 익명 람다 이벤트 구독은 GC leak 발생.
     IDisposable 패턴 추가 + Tabs.Remove() 시 Dispose 호출 필수. -->

```csharp
namespace GhostWin.App.ViewModels;

public partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private readonly SessionInfo _session;
    private bool _disposed;

    public uint SessionId => _session.Id;
    public string Title => _session.Title;
    public string Cwd => _session.Cwd;
    public bool IsActive => _session.IsActive;

    public TerminalTabViewModel(SessionInfo session)
    {
        _session = session;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
    }
}
```

#### 3.3.6 MainWindow.xaml (XAML 구조)

```xml
<ui:FluentWindow x:Class="GhostWin.App.Views.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:vm="clr-namespace:GhostWin.App.ViewModels"
    xmlns:controls="clr-namespace:GhostWin.App.Controls"
    Title="{Binding WindowTitle}"
    Width="1024" Height="768"
    WindowBackdropType="Mica"
    ExtendsContentIntoTitleBar="True"
    d:DataContext="{d:DesignInstance vm:MainWindowViewModel}">

    <Grid>
        <Grid.ColumnDefinitions>
            <!-- 사이드바: cmux opt-out 원칙, 기본 표시 -->
            <ColumnDefinition Width="200" MinWidth="120" MaxWidth="400"/>
            <ColumnDefinition Width="Auto"/>   <!-- GridSplitter -->
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- ═══ 사이드바 (cmux 정보 밀도 원칙) ═══ -->
        <Border Grid.Column="0"
                Background="{ui:ThemeResource ControlFillColorDefaultBrush}">
            <DockPanel>
                <!-- 상단: 앱 타이틀 + New Tab -->
                <StackPanel DockPanel.Dock="Top" Margin="12,40,12,8">
                    <TextBlock Text="GhostWin" FontSize="16" FontWeight="SemiBold"
                               Foreground="{ui:ThemeResource TextFillColorPrimaryBrush}"/>
                    <ui:Button Content="New Tab"
                               Icon="{ui:SymbolIcon Add24}"
                               Command="{Binding NewTabCommand}"
                               HorizontalAlignment="Stretch" Margin="0,8,0,0"/>
                </StackPanel>

                <!-- 탭 리스트 -->
                <ListBox DockPanel.Dock="Top"
                         ItemsSource="{Binding Tabs}"
                         SelectedItem="{Binding SelectedTab}"
                         HorizontalContentAlignment="Stretch"
                         Background="Transparent"
                         BorderThickness="0">
                    <ListBox.ItemTemplate>
                        <DataTemplate DataType="{x:Type vm:TerminalTabViewModel}">
                            <Grid Margin="4,6">
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>

                                <!-- 활성 탭 indicator (이중 신호: 색상 + 형태) -->
                                <Border Grid.RowSpan="2"
                                        Width="3" CornerRadius="2"
                                        Background="{ui:ThemeResource SystemAccentColorPrimary}"
                                        Visibility="{Binding IsActive,
                                            Converter={StaticResource BoolToVisibility}}"
                                        Margin="0,0,8,0"/>

                                <!-- 탭 제목 -->
                                <TextBlock Grid.Column="1" Grid.Row="0"
                                           Text="{Binding Title}"
                                           FontSize="13" FontWeight="Medium"
                                           TextTrimming="CharacterEllipsis"/>

                                <!-- CWD (cmux sidebar 정보 밀도) -->
                                <TextBlock Grid.Column="1" Grid.Row="1"
                                           Text="{Binding Cwd}"
                                           FontSize="11" Opacity="0.6"
                                           TextTrimming="CharacterEllipsis"/>

                                <!-- 닫기 버튼 -->
                                <ui:Button Grid.Column="2" Grid.RowSpan="2"
                                           Icon="{ui:SymbolIcon Dismiss16}"
                                           Appearance="Transparent"
                                           Command="{Binding DataContext.CloseTabCommand,
                                               RelativeSource={RelativeSource AncestorType=ListBox}}"
                                           CommandParameter="{Binding}"
                                           Width="24" Height="24"/>
                            </Grid>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </DockPanel>
        </Border>

        <!-- GridSplitter -->
        <GridSplitter Grid.Column="1" Width="1"
                      Background="{ui:ThemeResource ControlStrokeColorDefaultBrush}"
                      HorizontalAlignment="Center"/>

        <!-- ═══ 터미널 렌더링 영역 ═══ -->
        <Border Grid.Column="2" Background="Black">
            <controls:TerminalHostControl x:Name="TerminalHost"/>
        </Border>
    </Grid>

    <!-- 키바인딩 -->
    <ui:FluentWindow.InputBindings>
        <KeyBinding Gesture="Ctrl+T" Command="{Binding NewTabCommand}"/>
        <KeyBinding Gesture="Ctrl+W"
                    Command="{Binding CloseTabCommand}"
                    CommandParameter="{Binding SelectedTab}"/>
        <KeyBinding Gesture="Ctrl+Tab" Command="{Binding NextTabCommand}"/>
    </ui:FluentWindow.InputBindings>
</ui:FluentWindow>
```

#### 3.3.7 cmux 디자인 원칙 적용 명세

| cmux 원칙 | GhostWin 적용 | 구현 위치 |
|-----------|-------------|----------|
| **4계층 레이아웃** | Window → Workspace(탭) → Surface(HwndHost) | MainWindow.xaml Grid 구조 |
| **Opt-out 사이드바** | 기본 표시, `sidebar.visible` false로 숨김 | AppSettings.Sidebar.Visible → Visibility 바인딩 |
| **정보 밀도** | CWD + Title 기본 표시. Git 브랜치는 **2차**에서 구현 | DataTemplate에 2행 구조 (1차: Title + CWD) |
| **이중 신호 접근성** | LeftRail indicator(형태) + AccentColor(색상) | Border Width=3 + SystemAccentColorPrimary |
| **UI/터미널 폰트 분리** | 사이드바: Segoe UI Variable / 터미널: 엔진 설정 폰트 | WPF 기본 폰트 vs ghostwin.json terminal.font |
| **Indicator 스타일** | leftRail (기본값) | Border Width=3, CornerRadius=2 |

<!-- [Review Fix] Plan §4.3에서 Git 브랜치를 1차 포함으로 표기했으나,
     1차 scope에 구현 공수를 추가하면 M-3 일정에 영향.
     CWD 표시만 1차, Git 브랜치는 2차로 확정. Plan과 동기화 필요. -->

---

### 3.4 M-4: Settings System

**목표**: AppSettings JSON 파싱, FileSystemWatcher 핫 리로드, WeakReferenceMessenger 전파

#### 3.4.1 ISettingsService 인터페이스 (Core 프로젝트)

```csharp
namespace GhostWin.Core.Interfaces;

/// <summary>
/// ghostwin.json의 앱 UI 관련 설정을 관리.
/// 터미널 렌더링 설정은 엔진 C++ settings가 별도 관리.
/// 구현체: GhostWin.Services.SettingsService
/// </summary>
public interface ISettingsService
{
    /// <summary>현재 설정 (읽기 전용). 변경 시 새 인스턴스로 교체.</summary>
    AppSettings Current { get; }

    /// <summary>설정 파일 경로 (%APPDATA%/GhostWin/ghostwin.json)</summary>
    string SettingsFilePath { get; }

    /// <summary>설정 로드. 파일 없으면 기본값 사용.</summary>
    void Load();

    /// <summary>현재 설정을 파일에 저장.</summary>
    void Save();

    /// <summary>변경 감시 시작 (FileSystemWatcher).</summary>
    void StartWatching();

    /// <summary>변경 감시 중지.</summary>
    void StopWatching();
}
```

#### 3.4.2 AppSettings 모델 (Core 프로젝트)

```csharp
namespace GhostWin.Core.Models;

/// <summary>
/// ghostwin.json에서 C# 앱이 관리하는 UI 설정만 매핑.
/// 터미널 렌더링 설정(terminal.font, terminal.colors 등)은
/// 엔진 C++ settings가 직접 관리하므로 여기에 포함하지 않음.
/// </summary>
public sealed class AppSettings
{
    /// <summary>앱 외관 (light / dark / system)</summary>
    public string Appearance { get; set; } = "dark";

    /// <summary>사이드바 설정</summary>
    /// <remarks>
    /// [Review Fix] C++ MultiplexerSettings::Sidebar도 동일 JSON 섹션을 파싱.
    /// C#은 앱 UI 레이아웃(visible, width)만 사용하고,
    /// show_ports, show_pr, show_latest_alert 등은 C++ 전용 (2차 기능).
    /// 기본값은 C++과 동일하게 유지하여 불일치 방지.
    /// </remarks>
    public SidebarSettings Sidebar { get; set; } = new();

    /// <summary>타이틀바 설정</summary>
    public TitlebarSettings Titlebar { get; set; } = new();

    /// <summary>
    /// 앱 레벨 단축키 (new_tab, close_tab, toggle_sidebar 등).
    /// </summary>
    /// <remarks>
    /// [Review Fix] 키바인딩 이중 파싱 결정:
    /// - C++ KeyMap도 동일 "keybindings" 섹션을 파싱하여 엔진 내부 dispatch에 사용.
    /// - WPF에서는 C# XAML KeyBinding이 앱 레벨 단축키를 처리.
    /// - 엔진 C++ KeyMap은 WPF 전환 후 사용되지 않음 (WinUI3 전용 경로).
    ///   M-6에서 WinUI3 코드 제거 시 C++ KeyMap 호출부도 함께 제거.
    /// - 1차 마이그레이션에서는 양쪽이 공존하지만, C# 앱이 입력을 먼저 처리하므로
    ///   엔진 KeyMap은 실질적으로 호출되지 않음.
    /// </remarks>
    public Dictionary<string, string> Keybindings { get; set; } = new();
}

/// <remarks>
/// [Review Fix] 이중화 경계: C#은 visible, width만 사용.
/// show_ports, show_pr, show_latest_alert는 C++ MultiplexerSettings 전용 (2차 기능).
/// </remarks>
public sealed class SidebarSettings
{
    public bool Visible { get; set; } = true;
    public int Width { get; set; } = 200;
    public bool ShowCwd { get; set; } = true;
    public bool ShowGit { get; set; } = true;
    public bool HideAllDetails { get; set; } = false;
}

public sealed class TitlebarSettings
{
    public bool ShowSessionInfo { get; set; } = true;
    public bool UseMica { get; set; } = true;
}
```

#### 3.4.3 설정 JSON 스키마 (앱 UI 섹션)

ghostwin.json에서 C# `SettingsService`가 읽는 섹션:

```json
{
    "app": {
        "appearance": "dark",
        "sidebar": {
            "visible": true,
            "width": 200,
            "show_cwd": true,
            "show_git": true,
            "hide_all_details": false
        },
        "titlebar": {
            "show_session_info": true,
            "use_mica": true
        }
    },
    "keybindings": {
        "new_tab": "Ctrl+T",
        "close_tab": "Ctrl+W",
        "next_tab": "Ctrl+Tab",
        "prev_tab": "Ctrl+Shift+Tab",
        "toggle_sidebar": "Ctrl+Shift+B"
    },
    "terminal": {
        "_comment": "이 섹션은 엔진 C++ settings가 직접 관리. C#에서 읽지 않음.",
        "font": { "family": "JetBrainsMono NF", "size_pt": 11.25 },
        "colors": { "theme": "catppuccin-mocha" }
    }
}
```

> **설정 이중화 경계**: `"app"` + `"keybindings"` = C# 관리. `"terminal"` + `"multiplexer"` + `"agent"` = C++ 엔진 관리. 동일 JSON 파일을 양쪽이 독립적으로 읽음.

#### 3.4.4 SettingsService 구현 핵심

```csharp
namespace GhostWin.Services;

public sealed class SettingsService : ISettingsService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; } = new();
    public string SettingsFilePath { get; }

    public SettingsService()
    {
        SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhostWin", "ghostwin.json");
    }

    public void Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            using var doc = JsonDocument.Parse(json);

            var settings = new AppSettings();

            // "app" 섹션만 파싱
            if (doc.RootElement.TryGetProperty("app", out var appElem))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(
                    appElem.GetRawText(), JsonOptions) ?? new();
            }

            // "keybindings" 섹션
            if (doc.RootElement.TryGetProperty("keybindings", out var kbElem))
            {
                settings.Keybindings = JsonSerializer
                    .Deserialize<Dictionary<string, string>>(
                        kbElem.GetRawText(), JsonOptions) ?? [];
            }

            lock (_lock) { Current = settings; }
        }
        catch (JsonException)
        {
            // 문법 오류 시 기존 설정 유지 + 로깅
            Debug.WriteLine($"[SettingsService] JSON parse error, keeping current settings");
        }
    }

    public void StartWatching()
    {
        var dir = Path.GetDirectoryName(SettingsFilePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir, "ghostwin.json")
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // [Review Fix] debounce 200ms → 50ms 변경 (NFR-03: 설정 리로드 < 100ms 충족)
        // [Review Fix] Timer 콜백은 ThreadPool 스레드에서 실행됨.
        //   WeakReferenceMessenger.Send()는 호출 스레드에서 핸들러를 동기 실행하므로,
        //   UI를 수정하는 핸들러에서 InvalidOperationException 발생.
        //   → Dispatcher.BeginInvoke로 UI 스레드 마셜링 후 Send() 호출.
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            Load();
            Application.Current?.Dispatcher.BeginInvoke(() =>
                WeakReferenceMessenger.Default.Send(
                    new SettingsChangedMessage(Current)));
        }, null, 50, Timeout.Infinite);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Save, StopWatching, Dispose 생략 (표준 패턴)
}
```

#### 3.4.5 SettingsEvents (Core 프로젝트)

```csharp
namespace GhostWin.Core.Events;

public sealed record SettingsChangedMessage(AppSettings Settings);
```

---

### 3.5 M-5: Titlebar Customization

**목표**: FluentWindow 커스텀 타이틀바, 드래그 이동, 최소화/최대화/닫기

#### 3.5.1 FluentWindow 기반 구현

WPF-UI의 `FluentWindow`가 기본 제공하는 기능 활용:

```xml
<!-- MainWindow.xaml (이미 M-3에서 정의, M-5에서 타이틀바 영역 추가) -->
<ui:FluentWindow ...
    ExtendsContentIntoTitleBar="True"
    WindowBackdropType="Mica">

    <ui:FluentWindow.TitleBar>
        <ui:TitleBar Title="{Binding WindowTitle}"
                     ShowMaximize="True"
                     ShowMinimize="True"
                     ShowClose="True"
                     Background="Transparent"/>
    </ui:FluentWindow.TitleBar>

    <!-- ... Grid 콘텐츠 ... -->
</ui:FluentWindow>
```

#### 3.5.2 타이틀바 구성 요소

| 요소 | 구현 | 비고 |
|------|------|------|
| 앱 아이콘 | `TitleBar.Icon` | 16x16 ICO |
| 앱 타이틀 + 세션 정보 | `Title` 바인딩 | `WindowTitle` ← ViewModel |
| 드래그 이동 | `FluentWindow` 기본 제공 | 타이틀바 영역 자동 |
| 더블클릭 최대화 | `FluentWindow` 기본 제공 | |
| 최소화/최대화/닫기 | `TitleBar` 기본 제공 | Fluent 스타일 캡션 버튼 |
| Mica 배경 | `WindowBackdropType="Mica"` | 설정: `titlebar.use_mica` |

#### 3.5.3 Mica Fallback

Windows 10 또는 Mica 미지원 환경:

```csharp
// MainWindow.xaml.cs OnLoaded
try
{
    WindowBackdropType = WindowBackdropType.Mica;
}
catch
{
    // Mica 미지원 시 Acrylic 또는 Solid 배경
    WindowBackdropType = WindowBackdropType.None;
    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
}
```

---

### 3.6 M-6: WinUI3 Removal

**목표**: WinUI3 코드 및 의존성 완전 제거

#### 3.6.1 삭제 대상 파일

| 파일 | 근거 |
|------|------|
| `src/app/winui_app.cpp` | God Class — WPF MVVM으로 대체 |
| `src/app/winui_app.h` | winui_app.cpp 헤더 |
| `src/app/main_winui.cpp` | WinUI3 진입점 — WPF App.xaml이 대체 |
| `src/ui/tab_sidebar.cpp` | WPF DataTemplate + ListBox로 대체 |
| `src/ui/titlebar_manager.cpp` | WPF-UI FluentWindow + TitleBar로 대체 |
| `resources.pri` (루트) | WinAppSDK MRM fix용 — WPF 불필요 |

#### 3.6.2 CMakeLists.txt 수정

- `ghostwin_winui` 타깃 블록 (line 184~237) 제거
- WinAppSDK NuGet 참조 제거
- `external/winui/` 경로 참조 제거

#### 3.6.3 M-6 실행 전 체크리스트

- [ ] 기능 동등성 확인: 다중 탭, 세션 전환, 설정 핫 리로드, TSF 한글, 타이틀바
- [ ] Git tag `pre-winui3-removal` 생성
- [ ] 엔진 DLL + WPF 앱만으로 빌드 성공 확인
- [ ] V3/V6 벤치마크 패스

---

## 4. 빌드 파이프라인

### 4.1 통합 빌드 스크립트 (build_all.ps1)

<!-- [Review Fix] DLL 복사 경로를 Plan §13.1과 통일: lib/ 서브디렉토리 사용 -->

```text
Step 1: scripts/build_libghostty.ps1    ← Zig 빌드 (변경 시만)
Step 2: scripts/build_ghostwin.ps1      ← CMake → ghostwin_engine.dll
Step 3: DLL 복사
        build/ghostwin_engine.dll → src/GhostWin.App/lib/
        build/ghostty-vt.dll      → src/GhostWin.App/lib/
Step 4: dotnet build src/GhostWin.App/GhostWin.App.csproj -c Release
Step 5: scripts/run_benchmarks.ps1      ← V3/V6 벤치마크 (선택적)
```

### 4.2 DLL 복사 전략

네이티브 DLL을 MSBuild 프로세스에 통합하지 않고 스크립트에서 처리:

- 이유: CMake 출력 경로(`build/`)와 dotnet 출력 경로(`bin/Release/`)가 다름
- `build_all.ps1`이 Step 3에서 `Copy-Item`으로 `lib/` 디렉토리에 복사
- `.gitignore`에 `src/GhostWin.App/lib/*.dll` 추가
- `.csproj`에서 `lib/*.dll`을 출력 디렉토리에 복사하는 `ContentWithTargetPath` 추가

### 4.3 병행 운용

M-1~M-5 기간 동안:
- WinUI3 `ghostwin_winui` 타깃은 CMakeLists.txt에서 유지 (변경 없음)
- WPF 앱은 `GhostWin.sln`으로 독립 빌드
- M-6에서만 WinUI3 코드 제거

### 4.4 디버깅

- Visual Studio Mixed Mode Debugging: C# WPF + C++ 엔진 DLL 동시
- 엔진 빌드: `RelWithDebInfo`로 PDB 포함
- 네이티브 예외: `SEHException`으로 catch 가능

---

## 5. 성능 요구사항

| NFR | 목표 | 측정 | M-2 검증 |
|-----|------|------|---------|
| P/Invoke 왕복 | < 1ms | V3 벤치마크 | 필수 |
| 탭 전환 | < 50ms | 시각 지연 없음 | M-3 |
| 설정 리로드 | < 100ms | JSON 변경 → 반영 | M-4 |
| 메모리 (탭당) | < 20MB 추가 | Task Manager | M-3 |
| 대량 출력 스루풋 | 프리징 없음 | V6 벤치마크 (1MB) | 필수 |

---

## 6. 리스크 및 완화

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| HwndHost Airspace (팝업 가려짐) | 중 | 높음 | Popup → Window로 Z-order 강제. M-2에서 검증 (PoC 미검증) |
| 콜백 스레드 안전성 | 높음 | 중 | `[UnmanagedCallersOnly]` + `Dispatcher.BeginInvoke`. M-2에서 로그로 검증 |
| WPF-UI 3.x→4.x breaking change | 중 | 중 | M-1 게이트에서 평가. 실패 시 3.x 유지 |
| 설정 이중 감시 경합 | 낮음 | 중 | debounce 200ms로 완화. 양쪽 FileWatcher가 순서 무관하게 독립 동작 |
| .NET 10 Preview 안정성 | 중 | 중 | .NET 9 fallback 가능 (WPF-UI 호환 확인 필요) |
| CMake+MSBuild 빌드 통합 | 중 | 높음 | `build_all.ps1`로 순서 보장, DLL 복사 자동화 |

---

## 7. 구현 순서 및 예상 일정

```text
M-1: Solution Structure (기반)    [2~3일]
    ↓
M-2: Engine Interop (렌더링)      [5~7일]     M-4: Settings (독립) [3~5일]
    ↓
M-3: Session/Tab (다중 세션)      [5~7일]
    ↓
M-5: TitleBar (외관 완성)         [2~3일]
    ↓
M-6: WinUI3 Removal (정리)       [1~2일]
─────────────────────────────────────────────
합계: 18~27일 (1인 기준)
```

---

## 8. Glossary

| 용어 | 의미 |
|------|------|
| Engine DLL | `ghostwin_engine.dll` — C++ 네이티브 엔진 (DX11, ConPTY, VT, TSF, GlyphAtlas) |
| C API | `ghostwin_engine.h`에 정의된 19개 함수 + 7종 콜백 |
| GwCallbacks | 네이티브 콜백 구조체 (context + 함수 포인터 7개) |
| HwndHost | WPF에서 Win32 HWND를 호스팅하는 컨트롤 (DX11 SwapChain 렌더링 영역) |
| Airspace | WPF 렌더링 영역과 HwndHost 영역이 겹칠 때 Z-order 문제 |
| Dispatcher | WPF UI 스레드 메시지 펌프. `BeginInvoke`로 스레드 마셜링 |
| cmux | 참조 터미널 멀티플렉서 ([cmux.com](https://cmux.com)). UI 디자인 원칙 출처 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-06 | 초안 작성 — M-1~M-6 상세 설계, 인터페이스 시그니처, XAML 구조, 콜백 마셜링 | 노수장 |
| 0.2 | 2026-04-06 | 검토 반영: 콜백 len 단위 명시, Timer→Dispatcher 마셜링, TerminalTabViewModel IDisposable, TsfBridge 직접호출 유지, keybindings/sidebar 이중화 경계, debounce 50ms, DLL 경로 lib/ 통일, Git 브랜치 2차 확정 | AI Agent |
