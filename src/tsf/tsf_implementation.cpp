// GhostWin Terminal — TSF COM Implementation
// Based on Windows Terminal src/tsf/Implementation.cpp
// Core IME composition handling via Text Services Framework

#include "tsf/tsf_implementation.h"

#include <ctffunc.h>

// GUID_PROP_COMPOSING is defined in msctf.h / uuid.lib
// Use EXTERN_C reference to avoid LNK2005 duplicate definition
EXTERN_C const GUID GUID_PROP_COMPOSING;

namespace ghostwin {

// TSF 이벤트 카운터 (--test-ime에서 조합 시작 횟수 검증용)
std::atomic<int> g_tsf_composition_start_count{0};

// ─── Factory ───

TsfImplementation* TsfImplementation::Create() {
    auto* impl = new TsfImplementation();

    HRESULT hr;

    // 1. Category Manager
    hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(impl->m_categoryMgr.GetAddressOf()));
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to create CategoryMgr: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 2. Display Attribute Manager
    hr = CoCreateInstance(CLSID_TF_DisplayAttributeMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(impl->m_displayAttrMgr.GetAddressOf()));
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to create DisplayAttributeMgr: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 3. Thread Manager
    hr = CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(impl->m_threadMgr.GetAddressOf()));
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to create ThreadMgr: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 4. Activate with TF_TMAE_CONSOLE flag
    // TS_SS_TRANSITORY 컨텍스트 + CUAS 에뮬레이션 활성화
    hr = impl->m_threadMgr->ActivateEx(
        &impl->m_clientId,
        TF_TMAE_NOACTIVATETIP | TF_TMAE_CONSOLE);
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to activate ThreadMgr: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 5. Document Manager
    hr = impl->m_threadMgr->CreateDocumentMgr(
        impl->m_documentMgr.GetAddressOf());
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to create DocumentMgr: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 6. Context — ITfContextOwnerCompositionSink로 생성
    TfEditCookie ecTextStore = 0;
    hr = impl->m_documentMgr->CreateContext(
        impl->m_clientId, 0,
        static_cast<ITfContextOwnerCompositionSink*>(impl),
        impl->m_context.GetAddressOf(), &ecTextStore);
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to create Context: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 7. Advise sinks (ITfContextOwner + ITfTextEditSink)
    hr = impl->m_context.As(&impl->m_contextSource);
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to get ITfSource: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    hr = impl->m_contextSource->AdviseSink(
        IID_ITfContextOwner,
        static_cast<ITfContextOwner*>(impl),
        &impl->m_cookieContextOwner);
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to advise ITfContextOwner: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    hr = impl->m_contextSource->AdviseSink(
        IID_ITfTextEditSink,
        static_cast<ITfTextEditSink*>(impl),
        &impl->m_cookieTextEditSink);
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to advise ITfTextEditSink: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    // 8. Push context
    hr = impl->m_documentMgr->Push(impl->m_context.Get());
    if (FAILED(hr)) {
        LOG_E("tsf", "Failed to push context: 0x%08lX", hr);
        delete impl;
        return nullptr;
    }

    LOG_I("tsf", "TSF initialized (clientId=%lu)", impl->m_clientId);
    return impl;
}

// ─── FindWindowOfActiveTSF (WT 패턴) ───
// WinUI3 InputSite HWND 탐색: 기존 DocumentMgr → Context → View → HWND
// 우리 자신의 DocumentMgr은 건너뜀

HWND TsfImplementation::FindWindowOfActiveTSF() {
    if (!m_threadMgr) return nullptr;

    ComPtr<IEnumTfDocumentMgrs> enumDocMgrs;
    HRESULT hr = m_threadMgr->EnumDocumentMgrs(enumDocMgrs.GetAddressOf());
    if (FAILED(hr) || !enumDocMgrs) return nullptr;

    ComPtr<ITfDocumentMgr> docMgr;
    while (enumDocMgrs->Next(1, docMgr.ReleaseAndGetAddressOf(), nullptr) == S_OK) {
        // 우리 DocumentMgr은 건너뜀
        if (docMgr.Get() == m_documentMgr.Get()) continue;

        ComPtr<ITfContext> ctx;
        if (FAILED(docMgr->GetTop(ctx.GetAddressOf())) || !ctx) continue;

        ComPtr<ITfContextView> view;
        if (FAILED(ctx->GetActiveView(view.GetAddressOf())) || !view) continue;

        HWND hwnd = nullptr;
        if (SUCCEEDED(view->GetWnd(&hwnd)) && hwnd) {
            LOG_I("tsf", "Found InputSite HWND: %p", hwnd);
            return hwnd;
        }
    }

    LOG_W("tsf", "No InputSite HWND found (WinUI3 not ready?)");
    return nullptr;
}

// ─── Focus Management (AssociateFocus 기반) ───
// SetFocus → WinUI3 내부 TSF와 충돌
// AssociateFocus → HWND에 포커스 올 때 자동 활성화, WinUI3와 공존

void TsfImplementation::Focus(IDataProvider* provider) {
    m_provider = provider;

    // provider->GetHwnd(): 앱이 제공하는 입력 전용 HWND 사용
    // WinUI3 InputSite가 아닌 자체 HWND이므로 TSF 충돌 없음
    HWND hwnd = provider ? provider->GetHwnd() : nullptr;
    if (!hwnd || !m_threadMgr || !m_documentMgr) {
        LOG_W("tsf", "Cannot focus: hwnd=%p", hwnd);
        return;
    }

    HRESULT hr = m_threadMgr->AssociateFocus(
        hwnd, m_documentMgr.Get(), m_prevDocMgr.ReleaseAndGetAddressOf());
    if (FAILED(hr)) {
        LOG_E("tsf", "AssociateFocus failed: 0x%08lX", hr);
        return;
    }
    m_associatedHwnd = hwnd;

    // 자체 HWND이므로 SetFocus 안전 (WinUI3 충돌 없음)
    m_threadMgr->SetFocus(m_documentMgr.Get());

    ComPtr<ITfDocumentMgr> currentFocus;
    m_threadMgr->GetFocus(currentFocus.GetAddressOf());
    LOG_I("tsf", "Focus OK (hwnd=%p, active=%s)",
          hwnd, (currentFocus.Get() == m_documentMgr.Get()) ? "ours" : "other");
}

void TsfImplementation::Unfocus(IDataProvider* provider) {
    if (m_provider == provider) {
        m_provider = nullptr;
    }
    // AssociateFocus 해제: null documentMgr로 복원
    if (m_associatedHwnd && m_threadMgr) {
        ComPtr<ITfDocumentMgr> prev;
        m_threadMgr->AssociateFocus(
            m_associatedHwnd, nullptr, prev.GetAddressOf());
        m_associatedHwnd = nullptr;
        m_prevDocMgr.Reset();
    }
    LOG_I("tsf", "Focus removed");
}

// ─── CancelComposition ───
// Ctrl+C, Escape 등에서 호출: 조합 텍스트를 폐기하고 TSF 내부 상태 초기화
// ConPTY에는 조합 텍스트를 보내지 않음 (폐기)

void TsfImplementation::CancelComposition() {
    if (m_compositions <= 0) return;

    LOG_I("tsf", "CancelComposition: discarding composing='%ls' (count=%d)",
          m_lastComposing.c_str(), m_compositions);

    // 카운터 및 조합 상태 초기화
    m_compositions = 0;
    m_lastComposing.clear();
    m_directOutputSent = false;
    m_pendingDirectSend.clear();
    m_pendingDirectSendActive = false;

    // 프리뷰 오버레이 클리어
    if (m_provider) {
        CompositionPreview empty;
        empty.active = false;
        m_provider->HandleCompositionUpdate(empty);
    }
}

// ─── Shutdown ───

void TsfImplementation::Shutdown() {
    // AssociateFocus 해제
    if (m_associatedHwnd && m_threadMgr) {
        ComPtr<ITfDocumentMgr> prev;
        m_threadMgr->AssociateFocus(
            m_associatedHwnd, nullptr, prev.GetAddressOf());
        m_associatedHwnd = nullptr;
        m_prevDocMgr.Reset();
    }

    if (m_contextSource) {
        if (m_cookieContextOwner != TF_INVALID_COOKIE) {
            m_contextSource->UnadviseSink(m_cookieContextOwner);
            m_cookieContextOwner = TF_INVALID_COOKIE;
        }
        if (m_cookieTextEditSink != TF_INVALID_COOKIE) {
            m_contextSource->UnadviseSink(m_cookieTextEditSink);
            m_cookieTextEditSink = TF_INVALID_COOKIE;
        }
    }
    if (m_documentMgr) {
        m_documentMgr->Pop(TF_POPF_ALL);
    }
    if (m_threadMgr) {
        m_threadMgr->Deactivate();
    }
    m_provider = nullptr;
    LOG_I("tsf", "TSF shutdown");
}

// ─── IUnknown ───

STDMETHODIMP TsfImplementation::QueryInterface(REFIID riid, void** ppvObj) noexcept {
    if (!ppvObj) return E_POINTER;

    if (riid == IID_IUnknown || riid == IID_ITfContextOwner) {
        *ppvObj = static_cast<ITfContextOwner*>(this);
    } else if (riid == IID_ITfContextOwnerCompositionSink) {
        *ppvObj = static_cast<ITfContextOwnerCompositionSink*>(this);
    } else if (riid == IID_ITfTextEditSink) {
        *ppvObj = static_cast<ITfTextEditSink*>(this);
    } else {
        *ppvObj = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) TsfImplementation::AddRef() noexcept {
    return ++m_refCount;
}

STDMETHODIMP_(ULONG) TsfImplementation::Release() noexcept {
    auto ref = --m_refCount;
    if (ref == 0) {
        Shutdown();
        delete this;
    }
    return ref;
}

// ─── ITfContextOwner ───

STDMETHODIMP TsfImplementation::GetACPFromPoint(const POINT*, DWORD, LONG*) {
    return E_NOTIMPL;
}

STDMETHODIMP TsfImplementation::GetTextExt(LONG, LONG, RECT* prc, BOOL* pfClipped) {
    if (!prc || !pfClipped) return E_INVALIDARG;
    *pfClipped = FALSE;

    if (m_provider) {
        try { *prc = m_provider->GetCursorPosition(); }
        catch (...) { *prc = {}; }
    } else {
        *prc = {};
    }
    return S_OK;
}

STDMETHODIMP TsfImplementation::GetScreenExt(RECT* prc) {
    if (!prc) return E_INVALIDARG;

    if (m_provider) {
        try { *prc = m_provider->GetViewport(); }
        catch (...) { *prc = {}; }
    } else {
        *prc = {};
    }
    return S_OK;
}

STDMETHODIMP TsfImplementation::GetStatus(TF_STATUS* pdcs) {
    if (!pdcs) return E_INVALIDARG;
    // TS_SS_TRANSITORY: 터미널은 비상주 컨텍스트 (이전 텍스트 접근 불가)
    // TS_SS_NOHIDDENTEXT: 숨겨진 텍스트 없음
    pdcs->dwDynamicFlags = 0;
    pdcs->dwStaticFlags = TS_SS_TRANSITORY | TS_SS_NOHIDDENTEXT;
    return S_OK;
}

STDMETHODIMP TsfImplementation::GetWnd(HWND* phwnd) {
    if (!phwnd) return E_INVALIDARG;
    *phwnd = m_provider ? m_provider->GetHwnd() : nullptr;
    return S_OK;
}

STDMETHODIMP TsfImplementation::GetAttribute(REFGUID, VARIANT*) {
    return E_NOTIMPL;
}

// ─── ITfContextOwnerCompositionSink ───

STDMETHODIMP TsfImplementation::OnStartComposition(ITfCompositionView*, BOOL* pfOk) {
    if (pfOk) *pfOk = TRUE;
    m_compositions++;
    g_tsf_composition_start_count.fetch_add(1, std::memory_order_relaxed);

    // 지연 전송 취소: 종성 분리(한→하+나) 시 OnEndComposition→OnStartComposition 연속
    // DoCompositionUpdate가 정확한 finalizedText를 전송하므로 pending 취소
    if (m_pendingDirectSendActive) {
        LOG_I("tsf", "Pending direct send cancelled (syllable transition): '%ls'",
              m_pendingDirectSend.c_str());
        m_pendingDirectSend.clear();
        m_pendingDirectSendActive = false;
    }

    LOG_I("tsf", "Composition started (count=%d, total=%d)",
          m_compositions, g_tsf_composition_start_count.load(std::memory_order_relaxed));
    return S_OK;
}

STDMETHODIMP TsfImplementation::OnUpdateComposition(ITfCompositionView*, ITfRange*) {
    return S_OK;
}

STDMETHODIMP TsfImplementation::OnEndComposition(ITfCompositionView*) {
    m_compositions--;
    if (m_compositions < 0) m_compositions = 0;  // 방어적 클램프
    LOG_I("tsf", "Composition ended (count=%d)", m_compositions);

    if (m_compositions == 0) {
        // 모든 조합 종료.
        // 확정 텍스트 전송은 DoCompositionUpdate가 GUID_PROP_COMPOSING으로 분리하여 처리.
        // OnEndComposition에서는 pending send 하지 않음:
        //   - BS 취소: m_lastComposing="ㅎ" 이지만 전송하면 안 됨 (ghost 버그)
        //   - Space 확정: DoCompositionUpdate가 finalizedText로 이미 전송
        //   - 종성 분리: OnStartComposition이 이어지고 DoCompositionUpdate가 정확한 텍스트 전송
        LOG_I("tsf", "Composition ended (lastComposing='%ls')",
              m_lastComposing.c_str());
        m_lastComposing.clear();
        m_pendingDirectSend.clear();
        m_pendingDirectSendActive = false;

        // 프리뷰(오버레이) 클리어
        if (m_provider) {
            CompositionPreview empty;
            empty.active = false;
            m_provider->HandleCompositionUpdate(empty);
        }
    }
    // 중첩 조합(count=2→1): 비동기 프리뷰 갱신
    else if (m_context && !m_inEditSession) {
        auto proxy = new EditSessionProxy(this);
        HRESULT hrSession = S_OK;
        m_context->RequestEditSession(
            m_clientId, proxy,
            TF_ES_READWRITE | TF_ES_ASYNC,
            &hrSession);
        proxy->Release();
    }
    return S_OK;
}

// ─── SendPendingDirectSend ───
// WM_USER+50 핸들러에서 호출: pending이 active이면 전송
void TsfImplementation::SendPendingDirectSend() {
    if (!m_pendingDirectSendActive) return;

    if (!m_pendingDirectSend.empty() && m_provider) {
        LOG_I("tsf", "Finalized (deferred): '%ls' (%zu chars)",
              m_pendingDirectSend.c_str(), m_pendingDirectSend.size());
        m_provider->HandleOutput(m_pendingDirectSend);
        m_directOutputSent = true;  // DoCompositionUpdate 중복 방지
    }
    m_pendingDirectSend.clear();
    m_pendingDirectSendActive = false;
}

// ─── ITfTextEditSink ───

STDMETHODIMP TsfImplementation::OnEndEdit(
        ITfContext* pic, TfEditCookie ecReadOnly, ITfEditRecord*) {
    // Edit Session 재귀 방지: DoCompositionUpdate 내 SetText가
    // OnEndEdit을 트리거하면 무한 루프 발생
    if (m_inEditSession) return S_OK;

    // 조합 중이거나 잔여 확정 텍스트가 있을 수 있으므로 항상 Edit Session 요청
    // m_compositions == 0이어도 TSF가 확정 텍스트를 버퍼에 남겨둔 채
    // OnEndEdit을 호출하는 경우가 있음 (Space/Enter 확정의 일부 IME)
    if (m_context) {
        auto proxy = new EditSessionProxy(this);
        HRESULT hrSession = S_OK;
        m_context->RequestEditSession(
            m_clientId, proxy,
            TF_ES_READWRITE | TF_ES_ASYNC,
            &hrSession);
        proxy->Release();
    }
    return S_OK;
}

// ─── DoCompositionUpdate ───
// 핵심: TSF 컨텍스트에서 확정 텍스트와 조합 중 텍스트를 분리

HRESULT TsfImplementation::DoCompositionUpdate(TfEditCookie ec) {
    if (!m_context) return E_FAIL;

    // 재귀 방지: SetText → OnEndEdit → RequestEditSession → DoCompositionUpdate 루프 차단
    m_inEditSession = true;

    // m_compositions == 0이면 stale 비동기 세션 가능성 기록
    bool staleSession = (m_compositions == 0);

    // 전체 텍스트 범위 가져오기
    ComPtr<ITfRange> fullRange;
    HRESULT hr = m_context->GetStart(ec, fullRange.GetAddressOf());
    if (FAILED(hr)) return hr;

    ComPtr<ITfRange> endRange;
    hr = m_context->GetEnd(ec, endRange.GetAddressOf());
    if (FAILED(hr)) return hr;

    hr = fullRange->ShiftEndToRange(ec, endRange.Get(), TF_ANCHOR_END);
    if (FAILED(hr)) return hr;

    // GUID_PROP_COMPOSING 속성으로 조합/비조합 구간 구분
    ComPtr<ITfProperty> propComposing;
    hr = m_context->GetProperty(GUID_PROP_COMPOSING, propComposing.GetAddressOf());

    std::wstring finalizedText;
    std::wstring composingText;

    if (propComposing) {
        ComPtr<IEnumTfRanges> enumRanges;
        hr = propComposing->EnumRanges(ec, enumRanges.GetAddressOf(), fullRange.Get());

        if (SUCCEEDED(hr) && enumRanges) {
            ITfRange* subRange = nullptr;
            while (enumRanges->Next(1, &subRange, nullptr) == S_OK) {
                VARIANT var{};
                hr = propComposing->GetValue(ec, subRange, &var);

                bool isComposing = false;
                if (SUCCEEDED(hr) && var.vt == VT_I4) {
                    isComposing = (var.lVal != 0);
                }
                VariantClear(&var);

                std::wstring rangeText = GetTextFromRange(ec, subRange);

                if (isComposing) {
                    composingText += rangeText;
                } else {
                    // 확정된 텍스트 (조합 아님)
                    finalizedText += rangeText;
                }

                subRange->Release();
            }
        }
    } else {
        // 속성 없으면 전체가 확정 텍스트
        finalizedText = GetTextFromRange(ec, fullRange.Get());
    }

    // 확정 텍스트가 있으면 앱에 전달하고 TSF 버퍼에서 제거
    // 단, OnEndComposition에서 이미 직접 전송된 경우 중복 건너뜀
    if (!finalizedText.empty() && m_provider) {
        if (m_directOutputSent) {
            LOG_I("tsf", "Finalized (skip dup): '%ls'", finalizedText.c_str());
            m_directOutputSent = false;
        } else {
            LOG_I("tsf", "Finalized: '%ls' (%zu chars)",
                  finalizedText.c_str(), finalizedText.size());
            m_provider->HandleOutput(finalizedText);
        }

        // TSF 버퍼에서 확정 텍스트 제거
        ComPtr<ITfRange> startRange;
        m_context->GetStart(ec, startRange.GetAddressOf());
        if (startRange) {
            LONG shifted = 0;
            startRange->ShiftEnd(ec, static_cast<LONG>(finalizedText.size()),
                                 &shifted, nullptr);
            startRange->SetText(ec, 0, L"", 0);
        }
    }

    // 마지막 조합 텍스트 추적 (OnEndComposition에서 확정 전송용)
    if (!staleSession) {
        m_lastComposing = composingText;
    }

    // 조합 미리보기 업데이트
    // Race condition 방어: stale session(m_compositions==0)에서는 오버레이 갱신 스킵
    // 단, composingText가 비어있으면 BS 취소이므로 정리 필수
    if (m_provider && !staleSession) {
        CompositionPreview preview;
        preview.text = composingText;
        preview.cursor_offset = static_cast<uint32_t>(composingText.size());
        preview.active = !composingText.empty();
        m_provider->HandleCompositionUpdate(preview);

        if (!composingText.empty()) {
            LOG_I("tsf", "Composing: '%ls'", composingText.c_str());
        }
    } else if (staleSession && composingText.empty()) {
        // BS로 마지막 글자 제거 시: OnEndComposition이 먼저 호출되어
        // m_lastComposing="ㅎ"로 pending send가 이미 posted됨.
        // 여기서 pending을 취소하고 오버레이도 클리어해야 함.
        m_lastComposing.clear();
        if (m_pendingDirectSendActive) {
            LOG_I("tsf", "Pending send cancelled (BS removed last char)");
            m_pendingDirectSend.clear();
            m_pendingDirectSendActive = false;
        }
        if (m_provider) {
            CompositionPreview preview;
            preview.active = false;
            m_provider->HandleCompositionUpdate(preview);
        }
    } else if (staleSession && !composingText.empty()) {
        LOG_I("tsf", "DoCompositionUpdate: skip overlay (stale, comp='%ls')",
              composingText.c_str());
    }

    m_inEditSession = false;
    return S_OK;
}

// ─── Helper: extract text from ITfRange ───

std::wstring TsfImplementation::GetTextFromRange(TfEditCookie ec, ITfRange* range) {
    if (!range) return {};

    std::wstring result;
    wchar_t buf[128];
    ULONG fetched = 0;

    // Clone range to avoid modifying original
    ComPtr<ITfRange> clone;
    range->Clone(clone.GetAddressOf());
    if (!clone) return {};

    while (true) {
        HRESULT hr = clone->GetText(ec, TF_TF_MOVESTART, buf, ARRAYSIZE(buf), &fetched);
        if (FAILED(hr) || fetched == 0) break;
        result.append(buf, fetched);
    }

    return result;
}

// ─── EditSessionProxy ───

TsfImplementation::EditSessionProxy::EditSessionProxy(
        TsfImplementation* owner, TfEditCookie) : m_owner(owner) {}

STDMETHODIMP TsfImplementation::EditSessionProxy::QueryInterface(
        REFIID riid, void** ppvObj) noexcept {
    if (!ppvObj) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_ITfEditSession) {
        *ppvObj = static_cast<ITfEditSession*>(this);
        AddRef();
        return S_OK;
    }
    *ppvObj = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) TsfImplementation::EditSessionProxy::AddRef() noexcept {
    return ++m_refCount;
}

STDMETHODIMP_(ULONG) TsfImplementation::EditSessionProxy::Release() noexcept {
    auto ref = --m_refCount;
    if (ref == 0) delete this;
    return ref;
}

STDMETHODIMP TsfImplementation::EditSessionProxy::DoEditSession(TfEditCookie ec) {
    if (m_owner) {
        return m_owner->DoCompositionUpdate(ec);
    }
    return E_FAIL;
}

// ─── TsfHandle (pimpl wrapper) ───

TsfHandle::~TsfHandle() { Destroy(); }

TsfHandle::TsfHandle(TsfHandle&& other) noexcept : m_impl(other.m_impl) {
    other.m_impl = nullptr;
}

TsfHandle& TsfHandle::operator=(TsfHandle&& other) noexcept {
    if (this != &other) {
        Destroy();
        m_impl = other.m_impl;
        other.m_impl = nullptr;
    }
    return *this;
}

TsfHandle TsfHandle::Create() {
    TsfHandle handle;
    handle.m_impl = TsfImplementation::Create();
    return handle;
}

void TsfHandle::Focus(IDataProvider* provider) const {
    if (m_impl) m_impl->Focus(provider);
}

void TsfHandle::Unfocus(IDataProvider* provider) const {
    if (m_impl) m_impl->Unfocus(provider);
}

bool TsfHandle::HasActiveComposition() const noexcept {
    return m_impl ? m_impl->HasActiveComposition() : false;
}

void TsfHandle::CancelComposition() const {
    if (m_impl) m_impl->CancelComposition();
}

void TsfHandle::SendPendingDirectSend() const {
    if (m_impl) m_impl->SendPendingDirectSend();
}

void TsfHandle::Destroy() {
    if (m_impl) {
        m_impl->Shutdown();
        m_impl->Release();
        m_impl = nullptr;
    }
}

} // namespace ghostwin
