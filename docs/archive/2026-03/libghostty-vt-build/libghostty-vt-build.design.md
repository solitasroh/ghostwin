# libghostty-vt-build Design Document

> **Summary**: libghostty-vt Windows DLL 빌드 + C 브릿지 + VtCore 래퍼 + 테스트
>
> **Project**: GhostWin Terminal
> **Version**: 0.3.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Implementation Complete — 7/7 Tests Passed
> **Planning Doc**: [libghostty-vt-build.plan.md](../../01-plan/features/libghostty-vt-build.plan.md)
> **ADRs**: [001](../../adr/001-simd-false-gnu-target.md), [002](../../adr/002-c-bridge-pattern.md), [003](../../adr/003-dll-dynamic-crt.md)

---

## 1. Overview

### 1.1 Design Goals

1. Zig 0.15.2 + `-Dsimd=false -Dtarget=x86_64-windows-gnu`로 ghostty-vt DLL + static lib 빌드
2. C 브릿지 레이어로 ghostty C 헤더의 MSVC C++ 호환 문제 해결 (ADR-002)
3. VtCore C++ 래퍼로 C++ 프로젝트에 깨끗한 인터페이스 제공
4. 7개 테스트로 핵심 API 검증

### 1.2 Design Principles

- **C 브릿지 격리**: ghostty 헤더는 `vt_bridge.c`에서만 include (MSVC typedef 충돌 방지)
- **CRT 독립**: `-Dsimd=false`로 CRT 의존 제거 — `VirtualAlloc` 직접 사용
- **환경 독립**: `x86_64-windows-gnu` 타겟으로 `--libc` 파일 불필요
- **DLL 격리**: GNU 타겟 빌드 산출물을 DLL로 격리하여 MSVC 링커 호환성 확보 (ADR-003)

---

## 2. Architecture

### 2.1 Component Diagram

```
┌─────────────────────────────────────────────────────────┐
│                    Phase 1 구현                           │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  ┌──────────────┐     ┌──────────────┐                  │
│  │ vt_core_test │     │   VtCore     │  ← C++ 인터페이스│
│  │ (C++20)      │────▶│ vt_core.h/   │                  │
│  │  7개 테스트   │     │ vt_core.cpp  │                  │
│  └──────────────┘     └──────┬───────┘                  │
│                              │ #include "vt_bridge.h"    │
│                              ▼                           │
│                   ┌──────────────────┐                   │
│                   │   vt_bridge      │  ← C/C++ 호환     │
│                   │  vt_bridge.h     │    void* 핸들      │
│                   │  vt_bridge.c     │  ← 순수 C          │
│                   └──────┬───────────┘                   │
│                          │ #include <ghostty/vt/*.h>     │
│                          ▼                               │
│               ┌──────────────────────┐                   │
│               │  ghostty-vt.dll      │  ← GNU/MSVC 격리  │
│               │  + ghostty-vt.lib    │    (import lib)    │
│               │  Zig 0.15.2          │                   │
│               │  gnu + simd=false    │                   │
│               └──────────────────────┘                   │
│                                                          │
├─────────────────────────────────────────────────────────┤
│  Build: CMake 4.0 + Ninja + MSVC 14.51                  │
│  Link: ghostty-vt.lib (import) + ntdll.lib               │
└─────────────────────────────────────────────────────────┘
```

### 2.2 Data Flow

```
테스트 입력 "\x1b[31mHello\x1b[0m"
        │
        ▼
VtCore::write(data)              ← C++ (vt_core.cpp)
        │
        ▼
vt_bridge_write(term, data, len) ← C (vt_bridge.c)
        │
        ▼ (C ABI, void* → GhosttyTerminal cast)
ghostty_terminal_vt_write()      ← ghostty DLL
        │
        ▼
VtCore::update_render_state()
        │
        ▼
vt_bridge_update_render_state()
        │
        ▼
VtRenderInfo { dirty, cols, rows, cursor_x/y, cursor_visible, cursor_style }
```

### 2.3 Dependencies

| Component | Depends On | Purpose |
|-----------|-----------|---------|
| vt_core_test.cpp | VtCore (vt_core.h) | C++ API 검증 |
| vt_core.cpp | vt_bridge.h | C++ → C 브릿지 호출 |
| vt_bridge.c | ghostty/vt/*.h, ghostty-vt.dll | ghostty C API 호출 |
| ghostty-vt.dll | Zig 0.15.2 (gnu, simd=false) | VT 파싱 엔진 |
| 링크 | ghostty-vt.lib (import), ntdll.lib | DLL import + Zig 런타임 |

### 2.4 DLL 격리 패턴 (ADR-003)

GNU 타겟으로 빌드한 `ghostty-vt-static.lib`은 내부의 `compiler_rt.obj` COMDAT 심볼이 MSVC 링커(link.exe)와 호환되지 않는다 (LNK1143). DLL 방식을 사용하면:

- MSVC 링커는 `ghostty-vt.lib`(import lib)만 참조 — MSVC 호환 형식
- `compiler_rt.obj` 등 Zig 런타임은 DLL 내부에 격리
- `-Dsimd=false`로 CRT 의존성이 없으므로 DLL CRT 미초기화 문제도 발생하지 않음

---

## 3. File Structure (최종 구현)

```
ghostwin/
├── .zig-version                         # Zig 0.15.2 고정
├── .gitmodules                          # ghostty 서브모듈
├── CMakeLists.txt                       # CMake (C+CXX, Ninja)
├── onboarding.md
├── CLAUDE.md                            # 프로젝트 규칙
│
├── external/ghostty/                    # git submodule
│   ├── include/ghostty/vt.h             # C API 헤더
│   ├── zig-out/lib/ghostty-vt-static.lib  # 7.8MB (직접 링크 불가, 참고용)
│   ├── zig-out/lib/ghostty-vt.lib         # import lib (28KB, MSVC 링크용)
│   ├── zig-out/bin/ghostty-vt.dll         # 2.6MB (런타임 필요)
│   └── msvc_libc.txt                    # MSVC 경로 (MSVC 타겟 빌드 시만)
│
├── src/vt-core/
│   ├── vt_bridge.h                      # C/C++ 호환 인터페이스 (void*)
│   ├── vt_bridge.c                      # 순수 C — ghostty 헤더 include
│   ├── vt_core.h                        # C++ 공개 인터페이스
│   └── vt_core.cpp                      # C++ 구현 — vt_bridge.h만 include
│
├── tests/
│   ├── vt_core_test.cpp                 # 7개 테스트 (메인)
│   ├── vt_minimal_test.cpp              # 최소 crash 디버그용
│   └── ghostty_raw_test.c              # 순수 C API 직접 호출 테스트
│
├── scripts/
│   ├── build_libghostty.ps1             # libghostty-vt Zig 빌드
│   └── build_ghostwin.ps1              # CMake + Ninja + 테스트
│
└── docs/
    ├── 00-research/ (7개)               # 기술 리서치 + 트러블슈팅
    ├── 01-plan/features/                # Plan 문서
    ├── 02-design/features/              # Design 문서 (이 파일)
    ├── 03-analysis/                     # Gap 분석 보고서
    └── adr/ (3개)                       # Architecture Decision Records
```

---

## 4. C Bridge Interface

### 4.1 vt_bridge.h

```c
// C/C++ 호환 — void* 핸들로 ghostty 타입 숨김

/* Result codes */
#define VT_OK        0
#define VT_OOM      -1
#define VT_INVALID  -2

/* Dirty state */
#define VT_DIRTY_CLEAN   0
#define VT_DIRTY_PARTIAL 1
#define VT_DIRTY_FULL    2

/* Cursor style */
#define VT_CURSOR_BAR          0
#define VT_CURSOR_BLOCK        1
#define VT_CURSOR_UNDERLINE    2
#define VT_CURSOR_BLOCK_HOLLOW 3

typedef struct {
    int dirty;
    uint16_t cols;
    uint16_t rows;
    uint16_t cursor_x;
    uint16_t cursor_y;
    int cursor_visible;
    int cursor_style;
} VtRenderInfo;

void* vt_bridge_terminal_new(uint16_t cols, uint16_t rows, size_t max_scrollback);
void  vt_bridge_terminal_free(void* terminal);
void* vt_bridge_render_state_new(void);
void  vt_bridge_render_state_free(void* render_state);
void  vt_bridge_write(void* terminal, const uint8_t* data, size_t len);
VtRenderInfo vt_bridge_update_render_state(void* render_state, void* terminal);
int   vt_bridge_resize(void* terminal, uint16_t cols, uint16_t rows);
```

### 4.2 C++ Wrapper (vt_core.h)

```cpp
namespace ghostwin {

enum class DirtyState : int { Clean = 0, Partial = 1, Full = 2 };
enum class CursorStyle : int { Bar = 0, Block = 1, Underline = 2, BlockHollow = 3 };

struct RenderInfo {
    DirtyState dirty;
    uint16_t cols, rows, cursor_x, cursor_y;
    bool cursor_visible;
    CursorStyle cursor_style;
};

class VtCore {
public:
    static std::unique_ptr<VtCore> create(uint16_t cols, uint16_t rows,
                                          size_t max_scrollback = 10000);
    ~VtCore();
    void write(std::span<const uint8_t> data);
    RenderInfo update_render_state();
    bool resize(uint16_t cols, uint16_t rows);
    uint16_t cols() const;
    uint16_t rows() const;
private:
    VtCore();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
```

### 4.3 C API Mapping

| vt_bridge 함수 | ghostty C API | 비고 |
|----------------|---------------|------|
| `vt_bridge_terminal_new` | `ghostty_terminal_new(NULL, &term, opts)` | NULL allocator = 기본 |
| `vt_bridge_terminal_free` | `ghostty_terminal_free(term)` | |
| `vt_bridge_write` | `ghostty_terminal_vt_write(term, data, len)` | void 반환, 실패 없음 |
| `vt_bridge_update_render_state` | `ghostty_render_state_update` + 여러 `_get` | dirty 리셋 포함 |
| `vt_bridge_resize` | `ghostty_terminal_resize(term, c, r, 0, 0)` | pixel 크기 0 |

---

## 5. Build System

### 5.1 Zig 빌드 (libghostty-vt)

```bash
cd external/ghostty
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```

산출물:
- `zig-out/lib/ghostty-vt-static.lib` (7.8MB) — 참고용, MSVC 직접 링크 불가
- `zig-out/lib/ghostty-vt.lib` (28KB) — import lib, MSVC 링크용
- `zig-out/bin/ghostty-vt.dll` (2.6MB) — 런타임 필요

### 5.2 CMake 빌드 (VtCore + 테스트)

```bash
# MSVC 14.51 환경 필수
vcvarsall.bat x64 -vcvars_ver=14.51

cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
cmake --build build
./build/vt_core_test.exe
```

### 5.3 CMakeLists.txt 핵심

- `LANGUAGES C CXX` — C 브릿지 + C++ 래퍼
- ghostty-vt DLL: `SHARED IMPORTED` + import lib (`ghostty-vt.lib`)
- `target_link_libraries(... ntdll)` — Zig 런타임 Nt* 함수
- `copy_ghostty_dll` — DLL을 빌드 출력 디렉토리에 복사

### 5.4 한국어 Windows 빌드 주의사항

- MSVC의 `/showIncludes` 출력이 CP949 한글("참고: 포함 파일:")로 되어 있어 Ninja lexer에서 파싱 에러 발생
- `build_ghostwin.ps1`에서 CMake 캐시의 컴파일러 파일을 영어 접두사로 패치하여 해결
- `VSLANG=1033`은 cl.exe의 `/showIncludes` 출력 언어에 영향을 주지 않음

### 5.5 Static Lib 직접 링크가 불가능한 이유 (ADR-003)

Zig GNU 타겟으로 빌드한 `ghostty-vt-static.lib` 내부의 `compiler_rt.obj` COMDAT 심볼이 MSVC 링커와 호환되지 않음:

```
ghostty-vt-static.lib(compiler_rt.obj) : fatal error LNK1143
```

DLL 방식은 import lib만 MSVC가 참조하므로 이 문제를 구조적으로 회피한다.

---

## 6. Test Design

| # | Test | 검증 대상 | Pass 기준 |
|---|------|----------|----------|
| T1 | create_destroy | terminal + render_state 생성/해제 | crash 없음, 올바른 cols/rows |
| T2 | write_plain | 평문 쓰기 | crash 없음 |
| T3 | write_ansi | ANSI escape 파싱 | crash 없음 |
| T4 | render_state | 렌더 상태 업데이트 | dirty != Clean, 올바른 cols/rows |
| T5 | resize | 터미널 크기 변경 | 성공, 올바른 cols/rows |
| T6 | lifecycle_cycle | 50회 생성/쓰기/해제 반복 | crash 없음 (누수 검증) |
| T7 | dirty_reset | dirty 상태 리셋 확인 | 두 번째 호출 시 Clean |

**결과: 7/7 PASS**

---

## 7. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-28 | Initial draft | Solit |
| 0.2 | 2026-03-29 | 구현 반영: C 브릿지 패턴(ADR-002), gnu+simd=false(ADR-001), MSVC 14.51, 7/7 PASS | Solit |
| **0.3** | **2026-03-29** | **DLL 방식 확정(ADR-003 갱신): static lib MSVC 링커 불호환(LNK1143) 확인, VtRenderInfo 전체 필드 명세, C++ enum class 추가, 빌드 스크립트 한국어 로케일 대응** | **Solit** |
