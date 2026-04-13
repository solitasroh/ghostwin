#pragma once

// GhostWin Terminal — TSF (Text Services Framework) handle
// Based on Windows Terminal src/tsf/Handle.h pattern
// Provides IDataProvider interface + pimpl TsfHandle wrapper

#include <Windows.h>
#include <string_view>
#include <string>
#include <mutex>
#include <cstdint>
#include <atomic>

namespace ghostwin {

// TSF 이벤트 카운터 (--test-ime에서 조합 시작 횟수 검증용)
extern std::atomic<int> g_tsf_composition_start_count;

// Forward declarations
struct TsfImplementation;

// Composition preview data — consumed by RenderLoop for overlay rendering
struct CompositionPreview {
    std::wstring text;        // 조합 중인 문자열 (예: "한")
    uint32_t cursor_offset;   // 조합 문자열 내 커서 위치
    bool active;              // 조합 진행 중 여부
};

// IDataProvider — TSF Implementation이 앱에 요청하는 인터페이스
// WT의 IDataProvider 패턴 차용 (Handle.h)
struct IDataProvider {
    virtual ~IDataProvider() = default;

    // TSF에게 HWND 제공 (IME 후보창 위치, 포커스)
    virtual HWND GetHwnd() = 0;

    // 터미널 뷰포트 영역 (스크린 좌표)
    virtual RECT GetViewport() = 0;

    // 커서 위치 (스크린 좌표 — IME 후보창 배치용)
    virtual RECT GetCursorPosition() = 0;

    // 확정된 텍스트 수신 → ConPTY에 전송
    virtual void HandleOutput(std::wstring_view text) = 0;

    // 조합 미리보기 업데이트 → 렌더러가 오버레이 표시
    virtual void HandleCompositionUpdate(const CompositionPreview& preview) = 0;
};

// TsfHandle — pimpl wrapper for TSF Implementation
// 생성, 포커스 관리, 조합 상태 조회
class TsfHandle {
public:
    TsfHandle() = default;
    ~TsfHandle();

    // Move only
    TsfHandle(TsfHandle&& other) noexcept;
    TsfHandle& operator=(TsfHandle&& other) noexcept;
    TsfHandle(const TsfHandle&) = delete;
    TsfHandle& operator=(const TsfHandle&) = delete;

    // Factory — TSF 초기화 (ThreadMgr, DocumentMgr, Context)
    static TsfHandle Create();

    // 포커스 관리
    void Focus(IDataProvider* provider) const;
    void Unfocus(IDataProvider* provider) const;

    // 조합 상태
    bool HasActiveComposition() const noexcept;

    // 조합 취소: 조합 텍스트 폐기 + TSF 상태 초기화
    void CancelComposition() const;

    // 지연 전송: WM_USER+50에서 호출 — pending 상태이면 확정 텍스트 전송
    void SendPendingDirectSend() const;

    /// Shutdown TSF (Deactivate ThreadMgr). Call on UI thread (STA) before
    /// engine destroy to prevent ITfThreadMgr activation count underflow.
    void Shutdown();

    explicit operator bool() const noexcept { return m_impl != nullptr; }

private:
    void Destroy();
    TsfImplementation* m_impl = nullptr;
};

} // namespace ghostwin
