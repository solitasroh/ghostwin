#pragma once

// GhostWin Terminal — IME Input Handler
// InputPreTranslateKeyboardSource + WM_IME_COMPOSITION direct processing
// Based on Windows Terminal's WinUI3 IME architecture (WT검증 패턴)

#include <Windows.h>
#include <Unknwn.h>
#include <imm.h>
#include <Microsoft.UI.Input.InputPreTranslateSource.Interop.h>

#include <string>
#include <mutex>
#include <functional>

namespace ghostwin {

using namespace ABI::Microsoft::UI::Input;

// ConPTY에 텍스트를 전송하는 콜백
using SendInputCallback = std::function<void(const std::wstring&)>;

class ImeHandler : public IInputPreTranslateKeyboardSourceHandler {
public:
    ImeHandler(SendInputCallback send_callback);

    // 조합 중인 문자열 (RenderLoop에서 오버레이 렌더링용)
    std::wstring GetComposition() const;

    // ─── IUnknown ───
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override;
    STDMETHODIMP_(ULONG) AddRef() noexcept override;
    STDMETHODIMP_(ULONG) Release() noexcept override;

    // ─── IInputPreTranslateKeyboardSourceHandler ───
    STDMETHODIMP OnDirectMessage(
        IInputPreTranslateKeyboardSourceInterop* source,
        const MSG* msg, UINT keyboardModifiers,
        bool* handled) override;

    STDMETHODIMP OnTreeMessage(
        IInputPreTranslateKeyboardSourceInterop* source,
        const MSG* msg, UINT keyboardModifiers,
        bool* handled) override;

private:
    ULONG m_ref = 1;
    SendInputCallback m_send;

    mutable std::mutex m_mutex;
    std::wstring m_composition;  // 조합 중 텍스트 (GCS_COMPSTR)

    // 내부 처리
    bool HandleImeComposition(const MSG* msg);
    bool HandleChar(const MSG* msg, UINT modifiers);
    bool HandleKeyDown(const MSG* msg, UINT modifiers);

    // UTF-16 → UTF-8 → ConPTY
    void SendWchar(wchar_t ch);
    void SendText(const std::wstring& text);
    void SendVTSequence(const char* seq);
};

} // namespace ghostwin
