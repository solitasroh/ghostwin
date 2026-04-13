#pragma once

// GhostWin Terminal — TSF COM Implementation
// Based on Windows Terminal src/tsf/Implementation.h
// Implements ITfContextOwner, ITfContextOwnerCompositionSink, ITfTextEditSink

#include "tsf/tsf_handle.h"
#include "common/log.h"

#include <msctf.h>
#include <wrl/client.h>

namespace ghostwin {

// g_tsf_composition_start_count는 tsf_handle.h에서 extern 선언됨

using Microsoft::WRL::ComPtr;

struct TsfImplementation : ITfContextOwner,
                           ITfContextOwnerCompositionSink,
                           ITfTextEditSink {
    // Factory
    static TsfImplementation* Create();

    // Focus management (AssociateFocus 기반 — WT 패턴)
    void Focus(IDataProvider* provider);
    void Unfocus(IDataProvider* provider);
    bool HasActiveComposition() const noexcept { return m_compositions > 0; }

    // 조합 취소: Ctrl+키 등에서 조합 텍스트를 폐기하고 TSF 상태 초기화
    void CancelComposition();

    // 지연 전송: WM_USER+50에서 호출 — pending이 active이면 전송
    void SendPendingDirectSend();

    // Cleanup
    void Shutdown();

    // ─── IUnknown ───
    STDMETHODIMP QueryInterface(REFIID riid, void** ppvObj) noexcept override;
    STDMETHODIMP_(ULONG) AddRef() noexcept override;
    STDMETHODIMP_(ULONG) Release() noexcept override;

    // ─── ITfContextOwner ───
    STDMETHODIMP GetACPFromPoint(const POINT*, DWORD, LONG*) override;
    STDMETHODIMP GetTextExt(LONG, LONG, RECT* prc, BOOL* pfClipped) override;
    STDMETHODIMP GetScreenExt(RECT* prc) override;
    STDMETHODIMP GetStatus(TF_STATUS* pdcs) override;
    STDMETHODIMP GetWnd(HWND* phwnd) override;
    STDMETHODIMP GetAttribute(REFGUID, VARIANT*) override;

    // ─── ITfContextOwnerCompositionSink ───
    STDMETHODIMP OnStartComposition(ITfCompositionView*, BOOL* pfOk) override;
    STDMETHODIMP OnUpdateComposition(ITfCompositionView*, ITfRange*) override;
    STDMETHODIMP OnEndComposition(ITfCompositionView*) override;

    // ─── ITfTextEditSink ───
    STDMETHODIMP OnEndEdit(ITfContext* pic, TfEditCookie ecReadOnly,
                           ITfEditRecord* pEditRecord) override;

private:
    TsfImplementation() = default;
    ~TsfImplementation() = default;

    // 조합 업데이트 처리 (edit session 콜백)
    HRESULT DoCompositionUpdate(TfEditCookie ec);

    // 텍스트 범위에서 문자열 추출
    static std::wstring GetTextFromRange(TfEditCookie ec, ITfRange* range);

    ULONG m_refCount = 1;

    // TSF COM objects
    ComPtr<ITfThreadMgrEx> m_threadMgr;
    ComPtr<ITfDocumentMgr> m_documentMgr;
    ComPtr<ITfContext> m_context;
    ComPtr<ITfCategoryMgr> m_categoryMgr;
    ComPtr<ITfDisplayAttributeMgr> m_displayAttrMgr;
    ComPtr<ITfSource> m_contextSource;

    TfClientId m_clientId = TF_CLIENTID_NULL;
    DWORD m_cookieContextOwner = TF_INVALID_COOKIE;
    DWORD m_cookieTextEditSink = TF_INVALID_COOKIE;

    // 앱 데이터 제공자
    IDataProvider* m_provider = nullptr;

    // AssociateFocus 상태 (복원용)
    HWND m_associatedHwnd = nullptr;
    ComPtr<ITfDocumentMgr> m_prevDocMgr;

    // 조합 카운터 (중첩 조합 추적)
    int m_compositions = 0;

    // 마지막 조합 텍스트 (OnEndComposition에서 확정 전송용)
    // Edit Session이 TF_E_LOCKED로 실패할 때 이 값을 사용
    std::wstring m_lastComposing;

    // Edit Session 재귀 방지 (SetText → OnEndEdit → RequestEditSession 루프 차단)
    bool m_inEditSession = false;

    // 직접 전송 플래그: OnEndComposition에서 m_lastComposing으로 전송 후 설정
    // DoCompositionUpdate에서 중복 전송 방지
    bool m_directOutputSent = false;

    // 지연 전송 (PostMessage 방식): OnEndComposition → WM_USER+50
    // OnStartComposition이 그 사이에 오면 취소 (종성 분리 시 DoCompositionUpdate가 정확한 텍스트 전송)
    std::wstring m_pendingDirectSend;
    bool m_pendingDirectSendActive = false;

    // Edit session proxy for async callbacks
    class EditSessionProxy : public ITfEditSession {
    public:
        EditSessionProxy(TsfImplementation* owner, TfEditCookie ec = 0);

        STDMETHODIMP QueryInterface(REFIID riid, void** ppvObj) noexcept override;
        STDMETHODIMP_(ULONG) AddRef() noexcept override;
        STDMETHODIMP_(ULONG) Release() noexcept override;
        STDMETHODIMP DoEditSession(TfEditCookie ec) override;

    private:
        ULONG m_refCount = 1;
        TsfImplementation* m_owner;
    };
};

} // namespace ghostwin
