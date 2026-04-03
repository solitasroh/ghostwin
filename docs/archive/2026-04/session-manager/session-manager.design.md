# session-manager Design Document

> **Summary**: Phase 5-A — 다중 ConPTY 세션 격리. SessionManager가 세션 생명주기(3-state + generation)를 관리하고, 활성 세션만 렌더링하며 비활성 세션은 VT 파싱만 수행.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-03
> **Status**: Draft (v1.0 — 코드 품질 개선 재설계)
> **Planning Doc**: [multi-session-ui.plan.md](../../01-plan/features/multi-session-ui.plan.md)
> **Research Doc**: [session-manager-architecture-research.md](../../00-research/session-manager-architecture-research.md)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | GhostWinApp이 단일 ConPtySession/VtCore/RenderState/TSF를 직접 소유. 탭 추가·전환 불가 |
| **Solution** | Session 구조체로 세션별 상태 격리, SessionManager가 vector\<Session\> 관리, 활성 세션 전환 API 제공 |
| **Function/UX Effect** | 내부 인프라 변경. 외부 동작은 단일 세션과 동일하나, Phase 5-B(TabSidebar)의 기반 |
| **Core Value** | 다중 세션의 핵심 기반. 이후 탭/Pane/복원 모두 SessionManager 위에 구축 |

---

## 1. Overview

### 1.1 Design Goals

1. **세션 격리**: 각 세션이 독립적인 ConPTY + VtCore + RenderState + TSF 상태를 보유
2. **활성 세션 전환**: O(1) 전환. 비활성 세션은 VT 파싱만 계속하고 렌더링 중단
3. **공유 인프라 유지**: DX11Renderer, GlyphAtlas, QuadBuilder는 전역 공유 (메모리 효율)
4. **기존 동작 무파괴**: 단일 세션 모드에서 Phase 4 테스트 10/10 PASS 유지
5. **Phase 5-B/D 확장 준비**: TabSidebar, PaneSplit이 SessionManager API로 세션 접근

### 1.2 Design Principles

- **최소 침습 리팩토링**: GhostWinApp 멤버를 Session 구조체로 이동, 기존 로직 최대한 보존
- **세션별 mutex (ADR-006 확장)**: 세션 간 lock 경합 제거
- **참조 구현 준수**: CMUX(lifecycle), Alacritty(FairMutex), WT(TermControl 분리) 패턴 종합
- **3-state lifecycle + generation**: CMUX PR #808 패턴 — stale 참조 방지, ghost terminal 방지
- **Lazy resize**: 비활성 세션은 activate 시점에 resize (WT/Alacritty 패턴)
- **Thread ownership 명시**: 모든 필드에 접근 가능 스레드를 문서화

### 1.3 Reference Architecture Comparison

| 항목 | CMUX | Alacritty | WT | **GhostWin 결정** |
|------|------|-----------|----|--------------------|
| 계층 | 5단계 | 2단계 | 3단계 | **3단계 (App→Tab→Session)** |
| 스레드/세션 | Main+Renderer+IO | Main+IO | Main+IO+Renderer | **Main+IO(세션당), Render 1개 공유** |
| Mutex | [추측] | FairMutex(lease) | til::shared_mutex | **세션별 독립 mutex** |
| Lifecycle | 3-state+generation | HashMap remove | Tab.Shutdown() | **3-state+generation (CMUX 패턴)** |
| 비활성 렌더 | CVDisplayLink 정지 | GL context switch | AtlasEngine per-tab | **active만 렌더** |
| Resize | 모든 surface | per-window | per-tab | **active 즉시 + 비활성 lazy** |

### 1.4 Plan과의 차이점

| Plan 기술 | Design 결정 | 변경 근거 |
|-----------|------------|-----------|
| Session에 `VtCore` 별도 필드 | ConPtySession 내부 소유 유지 | 기존 pimpl 아키텍처 존중. `ConPtySession::vt_core()` 접근자로 충분 |

---

## 2. Architecture

### 2.1 현재 구조 (Before — 단일 세션)

```
GhostWinApp
├── m_session: unique_ptr<ConPtySession>    ← 단일
├── m_state:   unique_ptr<TerminalRenderState> ← 단일
├── m_tsf:     TsfHandle                    ← 단일
├── m_tsf_data: SessionTsfAdapter              ← 단일
├── m_vt_mutex: mutex                       ← 단일    [없음: 코드상 이름 확인 필요]
├── m_composition: wstring                  ← 단일
├── m_ime_mutex: mutex                      ← 단일
│
├── m_renderer: unique_ptr<DX11Renderer>    ← 공유 (변경 없음)
├── m_atlas:    unique_ptr<GlyphAtlas>      ← 공유 (변경 없음)
├── m_staging:  vector<QuadInstance>         ← 공유 (변경 없음)
├── m_render_thread: thread                  ← 공유 (변경 없음)
└── m_input_hwnd: HWND                       ← 공유 (변경 없음)
```

### 2.2 목표 구조 (After — 다중 세션)

```
GhostWinApp
├── m_session_mgr: SessionManager
│   ├── sessions_: vector<unique_ptr<Session>>      [main thread only: push/erase/move]
│   │   ├── Session 0: { conpty, state, tsf, tsf_data, vt_mutex, ... }
│   │   ├── Session 1: { conpty, state, tsf, tsf_data, vt_mutex, ... }
│   │   └── Session N: { ... }
│   ├── active_idx_: atomic<uint32_t>               [main: store(release), render: load(acquire)]
│   ├── next_id_: SessionId                         [main thread only]
│   └── cleanup_queue_: vector<unique_ptr<Session>>  [cleanup_mutex_ 보호]
│
├── m_renderer: unique_ptr<DX11Renderer>     ← 공유 (불변)
├── m_atlas:    unique_ptr<GlyphAtlas>       ← 공유 (불변)
├── m_staging:  vector<QuadInstance>          ← 공유 (불변)
├── m_render_thread: thread                   ← 공유 (불변)
└── m_input_hwnd: HWND                        ← 공유 (불변)
```

### 2.3 Component Diagram

```
┌──────────────────────────────────────────────────────────┐
│                      GhostWinApp                          │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │                  SessionManager                     │  │
│  │                                                    │  │
│  │  ┌──────────┐  ┌──────────┐       ┌──────────┐   │  │
│  │  │Session 0 │  │Session 1 │  ...  │Session N │   │  │
│  │  │ ConPTY   │  │ ConPTY   │       │ ConPTY   │   │  │
│  │  │ VtCore   │  │ VtCore   │       │ VtCore   │   │  │
│  │  │ RenderSt │  │ RenderSt │       │ RenderSt │   │  │
│  │  │ TsfCtx   │  │ TsfCtx   │       │ TsfCtx   │   │  │
│  │  │ vt_mutex │  │ vt_mutex │       │ vt_mutex │   │  │
│  │  │ io_thrd ●│  │ io_thrd ●│       │ io_thrd ●│   │  │
│  │  └──────────┘  └──────────┘       └──────────┘   │  │
│  │       ▲ active                                    │  │
│  └───────┼────────────────────────────────────────────┘  │
│          │                                               │
│  ┌───────┴────────────────────────────────────────────┐  │
│  │              Shared Rendering Infra                 │  │
│  │  DX11Renderer ← GlyphAtlas ← QuadBuilder          │  │
│  │  render_thread (1개) — 활성 세션만 렌더링            │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │              Input Layer (공유)                     │  │
│  │  m_input_hwnd (Hidden Win32 HWND)                  │  │
│  │  TSF Focus → 활성 세션의 SessionTsfAdapter로 전환      │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### 2.4 Dependencies

| Component | Depends On | Purpose |
|-----------|-----------|---------|
| SessionManager | ConPtySession, TerminalRenderState, TsfHandle | 세션 생명주기 관리 |
| GhostWinApp | SessionManager | 기존 m_session 등을 SessionManager로 위임 |
| RenderLoop | SessionManager::active_session() | 활성 세션의 RenderState만 렌더링 |
| InputWndProc | SessionManager::active_session() | 활성 세션에 키보드 입력 전달 |

---

## 3. Data Model

### 3.1 Session 구조체

```cpp
// src/session/session.h
#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

// Forward declarations
namespace ghostwin {
class ConPtySession;
class TerminalRenderState;
class SessionManager;
struct TsfHandle;
struct IDataProvider;
}

namespace ghostwin {

/// 세션 고유 ID (0부터 단조 증가, 재사용 없음)
using SessionId = uint32_t;

/// 세션 생명주기 상태 (CMUX PR #808 패턴)
/// Live → Closing → Closed, 역방향 전이 없음
enum class SessionState : uint8_t {
    Live,      // 정상 동작. I/O thread 실행, 입력/렌더 가능
    Closing,   // 종료 진행 중. I/O thread 정리, 새 activate 거부
    Closed,    // 완전 종료. erase 대기 (소멸자만 남음)
};

/// 단일 터미널 세션 — ConPTY + VT 파서 + 렌더 상태 + IME 격리
///
/// Thread ownership:
///   - lifecycle, generation: main thread write(release), render thread read(acquire)
///   - conpty, state: main thread + I/O thread (vt_mutex 보호)
///   - tsf, tsf_data, composition: main thread only
///   - title, cwd: main thread only
///   - resize_pending, pending_cols, pending_rows: main thread only
struct Session {
    Session() = default;
    ~Session() = default;

    // Non-copyable, non-movable (mutex 소유)
    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    Session(Session&&) = delete;
    Session& operator=(Session&&) = delete;

    SessionId id = 0;

    // ─── Lifecycle (CMUX 패턴) ───
    // atomic: render thread가 lock 없이 읽고, main thread가 쓴다.
    // 순서 보장: generation을 먼저 증가(release) → lifecycle store(release)
    //           render thread: lifecycle load(acquire) → generation load(acquire)
    std::atomic<SessionState> lifecycle{SessionState::Live};
    std::atomic<uint32_t> generation{1};  // 상태 전이마다 증가.
                                          // {id, generation} 쌍으로 stale 참조 방지.

    // ─── 세션별 격리 상태 (GhostWinApp에서 이동) ───
    std::unique_ptr<ConPtySession> conpty;               // [main + I/O, vt_mutex 보호]
    std::unique_ptr<TerminalRenderState> state;          // [main + render, vt_mutex 보호]
    std::mutex vt_mutex;           // ADR-006: 세션별 독립 mutex

    // ─── TSF/IME 격리 [main thread only] ───
    TsfHandle tsf{};
    std::wstring composition;      // 조합 중 문자열
    std::mutex ime_mutex;          // [main(write) + render(read)]

    // ─── 메타데이터 [main thread only] ───
    std::wstring title;            // 탭 제목 (ConPTY title 또는 사용자 지정)
    std::wstring cwd;              // 현재 작업 디렉토리 (OSC 7 / 프로세스 쿼리)

    // ─── 환경변수 (Phase 6 훅 연동 대비) [main thread only] ───
    std::wstring env_session_id;   // "GHOSTWIN_SESSION_ID=<id>"

    // ─── Pending resize (Lazy resize 패턴) [main thread only] ───
    bool resize_pending = false;
    uint16_t pending_cols = 0;
    uint16_t pending_rows = 0;

    /// 상태 전이 (generation 자동 증가, memory order 보장)
    /// 호출 제한: main thread only
    void transition_to(SessionState new_state) {
        generation.fetch_add(1, std::memory_order_release);
        lifecycle.store(new_state, std::memory_order_release);
    }

    /// Live 상태에서만 activate 허용
    /// 호출: any thread (atomic read)
    [[nodiscard]] bool is_live() const {
        return lifecycle.load(std::memory_order_acquire) == SessionState::Live;
    }
};

} // namespace ghostwin
```

### 3.2 SessionRef — dangling-safe 세션 참조

TSF COM 런타임이 Unfocus() 후 비동기로 콜백할 수 있으므로, raw pointer 대신 `{id, generation}` 쌍으로 매 호출 시 유효성을 검증한다.

```cpp
// src/session/session.h (Session 아래에 추가)

namespace ghostwin {

/// dangling-safe 세션 참조
/// raw pointer 대신 {id, generation} + SessionManager*로 매 호출 시 유효성 검증.
struct SessionRef {
    SessionId id = 0;
    uint32_t generation = 0;
    SessionManager* mgr = nullptr;

    /// 유효한 Live 세션이면 포인터 반환, 아니면 nullptr.
    /// 내부 구현: SessionManager::get(id) → generation 비교 → is_live() 체크.
    /// 세션 수 ≤ 10이므로 O(n) 탐색 비용은 무시 가능.
    [[nodiscard]] Session* resolve() const;
};

} // namespace ghostwin
```

### 3.3 SessionTsfAdapter — SessionRef 기반 IDataProvider

기존 `GhostWinApp*` 기반에서 `SessionRef` 기반으로 전환. UTF-16→UTF-8 변환은 `HandleOutput` 내부에서 수행.

```cpp
// src/session/session.h (SessionRef 아래에 추가)

namespace ghostwin {

/// TSF IDataProvider 구현 — SessionRef로 안전하게 세션 접근
///
/// Thread ownership: main thread only (TSF 콜백은 항상 main thread)
///
/// Phase 5-D(PaneSplit) 확장 시:
///   get_viewport/get_cursor_pos 콜백이 Pane-aware 버전으로 교체 가능.
///   현재는 GhostWinApp 전체 영역을 반환.
struct SessionTsfAdapter : IDataProvider {
    SessionRef session_ref;
    HWND input_hwnd = nullptr;

    // 타입 별칭: heap allocation 회피를 위해 함수 포인터 + context 사용
    using RectFn = RECT(*)(void* ctx);
    RectFn get_viewport = nullptr;
    RectFn get_cursor_pos = nullptr;
    void* fn_context = nullptr;         // GhostWinApp* (non-owning)

    HWND GetHwnd() override { return input_hwnd; }

    RECT GetViewport() override {
        return get_viewport ? get_viewport(fn_context) : RECT{};
    }

    RECT GetCursorPosition() override {
        return get_cursor_pos ? get_cursor_pos(fn_context) : RECT{};
    }

    /// 확정 텍스트를 ConPTY에 전송.
    /// UTF-16 → UTF-8 변환은 여기서 수행 (WideCharToMultiByte).
    void HandleOutput(std::wstring_view text) override {
        auto* s = session_ref.resolve();
        if (!s) return;

        // UTF-16 → UTF-8 변환
        int len = WideCharToMultiByte(CP_UTF8, 0,
            text.data(), static_cast<int>(text.size()),
            nullptr, 0, nullptr, nullptr);
        if (len <= 0) return;

        // 스택 버퍼 최적화 (대부분의 IME 출력은 짧음)
        constexpr int kStackBufSize = 128;
        char stack_buf[kStackBufSize];
        char* buf = (len <= kStackBufSize) ? stack_buf : new char[len];

        WideCharToMultiByte(CP_UTF8, 0,
            text.data(), static_cast<int>(text.size()),
            buf, len, nullptr, nullptr);

        s->conpty->send_input({reinterpret_cast<const uint8_t*>(buf),
                               static_cast<size_t>(len)});

        if (buf != stack_buf) delete[] buf;
    }

    void HandleCompositionUpdate(const CompositionPreview& preview) override {
        auto* s = session_ref.resolve();
        if (!s) return;
        std::lock_guard lock(s->ime_mutex);
        s->composition = preview.text;
    }
};

} // namespace ghostwin
```

### 3.4 SessionManager 클래스

```cpp
// src/session/session_manager.h
#pragma once

#include "session.h"
#include <condition_variable>
#include <functional>
#include <optional>
#include <thread>
#include <vector>

namespace ghostwin {

/// 세션 생성 설정
struct SessionCreateParams {
    std::wstring shell_path;       // 빈 문자열 = auto-detect
    std::wstring initial_dir;      // 빈 문자열 = 현재 디렉토리
    uint16_t cols = 80;
    uint16_t rows = 24;
};

/// 세션 이벤트 콜백
///
/// 주의: 모든 콜백은 예외를 던지면 안 됨.
/// SessionManager 내부에서 try-catch로 감싸고 LOG_E 처리하지만,
/// 호출자는 noexcept를 보장할 것.
struct SessionEvents {
    // 함수 포인터 + context 패턴 (std::function heap 할당 회피)
    using SessionFn = void(*)(void* ctx, SessionId id);
    using ExitFn    = void(*)(void* ctx, SessionId id, uint32_t exit_code);
    using TitleFn   = void(*)(void* ctx, SessionId id, const std::wstring& title);
    using CwdFn     = void(*)(void* ctx, SessionId id, const std::wstring& cwd);

    void* context = nullptr;               // GhostWinApp* (non-owning)
    SessionFn on_created   = nullptr;
    SessionFn on_closed    = nullptr;
    SessionFn on_activated = nullptr;
    TitleFn   on_title_changed = nullptr;   // ConPTY title 변경 시 (향후 구현)
    CwdFn     on_cwd_changed   = nullptr;   // OSC 7 파싱 시 (향후 구현)

    /// I/O thread에서 자식 프로세스 종료 시 호출.
    /// 핸들러는 DispatcherQueue로 UI thread 전환 후 close_session 호출해야 함.
    ExitFn on_child_exit = nullptr;
};

class SessionManager {
public:
    explicit SessionManager(SessionEvents events = {});
    ~SessionManager();

    // Non-copyable, non-movable
    SessionManager(const SessionManager&) = delete;
    SessionManager& operator=(const SessionManager&) = delete;

    // ─── 세션 생명주기 [main thread only] ───

    /// 새 세션 생성. 성공 시 SessionId 반환.
    ///
    /// @param params     세션 설정 (셸 경로, 초기 디렉토리, 크기)
    /// @param input_hwnd TSF용 공유 HWND
    /// @param viewport_fn  TSF IDataProvider 콜백 (뷰포트 영역)
    /// @param cursor_fn    TSF IDataProvider 콜백 (커서 위치)
    /// @param fn_ctx       콜백 context (GhostWinApp*)
    ///
    /// @throws ConPTY/RenderState 생성 실패 시 예외 전파.
    ///         TSF 실패는 LOG_W 후 IME 불가 모드로 계속.
    [[nodiscard]] SessionId create_session(
        const SessionCreateParams& params,
        HWND input_hwnd,
        SessionTsfAdapter::RectFn viewport_fn,
        SessionTsfAdapter::RectFn cursor_fn,
        void* fn_ctx);

    /// 세션 닫기. 자식 프로세스 종료 + 자원 해제.
    /// 마지막 세션이면 false 반환 (호출자가 앱 종료 결정).
    [[nodiscard]] bool close_session(SessionId id);

    // ─── 활성 세션 ───

    /// 활성 세션 전환 [main thread only]. TSF Focus도 자동 전환.
    void activate(SessionId id);

    /// 현재 활성 세션 [any thread, atomic read].
    /// null 가능 — 세션 0개일 때.
    ///
    /// 구현: active_idx_ atomic load → sessions_[idx].get()
    /// render thread: 반드시 is_live() 체크 후 사용.
    [[nodiscard]] Session* active_session();
    [[nodiscard]] const Session* active_session() const;

    /// 활성 세션 ID [any thread].
    [[nodiscard]] SessionId active_id() const;

    // ─── 조회 ───

    /// ID로 세션 조회 (null 가능) [any thread, 단 main thread에서만 수정].
    [[nodiscard]] Session* get(SessionId id);
    [[nodiscard]] const Session* get(SessionId id) const;

    /// 전체 세션 수 [main thread only].
    [[nodiscard]] size_t count() const;

    /// 세션 ID 목록 (순서 = 생성 순) [main thread only].
    [[nodiscard]] std::vector<SessionId> ids() const;

    // ─── 크기 조정 [main thread only] ───

    /// 모든 세션에 cols/rows 전파 (창 resize 시).
    /// 활성 세션만 즉시 resize, 비활성은 activate 시 lazy 적용.
    void resize_all(uint16_t cols, uint16_t rows);

    // ─── 인덱스 기반 탐색 (탭 UI용) [main thread only] ───

    /// 인덱스 → SessionId (범위 초과 시 nullopt).
    [[nodiscard]] std::optional<SessionId> id_at(size_t index) const;

    /// SessionId → 인덱스 (미발견 시 nullopt).
    [[nodiscard]] std::optional<size_t> index_of(SessionId id) const;

    /// 다음/이전 세션으로 순환 전환.
    void activate_next();
    void activate_prev();

    // ─── Phase 5-B/D 확장 API ───

    /// 탭 드래그 순서 변경 [main thread only].
    /// active_idx_ 자동 재조정 (활성 세션 추적).
    void move_session(size_t from_index, size_t to_index);

    /// 개별 세션 resize [main thread only].
    /// Phase 5-D PaneSplit: 각 Pane이 다른 크기.
    void resize_session(SessionId id, uint16_t cols, uint16_t rows);

private:
    // ─── 데이터 멤버 ───
    std::vector<std::unique_ptr<Session>> sessions_;     // [main thread only: 구조 수정]
    std::atomic<uint32_t> active_idx_{0};                // [main: store(release), render: load(acquire)]
    SessionId next_id_ = 0;                              // [main thread only]
    SessionEvents events_;

    // ─── 비동기 cleanup ───
    // close_session에서 dying Session을 즉시 소멸하면 ConPtySession 소멸자의
    // I/O thread join + 자식 프로세스 대기(최대 5초)로 UI freeze 발생.
    // → 별도 cleanup 스레드에서 순차 소멸.
    //
    // 정책: **순차 처리** (단일 cleanup thread).
    //   - 10개 세션 연속 close 시 최악 50초이나, 실제로는 셸이 이미 종료된
    //     경우가 대부분이므로 I/O join은 즉시 완료 (< 100ms).
    //   - 10개 동시 close는 앱 종료 시나리오이며, ~SessionManager()가
    //     cleanup_thread를 join하므로 모든 세션 정리 완료를 보장.
    std::vector<std::unique_ptr<Session>> cleanup_queue_;   // [cleanup_mutex_ 보호]
    std::thread cleanup_thread_;
    std::mutex cleanup_mutex_;
    std::condition_variable cleanup_cv_;
    std::atomic<bool> cleanup_running_{true};

    void cleanup_worker();
    void enqueue_cleanup(std::unique_ptr<Session> dying);

    // ─── 내부 헬퍼 [main thread only] ───
    void switch_tsf_focus(Session* from, Session* to);
    void apply_pending_resize(Session* sess);

    using SessionIter = std::vector<std::unique_ptr<Session>>::iterator;
    SessionIter find_session(SessionId id);

    /// 지정 인덱스를 제외하고 가장 가까운 Live 세션의 ID를 반환.
    /// 미발견 시 nullopt.
    [[nodiscard]] std::optional<SessionId> find_next_live_id(size_t exclude_index) const;

    /// 이벤트 콜백 안전 호출 (예외 catch + LOG_E)
    void fire_event(SessionEvents::SessionFn fn, SessionId id);
};

} // namespace ghostwin
```

---

## 4. Thread Model

### 4.1 스레드 구조 (다중 세션)

```
┌─────────────────────────────────────────────────────────┐
│  Main Thread (WinUI3 DispatcherQueue)                   │
│  ├─ InputWndProc → active_session()->conpty->send_input │
│  ├─ TSF callbacks → active_session()->tsf_data          │
│  ├─ Resize timer → session_mgr.resize_all()             │
│  └─ Session lifecycle (create/close/activate)           │
├─────────────────────────────────────────────────────────┤
│  Render Thread (1개, 공유)                               │
│  └─ Loop:                                               │
│      active = session_mgr.active_session()              │
│      if (active && active->is_live())                   │
│        gen = active->generation.load(acquire)           │
│        dirty = active->state->start_paint(              │
│                    active->vt_mutex, active->conpty.vt)  │
│        if (dirty && gen unchanged) build quads → GPU    │
│      else Sleep(16)                                     │
├─────────────────────────────────────────────────────────┤
│  I/O Thread 0 (Session 0 전용)                          │
│  └─ ReadFile(conpty_0.output) → vt_core_0.write()       │
│     lock: session_0.vt_mutex                            │
├─────────────────────────────────────────────────────────┤
│  I/O Thread 1 (Session 1 전용)                          │
│  └─ ReadFile(conpty_1.output) → vt_core_1.write()       │
│     lock: session_1.vt_mutex                            │
├─────────────────────────────────────────────────────────┤
│  Cleanup Thread (1개)                                    │
│  └─ cleanup_queue_에서 dying Session 순차 소멸           │
│     (I/O thread join + 자식 프로세스 대기)                │
└─────────────────────────────────────────────────────────┘
```

### 4.2 동기화 규칙

| Lock | 소유자 | 보호 대상 | 획득 위치 |
|------|--------|-----------|-----------|
| `session.vt_mutex` | 각 Session | VtCore read/write, RenderState | I/O thread (write), Render thread (start_paint), Main thread (resize) |
| `session.ime_mutex` | 각 Session | composition 문자열 | Main thread (TSF callback write), Render thread (overlay read) |
| `cleanup_mutex_` | SessionManager | cleanup_queue_ | Main thread (enqueue), Cleanup thread (dequeue) |

**핵심 불변식**:
1. 서로 다른 세션의 vt_mutex는 독립. 세션 간 lock 경합 없음.
2. `vt_mutex`와 `ime_mutex`를 동시에 잡는 코드 경로 없음 — deadlock 불가.
   - RenderLoop: vt_mutex lock → start_paint → unlock → ime_mutex lock → composition 읽기 → unlock
   - 두 mutex를 nested로 잡지 않음.
3. `cleanup_mutex_`는 sessions_ vector와 무관. sessions_는 main thread only.

### 4.3 Thread Ownership 요약

| 필드 | Main | Render | I/O | Cleanup |
|------|:----:|:------:|:---:|:-------:|
| `sessions_` (vector 구조) | RW | — | — | — |
| `active_idx_` | store(release) | load(acquire) | — | — |
| `session.lifecycle` | store(release) | load(acquire) | — | — |
| `session.generation` | fetch_add(release) | load(acquire) | — | — |
| `session.conpty` | RW | — | R (vt_mutex) | 소멸 |
| `session.state` | RW | R (vt_mutex) | — | — |
| `session.vt_mutex` | lock | lock | lock | — |
| `session.ime_mutex` | lock(W) | lock(R) | — | — |
| `session.tsf*` | RW | — | — | — |
| `session.title/cwd` | RW | — | — | — |
| `session.resize_*` | RW | — | — | — |
| `cleanup_queue_` | lock(W) | — | — | lock(RW) |

### 4.4 비활성 세션 동작

| 구성요소 | 활성 세션 | 비활성 세션 |
|----------|-----------|-------------|
| I/O Thread | 실행 (ConPTY 출력 읽기) | **실행** (VT 파싱 계속) |
| VtCore | write + render state 업데이트 | **write만** (데이터 누적) |
| RenderState | start_paint 호출 | **호출 안 함** (더티 누적) |
| GPU 렌더링 | QuadBuilder + Draw | **안 함** |
| TSF | Focused | **Unfocused** |

비활성 세션의 I/O thread가 계속 VtCore에 데이터를 쓰므로, 세션 전환 시 즉시 최신 상태를 렌더링할 수 있음.

### 4.5 Render Thread ↔ SessionManager 동기화

**문제**: Main thread가 `active_idx_`를 변경하거나 `sessions_.erase()`를 수행하면 render thread가 stale 포인터에 접근할 수 있음.

**해결**: Atomic snapshot + generation 이중 검증

```cpp
// Render Thread (매 프레임) — 유일한 구현. 4.1 다이어그램과 동일.
void GhostWinApp::RenderLoop() {
    while (m_render_running.load(std::memory_order_acquire)) {
        // 1. active_session()이 내부적으로 atomic load + bounds check 수행
        Session* active = m_session_mgr.active_session();

        // 2. null 또는 Closing/Closed → 렌더 스킵
        if (!active || !active->is_live()) {
            Sleep(16);
            continue;
        }

        // 3. generation 스냅샷 (렌더 중 세션 교체 감지용)
        uint32_t gen = active->generation.load(std::memory_order_acquire);

        // 4. vt_mutex lock → start_paint (기존과 동일)
        bool dirty = active->state->start_paint(
            active->vt_mutex,
            active->conpty->vt_core());

        if (!dirty) { Sleep(1); continue; }

        // 5. generation 재검증 — 세션 상태 전이 발생 시 이번 프레임 폐기
        if (active->generation.load(std::memory_order_acquire) != gen) continue;

        // 6. IME 조합 오버레이 (별도 mutex — vt_mutex와 nested 아님)
        {
            std::lock_guard lock(active->ime_mutex);
            // active->composition 읽기 → 오버레이 빌드
        }

        // 7. QuadBuilder + GPU draw (기존과 동일)
        // ...
    }
}
```

**핵심 불변식**:
- `sessions_` vector 수정(push/erase)은 **main thread 전용**
- render thread는 `active_idx_` (atomic)과 `session->lifecycle` (atomic)만 읽음
- `sessions_.erase()` 후 dying Session은 cleanup 큐에서 살아있으므로, render thread가 1프레임 지연 참조해도 포인터는 유효
- generation 불일치 시 프레임을 폐기하여 partial render 방지

### 4.6 앱 종료 순서

```
1. m_render_running.store(false, release)  → Render Thread 루프 탈출
2. m_render_thread.join()                   → Render Thread 완전 정지
3. m_session_mgr.~SessionManager()
   → 모든 Session을 Closed로 전이
   → 모든 Session을 cleanup 큐로 이동
   → cleanup_running_ = false → cleanup_cv_ notify
   → cleanup_thread_.join() → 모든 ConPtySession 소멸 완료
4. m_renderer, m_atlas 등 GPU 리소스 해제
```

---

## 5. API Specification (SessionManager 메서드 상세)

### 5.1 create_session

```cpp
SessionId SessionManager::create_session(
    const SessionCreateParams& params,
    HWND input_hwnd,
    SessionTsfAdapter::RectFn viewport_fn,
    SessionTsfAdapter::RectFn cursor_fn,
    void* fn_ctx)
{
    // 1. Session 할당
    auto sess = std::make_unique<Session>();
    sess->id = next_id_++;
    sess->env_session_id = L"GHOSTWIN_SESSION_ID=" + std::to_wstring(sess->id);

    // 2. SessionTsfAdapter 설정 (함수 포인터 + context, heap 할당 없음)
    sess->tsf_data.session_ref = SessionRef{sess->id, sess->generation.load(), this};
    sess->tsf_data.input_hwnd = input_hwnd;
    sess->tsf_data.get_viewport = viewport_fn;
    sess->tsf_data.get_cursor_pos = cursor_fn;
    sess->tsf_data.fn_context = fn_ctx;

    // 3. TSF 핸들 생성 (실패 시 IME 불가 모드로 계속)
    try {
        sess->tsf = TsfHandle::Create();
    } catch (...) {
        LOG_W("TSF initialization failed for session %u, IME disabled", sess->id);
        // sess->tsf는 기본 상태로 유지 — IME 불가
    }

    // 4. RenderState 생성
    sess->state = std::make_unique<TerminalRenderState>(params.cols, params.rows);

    // 5. ConPTY 세션 생성 (I/O thread 자동 시작)
    //    on_exit: I/O thread에서 호출됨 → main thread로 디스패치 필요
    //    this 캡처 안전: SessionManager 소멸자가 모든 ConPtySession을
    //    cleanup_thread에서 join하므로, on_exit는 소멸 전에 반드시 완료됨.
    SessionConfig config{};
    config.cols = params.cols;
    config.rows = params.rows;
    config.shell_path = params.shell_path;
    config.initial_dir = params.initial_dir;
    config.on_exit = [this, id = sess->id](uint32_t exit_code) {
        // I/O thread에서 호출 — SessionEvents.on_child_exit로 전달
        // GhostWinApp의 핸들러가 DispatcherQueue로 UI thread 전환 후 close_session 호출
        fire_exit_event(id, exit_code);
    };
    sess->conpty = ConPtySession::create(config);
    // ConPTY 생성 실패 시 예외 전파 — 호출자가 사용자에게 오류 표시

    // 6. 벡터에 추가
    SessionId id = sess->id;
    sessions_.push_back(std::move(sess));

    // 7. 첫 세션이면 자동 활성화
    if (sessions_.size() == 1) {
        activate(id);
    }

    // 8. 이벤트 콜백 (예외 안전)
    fire_event(events_.on_created, id);

    return id;
}
```

### 5.2 activate

```cpp
void SessionManager::activate(SessionId id) {
    auto it = find_session(id);
    if (it == sessions_.end()) return;

    // Closing/Closed 세션은 활성화 거부 (CMUX lifecycle 규칙)
    if (!(*it)->is_live()) return;

    size_t new_index = static_cast<size_t>(std::distance(sessions_.begin(), it));
    uint32_t current_idx = active_idx_.load(std::memory_order_relaxed);
    if (new_index == current_idx && !sessions_.empty()) return;  // 이미 활성

    Session* old_active = active_session();

    // Lazy resize 적용 (비활성 중 누적된 pending resize)
    apply_pending_resize(it->get());

    // TSF 포커스 전환
    switch_tsf_focus(old_active, it->get());

    // atomic store — render thread가 다음 프레임부터 새 세션 렌더링
    active_idx_.store(static_cast<uint32_t>(new_index), std::memory_order_release);

    // 새 세션의 RenderState에 전체 더티 마크 (즉시 렌더링)
    if ((*it)->state) {
        (*it)->state->force_all_dirty();
    }

    fire_event(events_.on_activated, id);
}
```

### 5.3 close_session (2단계 종료 — CMUX lifecycle 패턴)

```
[Live] ──transition_to(Closing)──→ [Closing] ──transition_to(Closed)──→ [Closed] ──erase──→ 삭제
         generation++               activate 거부   generation++          벡터에서 제거
```

```cpp
bool SessionManager::close_session(SessionId id) {
    auto it = find_session(id);
    if (it == sessions_.end() || !(*it)->is_live()) return true;

    Session* sess = it->get();
    size_t closing_index = static_cast<size_t>(std::distance(sessions_.begin(), it));
    uint32_t current_active = active_idx_.load(std::memory_order_relaxed);
    bool was_active = (closing_index == current_active);

    // ─── Phase 1: Closing ───
    sess->transition_to(SessionState::Closing);

    // TSF Unfocus (활성이었다면)
    if (was_active) {
        sess->tsf.Unfocus(&sess->tsf_data);
    }

    // ─── Phase 2: 활성 인덱스 재조정 (erase 전에 수행!) ───
    // 핵심: erase 전에 active_idx_를 먼저 조정해야 render thread가 유효한 인덱스를 읽음.
    if (was_active) {
        auto next_id = find_next_live_id(closing_index);
        if (next_id) {
            // activate()가 active_idx_를 새 인덱스로 설정
            activate(*next_id);
        }
        // next_id 없음 = 마지막 세션. active_idx_는 0으로 유지 (sessions_ 비어짐).
    }

    // ─── Phase 3: Closed + vector에서 제거 ───
    sess->transition_to(SessionState::Closed);

    auto dying = std::move(*it);
    sessions_.erase(it);

    // erase로 인덱스가 shift됨 → active_idx_ 재조정
    // case 1: was_active → activate()가 이미 새 인덱스를 설정했으나,
    //         erase로 그 인덱스가 1 감소할 수 있음
    // case 2: !was_active → 닫힌 세션이 활성 세션보다 앞이면 1 감소
    uint32_t adj_active = active_idx_.load(std::memory_order_relaxed);
    if (closing_index < adj_active) {
        active_idx_.store(adj_active - 1, std::memory_order_release);
    } else if (was_active && !sessions_.empty()) {
        // activate()가 erase 전 인덱스를 기준으로 설정했으므로 재계산 필요
        auto new_it = find_session(*find_next_live_id(0));
        if (new_it != sessions_.end()) {
            size_t corrected = static_cast<size_t>(std::distance(sessions_.begin(), new_it));
            active_idx_.store(static_cast<uint32_t>(corrected), std::memory_order_release);
        }
    }

    // cleanup 큐: 별도 스레드에서 ConPtySession 소멸 (I/O join + 자식 대기)
    enqueue_cleanup(std::move(dying));

    fire_event(events_.on_closed, id);

    return !sessions_.empty();  // false = 마지막 세션 — 호출자가 앱 종료 결정
}
```

### 5.4 resize_all (Lazy Resize 패턴)

활성 세션만 즉시 resize, 비활성은 activate 시 lazy 적용.
근거: resize는 vt_mutex lock이 필요하므로 10개 세션 순차 lock은 지연 유발.

```cpp
void SessionManager::resize_all(uint16_t cols, uint16_t rows) {
    Session* current_active = active_session();  // 루프 밖에서 1회 캐싱

    for (auto& sess : sessions_) {
        if (!sess->is_live()) continue;

        if (sess.get() == current_active) {
            // 활성 세션: 즉시 resize
            std::lock_guard lock(sess->vt_mutex);
            sess->conpty->resize(cols, rows);
            sess->state->resize(cols, rows);
        } else {
            // 비활성 세션: pending으로 기록, activate 시 적용
            sess->resize_pending = true;
            sess->pending_cols = cols;
            sess->pending_rows = rows;
        }
    }
}
```

### 5.5 cleanup_worker (순차 소멸)

```cpp
void SessionManager::cleanup_worker() {
    while (true) {
        std::unique_lock lock(cleanup_mutex_);
        cleanup_cv_.wait(lock, [this] {
            return !cleanup_queue_.empty() || !cleanup_running_.load();
        });

        if (!cleanup_running_.load() && cleanup_queue_.empty()) return;

        // 하나씩 꺼내서 소멸 (lock 해제 후 소멸자 실행 — UI freeze 방지)
        std::unique_ptr<Session> dying;
        if (!cleanup_queue_.empty()) {
            dying = std::move(cleanup_queue_.front());
            cleanup_queue_.erase(cleanup_queue_.begin());
        }
        lock.unlock();

        // dying이 scope 끝에서 소멸 → ConPtySession::~Impl()이
        // I/O thread join + 자식 프로세스 대기(최대 shutdown_timeout_ms)
        dying.reset();
    }
}

void SessionManager::enqueue_cleanup(std::unique_ptr<Session> dying) {
    {
        std::lock_guard lock(cleanup_mutex_);
        cleanup_queue_.push_back(std::move(dying));
    }
    cleanup_cv_.notify_one();
}
```

### 5.6 fire_event (예외 안전 콜백 호출)

```cpp
void SessionManager::fire_event(SessionEvents::SessionFn fn, SessionId id) {
    if (!fn || !events_.context) return;
    try {
        fn(events_.context, id);
    } catch (...) {
        LOG_E("Session event callback threw exception for session %u", id);
    }
}
```

### 5.7 move_session (탭 순서 변경)

```cpp
void SessionManager::move_session(size_t from, size_t to) {
    if (from >= sessions_.size() || to >= sessions_.size() || from == to) return;

    uint32_t current_active = active_idx_.load(std::memory_order_relaxed);

    // 이동할 세션 추출
    auto moving = std::move(sessions_[from]);
    sessions_.erase(sessions_.begin() + from);
    sessions_.insert(sessions_.begin() + to, std::move(moving));

    // 활성 인덱스 추적 (활성 세션이 이동에 영향 받는 경우)
    uint32_t new_active = current_active;
    if (current_active == from) {
        new_active = static_cast<uint32_t>(to);
    } else {
        if (from < current_active && to >= current_active) new_active--;
        else if (from > current_active && to <= current_active) new_active++;
    }
    if (new_active != current_active) {
        active_idx_.store(new_active, std::memory_order_release);
    }
}
```

---

## 6. GhostWinApp 리팩토링

### 6.1 멤버 변경 요약

| Before (단일 세션) | After (SessionManager) | 변경 |
|--------------------|------------------------|------|
| `m_session` | `m_session_mgr.active_session()->conpty` | 삭제 → Session으로 이동 |
| `m_state` | `m_session_mgr.active_session()->state` | 삭제 → Session으로 이동 |
| `m_tsf` | `m_session_mgr.active_session()->tsf` | 삭제 → Session으로 이동 |
| `m_tsf_data` | `m_session_mgr.active_session()->tsf_data` | 삭제 → Session으로 이동 |
| `m_vt_mutex` | `session->vt_mutex` (각 세션별) | 삭제 → Session으로 이동 |
| `m_composition` | `session->composition` (각 세션별) | 삭제 → Session으로 이동 |
| `m_ime_mutex` | `session->ime_mutex` (각 세션별) | 삭제 → Session으로 이동 |
| `m_renderer` | `m_renderer` (불변) | 유지 |
| `m_atlas` | `m_atlas` (불변) | 유지 |
| `m_staging` | `m_staging` (불변) | 유지 |
| `m_render_thread` | `m_render_thread` (불변) | 유지 |
| `m_input_hwnd` | `m_input_hwnd` (불변) | 유지 |
| — | `m_session_mgr` (신규) | **추가** |

### 6.2 RenderLoop 변경

Section 4.5의 코드가 유일한 구현. 별도 버전 없음.

### 6.3 InputWndProc 변경

```cpp
// Before
case WM_CHAR:
    app->m_session->send_input(...);

// After
case WM_CHAR:
    if (auto* s = app->m_session_mgr.active_session(); s && s->is_live())
        s->conpty->send_input(...);
```

### 6.4 StartTerminal 변경

```cpp
// Before
void GhostWinApp::StartTerminal(uint32_t w, uint32_t h) {
    SessionConfig config{};
    config.cols = cols; config.rows = rows;
    m_session = ConPtySession::create(config);
    m_state = std::make_unique<TerminalRenderState>(cols, rows);
    m_tsf = TsfHandle::Create();
    // ...
}

// After — 함수 포인터 콜백 (std::function 대신)
static RECT GetViewportRectStatic(void* ctx) {
    return static_cast<GhostWinApp*>(ctx)->GetViewportRect();
}
static RECT GetCursorRectStatic(void* ctx) {
    return static_cast<GhostWinApp*>(ctx)->GetCursorRect();
}

void GhostWinApp::StartTerminal(uint32_t w, uint32_t h) {
    SessionCreateParams params{};
    params.cols = cols; params.rows = rows;
    m_session_mgr.create_session(params, m_input_hwnd,
        &GetViewportRectStatic, &GetCursorRectStatic, this);
    // 첫 세션 자동 활성화 (SessionManager 내부)
}
```

### 6.5 Resize 변경

```cpp
// Before
{
    std::lock_guard lock(m_vt_mutex);
    m_session->resize(cols, rows);
    m_state->resize(cols, rows);
}

// After
m_session_mgr.resize_all(cols, rows);
```

---

## 7. File Structure

### 7.1 신규 파일

```
src/
├── session/
│   ├── session.h              ← Session + SessionRef + SessionTsfAdapter
│   ├── session_manager.h      ← SessionManager 클래스 선언
│   └── session_manager.cpp    ← SessionManager 구현
```

### 7.2 수정 파일

| 파일 | 변경 내용 |
|------|-----------|
| `src/app/winui_app.h` | m_session/m_state/m_tsf 등 삭제, m_session_mgr 추가, static 콜백 함수 선언 |
| `src/app/winui_app.cpp` | RenderLoop, InputWndProc, StartTerminal, Resize 수정 |
| `CMakeLists.txt` | session 라이브러리 추가 |

### 7.3 CMake 변경

```cmake
# 신규: session 라이브러리
add_library(session STATIC
    src/session/session_manager.cpp
)
target_include_directories(session PUBLIC src/)
target_link_libraries(session PUBLIC conpty renderer)

# ghostwin_winui에 session 링크 추가
target_link_libraries(ghostwin_winui PRIVATE
    session renderer conpty  # session 추가
    # ... 기존 라이브러리 ...
)
```

---

## 8. Error Handling

### 8.1 세션 생성 실패

| 실패 지점 | 원인 | 처리 |
|-----------|------|------|
| ConPtySession::create | CreatePseudoConsole 실패, 셸 경로 미발견 | create_session이 예외 전파. 호출자가 사용자에게 오류 표시 |
| TsfHandle::Create | COM 초기화 실패 | try-catch → LOG_W, IME 불가 모드로 세션 생성 계속 |
| TerminalRenderState 할당 | OOM | std::bad_alloc 전파 |

### 8.2 자식 프로세스 종료

```
자식 종료 → on_exit 콜백 (I/O thread)
  → fire_exit_event(id, exit_code)
    → SessionEvents.on_child_exit(ctx, id, exit_code)
      → GhostWinApp: DispatcherQueue.TryEnqueue (UI thread로 전환)
        → close_session(id)
        → events_.on_closed(id) via fire_event (예외 안전)
        → (Phase 5-B) 탭 UI에 종료 표시 또는 자동 닫기
```

on_exit lambda의 `this` 캡처 안전 보장:
- SessionManager 소멸자가 모든 ConPtySession을 cleanup_thread에서 join
- on_exit 콜백은 I/O thread 종료 전에 완료됨
- 따라서 `this`는 on_exit 호출 시점에 항상 유효

### 8.3 마지막 세션 닫기

`close_session` 반환값 `false` → GhostWinApp이 `m_window.Close()` 호출하여 앱 종료.

### 8.4 이벤트 콜백 예외 안전

모든 이벤트 콜백은 `fire_event()` 래퍼를 통해 호출됨:
- `try-catch(...)` + `LOG_E`로 예외 전파 차단
- 콜백 예외가 SessionManager 내부 상태를 오염시키지 않음

---

## 9. Test Plan

### 9.1 Test Scope

| 유형 | 대상 | 방법 |
|------|------|------|
| 단위 | SessionManager create/close/activate | 기존 test 프레임워크 |
| 통합 | 다중 세션 ConPTY I/O | 2개 세션 생성 → 각각 입력/출력 검증 |
| 통합 | 세션 전환 + TSF 포커스 | activate → IME 입력 대상 확인 |
| 회귀 | Phase 4 기존 테스트 | 단일 세션 모드로 10/10 PASS 유지 |

### 9.2 Test Cases

#### 기본 동작

- [ ] **TC-01**: 세션 생성 → id 0 반환, count() == 1, active_id() == 0
- [ ] **TC-02**: 2개 세션 생성 → count() == 2, 첫 세션이 활성
- [ ] **TC-03**: activate(1) → active_id() == 1, 이전 세션 TSF Unfocus 확인
- [ ] **TC-04**: close_session(active) → 인접 세션 자동 활성화
- [ ] **TC-05**: 마지막 세션 close → 반환값 false
- [ ] **TC-06**: 활성 세션에 입력 전송 → 해당 ConPTY만 수신 확인
- [ ] **TC-07**: 비활성 세션 ConPTY 출력 → VtCore에 데이터 누적 확인
- [ ] **TC-08**: 세션 전환 후 렌더링 → 전환된 세션의 최신 상태 표시
- [ ] **TC-09**: resize_all → 활성 세션 즉시 resize + 비활성 pending 확인
- [ ] **TC-10**: 10개 세션 생성 → 메모리/스레드 안정성 (30초 유지)

#### Lifecycle 상태 전이

- [ ] **TC-11**: close_session → lifecycle == Closed, generation 증가 확인
- [ ] **TC-12**: Closing 상태 세션에 activate() → 활성화 거부 확인
- [ ] **TC-13**: Closing 중 create_session → 새 세션 정상 생성 확인
- [ ] **TC-14**: 세션 0개 상태에서 active_session() → nullptr 확인
- [ ] **TC-15**: ids(), id_at(), index_of() 정상 동작 (빈 목록, 1개, N개)
- [ ] **TC-16**: activate_next()/activate_prev() 순환 전환 확인
- [ ] **TC-17**: move_session() 드래그 순서 변경 + active_idx_ 추적 확인

#### 동시성

- [ ] **TC-18**: Render thread + Main thread close_session 동시 실행 → crash 없음
- [ ] **TC-19**: I/O thread vt_mutex 보유 중 resize_session → deadlock 없음
- [ ] **TC-20**: 비동기 cleanup 큐: 10개 세션 연속 close → 모든 I/O thread join 완료

#### NFR 측정

- [ ] **TC-21**: activate() 호출 → force_all_dirty() 완료: **< 50ms** (타임스탬프 계측)
- [ ] **TC-22**: 세션당 메모리 증가: **< 20MB** (GetProcessMemoryInfo 전후 비교)

#### 추가 (코드 품질 리뷰 반영)

- [ ] **TC-23**: 세션 0개 상태에서 resize_all() → crash 없음 (edge case)
- [ ] **TC-24**: move_session에서 활성 세션이 이동 → active_idx_ 정확 추적
- [ ] **TC-25**: close_session(0) 후 활성 세션 인덱스 shift 정확성 확인
- [ ] **TC-26**: SessionRef::resolve() — Closed 세션에 대해 nullptr 반환 확인

---

## 10. Implementation Order

### 10.1 단계별 구현

1. [ ] **Step 1: Session 구조체 + 파일 생성**
   - `src/session/session.h` — Session, SessionRef, SessionTsfAdapter 정의
   - `src/session/session_manager.h` — SessionManager 선언
   - `src/session/session_manager.cpp` — create/close/activate/resize_all/cleanup_worker 구현
   - `CMakeLists.txt` — session 라이브러리 추가

2. [ ] **Step 2: GhostWinApp 리팩토링 (단일 세션 동작 유지)**
   - m_session/m_state/m_tsf/m_vt_mutex 등 → m_session_mgr로 교체
   - StartTerminal → create_session 호출 (static 함수 포인터 콜백)
   - RenderLoop → active_session() + generation 이중 검증
   - InputWndProc → active_session() + is_live() 체크
   - Resize → resize_all 호출

3. [ ] **Step 3: 기존 테스트 통과 확인**
   - 단일 세션으로 Phase 4 테스트 10/10 PASS
   - --test-ime 모드 정상 동작 확인

4. [ ] **Step 4: 다중 세션 테스트**
   - TC-01 ~ TC-26 구현 및 검증
   - 10개 세션 안정성 테스트

---

## 11. Risks & Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| TSF DocumentMgr 다중 생성 시 COM 충돌 | 상 | 중 | WT 소스의 TermControl::_InitializeTerminal 참조. 스레드당 ThreadMgr 1개 유지 |
| close_session 중 erase → active_idx_ 범위 초과 | 상 | 중 | erase 전 active_idx_ 재조정. TC-25에서 검증 |
| on_exit lambda의 this 캡처 dangling | 상 | 하 | 소멸자에서 cleanup_thread join → 모든 on_exit 완료 보장 |
| 자식 프로세스 종료 콜백의 스레드 안전성 | 중 | 중 | DispatcherQueue.TryEnqueue로 UI 스레드 전환 후 처리 |
| 10개 세션 × I/O thread = 12+ 스레드 | 하 | 하 | ConPTY I/O는 대부분 블로킹 ReadFile. CPU 점유 최소 |
| cleanup_thread 순차 처리 시 10개 연속 close 최대 50초 | 하 | 하 | 실제로 셸 종료 후 join은 < 100ms. 앱 종료 시에만 발생 |

---

## 12. Coding Conventions

### 12.1 Naming

| 대상 | 규칙 | 예시 |
|------|------|------|
| 클래스 | PascalCase | `SessionManager`, `Session` |
| 멤버 변수 | snake_case + trailing _ | `sessions_`, `active_idx_` |
| 메서드 | snake_case | `create_session()`, `active_session()` |
| 타입 별칭 | PascalCase | `SessionId`, `SessionIter` |
| 상수 | kCamelCase | `kMaxSessions` |
| 파일 | snake_case | `session_manager.h` |

### 12.2 헤더 규칙

- `#pragma once` 사용
- 헤더에 구현 최소화 (인라인 trivial getter만 허용)
- forward declaration 적극 사용

### 12.3 Memory Ordering 규칙

| 변수 | Store | Load | 근거 |
|------|-------|------|------|
| `active_idx_` | `release` | `acquire` | render thread가 새 인덱스로 올바른 Session 포인터를 읽도록 보장 |
| `lifecycle` | `release` | `acquire` | transition_to 후 render thread가 최신 상태를 볼 수 있도록 보장 |
| `generation` | `release` (fetch_add) | `acquire` | lifecycle과 쌍으로 사용. generation 변경이 lifecycle 변경 전에 보장 |
| `m_render_running` | `release` | `acquire` | 종료 신호가 render thread에 전파되도록 보장 |

---

## 13. 코드 품질 개선 추적

v0.3 → v1.0 에서 해결한 이슈 목록 (code-analyzer + design-validator 피드백):

| ID | 심각도 | 이슈 | 해결 방법 | Section |
|----|--------|------|-----------|---------|
| C1 | Critical | RenderLoop에서 generation bare access (atomic load 누락) | 모든 atomic 접근에 명시적 memory_order 사용. 유일한 RenderLoop 구현(4.5) | 4.5 |
| C2 | Critical | active_idx_ memory ordering 미명시 | store(release)/load(acquire) 명시. 12.3에 규칙 테이블 추가 | 5.2, 12.3 |
| C3 | Critical | close_session erase 후 active_idx_ 범위 초과 race | erase 전 active_idx_ 재조정. closing_index < adj_active 분기 처리 | 5.3 |
| W1 | Warning | std::function 콜백 heap 할당 | 함수 포인터 + void* context 패턴으로 교체 | 3.3, 3.4 |
| W5 | Warning | Session 기본 생성자 미명시 | `Session() = default;` 추가 | 3.1 |
| W6 | Warning | find_next_live_session 반환 타입 불일치 | `optional<SessionId>` 반환으로 통일 (find_next_live_id) | 3.4 |
| W7 | Warning | 콜백 noexcept 강제 없음 | fire_event() 래퍼에서 try-catch + LOG_E | 5.6 |
| W8 | Warning | on_exit lambda this 캡처 dangling 위험 | 소멸자 join 보장 문서화 + Risks 테이블에 명시 | 5.1, 11 |
| W9 | Warning | resize_all에서 active_session() 매 iteration 호출 | 루프 전 캐싱 | 5.4 |
| M1 | Major | SessionTsfAdapter UTF-16→UTF-8 변환 위치 미기술 | HandleOutput 내부에 변환 코드 명시 | 3.3 |
| M2 | Major | active_idx_ memory ordering 정책 미문서화 | 12.3 Memory Ordering 규칙 테이블 | 12.3 |
| M3 | Major | session_at() API 미선언인데 사용 | 제거. active_session()으로 통일 | 4.5 |
| M4 | Major | Plan과 VtCore 소유권 차이 미명시 | 1.4 Plan과의 차이점 테이블 추가 | 1.4 |
| M5 | Major | cleanup_worker 순차/병렬 정책 미기술 | 순차 처리 정책 + worst-case 추정 문서화 | 3.4, 5.5 |
| I3 | Info | get() const 오버로드 누락 | `const Session* get(SessionId) const` 추가 | 3.4 |
| m2 | Minor | env_session_id 미초기화 | create_session에서 초기화 | 5.1 |
| m4 | Minor | find_next_live_session 반환 타입 통일 | optional\<SessionId\> 반환 | 3.4 |
| m5 | Minor | active_index() accessor 미선언 | active_session()으로 대체, 내부에서만 atomic load | 3.4 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-03 | Initial draft — Plan FR-01 기반 상세 설계 | 노수장 |
| 0.2 | 2026-04-03 | CMUX/Alacritty/Ghostty/WT 리서치 반영: 3-state lifecycle + generation, lazy resize, 환경변수 주입, 2단계 close | 노수장 |
| 0.3 | 2026-04-03 | Round 1 리뷰 반영: atomic lifecycle, SessionRef, 비동기 cleanup, move/resize_session API, TC-11~22 | 노수장 |
| **1.0** | **2026-04-03** | **코드 품질 재설계**: C1~C3 critical race 수정 (atomic ordering 통일, close_session 인덱스 재조정), std::function → 함수 포인터 전환, thread ownership 전 필드 명시, cleanup 순차 정책 문서화, fire_event 예외 안전 래퍼, RenderLoop 단일 구현 통일, TC-23~26 추가, Memory Ordering 규칙 테이블, Section 13 품질 추적 | 노수장 |
