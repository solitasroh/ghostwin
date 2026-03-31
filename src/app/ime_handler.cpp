// GhostWin Terminal — IME Input Handler
// WM_IME_COMPOSITION + WM_CHAR direct processing via InputPreTranslateKeyboardSource

#include "app/ime_handler.h"
#include "common/log.h"

#pragma comment(lib, "imm32.lib")

namespace ghostwin {

ImeHandler::ImeHandler(SendInputCallback send_callback)
    : m_send(std::move(send_callback)) {}

std::wstring ImeHandler::GetComposition() const {
    std::lock_guard lock(m_mutex);
    return m_composition;
}

// ─── IUnknown ───

STDMETHODIMP ImeHandler::QueryInterface(REFIID riid, void** ppv) noexcept {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown ||
        riid == __uuidof(IInputPreTranslateKeyboardSourceHandler)) {
        *ppv = static_cast<IInputPreTranslateKeyboardSourceHandler*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) ImeHandler::AddRef() noexcept { return ++m_ref; }
STDMETHODIMP_(ULONG) ImeHandler::Release() noexcept {
    auto ref = --m_ref;
    if (ref == 0) delete this;
    return ref;
}

// ─── OnDirectMessage ───
// XAML 입력 파이프라인 직전에서 Win32 MSG를 가로챔

STDMETHODIMP ImeHandler::OnDirectMessage(
        IInputPreTranslateKeyboardSourceInterop*,
        const MSG* msg, UINT keyboardModifiers,
        bool* handled) {
    if (!msg || !handled) return E_POINTER;
    *handled = false;

    switch (msg->message) {
    case WM_IME_COMPOSITION:
        *handled = HandleImeComposition(msg);
        return S_OK;

    case WM_IME_CHAR:
        // WM_IME_COMPOSITION에서 GCS_RESULTSTR로 이미 처리
        *handled = true;
        return S_OK;

    case WM_IME_STARTCOMPOSITION:
        LOG_I("ime", "WM_IME_STARTCOMPOSITION");
        *handled = true;  // 기본 IME 창 표시 억제
        return S_OK;

    case WM_IME_ENDCOMPOSITION:
        LOG_I("ime", "WM_IME_ENDCOMPOSITION");
        {
            std::lock_guard lock(m_mutex);
            m_composition.clear();
        }
        *handled = true;
        return S_OK;

    case WM_CHAR:
        *handled = HandleChar(msg, keyboardModifiers);
        return S_OK;

    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
        *handled = HandleKeyDown(msg, keyboardModifiers);
        return S_OK;
    }

    return S_OK;
}

STDMETHODIMP ImeHandler::OnTreeMessage(
        IInputPreTranslateKeyboardSourceInterop* source,
        const MSG* msg, UINT keyboardModifiers,
        bool* handled) {
    // OnDirect에서 처리 안 된 메시지를 여기서 재시도
    return OnDirectMessage(source, msg, keyboardModifiers, handled);
}

// ─── WM_IME_COMPOSITION ───
// 한글 IME의 핵심: GCS_RESULTSTR(확정) + GCS_COMPSTR(조합) 동시 처리

bool ImeHandler::HandleImeComposition(const MSG* msg) {
    HWND hwnd = msg->hwnd;
    LPARAM lp = msg->lParam;
    HIMC himc = ImmGetContext(hwnd);
    if (!himc) return false;

    // 1. 확정 텍스트 (GCS_RESULTSTR) → 즉시 ConPTY 전송
    if (lp & GCS_RESULTSTR) {
        LONG bytes = ImmGetCompositionStringW(himc, GCS_RESULTSTR, nullptr, 0);
        if (bytes > 0) {
            std::wstring result(bytes / sizeof(wchar_t), L'\0');
            ImmGetCompositionStringW(himc, GCS_RESULTSTR, result.data(), bytes);
            SendText(result);
            LOG_I("ime", "RESULT: len=%d", static_cast<int>(result.size()));
        }
    }

    // 2. 조합 중 텍스트 (GCS_COMPSTR) → 오버레이용
    if (lp & GCS_COMPSTR) {
        LONG bytes = ImmGetCompositionStringW(himc, GCS_COMPSTR, nullptr, 0);
        std::wstring comp;
        if (bytes > 0) {
            comp.resize(bytes / sizeof(wchar_t));
            ImmGetCompositionStringW(himc, GCS_COMPSTR, comp.data(), bytes);
        }
        {
            std::lock_guard lock(m_mutex);
            m_composition = std::move(comp);
        }
    }

    ImmReleaseContext(hwnd, himc);
    return true;
}

// ─── WM_CHAR ───
// 비-IME 문자 (영문, 숫자, 특수문자)

bool ImeHandler::HandleChar(const MSG* msg, UINT modifiers) {
    wchar_t ch = static_cast<wchar_t>(msg->wParam);

    // Ctrl+키는 HandleKeyDown에서 처리
    if (modifiers & 0x0008) return false;  // FCONTROL

    if (ch == 0x0D) {  // Enter (CR)
        SendVTSequence("\r");
        return true;
    }
    if (ch == 0x09) {  // Tab
        SendVTSequence("\t");
        return true;
    }
    if (ch == 0x1B) {  // Escape
        SendVTSequence("\033");
        return true;
    }
    if (ch == 0x08) {  // Backspace → DEL(0x7F)
        uint8_t del = 0x7F;
        m_send(std::wstring(1, static_cast<wchar_t>(del)));
        return true;
    }
    if (ch < 0x20) {
        // 기타 제어 문자 (Ctrl+A=0x01 등)
        uint8_t ctrl = static_cast<uint8_t>(ch);
        std::string s(1, static_cast<char>(ctrl));
        // UTF-8 바이트 직접 전송
        m_send(std::wstring(1, ch));
        return true;
    }

    // 일반 문자
    SendWchar(ch);
    return true;
}

// ─── WM_KEYDOWN ───
// 특수키 (화살표, F키 등 — WM_CHAR가 생성되지 않는 키)

bool ImeHandler::HandleKeyDown(const MSG* msg, UINT modifiers) {
    UINT vk = static_cast<UINT>(msg->wParam);
    bool ctrl = (modifiers & 0x0008) != 0;

    // Ctrl+A~Z
    if (ctrl && vk >= 'A' && vk <= 'Z') {
        uint8_t code = static_cast<uint8_t>(vk - 'A' + 1);
        std::wstring s(1, static_cast<wchar_t>(code));
        m_send(s);
        return true;
    }

    const char* seq = nullptr;
    switch (vk) {
    case VK_UP:      seq = "\033[A"; break;
    case VK_DOWN:    seq = "\033[B"; break;
    case VK_RIGHT:   seq = "\033[C"; break;
    case VK_LEFT:    seq = "\033[D"; break;
    case VK_HOME:    seq = "\033[H"; break;
    case VK_END:     seq = "\033[F"; break;
    case VK_DELETE:  seq = "\033[3~"; break;
    case VK_INSERT:  seq = "\033[2~"; break;
    case VK_PRIOR:   seq = "\033[5~"; break;  // PageUp
    case VK_NEXT:    seq = "\033[6~"; break;  // PageDown
    case VK_F1:      seq = "\033OP"; break;
    case VK_F2:      seq = "\033OQ"; break;
    case VK_F3:      seq = "\033OR"; break;
    case VK_F4:      seq = "\033OS"; break;
    case VK_F5:      seq = "\033[15~"; break;
    case VK_F6:      seq = "\033[17~"; break;
    case VK_F7:      seq = "\033[18~"; break;
    case VK_F8:      seq = "\033[19~"; break;
    case VK_F9:      seq = "\033[20~"; break;
    case VK_F10:     seq = "\033[21~"; break;
    case VK_F11:     seq = "\033[23~"; break;
    case VK_F12:     seq = "\033[24~"; break;
    default: return false;
    }

    if (seq) {
        SendVTSequence(seq);
        return true;
    }
    return false;
}

// ─── Helpers ───

void ImeHandler::SendWchar(wchar_t ch) {
    wchar_t wstr[2] = { ch, 0 };
    m_send(std::wstring(wstr, 1));
}

void ImeHandler::SendText(const std::wstring& text) {
    if (!text.empty()) m_send(text);
}

void ImeHandler::SendVTSequence(const char* seq) {
    // VT sequence는 ASCII이므로 wchar로 변환
    std::wstring ws;
    while (*seq) ws += static_cast<wchar_t>(*seq++);
    m_send(ws);
}

} // namespace ghostwin
