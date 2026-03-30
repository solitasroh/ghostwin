# ADR-009: Code-only WinUI3 + CMake 빌드 필수 요소

- **상태**: 채택
- **날짜**: 2026-03-30
- **관련**: Phase 4-A winui3-shell, sotanakamura/winui3-without-xaml, microsoft-ui-xaml#5746

## 배경

GhostWin은 CMake + Ninja + MSVC 빌드 체인을 사용한다. WinUI3는 MSBuild + NuGet .targets가 자동 처리하는 초기화 단계가 있으나, CMake에서는 수동으로 해결해야 한다. XAML 파일 없이 코드로만 UI를 구성하는 Code-only 방식을 채택했으므로(Design Section 1.1), MSBuild의 XAML 컴파일러 의존성은 제거되지만 별도의 초기화 코드가 필요하다.

## 문제

`Application::Start()` 호출 시 프로세스가 `0xC000027B` (STATUS_STOWED_EXCEPTION)로 즉시 종료되었다. `RaiseFailFastException`이 내부적으로 호출되어 try-catch, SEH, `set_terminate` 모두 우회했다. Windows Event Viewer에서 `Microsoft.UI.Xaml.dll` 내부 크래시로 확인.

## 결정

CMake Code-only WinUI3 빌드에 다음 4가지를 필수 적용한다.

### 1. IXamlMetadataProvider 구현

```cpp
class GhostWinApp : public winrt::Microsoft::UI::Xaml::ApplicationT<
        GhostWinApp, markup::IXamlMetadataProvider> {
    // XamlControlsXamlMetaDataProvider에 위임
    winrt::Microsoft::UI::Xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider m_provider;
    markup::IXamlType GetXamlType(winrt::Windows::UI::Xaml::Interop::TypeName const& type) {
        return m_provider.GetXamlType(type);
    }
    // ...
};
```

MSBuild는 XAML 컴파일러가 자동 생성하는 `XamlMetaDataProvider`를 연결한다. Code-only에서는 `XamlControlsXamlMetaDataProvider`를 직접 위임해야 한다.

### 2. Undocked RegFree WinRT 수동 활성화

```cpp
HMODULE hRuntime = LoadLibraryW(L"Microsoft.WindowsAppRuntime.dll");
if (hRuntime) {
    auto fn = (HRESULT(STDAPICALLTYPE*)())
        GetProcAddress(hRuntime, "WindowsAppRuntime_EnsureIsLoaded");
    if (fn) fn();
}
```

MSBuild는 `UndockedRegFreeWinRT-AutoInitializer.cpp`를 자동 컴파일하여 WinRT activation factory를 등록한다. CMake에서는 Bootstrap 후 `WindowsAppRuntime_EnsureIsLoaded()`를 수동 호출한다. 모듈은 프로세스 수명 동안 유지(intentional leak).

### 3. GetCurrentTime 매크로 충돌 해결

```cpp
#undef GetCurrentTime  // windows.h와 WinUI3 DispatcherQueueTimer 충돌
```

`windows.h`의 `GetCurrentTime` 매크로가 `winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer::GetCurrentTime`과 충돌한다.

### 4. CppWinRT Projection 헤더 완전 생성

`setup_winui.ps1`에서 3단계로 생성:

1. **Windows SDK** winmd → `cppwinrt.exe -input <SDK UnionMetadata>`
2. **WinAppSDK** winmd (uap10.0 + uap10.0.18362) → WebView2 winmd를 `-ref`로
3. **WebView2** winmd → `Microsoft.UI.Xaml.Controls.h`가 WebView2 타입을 참조하므로 필수

## 대안 검토

| 대안 | 장점 | 단점 | 판정 |
|------|------|------|------|
| MSBuild 전환 | 자동 처리 | CMake+Ninja 빌드 체인 포기, libghostty-vt DLL 통합 복잡 | 기각 |
| Self-contained | Runtime 미설치 동작 | 50+ DLL 복사 필요, 배포 크기 증가 | 향후 검토 |
| XAML 파일 사용 | 표준 패턴 | midl.exe + XBF 의존성, CMake 통합 복잡 | 기각 |
| **현재 방식** | CMake 유지, 최소 변경 | 4가지 수동 처리 필요 | **채택** |

## 결과

- `Application::Start()` 정상 동작, WinUI3 창 + SwapChainPanel DX11 렌더링 성공
- Phase 3 HWND 기반 빌드(`ghostwin_terminal`) 영향 없음
- 진단 불가능한 fail-fast(0xC000027B)에 대해 Windows Event Viewer 기반 디버깅 절차 확립

## 참조

- [sotanakamura/winui3-without-xaml](https://github.com/sotanakamura/winui3-without-xaml)
- [microsoft-ui-xaml#5746](https://github.com/microsoft/microsoft-ui-xaml/issues/5746) — FrameworkApplication::StartDesktop crash
- [microsoft-ui-xaml#8151](https://github.com/microsoft/microsoft-ui-xaml/discussions/8151) — Step-by-step WinUI3 without XAML
- [RaiseFailFastException](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-raisefailfastexception) — 모든 예외 핸들러 우회
