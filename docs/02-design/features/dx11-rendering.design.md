# dx11-rendering Design Document

> **Summary**: D3D11 GPU 가속 렌더링 엔진 상세 설계 -- VtCore 셀 데이터를 GPU 인스턴싱으로 화면에 그린다
>
> **Project**: GhostWin Terminal
> **Version**: 0.3.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Reviewed
> **Plan Reference**: `docs/01-plan/features/dx11-rendering.plan.md`

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | Phase 2의 ConPTY -> VtCore 파이프라인은 완성되었으나, 파싱된 터미널 상태를 화면에 표시할 렌더러가 없어 시각적 확인이 불가능하다 |
| **Solution** | D3D11 GPU 인스턴싱 + DirectWrite 글리프 아틀라스 + `_api`/`_p` 이중 상태 분리 패턴을 적용한 렌더링 엔진. 전용 렌더 스레드에서 waitable object 기반 60fps 루프를 구동하고, dirty row 비트마스크 기반 증분 렌더로 유휴 시 GPU 0%를 달성한다 |
| **Function/UX Effect** | cmd.exe/pwsh.exe 출력이 GPU 가속으로 실시간 렌더링되어 Win32 HWND 창에서 시각적으로 확인 가능한 터미널 화면이 생성된다 |
| **Core Value** | 4-스레드 모델의 3번째 단계(렌더)를 실현하여, Phase 4(WinUI3 SwapChainPanel) 통합 전 렌더링 코어를 독립 검증한다 |

---

## 1. Architecture Overview

### 1.1 시스템 컨텍스트

```
┌─────────────────────────────────────────────────────────────────────┐
│                         GhostWin Process                            │
│                                                                     │
│  ┌──────────┐    ┌──────────┐    ┌─────────────┐    ┌───────────┐  │
│  │ ConPTY   │───>│ I/O      │───>│ VtCore      │───>│ Renderer  │  │
│  │ Session  │    │ Thread   │    │ (libghostty) │    │ (D3D11)   │  │
│  └──────────┘    └──────────┘    └─────────────┘    └───────────┘  │
│       ▲                                                    │        │
│       │          ┌──────────┐                              ▼        │
│       └──────────│ Main     │                        ┌──────────┐  │
│      (키 입력)   │ Thread   │                        │ Win32    │  │
│                  │ (Win32)  │                        │ HWND     │  │
│                  └──────────┘                        └──────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

### 1.2 4-스레드 모델

| Thread | 역할 | 동기화 |
|--------|------|--------|
| **I/O Thread** | ConPTY ReadFile -> VtCore.write() | vt_mutex lock |
| **Main Thread** | Win32 메시지 루프, 키 입력 -> ConPTY, 리사이즈 디바운스 | vt_mutex lock (resize) |
| **Render Thread** | StartPaint dirty-row 스냅샷, QuadInstance 구성, GPU 렌더 | vt_mutex lock (StartPaint, dirty row만 복사 -> 최소 시간) |
| ~~ConPTY Thread~~ | (OS 관리, Phase 2에서 생성) | 없음 |

### 1.3 핵심 설계 결정

| Decision | Selected | Rationale | Reference |
|----------|----------|-----------|-----------|
| 렌더링 API | D3D11 FL 10.0+ | WT 검증, 싱글스레드 렌더에 적합 | Research Agent 2 |
| 스왑체인 | HWND + FLIP_SEQUENTIAL + FRAME_LATENCY_WAITABLE_OBJECT | FLIP_DISCARD는 Present 후 이전 프레임 내용이 파기되어 dirty rect 증분 렌더에 부적합. waitable object를 위해 DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT 필요 | Plan 6.1 |
| 윈도우 스타일 | WS_EX_NOREDIRECTIONBITMAP | FLIP 모델 시 DWM 리다이렉션 surface 미할당 → 메모리 절감 + 레이턴시 감소. 필수는 아니나 성능상 강력 권장 | Research 6.2 권고 #3 |
| 스레드 동기화 | `_api`/`_p` 이중 상태 + StartPaint dirty-row-only 스냅샷 | GPU 작업 중 락 불필요, dirty row만 복사하여 vt_mutex 보유 시간 최소화 | WT AtlasEngine 패턴 |
| 인스턴스 버퍼 | USAGE_DYNAMIC + MAP_WRITE_DISCARD | 프레임당 1회 업로드, staging buffer 사전 할당으로 per-frame allocation 제거 | Research Agent 3 |
| 아틀라스 텍스처 | USAGE_DEFAULT + grow-only | GPU 최적 배치, LRU 미구현 (Phase 3 스코프) | Plan 6.1 |
| 아틀라스 패킹 | stb_rect_pack (Skyline) | C/C++ 최선, 헤더 only, MIT | Research Agent 2 |
| 폰트 래스터화 | DirectWrite 그레이스케일 AA | ClearType는 Phase 4 (dwrite-hlsl MIT 재사용 예정) | Plan 2.2 |
| 셰이더 컴파일 | Debug: D3DCompileFromFile(D3DCOMPILE_DEBUG) / Release: fxc.exe /O2 -> .cso | WT 동일 패턴. PIX/RenderDoc 프레임 캡처 시 디버그 심볼 활용 | Research 1.5 |
| 프레임 페이싱 | SetMaximumFrameLatency(1) + waitable object | 입력 레이턴시 최소화 | Research Agent 3 |
| Present | Present1() + dirty rect | DWM PSR 지원, 유휴 시 Present 생략 | Research Agent 3 |
| QuadInstance | 32바이트, 16비트 타입 | 캐시 라인(64B)에 2개 수용, MAP_WRITE_DISCARD 시 write combining 이점 | Plan 6.1 |
| dirty 추적 | std::bitset<256> 행별 비트마스크 | row 0과 row 199만 dirty 시 2행만 렌더 (연속 범위 방식의 과잉 렌더 방지) | WT invalidatedRows 개선 |
| 글리프 캐시 | 2-tier: ASCII 직접 매핑 + 비-ASCII 해시맵 | ASCII(영문 터미널 기준 다수) O(1) 접근, CJK 등 비-ASCII는 해시맵. 한국어 비중이 높은 세션에서는 Tier 2 비율 증가 | 성능 최적화 |
| 오파크 핸들 | typedef struct X* 타입 안전 핸들 (void* 금지) | 15개 함수 추가 시 void* 혼동 방지 | C API best practice |

---

## 2. Module Design

### 2.1 모듈 의존성 다이어그램

```
                    ┌──────────────────┐
                    │  terminal_window  │ (Win32 HWND, 메시지 루프)
                    │  .h / .cpp       │
                    └────────┬─────────┘
                             │ owns
                             ▼
┌───────────┐     ┌──────────────────┐     ┌──────────────┐
│ conpty    │────>│  dx11_renderer   │<────│ render_state │
│ (Phase 2) │     │  .h / .cpp       │     │ .h / .cpp    │
└───────────┘     └────────┬─────────┘     └──────────────┘
                           │ uses
                    ┌──────┼──────┐
                    ▼      ▼      ▼
             ┌──────────┐┌────────────┐┌──────────────┐
             │quad_     ││ glyph_atlas││ shader_vs/ps │
             │builder   ││ .h / .cpp  ││ .hlsl        │
             │.h / .cpp ││            ││              │
             └──────────┘└────────────┘└──────────────┘
```

### 2.2 파일 구조

```
src/
├── common/                        # Phase 3 공통 인프라 (신규)
│   ├── error.h                    # ErrorCode 열거, Error 구조체
│   ├── log.h                      # 스레드 안전 최소 로거
│   └── render_constants.h         # constexpr 상수 모음
├── renderer/                      # Phase 3 (신규)
│   ├── dx11_renderer.h            # 렌더러 공개 인터페이스
│   ├── dx11_renderer.cpp          # D3D11 디바이스, 스왑체인, 렌더 루프
│   ├── quad_builder.h             # 셀 -> QuadInstance 변환 전담 (SRP)
│   ├── quad_builder.cpp           # make_bg/text/cursor_quad
│   ├── glyph_atlas.h             # 글리프 아틀라스 관리 인터페이스
│   ├── glyph_atlas.cpp           # DirectWrite + stb_rect_pack + 2-tier 캐시
│   ├── render_state.h            # _api/_p 이중 상태 + flat buffer
│   ├── render_state.cpp          # StartPaint dirty-row-only 스냅샷
│   ├── terminal_window.h         # Win32 HWND 래퍼 (Phase 3 PoC)
│   ├── terminal_window.cpp       # 메시지 루프, 키 입력, 리사이즈 디바운스
│   ├── shader_vs.hlsl            # 버텍스 셰이더
│   └── shader_ps.hlsl            # 픽셀 셰이더
├── vt-core/                       # Phase 3 확장
│   ├── vt_bridge.h               # +타입 안전 핸들 + ~15개 행/셀 반복자 함수
│   ├── vt_bridge.c               # +15개 함수 구현
│   ├── vt_core.h                 # +CellData, for_each_row, palette()
│   └── vt_core.cpp               # +반복자 C++ 래퍼 + 스타일 변환
└── conpty/                        # Phase 2 (기존, 변경 없음)

third_party/
└── stb_rect_pack.h               # v1.01, MIT license
```

---

## 3. Cross-Cutting Concerns

### 3.1 에러 처리 전략 (Error Handling)

프로젝트 전체에서 일관된 에러 전파 패턴을 정의한다.

```cpp
// common/error.h
#pragma once
#include <cstdint>

namespace ghostwin {

enum class ErrorCode : uint32_t {
    Ok = 0,
    DeviceCreationFailed,
    SwapchainCreationFailed,
    ShaderCompilationFailed,
    AtlasOverflow,
    OutOfMemory,
    InvalidArgument,
    DeviceRemoved,
};

struct Error {
    ErrorCode code = ErrorCode::Ok;
    const char* message = nullptr;  // static lifetime 문자열

    [[nodiscard]] bool ok() const { return code == ErrorCode::Ok; }
    explicit operator bool() const { return ok(); }
};

} // namespace ghostwin
```

**팩토리 함수 규약**: `std::unique_ptr<T> create(config, Error* out_error = nullptr)` — nullptr 반환 시 `out_error`에 이유 기록. 호출자가 종료 여부를 결정한다.

### 3.2 로깅 인프라 (Logging)

4-스레드 동시 출력 시 stderr 뒤섞임 방지를 위한 최소 스레드 안전 로거.

```cpp
// common/log.h
#pragma once
#include <cstdio>
#include <cstdarg>
#include <mutex>

namespace ghostwin {

enum class LogLevel { Debug, Info, Warn, Error };

inline void log(LogLevel level, const char* tag, const char* fmt, ...) {
    static std::mutex log_mutex;
    static constexpr const char* level_str[] = {"DBG", "INF", "WRN", "ERR"};

    va_list args;
    va_start(args, fmt);
    {
        std::lock_guard lock(log_mutex);
        fprintf(stderr, "[%s][%s] ", level_str[static_cast<int>(level)], tag);
        vfprintf(stderr, fmt, args);
        fputc('\n', stderr);
    }
    va_end(args);
}

#define LOG_D(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Debug, tag, __VA_ARGS__)
#define LOG_I(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Info,  tag, __VA_ARGS__)
#define LOG_W(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Warn,  tag, __VA_ARGS__)
#define LOG_E(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Error, tag, __VA_ARGS__)

} // namespace ghostwin
```

### 3.3 상수 정의 (Constants)

매직 넘버를 제거하고 constexpr 상수로 집중 관리.

```cpp
// common/render_constants.h
#pragma once
#include <cstdint>

namespace ghostwin::constants {

// 터미널 기본값
constexpr uint16_t kDefaultCols = 80;
constexpr uint16_t kDefaultRows = 24;

// CellData
constexpr uint8_t kMaxCodepoints = 4;  // ZWJ 4개면 99%+ 커버, Phase 3 이모지 스코프 밖

// 리사이즈
constexpr uint32_t kResizeDebounceMs = 100;

// 스왑체인
constexpr uint32_t kSwapchainBufferCount = 2;

// 아틀라스
constexpr uint32_t kInitialAtlasSize = 1024;
constexpr uint32_t kMaxAtlasSize = 4096;

// 폰트
constexpr float kDefaultFontSizePt = 12.0f;

// 인스턴스 버퍼
constexpr uint32_t kQuadInstanceSize = 32;
constexpr uint32_t kIndexCount = 6;              // [0,1,2,0,2,3]
constexpr uint32_t kInstanceMultiplier = 3;      // bg + text + decoration

// dirty row
constexpr uint16_t kMaxRows = 256;

// 커서 블링킹
constexpr uint32_t kDefaultBlinkIntervalMs = 530;  // GetCaretBlinkTime 폴백

} // namespace ghostwin::constants
```

---

## 4. Detailed Interface Design

### 4.1 vt_bridge 확장 API (C, FR-01)

기존 `vt_bridge.h`에 추가. **void* 대신 타입 안전 핸들 사용 (기존 API도 점진 마이그레이션)**.

```c
/* ─── 타입 안전 오파크 핸들 ─── */
typedef struct VtRowIteratorImpl*  VtRowIterator;
typedef struct VtCellIteratorImpl* VtCellIterator;

/* ─── 색상 ─── */
typedef struct {
    uint8_t r, g, b, a;
} VtColor;

/* ─── 셀 스타일 플래그 (패킹) ─── */
typedef uint8_t VtStyleFlags;
#define VT_STYLE_BOLD          (1 << 0)
#define VT_STYLE_ITALIC        (1 << 1)
#define VT_STYLE_UNDERLINE     (1 << 2)
#define VT_STYLE_STRIKETHROUGH (1 << 3)
#define VT_STYLE_INVERSE       (1 << 4)
#define VT_STYLE_WIDE          (1 << 5)

/* ─── 팔레트 (ANSI 256) ─── */
typedef struct {
    VtColor palette[256];
    VtColor default_fg;
    VtColor default_bg;
    VtColor cursor_color;
} VtPalette;

/* ─── 커서 정보 ─── */
typedef struct {
    uint16_t x;
    uint16_t y;
    int      style;     /* VT_CURSOR_* 상수 */
    int      visible;
    int      blink;     /* 블링킹 활성 여부 */
} VtCursorInfo;

/* ─── 행 반복자 ─── */
VtRowIterator  vt_bridge_row_iterator_new(void);
void           vt_bridge_row_iterator_free(VtRowIterator iter);
int            vt_bridge_row_iterator_init(VtRowIterator iter, void* render_state);
int            vt_bridge_row_iterator_next(VtRowIterator iter);
int            vt_bridge_row_is_dirty(VtRowIterator iter);
void           vt_bridge_row_set_clean(VtRowIterator iter);

/* ─── 셀 반복자 ─── */
VtCellIterator vt_bridge_cell_iterator_new(void);
void           vt_bridge_cell_iterator_free(VtCellIterator iter);
int            vt_bridge_cell_iterator_init(VtCellIterator iter, VtRowIterator row);
int            vt_bridge_cell_iterator_next(VtCellIterator iter);

/* ─── 셀 데이터 접근 ─── */
size_t         vt_bridge_cell_grapheme_count(VtCellIterator iter);
size_t         vt_bridge_cell_graphemes(VtCellIterator iter,
                                        uint32_t* buf, size_t buf_len);
VtStyleFlags   vt_bridge_cell_style(VtCellIterator iter);
VtColor        vt_bridge_cell_fg_color(VtCellIterator iter, const VtPalette* pal);
VtColor        vt_bridge_cell_bg_color(VtCellIterator iter, const VtPalette* pal);

/* ─── 팔레트 + 커서 ─── */
int            vt_bridge_get_palette(void* render_state, VtPalette* out);
int            vt_bridge_get_cursor(void* render_state, VtCursorInfo* out);
```

**ghostty API 매핑 테이블**:

| vt_bridge | ghostty API | 비고 |
|-----------|-------------|------|
| `row_iterator_new` | `ghostty_render_state_row_iterator_new` | opaque handle |
| `row_iterator_init` | `ghostty_render_state_get(ROW_ITERATOR)` | render_state 필요 |
| `row_iterator_next` | `ghostty_render_state_row_iterator_next` | 0=end, 1=valid |
| `row_is_dirty` | `ghostty_render_state_row_get(DIRTY)` | dirty flag |
| `cell_iterator_init` | `ghostty_render_state_row_get(CELLS)` | row_iter 필요 |
| `cell_iterator_next` | `ghostty_render_state_row_cells_next` | 0=end, 1=valid |
| `cell_graphemes` | `get(GRAPHEMES_BUF)` | UTF-32 코드포인트 |
| `cell_style` | `get(STYLE)` | VtStyleFlags 비트 패킹 |
| `cell_fg_color` | `get(FG_COLOR)` + palette 해석 | ANSI/256/RGB |
| `cell_bg_color` | `get(BG_COLOR)` + palette 해석 | ANSI/256/RGB |
| `get_palette` | `ghostty_render_state_colors_get` | 256색 + default |
| `get_cursor` | 기존 API + `CURSOR_BLINK` 추가 | 블링킹 상태 포함 |

### 4.2 VtCore C++ 확장 (FR-01)

기존 `vt_core.h`에 추가:

```cpp
namespace ghostwin {

/// 셀 데이터 -- 크기 최적화 (32B 고정)
struct CellData {
    uint32_t    codepoints[constants::kMaxCodepoints]; // 16B (4 코드포인트)
    uint32_t    fg_packed;      // 4B -- RGBA (VtColor를 uint32_t로 팩킹)
    uint32_t    bg_packed;      // 4B -- RGBA
    uint8_t     cp_count;       // 1B -- 실제 코드포인트 수
    VtStyleFlags style_flags;   // 1B -- bold|italic|underline|... 비트 패킹
    uint8_t     _pad[6];        // 6B -- 32B 정렬
};
static_assert(sizeof(CellData) == 32, "CellData must be 32 bytes for memcpy alignment");

/// Row iteration callback.
using RowCallback = std::function<void(uint16_t row_index, bool dirty,
                                       std::span<const CellData> cells)>;

class VtCore {
public:
    // ... 기존 메서드 유지 ...

    /// Phase 3: 행/셀 데이터를 콜백으로 순회.
    /// vt_mutex 보유 상태에서 호출해야 한다.
    /// 내부에서 vt_bridge 비트필드 -> CellData 변환을 수행한다.
    void for_each_row(RowCallback callback);

    /// Phase 3: 팔레트 조회.
    [[nodiscard]] VtPalette palette() const;

    /// Phase 3: 커서 정보 조회.
    [[nodiscard]] VtCursorInfo cursor_info() const;

    /// Phase 3: render_state 내부 핸들 접근 (StartPaint 스냅샷용).
    [[nodiscard]] void* raw_render_state() const;
    [[nodiscard]] void* raw_terminal() const;
};

} // namespace ghostwin
```

**스타일 변환 위치**: `VtCore::for_each_row()` 내부에서 `vt_bridge_cell_style()` 반환값(`VtStyleFlags`)을 `CellData.style_flags`에 직접 대입 (비트 레이아웃 동일, 변환 비용 0).

### 4.3 RenderState -- `_api`/`_p` 이중 상태 (FR-06)

**핵심 개선: flat buffer + dirty-row-only 복사 + 비트마스크**

```cpp
// render_state.h
#pragma once

#include "common/render_constants.h"
#include <cstdint>
#include <vector>
#include <bitset>
#include <mutex>

namespace ghostwin {

struct CellData;

/// 렌더 프레임 데이터 -- flat buffer로 메모리 연속성 보장.
/// rows * cols 크기의 CellData를 단일 vector에 배치.
struct RenderFrame {
    std::vector<CellData> cell_buffer;  // rows * cols, 한 번만 할당
    uint16_t cols = 0;
    uint16_t rows_count = 0;

    // 행 접근 (연속 메모리 slice)
    std::span<CellData> row(uint16_t r) {
        return {cell_buffer.data() + r * cols, cols};
    }
    std::span<const CellData> row(uint16_t r) const {
        return {cell_buffer.data() + r * cols, cols};
    }

    // dirty row 비트마스크 (행별 독립 추적, 최대 256행)
    std::bitset<constants::kMaxRows> dirty_rows;

    bool is_row_dirty(uint16_t r) const { return dirty_rows.test(r); }
    void set_row_dirty(uint16_t r) { dirty_rows.set(r); }
    void clear_all_dirty() { dirty_rows.reset(); }
    bool any_dirty() const { return dirty_rows.any(); }

    // 커서
    VtCursorInfo cursor{};

    // 팔레트
    VtPalette palette{};

    // 할당
    void allocate(uint16_t c, uint16_t r) {
        cols = c;
        rows_count = r;
        cell_buffer.resize(static_cast<size_t>(c) * r);
        dirty_rows.reset();
    }
};

/// _api/_p 이중 상태 관리자.
class TerminalRenderState {
public:
    TerminalRenderState(uint16_t cols, uint16_t rows);

    /// API 측: VtCore에서 렌더 상태를 _api에 업데이트한다.
    /// start_paint() 내부에서 호출됨 (vt_mutex 보유 상태).
    /// I/O 스레드가 직접 호출하지 않는다 -- VtCore 내부 상태를
    /// 렌더 시점에 한 번 읽어오는 방식.
    void update_from_vtcore(class VtCore& vt);

    /// 렌더 스레드: VtCore에서 _api를 업데이트 후 dirty row만 _p에 복사.
    /// vt_mutex를 내부에서 lock/unlock (최소 시간).
    /// 반환값: dirty가 있으면 true.
    bool start_paint(std::mutex& vt_mutex, class VtCore& vt);

    /// 렌더 스레드: 현재 프레임 데이터 접근 (start_paint 이후에만 유효).
    [[nodiscard]] const RenderFrame& frame() const;

    /// 리사이즈
    void resize(uint16_t cols, uint16_t rows);

private:
    RenderFrame _api;   // API 스레드가 기록
    RenderFrame _p;     // 렌더 스레드가 읽기 전용
    bool _api_dirty = false;
};

} // namespace ghostwin
```

**StartPaint 시퀀스 (dirty-row-only 복사)**:

```
Render Thread                     vt_mutex
─────────────                     ────────
start_paint(vt_mutex, vtcore) 진입
  lock(vt_mutex)  ───────────>    LOCKED

  // 1. VtCore → _api 업데이트 (행/셀 반복자로 셀 데이터 읽기)
  update_from_vtcore(vtcore);     // for_each_row → _api.cell_buffer 갱신
  if (!_api.any_dirty())
    unlock → return false         UNLOCKED (렌더 생략)

  // 2. dirty row만 _api → _p 복사 (O(dirty_rows * cols) only)
  _p.dirty_rows = _api.dirty_rows;
  for each set bit r in _api.dirty_rows:
    memcpy(_p.row(r).data(), _api.row(r).data(), cols * sizeof(CellData))

  // 커서 + 팔레트는 항상 복사 (경량)
  _p.cursor = _api.cursor;
  _p.palette = _api.palette;

  _api.clear_all_dirty();
  _api_dirty = false;
  unlock(vt_mutex)  ─────────>    UNLOCKED
  return true                     (렌더 진행)
```

**복사 비용 분석**:

| 시나리오 | CellData 크기 | 전체 복사 | dirty-row-only (1행) |
|----------|-------------|-----------|---------------------|
| 80x24 | 32B | 60 KB | 2.5 KB |
| 200x50 | 32B | 312 KB | 6.25 KB |
| 300x80 | 32B | 750 KB | 9.4 KB |

**vt_mutex 보유 시간 (추정)**: dirty 1행 = ~2.5KB memcpy ≈ 1-5μs (캐시 상태 의존). 전체 스크롤(24행) = ~60KB ≈ 15-50μs. 16ms 프레임 버짓 대비 충분히 작음. 실측은 구현 후 FrameStats로 확인.

### 4.4 DX11Renderer (FR-02, FR-05, FR-06, FR-07)

```cpp
// dx11_renderer.h
#pragma once

#include "common/error.h"
#include <cstdint>
#include <memory>
#include <mutex>

struct HWND__;
typedef HWND__* HWND;

namespace ghostwin {

class TerminalRenderState;

struct RendererConfig {
    HWND hwnd = nullptr;
    uint16_t cols = constants::kDefaultCols;
    uint16_t rows = constants::kDefaultRows;
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
};

class DX11Renderer {
public:
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create(
        const RendererConfig& config, Error* out_error = nullptr);
    ~DX11Renderer();

    DX11Renderer(const DX11Renderer&) = delete;
    DX11Renderer& operator=(const DX11Renderer&) = delete;

    /// 렌더 스레드 시작. 내부에서 스레드를 생성한다.
    void start(TerminalRenderState& state, std::mutex& vt_mutex);

    /// 렌더 스레드 정지 (동기적, join).
    void stop();

    /// 스왑체인 리사이즈 (Main Thread에서 호출).
    void resize_swapchain(uint32_t width_px, uint32_t height_px);

    /// 셀 크기 조회 (폰트 메트릭 기반).
    [[nodiscard]] uint32_t cell_width_px() const;
    [[nodiscard]] uint32_t cell_height_px() const;

    /// Debug Layer 리포트 (셧다운 시 호출).
    void report_live_objects();

private:
    DX11Renderer();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
```

**Impl 내부 ComPtr 규약**: `DX11Renderer::Impl`은 `Microsoft::WRL::ComPtr<T>`로 모든 D3D11/DXGI COM 객체를 관리한다. raw pointer 직접 소유 금지.

```cpp
// dx11_renderer.cpp 내부 (Impl 멤버 발췌)
struct DX11Renderer::Impl {
    ComPtr<ID3D11Device>        device;
    ComPtr<ID3D11DeviceContext>  context;
    ComPtr<IDXGISwapChain1>     swapchain;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11Buffer>        index_buffer;
    ComPtr<ID3D11Buffer>        instance_buffer;
    ComPtr<ID3D11Buffer>        constant_buffer;
    ComPtr<ID3D11VertexShader>  vertex_shader;
    ComPtr<ID3D11PixelShader>   pixel_shader;
    ComPtr<ID3D11InputLayout>   input_layout;
    ComPtr<ID3D11BlendState>    blend_state;
    HANDLE                      frame_latency_waitable = nullptr;

    std::unique_ptr<GlyphAtlas> atlas;
    std::unique_ptr<QuadBuilder> quad_builder;

    // staging buffer -- 사전 할당, per-frame allocation 제거
    std::vector<QuadInstance> instance_staging;

    std::thread render_thread;
    std::atomic<bool> stop_flag{false};

    uint32_t staging_capacity = 0;
    // ...
};
```

#### 4.4.1 D3D11 초기화 시퀀스 (FR-02)

```
 1. D3D11CreateDevice(HARDWARE, FL 10.0+, DEBUG flag)
      ├── Debug 빌드: D3D11_CREATE_DEVICE_DEBUG | BGRA_SUPPORT
      └── Release 빌드: BGRA_SUPPORT only
      └── 실패 시: WARP 폴백 시도 (D3D_DRIVER_TYPE_WARP)
 2. IDXGIDevice -> IDXGIAdapter -> IDXGIFactory2
 3. CreateSwapChainForHwnd(FLIP_SEQUENTIAL, B8G8R8A8_UNORM, BufferCount=2,
      Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT)
 4. QueryInterface -> IDXGISwapChain2 -> SetMaximumFrameLatency(1)
 5. frame_latency_waitable = swapchain2->GetFrameLatencyWaitableObject()
 6. GetBuffer(0) -> CreateRenderTargetView
 7. VS 상수 버퍼 생성 (positionScale, atlasScale)
 8. 인덱스 버퍼 생성 (IMMUTABLE, 6 indices: [0,1,2,0,2,3])
 9. instance_staging 사전 할당 (cols * rows * kInstanceMultiplier)
10. 인스턴스 버퍼 생성 (DYNAMIC, staging_capacity * kQuadInstanceSize)
11. 셰이더 로드 (Debug: D3DCompileFromFile /Zi / Release: .cso 파일)
12. 입력 레이아웃 생성 (QuadInstance 대응)
13. 블렌드 스테이트 생성 (premultiplied alpha)
14. 뷰포트 설정
```

#### 4.4.2 QuadInstance 구조체 (FR-05)

```cpp
#pragma pack(push, 1)
struct QuadInstance {
    uint16_t shading_type;    //  2B -- ShadingType 열거
    uint8_t  pad[2];          //  2B -- 정렬 패딩
    int16_t  pos_x;           //  2B -- 화면 픽셀 X
    int16_t  pos_y;           //  2B -- 화면 픽셀 Y
    uint16_t size_x;          //  2B -- 셀 폭 (픽셀)
    uint16_t size_y;          //  2B -- 셀 높이 (픽셀)
    uint16_t tex_u;           //  2B -- 아틀라스 U
    uint16_t tex_v;           //  2B -- 아틀라스 V
    uint16_t tex_w;           //  2B -- 글리프 폭
    uint16_t tex_h;           //  2B -- 글리프 높이
    uint32_t fg_color;        //  4B -- RGBA 전경색
    uint32_t bg_color;        //  4B -- RGBA 배경색
};                            // 합계: 32바이트 (AVX 정렬)
#pragma pack(pop)

static_assert(sizeof(QuadInstance) == constants::kQuadInstanceSize);

enum class ShadingType : uint16_t {
    TextBackground  = 0,
    TextGrayscale   = 1,
    Cursor          = 2,
    SolidLine       = 3,  // 밑줄, 취소선
};
```

#### 4.4.3 QuadBuilder -- 셀 -> QuadInstance 변환 (SRP 분리)

```cpp
// quad_builder.h
#pragma once

#include <cstdint>
#include <span>

struct ID3D11DeviceContext;

namespace ghostwin {

struct QuadInstance;
struct RenderFrame;
class GlyphAtlas;

/// 셀 데이터에서 QuadInstance 배열을 구성하는 전담 클래스.
/// DX11Renderer에서 분리하여 독립 테스트 가능.
class QuadBuilder {
public:
    QuadBuilder(uint32_t cell_w, uint32_t cell_h);

    /// dirty rows에서 QuadInstance 배열을 생성한다.
    /// 반환값: 인스턴스 수.
    uint32_t build(const RenderFrame& frame,
                   GlyphAtlas& atlas,
                   ID3D11DeviceContext* ctx,
                   std::span<QuadInstance> out_buffer);

    void update_cell_size(uint32_t cell_w, uint32_t cell_h);

private:
    QuadInstance make_bg_quad(uint32_t fg, uint32_t bg,
                              uint16_t row, uint16_t col) const;
    QuadInstance make_text_quad(const struct GlyphEntry& glyph,
                                uint32_t fg, uint32_t bg,
                                uint16_t row, uint16_t col) const;
    QuadInstance make_cursor_quad(const RenderFrame& frame) const;
    QuadInstance make_underline_quad(uint32_t fg,
                                     uint16_t row, uint16_t col) const;

    uint32_t cell_w_;
    uint32_t cell_h_;
};

} // namespace ghostwin
```

#### 4.4.4 렌더 루프 (FR-06, FR-07)

```
Render Thread Loop:
──────────────────
while (!stop_flag.load(relaxed)) {
    // 1. 프레임 페이싱 -- waitable object 대기
    WaitForSingleObject(frame_latency_waitable, 100ms);
    if (stop_flag) break;

    // 2. VtCore → _api 업데이트 + dirty-row-only 스냅샷 (vt_mutex 최소 시간)
    bool has_dirty = render_state.start_paint(vt_mutex, vtcore);
    if (!has_dirty) continue;  // Present 생략 → GPU 0%

    // 3. QuadBuilder로 인스턴스 구성 (GPU 미사용, vt_mutex 미보유)
    uint32_t count = quad_builder->build(
        render_state.frame(), *atlas, context.Get(),
        std::span(instance_staging));

    if (count == 0) continue;

    // 4. GPU 업로드 (MAP_WRITE_DISCARD, 프레임당 1회)
    D3D11_MAPPED_SUBRESOURCE mapped;
    context->Map(instance_buffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    memcpy(mapped.pData, instance_staging.data(), count * kQuadInstanceSize);
    context->Unmap(instance_buffer.Get(), 0);

    // 5. Draw
    context->DrawIndexedInstanced(kIndexCount, count, 0, 0, 0);

    // 6. Present1(1, dirty_rect)
    DXGI_PRESENT_PARAMETERS params = {};
    // dirty_rect는 dirty rows의 최소/최대 y 범위로 계산
    swapchain->Present1(1, 0, &params);
}
```

#### 4.4.5 Device Removed 복구 시퀀스

```
Present1() 반환 확인:
  DXGI_ERROR_DEVICE_REMOVED 또는 DXGI_ERROR_DEVICE_RESET 발생 시:

  Phase 3: 로그 + 정상 종료
  1. LOG_E("renderer", "Device removed: reason=0x%X", device->GetDeviceRemovedReason())
  2. stop_flag = true → 렌더 루프 탈출
  3. 프로그램 정상 종료 (리소스는 소멸자에서 ComPtr 해제)

  Phase 4 (미래): 디바이스 재생성 시퀀스
  - 기존 리소스 역순 해제 → D3D11CreateDevice 재시도 → WARP 폴백 → 리소스 재생성
```

### 4.5 GlyphAtlas (FR-04)

```cpp
// glyph_atlas.h
#pragma once

#include "common/error.h"
#include <cstdint>
#include <memory>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11ShaderResourceView;

namespace ghostwin {

/// 글리프 아틀라스 내 위치
struct GlyphEntry {
    uint16_t u, v;
    uint16_t width, height;
    int16_t  offset_x, offset_y;
    bool     valid = false;
};

struct AtlasConfig {
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
    uint32_t initial_size = constants::kInitialAtlasSize;
    uint32_t max_size = constants::kMaxAtlasSize;
};

class GlyphAtlas {
public:
    [[nodiscard]] static std::unique_ptr<GlyphAtlas> create(
        ID3D11Device* device, const AtlasConfig& config,
        Error* out_error = nullptr);
    ~GlyphAtlas();

    /// 글리프 조회 또는 래스터화.
    GlyphEntry lookup_or_rasterize(
        ID3D11DeviceContext* ctx,
        const uint32_t* codepoints, uint8_t cp_count,
        uint8_t style_flags);

    [[nodiscard]] ID3D11ShaderResourceView* srv() const;
    [[nodiscard]] uint32_t cell_width() const;
    [[nodiscard]] uint32_t cell_height() const;

    // 통계 (디버그용)
    [[nodiscard]] uint32_t glyph_count() const;
    [[nodiscard]] uint32_t atlas_width() const;
    [[nodiscard]] uint32_t atlas_height() const;

private:
    GlyphAtlas();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
```

#### 4.5.1 2-Tier 글리프 캐시

ASCII 95자가 터미널 출력의 95%+ 를 차지. O(1) 직접 접근으로 해시 연산 제거.

```cpp
// glyph_atlas.cpp 내부
struct GlyphAtlas::Impl {
    // Tier 1: ASCII 직접 매핑 (O(1), 해시 없음)
    // [codepoint][shape_key: bold|italic|wide 3bit] = 128 * 8 = 1024 엔트리
    std::array<GlyphEntry, 128 * 8> ascii_cache{};

    // Tier 2: 비-ASCII + 복합 문자 해시맵
    struct GlyphKey {
        uint32_t codepoint;
        uint8_t  style_flags;
        bool operator==(const GlyphKey&) const = default;
    };
    struct GlyphKeyHash {
        size_t operator()(const GlyphKey& k) const noexcept {
            return k.codepoint ^ (static_cast<size_t>(k.style_flags) << 21);
        }
    };
    std::unordered_map<GlyphKey, GlyphEntry, GlyphKeyHash> complex_cache;

    GlyphEntry& lookup(uint32_t cp, uint8_t style) {
        // 글리프 형태에 영향을 주는 속성만 캐시 키에 포함:
        // bold(bit0), italic(bit1) → 글리프 형태 변경
        // wide(bit5) → 래스터화 폭 2배 (전각)
        // underline/strikethrough/inverse → 별도 QuadInstance로 처리, 캐시 무관
        uint8_t shape_key = (style & 0x3) | ((style >> 3) & 0x4);  // bold|italic|wide → 3bit, 8가지
        if (cp < 128 && shape_key < 8) {
            return ascii_cache[cp * 8 + shape_key];
        }
        return complex_cache[{cp, style}];
    }
};
```

#### 4.5.2 DirectWrite MapCharacters 블록 캐싱

```cpp
/// 폰트 폴백 캐시 -- Unicode 블록(256 codepoint) 단위로 캐싱.
/// MapCharacters 호출 빈도 감소가 목적 (WT에서 CJK 시 CPU 85% 소모 보고).
/// 주의: 같은 블록 내에서도 폰트가 다를 수 있으므로 (예: 일부 CJK 확장),
/// cache miss 시 개별 codepoint fallback으로 재시도하는 로직 필요.
struct FontFallbackCache {
    // block_id = codepoint >> 8
    std::unordered_map<uint32_t, ComPtr<IDWriteFontFace>> block_cache;

    ComPtr<IDWriteFontFace> resolve(
        uint32_t codepoint,
        IDWriteFontFallback* fallback,
        IDWriteTextFormat* base_format)
    {
        uint32_t block = codepoint >> 8;
        auto it = block_cache.find(block);
        if (it != block_cache.end()) return it->second;

        // cache miss → MapCharacters 호출 (비용 큼)
        auto face = do_map_characters(codepoint, fallback, base_format);
        block_cache[block] = face;
        return face;
    }
};
```

**아틀라스 관리 전략**:

| 항목 | Phase 3 | Phase 4 (미래) |
|------|---------|----------------|
| 텍스처 포맷 | R8_UNORM (그레이스케일) | R8G8B8A8_UNORM (ClearType + 이모지) |
| 초기 크기 | 1024x1024 | 동적 (윈도우 크기 기반) |
| 성장 전략 | grow-only (2x), 최대 4096 | LRU 퇴출 |
| 캐시 | 2-tier: ASCII 직접 매핑 + 해시맵 | + font_face별 분리 캐시 |
| 폰트 폴백 | GetSystemFontFallback() + 블록 캐싱 | + 사용자 설정 폴백 체인 |
| CJK 최적화 | MapCharacters 블록 캐싱 | 폰트 페이스별 분리 캐시 (WT 패턴) |

**아틀라스 grow 시퀀스**: 기존 텍스처 전체를 새 텍스처에 CopyResource → stb_rect_pack 리셋 (기존 영역을 occupied로 마킹) → 새 글리프는 확장 영역에 패킹.

### 4.6 TerminalWindow -- Win32 HWND (FR-10, FR-11)

```cpp
// terminal_window.h
#pragma once

#include <cstdint>
#include <memory>

namespace ghostwin {

class ConPtySession;
class DX11Renderer;

struct WindowConfig {
    uint16_t cols = constants::kDefaultCols;
    uint16_t rows = constants::kDefaultRows;
    const wchar_t* title = L"GhostWin Terminal";
};

class TerminalWindow {
public:
    [[nodiscard]] static std::unique_ptr<TerminalWindow> create(
        const WindowConfig& config);
    ~TerminalWindow();

    /// 메시지 루프 실행 (블로킹). WM_QUIT까지.
    int run(ConPtySession& session, DX11Renderer& renderer);

    [[nodiscard]] HWND hwnd() const;

private:
    TerminalWindow();
    struct Impl;
    std::unique_ptr<Impl> impl_;
    static LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);
};

} // namespace ghostwin
```

**HWND 생성 시 필수 플래그**:

```cpp
HWND hwnd = CreateWindowExW(
    WS_EX_NOREDIRECTIONBITMAP,  // FLIP 스왑체인 성능 권장 — DWM 리다이렉션 surface 미할당
    wc.lpszClassName,
    config.title,
    WS_OVERLAPPEDWINDOW,
    CW_USEDEFAULT, CW_USEDEFAULT,
    width_px, height_px,
    nullptr, nullptr, hInstance, this);
```

**근거**: FLIP 스왑체인에서 이 플래그 없이도 동작하나, DWM이 추가 리다이렉션 surface를 할당하여 VRAM 및 레이턴시 증가. WT도 사용하며, Research 6.2 권고 #3에서 강력 권장.

**키 입력 처리 (FR-11)**:

| Win32 Message | 처리 | 비고 |
|---------------|------|------|
| WM_CHAR | UTF-16 -> UTF-8 변환 -> `session.send_input()` | 일반 문자 입력 |
| WM_KEYDOWN | VK_RETURN, VK_BACK, VK_TAB, 방향키 -> VT 시퀀스 | 특수 키 |
| WM_KEYDOWN + Ctrl | Ctrl+C (0x03), Ctrl+D (0x04), etc. | 제어 문자 |
| WM_SIZE | 100ms 디바운스 타이머 리셋 | FR-10 |
| WM_TIMER(kResizeTimer) | 디바운스 만료 -> 리사이즈 실행 | 아래 참조 |
| WM_CLOSE | 렌더 스레드 정지 -> ConPTY 셧다운 | 정리 |

**커서 블링킹 (FR-09)**:

```
Main Thread:
  WM_CREATE 시:
    blink_interval = GetCaretBlinkTime();  // 시스템 설정 (기본 530ms)
    if (blink_interval == INFINITE) → 블링킹 비활성
    else SetTimer(hwnd, kBlinkTimer, blink_interval, nullptr);

  WM_TIMER(kBlinkTimer):
    cursor_blink_visible = !cursor_blink_visible;
    // 렌더 스레드에 dirty 신호 전달 (cursor row만 dirty)

  WM_SETFOCUS:  커서 표시 + 블링크 타이머 시작
  WM_KILLFOCUS: 커서 숨김 + 블링크 타이머 정지
```

**리사이즈 시퀀스 (FR-10)**:

```
WM_SIZE 수신 (Main Thread):
  1. KillTimer(kResizeTimer) + SetTimer(kResizeTimer, kResizeDebounceMs)

WM_TIMER(kResizeTimer) 수신 (Main Thread):
  1. KillTimer(kResizeTimer)
  2. 새 크기: cols = width_px / cell_width, rows = height_px / cell_height
  3. renderer.stop()              -- 렌더 스레드 join
  4. renderer.resize_swapchain(width_px, height_px)  -- ResizeBuffers
  5. {lock vt_mutex}
     session.resize(cols, rows)   -- ConPTY + VtCore
     render_state.resize(cols, rows)
     {unlock vt_mutex}
  6. renderer.start(...)          -- 렌더 스레드 재시작
```

### 4.7 HLSL 셰이더 (FR-03)

#### 버텍스 셰이더 (`shader_vs.hlsl`)

```hlsl
cbuffer ConstBuffer : register(b0) {
    float2 positionScale;  // = float2(2.0/width, -2.0/height)
    float2 atlasScale;     // = float2(1.0/atlas_width, 1.0/atlas_height)
};

struct VSInput {
    uint   shadingType : SHADING_TYPE;
    uint   pad         : PAD;
    int2   position    : POSITION;
    uint2  size        : SIZE;
    uint2  texcoord    : TEXCOORD;
    uint2  texsize     : TEXSIZE;
    float4 fgColor     : FG_COLOR;
    float4 bgColor     : BG_COLOR;
    uint   vertexId    : SV_VertexID;
};

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    uint   shadingType : BLENDINDICES0;
};

PSInput main(VSInput input) {
    PSInput output;
    float2 corner = float2(
        (input.vertexId == 1 || input.vertexId == 2) ? 1.0 : 0.0,
        (input.vertexId == 2 || input.vertexId == 3) ? 1.0 : 0.0);

    float2 pixelPos = float2(input.position) + corner * float2(input.size);
    output.pos = float4(pixelPos * positionScale + float2(-1.0, 1.0), 0.0, 1.0);

    float2 texOffset = corner * float2(input.texsize);
    output.uv = (float2(input.texcoord) + texOffset) * atlasScale;

    output.fgColor = input.fgColor;
    output.bgColor = input.bgColor;
    output.shadingType = input.shadingType;
    return output;
}
```

#### 픽셀 셰이더 (`shader_ps.hlsl`)

**참고**: `PSInput` 구조체는 VS/PS 모두에서 사용된다. 구현 시 `shader_common.hlsl`로 분리하여 `#include`하거나, PS에서 동일 구조체를 재정의한다.

```hlsl
Texture2D<float> glyphAtlas : register(t0);
SamplerState     pointSamp : register(s0);  // point filtering -- 글리프 픽셀 정확도 보장

float4 main(PSInput input) : SV_Target {
    if (input.shadingType == 0)  // Background
        return input.bgColor;
    if (input.shadingType == 1) {  // Grayscale text
        float alpha = glyphAtlas.Sample(pointSamp, input.uv);
        return float4(input.fgColor.rgb * alpha, alpha);  // premultiplied
    }
    if (input.shadingType == 2)  // Cursor
        return input.fgColor;
    if (input.shadingType == 3)  // Solid line (underline, strikethrough)
        return input.fgColor;
    return float4(1, 0, 1, 1);  // debug magenta
}
```

**블렌드 스테이트**: `SrcBlend=ONE, DestBlend=INV_SRC_ALPHA, BlendOp=ADD` (premultiplied alpha).

**셰이더 디버깅**: Debug 빌드에서 `D3DCompileFromFile`에 `D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION` 플래그 사용. fxc.exe CLI에서는 `/Zi /Od`에 해당. PIX for Windows 또는 RenderDoc에서 프레임 캡처 시 `set_debug_name()`으로 지정한 리소스 이름이 탐색기에 표시됨.

---

## 5. Thread Safety Design

### 5.1 vt_mutex 소유권 규약

```
vt_mutex 소유권 규약:
- 소유자: ConPtySession::Impl (생성/파괴 책임)
- 사용자: I/O Thread (write), Main Thread (resize), Render Thread (start_paint)
- 전파: DX11Renderer::start() 호출 시 참조로 전달
- 불변식: vt_mutex의 수명 > 렌더 스레드의 수명
           (stop() join 후에만 ConPtySession 파괴 가능)
- 데드락 방지: vt_mutex 보유 중 다른 뮤텍스 잠금 금지 (단일 뮤텍스 규칙)
```

### 5.2 3-way 경합 분석 (ADR-006 Phase 3 확장)

| Thread | Operation | vt_mutex | 빈도 | 보유 시간 |
|--------|-----------|----------|------|----------|
| I/O | VtCore::write() | lock | 수백~수천/sec | ~100μs (데이터 크기 의존) |
| Main | ConPtySession::resize() | lock | 드물게 | ~50μs |
| **Render** | **start_paint()** | **lock** | **60fps (16ms)** | **~1-15μs (dirty row만 복사)** |

**최악 경합 시나리오**: I/O write(100μs)가 렌더 start_paint을 블록 → 1프레임(16ms) 버짓 대비 <1% 영향. 렌더가 I/O를 블록하는 시간은 1-15μs로 무시 가능.

**SPSC 큐 전환 기준**: 벤치마크에서 vt_mutex 경합으로 인해 연속 3프레임 이상 누락 시 재검토.

### 5.3 렌더 스레드 라이프사이클

```
create()  → D3D11 초기화, GPU 리소스 생성
start()   → std::thread 생성
            stop_flag.store(false, release)
            ┌─── render_loop ───────────────────────┐
            │ while (!stop_flag.load(relaxed))       │
            │   DWORD r = WaitForSingleObject(       │
            │       waitable, 100);  // 100ms 타임아웃│
            │   if (r == WAIT_TIMEOUT) continue;     │
            │   if (stop_flag) break;                │
            │   start_paint() → build() → Draw →     │
            │   Present1()                           │
            │   // DEVICE_REMOVED 시 복구 시퀀스 진입 │
            └────────────────────────────────────────┘
stop()    → stop_flag.store(true, release)
            // waitable object 타임아웃 100ms 이내 스레드 탈출
            thread.join()
~DX11Renderer() → report_live_objects() → COM 리소스 해제
```

**WaitForSingleObject 타임아웃 100ms**: stop() 호출 후 최대 100ms 이내 스레드 종료 보장. SetEvent 불필요.

---

## 6. Error Handling

| 상황 | 처리 | 복구 | 로그 |
|------|------|------|------|
| D3D11CreateDevice 실패 | WARP 폴백 시도 | 재실패 시 Error 반환 | LOG_E |
| CreateSwapChain 실패 | Error 반환 | 호출자가 종료 결정 | LOG_E |
| 셰이더 컴파일 실패 | ID3DBlob 에러 메시지 | Error 반환 | LOG_E |
| 아틀라스 가득 참 | 2x grow + CopyResource | max_size 초과 시 LOG_W + □ 글리프 | LOG_W |
| MapCharacters 실패 | default 글리프(□) 표시 | 자동 복구 | LOG_W |
| Present DEVICE_REMOVED | Section 4.4.5 복구 시퀀스 | 전체 리소스 재초기화 | LOG_E |
| ResizeBuffers 실패 | 기존 크기 유지 | 다음 리사이즈에서 재시도 | LOG_W |

---

## 7. Debug Infrastructure (FR-12)

### 7.1 D3D11 Debug Layer

```cpp
#if defined(_DEBUG) || defined(DEBUG)
    creation_flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

void set_debug_name(ID3D11DeviceChild* obj, const char* name) {
    #if defined(_DEBUG)
    obj->SetPrivateData(WKPDID_D3DDebugObjectName, (UINT)strlen(name), name);
    #endif
}

void DX11Renderer::report_live_objects() {
    #if defined(_DEBUG)
    ComPtr<ID3D11Debug> debug;
    impl_->device->QueryInterface(IID_PPV_ARGS(&debug));
    if (debug) debug->ReportLiveDeviceObjects(D3D11_RLDO_DETAIL);
    #endif
}
```

**셰이더 디버깅 도구**: PIX for Windows 또는 RenderDoc에서 프레임을 캡처하면, `set_debug_name()`으로 지정한 이름("InstanceBuffer", "GlyphAtlasSRV" 등)이 리소스 탐색기에 표시된다. Debug 빌드에서 D3DCompileFromFile에 `D3DCOMPILE_DEBUG` 플래그를 전달하면 셰이더 소스 레벨 디버깅이 가능하다.

### 7.2 렌더 성능 계측

```cpp
struct FrameStats {
    uint64_t frame_count = 0;
    float    last_frame_ms = 0;
    float    avg_frame_ms = 0;
    uint32_t instance_count = 0;
    uint32_t atlas_glyph_count = 0;
    uint32_t present_skip_count = 0;  // dirty 없어서 생략된 횟수
    uint32_t mutex_contention_count = 0;  // TryLock 실패 횟수
};
```

---

## 8. CMake Integration

```cmake
# ─── Common library ───
add_library(ghostwin_common INTERFACE)
target_include_directories(ghostwin_common INTERFACE src/common)

# ─── stb_rect_pack (header-only) ───
add_library(stb_rect_pack INTERFACE)
target_include_directories(stb_rect_pack INTERFACE third_party)

# ─── Renderer library ───
add_library(renderer STATIC
    src/renderer/dx11_renderer.cpp
    src/renderer/quad_builder.cpp
    src/renderer/glyph_atlas.cpp
    src/renderer/render_state.cpp
    src/renderer/terminal_window.cpp
)
target_include_directories(renderer PUBLIC src/renderer)
target_link_libraries(renderer PUBLIC
    vt_core ghostwin_common stb_rect_pack
    d3d11 dxgi d3dcompiler dwrite d2d1
)

# ─── HLSL shader compilation ───
find_program(FXC fxc PATHS
    "C:/Program Files (x86)/Windows Kits/10/bin/${CMAKE_SYSTEM_VERSION}/x64")
if(FXC)
    set(SHADER_FLAGS "")
    if(CMAKE_BUILD_TYPE STREQUAL "Debug")
        set(SHADER_FLAGS /Zi /Od)  # 디버그 심볼 + 최적화 비활성
    else()
        set(SHADER_FLAGS /O2)
    endif()

    add_custom_command(
        OUTPUT ${CMAKE_BINARY_DIR}/shader_vs.cso
        COMMAND ${FXC} /T vs_4_0 ${SHADER_FLAGS}
            /Fo ${CMAKE_BINARY_DIR}/shader_vs.cso
            ${CMAKE_SOURCE_DIR}/src/renderer/shader_vs.hlsl
        DEPENDS src/renderer/shader_vs.hlsl
        COMMENT "Compiling vertex shader")
    add_custom_command(
        OUTPUT ${CMAKE_BINARY_DIR}/shader_ps.cso
        COMMAND ${FXC} /T ps_4_0 ${SHADER_FLAGS}
            /Fo ${CMAKE_BINARY_DIR}/shader_ps.cso
            ${CMAKE_SOURCE_DIR}/src/renderer/shader_ps.hlsl
        DEPENDS src/renderer/shader_ps.hlsl
        COMMENT "Compiling pixel shader")
    add_custom_target(compile_shaders ALL DEPENDS
        ${CMAKE_BINARY_DIR}/shader_vs.cso
        ${CMAKE_BINARY_DIR}/shader_ps.cso)
    add_dependencies(renderer compile_shaders)
endif()

# ─── Main executable (Phase 3 PoC) ───
add_executable(ghostwin_terminal src/main.cpp)
target_link_libraries(ghostwin_terminal PRIVATE renderer conpty)
add_dependencies(ghostwin_terminal copy_ghostty_dll)

# ─── Tests ───
add_executable(dx11_render_test tests/dx11_render_test.cpp)
target_link_libraries(dx11_render_test PRIVATE renderer conpty)
add_dependencies(dx11_render_test copy_ghostty_dll)

add_executable(quad_builder_test tests/quad_builder_test.cpp)
target_link_libraries(quad_builder_test PRIVATE renderer)

add_executable(render_state_test tests/render_state_test.cpp)
target_link_libraries(render_state_test PRIVATE renderer vt_core)
add_dependencies(render_state_test copy_ghostty_dll)
```

---

## 9. Implementation Order

| Step | Module | Files | FR | 검증 방법 |
|------|--------|-------|----|----------|
| **S1** | common 인프라 | `error.h`, `log.h`, `render_constants.h` | -- | 빌드 확인 |
| **S2** | vt_bridge 확장 (타입 안전 핸들) | `vt_bridge.h/c` | FR-01 | 단위 테스트: 행/셀 반복, 색상 추출 |
| **S3** | VtCore C++ 래퍼 | `vt_core.h/cpp` | FR-01 | for_each_row 콜백으로 셀 데이터 출력 |
| **S4** | stb_rect_pack 통합 | `third_party/` | FR-04 | 빌드 확인 |
| **S5** | D3D11 디바이스+스왑체인 | `dx11_renderer.cpp` | FR-02 | HWND에 단색 클리어 (WS_EX_NOREDIRECTIONBITMAP 확인) |
| **S6** | HLSL 셰이더 + CMake fxc | `shader_vs/ps.hlsl` | FR-03 | 하드코딩 쿼드 1개 렌더 |
| **S7** | 글리프 아틀라스 (2-tier 캐시) | `glyph_atlas.cpp` | FR-04 | ASCII + 한글 글리프 래스터화 확인 |
| **S8** | render_state (flat buffer + bitset) | `render_state.cpp` | FR-06 | start_paint dirty-row-only 복사 정상 동작 |
| **S9** | QuadBuilder | `quad_builder.cpp` | FR-05 | 단위 테스트: CellData -> QuadInstance 변환 |
| **S10** | 렌더 스레드 루프 | `dx11_renderer.cpp` | FR-06,07 | 60fps 프레임 카운터 표시 |
| **S11** | 셀 -> QuadInstance 통합 | `dx11_renderer.cpp` | FR-05,08 | VtCore 데이터로 실제 텍스트 렌더 |
| **S12** | 커서 렌더링 + 블링킹 | `dx11_renderer.cpp` + `terminal_window.cpp` | FR-09 | Block/Bar/Underline 전환 + 블링크 확인 |
| **S13** | Win32 창 + 키 입력 | `terminal_window.cpp` | FR-11 | echo, dir 등 셸 명령 실행 |
| **S14** | 리사이즈 | `terminal_window.cpp` | FR-10 | 창 크기 변경 후 정상 렌더 |
| **S15** | Debug Layer + 성능 계측 | `dx11_renderer.cpp` | FR-12 | ReportLiveDeviceObjects 클린 |
| **S16** | 통합 테스트 | `dx11_render_test.cpp` | All | Plan 4.1 DoD 7항목 + QC 5항목 |

---

## 10. Testing Strategy

### 10.1 수동 검증 체크리스트 (Plan 4.1 + 4.2 완전 매핑)

| # | 검증 항목 | 방법 | 성공 기준 | Plan 매핑 |
|---|----------|------|----------|----------|
| T1 | cmd.exe 출력 실시간 렌더링 | `dir`, `tree`, `type` 명령 실행 | 문자 깨짐 없이 즉시 표시 | DoD-1 |
| T2 | 키보드 입력으로 셸 대화 | echo, cd, dir 등 | 입력/출력 정상 | DoD-2 |
| T3 | ANSI 색상 16/256/TrueColor | VT 시퀀스 출력 테스트 | 정확한 색상 표시 | DoD-3 |
| T4 | 커서 위치/블링킹 | 텍스트 입력 시 커서 관찰 | 올바른 위치, 블링킹 동작 | DoD-4 |
| T5 | 유휴 시 GPU 사용률 | 작업 관리자 GPU 탭 | ~0% | DoD-5 |
| T6 | 리사이즈 | 창 크기 변경 | 화면 정상 갱신, 디바운스 적용 | DoD-6 |
| T7 | Phase 2 테스트 유지 | `conpty_integration_test` 실행 | 8/8 PASS | DoD-7 |
| **T8** | **한글/CJK 글리프 렌더링** | `echo 가나다라`, `Write-Host "한글 테스트"`, 한자/일본어 | **CJK 문자 정상 표시, 전각 2셀 폭** | **QC-4** |
| T9 | D3D11 Debug Layer 클린 | Debug 빌드 실행 + 종료 | WARNING/ERROR 0 | QC-1 |
| T10 | ReportLiveDeviceObjects | Debug 빌드 정상 종료 | 누수 0 | QC-2 |
| T11 | data race 없음 | 대용량 출력 + 리사이즈 동시 | 크래시/깨짐 없음 | QC-3 |
| T12 | MSVC Debug/Release 빌드 | 양쪽 빌드 성공 | 워닝 0 | QC-5 |

### 10.2 자동 검증

| 테스트 | 대상 | GPU 필요 | 검증 |
|--------|------|---------|------|
| vt_bridge_cell_test | S2 | No | 행/셀 반복자, 색상 추출, 타입 안전 핸들 |
| quad_builder_test | S9 | No (WARP) | CellData -> QuadInstance 변환 정합성 |
| render_state_test | S8 | No | start_paint dirty-row-only 스냅샷 정합성 |
| dx11_init_test | S5 | Yes (WARP 폴백) | D3D11 디바이스 + 스왑체인 생성 |
| atlas_test | S7 | Yes (WARP 폴백) | 글리프 래스터화 + 2-tier 캐시 |

**WARP 테스트 전략**: GPU 없는 CI 환경에서 `D3D_DRIVER_TYPE_WARP`로 소프트웨어 렌더링 사용. 기능 정합성 검증 가능 (성능 테스트 제외).

### 10.3 성능 측정

| 지표 | 측정 도구 | 목표 |
|------|----------|------|
| 프레임 시간 | FrameStats.last_frame_ms | < 16ms |
| 입력 레이턴시 | 키 WM_CHAR -> Present1 시간 측정 | < 32ms (2프레임) |
| vt_mutex 경합 | FrameStats.mutex_contention_count | < 1% |
| start_paint 시간 | 내부 타이머 | < 50μs (dirty row 복사) |
| GPU 메모리 | D3D11 Debug Layer | 아틀라스 < 128MB |

---

## 11. Phase 4 Migration Notes

Phase 4 (WinUI3 통합) 전환 시 필요한 변경:

| 항목 | Phase 3 (현재) | Phase 4 (미래) | 변경점 |
|------|---------------|---------------|--------|
| 창 | Win32 HWND + WS_EX_NOREDIRECTIONBITMAP | WinUI3 Window | terminal_window 교체 |
| 스왑체인 | CreateSwapChainForHwnd | CreateSwapChainForComposition | DX11Renderer Impl 내부 변경 |
| 렌더 스레드 | std::thread | ThreadPool::RunAsync | 런타임 전환 |
| 키 입력 | WM_CHAR/WM_KEYDOWN | CoreIndependentInputSource | 입력 계층 교체 |
| 아틀라스 | R8_UNORM 그레이스케일 | R8G8B8A8_UNORM ClearType (dwrite-hlsl MIT 재사용) | 포맷 + 셰이더 |
| DPI | WM_DPICHANGED | CompositionScaleChanged | DPI 처리 교체 |
| IME | 없음 | TSF ITfContextOwner | 신규 모듈 |
| **디바이스 공유** | **단일 D3D11 디바이스 (Phase 3 단일 창)** | **멀티 탭/pane이 단일 ID3D11Device 공유** | **디바이스 팩토리 패턴 도입** |

**Phase 3 -> Phase 4 보존 인터페이스**: `render_state.h`, `glyph_atlas.h`, `quad_builder.h`의 공개 인터페이스는 변경 없이 유지. D3D11 리소스 타입을 공개 헤더에 노출하지 않는다 (Pimpl).

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-29 | Initial design from Plan + research | Solit |
| 0.2 | 2026-03-29 | 28건 에이전트 리뷰 반영 (타입 안전 핸들, flat buffer, dirty-row-only, 2-tier 캐시, 등) | Solit |
| **0.3** | **2026-03-29** | **사실 검증 수정: IDXGISwapChain2 waitable object 플래그 추가, D3DCompileFromFile 플래그 정정(D3DCOMPILE_DEBUG), 글리프 샘플러 linear→point, WS_EX_NOREDIRECTIONBITMAP 필수→권장, CellData 32B 확정, ASCII 95% 한국어 주의사항, 2-tier 캐시 wide 플래그 포함, update_from_vtcore 호출 시점 명확화, MapCharacters 블록 캐싱 주의사항, Device Removed Phase 3 범위 축소, HLSL PSInput 공유 주석** | **Solit** |
