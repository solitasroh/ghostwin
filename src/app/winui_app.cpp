// GhostWin Terminal — WinUI3 Application implementation (Code-only)
// Phase 4-A: SwapChainPanel DX11 integration

#include "app/winui_app.h"

#include <microsoft.ui.xaml.media.dxinterop.h>
#include <microsoft.ui.xaml.window.h>

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

    // SwapChainPanel
    m_panel = controls::SwapChainPanel();
    m_panel.IsTabStop(true);
    controls::Grid::SetColumn(m_panel, 1);
    grid.Children().Append(m_panel);

    m_window.Content(grid);

    // Panel Loaded -> D3D11 init
    m_panel.Loaded([self = get_strong()](auto&&, auto&&) {
        self->InitializeD3D11(self->m_panel);
    });

    // SizeChanged -> debounce timer
    m_panel.SizeChanged([self = get_strong()](auto&&, winui::SizeChangedEventArgs const&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    // DPI change -> same debounce timer (W2: 디바운스 경유)
    m_panel.CompositionScaleChanged([self = get_strong()](
            controls::SwapChainPanel const&, auto&&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    // Keyboard: special keys
    m_panel.KeyDown([self = get_strong()](auto&&,
            winui::Input::KeyRoutedEventArgs const& e) {
        using winrt::Windows::System::VirtualKey;
        const char* seq = nullptr;
        switch (e.Key()) {
        case VirtualKey::Up:     seq = "\033[A"; break;
        case VirtualKey::Down:   seq = "\033[B"; break;
        case VirtualKey::Right:  seq = "\033[C"; break;
        case VirtualKey::Left:   seq = "\033[D"; break;
        case VirtualKey::Home:   seq = "\033[H"; break;
        case VirtualKey::End:    seq = "\033[F"; break;
        case VirtualKey::Delete: seq = "\033[3~"; break;
        default: break;
        }
        if (seq && self->m_session) {
            self->m_session->send_input({
                reinterpret_cast<const uint8_t*>(seq), strlen(seq)});
            e.Handled(true);
        }
    });

    // Keyboard: character input (incl. surrogate pairs)
    m_panel.CharacterReceived([self = get_strong()](auto&&,
            winui::Input::CharacterReceivedRoutedEventArgs const& e) {
        if (!self->m_session) return;
        wchar_t ch = e.Character();

        // IME 조합 중이면 한글 + Backspace를 무시 (IMM32가 직접 처리)
        if (self->m_composing.load(std::memory_order_acquire)) {
            // 한글 완성형 + 자모 + Backspace(조합 취소)
            if ((ch >= 0xAC00 && ch <= 0xD7A3) ||
                (ch >= 0x3131 && ch <= 0x3163) ||
                ch == 0x08) {  // Backspace — IME가 조합 문자 수정
                e.Handled(true);
                return;
            }
        }

        // Surrogate pair buffering (emoji)
        if (ch >= 0xD800 && ch <= 0xDBFF) {
            self->m_high_surrogate = ch;
            e.Handled(true);
            return;
        }
        if (ch >= 0xDC00 && ch <= 0xDFFF && self->m_high_surrogate != 0) {
            wchar_t pair[2] = { self->m_high_surrogate, ch };
            self->m_high_surrogate = 0;
            char utf8[8];
            int len = WideCharToMultiByte(CP_UTF8, 0, pair, 2,
                                          utf8, sizeof(utf8), nullptr, nullptr);
            if (len > 0) {
                self->m_session->send_input({
                    reinterpret_cast<uint8_t*>(utf8), static_cast<size_t>(len)});
            }
            e.Handled(true);
            return;
        }
        self->m_high_surrogate = 0;

        // Control characters
        if (ch < 0x20) {
            uint8_t byte = (ch == 0x08) ? 0x7F : static_cast<uint8_t>(ch);
            self->m_session->send_input({&byte, 1});
        } else {
            char utf8[4];
            int len = WideCharToMultiByte(CP_UTF8, 0, &ch, 1,
                                          utf8, sizeof(utf8), nullptr, nullptr);
            if (len > 0) {
                self->m_session->send_input({
                    reinterpret_cast<uint8_t*>(utf8), static_cast<size_t>(len)});
            }
        }
        e.Handled(true);
    });

    // Cursor blink timer (530ms)
    m_blink_timer = winui::DispatcherTimer();
    m_blink_timer.Interval(std::chrono::milliseconds(530));
    m_blink_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_cursor_blink_visible.store(
            !self->m_cursor_blink_visible.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
    });
    m_blink_timer.Start();

    // Resize debounce timer (100ms, DPI-aware: 물리 픽셀 단위)
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

    // Mica backdrop 비활성화 — ALPHA_MODE_IGNORE에서 반투명 배경 불가
    // ClearType 서브픽셀 블렌딩은 불투명 배경 필수 (WT/Alacritty 동일)

    m_window.Activate();
    m_panel.Focus(winui::FocusState::Programmatic);
}

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

void GhostWinApp::StartTerminal(uint32_t width_px, uint32_t height_px) {
    Error err{};

    // GlyphAtlas
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

    // ConPTY session
    SessionConfig scfg;
    scfg.cols = cols;
    scfg.rows = rows;
    // C4: on_exit에서 렌더 스레드 정지 후 Close
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

    // RenderState
    m_state = std::make_unique<TerminalRenderState>(cols, rows);

    // Staging buffer
    m_staging.resize(
        static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1);

    // C1/C2: joinable thread (detach 금지)
    m_render_running.store(true, std::memory_order_release);
    m_render_thread = std::thread([this] { RenderLoop(); });

    // IME 서브클래스 등록
    SetupImeSubclass();
}

void GhostWinApp::ShutdownRenderThread() {
    m_render_running.store(false, std::memory_order_release);
    if (m_render_thread.joinable()) {
        m_render_thread.join();
    }
}

void GhostWinApp::RenderLoop() {
    QuadBuilder builder(
        m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

    while (m_render_running.load(std::memory_order_acquire)) {
        // C3: resize는 렌더 스레드 내에서만 실행 — D3D context 단일 스레드 접근 보장
        // UI 스레드는 atomic flag만 설정하고 렌더 스레드가 처리
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
                constants::kInstanceMultiplier + 1);
            builder.update_cell_size(
                m_atlas->cell_width(), m_atlas->cell_height());
            m_resize_requested.store(false, std::memory_order_release);
        }

        // Normal render
        bool composing = m_composing.load(std::memory_order_acquire);
        bool dirty = m_state->start_paint(m_vt_mutex, m_session->vt_core());
        if (!dirty && !composing) {
            Sleep(1);
            continue;
        }
        // 조합 중에는 전체 프레임을 강제 렌더 (오버레이가 프레임 위에 그려져야 함)
        if (composing && !dirty) {
            m_state->force_all_dirty();
            m_state->start_paint(m_vt_mutex, m_session->vt_core());
        }

        const auto& frame = m_state->frame();
        uint32_t count = builder.build(
            frame, *m_atlas, m_renderer->context(),
            std::span<QuadInstance>(m_staging));

        // IME 조합 중 문자 오버레이 렌더링
        if (composing) {  // 이미 위에서 로드한 값 재사용
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

                    // 배경 (조합 중 하이라이트)
                    auto& bg = m_staging[count++];
                    bg = {};
                    bg.pos_x = static_cast<uint16_t>(col * cell_w);
                    bg.pos_y = static_cast<uint16_t>(row * cell_h);
                    // 한글 완성형/자모에 따라 1cell 또는 2cell
                    bool is_wide = (cp >= 0xAC00 && cp <= 0xD7A3) ||
                                   (cp >= 0x1100 && cp <= 0x11FF);
                    uint16_t char_cells = is_wide ? 2 : 1;
                    bg.size_x = static_cast<uint16_t>(cell_w * char_cells);
                    bg.size_y = static_cast<uint16_t>(cell_h);
                    bg.bg_packed = 0xFF443344;  // 디버그: 밝은 빨간색
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
                            row * cell_h + (m_atlas->baseline() - glyph.offset_y));
                        fg.size_x = static_cast<uint16_t>(glyph.width);
                        fg.size_y = static_cast<uint16_t>(glyph.height);
                        fg.tex_u = static_cast<uint16_t>(glyph.u);
                        fg.tex_v = static_cast<uint16_t>(glyph.v);
                        fg.tex_w = static_cast<uint16_t>(glyph.width);
                        fg.tex_h = static_cast<uint16_t>(glyph.height);
                        fg.fg_packed = 0xFFFFFFFF;  // 흰색 전경
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

// ─── IME (Korean hangul input — IMM32 + HWND subclass) ───

void GhostWinApp::SetupImeSubclass() {
    auto windowNative = m_window.as<IWindowNative>();
    winrt::check_hresult(windowNative->get_WindowHandle(&m_hwnd));

    SetWindowSubclass(m_hwnd, ImeSubclassProc, 1,
                      reinterpret_cast<DWORD_PTR>(this));
    LOG_I("winui", "IME subclass registered (HWND=%p)", m_hwnd);
}

LRESULT CALLBACK GhostWinApp::ImeSubclassProc(
        HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam,
        UINT_PTR /*id*/, DWORD_PTR refData) {
    auto* app = reinterpret_cast<GhostWinApp*>(refData);

    switch (msg) {
    case WM_IME_STARTCOMPOSITION:
        LOG_I("ime", "WM_IME_STARTCOMPOSITION received");
        app->OnImeStartComposition();
        return 0;
    case WM_IME_COMPOSITION:
        LOG_I("ime", "WM_IME_COMPOSITION lParam=0x%08lX", (unsigned long)lParam);
        app->OnImeComposition(hwnd, lParam);
        return 0;
    case WM_IME_ENDCOMPOSITION:
        LOG_I("ime", "WM_IME_ENDCOMPOSITION");
        app->OnImeEndComposition();
        return 0;
    case WM_NCDESTROY:
        RemoveWindowSubclass(hwnd, ImeSubclassProc, 1);
        break;
    }
    return DefSubclassProc(hwnd, msg, wParam, lParam);
}

void GhostWinApp::OnImeStartComposition() {
    m_composing.store(true, std::memory_order_release);

    HIMC hImc = ImmGetContext(m_hwnd);
    if (hImc && m_state && m_atlas) {
        // cursor 좌표는 셀 단위 → 픽셀 변환
        // WinUI3 창의 클라이언트 좌표 (물리 픽셀)
        auto cursor = m_state->frame().cursor;
        COMPOSITIONFORM cf{};
        cf.dwStyle = CFS_POINT;
        cf.ptCurrentPos.x = static_cast<LONG>(cursor.x * m_atlas->cell_width());
        cf.ptCurrentPos.y = static_cast<LONG>(cursor.y * m_atlas->cell_height());
        ImmSetCompositionWindow(hImc, &cf);
        ImmReleaseContext(m_hwnd, hImc);
    }
}

void GhostWinApp::OnImeComposition(HWND hwnd, LPARAM lParam) {
    HIMC hImc = ImmGetContext(hwnd);
    if (!hImc) return;

    if (lParam & GCS_RESULTSTR) {
        // 확정 문자 → ConPTY
        LONG bytes = ImmGetCompositionStringW(hImc, GCS_RESULTSTR, nullptr, 0);
        if (bytes > 0) {
            std::wstring result(bytes / sizeof(wchar_t), L'\0');
            ImmGetCompositionStringW(hImc, GCS_RESULTSTR, result.data(), bytes);
            // 동적 UTF-8 버퍼 (다중 음절 안전)
            int utf8_len = WideCharToMultiByte(CP_UTF8, 0,
                result.data(), static_cast<int>(result.size()),
                nullptr, 0, nullptr, nullptr);
            if (utf8_len > 0 && m_session) {
                std::vector<char> utf8(utf8_len);
                WideCharToMultiByte(CP_UTF8, 0,
                    result.data(), static_cast<int>(result.size()),
                    utf8.data(), utf8_len, nullptr, nullptr);
                m_session->send_input({
                    reinterpret_cast<uint8_t*>(utf8.data()),
                    static_cast<size_t>(utf8_len)});
            }
        }
    }

    if (lParam & GCS_COMPSTR) {
        // 조합 중 문자 → 렌더링용 저장
        LONG bytes = ImmGetCompositionStringW(hImc, GCS_COMPSTR, nullptr, 0);
        std::wstring comp;
        if (bytes > 0) {
            comp.resize(bytes / sizeof(wchar_t));
            ImmGetCompositionStringW(hImc, GCS_COMPSTR, comp.data(), bytes);
        }
        {
            std::lock_guard lock(m_ime_mutex);
            m_composition = std::move(comp);
        }
    }

    ImmReleaseContext(hwnd, hImc);
}

void GhostWinApp::OnImeEndComposition() {
    m_composing.store(false, std::memory_order_release);
    {
        std::lock_guard lock(m_ime_mutex);
        m_composition.clear();
    }
}

} // namespace ghostwin
