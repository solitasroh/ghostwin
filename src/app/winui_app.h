#pragma once

// GhostWin Terminal — WinUI3 Application + Window (Code-only)
// Phase 4-A: SwapChainPanel DX11 integration

#include "renderer/dx11_renderer.h"
#include "renderer/glyph_atlas.h"
#include "renderer/render_state.h"
#include "renderer/quad_builder.h"
#include "conpty/conpty_session.h"
#include "common/log.h"

// Windows.h macro conflict with WinUI3
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

    // IXamlMetadataProvider — XAML 타입 메타데이터 (code-only 필수)
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
    // XAML metadata provider
    winrt::Microsoft::UI::Xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider m_provider;

    winrt::Microsoft::UI::Xaml::Window m_window{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel m_panel{nullptr};

    // Terminal components
    std::unique_ptr<DX11Renderer> m_renderer;
    std::unique_ptr<GlyphAtlas> m_atlas;
    std::unique_ptr<ConPtySession> m_session;
    std::unique_ptr<TerminalRenderState> m_state;

    // Render thread control (C1/C2: joinable, not detached)
    std::thread m_render_thread;
    std::atomic<bool> m_render_running{false};
    std::atomic<bool> m_resize_requested{false};
    std::atomic<uint32_t> m_pending_width{800};
    std::atomic<uint32_t> m_pending_height{600};

    // Mica backdrop
    winrt::Microsoft::UI::Composition::SystemBackdrops::MicaController m_mica_controller{nullptr};
    winrt::Microsoft::UI::Composition::SystemBackdrops::SystemBackdropConfiguration m_backdrop_config{nullptr};

    std::mutex m_vt_mutex;
    std::vector<QuadInstance> m_staging;

    // Timers
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_resize_timer{nullptr};
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_blink_timer{nullptr};
    std::atomic<bool> m_cursor_blink_visible{true};  // W3: atomic (UI/렌더 스레드 간)

    // IME (TextBox TextComposition — WinUI3 유일 실용적 방법)
    winrt::Microsoft::UI::Xaml::Controls::TextBox m_ime_textbox{nullptr};
    std::atomic<bool> m_composing{false};
    std::wstring m_composition;
    std::mutex m_ime_mutex;

    void InitializeD3D11(winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel const& panel);
    void StartTerminal(uint32_t width_px, uint32_t height_px);
    void ShutdownRenderThread();
    void RenderLoop();
    void SetupImeInput();
    void SendTextToTerminal(const std::wstring& text);
};

} // namespace ghostwin
