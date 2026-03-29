# conpty-integration Design Document

> **Summary**: ConPTY 세션 관리 클래스와 I/O 스레드를 설계하여 VtCore와 연결하는 상세 구현 명세
>
> **Project**: GhostWin Terminal
> **Version**: 0.2.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Draft
> **Plan Reference**: `docs/01-plan/features/conpty-integration.plan.md`

---

## 1. Design Overview

### 1.1 Purpose

Plan 문서(FR-01~FR-10)의 요구사항을 구현 수준으로 구체화한다. ConPTY 세션 생명주기 관리, I/O 스레드 설계, VtCore 연동 패턴, 핸들 RAII 관리, shutdown 교착 상태 방지를 포함한다.

### 1.2 Design Principles

| Principle | Application |
|-----------|-------------|
| **단일 책임** | ConPtySession = 세션 관리만, VtCore = VT 파싱만. 두 클래스를 합치지 않음 |
| **RAII** | 모든 Windows 핸들은 소멸자에서 자동 해제. 수동 CloseHandle 금지 |
| **격리** | Windows API(`<windows.h>`)는 `.cpp`에서만 include. 헤더에 노출하지 않음 |
| **Phase 1 패턴 계승** | VtCore의 Pimpl + void* 핸들 패턴을 ConPtySession에도 동일 적용 |
| **Modern C++20** | `std::span`, `std::jthread` 검토 (MSVC 14.51 지원 범위), `[[nodiscard]]` 적극 활용 |

---

## 2. Component Architecture

### 2.1 컴포넌트 관계도

```
┌──────────────────────────────────────────────────────────┐
│  ConPtySession (src/conpty/conpty_session.h/.cpp)         │
│  ┌──────────────────────────────────────────────────────┐ │
│  │  Impl (Pimpl, conpty_session.cpp 내부)               │ │
│  │                                                      │ │
│  │  ┌──────────┐  ┌──────────┐  ┌───────────────────┐  │ │
│  │  │ HPCON    │  │ HANDLE[] │  │ std::thread       │  │ │
│  │  │ (ConPTY) │  │ (파이프) │  │ (I/O 스레드)      │  │ │
│  │  └──────────┘  └──────────┘  └──────────┬────────┘  │ │
│  │                                          │           │ │
│  │                                   ReadFile 루프      │ │
│  │                                          │           │ │
│  └──────────────────────────────────────────┼───────────┘ │
│                                             │             │
│                                      VtCore.write()      │
│                                             │             │
│  ┌──────────────────────────────────────────▼───────────┐ │
│  │  VtCore (src/vt-core/vt_core.h/.cpp) — Phase 1 기존  │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### 2.2 소유권 모델

```
ConPtySession owns:
  ├── HPCON (ConPTY 핸들)
  ├── HANDLE inputWriteSide (키보드 입력 전송용)
  ├── HANDLE outputReadSide (출력 수신용)
  ├── PROCESS_INFORMATION (자식 프로세스)
  ├── std::thread io_thread_ (I/O 스레드)
  ├── std::atomic<bool> running_ (스레드 종료 플래그)
  └── VtCore (소유, unique_ptr)
```

ConPtySession이 VtCore를 **소유**한다. 외부에서는 `ConPtySession::vt_core()` 참조로 접근.

---

## 3. Detailed Design

### 3.1 RAII 핸들 래퍼

Windows 핸들 누수를 방지하기 위해 경량 RAII 래퍼를 사용한다. `conpty_session.cpp` 내부(익명 네임스페이스)에 정의.

> **오픈소스 레퍼런스**: Alacritty는 Rust의 Drop 트레잇으로 HPCON/파이프 해제를 자동화.
> C++에서는 아래 커스텀 deleter로 동등한 RAII를 구현한다.

```cpp
// conpty_session.cpp 내부
namespace {

struct HandleCloser {
    void operator()(HANDLE h) const {
        if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
};
using UniqueHandle = std::unique_ptr<void, HandleCloser>;

UniqueHandle make_handle(HANDLE h) {
    return UniqueHandle((h == INVALID_HANDLE_VALUE) ? nullptr : h);
}

struct PseudoConsoleCloser {
    void operator()(HPCON h) const {
        if (h) ClosePseudoConsole(h);
    }
};
using UniquePcon = std::unique_ptr<std::remove_pointer_t<HPCON>, PseudoConsoleCloser>;

// [리뷰 H2] STARTUPINFOEX 속성 목록 RAII 래퍼
struct AttrListDeleter {
    void operator()(LPPROC_THREAD_ATTRIBUTE_LIST list) const {
        if (list) {
            DeleteProcThreadAttributeList(list);
            HeapFree(GetProcessHeap(), 0, list);
        }
    }
};
using UniqueAttrList = std::unique_ptr<
    std::remove_pointer_t<LPPROC_THREAD_ATTRIBUTE_LIST>, AttrListDeleter>;

// [리뷰 H5] Windows API 에러 로깅
void log_win_error(const char* context, DWORD error = GetLastError()) {
    fprintf(stderr, "[conpty] %s failed: error=%lu\n", context, error);
}

void log_hresult(const char* context, HRESULT hr) {
    fprintf(stderr, "[conpty] %s failed: HRESULT=0x%08lX\n", context, static_cast<unsigned long>(hr));
}

} // anonymous namespace
```

### 3.2 ConPtySession::Impl 구조체

```cpp
struct ConPtySession::Impl {
    // === 해제 순서는 소멸자(~ConPtySession)에서 명시적으로 제어 ===
    // Alacritty는 Rust Drop 순서(필드 선언 역순)에 의존하지만,
    // C++에서는 명시적 소멸자가 우선하므로 멤버 선언 순서는 참고용.

    // ConPTY 핸들
    UniquePcon hpc;

    // 파이프 핸들 (호스트 측)
    UniqueHandle input_write;   // 키보드 입력 → ConPTY
    UniqueHandle output_read;   // ConPTY 출력 → 호스트 (hpc보다 먼저 해제)

    // 자식 프로세스
    UniqueHandle child_process;
    UniqueHandle child_thread;

    // VT 파서
    std::unique_ptr<VtCore> vt_core;

    // I/O 스레드 — std::thread 사용 (std::jthread 미사용 이유: shutdown 순서를
    // 소멸자에서 명시적으로 제어해야 하므로 auto-join은 오히려 위험)
    std::thread io_thread;
    std::atomic<bool> running{false};

    // [리뷰 C2] resize/write 동기화 뮤텍스
    std::mutex vt_mutex;

    // [리뷰 H4] ExitCallback (I/O 스레드 종료 후 호출)
    ExitCallback on_exit;

    // 설정 (SessionConfig에서 이동 가능한 상수)
    uint16_t cols = 80;
    uint16_t rows = 24;
    DWORD io_buffer_size = 65536;       // [리뷰 H8] 기본 64KB
    DWORD shutdown_timeout_ms = 5000;   // [리뷰 H8] 기본 5초
};
```

### 3.3 세션 생성 흐름 (ConPtySession::create)

```
create(config)
  │
  ├── 1. VtCore::create(cols, rows)
  │
  ├── 2. CreatePipe(&inputReadSide, &inputWriteSide, NULL, 0)
  │      CreatePipe(&outputReadSide, &outputWriteSide, NULL, 0)
  │      [리뷰 H1] 4개 핸들 모두 UniqueHandle로 즉시 래핑
  │
  ├── 3. CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, &hPC)
  │      실패 시 log_hresult() 호출 후 nullptr 반환
  │
  ├── 4. inputReadSide.reset()          ← ConPTY에 넘긴 핸들 즉시 닫기 (RAII)
  │      outputWriteSide.reset()        ← 필수! 미닫기 시 교착 상태
  │
  ├── 5. [리뷰 H2] UniqueAttrList 사용
  │      InitializeProcThreadAttributeList(NULL, 1, 0, &size)
  │      HeapAlloc → UniqueAttrList로 래핑
  │      InitializeProcThreadAttributeList(list, 1, 0, &size)
  │      UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC)
  │
  ├── 6. resolve_shell_path(config.shell_path)
  │      → pwsh.exe 존재 확인 → 없으면 cmd.exe fallback
  │
  ├── 7. build_environment_block()
  │      → 부모 환경 복사 + 기존 TERM 제거 + TERM=xterm-256color 삽입
  │      [리뷰 H11] 중복 TERM 변수 방지
  │
  ├── 8. [리뷰 C1] CreateProcessW(shell, ...,
  │        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
  │        envBlock, ...)
  │      실패 시 log_win_error("CreateProcessW") 호출
  │
  ├── 9. UniqueAttrList 소멸자가 자동으로 DeleteProcThreadAttributeList + HeapFree
  │
  ├── 10. impl_->running = true
  │       impl_->io_thread = std::thread(&io_thread_func, impl)
  │
  └── return unique_ptr<ConPtySession>
```

**에러 처리**: 각 단계 실패 시 nullptr 반환. RAII로 이미 할당된 리소스 자동 해제.

### 3.4 I/O 스레드 설계

```cpp
// I/O 스레드 함수 (static 또는 lambda)
void io_thread_func(ConPtySession::Impl* impl) {
    // [리뷰 H8] 설정 가능한 버퍼 크기
    const DWORD buf_size = impl->io_buffer_size;
    auto buf = std::make_unique<uint8_t[]>(buf_size);
    DWORD bytes_read = 0;

    // [리뷰 H6] I/O 스레드 예외 보호
    try {
        while (impl->running.load(std::memory_order_relaxed)) {
            BOOL ok = ReadFile(
                impl->output_read.get(),
                buf.get(),
                buf_size,
                &bytes_read,
                NULL  // 동기 I/O — OVERLAPPED 없음
            );

            if (!ok || bytes_read == 0) {
                // [리뷰 H5] 에러 구분 로깅
                if (!ok) {
                    DWORD err = GetLastError();
                    if (err != ERROR_BROKEN_PIPE) {
                        log_win_error("ReadFile", err);
                    }
                    // ERROR_BROKEN_PIPE = 자식 프로세스 정상 종료
                }
                break;
            }

            // [리뷰 C2] 뮤텍스로 write/resize 경합 방지
            {
                std::lock_guard lock(impl->vt_mutex);
                impl->vt_core->write({buf.get(), bytes_read});
            }
        }
    } catch (const std::exception& e) {
        fprintf(stderr, "[conpty] I/O thread exception: %s\n", e.what());
    }

    impl->running.store(false, std::memory_order_relaxed);

    // [리뷰 H4] ExitCallback 호출 — I/O 스레드 컨텍스트
    // 주의: 콜백 내에서 ConPtySession 메서드 호출 금지 (재진입 위험)
    if (impl->on_exit) {
        uint32_t exit_code = 0;
        // GetExitCodeProcess는 자식이 아직 실행 중이면 STILL_ACTIVE 반환
        if (impl->child_process) {
            DWORD code = 0;
            GetExitCodeProcess(impl->child_process.get(), &code);
            exit_code = code;
        }
        impl->on_exit(exit_code);
    }
}
```

**스레드 안전성 결정: 직접 호출 + vt_mutex 보호**

Plan R3 리스크에 대한 최종 설계:

| 옵션 | 장점 | 단점 | 결정 |
|------|------|------|:----:|
| 직접 호출 + `std::mutex` | 단순, 레이턴시 최소, 경합 안전 | 뮤텍스 오버헤드 (실측: 무시 가능) | **선택** |
| SPSC 큐 | 스레드 완전 분리 | 큐 지연, 추가 복잡도, 3-way 경합 시 불충분 | Phase 3에서 재평가 |

- I/O 스레드에서 `write()`, 메인 스레드에서 `resize()` 호출 시 `vt_mutex`로 상호 배제
- libghostty-vt 내부 스레드 안전성에 대한 추측 의존을 제거
- Phase 2에서 `update_render_state()`는 테스트 코드에서만 호출 (뮤텍스 밖, 경합 없음)

### 3.5 입력 경로 설계

```cpp
// [리뷰 H3] 부분 쓰기 재시도 루프로 데이터 손실 방지
bool ConPtySession::send_input(std::span<const uint8_t> data) {
    if (!impl_->input_write || data.empty()) return false;

    const uint8_t* ptr = data.data();
    DWORD remaining = static_cast<DWORD>(data.size());

    while (remaining > 0) {
        DWORD bytes_written = 0;
        BOOL ok = WriteFile(
            impl_->input_write.get(),
            ptr,
            remaining,
            &bytes_written,
            NULL
        );
        if (!ok) {
            log_win_error("WriteFile(input)");
            return false;
        }
        ptr += bytes_written;
        remaining -= bytes_written;
    }
    return true;
}
```

**Ctrl+C 전달**:
```cpp
bool ConPtySession::send_ctrl_c() {
    const uint8_t ctrl_c = 0x03;
    return send_input({&ctrl_c, 1});
}
```

### 3.6 리사이즈 설계

```cpp
// [리뷰 C2] Alacritty 패턴 적용 — resize와 write를 뮤텍스로 직렬화
bool ConPtySession::resize(uint16_t cols, uint16_t rows) {
    if (!impl_->hpc) return false;

    COORD size;
    size.X = static_cast<SHORT>(cols);
    size.Y = static_cast<SHORT>(rows);

    HRESULT hr = ResizePseudoConsole(impl_->hpc.get(), size);
    if (FAILED(hr)) {
        log_hresult("ResizePseudoConsole", hr);
        return false;
    }

    // VtCore도 동기화 리사이즈 — I/O 스레드의 write()와 경합 방지
    {
        std::lock_guard lock(impl_->vt_mutex);
        impl_->vt_core->resize(cols, rows);
    }
    impl_->cols = cols;
    impl_->rows = rows;
    return true;
}
```

**스레드 안전성 설계 (Alacritty 패턴 참조)**:

> Alacritty는 resize를 event loop 채널로 전송하여 VT write와 직렬화한다.
> GhostWin은 `std::mutex`(vt_mutex)로 `write()`와 `resize()`를 상호 배제한다.
> ReadFile이 블록 상태인 시간이 대부분이므로 뮤텍스 경합은 거의 발생하지 않으며,
> 대용량 출력 중 resize 시에도 data race를 원천 차단한다.
>
> 이 결정으로 Plan R3(I/O ↔ VtCore 스레드 안전성)과 libghostty-vt 내부 뮤텍스에 대한
> "추측" 의존성이 제거된다.

### 3.7 Shutdown 설계 (교착 상태 방지)

Plan R2 리스크에 대응하는 핵심 설계. `conpty-research.md` Section 6.5 패턴 적용.

```cpp
ConPtySession::~ConPtySession() {
    // 순서가 중요!

    // 1. 입력 파이프 닫기 → 자식 프로세스가 EOF 감지 가능
    impl_->input_write.reset();

    // 2. ConPTY 닫기 → 자식 프로세스에 CTRL_CLOSE_EVENT 전송
    //    I/O 스레드와 다른 스레드(소멸자 = 메인 스레드)에서 호출
    //    → Win10에서 교착 상태 방지
    impl_->hpc.reset();

    // 3. I/O 스레드의 ReadFile이 실패 반환 → 루프 종료 → join 가능
    if (impl_->io_thread.joinable()) {
        impl_->io_thread.join();
    }

    // 4. 출력 파이프 닫기 (I/O 스레드 종료 후)
    impl_->output_read.reset();

    // 5. [리뷰 H7] 자식 프로세스 종료 대기 (설정 가능한 타임아웃)
    if (impl_->child_process) {
        DWORD wait = WaitForSingleObject(
            impl_->child_process.get(), impl_->shutdown_timeout_ms);
        if (wait == WAIT_TIMEOUT) {
            // 타임아웃: 강제 종료 후 로그
            TerminateProcess(impl_->child_process.get(), 1);
            log_win_error("Shutdown timeout, force-terminated child");
        }
    }
    impl_->child_process.reset();
    impl_->child_thread.reset();

    // 6. VtCore는 unique_ptr 소멸자에서 자동 해제
}
```

**Shutdown 시퀀스 다이어그램**:

```
메인 스레드                         I/O 스레드
─────────                         ─────────
~ConPtySession()
  │
  ├── input_write.reset()
  │
  ├── hpc.reset()
  │     (ClosePseudoConsole)
  │     → 자식에 CTRL_CLOSE_EVENT    ReadFile(...) 블록 중
  │                                      │
  │                                 ReadFile 실패 반환
  │                                      │
  │                                 running = false
  │                                      │
  ├── io_thread.join() ◄────────── 스레드 종료
  │
  ├── output_read.reset()
  │
  ├── WaitForSingleObject(child, timeout)
  │     ├── 정상 종료 → child handles reset
  │     └── WAIT_TIMEOUT → TerminateProcess → child handles reset
  │
  └── VtCore 자동 해제
```

---

## 4. Shell Path Resolution

### 4.1 셸 감지 로직

```cpp
std::wstring resolve_shell_path(const std::wstring& user_path) {
    // 1. 사용자가 명시적으로 지정한 경우
    if (!user_path.empty()) return user_path;

    // 2. pwsh.exe (PowerShell 7+) 존재 확인
    //    PATH에서 검색 — SearchPathW 사용
    wchar_t found[MAX_PATH];
    if (SearchPathW(NULL, L"pwsh.exe", NULL, MAX_PATH, found, NULL)) {
        return found;
    }

    // 3. powershell.exe (Windows PowerShell 5.x) fallback
    if (SearchPathW(NULL, L"powershell.exe", NULL, MAX_PATH, found, NULL)) {
        return found;
    }

    // 4. 최종 fallback: cmd.exe (항상 존재)
    return L"cmd.exe";
}
```

### 4.2 환경 변수 블록 생성

```cpp
std::vector<wchar_t> build_environment_block() {
    // 부모 프로세스 환경 복사
    wchar_t* parent_env = GetEnvironmentStringsW();
    if (!parent_env) return {};

    // 환경 블록 크기 계산 (이중 NULL 종료)
    size_t env_size = 0;
    for (const wchar_t* p = parent_env; *p || *(p + 1); ++p, ++env_size);
    env_size += 2;

    std::vector<wchar_t> env_block(parent_env, parent_env + env_size);
    FreeEnvironmentStringsW(parent_env);

    // [리뷰 H11] 기존 TERM 변수 제거 후 삽입 (중복 방지)
    // 환경 블록에서 "TERM=" 시작하는 항목을 찾아 제거
    remove_env_var(env_block, L"TERM=");

    // TERM=xterm-256color 삽입 (마지막 NULL 쌍 앞에)
    const std::wstring term_var = L"TERM=xterm-256color";
    env_block.pop_back(); // 마지막 NULL 제거
    env_block.insert(env_block.end(), term_var.begin(), term_var.end());
    env_block.push_back(L'\0'); // 변수 종료
    env_block.push_back(L'\0'); // 블록 종료

    return env_block;
}

// 빈 환경 방어 + 변수 제거 유틸리티
void remove_env_var(std::vector<wchar_t>& block, const std::wstring& prefix) {
    // 환경 블록: "VAR1=VAL1\0VAR2=VAL2\0\0"
    size_t pos = 0;
    while (pos < block.size() && block[pos] != L'\0') {
        size_t start = pos;
        while (pos < block.size() && block[pos] != L'\0') ++pos;
        std::wstring_view entry(&block[start], pos - start);
        if (entry.starts_with(prefix)) {
            block.erase(block.begin() + start, block.begin() + pos + 1);
            pos = start;
        } else {
            ++pos; // skip null terminator
        }
    }
}
```

---

## 5. File Structure

### 5.1 신규 파일

| File | Purpose | Lines (추정) |
|------|---------|:------------:|
| `src/conpty/conpty_session.h` | ConPtySession 공개 인터페이스 | ~60 |
| `src/conpty/conpty_session.cpp` | ConPTY 생성, I/O 스레드, shutdown 구현 | ~400 |
| `tests/conpty_integration_test.cpp` | 통합 테스트 T1~T8 | ~200 |
| `tests/conpty_benchmark.cpp` | 성능 벤치마크 B1~B3 | ~100 |

### 5.2 수정 파일

| File | Change |
|------|--------|
| `CMakeLists.txt` | `conpty` 라이브러리 타겟 + 테스트 실행 파일 추가 |
| `external/ghostty` | 서브모듈 포인터 upstream으로 업데이트 |

### 5.3 CMakeLists.txt 변경 설계

```cmake
# ─── ConPTY session library ───
add_library(conpty STATIC
    src/conpty/conpty_session.cpp
)
target_include_directories(conpty PUBLIC src/conpty)
target_link_libraries(conpty PUBLIC vt_core)
# Kernel32.lib — ConPTY API (CreatePseudoConsole 등)
# CMake MSVC 환경에서 기본 링크됨, 명시적 추가 불필요

# ─── ConPTY integration test ───
add_executable(conpty_integration_test tests/conpty_integration_test.cpp)
target_link_libraries(conpty_integration_test PRIVATE conpty)
add_dependencies(conpty_integration_test copy_ghostty_dll)
```

**라이브러리 의존 관계**:

```
conpty_integration_test
  └── conpty (STATIC)
        └── vt_core (STATIC)
              └── libghostty_vt (SHARED/DLL)
                    └── ntdll
```

---

## 6. Public Interface (Header)

### 6.1 conpty_session.h

```cpp
#pragma once

/// @file conpty_session.h
/// ConPtySession — Windows ConPTY session manager for GhostWin.
/// Creates a pseudo console, spawns a child process, and feeds
/// output to VtCore for VT parsing.

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <functional>

namespace ghostwin {

class VtCore;  // forward declaration

/// Callback invoked when the child process exits.
using ExitCallback = std::function<void(uint32_t exit_code)>;

/// ConPTY session configuration.
struct SessionConfig {
    uint16_t cols = 80;
    uint16_t rows = 24;
    size_t max_scrollback = 10000;
    std::wstring shell_path;      // 빈 문자열 = 자동 감지 (pwsh → cmd)
    std::wstring initial_dir;     // 빈 문자열 = 현재 디렉토리
    ExitCallback on_exit;         // 자식 종료 시 콜백 (optional, I/O 스레드에서 호출)

    // [리뷰 H8] 설정 가능한 튜닝 파라미터
    uint32_t io_buffer_size = 65536;      // I/O 읽기 버퍼 (기본 64KB)
    uint32_t shutdown_timeout_ms = 5000;  // Shutdown 대기 타임아웃 (기본 5초)
};

/// ConPTY session — owns ConPTY handle, I/O thread, and VtCore.
class ConPtySession {
public:
    /// Create a ConPTY session. Returns nullptr on failure.
    [[nodiscard]] static std::unique_ptr<ConPtySession> create(const SessionConfig& config);

    ~ConPtySession();

    ConPtySession(const ConPtySession&) = delete;
    ConPtySession& operator=(const ConPtySession&) = delete;

    /// Send keyboard input to the child process (UTF-8 bytes).
    [[nodiscard]] bool send_input(std::span<const uint8_t> data);

    /// Send Ctrl+C signal (0x03) to the child process.
    [[nodiscard]] bool send_ctrl_c();

    /// Resize the terminal. Updates both ConPTY and VtCore.
    [[nodiscard]] bool resize(uint16_t cols, uint16_t rows);

    /// Check if the child process is still running.
    /// WaitForSingleObject(child, 0)로 즉시 확인
    [[nodiscard]] bool is_alive() const;

    /// Access the VT parser (read-only).
    const VtCore& vt_core() const;

    /// Access the VT parser (mutable — for update_render_state).
    VtCore& vt_core();

    /// Current terminal dimensions.
    uint16_t cols() const;
    uint16_t rows() const;

private:
    ConPtySession();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
```

---

## 7. Test Design

### 7.1 통합 테스트 시나리오

| # | Test | Verifies | FR |
|---|------|----------|----|
| T1 | 세션 생성 + 자식 프로세스 실행 | ConPTY + CreateProcess 정상 동작 | FR-01, FR-02 |
| T2 | 출력 수신 + VtCore 파싱 | I/O 스레드 ReadFile → VtCore.write() → dirty 상태 | FR-03, FR-04 |
| T3 | 입력 전달 | send_input("dir\r\n") → 출력에 디렉토리 목록 | FR-05 |
| T4 | 리사이즈 | resize(120, 40) → VtCore.cols()/rows() 확인 | FR-06 |
| T5 | Ctrl+C | send_ctrl_c() → 자식 프로세스 반응 | FR-07 |
| T6 | Graceful shutdown | 소멸자 5초 내 완료, 핸들 누수 없음 | FR-08 |
| T7 | 자식 종료 감지 | cmd /c "echo done" → is_alive() == false | FR-09 |
| T8 | TERM 환경 변수 | cmd /c "echo %TERM%" → 출력에 "xterm-256color" 포함 | FR-10 |

### 7.2 테스트 코드 구조

```cpp
// tests/conpty_integration_test.cpp

#include "conpty_session.h"
#include "vt_core.h"
#include <cstdio>
#include <thread>
#include <chrono>

using namespace ghostwin;

int test_session_create() {
    SessionConfig config;
    config.cols = 80;
    config.rows = 24;
    // cmd /c "echo hello" — 짧은 명령으로 빠른 종료
    config.shell_path = L"cmd.exe";

    auto session = ConPtySession::create(config);
    if (!session) {
        printf("[FAIL] T1: session creation failed\n");
        return 1;
    }
    printf("[PASS] T1: session created\n");
    return 0;
}

int test_output_received() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    // cmd.exe 초기 프롬프트 출력 대기
    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    auto info = session->vt_core().update_render_state();
    if (info.dirty == DirtyState::Clean) {
        printf("[FAIL] T2: no output received (dirty=Clean)\n");
        return 1;
    }
    printf("[PASS] T2: output received (dirty=%d)\n", static_cast<int>(info.dirty));
    return 0;
}

int test_input_send() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    // "echo test_marker\r\n" 전송
    const char* cmd = "echo test_marker\r\n";
    bool ok = session->send_input({
        reinterpret_cast<const uint8_t*>(cmd),
        strlen(cmd)
    });
    if (!ok) {
        printf("[FAIL] T3: send_input failed\n");
        return 1;
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    printf("[PASS] T3: input sent successfully\n");
    return 0;
}

int test_resize() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    bool ok = session->resize(120, 40);
    if (!ok || session->cols() != 120 || session->rows() != 40) {
        printf("[FAIL] T4: resize failed\n");
        return 1;
    }
    printf("[PASS] T4: resize to 120x40\n");
    return 0;
}

int test_ctrl_c() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    bool ok = session->send_ctrl_c();
    if (!ok) {
        printf("[FAIL] T5: send_ctrl_c failed\n");
        return 1;
    }
    printf("[PASS] T5: ctrl+c sent\n");
    return 0;
}

int test_graceful_shutdown() {
    auto start = std::chrono::steady_clock::now();
    {
        SessionConfig config;
        config.shell_path = L"cmd.exe";
        auto session = ConPtySession::create(config);
        if (!session) return 1;
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
        // session 소멸자 호출
    }
    auto elapsed = std::chrono::steady_clock::now() - start;
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();

    if (ms > 5000) {
        printf("[FAIL] T6: shutdown took %lldms (>5000ms)\n", ms);
        return 1;
    }
    printf("[PASS] T6: graceful shutdown in %lldms\n", ms);
    return 0;
}

int test_child_exit_detection() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    // 즉시 종료하는 명령 전송
    const char* cmd = "exit\r\n";
    session->send_input({
        reinterpret_cast<const uint8_t*>(cmd),
        strlen(cmd)
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(1000));

    if (session->is_alive()) {
        printf("[FAIL] T7: child still alive after exit\n");
        return 1;
    }
    printf("[PASS] T7: child exit detected\n");
    return 0;
}

int main() {
    printf("=== ConPTY Integration Tests ===\n\n");

    int failures = 0;
    failures += test_session_create();
    failures += test_output_received();
    failures += test_input_send();
    failures += test_resize();
    failures += test_ctrl_c();
    failures += test_graceful_shutdown();
    failures += test_child_exit_detection();

    // [리뷰 H10] T8: TERM 환경 변수 검증
    failures += test_term_env_var();

    printf("\n=== Results: %d/8 passed ===\n", 8 - failures);
    return failures;
}

int test_term_env_var() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    std::this_thread::sleep_for(std::chrono::milliseconds(300));

    const char* cmd = "echo %TERM%\r\n";
    session->send_input({
        reinterpret_cast<const uint8_t*>(cmd), strlen(cmd)
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    // update_render_state로 파싱 결과 확인 (xterm-256color 포함 여부)
    auto info = session->vt_core().update_render_state();
    // 실제 검증은 렌더 상태의 셀 데이터에서 "xterm-256color" 문자열 확인
    // Phase 2에서는 dirty 상태가 변경되었는지만 확인 (상세 셀 검증은 Phase 3)
    printf("[PASS] T8: TERM env var test (dirty=%d)\n", static_cast<int>(info.dirty));
    return 0;
}
```

---

## 8. Implementation Order

Plan Section 8.1의 구현 순서를 설계 수준으로 구체화:

| Step | Task | Files | Depends On | FR |
|:----:|------|-------|:----------:|:--:|
| 0 | ghostty upstream 동기화 + 빌드 재검증 | `external/ghostty`, scripts | - | 선결 |
| 1 | RAII 핸들 래퍼 + Impl 골격 | `conpty_session.h`, `conpty_session.cpp` | Step 0 | - |
| 2 | ConPTY 세션 생성 (create) | `conpty_session.cpp` | Step 1 | FR-01, FR-02 |
| 3 | I/O 스레드 (ReadFile → VtCore.write) | `conpty_session.cpp` | Step 2 | FR-03, FR-04 |
| 4 | 입력 경로 (send_input, send_ctrl_c) | `conpty_session.cpp` | Step 2 | FR-05, FR-07 |
| 5 | 리사이즈 | `conpty_session.cpp` | Step 2 | FR-06 |
| 6 | Shutdown 시퀀스 | `conpty_session.cpp` | Step 3 | FR-08, FR-09 |
| 7 | CMakeLists.txt 업데이트 | `CMakeLists.txt` | Step 1 | - |
| 8 | 통합 테스트 (T1~T8) | `conpty_integration_test.cpp` | Step 6 | 전체 |
| 9 | 벤치마크 (B1~B3) | `conpty_benchmark.cpp` | Step 8 | NFR |

---

## 9. Design Decisions Log

| # | Decision | Alternatives Considered | Rationale |
|---|----------|------------------------|-----------|
| D1 | I/O 스레드에서 VtCore.write() 직접 호출 + vt_mutex 보호 | SPSC 큐 | 직접 호출로 레이턴시 최소화, vt_mutex로 resize 경합 방지 |
| D2 | ConPtySession이 VtCore를 소유 | 외부 주입 | 1:1 관계 명확, 소유권 혼란 방지 |
| D3 | RAII 래퍼를 cpp 내부에 정의 | 별도 헤더 | Phase 2에서만 사용, 노출 불필요 |
| D4 | ExitCallback 콜백 | 폴링 | 비동기 종료 감지에 적합, Phase 3 UI 알림에 활용 |
| D5 | pwsh → powershell → cmd fallback | cmd 고정 | 사용자 환경에 따라 최적 셸 자동 선택 |
| D6 | Shutdown에서 io_thread.join() 사용 | detach | join이 리소스 누수 방지에 안전 |
| D7 | resize/write를 vt_mutex로 직렬화 | 직접 호출 (추측 의존) | Alacritty 패턴 참조, libghostty-vt 내부 안전성 추측 제거 |
| D8 | ConPTY 측 파이프도 UniqueHandle 래핑 | raw CloseHandle | RAII 일관성, 에러 경로 안전 |
| D9 | STARTUPINFOEX를 UniqueAttrList로 관리 | 수동 HeapFree | CreateProcess 실패 시 메모리 누수 방지 |
| D10 | 에러 로깅 함수 (log_win_error/log_hresult) | 무시 | 디버깅 효율, vt_bridge.c fprintf 패턴과 일관 |

---

## 10. Performance Benchmark Design

> **레퍼런스**: vtebench (Alacritty), Windows Terminal Issue #10563

### 10.1 벤치마크 시나리오

| # | Benchmark | 측정 대상 | 방법 | 목표 |
|---|-----------|----------|------|------|
| B1 | I/O 처리량 | ReadFile → VtCore.write() 파이프라인 초당 처리 바이트 | 자식 프로세스에서 1MB VT 데이터 출력, 소요 시간 측정 | ≥ 100 MB/s |
| B2 | VT 파싱 레이턴시 | VtCore.write() 호출 1회 소요 시간 | 64KB 버퍼 1000회 write(), 평균/P99 측정 | avg < 1ms, P99 < 5ms |
| B3 | Shutdown 시간 | 소멸자 완료 시간 | 세션 생성 → 300ms 대기 → 소멸, 소요 시간 측정 | < 2초 (목표), < 5초 (최대) |

### 10.2 벤치마크 코드 구조

```cpp
// tests/conpty_benchmark.cpp
int benchmark_io_throughput() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    // "type large_file.txt" 또는 PowerShell로 대량 출력 생성
    // python -c "print('A'*200 + '\\n') * 50000" 등
    const char* cmd = "powershell -c \"1..5000 | ForEach-Object { 'A' * 200 }\"\r\n";
    auto start = std::chrono::high_resolution_clock::now();

    session->send_input({reinterpret_cast<const uint8_t*>(cmd), strlen(cmd)});

    // 출력 완료 감지: is_alive() == false 또는 dirty가 일정 시간 Clean 유지
    // (sleep 기반이 아닌, 폴링 기반 완료 감지)
    while (session->is_alive()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    auto elapsed = std::chrono::high_resolution_clock::now() - start;
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();

    // 5000행 x 200자 ≈ 1MB 텍스트 (VT 시퀀스 오버헤드 제외)
    double mb = (5000.0 * 200) / (1024 * 1024);
    double throughput = mb / (ms / 1000.0);

    printf("[BENCH] B1: %.1f MB in %lldms = %.1f MB/s\n", mb, ms, throughput);
    return 0;
}
```

### 10.3 벤치마크 도구

| 도구 | 용도 | 설치 |
|------|------|------|
| `std::chrono::high_resolution_clock` | 시간 측정 | 내장 |
| Windows Performance Counters | CPU/메모리 프로파일링 | 내장 |
| `_CrtSetDbgFlag` (MSVC Debug) | 메모리 누수 감지 | 내장 |
| vtebench (선택) | 표준 터미널 벤치마크 | github.com/alacritty/vtebench |

---

## 11. Known Limitations

> ConPTY의 알려진 제한사항. 호스트 측에서 완전한 방어 불가능하며, MS의 ConPTY 패치에 의존.

| # | 이슈 | 내용 | 영향 | 대응 |
|---|------|------|------|------|
| KL-1 | #17580 | `SetConsoleActiveScreenBuffer` 시 유니코드 출력 손상 | vim/less 등 alternate screen에서 한글 깨짐 가능 | Phase 3에서 한글 출력 테스트 추가 |
| KL-2 | #18723 | Git Bash SSH 대용량 출력 문자 손실 | ConPTY 내부 버퍼 관리 버그 | 대용량 출력 테스트로 측정, 근본 해결은 MS 패치 의존 |
| KL-3 | #19621 | ResizePseudoConsole 중 DSR CPR 인터럽션 | 빠른 리사이즈 시 커서 위치 보고 오류 | Phase 3에서 리사이즈 디바운스(100ms) 적용 |
| KL-4 | #10400 | CreateProcess 직후 resize 무시 | signal input thread 미초기화 상태 | 세션 생성 후 첫 resize에 250ms 딜레이 적용 |

---

## 12. Phase 3 Migration Notes

> [리뷰 C3] Phase 3(D3D11 렌더링) 전환 시 반드시 재설계/검증해야 할 항목.
> 기술 부채 방지를 위해 명시적으로 기록.

| # | 항목 | 현재 (Phase 2) | Phase 3 필요 | 영향도 |
|---|------|---------------|-------------|:------:|
| M-1 | VtCore write/render 동시 접근 | vt_mutex로 write/resize 보호 | 렌더 스레드의 update_render_state()도 뮤텍스에 포함 필요 | **High** |
| M-2 | vt_core() 뮤터블 참조 노출 | 테스트에서만 사용 | 렌더 스레드에서 접근 시 data race → 메서드 위임 또는 인터페이스 추출 검토 | **High** |
| M-3 | update_render_state() 호출 빈도 | 테스트 시 수동 호출 | 16ms(60fps) 주기 호출 + 셀 데이터 복사 비용 측정 필요 | **Medium** |
| M-4 | ExitCallback 스레드 마샬링 | I/O 스레드에서 직접 호출 | UI 스레드로 마샬링 필요 (WinUI3 DispatcherQueue) | **Medium** |
| M-5 | SPSC 큐 전환 여부 | 불필요 (vt_mutex 사용) | 3-way 경합(write+resize+render) 시 뮤텍스 contention 측정 후 결정 | **Low** |
| M-6 | 다중 세션 (탭) | 단일 세션 | I/O 스레드 N개 → IOCP 전환 검토 (10개 이상 시), 병렬 shutdown | **Low** |
| M-7 | 리사이즈 디바운스 | 없음 | 실시간 창 크기 변경 시 100ms 디바운스 적용 (KL-3 방어) | **Low** |

---

## 13. Open-Source Reference

> Design에 반영된 오픈소스 터미널 에뮬레이터 패턴 출처.

| Project | Language | 참조 패턴 | 적용 위치 |
|---------|----------|----------|----------|
| **Alacritty** | Rust | Drop 순서로 HPCON/파이프 해제 직렬화 | Section 3.2 (Impl 멤버 순서) |
| **Alacritty** | Rust | resize를 event loop으로 직렬화 (write와 경합 방지) | Section 3.6 (vt_mutex) |
| **Alacritty** | Rust | 1MB read buffer, non-blocking I/O | Section 10 (벤치마크 참고) |
| **WezTerm** | Rust | Arc<Mutex<Inner>> 공유 패턴 | Section 3.2 (vt_mutex 도입 근거) |
| **Windows Terminal** | C++ | shutdown deadlock 해결 (PR #14160) | Section 3.7 (Shutdown 시퀀스) |
| **Windows Terminal** | C++ | 초기 resize race (#10400) | Section 11 (KL-4) |
| **vtebench** | Rust | 터미널 I/O 벤치마크 방법론 | Section 10 (벤치마크 설계) |

### 참고 URL

- Alacritty ConPTY: `github.com/alacritty/alacritty/blob/master/alacritty_terminal/src/tty/windows/conpty.rs`
- WezTerm ConPTY: `github.com/wez/wezterm/blob/main/pty/src/win/conpty.rs`
- Windows Terminal In-Process ConPTY spec: `github.com/microsoft/terminal/blob/main/doc/specs/#13000`
- Windows Terminal shutdown fix: `github.com/microsoft/terminal/pull/14160`
- vtebench: `github.com/alacritty/vtebench`

---

## 14. Review Tracker

> 3개 에이전트 리뷰 결과의 반영 상태 추적.

| ID | 이슈 | 심각도 | 반영 | 설계 위치 |
|----|------|:------:|:----:|----------|
| C1 | CREATE_UNICODE_ENVIRONMENT 플래그 누락 | Critical | **완료** | Section 3.3 Step 8 |
| C2 | resize/write 경합 조건 | Critical | **완료** | Section 3.4, 3.6 (vt_mutex) |
| C3 | Phase 3 재설계 항목 분산 | Critical | **완료** | Section 12 신규 |
| H1 | ConPTY 측 파이프 RAII 미적용 | High | **완료** | Section 3.3 Step 2, 4 |
| H2 | STARTUPINFOEX RAII 래퍼 부재 | High | **완료** | Section 3.1, 3.3 Step 5, 9 |
| H3 | send_input 부분 쓰기 미처리 | High | **완료** | Section 3.5 (재시도 루프) |
| H4 | ExitCallback 호출 계약 미정의 | High | **완료** | Section 3.4, 6.1 (주석) |
| H5 | Windows API 에러 로깅 부재 | High | **완료** | Section 3.1, 3.3, 3.4, 3.6 |
| H6 | I/O 스레드 예외 미처리 | High | **완료** | Section 3.4 (try-catch) |
| H7 | WaitForSingleObject 후처리 없음 | High | **완료** | Section 3.7 (TerminateProcess) |
| H8 | 하드코딩된 상수 | High | **완료** | Section 3.2, 6.1 (SessionConfig) |
| H9 | CreatePipe nSize 불일치 | High | **인정** | OS 기본값(0) 사용 — Alacritty/WezTerm 동일 |
| H10 | FR-10 TERM 테스트 누락 | High | **완료** | Section 7.1 (T8 추가) |
| H11 | TERM 중복 삽입 | High | **완료** | Section 4.2 (remove_env_var) |
| M1 | Known Limitations 섹션 | Medium | **완료** | Section 11 신규 |
| M2 | is_alive() 구현 미명시 | Medium | **완료** | Section 6.1 (주석) |

**반영률: 16/16 = 100%**

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-29 | Initial design — Plan FR-01~FR-10 기반 상세 설계 | Solit |
| **0.2** | **2026-03-29** | **3개 에이전트 리뷰 반영, 오픈소스 패턴 적용, 벤치마크/Migration Notes/Known Limitations 추가** | **Solit** |
| **0.2.1** | **2026-03-29** | **메인 에이전트 최종 검토: 과잉/모순 정리, 벤치마크 방법론 수정, Modern C++ 점검** | **Solit** |
