#pragma once

// GhostWin Terminal — WinUI3 Application + Window (Code-only)
// Phase 4-B: Hidden Win32 HWND + TSF 직접 구현
// WinUI3 InputSite와 분리된 HWND에서 IME/키보드 처리 (WT/Alacritty 패턴)

#include "renderer/dx11_renderer.h"
#include "renderer/glyph_atlas.h"
#include "renderer/render_state.h"
#include "renderer/quad_builder.h"
#include "conpty/conpty_session.h"
#include "tsf/tsf_handle.h"
#include "common/log.h"

#undef GetCurrentTime

#include <winrt/base.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Composition.SystemBackdrops.h>
#include <winrt/Microsoft.UI.Composition.h>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.XamlTypeInfo.h>

#include <microsoft.ui.xaml.window.h>

#include <atomic>
#include <mutex>
#include <thread>
#include <vector>

namespace ghostwin {

namespace markup = winrt::Microsoft::UI::Xaml::Markup;

class GhostWinApp : public winrt::Microsoft::UI::Xaml::ApplicationT<
        GhostWinApp, markup::IXamlMetadataProvider> {
public:
    void OnLaunched(winrt::Microsoft::UI::Xaml::LaunchActivatedEventArgs const&);

    markup::IXamlType GetXamlType(winrt::Windows::UI::Xaml::Interop::TypeName const& type) {
        return m_provider.GetXamlType(type);
    }
    markup::IXamlType GetXamlType(winrt::hstring const& fullName) {
        return m_provider.GetXamlType(fullName);
    }
    winrt::com_array<markup::XmlnsDefinition> GetXmlnsDefinitions() {
        return m_provider.GetXmlnsDefinitions();
    }

private:
    winrt::Microsoft::UI::Xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider m_provider;
    winrt::Microsoft::UI::Xaml::Window m_window{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel m_panel{nullptr};

    std::unique_ptr<DX11Renderer> m_renderer;
    std::unique_ptr<GlyphAtlas> m_atlas;
    std::unique_ptr<ConPtySession> m_session;
    std::unique_ptr<TerminalRenderState> m_state;

    std::thread m_render_thread;
    std::atomic<bool> m_render_running{false};
    std::atomic<bool> m_resize_requested{false};
    std::atomic<uint32_t> m_pending_width{800};
    std::atomic<uint32_t> m_pending_height{600};

    // DPI-aware rendering (FR-05)
    std::atomic<float> m_current_dpi_scale{1.0f};
    std::atomic<float> m_pending_dpi_scale{1.0f};
    std::atomic<bool>  m_dpi_change_requested{false};

    std::mutex m_vt_mutex;
    std::vector<QuadInstance> m_staging;

    winrt::Microsoft::UI::Xaml::DispatcherTimer m_resize_timer{nullptr};
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_blink_timer{nullptr};
    std::atomic<bool> m_cursor_blink_visible{true};

    // ─── 입력: Hidden Win32 HWND + TSF ───
    HWND m_input_hwnd = nullptr;   // 입력 전용 hidden child HWND
    TsfHandle m_tsf;
    std::wstring m_composition;    // 조합 중 문자열 (렌더 스레드 공유)
    std::mutex m_ime_mutex;
    wchar_t m_pending_high_surrogate = 0;  // 서로게이트 쌍 결합용

    // TSF IDataProvider 어댑터
    struct TsfDataAdapter : IDataProvider {
        GhostWinApp* app = nullptr;
        HWND GetHwnd() override;
        RECT GetViewport() override;
        RECT GetCursorPosition() override;
        void HandleOutput(std::wstring_view text) override;
        void HandleCompositionUpdate(const CompositionPreview& preview) override;
    };
    TsfDataAdapter m_tsf_data;

    // Hidden HWND 생성 + WndProc
    void CreateInputHwnd(HWND parent);
    static LRESULT CALLBACK InputWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);
    bool HandleKeyDown(WPARAM vk);  // true = handled (eat message)

    void InitializeD3D11(winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel const& panel);
    void StartTerminal(uint32_t width_px, uint32_t height_px);
    void ShutdownRenderThread();
    void RenderLoop();

    void SendUtf8(const std::wstring& text);
    void SendVt(const char* seq);
    void PasteFromClipboard();
    HWND GetWindowHwnd();

    // --test-ime 자동 테스트 모드
    bool m_test_mode = false;
    std::thread m_test_thread;
    static void RunImeTest(GhostWinApp* app);
};

} // namespace ghostwin
