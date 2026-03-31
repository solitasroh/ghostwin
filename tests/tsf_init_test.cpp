// TSF COM Initialization Test
// Verifies TSF can be created and initialized outside WinUI3 context
// Tests: ThreadMgr, DocumentMgr, Context creation, Focus

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <msctf.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>

using Microsoft::WRL::ComPtr;

static int g_passed = 0;
static int g_failed = 0;

#define TEST(name, expr) do { \
    printf("[%s] %s\n", (expr) ? "PASS" : "FAIL", name); \
    if (expr) g_passed++; else g_failed++; \
} while(0)

EXTERN_C const GUID GUID_PROP_COMPOSING;

int main() {
    printf("=== TSF COM Initialization Test ===\n\n");

    // 1. COM 초기화 (STA — TSF 필수)
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    TEST("COM STA initialized", SUCCEEDED(hr));
    if (FAILED(hr)) {
        printf("FATAL: COM init failed 0x%08lX\n", hr);
        return 1;
    }

    // 2. ThreadMgr 생성
    ComPtr<ITfThreadMgrEx> threadMgr;
    hr = CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(threadMgr.GetAddressOf()));
    TEST("ITfThreadMgrEx created", SUCCEEDED(hr) && threadMgr);

    // 3. CategoryMgr 생성
    ComPtr<ITfCategoryMgr> catMgr;
    hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(catMgr.GetAddressOf()));
    TEST("ITfCategoryMgr created", SUCCEEDED(hr) && catMgr);

    // 4. DisplayAttributeMgr 생성
    ComPtr<ITfDisplayAttributeMgr> dispMgr;
    hr = CoCreateInstance(CLSID_TF_DisplayAttributeMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(dispMgr.GetAddressOf()));
    TEST("ITfDisplayAttributeMgr created", SUCCEEDED(hr) && dispMgr);

    // 5. ThreadMgr Activate (TF_TMAE_CONSOLE)
    TfClientId clientId = TF_CLIENTID_NULL;
    if (threadMgr) {
        hr = threadMgr->ActivateEx(&clientId,
            TF_TMAE_NOACTIVATETIP | TF_TMAE_CONSOLE);
        TEST("ThreadMgr activated (TF_TMAE_CONSOLE)", SUCCEEDED(hr));
        printf("  clientId = %lu\n", clientId);
    }

    // 6. DocumentMgr 생성
    ComPtr<ITfDocumentMgr> docMgr;
    if (threadMgr) {
        hr = threadMgr->CreateDocumentMgr(docMgr.GetAddressOf());
        TEST("DocumentMgr created", SUCCEEDED(hr) && docMgr);
    }

    // 7. Context 생성 (ITfContextOwnerCompositionSink 없이 null로 — 테스트용)
    ComPtr<ITfContext> context;
    TfEditCookie ecTextStore = 0;
    if (docMgr) {
        hr = docMgr->CreateContext(clientId, 0, nullptr,
                                   context.GetAddressOf(), &ecTextStore);
        TEST("Context created", SUCCEEDED(hr) && context);
        printf("  ecTextStore = %lu\n", ecTextStore);
    }

    // 8. Context Push
    if (docMgr && context) {
        hr = docMgr->Push(context.Get());
        TEST("Context pushed", SUCCEEDED(hr));
    }

    // 9. SetFocus
    if (threadMgr && docMgr) {
        hr = threadMgr->SetFocus(docMgr.Get());
        TEST("SetFocus succeeded", SUCCEEDED(hr));
    }

    // 10. GUID_PROP_COMPOSING 접근 가능 확인
    if (context) {
        ComPtr<ITfProperty> prop;
        hr = context->GetProperty(GUID_PROP_COMPOSING, prop.GetAddressOf());
        TEST("GUID_PROP_COMPOSING accessible", SUCCEEDED(hr) && prop);
    }

    // 11. GetStatus (TS_SS_TRANSITORY 확인)
    printf("\n--- TF_STATUS check ---\n");
    printf("  TS_SS_TRANSITORY = 0x%X\n", TS_SS_TRANSITORY);
    printf("  TS_SS_NOHIDDENTEXT = 0x%X\n", TS_SS_NOHIDDENTEXT);

    // Cleanup
    if (docMgr) docMgr->Pop(TF_POPF_ALL);
    if (threadMgr) threadMgr->Deactivate();

    CoUninitialize();

    printf("\n=== Results: %d passed, %d failed ===\n", g_passed, g_failed);
    return g_failed > 0 ? 1 : 0;
}
