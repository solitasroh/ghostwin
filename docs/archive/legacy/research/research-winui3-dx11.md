# GhostWin Terminal — WinUI3 + DirectX 11 기술 리서치

> 작성일: 2026-03-28
> 목적: WinUI3 + D3D11 통합 및 네이티브 UI 구현을 위한 심층 기술 조사
> 상태: 초안

---

## 요약

| 항목 | 실현 가능성 | 비고 |
|------|-----------|------|
| SwapChainPanel + D3D11 통합 | **상** | Windows Terminal이 실전 검증. C++ 패턴 확립됨 |
| Windows App SDK 1.8 안정성 | **상** | v1.8.6 (2026-03-18) 현재 지원 중 |
| WinUI3 C++/WinRT 개발 환경 | **상** | VS 2022 + vcpkg + NuGet 패턴 정립 |
| TabView 수직 사이드바 | **중** | TabView 자체는 수직 미지원, NavigationView 혼합 필요 |
| Toast / 인앱 알림 | **상** | AppNotificationBuilder API 완성도 높음 |
| 한국어 IME + 커스텀 컨트롤 | **중** | TSF ITfContextOwner 직접 구현 필요. 난이도 높음 |
| Win32 Interop (ConPTY, HWND) | **상** | 공식 API 완비, Windows Terminal 레퍼런스 있음 |
| 시스템 트레이 아이콘 | **상** | Win32 Shell_NotifyIcon + HWND interop 패턴 확립 |

---

## 1. WinUI3 SwapChainPanel + D3D11 통합

### 1-1. 핵심 원리 (확인된 사실)

SwapChainPanel은 DXGI 스왑체인을 XAML 비주얼 트리에 통합하는 브리지 컨트롤이다.
핵심은 `ISwapChainPanelNative` COM 인터페이스를 통해 DXGI 스왑체인을 SwapChainPanel에 연결하는 것이다.

**출처:** [DirectX and XAML interop — Microsoft Learn](https://learn.microsoft.com/en-us/windows/uwp/gaming/directx-and-xaml-interop), [SwapChainPanel Class — Windows App SDK 1.8](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel?view=windows-app-sdk-1.8)

### 1-2. 필수 헤더

```cpp
#include <microsoft.ui.xaml.media.dxinterop.h>   // ISwapChainPanelNative 정의
#include <dxgi1_2.h>                              // IDXGIFactory2::CreateSwapChainForComposition
#include <winrt/Windows.System.Threading.h>       // ThreadPool (WinUI3 데스크톱 필수!)
```

> **주의 (확인된 사실):** UWP와 달리 WinUI3 데스크톱 앱에서는 `winrt/Windows.System.Threading.h`를 PCH에 명시적으로 포함해야 한다.
> 출처: [WindowsAppSDK Discussion #3264](https://github.com/microsoft/WindowsAppSDK/discussions/3264)

### 1-3. CreateSwapChainForComposition 패턴

```cpp
// 1. D3D11 디바이스 생성
ComPtr<ID3D11Device> d3dDevice;
D3D11CreateDevice(
    nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
    D3D11_CREATE_DEVICE_BGRA_SUPPORT,  // BGRA 필수
    nullptr, 0, D3D11_SDK_VERSION,
    &d3dDevice, nullptr, nullptr);

// 2. DXGI Factory 획득
ComPtr<IDXGIDevice> dxgiDevice;
d3dDevice.As(&dxgiDevice);
ComPtr<IDXGIAdapter> adapter;
dxgiDevice->GetAdapter(&adapter);
ComPtr<IDXGIFactory2> factory;
adapter->GetParent(IID_PPV_ARGS(&factory));

// 3. SwapChain 생성 (ForComposition 패턴)
DXGI_SWAP_CHAIN_DESC1 desc = {};
desc.Width  = static_cast<UINT>(panelWidth);
desc.Height = static_cast<UINT>(panelHeight);
desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
desc.SampleDesc.Count = 1;
desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
desc.BufferCount = 2;
desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; // 필수
desc.Scaling    = DXGI_SCALING_STRETCH;             // 필수
desc.AlphaMode  = DXGI_ALPHA_MODE_PREMULTIPLIED;

ComPtr<IDXGISwapChain1> swapChain;
factory->CreateSwapChainForComposition(
    d3dDevice.Get(), &desc, nullptr, &swapChain);
```

> **필수 조건 (확인된 사실):**
> - `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` 또는 `FLIP_DISCARD` 사용
> - `DXGI_SCALING_STRETCH` 사용
> - `DXGI_ALPHA_MODE_PREMULTIPLIED` (투명도 필요 시)
>
> 출처: [IDXGIFactory2::CreateSwapChainForComposition — MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition)

### 1-4. ISwapChainPanelNative 연결 (C++/WinRT)

```cpp
// SwapChainPanel이 Loaded 이벤트 후에 초기화할 것
// m_swapChainPanel은 XAML에서 바인딩된 SwapChainPanel 컨트롤

// C++/WinRT 올바른 방법: winrt::get_unknown() 또는 .as<>() 사용
auto panelNative = m_swapChainPanel.as<ISwapChainPanelNative>();
winrt::check_hresult(panelNative->SetSwapChain(swapChain.Get()));
```

> **중요 (확인된 사실):**
> `reinterpret_cast<IUnknown*>(&panel)->QueryInterface(...)` 방식은 **접근 위반(AV)** 발생.
> C++/WinRT에서는 반드시 `winrt::get_unknown()` 또는 `.as<ISwapChainPanelNative>()` 사용.
> 출처: [WindowsAppSDK Discussion #865](https://github.com/microsoft/WindowsAppSDK/discussions/865)

> **타이밍 주의 (확인된 사실):**
> `InitializeComponent()` 직후가 아니라 SwapChainPanel의 `Loaded` 이벤트에서 초기화해야 함.
> 출처: [Creating DirectX 11 SwapChain in WinUI 3.0](https://juhakeranen.com/winui3/directx-11-2-swap-chain.html)

### 1-5. XAML 정의

```xml
<SwapChainPanel x:Name="m_swapChainPanel"
                SizeChanged="OnSwapChainPanelSizeChanged"
                CompositionScaleChanged="OnCompositionScaleChanged"
                Loaded="OnSwapChainPanelLoaded" />
```

### 1-6. Windows Terminal의 SwapChainPanel 사용 방식

Windows Terminal은 `ISwapChainPanelNative2`(더 신버전 인터페이스)를 사용하여 스왑체인 핸들(HANDLE)을 직접 전달한다.
`TermControl._AttachDxgiSwapChainToXaml()` 메서드에서 이 연결이 이루어진다.

```cpp
// Windows Terminal TermControl.cpp 패턴 (참고)
auto panelNative2 = SwapChainPanel().as<ISwapChainPanelNative2>();
panelNative2->SetSwapChainHandle(swapChainHandle);
```

**PR #10023:** AtlasEngine이 스왑체인 오브젝트(`IDXGISwapChain1`) 대신 핸들(`HANDLE`)을 반환하도록 변경.
출처: [Use DComp surface handle — Terminal PR #10023](https://github.com/microsoft/terminal/pull/10023)

### 1-7. 렌더 스레드 / UI 스레드 분리 패턴

#### Windows Terminal AtlasEngine의 이중 상태 구조 (확인된 사실)

```
_api 필드  : API 콜백에서 접근 (콘솔 락 보유 상태)
_p 필드    : 백그라운드 렌더 스레드 Present()에서만 접근
StartPaint(): _api → _p 동기화 수행
```

출처: [Atlas Engine — DeepWiki](https://deepwiki.com/microsoft/terminal/3.2-atlas-engine), [AtlasEngine.h — github.com/microsoft/terminal](https://github.com/microsoft/terminal/blob/main/src/renderer/atlas/AtlasEngine.h)

#### GhostWin 적용 패턴 (권장)

```cpp
// 백그라운드 렌더링 루프: ThreadPool 사용
winrt::Windows::System::Threading::ThreadPool::RunAsync([this, self = get_strong()](auto) {
    while (m_renderLoopRunning) {
        RenderFrame();
        m_swapChain->Present(1, 0);
    }
});

// UI 스레드에서 스왑체인 크기 변경 시: Dispatcher 큐 사용
m_swapChainPanel.DispatcherQueue().TryEnqueue([this]() {
    ResizeSwapChain();
});
```

> **렌더 스레드 독립 입력 (확인된 사실):**
> `SwapChainPanel::CreateCoreIndependentInputSource()` 메서드로 마우스/터치 입력을 백그라운드 스레드에서 처리 가능.
> 출처: [SwapChainPanel.CreateCoreIndependentInputSource — MSDN](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.swapchainpanel.createcoreindependentinputsource)

### 1-8. DPI 변경 처리 (주의 항목)

```cpp
// CompositionScaleChanged 이벤트 처리
void OnCompositionScaleChanged(SwapChainPanel const& panel, IInspectable const&) {
    float scaleX = panel.CompositionScaleX();
    float scaleY = panel.CompositionScaleY();
    // DXGI 스왑체인 리사이즈 필요
    ResizeSwapChain(
        static_cast<UINT>(panel.ActualWidth()  * scaleX),
        static_cast<UINT>(panel.ActualHeight() * scaleY));
}
```

> **알려진 리스크 (확인된 사실):** DPI 변경 시 깜박임(flicker) 발생 가능.
> Windows Terminal 코드에서 해결 방법 참고 권장.
> 출처: onboarding.md 리스크 항목

---

## 2. WinUI3 C++/WinRT 개발 환경

### 2-1. Windows App SDK 최신 버전 (확인된 사실)

| 채널 | 버전 | 릴리즈일 | 지원 종료 |
|------|------|---------|----------|
| **Stable (권장)** | **1.8.6 (1.8.260317003)** | 2026-03-18 | 2026-09-09 |
| Preview | 2.0-Preview1 | 2026-02-13 | 미지원 |
| Experimental | 2.0-Experimental6 | 2026-03-13 | 미지원 |

**GhostWin 권장: Windows App SDK 1.8.x Stable**

출처: [Windows App SDK Release Channels — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels), [NuGet: Microsoft.WindowsAppSDK 1.8.260317003](https://www.nuget.org/packages/Microsoft.WindowsAppSdk/)

### 2-2. C++/WinRT 프로젝트 설정

#### NuGet 패키지 (필수)

```xml
<!-- .vcxproj 또는 NuGet 패키지 매니저에서 설치 -->
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.260317003" />
<PackageReference Include="Microsoft.Windows.CppWinRT" Version="2.0.250130.2" />
```

> **주의 (확인된 사실):**
> `Microsoft.Windows.CppWinRT` NuGet 패키지 없이는 Windows App SDK의 WinRT API 헤더를 찾을 수 없음.
> 출처: [Use Windows App SDK in existing project — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project)

#### vcpkg를 사용하는 경우

```json
// vcpkg.json
{
  "dependencies": [
    "cppwinrt",
    "directx-headers",
    "wil"
  ]
}
```

> **권장 (확인된 사실):**
> vcpkg의 `cppwinrt` 포트가 Windows SDK에 포함된 것보다 최신 헤더를 제공.
> Visual Studio 2022 17.6+ 에 vcpkg가 내장됨.
> 출처: [vcpkg is Now Included with Visual Studio — C++ Blog](https://devblogs.microsoft.com/cppblog/vcpkg-is-now-included-with-visual-studio/)

### 2-3. XAML + C++ 코드 비하인드 기본 패턴

```cpp
// MainWindow.xaml.h
namespace winrt::GhostWin::implementation {
    struct MainWindow : MainWindowT<MainWindow> {
        MainWindow();

        void OnSwapChainPanelLoaded(
            winrt::Windows::Foundation::IInspectable const& sender,
            winrt::Microsoft::UI::Xaml::RoutedEventArgs const& e);
    };
}

// MainWindow.xaml.cpp
void MainWindow::OnSwapChainPanelLoaded(IInspectable const&, RoutedEventArgs const&) {
    // SwapChainPanel 준비 완료 후 D3D11 초기화
    InitializeD3D11();
}
```

### 2-4. Windows 11 네이티브 룩앤필

#### Mica 배경 적용 (C++/WinRT)

```cpp
// App.xaml.cpp 또는 MainWindow.xaml.cpp
#include <winrt/Microsoft.UI.Composition.SystemBackdrops.h>

MicaController m_micaController{nullptr};
SystemBackdropConfiguration m_backdropConfig{nullptr};

void SetupMicaBackdrop() {
    if (!MicaController::IsSupported()) return;

    m_backdropConfig = SystemBackdropConfiguration();
    m_micaController = MicaController();

    // Window의 ICompositionSupportsSystemBackdrop 인터페이스에 연결
    m_micaController.AddSystemBackdropTarget(
        m_window.as<ICompositionSupportsSystemBackdrop>());
    m_micaController.SetSystemBackdropConfiguration(m_backdropConfig);
}
```

출처: [System backdrops (Mica/Acrylic) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)

> **주의 (확인된 사실):**
> SwapChainPanel은 투명도 미지원, AcrylicBrush 효과도 미지원.
> Mica를 SwapChainPanel 아래에 적용하는 방식은 불가능. 창 전체 배경에만 적용 가능.

#### 타이틀 바 커스터마이징

```cpp
// ExtendsContentIntoTitleBar (권장 방법)
// Window API 사용이 AppWindow API보다 안전
m_window.ExtendsContentIntoTitleBar(true);
m_window.SetTitleBar(m_customTitleBar);
```

출처: [Title bar customization — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)

---

## 3. 탭/Pane 멀티플렉서 UI

### 3-1. WinUI3 TabView 분석

TabView는 브라우저 스타일 수평 탭을 위한 컨트롤로, **수직 탭 배치를 기본 지원하지 않는다**.

```xml
<!-- 기본 TabView 구조 -->
<TabView x:Name="m_tabView"
         TabItemsSource="{x:Bind TerminalTabs}"
         AddTabButtonClick="OnAddTabButtonClick"
         TabCloseRequested="OnTabCloseRequested">
    <TabView.TabItemTemplate>
        <DataTemplate x:DataType="local:TerminalTab">
            <TabViewItem Header="{x:Bind Title}">
                <TabViewItem.IconSource>
                    <SymbolIconSource Symbol="Document" />
                </TabViewItem.IconSource>
            </TabViewItem>
        </DataTemplate>
    </TabView.TabItemTemplate>
</TabView>
```

출처: [TabView Class — Windows App SDK 1.8](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.tabview?view=windows-app-sdk-1.8)

### 3-2. cmux 스타일 수직 탭 사이드바 구현 방법

TabView가 수직을 지원하지 않으므로, **NavigationView + 커스텀 컨트롤** 조합이 현실적 방안이다.

```xml
<!-- 수직 사이드바 레이아웃 -->
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="220" />    <!-- 사이드바 -->
        <ColumnDefinition Width="*" />      <!-- 터미널 영역 -->
    </Grid.ColumnDefinitions>

    <!-- 사이드바: NavigationView 또는 커스텀 ListView -->
    <ListView Grid.Column="0"
              x:Name="m_tabSidebar"
              ItemsSource="{x:Bind TerminalSessions}"
              SelectionChanged="OnSessionSelected">
        <ListView.ItemTemplate>
            <DataTemplate x:DataType="local:TerminalSession">
                <Grid>
                    <!-- 에이전트 상태 배지 + 탭 이름 -->
                    <InfoBadge x:Name="m_statusBadge"
                               Value="{x:Bind UnreadCount}"
                               Visibility="{x:Bind HasNotification}" />
                    <TextBlock Text="{x:Bind Title}" />
                    <TextBlock Text="{x:Bind GitBranch}"
                               Foreground="Gray" FontSize="11" />
                </Grid>
            </DataTemplate>
        </ListView.ItemTemplate>
    </ListView>

    <!-- 터미널 SwapChainPanel 영역 -->
    <SwapChainPanel Grid.Column="1" x:Name="m_terminalPanel" />
</Grid>
```

> **실현 가능성: 중**
> 추측: TabView의 ControlTemplate을 완전히 교체하여 수직화하는 방법도 있으나, NavigationView + ListView 조합이 구현 난이도가 낮다.

### 3-3. 에이전트 상태 배지 (InfoBadge 컨트롤)

```xml
<!-- InfoBadge: 알림 수 표시 -->
<InfoBadge x:Name="m_agentBadge"
           Style="{ThemeResource AttentionValueInfoBadgeStyle}"
           Value="{x:Bind NotificationCount}"
           Visibility="{x:Bind HasPendingInput, Converter={StaticResource BoolToVisConverter}}" />
```

출처: [Info badge — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/design/controls/info-badge), [InfoBadge Class — Windows App SDK 1.6](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.infobadge?view=windows-app-sdk-1.6)

### 3-4. Pane 분할 레이아웃 관리

중첩 Grid/SplitView 방식으로 구현 가능하다.

```xml
<!-- 수평/수직 Pane 분할: 중첩 Grid 패턴 -->
<Grid x:Name="m_paneRoot">
    <!-- 동적으로 ColumnDefinitions/RowDefinitions 추가 -->
    <!-- GridSplitter로 크기 조정 구현 -->
</Grid>
```

> **GridSplitter (확인된 사실):**
> WinUI3 자체에는 GridSplitter가 없다. Community Toolkit의 `CommunityToolkit.WinUI.UI.Controls` 패키지에 포함되어 있음.
> 출처: [Proposal: Splitter control — microsoft-ui-xaml #5346](https://github.com/microsoft/microsoft-ui-xaml/issues/5346)

#### Windows Terminal Pane 구현 참고

Windows Terminal은 `Pane.cpp` / `Pane.h`에서 이진 트리(binary tree) 방식으로 pane 분할을 관리한다.
각 노드는 `SplitState` (수평/수직), 비율(`_desiredSplitPosition`), 자식 노드 2개를 가진다.
출처: [github.com/microsoft/terminal — src/cascadia/TerminalApp/](https://github.com/microsoft/terminal)

---

## 4. 알림 시스템

### 4-1. Win32 Toast 알림 (AppNotificationBuilder)

Windows App SDK 1.x부터 `AppNotificationBuilder`를 사용한다. "Toast notification" 용어는 "App notification"으로 대체 중.

```cpp
#include <winrt/Microsoft.Windows.AppNotifications.h>
#include <winrt/Microsoft.Windows.AppNotifications.Builder.h>

using namespace winrt::Microsoft::Windows::AppNotifications;
using namespace winrt::Microsoft::Windows::AppNotifications::Builder;

void SendAgentWaitingNotification(std::wstring_view sessionName) {
    auto builder = AppNotificationBuilder()
        .AddText(L"에이전트 입력 대기")
        .AddText(std::wstring(sessionName) + L" 세션이 응답을 기다리고 있습니다.")
        .AddButton(AppNotificationButton(L"세션으로 이동")
            .AddArgument(L"action", L"jumpToSession")
            .AddArgument(L"session", std::wstring(sessionName)));

    AppNotificationManager::Default().Show(builder.BuildNotification());
}
```

출처: [App notifications quickstart — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart)

> **패키징 요구사항 (확인된 사실):**
> Unpackaged 앱에서는 `AppNotificationManager` 대신 `ToastNotificationManagerCompat` 사용 필요.
> GhostWin이 packaged 앱으로 배포될 경우 `AppNotificationManager.Default` 사용 가능.

### 4-2. 인앱 알림 패널 (InfoBar)

```xml
<!-- 상단 고정 인앱 알림 -->
<InfoBar x:Name="m_agentAlert"
         IsOpen="{x:Bind HasPendingAgents}"
         Severity="Informational"
         Title="에이전트 대기 중"
         Message="{x:Bind PendingAgentSummary}"
         IsClosable="True">
    <InfoBar.ActionButton>
        <Button Content="모두 보기" Click="OnShowNotificationPanel" />
    </InfoBar.ActionButton>
</InfoBar>
```

출처: [InfoBar — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/design/controls/infobar), [InfoBar Class — Windows App SDK 1.7](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.infobar?view=windows-app-sdk-1.7)

### 4-3. 시스템 트레이 아이콘

WinUI3에는 기본 NotifyIcon이 없으므로, Win32 `Shell_NotifyIcon` + HWND Interop으로 구현해야 한다.

```cpp
// NativeWindow: 트레이 메시지 수신용 보이지 않는 Win32 창
class TrayIcon {
    HWND m_hwnd;
    NOTIFYICONDATAW m_nid{};
    GUID m_iconGuid;  // 앱 이동 시 아이콘 추적용

public:
    void Show(HICON icon, std::wstring_view tooltip) {
        m_nid.cbSize = sizeof(NOTIFYICONDATAW);
        m_nid.hWnd   = m_hwnd;
        m_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_GUID;
        m_nid.guidItem = m_iconGuid;
        m_nid.hIcon  = icon;
        m_nid.uCallbackMessage = WM_TRAY_CALLBACK;
        wcsncpy_s(m_nid.szTip, tooltip.data(), ARRAYSIZE(m_nid.szTip));
        Shell_NotifyIconW(NIM_ADD, &m_nid);
    }

    // 탐색기 재시작 처리 필수
    LRESULT OnTaskbarCreated() {
        Shell_NotifyIconW(NIM_ADD, &m_nid);
        return 0;
    }
};
```

> **WM_TASKBARCREATED 처리 필수 (확인된 사실):**
> 탐색기 재시작 시 트레이 아이콘이 사라지는 현상을 방지하기 위해 이 메시지 처리 필수.
> 출처: [Using NotifyIcon in WinUI 3 — Albert Akhmetov (2025)](https://albertakhmetov.com/posts/2025/using-notifyicon-in-winui-3/)

> **대안 라이브러리:**
> `H.NotifyIcon.WinUI` (NuGet v2.4.1) — WinUI3 전용 NotifyIcon 구현체.
> 출처: [H.NotifyIcon.WinUI — NuGet](https://www.nuget.org/packages/H.NotifyIcon.WinUI)

---

## 5. 한국어 IME + WinUI3

### 5-1. 문제 배경

GPU 렌더링 터미널에서 한국어 IME의 조합 문자를 렌더링하는 것은 난이도가 높다.
표준 TextBox는 자동으로 TSF를 처리하지만, D3D11 기반 커스텀 렌더러에서는 직접 구현이 필요하다.

### 5-2. Windows Terminal의 IME 처리 방식 (확인된 사실)

Windows Terminal은 `TsfDataProvider` 클래스로 TSF 인터페이스를 구현한다.

| 인터페이스 | 역할 |
|-----------|------|
| `ITfContextOwner` | TSF 컨텍스트 소유자 등록 |
| `ITfContextOwnerCompositionSink` | 조합 시작/종료 알림 수신 |
| `QueryInterface` | COM 인터페이스 쿼리 |
| `GetHwnd` | 입력 창 HWND 반환 |
| `GetViewport` | 텍스트 입력 영역 반환 |
| `GetCursorPosition` | 커서 위치 반환 (후보창 위치 결정) |
| `HandleOutput` | 조합 완료 텍스트 수신 |

출처: [terminal/src/cascadia/TerminalControl/TermControl.cpp — github.com/microsoft/terminal](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControl.cpp)

### 5-3. IMM32 vs TSF 선택

| 방식 | 장점 | 단점 |
|------|------|------|
| **IMM32 (WM_IME_COMPOSITION)** | 구현 단순, Win32 표준 | Win32 창 필요, UWP/WinUI 환경에서 제약 |
| **TSF (ITfContextOwner)** | 현대적, UWP/WinUI 호환 | 구현 복잡도 높음, COM 인터페이스 다수 |

> **GhostWin 권장 (추측):**
> WinUI3 데스크톱 앱 + HWND 획득이 가능하므로, IMM32 방식으로 프로토타입 후 TSF로 전환하는 단계적 접근을 권장.
> Windows Terminal TsfDataProvider 코드를 참고하여 클린룸 구현.

### 5-4. IMM32 기반 조합 문자 처리 (기본 구현)

```cpp
// Win32 서브클래싱 또는 메시지 훅으로 처리
case WM_IME_COMPOSITION: {
    HIMC hImc = ImmGetContext(hwnd);
    if (lParam & GCS_RESULTSTR) {
        // 확정된 문자열
        LONG len = ImmGetCompositionStringW(hImc, GCS_RESULTSTR, nullptr, 0);
        std::wstring result(len / sizeof(wchar_t), L'\0');
        ImmGetCompositionStringW(hImc, GCS_RESULTSTR, result.data(), len);
        SendInputToTerminal(result);
    }
    if (lParam & GCS_COMPSTR) {
        // 조합 중인 문자열 (화면에 언더라인으로 표시)
        LONG len = ImmGetCompositionStringW(hImc, GCS_COMPSTR, nullptr, 0);
        std::wstring comp(len / sizeof(wchar_t), L'\0');
        ImmGetCompositionStringW(hImc, GCS_COMPSTR, comp.data(), len);
        UpdateCompositionDisplay(comp);  // D3D11 렌더러에 조합 문자 전달
    }
    ImmReleaseContext(hwnd, hImc);
    break;
}
```

출처: [Processing the WM_IME_COMPOSITION Message — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/intl/processing-the-wm-ime-composition-message)

### 5-5. D3D11 렌더러와 조합 문자 연동

조합 중인 문자를 D3D11로 렌더링하려면:
1. 렌더 스레드와 공유되는 `compositionString` 상태 변수 유지
2. 커서 위치에 언더라인 스타일로 렌더링
3. 조합 완료 시 확정 문자로 교체

```cpp
// 렌더 상태 구조체에 조합 정보 추가
struct RenderState {
    // ...
    std::wstring compositionString;   // 현재 조합 중인 문자
    size_t compositionCursorPos = 0;  // 조합 커서 위치
    bool   isComposing = false;
};
```

> **실현 가능성: 중**
> 기본 조합 문자 표시는 구현 가능하나, TSF 후보창 위치 제어 등 완전한 IME 지원은 상당한 구현 비용이 필요하다.
> Windows Terminal의 구현을 참고하는 것이 가장 현실적인 방법.

---

## 6. Win32 Interop

### 6-1. HWND 획득 (C++/WinRT)

```cpp
#include <Microsoft.UI.Xaml.Window.h>
#include <microsoft.ui.interop.h>

// Window에서 HWND 획득
HWND GetWindowHandle(winrt::Microsoft::UI::Xaml::Window const& window) {
    auto windowNative = window.as<IWindowNative>();
    HWND hwnd = nullptr;
    winrt::check_hresult(windowNative->get_WindowHandle(&hwnd));
    return hwnd;
}
```

출처: [Retrieve a window handle (HWND) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/ui/retrieve-hwnd), [How to get Main Window Handle — Simon Mourier's Blog](https://www.simonmourier.com/blog/How-to-get-Main-Window-Handle-on-Page-in-WinUI-3-Using-cplusplus/)

### 6-2. 글로벌 핫키 등록

```cpp
// HWND 획득 후 핫키 등록
void RegisterGlobalHotKey(HWND hwnd) {
    // Ctrl+Alt+G: GhostWin 포커스
    RegisterHotKey(hwnd, HOTKEY_ID_FOCUS, MOD_CONTROL | MOD_ALT, 'G');
}

// WndProc에서 처리
case WM_HOTKEY: {
    if (wParam == HOTKEY_ID_FOCUS) {
        ShowAndFocusWindow();
    }
    break;
}
```

출처: [Chapter 5: Add global hot key in WinUI 3 — Whid](https://whid.eu/2022/05/13/chapter-5-add-global-hot-key-in-winui-3/)

### 6-3. ConPTY 연동

WinUI3 데스크톱 앱은 Win32 API를 직접 호출 가능하므로 ConPTY 사용에 제약이 없다.

```cpp
#include <consoleapi.h>

// ConPTY 생성 패턴
HRESULT CreateTerminalSession(
    COORD size,
    HANDLE hInput,
    HANDLE hOutput,
    HPCON* phpc)
{
    return CreatePseudoConsole(size, hInput, hOutput, 0, phpc);
}
```

출처: [CreatePseudoConsole — Microsoft Learn](https://learn.microsoft.com/en-us/windows/console/createpseudoconsole)

> **레퍼런스 (확인된 사실):**
> Windows Terminal 저장소 `samples/ConPTY/` 에 EchoCon, MiniTerm, GUIConsole 샘플이 있음.
> GUIConsole 샘플은 WinUI 3 기반으로 업데이트 예정 (Terminal issue #13851).

### 6-4. Named Pipe 훅 서버 (클로드 에이전트 연동)

```cpp
// Named Pipe 서버: OSC 알림 수신
void StartHookServer() {
    HANDLE hPipe = CreateNamedPipeW(
        L"\\\\.\\pipe\\GhostWinHook",
        PIPE_ACCESS_INBOUND,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
        PIPE_UNLIMITED_INSTANCES,
        4096, 4096, 0, nullptr);

    // 비동기 IOCP 처리로 UI 스레드 차단 없이 수신
}
```

> **OSC 훅 연동 (추측):**
> Claude Code의 Stop/Notification 이벤트를 Named Pipe 또는 OSC 시퀀스(OSC 9;99;777)로 수신하는 방식은
> libghostty-vt의 VT 파싱 결과에서 직접 처리하는 것이 가장 효율적일 것으로 추측됨.

---

## 7. 종합 아키텍처 권장 패턴

### 7-1. SwapChainPanel + UI 스레드 레이아웃

```
MainWindow (WinUI3)
├── Grid (루트)
│   ├── Column 0: ListView (수직 탭 사이드바)
│   │   └── DataTemplate: SessionItem (InfoBadge, TextBlock)
│   └── Column 1: SwapChainPanel (D3D11 렌더링 표면)
│       └── ISwapChainPanelNative::SetSwapChain(...)
├── InfoBar (상단 알림 배너, IsOpen 바인딩)
└── AppTitleBar (Mica 배경 + 커스텀 타이틀바)
```

### 7-2. 스레드 모델 연결

```
UI 스레드 (ASTA)
├── XAML 이벤트 처리 (탭 클릭, 설정)
├── SwapChainPanel 크기/DPI 변경 처리
└── InfoBadge / InfoBar 상태 업데이트

렌더 스레드 (ThreadPool MTA)
├── D3D11 Present() 루프
├── Glyph Atlas 업데이트
└── 조합 문자 오버레이 렌더링

파싱 스레드 (ThreadPool MTA)
├── libghostty-vt 호출
├── VT 시퀀스 처리 (OSC 9/99/777 감지)
└── 에이전트 상태 변경 → UI 스레드 디스패치

I/O 스레드 (IOCP)
├── ConPTY 비동기 Read/Write
└── Named Pipe 훅 서버
```

### 7-3. 에이전트 상태 알림 흐름

```
ConPTY 출력
  └→ I/O 스레드 수신
      └→ 파싱 스레드: VT 파싱 + OSC 감지
          └→ OSC 9 (에이전트 대기 감지)
              ├→ UI 스레드: InfoBadge.Value++ , 사이드바 하이라이트
              ├→ AppNotificationManager: Toast 발송
              └→ 트레이 아이콘: 깜박임 효과
```

---

## 8. 구현 우선순위 및 리스크 재평가

### Phase 4 (WinUI3 UI) 시작 전 확인 필요 사항

| 확인 항목 | 방법 | 우선순위 |
|----------|------|---------|
| SwapChainPanel Loaded 이벤트 타이밍 검증 | 단위 테스트 | 높음 |
| DPI 변경 시 깜박임 재현 및 수정 | Windows Terminal 코드 참고 | 높음 |
| IMM32 → TSF 마이그레이션 계획 수립 | TermControl.cpp TsfDataProvider 분석 | 중간 |
| WinUI3 Islands vs. 일반 WinUI3 창 결정 | 필요 시 검토 | 낮음 |

### 추가 발견된 리스크

| 리스크 | 심각도 | 근거 |
|--------|--------|------|
| `ISwapChainPanelNative` vs `ISwapChainPanelNative2` 선택 | **중간** | Terminal은 핸들 기반 Native2 사용. 어느 버전 선택할지 결정 필요 |
| GridSplitter WinUI3 미내장 | **낮음** | Community Toolkit 의존성 추가 필요 |
| SwapChainPanel + Mica 배경 중첩 불가 | **낮음** | 터미널 영역 배경은 D3D11로 직접 처리 |
| Windows App SDK 1.8 지원 종료 (2026-09-09) | **낮음** | MVP 출시 전에 대비 계획 수립 권장 |

---

## 9. 참고 문서 및 출처 목록

### 공식 문서
- [DirectX and XAML interop (UWP/WinUI3)](https://learn.microsoft.com/en-us/windows/uwp/gaming/directx-and-xaml-interop)
- [SwapChainPanel Class — Windows App SDK 1.8](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel?view=windows-app-sdk-1.8)
- [Windows App SDK Stable Channel Release Notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel)
- [App notifications quickstart](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart)
- [InfoBar control](https://learn.microsoft.com/en-us/windows/apps/design/controls/infobar)
- [Info badge control](https://learn.microsoft.com/en-us/windows/apps/design/controls/info-badge)
- [Title bar customization](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)
- [System backdrops (Mica/Acrylic)](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [Retrieve a window handle (HWND)](https://learn.microsoft.com/en-us/windows/apps/develop/ui/retrieve-hwnd)
- [CreatePseudoConsole](https://learn.microsoft.com/en-us/windows/console/createpseudoconsole)
- [Processing the WM_IME_COMPOSITION Message](https://learn.microsoft.com/en-us/windows/win32/intl/processing-the-wm-ime-composition-message)
- [IDXGIFactory2::CreateSwapChainForComposition](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition)
- [Text Services Framework](https://learn.microsoft.com/en-us/windows/win32/tsf/text-services-framework)

### 소스코드 레퍼런스
- [Windows Terminal — TermControl.cpp](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControl.cpp)
- [Windows Terminal — AtlasEngine.h](https://github.com/microsoft/terminal/blob/main/src/renderer/atlas/AtlasEngine.h)
- [Windows Terminal PR #10023 (DComp surface handle)](https://github.com/microsoft/terminal/pull/10023)
- [Atlas Engine — DeepWiki 분석](https://deepwiki.com/microsoft/terminal/3.2-atlas-engine)

### 커뮤니티 자료
- [Creating DirectX 11 SwapChain in WinUI 3.0 — juhakeranen.com](https://juhakeranen.com/winui3/directx-11-2-swap-chain.html)
- [Using SwapChainPanel in C++ WinUI3 — WindowsAppSDK Discussion #865](https://github.com/microsoft/WindowsAppSDK/discussions/865)
- [DirectX examples for C++/WinRT — WindowsAppSDK Discussion #3264](https://github.com/microsoft/WindowsAppSDK/discussions/3264)
- [Using NotifyIcon in WinUI 3 — Albert Akhmetov (2025)](https://albertakhmetov.com/posts/2025/using-notifyicon-in-winui-3/)
- [Add global hot key in WinUI 3 — Whid](https://whid.eu/2022/05/13/chapter-5-add-global-hot-key-in-winui-3/)
- [H.NotifyIcon.WinUI — NuGet](https://www.nuget.org/packages/H.NotifyIcon.WinUI)
- [NuGet: Microsoft.WindowsAppSDK 1.8.260317003](https://www.nuget.org/packages/Microsoft.WindowsAppSdk/)

---

*GhostWin Terminal — Research Document v1.0*
*작성: 2026-03-28*
