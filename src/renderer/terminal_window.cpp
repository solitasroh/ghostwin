/// @file terminal_window.cpp
/// Win32 terminal window: message loop, keyboard -> ConPTY, resize debounce,
/// render thread with start_paint -> QuadBuilder -> GPU draw.

#include "terminal_window.h"
#include "dx11_renderer.h"
#include "render_state.h"
#include "glyph_atlas.h"
#include "quad_builder.h"
#include "conpty/conpty_session.h"
#include "vt-core/vt_core.h"
#include "common/log.h"

#include <d3d11.h>
#include <thread>
#include <atomic>
#include <mutex>
#include <vector>
#include <cstring>

namespace ghostwin {

static constexpr UINT_PTR kResizeTimerId = 1;
static constexpr UINT_PTR kBlinkTimerId = 2;

struct TerminalWindow::Impl {
    HWND hwnd = nullptr;
    uint32_t cell_w = 0;
    uint32_t cell_h = 0;

    // Externally owned, set in run()
    ConPtySession* session = nullptr;
    DX11Renderer* renderer = nullptr;
    TerminalRenderState* state = nullptr;
    GlyphAtlas* atlas = nullptr;

    // Render thread
    std::thread render_thread;
    std::atomic<bool> stop_flag{false};
    std::mutex vt_mutex;  // protects VtCore access

    // Staging buffer (pre-allocated)
    std::vector<QuadInstance> staging;

    // Cursor blink
    bool cursor_blink_visible = true;

    void render_loop();
    void on_resize();
    void send_key_input(const uint8_t* data, size_t len);
};

// ─── Render thread loop ───

void TerminalWindow::Impl::render_loop() {
    LOG_I("render", "Render thread started");
    QuadBuilder builder(cell_w, cell_h);

    while (!stop_flag.load(std::memory_order_relaxed)) {
        // 1. start_paint: update from VtCore + dirty-row copy
        bool dirty = state->start_paint(vt_mutex, session->vt_core());
        if (!dirty) {
            Sleep(1);  // yield CPU when idle
            continue;
        }

        // 2. Build QuadInstances
        const auto& frame = state->frame();
        uint32_t count = builder.build(
            frame, *atlas, renderer->context(),
            std::span<QuadInstance>(staging));

        if (count == 0) continue;

        // 3. Upload to GPU + draw + present
        renderer->upload_and_draw(staging.data(), count);
    }
    LOG_I("render", "Render thread stopped");
}

// ─── WndProc ───

LRESULT CALLBACK TerminalWindow::wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));

    switch (msg) {
    case WM_CREATE: {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(lp);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
        return 0;
    }

    case WM_CHAR: {
        if (!self || !self->session) break;
        // UTF-16 -> UTF-8 conversion
        wchar_t wch = (wchar_t)wp;
        char utf8[4];
        int len = WideCharToMultiByte(CP_UTF8, 0, &wch, 1, utf8, sizeof(utf8), nullptr, nullptr);
        if (len > 0) {
            self->send_key_input((const uint8_t*)utf8, len);
        }
        return 0;
    }

    case WM_KEYDOWN: {
        if (!self || !self->session) break;
        // Special keys -> VT sequences
        const char* seq = nullptr;
        switch (wp) {
        case VK_UP:     seq = "\033[A"; break;
        case VK_DOWN:   seq = "\033[B"; break;
        case VK_RIGHT:  seq = "\033[C"; break;
        case VK_LEFT:   seq = "\033[D"; break;
        case VK_HOME:   seq = "\033[H"; break;
        case VK_END:    seq = "\033[F"; break;
        case VK_DELETE: seq = "\033[3~"; break;
        case VK_BACK:   { uint8_t bs = 0x7F; self->send_key_input(&bs, 1); return 0; }
        case VK_TAB:    { uint8_t tab = '\t'; self->send_key_input(&tab, 1); return 0; }
        case VK_RETURN: { uint8_t cr = '\r'; self->send_key_input(&cr, 1); return 0; }
        }
        if (seq) {
            self->send_key_input((const uint8_t*)seq, strlen(seq));
            return 0;
        }
        break;
    }

    case WM_SIZE: {
        if (!self || wp == SIZE_MINIMIZED) break;
        KillTimer(hwnd, kResizeTimerId);
        SetTimer(hwnd, kResizeTimerId, constants::kResizeDebounceMs, nullptr);
        return 0;
    }

    case WM_TIMER: {
        if (!self) break;
        if (wp == kResizeTimerId) {
            KillTimer(hwnd, kResizeTimerId);
            self->on_resize();
            return 0;
        }
        if (wp == kBlinkTimerId) {
            self->cursor_blink_visible = !self->cursor_blink_visible;
            return 0;
        }
        break;
    }

    case WM_SETFOCUS:
        if (self) {
            UINT blink = GetCaretBlinkTime();
            if (blink != INFINITE) {
                SetTimer(hwnd, kBlinkTimerId, blink, nullptr);
            }
        }
        return 0;

    case WM_KILLFOCUS:
        KillTimer(hwnd, kBlinkTimerId);
        return 0;

    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ─── Resize ───

void TerminalWindow::Impl::on_resize() {
    RECT rc;
    GetClientRect(hwnd, &rc);
    uint32_t w = rc.right - rc.left;
    uint32_t h = rc.bottom - rc.top;
    if (w == 0 || h == 0 || cell_w == 0 || cell_h == 0) return;

    uint16_t new_cols = (uint16_t)(w / cell_w);
    uint16_t new_rows = (uint16_t)(h / cell_h);
    if (new_cols == 0) new_cols = 1;
    if (new_rows == 0) new_rows = 1;

    // Stop render thread
    stop_flag.store(true, std::memory_order_release);
    if (render_thread.joinable()) render_thread.join();

    // Resize swapchain
    renderer->resize_swapchain(w, h);

    // Resize terminal + state
    {
        std::lock_guard lock(vt_mutex);
        (void)session->resize(new_cols, new_rows);
        state->resize(new_cols, new_rows);
    }

    // Resize staging buffer
    staging.resize(static_cast<size_t>(new_cols) * new_rows * constants::kInstanceMultiplier + 1);

    // Restart render thread
    stop_flag.store(false, std::memory_order_release);
    render_thread = std::thread([this] { render_loop(); });

    LOG_I("window", "Resized to %ux%u (%ux%u cells)", w, h, new_cols, new_rows);
}

// ─── Send input ───

void TerminalWindow::Impl::send_key_input(const uint8_t* data, size_t len) {
    (void)session->send_input({data, len});
}

// ─── Public API ───

TerminalWindow::TerminalWindow() : impl_(std::make_unique<Impl>()) {}
TerminalWindow::~TerminalWindow() {
    if (impl_->render_thread.joinable()) {
        impl_->stop_flag.store(true);
        impl_->render_thread.join();
    }
}

std::unique_ptr<TerminalWindow> TerminalWindow::create(const WindowConfig& config) {
    auto win = std::unique_ptr<TerminalWindow>(new TerminalWindow());

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"GhostWinTerminal";
    RegisterClassExW(&wc);

    win->impl_->hwnd = CreateWindowExW(
        WS_EX_NOREDIRECTIONBITMAP,
        wc.lpszClassName,
        config.title,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT,
        800, 600,
        nullptr, nullptr, wc.hInstance, win->impl_.get());

    if (!win->impl_->hwnd) {
        LOG_E("window", "CreateWindowExW failed: %lu", GetLastError());
        return nullptr;
    }

    return win;
}

int TerminalWindow::run(ConPtySession& session, DX11Renderer& renderer,
                         TerminalRenderState& state, GlyphAtlas& atlas) {
    impl_->session = &session;
    impl_->renderer = &renderer;
    impl_->state = &state;
    impl_->atlas = &atlas;
    impl_->cell_w = atlas.cell_width();
    impl_->cell_h = atlas.cell_height();

    // Pre-allocate staging buffer
    uint16_t cols = session.cols();
    uint16_t rows = session.rows();
    impl_->staging.resize(
        static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1);

    ShowWindow(impl_->hwnd, SW_SHOW);
    UpdateWindow(impl_->hwnd);

    // Start render thread
    impl_->stop_flag.store(false);
    impl_->render_thread = std::thread([this] { impl_->render_loop(); });

    // Cursor blink timer
    UINT blink = GetCaretBlinkTime();
    if (blink != INFINITE) {
        SetTimer(impl_->hwnd, kBlinkTimerId, blink, nullptr);
    }

    // Message loop
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    // Stop render thread
    impl_->stop_flag.store(true, std::memory_order_release);
    if (impl_->render_thread.joinable()) {
        impl_->render_thread.join();
    }

    return (int)msg.wParam;
}

HWND TerminalWindow::hwnd() const { return impl_->hwnd; }

} // namespace ghostwin
