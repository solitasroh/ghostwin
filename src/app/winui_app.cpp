// GhostWin Terminal — WinUI3 Application implementation (Code-only)
// Phase 4-B: Hidden Win32 HWND + TSF 직접 구현
//
// 아키텍처:
//   Hidden Win32 HWND (WinUI3 InputSite와 분리) → IME/키보드 입력
//   TSF AssociateFocus(m_input_hwnd) → 한글 IME 콜백
//   WM_CHAR → 비-IME 문자 (영문, 숫자, 기호)
//   WM_KEYDOWN → 특수키 (VT 시퀀스, Ctrl+키)
//   IME 활성 시: VK_PROCESSKEY(229) → TSF가 처리 → 콜백

#include "app/winui_app.h"
#include "vt-core/vt_bridge.h"  // VT_MODE_DECCKM, VT_MODE_BRACKETED_PASTE

#include <microsoft.ui.xaml.media.dxinterop.h>

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Microsoft.UI.Input.h>
#include <winrt/Microsoft.UI.Windowing.h>

#include <dxgi1_3.h>
#include <wrl/client.h>
#include <imm.h>
#include <cstdio>
#include <cmath>

namespace winui = winrt::Microsoft::UI::Xaml;
namespace controls = winui::Controls;
using Microsoft::WRL::ComPtr;

namespace ghostwin {

// ─── SendUtf8: wstring → UTF-8 → ConPTY ───
void GhostWinApp::SendUtf8(const std::wstring& text) {
    if (text.empty() || !m_session) return;
    int len = WideCharToMultiByte(CP_UTF8, 0, text.data(),
        static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (len > 0) {
        char buf[64];
        char* p = (len <= 64) ? buf : new char[len];
        WideCharToMultiByte(CP_UTF8, 0, text.data(),
            static_cast<int>(text.size()), p, len, nullptr, nullptr);
        m_session->send_input({reinterpret_cast<uint8_t*>(p),
                               static_cast<size_t>(len)});
        if (p != buf) delete[] p;
    }
}

void GhostWinApp::SendVt(const char* seq) {
    if (m_session)
        m_session->send_input({reinterpret_cast<const uint8_t*>(seq), strlen(seq)});
}

void GhostWinApp::PasteFromClipboard() {
    if (!m_session) return;
    if (!OpenClipboard(m_input_hwnd)) {
        LOG_W("winui", "PasteFromClipboard: OpenClipboard failed");
        return;
    }

    HANDLE hData = GetClipboardData(CF_UNICODETEXT);
    if (!hData) {
        CloseClipboard();
        return;
    }

    wchar_t* pData = static_cast<wchar_t*>(GlobalLock(hData));
    if (!pData) {
        CloseClipboard();
        return;
    }

    std::wstring raw(pData);
    GlobalUnlock(hData);
    CloseClipboard();

    if (raw.empty()) return;

    // \r\n → \r 변환 (터미널은 CR만 사용)
    std::wstring text;
    text.reserve(raw.size());
    for (size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] == '\r' && i + 1 < raw.size() && raw[i + 1] == '\n') {
            text += L'\r';
            ++i;
        } else if (raw[i] == '\n') {
            text += L'\r';
        } else {
            text += raw[i];
        }
    }

    // Bracket paste mode (DEC Private Mode 2004): 붙여넣기 텍스트를 마커로 래핑
    // bash, vim 등이 \033[?2004h 로 활성화 → 프로그램이 붙여넣기 vs 타이핑 구분 가능
    bool bracket = m_session && m_session->vt_core().mode_get(VT_MODE_BRACKETED_PASTE);
    if (bracket) SendVt("\033[200~");
    SendUtf8(text);
    if (bracket) SendVt("\033[201~");
    LOG_I("winui", "PasteFromClipboard: %zu chars (bracket=%d)", text.size(), bracket);
}

// ─── GetWindowHwnd: WinUI3 Window → native HWND ───
HWND GhostWinApp::GetWindowHwnd() {
    if (!m_window) return nullptr;
    auto windowNative = m_window.as<::IWindowNative>();
    HWND hwnd = nullptr;
    windowNative->get_WindowHandle(&hwnd);
    return hwnd;
}

// ─── Hidden Win32 HWND (입력 전용) ───

static const wchar_t* kInputWindowClass = L"GhostWinInput";
static bool g_input_class_registered = false;

void GhostWinApp::CreateInputHwnd(HWND parent) {
    if (!g_input_class_registered) {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = InputWndProc;
        wc.hInstance = GetModuleHandle(nullptr);
        wc.lpszClassName = kInputWindowClass;
        RegisterClassExW(&wc);
        g_input_class_registered = true;
    }

    m_input_hwnd = CreateWindowExW(
        0, kInputWindowClass, L"",
        WS_CHILD,  // child window, hidden
        0, 0, 1, 1,
        parent, nullptr, GetModuleHandle(nullptr), this);

    if (m_input_hwnd) {
        LOG_I("winui", "Input HWND created: %p (parent=%p)", m_input_hwnd, parent);
    } else {
        LOG_E("winui", "Failed to create input HWND: %lu", GetLastError());
    }
}

LRESULT CALLBACK GhostWinApp::InputWndProc(
        HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_CREATE) {
        auto* cs = reinterpret_cast<CREATESTRUCT*>(lp);
        SetWindowLongPtr(hwnd, GWLP_USERDATA,
                         reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
        return 0;
    }

    auto* self = reinterpret_cast<GhostWinApp*>(
        GetWindowLongPtr(hwnd, GWLP_USERDATA));
    if (!self || !self->m_session)
        return DefWindowProcW(hwnd, msg, wp, lp);

    switch (msg) {
    // ─── IME 메시지 진단 (Phase 1) ───
    case WM_IME_STARTCOMPOSITION:
        LOG_I("ime", "WM_IME_STARTCOMPOSITION");
        break;  // DefWindowProc 통과 (Phase 1: 행동 변경 없음)
    case WM_IME_COMPOSITION:
        LOG_I("ime", "WM_IME_COMPOSITION: lp=0x%08lX GCS_RESULT=%d GCS_COMP=%d",
              (unsigned long)lp, !!(lp & GCS_RESULTSTR), !!(lp & GCS_COMPSTR));
        break;  // DefWindowProc 통과
    case WM_IME_ENDCOMPOSITION:
        LOG_I("ime", "WM_IME_ENDCOMPOSITION");
        break;  // DefWindowProc 통과
    case WM_IME_CHAR:
        LOG_I("ime", "WM_IME_CHAR: U+%04X", (unsigned)wp);
        break;  // DefWindowProc 통과

    case WM_CHAR: {
        wchar_t ch = static_cast<wchar_t>(wp);

        // 서로게이트 쌍 처리 (Supplementary Plane: 이모지, CJK Ext B 등)
        // WM_CHAR는 U+10000 이상 문자를 high + low surrogate 2회로 전달
        if (ch >= 0xD800 && ch <= 0xDBFF) {
            // High surrogate — 저장하고 다음 WM_CHAR(low surrogate) 대기
            self->m_pending_high_surrogate = ch;
            LOG_I("winui", "WM_CHAR: U+%04X (high surrogate, pending)", (unsigned)ch);
            return 0;
        }
        if (ch >= 0xDC00 && ch <= 0xDFFF) {
            if (self->m_pending_high_surrogate != 0) {
                // Low surrogate — 쌍 결합 후 전송
                std::wstring text;
                text += self->m_pending_high_surrogate;
                text += ch;
                self->m_pending_high_surrogate = 0;
                LOG_I("winui", "WM_CHAR: surrogate pair U+%04X U+%04X → codepoint U+%05X",
                      (unsigned)text[0], (unsigned)text[1],
                      (unsigned)(0x10000 + ((text[0] - 0xD800) << 10) + (text[1] - 0xDC00)));
                self->SendUtf8(text);
                return 0;
            }
            // Orphan low surrogate — 리셋하고 무시
            LOG_W("winui", "WM_CHAR: orphan low surrogate U+%04X, ignoring", (unsigned)ch);
            self->m_pending_high_surrogate = 0;
            return 0;
        }
        // Non-surrogate 문자 도착 시 pending high surrogate 리셋
        self->m_pending_high_surrogate = 0;

        // TSF가 조합 중이면 WM_CHAR 무시 (TSF 콜백이 처리)
        // Space/Enter 등이 TSF 조합 확정과 WM_CHAR 양쪽으로 도착하는 이중 전송 방지
        //
        // Bug #4 fix: 제어 문자(Enter, Tab, Escape, Backspace)는 조합 중에도
        // 항상 처리. TSF가 조합을 확정(EC)하고 WM_CHAR(0x0D)가 도착하는데,
        // EC가 m_compositions를 0으로 만들기 전에 WM_CHAR가 먼저 도착하면
        // Enter가 삼켜지는 race condition 방지.
        if (self->m_tsf.HasActiveComposition()) {
            if (ch == '\r' || ch == '\t' || ch == 0x1B || ch == '\b' || ch == 0x7F) {
                LOG_I("winui", "WM_CHAR: U+%04X passthrough (control char during composition)", (unsigned)ch);
                // fall through — 제어 문자는 조합 중에도 ConPTY에 전송
            } else {
                LOG_I("winui", "WM_CHAR: U+%04X SKIP (TSF composing)", (unsigned)ch);
                return 0;
            }
        }

        LOG_I("winui", "WM_CHAR: U+%04X '%c'", (unsigned)ch,
              (ch > 0x20 && ch < 0x7F) ? (char)ch : '?');
        if (ch >= 0x20) {
            std::wstring text(1, ch);
            self->SendUtf8(text);
        } else if (ch == '\r') {
            self->SendVt("\r");
        } else if (ch == '\t') {
            self->SendVt("\t");
        } else if (ch == 0x1B) {
            self->SendVt("\033");
        } else if (ch == 0x7F || ch == '\b') {
            uint8_t del = 0x7F;
            self->m_session->send_input({&del, 1});
        }
        return 0;
    }
    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:  // Alt+key 조합 지원 (readline)
        if (self->HandleKeyDown(wp))
            return 0;  // handled — WM_CHAR/WM_SYSCHAR 생성 방지
        // DefWindowProc 호출 필요 — TranslateMessage가 WM_CHAR 생성
        break;
    case WM_SETFOCUS:
        LOG_I("winui", "Input HWND got focus");
        return 0;
    case WM_KILLFOCUS:
        LOG_I("winui", "Input HWND lost focus");
        // 조합 중이면 프리뷰 클리어 + 서로게이트 리셋 (WT 동작 참조: 확정하지 않고 폐기)
        if (self->m_tsf.HasActiveComposition()) {
            LOG_I("winui", "Clearing active composition on focus loss");
            std::lock_guard lock(self->m_ime_mutex);
            self->m_composition.clear();
        }
        self->m_pending_high_surrogate = 0;
        return 0;
    case WM_TIMER:
        // 포커스 유지 타이머: 부모 윈도우가 foreground일 때만
        if (wp == 1) {
            HWND parent = GetParent(hwnd);
            if (parent && GetForegroundWindow() == parent && GetFocus() != hwnd) {
                ::SetFocus(hwnd);
            }
        }
        return 0;
    case WM_USER + 50:
        // 지연 전송: OnEndComposition이 PostMessage로 예약
        // OnStartComposition이 그 사이에 오면 pending 취소됨 (종성 분리)
        // 여기까지 도달 = Space/Enter 등으로 확정 → 전송
        self->m_tsf.SendPendingDirectSend();
        return 0;
    case WM_USER + 99:
        // 테스트 모드: UI 스레드에서 foreground + focus 강제 설정
        // Alt key trick: Alt 이벤트 후 SetForegroundWindow 성공률 증가
        {
            HWND parent = GetParent(hwnd);
            if (parent) {
                keybd_event(VK_MENU, 0, 0, 0);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
                ShowWindow(parent, SW_RESTORE);
                SetForegroundWindow(parent);
                Sleep(100);
            }
            ::SetFocus(hwnd);
            bool ok = (GetForegroundWindow() == parent && GetFocus() == hwnd);
            LOG_I("test", "UI focus: fg=%p(%s) focus=%p(%s)",
                  GetForegroundWindow(), GetForegroundWindow() == parent ? "OURS" : "OTHER",
                  GetFocus(), GetFocus() == hwnd ? "INPUT" : "WRONG");
        }
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

bool GhostWinApp::HandleKeyDown(WPARAM vk) {
    bool ctrl  = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    bool shift = (GetKeyState(VK_SHIFT)   & 0x8000) != 0;
    bool alt   = (GetKeyState(VK_MENU)    & 0x8000) != 0;
    LOG_I("winui", "WM_KEYDOWN: vk=%u ctrl=%d shift=%d alt=%d composing=%d",
          (unsigned)vk, ctrl, shift, alt, m_tsf.HasActiveComposition());

    auto cancelComposition = [&]() {
        if (m_tsf.HasActiveComposition()) {
            LOG_I("winui", "Cancelling active composition");
            m_tsf.CancelComposition();
            std::lock_guard lock(m_ime_mutex);
            m_composition.clear();
        }
    };

    // ── Ctrl+V: 클립보드 붙여넣기 (WT 동일 동작) ──
    if (ctrl && vk == 'V') {
        cancelComposition();
        PasteFromClipboard();
        return true;
    }

    // ── Ctrl+키 (A-Z): 제어 코드 전송 ──
    // Bug #3 fix: 조합 중이면 먼저 TSF 조합 취소 (조합 텍스트 폐기)
    if (ctrl && vk >= 'A' && vk <= 'Z') {
        cancelComposition();
        uint8_t code = static_cast<uint8_t>(vk - 'A' + 1);
        m_session->send_input({&code, 1});
        return true;
    }

    // ── Alt+영문자: ESC prefix (readline/bash 단축키) ──
    // Alt+B=단어 뒤로, Alt+F=단어 앞으로, Alt+D=단어 삭제 등
    if (alt && !ctrl && vk >= 'A' && vk <= 'Z') {
        cancelComposition();
        char ch = shift ? static_cast<char>(vk) : static_cast<char>(vk - 'A' + 'a');
        uint8_t seq[2] = {0x1B, static_cast<uint8_t>(ch)};
        m_session->send_input({seq, 2});
        LOG_I("winui", "Alt+%c → ESC+'%c'", (char)vk, ch);
        return true;
    }

    // ── Alt+숫자: ESC prefix (readline Alt+0~9) ──
    if (alt && !ctrl && vk >= '0' && vk <= '9') {
        cancelComposition();
        uint8_t seq[2] = {0x1B, static_cast<uint8_t>(vk)};
        m_session->send_input({seq, 2});
        return true;
    }

    // ── IME VK_PROCESSKEY — DefWindowProc에서 IME로 전달 ──
    if (vk == VK_PROCESSKEY) return false;

    // ── Shift+Tab → backtab (역방향 자동완성) ──
    if (vk == VK_TAB && shift) {
        cancelComposition();
        SendVt("\033[Z");
        return true;
    }

    // ── 특수키 → VT 시퀀스 ──
    // DECCKM (DEC Private Mode 1): 화살표를 Application mode로 전환
    // vim, tmux 등이 \033[?1h 로 활성화 → 화살표가 \033OA~D 로 변경
    bool decckm = m_session && m_session->vt_core().mode_get(VT_MODE_DECCKM);
    const char* seq = nullptr;
    switch (vk) {
    case VK_UP:     seq = decckm ? "\033OA" : "\033[A"; break;
    case VK_DOWN:   seq = decckm ? "\033OB" : "\033[B"; break;
    case VK_RIGHT:  seq = decckm ? "\033OC" : "\033[C"; break;
    case VK_LEFT:   seq = decckm ? "\033OD" : "\033[D"; break;
    case VK_HOME:   seq = "\033[H"; break;
    case VK_END:    seq = "\033[F"; break;
    case VK_DELETE: seq = "\033[3~"; break;
    case VK_INSERT: seq = "\033[2~"; break;
    case VK_PRIOR:  seq = "\033[5~"; break;  // PageUp
    case VK_NEXT:   seq = "\033[6~"; break;  // PageDown
    case VK_F1:     seq = "\033OP"; break;
    case VK_F2:     seq = "\033OQ"; break;
    case VK_F3:     seq = "\033OR"; break;
    case VK_F4:     seq = "\033OS"; break;
    case VK_F5:     seq = "\033[15~"; break;
    case VK_F6:     seq = "\033[17~"; break;
    case VK_F7:     seq = "\033[18~"; break;
    case VK_F8:     seq = "\033[19~"; break;
    case VK_F9:     seq = "\033[20~"; break;
    case VK_F10:    seq = "\033[21~"; break;
    case VK_F11:    seq = "\033[23~"; break;
    case VK_F12:    seq = "\033[24~"; break;
    default: break;
    }
    if (seq) {
        SendVt(seq);
        return true;
    }
    // Enter, Tab, Escape, Backspace → WM_CHAR로 처리 (TranslateMessage 경유)
    return false;
}

// ─── TsfDataAdapter — IDataProvider 구현 ───

HWND GhostWinApp::TsfDataAdapter::GetHwnd() {
    return app ? app->m_input_hwnd : nullptr;  // 입력 전용 HWND
}

RECT GhostWinApp::TsfDataAdapter::GetViewport() {
    HWND hwnd = app ? app->GetWindowHwnd() : nullptr;
    if (!hwnd) return {};
    RECT rc;
    GetWindowRect(hwnd, &rc);
    return rc;
}

RECT GhostWinApp::TsfDataAdapter::GetCursorPosition() {
    if (!app || !app->m_state || !app->m_atlas) return {};
    HWND hwnd = app->GetWindowHwnd();
    if (!hwnd) return {};

    auto cursor = app->m_state->frame().cursor;
    uint32_t cw = app->m_atlas->cell_width();
    uint32_t ch = app->m_atlas->cell_height();

    float panelX = 0;
    if (app->m_panel) {
        auto offset = app->m_panel.ActualOffset();
        panelX = offset.x;
    }

    POINT pt = {
        static_cast<LONG>(panelX) + static_cast<LONG>(cursor.x * cw),
        static_cast<LONG>(cursor.y * ch)
    };
    ClientToScreen(hwnd, &pt);
    return { pt.x, pt.y, pt.x + static_cast<LONG>(cw), pt.y + static_cast<LONG>(ch) };
}

void GhostWinApp::TsfDataAdapter::HandleOutput(std::wstring_view text) {
    if (!app || text.empty()) return;
    LOG_I("tsf", "HandleOutput: %zu chars", text.size());
    app->SendUtf8(std::wstring(text));
}

void GhostWinApp::TsfDataAdapter::HandleCompositionUpdate(const CompositionPreview& preview) {
    if (!app) return;
    std::lock_guard lock(app->m_ime_mutex);
    app->m_composition = preview.text;
}

// ─── OnLaunched ───
void GhostWinApp::OnLaunched(winui::LaunchActivatedEventArgs const&) {
    // --test-ime 커맨드라인 확인
    std::wstring cmdLine(GetCommandLineW());
    m_test_mode = (cmdLine.find(L"--test-ime") != std::wstring::npos);
    if (m_test_mode) LOG_I("winui", "*** TEST MODE: --test-ime ***");

    auto resources = controls::XamlControlsResources();
    winui::Application::Current().Resources().MergedDictionaries().Append(resources);

    m_window = winui::Window();
    m_window.Title(L"GhostWin Terminal");
    m_window.ExtendsContentIntoTitleBar(true);

    // FR-07: Mica backdrop (falls back to solid color on unsupported systems)
    try {
        winrt::Microsoft::UI::Xaml::Media::MicaBackdrop mica;
        m_window.SystemBackdrop(mica);
        LOG_I("winui", "Mica backdrop applied");
    } catch (...) {
        LOG_W("winui", "Mica backdrop not supported, using solid background");
    }

    auto grid = controls::Grid();
    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLengthHelper::FromPixels(0));  // EXPERIMENT: remove sidebar to test pixel alignment
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLength{1, winui::GridUnitType::Star});
    grid.ColumnDefinitions().Append(col0);
    grid.ColumnDefinitions().Append(col1);

    auto sidebar = controls::ListView();
    controls::Grid::SetColumn(sidebar, 0);
    grid.Children().Append(sidebar);

    m_panel = controls::SwapChainPanel();
    controls::Grid::SetColumn(m_panel, 1);
    grid.Children().Append(m_panel);

    m_window.Content(grid);

    m_panel.Loaded([self = get_strong()](auto&&, auto&&) {
        auto offset = self->m_panel.ActualOffset();
        LOG_I("winui", "SwapChainPanel offset: (%.2f, %.2f), size: %.2fx%.2f",
              offset.x, offset.y,
              self->m_panel.ActualWidth(), self->m_panel.ActualHeight());
        bool pixel_aligned = (offset.x == std::floor(offset.x)) && (offset.y == std::floor(offset.y));
        if (!pixel_aligned) {
            LOG_W("winui", "WARNING: SwapChainPanel NOT pixel-aligned! DWM will apply bilinear filtering → BLUR");
        }
        self->InitializeD3D11(self->m_panel);

        // Window HWND 확보 후 입력 HWND + TSF 초기화
        HWND parentHwnd = self->GetWindowHwnd();
        if (parentHwnd) {
            self->CreateInputHwnd(parentHwnd);
            if (self->m_input_hwnd) {
                self->m_tsf_data.app = self.get();
                self->m_tsf = TsfHandle::Create();
                if (self->m_tsf) {
                    self->m_tsf.Focus(&self->m_tsf_data);
                }
                // 50ms 타이머: WinUI3 포커스 탈취에 대응하여 지속적 포커스 유지
                SetTimer(self->m_input_hwnd, 1, 50, nullptr);

                // --test-ime 모드
                if (self->m_test_mode) {
                    // grid_info.json 덤프 (UI 스레드에서 — XAML API 호출 필요)
                    CreateDirectoryA("test_results", nullptr);
                    if (self->m_atlas && self->m_panel) {
                        uint32_t cw = self->m_atlas->cell_width();
                        uint32_t ch = self->m_atlas->cell_height();
                        auto offset = self->m_panel.ActualOffset();
                        FILE* gf = fopen("test_results/grid_info.json", "w");
                        if (gf) {
                            fprintf(gf, "{\"grid_x\":%d,\"grid_y\":%d,\"cell_w\":%u,\"cell_h\":%u}\n",
                                    (int)offset.x, (int)offset.y, cw, ch);
                            fclose(gf);
                        }
                    }
                    self->m_test_thread = std::thread(RunImeTest, self.get());
                    self->m_test_thread.detach();
                }
            }
        }
    });

    // SwapChainPanel 클릭 시 별도 처리 불필요 — 타이머가 포커스 유지
    m_panel.PointerPressed([self = get_strong()](auto&&, auto&&) {
        // 타이머가 50ms 내에 포커스 복원
    });

    m_panel.SizeChanged([self = get_strong()](auto&&, winui::SizeChangedEventArgs const&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    m_panel.CompositionScaleChanged([self = get_strong()](
            controls::SwapChainPanel const&, auto&&) {
        float newScale = self->m_panel.CompositionScaleX();
        float oldScale = self->m_current_dpi_scale.load(std::memory_order_acquire);
        if (std::abs(newScale - oldScale) > 0.01f) {
            self->m_pending_dpi_scale.store(newScale, std::memory_order_release);
            self->m_dpi_change_requested.store(true, std::memory_order_release);
        }
        self->m_resize_timer.Stop();
        self->m_resize_timer.Start();
    });

    // ─── 타이머 ───

    m_blink_timer = winui::DispatcherTimer();
    m_blink_timer.Interval(std::chrono::milliseconds(530));
    m_blink_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_cursor_blink_visible.store(
            !self->m_cursor_blink_visible.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
    });
    m_blink_timer.Start();

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

    m_window.Activate();
}

// ─── IME 자동 테스트 (--test-ime) ───
static void PressKey(WORD vk) {
    INPUT inputs[2] = {};
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].ki.wVk = vk;
    inputs[1].type = INPUT_KEYBOARD;
    inputs[1].ki.wVk = vk;
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput(2, inputs, sizeof(INPUT));
}

// ─── 테스트 유틸 ───
// g_tap_mutex는 conpty_session.h에서 extern 선언 — tap 콜백 설정/해제 전용
// g_data_mutex — 테스트 데이터(g_input_bytes, g_echo_bytes) 보호용 (deadlock 방지)
static std::mutex g_data_mutex;
static std::vector<uint8_t> g_input_bytes;   // L2: send_input 캡처
static std::vector<uint8_t> g_echo_bytes;    // L3: echo 캡처

static void ClearTaps() {
    std::lock_guard lock(g_data_mutex);
    g_input_bytes.clear();
    g_echo_bytes.clear();
}

static bool CheckInputContains(const uint8_t* expected, size_t len, const char* label) {
    std::lock_guard lock(g_data_mutex);
    std::string hex;
    for (size_t i = 0; i < g_input_bytes.size(); i++) {
        char buf[4]; snprintf(buf, sizeof(buf), "%02X ", g_input_bytes[i]);
        hex += buf;
    }
    for (size_t i = 0; i + len <= g_input_bytes.size(); i++) {
        if (memcmp(&g_input_bytes[i], expected, len) == 0) {
            LOG_I("test", "  L2 send_input: PASS (%s found in %zu bytes)", label, g_input_bytes.size());
            return true;
        }
    }
    LOG_E("test", "  L2 send_input: FAIL (%s not found, got: %s)", label, hex.c_str());
    return false;
}

// L3 echo 검증: VT escape 시퀀스(ESC[...)를 스트립한 후 비교
static std::vector<uint8_t> StripVtEscapes(const std::vector<uint8_t>& raw) {
    std::vector<uint8_t> out;
    out.reserve(raw.size());
    for (size_t i = 0; i < raw.size(); i++) {
        if (raw[i] == 0x1B && i + 1 < raw.size() && raw[i + 1] == '[') {
            // ESC [ ... 끝 문자(0x40~0x7E)까지 건너뜀
            i += 2;
            while (i < raw.size() && (raw[i] < 0x40 || raw[i] > 0x7E)) i++;
            // 끝 문자도 건너뜀
        } else if (raw[i] == 0x1B && i + 1 < raw.size() && raw[i + 1] == ']') {
            // OSC 시퀀스: ESC ] ... ST(ESC \) 또는 BEL(0x07)까지 건너뜀
            i += 2;
            while (i < raw.size()) {
                if (raw[i] == 0x07) break;
                if (raw[i] == 0x1B && i + 1 < raw.size() && raw[i + 1] == '\\') { i++; break; }
                i++;
            }
        } else {
            out.push_back(raw[i]);
        }
    }
    return out;
}

static bool CheckEchoContains(const uint8_t* expected, size_t len, const char* label) {
    // 폴링 방식: ConPTY → I/O 스레드 → g_tap_echo 경로가 비동기이므로
    // 최대 3초 대기하면서 100ms마다 체크
    for (int poll = 0; poll < 30; poll++) {
        {
            std::lock_guard lock(g_data_mutex);
            auto stripped = StripVtEscapes(g_echo_bytes);
            for (size_t i = 0; i + len <= stripped.size(); i++) {
                if (memcmp(&stripped[i], expected, len) == 0) {
                    LOG_I("test", "  L3 echo:       PASS (%s found in %zu bytes, stripped %zu, poll=%d)",
                          label, g_echo_bytes.size(), stripped.size(), poll);
                    return true;
                }
            }
        }
        Sleep(100);
    }
    std::lock_guard lock(g_data_mutex);
    LOG_E("test", "  L3 echo:       FAIL (%s not found in %zu bytes after 3s polling)", label, g_echo_bytes.size());
    return false;
}

// ClearScreen: RunImeTest 내 lambda로 구현 (private 멤버 접근 필요)

// 한/영 전환 확인: UI 스레드(input_hwnd)의 keyboard layout을 확인
// SendInput은 UI 스레드에 전달되므로 GetKeyboardLayout(0)은 테스트 스레드 layout을 반환 — 잘못됨
// GetWindowThreadProcessId → GetKeyboardLayout(tid)로 UI 스레드 layout 확인
static HKL GetUiThreadLayout(HWND input_hwnd) {
    DWORD tid = GetWindowThreadProcessId(input_hwnd, nullptr);
    return GetKeyboardLayout(tid);
}

static bool EnsureKoreanLayout(HWND input_hwnd) {
    for (int attempt = 0; attempt < 3; attempt++) {
        PressKey(VK_HANGUL);
        Sleep(300);
        HKL layout = GetUiThreadLayout(input_hwnd);
        WORD langId = LOWORD(reinterpret_cast<uintptr_t>(layout));
        LOG_I("test", "  Hangul toggle attempt %d: layout=0x%04X", attempt + 1, langId);
        if (langId == 0x0412) return true;
    }
    LOG_E("test", "  Failed to switch to Korean layout after 3 attempts");
    return false;
}

static bool EnsureEnglishLayout(HWND input_hwnd) {
    HKL layout = GetUiThreadLayout(input_hwnd);
    WORD langId = LOWORD(reinterpret_cast<uintptr_t>(layout));
    if (langId == 0x0412) {
        // 한국어 상태이므로 전환 필요
        for (int attempt = 0; attempt < 3; attempt++) {
            PressKey(VK_HANGUL);
            Sleep(300);
            layout = GetUiThreadLayout(input_hwnd);
            langId = LOWORD(reinterpret_cast<uintptr_t>(layout));
            LOG_I("test", "  English toggle attempt %d: layout=0x%04X", attempt + 1, langId);
            if (langId != 0x0412) return true;
        }
        LOG_E("test", "  Failed to switch to English layout after 3 attempts");
        return false;
    }
    return true;
}

void GhostWinApp::RunImeTest(GhostWinApp* app) {
    LOG_I("test", "=== IME Layer Test START ===");
    Sleep(2000);

    // grid_info.json은 UI 스레드의 Loaded 핸들러에서 이미 덤프됨
    // (m_panel.ActualOffset()는 XAML API라 UI 스레드에서만 호출 가능)

    // 화면 초기화 lambda (private 멤버 접근 가능)
    auto ClearScreen = [app]() {
        if (!app->m_session) return;
        const char cls_cmd[] = "cls\r";
        app->m_session->send_input({reinterpret_cast<const uint8_t*>(cls_cmd), sizeof(cls_cmd) - 1});
        Sleep(500);
        if (app->m_state) {
            app->m_state->start_paint(app->m_vt_mutex, app->m_session->vt_core());
            const auto& frame = app->m_state->frame();
            int nonEmpty = 0;
            for (uint16_t r = 0; r < frame.rows_count; r++) {
                auto row = frame.row(r);
                for (uint16_t c = 0; c < frame.cols; c++) {
                    if (row[c].cp_count > 0 && row[c].codepoints[0] > 0x20) nonEmpty++;
                }
            }
            LOG_I("test", "  Screen cleared: %d non-empty cells remaining", nonEmpty);
        }
    };

    // Tap 콜백 설정 (g_tap_mutex로 콜백 포인터 보호, g_data_mutex로 데이터 보호)
    {
        std::lock_guard lock(g_tap_mutex);
        g_tap_input = [](std::span<const uint8_t> data) {
            std::lock_guard lock(g_data_mutex);
            g_input_bytes.insert(g_input_bytes.end(), data.begin(), data.end());
        };
        g_tap_echo = [](std::span<const uint8_t> data) {
            std::lock_guard lock(g_data_mutex);
            g_echo_bytes.insert(g_echo_bytes.end(), data.begin(), data.end());
        };
    }

    // Foreground 확보
    PostMessage(app->m_input_hwnd, WM_USER + 99, 0, 0);
    Sleep(1500);
    HWND fg = GetForegroundWindow();
    HWND mainHwnd = app->GetWindowHwnd();
    if (fg != mainHwnd) {
        LOG_E("test", "PRECONDITION FAIL: not foreground");
        LOG_E("test", "=== IME Layer Test ABORTED ===");
        { std::lock_guard lock(g_tap_mutex); g_tap_input = nullptr; g_tap_echo = nullptr; }
        return;
    }

    int pass = 0, fail = 0, total = 0;

    // L4 VT state 검증: 전체 grid 스캔 (ClearScreen이 grid를 비우므로 이전 데이터 오염 없음)
    // cursor ± 3은 프롬프트 출력 후 cursor 이동으로 범위 밖이 될 수 있음
    auto CheckVtCell = [app](uint32_t target_cp, const char* label) -> bool {
        if (!app->m_state || !app->m_session) return false;
        app->m_state->start_paint(app->m_vt_mutex, app->m_session->vt_core());
        const auto& frame = app->m_state->frame();
        int cursor_row = static_cast<int>(frame.cursor.y);
        for (int r = 0; r < frame.rows_count; r++) {
            auto row = frame.row(static_cast<uint16_t>(r));
            for (uint16_t c = 0; c < frame.cols; c++) {
                if (row[c].cp_count > 0 && row[c].codepoints[0] == target_cp) {
                    LOG_I("test", "  L4 VT state:   PASS (U+%04X at row=%d col=%u, cursor_row=%d)",
                          target_cp, r, c, cursor_row);
                    return true;
                }
            }
        }
        LOG_E("test", "  L4 VT state:   FAIL (U+%04X not found in %d rows, cursor_row=%d)",
              target_cp, frame.rows_count, cursor_row);
        return false;
    };

    // ═══ T1: English "echo GWTEST1" + Enter (고유 마커 문자열) ═══
    {
        LOG_I("test", "[T1] English: echo GWTEST1 + Enter");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();
        EnsureEnglishLayout(app->m_input_hwnd);

        PressKey('E'); Sleep(60); PressKey('C'); Sleep(60);
        PressKey('H'); Sleep(60); PressKey('O'); Sleep(60);
        PressKey(VK_SPACE); Sleep(60);
        // "GWTEST1" — Shift 포함 대문자
        // G
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'G';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'G'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        // W
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'W';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'W'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        // T
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'T';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'T'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        // E
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'E';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'E'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        // S
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'S';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'S'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        // T
        {
            INPUT inputs[4] = {};
            inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_SHIFT;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = 'T';
            inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'T'; inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_SHIFT; inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, sizeof(INPUT));
        }
        Sleep(60);
        PressKey('1'); Sleep(60);
        PressKey(VK_RETURN); Sleep(1000);

        // L2: "GWTEST1" 전송 확인 (고유 마커)
        const uint8_t exp_marker[] = {'G', 'W', 'T', 'E', 'S', 'T', '1'};
        bool l2 = CheckInputContains(exp_marker, 7, "\"GWTEST1\"");

        // L3: echo에 "GWTEST1" 포함 (VT escape 스트립 후)
        bool l3 = CheckEchoContains(exp_marker, 7, "\"GWTEST1\"-echo");

        // L4: VT state에 'G'(0x47) 존재 (cursor 근처)
        bool l4 = CheckVtCell(0x47, "G");

        total += 3;
        if (l2) pass++; else fail++;
        if (l3) pass++; else fail++;
        if (l4) pass++; else fail++;
    }

    // ═══ T2: Korean "한" + Space ═══
    {
        LOG_I("test", "[T2] Korean: han + Space");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 한/영 전환 확인 (GetKeyboardLayout 체크 + 재시도)
        if (!EnsureKoreanLayout(app->m_input_hwnd)) {
            LOG_E("test", "  T2 SKIP: Korean layout unavailable");
            total += 3; fail += 3;
        } else {
            PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
            PressKey('S'); Sleep(500);
            PressKey(VK_SPACE); Sleep(1000);

            // L2: UTF-8 "한" = ED 95 9C
            const uint8_t exp_han[] = {0xED, 0x95, 0x9C};
            bool l2 = CheckInputContains(exp_han, 3, "UTF8-han");

            // L3: echo에 "한" UTF-8 포함 (VT escape 스트립 후)
            bool l3 = CheckEchoContains(exp_han, 3, "UTF8-han-echo");

            // L4: VT state에 U+D55C ("한") 존재 (cursor 근처)
            bool l4 = CheckVtCell(0xD55C, "han");

            total += 3;
            if (l2) pass++; else fail++;
            if (l3) pass++; else fail++;
            if (l4) pass++; else fail++;
        }
    }

    // ═══ T3: Korean "한" + Backspace x3 (삭제 — 전송 안 됨) + L1(TSF 콜백) 검증 ═══
    do {
        LOG_I("test", "[T3] Korean: han + BS x3 (cancel) + TSF callback verify");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // TSF 조합 시작 카운터 스냅샷
        int comp_before = g_tsf_composition_start_count.load(std::memory_order_relaxed);

        // 한국어 입력 상태 확인 (UI 스레드 layout)
        HKL layout = GetUiThreadLayout(app->m_input_hwnd);
        WORD langId = LOWORD(reinterpret_cast<uintptr_t>(layout));
        if (langId != 0x0412) {
            if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                LOG_E("test", "  T3 SKIP: Korean layout unavailable");
                total += 2; fail += 2;
                break;
            }
        }

        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(300);
        PressKey(VK_BACK); Sleep(200);
        PressKey(VK_BACK); Sleep(200);
        PressKey(VK_BACK); Sleep(500);

        // L1: TSF OnStartComposition 콜백이 발생했는지 확인
        {
            int comp_after = g_tsf_composition_start_count.load(std::memory_order_relaxed);
            int comp_delta = comp_after - comp_before;
            if (comp_delta > 0) {
                LOG_I("test", "  L1 TSF callback: PASS (Composition started %d times)", comp_delta);
                pass++;
            } else {
                LOG_E("test", "  L1 TSF callback: FAIL (IME inactive — no composition started)");
                fail++;
            }
            total++;
        }

        // L2: "한" UTF-8 전송되지 않아야 함
        {
            const uint8_t exp_han[] = {0xED, 0x95, 0x9C};
            std::lock_guard lock(g_data_mutex);
            bool han_sent = false;
            for (size_t i = 0; i + 3 <= g_input_bytes.size(); i++) {
                if (memcmp(&g_input_bytes[i], exp_han, 3) == 0) { han_sent = true; break; }
            }
            if (!han_sent) {
                LOG_I("test", "  L2 send_input: PASS (han NOT sent — correct cancel)");
                pass++;
            } else {
                LOG_E("test", "  L2 send_input: FAIL (han WAS sent — should be cancelled)");
                fail++;
            }
            total++;
        }

        // L_OVERLAY: m_composition 오버레이 잔존 검증 (핵심!)
        // BS 3회 후 m_composition이 비어있어야 함 — 비어있지 않으면 오버레이에 잔상
        // 즉시 체크 (0ms) + 지연 체크 (500ms) 둘 다 수행하여 race condition 감지
        {
            // 즉시 체크 (비동기 edit session이 아직 실행되기 전)
            std::wstring comp_immediate;
            { std::lock_guard lock(app->m_ime_mutex); comp_immediate = app->m_composition; }
            if (!comp_immediate.empty()) {
                std::string hex;
                for (wchar_t c : comp_immediate) { char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c); hex += buf; }
                LOG_W("test", "  L_OVERLAY(0ms): WARN — m_composition not empty: %s (race?)", hex.c_str());
            } else {
                LOG_I("test", "  L_OVERLAY(0ms): OK — empty immediately");
            }

            // 500ms 대기 후 재체크 — 비동기 edit session이 오버레이를 재오염하는지
            Sleep(500);
            std::wstring comp_after;
            { std::lock_guard lock(app->m_ime_mutex); comp_after = app->m_composition; }
            if (comp_after.empty()) {
                LOG_I("test", "  L_OVERLAY:     PASS (m_composition empty after BS cancel)");
                pass++;
            } else {
                // hex dump
                std::string hex;
                for (wchar_t c : comp_after) {
                    char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c);
                    hex += buf;
                }
                LOG_E("test", "  L_OVERLAY:     FAIL (m_composition NOT empty: %s)", hex.c_str());
                fail++;
            }
            total++;
        }
    } while (false);

    // ═══ T4: Restore English ═══
    {
        LOG_I("test", "[T4] Restore English + ok");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 영어 전환 확인
        EnsureEnglishLayout(app->m_input_hwnd);

        PressKey('O'); Sleep(80); PressKey('K'); Sleep(80);
        PressKey(VK_RETURN); Sleep(500);

        const uint8_t exp[] = {'o', 'k'};
        bool l2 = CheckInputContains(exp, 2, "\"ok\"");
        total++; if (l2) pass++; else fail++;
    }

    // ═══ T5: 빠른 연타 Backspace (race condition 재현) ═══
    do {
        LOG_I("test", "[T5] Fast BS race: GKS(han) 30ms + BS x3 30ms + immediate overlay check");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        HKL layout5 = GetUiThreadLayout(app->m_input_hwnd);
        WORD langId5 = LOWORD(reinterpret_cast<uintptr_t>(layout5));
        if (langId5 != 0x0412) {
            if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                LOG_E("test", "  T5 SKIP: Korean layout unavailable");
                total += 2; fail += 2;
                break;
            }
        }

        // 빠른 연타 30ms 간격
        PressKey('G'); Sleep(30); PressKey('K'); Sleep(30);
        PressKey('S'); Sleep(30);
        PressKey(VK_BACK); Sleep(30);
        PressKey(VK_BACK); Sleep(30);
        PressKey(VK_BACK); Sleep(0);  // 즉시

        // L_OVERLAY 즉시 체크 (race condition 핵심)
        {
            std::wstring comp_imm;
            { std::lock_guard lock(app->m_ime_mutex); comp_imm = app->m_composition; }
            if (!comp_imm.empty()) {
                std::string hex;
                for (wchar_t c : comp_imm) { char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c); hex += buf; }
                LOG_W("test", "  L_OVERLAY(0ms): WARN — not empty: %s (race detected!)", hex.c_str());
            } else {
                LOG_I("test", "  L_OVERLAY(0ms): OK — empty immediately");
            }

            Sleep(500);
            std::wstring comp_delayed;
            { std::lock_guard lock(app->m_ime_mutex); comp_delayed = app->m_composition; }
            if (comp_delayed.empty()) {
                LOG_I("test", "  L_OVERLAY:     PASS (m_composition empty after fast BS)");
                pass++;
            } else {
                std::string hex;
                for (wchar_t c : comp_delayed) { char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c); hex += buf; }
                LOG_E("test", "  L_OVERLAY:     FAIL (m_composition NOT empty: %s)", hex.c_str());
                fail++;
            }
            total++;
        }

        // L2: "한" 전송 안 됨 확인
        {
            const uint8_t exp_han[] = {0xED, 0x95, 0x9C};
            std::lock_guard lock(g_data_mutex);
            bool han_sent = false;
            for (size_t i = 0; i + 3 <= g_input_bytes.size(); i++) {
                if (memcmp(&g_input_bytes[i], exp_han, 3) == 0) { han_sent = true; break; }
            }
            if (!han_sent) {
                LOG_I("test", "  L2 send_input: PASS (han NOT sent — correct fast cancel)");
                pass++;
            } else {
                LOG_E("test", "  L2 send_input: FAIL (han WAS sent during fast BS)");
                fail++;
            }
            total++;
        }
    } while (false);

    // ═══ T6: 연속 2음절 "한글" + Space 확정 ═══
    do {
        LOG_I("test", "[T6] Korean: hangul (2-syllable) + Space");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        HKL layout6 = GetUiThreadLayout(app->m_input_hwnd);
        WORD langId6 = LOWORD(reinterpret_cast<uintptr_t>(layout6));
        if (langId6 != 0x0412) {
            if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                LOG_E("test", "  T6 SKIP: Korean layout unavailable");
                total += 3; fail += 3;
                break;
            }
        }

        // GKS(한) + RMF(글) + Space
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(120);
        PressKey('R'); Sleep(120); PressKey('M'); Sleep(120);
        PressKey('F'); Sleep(500);
        PressKey(VK_SPACE); Sleep(1000);

        // L2: UTF-8 "한글" = ED 95 9C EA B8 80
        const uint8_t exp_hangul[] = {0xED, 0x95, 0x9C, 0xEA, 0xB8, 0x80};
        bool l2 = CheckInputContains(exp_hangul, 6, "UTF8-hangul");

        // L4: VT state에 U+D55C ("한") 존재
        bool l4a = CheckVtCell(0xD55C, "han(T6)");

        // L4: VT state에 U+AE00 ("글") 존재
        bool l4b = CheckVtCell(0xAE00, "geul(T6)");

        total += 3;
        if (l2) pass++; else fail++;
        if (l4a) pass++; else fail++;
        if (l4b) pass++; else fail++;
    } while (false);

    // ═══ T7: 한글 입력 중 Enter 확정 ═══
    do {
        LOG_I("test", "[T7] Korean: han + Enter (confirm with CR)");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        HKL layout7 = GetUiThreadLayout(app->m_input_hwnd);
        WORD langId7 = LOWORD(reinterpret_cast<uintptr_t>(layout7));
        if (langId7 != 0x0412) {
            if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                LOG_E("test", "  T7 SKIP: Korean layout unavailable");
                total += 2; fail += 2;
                break;
            }
        }

        // GKS(한) + Enter
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(500);
        PressKey(VK_RETURN); Sleep(1000);

        // L2: UTF-8 "한" (ED 95 9C) + CR (0x0D)
        const uint8_t exp_han_cr[] = {0xED, 0x95, 0x9C, 0x0D};
        bool l2 = CheckInputContains(exp_han_cr, 4, "UTF8-han+CR");

        // L4: VT state에 U+D55C ("한") 존재
        bool l4 = CheckVtCell(0xD55C, "han(T7)");

        total += 2;
        if (l2) pass++; else fail++;
        if (l4) pass++; else fail++;
    } while (false);

    // ═══ T8: 한글 → 영문 전환 중간에 ═══
    do {
        LOG_I("test", "[T8] Korean han + Space + toggle English + abc + Enter");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        HKL layout8 = GetUiThreadLayout(app->m_input_hwnd);
        WORD langId8 = LOWORD(reinterpret_cast<uintptr_t>(layout8));
        if (langId8 != 0x0412) {
            if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                LOG_E("test", "  T8 SKIP: Korean layout unavailable");
                total += 2; fail += 2;
                break;
            }
        }

        // GKS(한) + Space (확정)
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(300);
        PressKey(VK_SPACE); Sleep(500);

        // 한/영 전환 → 영문
        if (!EnsureEnglishLayout(app->m_input_hwnd)) {
            LOG_E("test", "  T8 SKIP: English toggle failed");
            total += 2; fail += 2;
            break;
        }

        // "abc" + Enter
        PressKey('A'); Sleep(80); PressKey('B'); Sleep(80);
        PressKey('C'); Sleep(80);
        PressKey(VK_RETURN); Sleep(1000);

        // L2: UTF-8 "한" (ED 95 9C) 포함 확인
        const uint8_t exp_han[] = {0xED, 0x95, 0x9C};
        bool l2_han = CheckInputContains(exp_han, 3, "UTF8-han(T8)");

        // L2: "abc" 포함 확인
        const uint8_t exp_abc[] = {'a', 'b', 'c'};
        bool l2_abc = CheckInputContains(exp_abc, 3, "abc(T8)");

        total += 2;
        if (l2_han) pass++; else fail++;
        if (l2_abc) pass++; else fail++;
    } while (false);

    // ═══ T9: Backspace 부분 삭제 (2번만) — ㅎ 잔존 확인 ═══
    do {
        LOG_I("test", "[T9] Korean: han + BS x2 + Space (partial delete)");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 한국어 레이아웃 확보
        {
            HKL layout9 = GetUiThreadLayout(app->m_input_hwnd);
            WORD langId9 = LOWORD(reinterpret_cast<uintptr_t>(layout9));
            if (langId9 != 0x0412) {
                if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                    LOG_E("test", "  T9 SKIP: Korean layout unavailable");
                    total += 2; fail += 2;
                    break;
                }
            }
        }

        // GKS(한) → BS x2 → Space
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(300);
        PressKey(VK_BACK); Sleep(200);
        PressKey(VK_BACK); Sleep(200);
        // 이제 ㅎ만 남은 상태 → Space로 확정
        PressKey(VK_SPACE); Sleep(1000);

        // L2: 호환자모 ㅎ (U+3160 area) 또는 자모 ㅎ — ConPTY 전송 여부 확인
        // 호환자모 ㅎ = U+314E → UTF-8 = E3 85 8E
        // 또는 ㅎ이 초성 단독이라 완성형으로 전환 안 될 수도 있음
        const uint8_t exp_hieut_compat[] = {0xE3, 0x85, 0x8E};  // U+314E
        bool l2 = CheckInputContains(exp_hieut_compat, 3, "UTF8-hieut(compat)");

        // L_OVERLAY: Space 후 오버레이 비어야 함
        {
            Sleep(300);
            std::wstring comp_after;
            { std::lock_guard lock(app->m_ime_mutex); comp_after = app->m_composition; }
            if (comp_after.empty()) {
                LOG_I("test", "  L_OVERLAY:     PASS (empty after partial BS + Space)");
                pass++;
            } else {
                std::string hex;
                for (wchar_t c : comp_after) { char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c); hex += buf; }
                LOG_E("test", "  L_OVERLAY:     FAIL (not empty: %s)", hex.c_str());
                fail++;
            }
            total++;
        }

        total++;
        if (l2) pass++; else fail++;
    } while (false);

    // ═══ T10: 빈 조합에서 Backspace (크래시 방지 확인) ═══
    do {
        LOG_I("test", "[T10] Empty composition + BS (crash guard)");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 영문 모드 확보 (조합 없는 상태)
        if (!EnsureEnglishLayout(app->m_input_hwnd)) {
            LOG_E("test", "  T10 SKIP: English toggle failed");
            total += 1; fail += 1;
            break;
        }
        Sleep(200);

        // 빈 상태에서 BS → DEL (0x7F) 전송 확인
        PressKey(VK_BACK); Sleep(500);

        // L2: DEL (0x7F) 전송 확인
        const uint8_t exp_del[] = {0x7F};
        bool l2 = CheckInputContains(exp_del, 1, "DEL(0x7F)");

        total++;
        if (l2) pass++; else fail++;

        // 크래시 없이 여기까지 도달 = PASS
        LOG_I("test", "  Crash guard: PASS (no crash)");
    } while (false);

    // ═══ T11: 매우 빠른 연속 음절 "안녕" (30ms 간격) ═══
    do {
        LOG_I("test", "[T11] Fast Korean: annyeong (30ms interval)");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 한국어 레이아웃 확보
        {
            HKL layout11 = GetUiThreadLayout(app->m_input_hwnd);
            WORD langId11 = LOWORD(reinterpret_cast<uintptr_t>(layout11));
            if (langId11 != 0x0412) {
                if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                    LOG_E("test", "  T11 SKIP: Korean layout unavailable");
                    total += 2; fail += 2;
                    break;
                }
            }
        }

        // DKSSUD = ㅇ(D)ㅏ(K)ㄴ(S)ㄴ(S)ㅕ(U)ㅇ(D) = "안녕" — 30ms 빠른 연타
        PressKey('D'); Sleep(30); PressKey('K'); Sleep(30);
        PressKey('S'); Sleep(30); PressKey('S'); Sleep(30);
        PressKey('U'); Sleep(30); PressKey('D'); Sleep(30);
        PressKey(VK_SPACE); Sleep(1000);

        // L2: UTF-8 "안녕" = EC 95 88 EB 85 95
        const uint8_t exp_annyeong[] = {0xEC, 0x95, 0x88, 0xEB, 0x85, 0x95};
        bool l2 = CheckInputContains(exp_annyeong, 6, "UTF8-annyeong");

        // L_OVERLAY: Space 후 오버레이 비어있는지
        {
            Sleep(300);
            std::wstring comp_after;
            { std::lock_guard lock(app->m_ime_mutex); comp_after = app->m_composition; }
            if (comp_after.empty()) {
                LOG_I("test", "  L_OVERLAY:     PASS (empty after fast annyeong + Space)");
                pass++;
            } else {
                std::string hex;
                for (wchar_t c : comp_after) { char buf[8]; snprintf(buf, sizeof(buf), "U+%04X ", (unsigned)c); hex += buf; }
                LOG_E("test", "  L_OVERLAY:     FAIL (not empty: %s)", hex.c_str());
                fail++;
            }
            total++;
        }

        total++;
        if (l2) pass++; else fail++;
    } while (false);

    // ═══ T12: 혼합 입력 한영 반복 ═══
    do {
        LOG_I("test", "[T12] Mixed: han + Space + toggle EN + a + toggle KR + han + Space");
        ClearScreen();
        Sleep(500);  // cls echo가 tap에 수집된 후 비우기
        ClearTaps();

        // 한국어 레이아웃 확보
        {
            HKL layout12 = GetUiThreadLayout(app->m_input_hwnd);
            WORD langId12 = LOWORD(reinterpret_cast<uintptr_t>(layout12));
            if (langId12 != 0x0412) {
                if (!EnsureKoreanLayout(app->m_input_hwnd)) {
                    LOG_E("test", "  T12 SKIP: Korean layout unavailable");
                    total += 3; fail += 3;
                    break;
                }
            }
        }

        // GKS(한) + Space (확정)
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(300);
        PressKey(VK_SPACE); Sleep(500);

        // 한/영 전환 → 영문
        if (!EnsureEnglishLayout(app->m_input_hwnd)) {
            LOG_E("test", "  T12 SKIP: English toggle failed");
            total += 3; fail += 3;
            break;
        }

        // "a"
        PressKey('A'); Sleep(200);

        // 한/영 전환 → 한국어
        if (!EnsureKoreanLayout(app->m_input_hwnd)) {
            LOG_E("test", "  T12 SKIP: Korean toggle failed");
            total += 3; fail += 3;
            break;
        }

        // GKS(한) + Space (확정)
        PressKey('G'); Sleep(120); PressKey('K'); Sleep(120);
        PressKey('S'); Sleep(300);
        PressKey(VK_SPACE); Sleep(1000);

        // L2: UTF-8 첫번째 "한" (ED 95 9C) 포함 확인
        const uint8_t exp_han1[] = {0xED, 0x95, 0x9C};
        bool l2_han1 = CheckInputContains(exp_han1, 3, "UTF8-han1(T12)");

        // L2: "a" 포함 확인
        const uint8_t exp_a[] = {'a'};
        bool l2_a = CheckInputContains(exp_a, 1, "a(T12)");

        // L2: 순서 확인 — "한" UTF-8 다음에 "a", 다음에 "한" UTF-8
        bool l2_order = false;
        {
            std::lock_guard lock(g_data_mutex);
            const uint8_t han_utf8[] = {0xED, 0x95, 0x9C};
            int han1_pos = -1, a_pos = -1, han2_pos = -1;
            for (size_t i = 0; i + 3 <= g_input_bytes.size(); i++) {
                if (memcmp(&g_input_bytes[i], han_utf8, 3) == 0) {
                    if (han1_pos < 0) han1_pos = static_cast<int>(i);
                    else if (han2_pos < 0) han2_pos = static_cast<int>(i);
                }
            }
            for (size_t i = 0; i < g_input_bytes.size(); i++) {
                if (g_input_bytes[i] == 'a') { a_pos = static_cast<int>(i); break; }
            }
            if (han1_pos >= 0 && a_pos > han1_pos && han2_pos > a_pos) {
                LOG_I("test", "  L2 order:      PASS (han@%d < a@%d < han@%d)",
                      han1_pos, a_pos, han2_pos);
                l2_order = true;
            } else {
                LOG_E("test", "  L2 order:      FAIL (han1@%d, a@%d, han2@%d)",
                      han1_pos, a_pos, han2_pos);
            }
        }

        total += 3;
        if (l2_han1) pass++; else fail++;
        if (l2_a) pass++; else fail++;
        if (l2_order) pass++; else fail++;
    } while (false);

    // ═══ Summary ═══
    LOG_I("test", "");
    LOG_I("test", "=== VERDICT: %d/%d PASS, %d FAIL ===", pass, total, fail);
    if (fail == 0) {
        LOG_I("test", "=== ALL LAYERS PASS ===");
    } else {
        LOG_E("test", "=== %d TESTS FAILED ===", fail);
    }

    // mutex 보호 하에 tap 콜백 해제
    { std::lock_guard lock(g_tap_mutex); g_tap_input = nullptr; g_tap_echo = nullptr; }
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

    HANDLE surface_handle = m_renderer->composition_surface_handle();
    if (surface_handle) {
        auto panelNative2 = panel.as<ISwapChainPanelNative2>();
        winrt::check_hresult(panelNative2->SetSwapChainHandle(surface_handle));
        LOG_I("winui", "SwapChainPanel connected via SetSwapChainHandle (v2, IGNORE)");
    } else {
        auto panelNative = panel.as<ISwapChainPanelNative>();
        ComPtr<IDXGISwapChain> sc;
        m_renderer->composition_swapchain()->QueryInterface(IID_PPV_ARGS(&sc));
        winrt::check_hresult(panelNative->SetSwapChain(sc.Get()));
        LOG_W("winui", "SwapChainPanel connected via SetSwapChain (v1, PREMULTIPLIED)");
    }

    // Store initial DPI scale for atlas creation
    m_current_dpi_scale.store(scaleX, std::memory_order_release);
    m_pending_dpi_scale.store(scaleX, std::memory_order_release);
    LOG_I("winui", "Initial DPI scale: %.2f", scaleX);

    StartTerminal(m_renderer->backbuffer_width(), m_renderer->backbuffer_height());
}

// ─── Terminal 시작 ───
void GhostWinApp::StartTerminal(uint32_t width_px, uint32_t height_px) {
    Error err{};
    AtlasConfig acfg;
    acfg.font_family = L"JetBrainsMono NF";
    acfg.font_size_pt = 11.25f;
    acfg.dpi_scale = m_current_dpi_scale.load(std::memory_order_acquire);
    m_atlas = GlyphAtlas::create(m_renderer->device(), acfg, &err);
    if (!m_atlas) { LOG_E("winui", "Failed to create glyph atlas: %s", err.message); return; }
    m_renderer->set_atlas_srv(m_atlas->srv());
    m_renderer->set_cleartype_params(m_atlas->enhanced_contrast(), m_atlas->gamma_ratios());

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
    if (!m_session) { LOG_E("winui", "Failed to create ConPTY session"); return; }

    m_state = std::make_unique<TerminalRenderState>(cols, rows);
    m_staging.resize(
        static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1 + 8);

    m_render_running.store(true, std::memory_order_release);
    m_render_thread = std::thread([this] { RenderLoop(); });
}

void GhostWinApp::ShutdownRenderThread() {
    m_render_running.store(false, std::memory_order_release);
    if (m_render_thread.joinable()) m_render_thread.join();

    // 포커스 유지 타이머 해제 (파괴된 HWND 접근 방지)
    if (m_input_hwnd) {
        KillTimer(m_input_hwnd, 1);
    }
}

// ─── Render Loop ───
void GhostWinApp::RenderLoop() {
    QuadBuilder builder(
        m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

    while (m_render_running.load(std::memory_order_acquire)) {

        // ─── DPI 변경 처리 (리사이즈보다 먼저) ───
        if (m_dpi_change_requested.load(std::memory_order_acquire)) {
            float newScale = m_pending_dpi_scale.load(std::memory_order_acquire);
            LOG_I("winui", "DPI scale changed: %.2f -> %.2f",
                  m_current_dpi_scale.load(std::memory_order_relaxed), newScale);

            Error dpi_err{};
            AtlasConfig dpi_acfg;
            dpi_acfg.font_family = L"JetBrainsMono NF";
            dpi_acfg.font_size_pt = 11.25f;
            dpi_acfg.dpi_scale = newScale;
            auto new_atlas = GlyphAtlas::create(m_renderer->device(), dpi_acfg, &dpi_err);

            if (new_atlas) {
                m_atlas = std::move(new_atlas);
                m_renderer->set_atlas_srv(m_atlas->srv());
                m_renderer->set_cleartype_params(
                    m_atlas->enhanced_contrast(), m_atlas->gamma_ratios());
                builder = QuadBuilder(
                    m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

                uint32_t w = m_pending_width.load(std::memory_order_acquire);
                uint32_t h = m_pending_height.load(std::memory_order_acquire);
                if (w == 0) w = 1; if (h == 0) h = 1;
                uint16_t cols = static_cast<uint16_t>(w / m_atlas->cell_width());
                uint16_t rows = static_cast<uint16_t>(h / m_atlas->cell_height());
                if (cols < 1) cols = 1; if (rows < 1) rows = 1;
                { std::lock_guard lock(m_vt_mutex); m_session->resize(cols, rows); m_state->resize(cols, rows); }
                m_staging.resize(
                    static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1 + 8);

                m_current_dpi_scale.store(newScale, std::memory_order_release);
                LOG_I("winui", "DPI atlas rebuilt: cell=%ux%u, grid=%ux%u",
                      m_atlas->cell_width(), m_atlas->cell_height(), cols, rows);
            } else {
                LOG_E("winui", "DPI atlas rebuild failed: %s", dpi_err.message);
            }
            m_dpi_change_requested.store(false, std::memory_order_release);
        }

        // ─── 리사이즈 처리 ───
        if (m_resize_requested.load(std::memory_order_acquire)) {
            uint32_t w = m_pending_width.load(std::memory_order_acquire);
            uint32_t h = m_pending_height.load(std::memory_order_acquire);
            if (w == 0) w = 1; if (h == 0) h = 1;
            m_renderer->resize_swapchain(w, h);
            uint16_t cols = static_cast<uint16_t>(w / m_atlas->cell_width());
            uint16_t rows = static_cast<uint16_t>(h / m_atlas->cell_height());
            if (cols < 1) cols = 1; if (rows < 1) rows = 1;
            { std::lock_guard lock(m_vt_mutex); m_session->resize(cols, rows); m_state->resize(cols, rows); }
            m_staging.resize(static_cast<size_t>(cols) * rows * constants::kInstanceMultiplier + 1 + 8);
            builder.update_cell_size(m_atlas->cell_width(), m_atlas->cell_height());
            m_resize_requested.store(false, std::memory_order_release);
        }

        std::wstring comp;
        { std::lock_guard lock(m_ime_mutex); comp = m_composition; }
        bool has_comp = !comp.empty();

        // 조합 오버레이 해제 감지: 이전 프레임에서 오버레이가 있었는데
        // 이번 프레임에서 없어지면 강제 리드로우 필요 (이전 픽셀 잔상 제거)
        static bool prev_had_comp = false;
        bool comp_just_cleared = (prev_had_comp && !has_comp);
        prev_had_comp = has_comp;

        bool dirty = m_state->start_paint(m_vt_mutex, m_session->vt_core());
        if (!dirty && !has_comp && !comp_just_cleared) { Sleep(1); continue; }
        if ((has_comp || comp_just_cleared) && !dirty) {
            m_state->force_all_dirty();
            m_state->start_paint(m_vt_mutex, m_session->vt_core());
        }

        const auto& frame = m_state->frame();
        uint32_t bg_count = 0;
        uint32_t count = builder.build(frame, *m_atlas, m_renderer->context(),
            std::span<QuadInstance>(m_staging), &bg_count);

        // 조합 오버레이 (TSF CompositionPreview → m_composition)
        if (has_comp && m_atlas) {
            auto cursor = frame.cursor;
            uint16_t col = cursor.x;
            uint16_t row = cursor.y;
            uint32_t cw = m_atlas->cell_width(), ch = m_atlas->cell_height();

            for (wchar_t c : comp) {
                if (count + 2 >= m_staging.size()) break;
                uint32_t cp = static_cast<uint32_t>(c);
                bool wide = (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0x1100 && cp <= 0x11FF);
                uint16_t cells = wide ? 2 : 1;

                auto& bg = m_staging[count++];
                bg = {};
                bg.pos_x = static_cast<uint16_t>(col * cw);
                bg.pos_y = static_cast<uint16_t>(row * ch);
                bg.size_x = static_cast<uint16_t>(cw * cells);
                bg.size_y = static_cast<uint16_t>(ch);
                bg.bg_packed = 0xFF443344;
                bg.fg_packed = 0xFF443344;
                bg.shading_type = 0;

                auto glyph = m_atlas->lookup_or_rasterize(m_renderer->context(), cp, 0);
                if (glyph.valid && glyph.width > 0) {
                    auto& fg = m_staging[count++];
                    fg = {};
                    fg.pos_x = static_cast<uint16_t>(col * cw + glyph.offset_x);
                    fg.pos_y = static_cast<uint16_t>(row * ch + m_atlas->baseline() + glyph.offset_y);
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
                col += cells;
            }
        }

        if (count > 0) m_renderer->upload_and_draw(m_staging.data(), count, bg_count);

        // Dump atlas once after first real frame
        static bool atlas_dumped = false;
        if (!atlas_dumped && m_atlas && m_atlas->glyph_count() > 50) {
            m_atlas->dump_atlas(m_renderer->context(), "atlas_dump.bmp");
            atlas_dumped = true;
        }
    }
}

} // namespace ghostwin
