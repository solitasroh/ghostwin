#pragma once

// GhostWin Terminal — WinUI3 Application + Window (Code-only)
// Phase 4-B: Hidden Win32 HWND + TSF 직접 구현
// WinUI3 InputSite와 분리된 HWND에서 IME/키보드 처리 (WT/Alacritty 패턴)

#include "renderer/dx11_renderer.h"
#include "renderer/glyph_atlas.h"
#include "renderer/render_state.h"
#include "renderer/quad_builder.h"
#include "session/session_manager.h"
#include "settings/settings_manager.h"
#include "settings/key_map.h"
#include "ui/tab_sidebar.h"
#include "ui/titlebar_manager.h"
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
    std::unique_ptr<settings::SettingsManager> m_settings;
    settings::KeyMap m_keymap;
    SessionManager m_session_mgr;
    TabSidebar m_tab_sidebar;
    TitleBarManager m_titlebar;

    // Phase 5-D: Settings Observer bridge (delegates to GhostWinApp methods)
    struct SettingsBridge : settings::ISettingsObserver {
        GhostWinApp* app = nullptr;
        void on_settings_changed(
            const settings::AppConfiguration& config,
            settings::ChangedFlags flags) override;
    };
    SettingsBridge m_settings_bridge;

    std::thread m_render_thread;
    std::atomic<bool> m_render_running{false};
    std::atomic<bool> m_resize_requested{false};
    std::atomic<uint32_t> m_pending_width{800};
    std::atomic<uint32_t> m_pending_height{600};

    // DPI-aware rendering (FR-05)
    std::atomic<float> m_current_dpi_scale{1.0f};
    std::atomic<float> m_pending_dpi_scale{1.0f};
    std::atomic<bool>  m_dpi_change_requested{false};

    std::vector<QuadInstance> m_staging;

    winrt::Microsoft::UI::Xaml::DispatcherTimer m_resize_timer{nullptr};
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_blink_timer{nullptr};
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_poll_timer{nullptr};  // Phase 5-B: title/CWD 2s poll
    std::atomic<bool> m_cursor_blink_visible{true};

    // ─── 입력: Hidden Win32 HWND ───
    HWND m_input_hwnd = nullptr;

    // TSF viewport/cursor callbacks (static, for SessionTsfAdapter function pointers)
    static RECT GetViewportRectStatic(void* ctx);
    static RECT GetCursorRectStatic(void* ctx);

    // Hidden HWND 생성 + WndProc
    void CreateInputHwnd(HWND parent);
    static LRESULT CALLBACK InputWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);
    bool HandleKeyDown(WPARAM vk);  // true = handled (eat message)

    // Phase 5-B: session creation extracted from [TEMP] Ctrl+T handler
    void create_new_session();

    // Grid column for sidebar width toggle (Ctrl+Shift+B)
    winrt::Microsoft::UI::Xaml::Controls::ColumnDefinition m_sidebar_col{nullptr};

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
