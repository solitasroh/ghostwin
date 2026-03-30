// GhostWin Terminal — WinUI3 Application implementation (Code-only)
// Phase 4: SwapChainPanel DX11 + TextBox IME composition

#include "app/winui_app.h"

#include <microsoft.ui.xaml.media.dxinterop.h>

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Microsoft.UI.Input.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>

#include <dxgi1_3.h>
#include <wrl/client.h>
#include <thread>
#include <chrono>

namespace winui = winrt::Microsoft::UI::Xaml;
namespace controls = winui::Controls;

using Microsoft::WRL::ComPtr;

namespace ghostwin {

// ─── Helper: send wstring to ConPTY as UTF-8 ───
void GhostWinApp::SendTextToTerminal(const std::wstring& text) {
    if (text.empty() || !m_session) return;
    int utf8_len = WideCharToMultiByte(CP_UTF8, 0,
        text.data(), static_cast<int>(text.size()),
        nullptr, 0, nullptr, nullptr);
    if (utf8_len > 0) {
        std::vector<char> utf8(utf8_len);
        WideCharToMultiByte(CP_UTF8, 0,
            text.data(), static_cast<int>(text.size()),
            utf8.data(), utf8_len, nullptr, nullptr);
        m_session->send_input({
            reinterpret_cast<uint8_t*>(utf8.data()),
            static_cast<size_t>(utf8_len)});
    }
}

// ─── OnLaunched ───
void GhostWinApp::OnLaunched(winui::LaunchActivatedEventArgs const&) {
    auto resources = controls::XamlControlsResources();
    winui::Application::Current().Resources().MergedDictionaries().Append(resources);

    m_window = winui::Window();
    m_window.Title(L"GhostWin Terminal");
    m_window.ExtendsContentIntoTitleBar(true);

    // Root Grid: 2-column (sidebar 220px + terminal stretch)
    auto grid = controls::Grid();
    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLengthHelper::FromPixels(220));
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLength{1, winui::GridUnitType::Star});
    grid.ColumnDefinitions().Append(col0);
    grid.ColumnDefinitions().Append(col1);

    // Sidebar placeholder
    auto sidebar = controls::ListView();
    controls::Grid::SetColumn(sidebar, 0);
    grid.Children().Append(sidebar);

    // SwapChainPanel (렌더링 전용 — 입력은 TextBox에서)
    m_panel = controls::SwapChainPanel();
    controls::Grid::SetColumn(m_panel, 1);
    grid.Children().Append(m_panel);

    // IME TextBox — 모든 키보드 입력의 진입점
    m_ime_textbox = controls::TextBox();
    m_ime_textbox.Width(1);
    m_ime_textbox.Height(1);
    m_ime_textbox.Opacity(0);
    m_ime_textbox.AcceptsReturn(false);
    m_ime_textbox.IsTabStop(true);
    controls::Grid::SetColumn(m_ime_textbox, 1);
    grid.Children().Append(m_ime_textbox);

    m_window.Content(grid);

    // Panel Loaded → D3D11 init
    m_panel.Loaded([self = get_strong()](auto&&, auto&&) {
        self->InitializeD3D11(self->m_panel);
    });

    // Panel 클릭 시 TextBox로 포커스 전달
    m_panel.PointerPressed([self = get_strong()](auto&&, auto&&) {
        self->m_ime_textbox.Focus(winui::FocusState::Programmatic);
    });

    // SizeChanged → debounce timer
    m_panel.SizeChanged([self = get_strong()](auto&&, winui::SizeChangedEventArgs const&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    // DPI change → same debounce
    m_panel.CompositionScaleChanged([self = get_strong()](
            controls::SwapChainPanel const&, auto&&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    // ─── 입력 처리: TextBox 이벤트 ───

    // 1. 특수키 + Ctrl 조합
    m_ime_textbox.PreviewKeyDown([self = get_strong()](auto&&,
            winui::Input::KeyRoutedEventArgs const& e) {
        using winrt::Windows::System::VirtualKey;
        if (!self->m_session) return;
        bool composing = self->m_composing.load(std::memory_order_acquire);

        // Ctrl+키 조합 (Ctrl+C=0x03, Ctrl+D=0x04, etc.)
        auto ctrlState = winrt::Microsoft::UI::Input::InputKeyboardSource::
            GetKeyStateForCurrentThread(VirtualKey::Control);
        bool ctrl = (static_cast<uint32_t>(ctrlState) &
                     static_cast<uint32_t>(winrt::Windows::UI::Core::CoreVirtualKeyStates::Down)) != 0;

        if (ctrl) {
            int key = static_cast<int>(e.Key());
            if (key >= static_cast<int>(VirtualKey::A) &&
                key <= static_cast<int>(VirtualKey::Z)) {
                uint8_t code = static_cast<uint8_t>(key - static_cast<int>(VirtualKey::A) + 1);
                self->m_session->send_input({&code, 1});
                e.Handled(true);
                return;
            }
        }

        const char* seq = nullptr;
        switch (e.Key()) {
        case VirtualKey::Up:      seq = "\033[A"; break;
        case VirtualKey::Down:    seq = "\033[B"; break;
        case VirtualKey::Right:   seq = "\033[C"; break;
        case VirtualKey::Left:    seq = "\033[D"; break;
        case VirtualKey::Home:    seq = "\033[H"; break;
        case VirtualKey::End:     seq = "\033[F"; break;
        case VirtualKey::Delete:  seq = "\033[3~"; break;
        case VirtualKey::Insert:  seq = "\033[2~"; break;
        case VirtualKey::PageUp:  seq = "\033[5~"; break;
        case VirtualKey::PageDown:seq = "\033[6~"; break;
        case VirtualKey::F1:      seq = "\033OP"; break;
        case VirtualKey::F2:      seq = "\033OQ"; break;
        case VirtualKey::F3:      seq = "\033OR"; break;
        case VirtualKey::F4:      seq = "\033OS"; break;
        case VirtualKey::F5:      seq = "\033[15~"; break;
        case VirtualKey::F6:      seq = "\033[17~"; break;
        case VirtualKey::F7:      seq = "\033[18~"; break;
        case VirtualKey::F8:      seq = "\033[19~"; break;
        case VirtualKey::F9:      seq = "\033[20~"; break;
        case VirtualKey::F10:     seq = "\033[21~"; break;
        case VirtualKey::F11:     seq = "\033[23~"; break;
        case VirtualKey::F12:     seq = "\033[24~"; break;
        case VirtualKey::Tab:     seq = "\t"; break;
        case VirtualKey::Enter:   seq = "\r"; break;
        case VirtualKey::Escape:
            // 조합 중 Escape → IME 취소를 TextBox에 위임
            if (composing) return;
            seq = "\033"; break;
        case VirtualKey::Back:
            // 조합 중 Backspace → IME가 처리 (자모 삭제)
            if (!composing) {
                uint8_t del = 0x7F;
                self->m_session->send_input({&del, 1});
            }
            return;  // TextBox에 위임
        default: break;
        }
        if (seq) {
            self->m_session->send_input({
                reinterpret_cast<const uint8_t*>(seq), strlen(seq)});
            e.Handled(true);
        }
    });

    // 2. IME 조합 시작
    m_ime_textbox.TextCompositionStarted([self = get_strong()](
            controls::TextBox const&, controls::TextCompositionStartedEventArgs const&) {
        self->m_composing.store(true, std::memory_order_release);
        LOG_I("ime", "Composition started");
    });

    // 3. IME 조합 변경 (ㄱ→가→간 실시간)
    m_ime_textbox.TextCompositionChanged([self = get_strong()](
            controls::TextBox const& sender,
            controls::TextCompositionChangedEventArgs const&) {
        auto text = sender.Text();
        std::wstring comp(text.c_str());
        {
            std::lock_guard lock(self->m_ime_mutex);
            self->m_composition = std::move(comp);
        }
        LOG_I("ime", "Composition: %ls", text.c_str());
    });

    // 4. IME 조합 완료 → ConPTY 전달
    m_ime_textbox.TextCompositionEnded([self = get_strong()](
            controls::TextBox const& sender,
            controls::TextCompositionEndedEventArgs const&) {
        auto text = sender.Text();
        if (!text.empty()) {
            std::wstring result(text.c_str());
            self->SendTextToTerminal(result);
            LOG_I("ime", "Committed: %ls", text.c_str());
        }
        // 상태 초기화
        self->m_composing.store(false, std::memory_order_release);
        {
            std::lock_guard lock(self->m_ime_mutex);
            self->m_composition.clear();
        }
        // TextBox 비우기 — DispatcherQueue로 비동기 처리하여
        // 다음 조합 시작과의 타이밍 충돌 방지
        self->m_window.DispatcherQueue().TryEnqueue([self]() {
            if (!self->m_composing.load(std::memory_order_acquire)) {
                self->m_ime_textbox.Text(L"");
            }
        });
    });

    // 5. 비-IME 문자 입력 (영문, 숫자, 특수문자)
    m_ime_textbox.TextChanged([self = get_strong()](auto&&,
            controls::TextChangedEventArgs const&) {
        // 조합 중이면 무시 (TextComposition 이벤트에서 처리)
        if (self->m_composing.load(std::memory_order_acquire)) return;

        auto text = self->m_ime_textbox.Text();
        if (text.empty()) return;

        std::wstring str(text.c_str());
        self->SendTextToTerminal(str);
        self->m_ime_textbox.Text(L"");
    });

    // ─── 타이머 ───

    // Cursor blink timer (530ms)
    m_blink_timer = winui::DispatcherTimer();
    m_blink_timer.Interval(std::chrono::milliseconds(530));
    m_blink_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_cursor_blink_visible.store(
            !self->m_cursor_blink_visible.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
    });
    m_blink_timer.Start();

    // Resize debounce timer (100ms)
    m_resize_timer = winui::DispatcherTimer();
    m_resize_timer.Interval(std::chrono::milliseconds(100));
    m_resize_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_resize_timer.Stop();
        float scaleX = self->m_panel.CompositionScaleX();
        float scaleY = self->m_panel.CompositionScaleY();
        uint32_t w = static_cast<uint32_t>(self->m_panel.ActualWidth() * scaleX);
        uint32_t h = static_cast<uint32_t>(self->m_panel.ActualHeight() * scaleY);
        self->m_pending_width.store(w > 0 ? w : 1, std::memory_order_release);
        self->m_pending_height.store(h > 0 ? h : 1, std::memory_order_release);
        self->m_resize_requested.store(true, std::memory_order_release);
    });

    // Mica 비활성화 — Grayscale AA 용 불투명 배경
    m_window.Activate();
    m_ime_textbox.Focus(winui::FocusState::Programmatic);
}

// ─── D3D11 초기화 ───
void GhostWinApp::InitializeD3D11(controls::SwapChainPanel const& panel) {
    float scaleX = panel.CompositionScaleX();
    float scaleY = panel.CompositionScaleY();
    float w = static_cast<float>(panel.ActualWidth()) * scaleX;
    float h = static_cast<float>(panel.ActualHeight()) * scaleY;
    if (w < 1.0f) w = 1.0f;
    if (h < 1.0f) h = 1.0f;

    CompositionConfig cfg;
    cfg.width = static_cast<uint32_t>(w);
    cfg.height = static_cast<uint32_t>(h);
    Error err{};
    m_renderer = DX11Renderer::create_for_composition(cfg, &err);
    if (!m_renderer) {
        LOG_E("winui", "Failed to create DX11 renderer: %s", err.message);
        return;
    }

    auto panelNative = panel.as<ISwapChainPanelNative>();
    ComPtr<IDXGISwapChain> sc;
    m_renderer->composition_swapchain()->QueryInterface(IID_PPV_ARGS(&sc));
    winrt::check_hresult(panelNative->SetSwapChain(sc.Get()));

    StartTerminal(cfg.width, cfg.height);
}

// ─── Terminal 시작 ───
void GhostWinApp::StartTerminal(uint32_t width_px, uint32_t height_px) {
    Error err{};

    AtlasConfig acfg;
    acfg.font_family = L"Cascadia Mono";
    acfg.font_size_pt = constants::kDefaultFontSizePt;
    m_atlas = GlyphAtlas::create(m_renderer->device(), acfg, &err);
    if (!m_atlas) {
        LOG_E("winui", "Failed to create glyph atlas: %s", err.message);
        return;
    }
    m_renderer->set_atlas_srv(m_atlas->srv());
    m_renderer->set_cleartype_params(
        m_atlas->enhanced_contrast(), m_atlas->gamma_ratios());

    uint16_t cols = static_cast<uint16_t>(width_px / m_atlas->cell_width());
    uint16_t rows = static_cast<uint16_t>(height_px / m_atlas->cell_height());
    if (cols < 1) cols = 1;
    if (rows < 1) rows = 1;

    LOG_I("winui", "Terminal %ux%u cells (cell=%ux%u)",
          cols, rows, m_atlas->cell_width(), m_atlas->cell_height());

    SessionConfig scfg;
    scfg.cols = cols;
    scfg.rows = rows;
    scfg.on_exit = [self = get_strong()](uint32_t code) {
        LOG_I("winui", "Child process exited with code %u", code);
        self->m_window.DispatcherQueue().TryEnqueue([self]() {
            self->ShutdownRenderThread();
            self->m_window.Close();
        });
    };
    m_session = ConPtySession::create(scfg);
    if (!m_session) {
        LOG_E("winui", "Failed to create ConPTY session");
        return;
    }

    m_state = std::make_unique<TerminalRenderState>(cols, rows);

    // +8: IME composition overlay margin
    m_staging.resize(
        static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1 + 8);

    m_render_running.store(true, std::memory_order_release);
    m_render_thread = std::thread([this] { RenderLoop(); });
}

// ─── IME 설정 (현재는 TextBox 이벤트 기반 — SetupImeSubclass 대체) ───
void GhostWinApp::SetupImeInput() {
    // TextBox 이벤트는 OnLaunched에서 이미 연결됨
    LOG_I("ime", "IME input via TextBox TextComposition events");
}

// ─── Shutdown ───
void GhostWinApp::ShutdownRenderThread() {
    m_render_running.store(false, std::memory_order_release);
    if (m_render_thread.joinable()) {
        m_render_thread.join();
    }
}

// ─── Render Loop ───
void GhostWinApp::RenderLoop() {
    QuadBuilder builder(
        m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

    while (m_render_running.load(std::memory_order_acquire)) {
        // Resize check
        if (m_resize_requested.load(std::memory_order_acquire)) {
            uint32_t w = m_pending_width.load(std::memory_order_acquire);
            uint32_t h = m_pending_height.load(std::memory_order_acquire);
            if (w == 0) w = 1;
            if (h == 0) h = 1;
            m_renderer->resize_swapchain(w, h);
            uint16_t cols = static_cast<uint16_t>(w / m_atlas->cell_width());
            uint16_t rows = static_cast<uint16_t>(h / m_atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            {
                std::lock_guard lock(m_vt_mutex);
                m_session->resize(cols, rows);
                m_state->resize(cols, rows);
            }
            m_staging.resize(
                static_cast<size_t>(cols) * rows *
                constants::kInstanceMultiplier + 1 + 8);
            builder.update_cell_size(
                m_atlas->cell_width(), m_atlas->cell_height());
            m_resize_requested.store(false, std::memory_order_release);
        }

        // Paint
        bool composing = m_composing.load(std::memory_order_acquire);
        bool dirty = m_state->start_paint(m_vt_mutex, m_session->vt_core());
        if (!dirty && !composing) {
            Sleep(1);
            continue;
        }
        if (composing && !dirty) {
            m_state->force_all_dirty();
            m_state->start_paint(m_vt_mutex, m_session->vt_core());
        }

        const auto& frame = m_state->frame();
        uint32_t count = builder.build(
            frame, *m_atlas, m_renderer->context(),
            std::span<QuadInstance>(m_staging));

        // IME 조합 중 문자 오버레이 렌더링
        if (composing) {
            std::wstring comp;
            {
                std::lock_guard lock(m_ime_mutex);
                comp = m_composition;
            }
            if (!comp.empty() && m_atlas) {
                auto cursor = frame.cursor;
                uint16_t col = cursor.x;
                uint16_t row = cursor.y;
                uint32_t cell_w = m_atlas->cell_width();
                uint32_t cell_h = m_atlas->cell_height();

                for (wchar_t ch : comp) {
                    if (count + 2 >= m_staging.size()) break;
                    uint32_t cp = static_cast<uint32_t>(ch);
                    bool is_wide = (cp >= 0xAC00 && cp <= 0xD7A3) ||
                                   (cp >= 0x1100 && cp <= 0x11FF);
                    uint16_t char_cells = is_wide ? 2 : 1;

                    // 배경 하이라이트
                    auto& bg = m_staging[count++];
                    bg = {};
                    bg.pos_x = static_cast<uint16_t>(col * cell_w);
                    bg.pos_y = static_cast<uint16_t>(row * cell_h);
                    bg.size_x = static_cast<uint16_t>(cell_w * char_cells);
                    bg.size_y = static_cast<uint16_t>(cell_h);
                    bg.bg_packed = 0xFF443344;
                    bg.fg_packed = 0xFF443344;
                    bg.shading_type = 0;

                    // 글리프
                    auto glyph = m_atlas->lookup_or_rasterize(
                        m_renderer->context(), cp, 0);
                    if (glyph.valid && glyph.width > 0) {
                        auto& fg = m_staging[count++];
                        fg = {};
                        fg.pos_x = static_cast<uint16_t>(
                            col * cell_w + glyph.offset_x);
                        fg.pos_y = static_cast<uint16_t>(
                            row * cell_h + m_atlas->baseline() + glyph.offset_y);
                        fg.size_x = static_cast<uint16_t>(glyph.width);
                        fg.size_y = static_cast<uint16_t>(glyph.height);
                        fg.tex_u = static_cast<uint16_t>(glyph.u);
                        fg.tex_v = static_cast<uint16_t>(glyph.v);
                        fg.tex_w = static_cast<uint16_t>(glyph.width);
                        fg.tex_h = static_cast<uint16_t>(glyph.height);
                        fg.fg_packed = 0xFFFFFFFF;
                        fg.bg_packed = 0xFF443344;
                        fg.shading_type = 1;
                    }
                    col += char_cells;
                }
            }
        }

        if (count > 0) {
            m_renderer->upload_and_draw(m_staging.data(), count);
        }
    }
}

} // namespace ghostwin
