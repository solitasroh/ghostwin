// GhostWin Terminal — WinUI3 entry point (Code-only, Unpackaged)
// Bootstrap + Application::Start

#include <windows.h>

#include <WindowsAppSDK-VersionInfo.h>
#include <MddBootstrap.h>

#include <winrt/base.h>
#include <winrt/Microsoft.UI.Xaml.h>

#include "app/winui_app.h"

namespace MddBootstrap = ::Microsoft::Windows::ApplicationModel::DynamicDependency::Bootstrap;

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    winrt::init_apartment(winrt::apartment_type::single_threaded);

    // Bootstrap — Runtime 미설치 시 ShowUI로 설치 안내
    auto hr = MddBootstrap::InitializeNoThrow(
        WINDOWSAPPSDK_RELEASE_MAJORMINOR,
        WINDOWSAPPSDK_RELEASE_VERSION_TAG_W,
        WINDOWSAPPSDK_RUNTIME_VERSION_UINT64,
        MddBootstrap::InitializeOptions::OnNoMatch_ShowUI);

    if (FAILED(hr)) {
        MessageBoxW(nullptr,
            L"Windows App SDK Runtime\uC774 \uC124\uCE58\uB418\uC5B4 \uC788\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4.\n"
            L"https://aka.ms/windowsappsdk-download \uC5D0\uC11C \uC124\uCE58\uD574 \uC8FC\uC138\uC694.",
            L"GhostWin Terminal", MB_OK | MB_ICONERROR);
        return 1;
    }

    MddBootstrap::unique_mddbootstrapshutdown bootstrapGuard(
        reinterpret_cast<MddBootstrap::details::mddbootstrapshutdown_t*>(1));

    // Undocked RegFree WinRT — XAML activation factory 등록
    // MSBuild는 자동 처리하나 CMake 빌드에서는 수동 로드 필요 (intentional leak)
    HMODULE hRuntime = LoadLibraryW(L"Microsoft.WindowsAppRuntime.dll");
    if (hRuntime) {
        using EnsureLoadedFn = HRESULT(STDAPICALLTYPE*)();
        auto fn = reinterpret_cast<EnsureLoadedFn>(
            GetProcAddress(hRuntime, "WindowsAppRuntime_EnsureIsLoaded"));
        if (fn) {
            HRESULT hrLoad = fn();
            if (FAILED(hrLoad)) {
                wchar_t buf[256];
                swprintf_s(buf, L"WindowsAppRuntime_EnsureIsLoaded: 0x%08X",
                           (unsigned)hrLoad);
                MessageBoxW(nullptr, buf, L"GhostWin Terminal", MB_OK | MB_ICONWARNING);
            }
        }
    }

    winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) {
        winrt::make<ghostwin::GhostWinApp>();
    });

    return 0;
}
