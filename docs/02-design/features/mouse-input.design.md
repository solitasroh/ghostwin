# mouse-input Design Document (v1.0)

> **Summary**: 마우스 클릭/모션/스크롤 — per-session Encoder 캐시 + WndProc 동기 P/Invoke
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft (v1.0 — 5개 터미널 벤치마킹 반영)
> **Planning Doc**: [mouse-input.plan.md](../../01-plan/features/mouse-input.plan.md)
> **PRD**: [mouse-input.prd.md](../../00-pm/mouse-input.prd.md)
> **Benchmarking**: [mouse-input-benchmarking.md](../../00-research/mouse-input-benchmarking.md)

---

## 0. Constraints & Locks

| ID | Constraint | Rationale |
|----|-----------|-----------|
| C-1 | `ghostty_mouse_encoder_*` C API 사용 필수 | VT 인코딩 자체 구현 금지. 17개 심볼 export 확인 |
| C-2 | `ghostty_surface_mouse_*` 사용 불가 | `-Demit-lib-vt=true` 빌드에 Surface 레이어 미포함 (0개 심볼) |
| C-3 | WndProc(Win32 message) 방식 유지 | HwndHost 기반 child HWND |
| C-4 | `gw_session_write` 패턴 준수 | GW_TRY/CATCH, session_mgr→get, conpty→send_input |
| C-5 | DefWindowProc 전달 유지 | OS 기본 동작 보존 |
| C-6 | Dispatcher.BeginInvoke 금지 (마우스 경로) | 5개 터미널 공통: 이벤트 스레드 동기 처리 |

---

## 1. Overview

### 1.1 Design Goals

5개 터미널 벤치마킹에서 확인된 공통 패턴 4가지를 적용하여, v0.1의 성능 문제를 원천 해결:

| # | 패턴 | v0.1 위반 | v1.0 적용 |
|:-:|-------|-----------|-----------|
| 1 | 힙 할당 최소 | Encoder/Event 매 호출 new/free | per-session 캐시 |
| 2 | Cell 중복 제거 | 없음 | `track_last_cell = true` |
| 3 | 동기 처리 | Dispatcher.BeginInvoke | WndProc → P/Invoke 직접 |
| 4 | 스크롤 누적 | 미구현 | pending_scroll + cell_height |

---

## 2. Architecture

### 2.1 데이터 흐름

```
[TerminalHostControl child HWND]
  ↓ WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_MOUSEWHEEL
[WndProc: lParam(좌표) + msg(버튼) + wParam(modifier) 추출]
  ↓ P/Invoke 직접 호출 (Dispatcher 없음, C-6)
[gw_session_write_mouse (C++ Engine)]
  ↓ per-session Encoder 캐시에서 조회
[ghostty_mouse_encoder_setopt_from_terminal (모드/포맷 동기화)]
  ↓
[ghostty_mouse_event_set_* → ghostty_mouse_encoder_encode]
  ↓ VT 시퀀스 바이트 (스택 128B 버퍼)
[conpty→send_input → 자식 프로세스 stdin]
```

### 2.2 v0.1 vs v1.0 비교

```
v0.1 (버벅임):
  WndProc → Dispatcher.BeginInvoke → engine_new → encode → engine_free
  [4단계, 힙 2회, 스레드 홉 1회]

v1.0 (동기):
  WndProc → P/Invoke 직접 → 캐시된 encoder.encode
  [2단계, 힙 0회, 스레드 홉 0회]
```

---

## 3. Detailed Design

### 3.1 C++ Engine: per-session Encoder 캐시

#### SessionState 확장 (`session_manager.h`)

```cpp
struct SessionState {
    // 기존 필드...
    std::unique_ptr<ConPtySession> conpty;

    // M-10a: per-session mouse encoder cache
    GhosttyMouseEncoder mouse_encoder = nullptr;
    GhosttyMouseEvent   mouse_event   = nullptr;

    ~SessionState() {
        if (mouse_event)   ghostty_mouse_event_free(mouse_event);
        if (mouse_encoder) ghostty_mouse_encoder_free(mouse_encoder);
    }
};
```

#### Session 생성 시 초기화

```cpp
// session_manager.cpp — createSession 내부
ghostty_mouse_encoder_new(nullptr, &state->mouse_encoder);
ghostty_mouse_event_new(nullptr, &state->mouse_event);

// track_last_cell 활성화 (ghostty 내장 cell 중복 제거)
bool track = true;
ghostty_mouse_encoder_setopt(state->mouse_encoder,
    GHOSTTY_MOUSE_ENCODER_OPT_TRACK_LAST_CELL, &track);
```

#### gw_session_write_mouse (`ghostwin_engine.cpp`)

```cpp
GWAPI int gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                  float x_px, float y_px,
                                  uint32_t button, uint32_t action,
                                  uint32_t mods) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        auto* session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;

        auto* vt = session->conpty->vt_core();
        if (!vt) return GW_ERR_INVALID;

        // 1. 터미널 상태 동기화 (모드/포맷이 변할 수 있음)
        ghostty_mouse_encoder_setopt_from_terminal(
            session->mouse_encoder,
            (GhosttyTerminal)vt->raw_terminal());

        // 2. Surface 크기 설정 (pixel→cell 변환용)
        auto* surf = eng->surface_mgr->find_by_session(id);
        if (surf) {
            GhosttyMouseEncoderSize sz{};
            sz.size = sizeof(sz);
            sz.screen_width = surf->width_px;
            sz.screen_height = surf->height_px;
            sz.cell_width = vt->cell_width();
            sz.cell_height = vt->cell_height();
            ghostty_mouse_encoder_setopt(session->mouse_encoder,
                GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &sz);
        }

        // 3. Event 설정 (캐시된 인스턴스 재사용)
        ghostty_mouse_event_set_action(session->mouse_event,
            (GhosttyMouseAction)action);
        if (button > 0)
            ghostty_mouse_event_set_button(session->mouse_event,
                (GhosttyMouseButton)button);
        else
            ghostty_mouse_event_clear_button(session->mouse_event);
        ghostty_mouse_event_set_position(session->mouse_event,
            GhosttyMousePosition{x_px, y_px});
        ghostty_mouse_event_set_mods(session->mouse_event,
            (GhosttyMods)mods);

        // 4. Encode (스택 버퍼, 힙 할당 0)
        char buf[128];
        size_t written = 0;
        ghostty_mouse_encoder_encode(session->mouse_encoder,
            session->mouse_event, buf, sizeof(buf), &written);

        // 5. Send (written==0이면 cell 중복 또는 모드 비활성)
        if (written > 0)
            session->conpty->send_input(
                {(const uint8_t*)buf, (uint32_t)written});

        return GW_OK;
    GW_CATCH_INT
}
```

**성능 분석**: Encoder/Event는 session 수명과 동일. `setopt_from_terminal`은 터미널 flags 읽기만 하므로 가볍다. `encode`는 스택 128B에 쓰기. **힙 할당 0**.

### 3.2 C# Interop

#### IEngineService.cs

```csharp
/// <summary>Forward mouse event to ConPTY via ghostty VT encoding.</summary>
/// <param name="sessionId">Target session</param>
/// <param name="xPx">Surface-space pixel X (child HWND lParam)</param>
/// <param name="yPx">Surface-space pixel Y</param>
/// <param name="button">0=none, 1=LEFT, 2=RIGHT, 3=MIDDLE, 4=WHEEL_UP, 5=WHEEL_DOWN</param>
/// <param name="action">0=PRESS, 1=RELEASE, 2=MOTION</param>
/// <param name="mods">Bitfield: 1=SHIFT, 2=CTRL, 4=ALT</param>
int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                    uint button, uint action, uint mods);
```

#### NativeEngine.cs

```csharp
[LibraryImport(Dll)]
internal static partial int gw_session_write_mouse(
    nint engine, uint id,
    float xPx, float yPx,
    uint button, uint action, uint mods);
```

#### EngineService.cs

```csharp
public int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                           uint button, uint action, uint mods)
    => NativeEngine.gw_session_write_mouse(
        _engine, sessionId, xPx, yPx, button, action, mods);
```

### 3.3 WPF: TerminalHostControl WndProc 확장

#### 추가 상수

```csharp
const uint WM_MOUSEMOVE    = 0x0200;
const uint WM_LBUTTONUP    = 0x0202;
const uint WM_RBUTTONUP    = 0x0205;
const uint WM_MBUTTONUP    = 0x0208;
const uint WM_MOUSEWHEEL   = 0x020A;
const uint MK_SHIFT        = 0x0004;
const uint MK_CONTROL      = 0x0008;
const int  VK_MENU         = 0x12;
```

#### WndProc 확장 (동기 처리, Dispatcher 금지)

```csharp
private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
{
    // --- Pane focus click (기존, Dispatcher 유지 — UI 작업이라 필요) ---
    if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
    {
        if (_hostsByHwnd.TryGetValue(hwnd, out var host))
        {
            var paneId = host.PaneId;
            var hostHwnd = hwnd;
            host.Dispatcher.BeginInvoke(() =>
            {
                if (_hostsByHwnd.TryGetValue(hostHwnd, out var live) &&
                    live._childHwnd != IntPtr.Zero)
                    live.PaneClicked?.Invoke(live, new PaneClickedEventArgs(paneId));
            });
        }
    }

    // --- Mouse input → Engine (동기, Dispatcher 없음, C-6) ---
    if (IsMouseMsg(msg))
    {
        if (_hostsByHwnd.TryGetValue(hwnd, out var host) && host._engine != null)
        {
            short x = (short)(lParam & 0xFFFF);
            short y = (short)((lParam >> 16) & 0xFFFF);
            uint button = ButtonFromMsg(msg);
            uint action = ActionFromMsg(msg);
            uint mods = ModsFromWParam(wParam);

            // 동기 P/Invoke — WndProc 스레드에서 직접 호출
            // ghostty encoder가 thread-safe (per-session 독립 인스턴스)
            host._engine.WriteMouseEvent(
                host.SessionId, (float)x, (float)y,
                button, action, mods);
        }
    }

    return DefWindowProc(hwnd, msg, wParam, lParam);
}
```

**핵심 변경**: `host._engine` 필드 추가 필요. `TerminalHostControl`에 `IEngineService` 참조를 주입하여 WndProc에서 직접 호출.

#### 헬퍼 함수

```csharp
static bool IsMouseMsg(uint msg)
    => msg is WM_LBUTTONDOWN or WM_LBUTTONUP
           or WM_RBUTTONDOWN or WM_RBUTTONUP
           or WM_MBUTTONDOWN or WM_MBUTTONUP
           or WM_MOUSEMOVE;

static uint ButtonFromMsg(uint msg) => msg switch
{
    WM_LBUTTONDOWN or WM_LBUTTONUP => 1,
    WM_RBUTTONDOWN or WM_RBUTTONUP => 2,
    WM_MBUTTONDOWN or WM_MBUTTONUP => 3,
    _ => 0,
};

static uint ActionFromMsg(uint msg) => msg switch
{
    WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN => 0,  // PRESS
    WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP => 1,        // RELEASE
    _ => 2,  // MOTION
};

static uint ModsFromWParam(nint wParam)
{
    uint w = (uint)(wParam & 0xFFFF);
    uint mods = 0;
    if ((w & MK_SHIFT) != 0) mods |= 1;
    if ((w & MK_CONTROL) != 0) mods |= 2;
    if ((GetKeyState(VK_MENU) & 0x8000) != 0) mods |= 4;
    return mods;
}
```

### 3.4 WM_MOUSEWHEEL 처리 (M-10b — 벤치마킹 반영)

#### 3.4.1 WM_MOUSEWHEEL 전달 문제

WM_MOUSEWHEEL은 child HWND가 아닌 **focus window에 전달**됨 (Win32 표준).
해결: `IsMouseMsg`에 WM_MOUSEWHEEL 추가 + child WndProc에서 수신 시도. 미수신이면 MainWindow에서 forwarding.

#### 3.4.2 스크롤 처리 전략 (2단계 분기)

5개 터미널 벤치마킹 결과, 스크롤은 **마우스 모드 ON/OFF로 분기**:

```
WM_MOUSEWHEEL (delta = HIWORD(wParam))
  ↓
gw_session_write_mouse(id, x, y, button=4or5, action=PRESS, mods)
  ↓ ghostty encoder 내부
  ├─ mouse_event != none → VT 시퀀스 (button 64/65) → send_input
  │   (written > 0 → return GW_OK)
  └─ mouse_event == none → 인코딩 안 함 (written == 0)
      → 반환값으로 "비활성" 판별 → scrollback viewport 이동
```

#### 3.4.3 비활성 모드 scrollback

`gw_session_write_mouse`의 반환값 확장 또는 별도 API로 처리:

**Option 1 (반환값 활용)**: `gw_session_write_mouse`가 `written > 0`이면 GW_OK, `written == 0`이면 GW_MOUSE_NOT_REPORTED(새 상수) 반환. WPF에서 반환값 확인 후 scrollback.

**Option 2 (별도 API)**: `gw_scroll_viewport(engine, sessionId, deltaRows)` 추가. WPF에서 마우스 모드를 `gw_mouse_mode` 쿼리로 확인 후 분기.

**권장: Option 1** — API 1개로 충분. 엔진 내부에서 모드 판별 완료.

#### 3.4.4 WndProc 스크롤 코드

```csharp
// WndProc 내부 — WM_MOUSEWHEEL 처리
if (msg == WM_MOUSEWHEEL)
{
    if (_hostsByHwnd.TryGetValue(hwnd, out var host) && host._engine != null)
    {
        short delta = (short)((wParam >> 16) & 0xFFFF);  // HIWORD
        uint mods = ModsFromWParam(wParam);

        // WM_MOUSEWHEEL의 lParam은 screen 좌표 (child 좌표 아님)
        // 현재 마우스 위치를 child 좌표로 변환
        var pt = new POINT((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
        ScreenToClient(hwnd, ref pt);

        uint button = delta > 0 ? 4u : 5u;  // 4=WHEEL_UP, 5=WHEEL_DOWN

        int result = host._engine.WriteMouseEvent(
            host.SessionId, (float)pt.x, (float)pt.y,
            button, 0 /* PRESS */, mods);

        // Option 1: 반환값으로 비활성 모드 판별
        if (result == GW_MOUSE_NOT_REPORTED)
        {
            // scrollback viewport 이동
            int lines = delta > 0 ? -3 : 3;  // Windows 기본 3줄
            host._engine.ScrollViewport(host.SessionId, lines);
        }
    }
}
```

#### 3.4.5 C++ Engine 변경

`gw_session_write_mouse` 반환값 확장:
```cpp
// written > 0 → GW_OK (VT 인코딩됨)
// written == 0 → GW_MOUSE_NOT_REPORTED (모드 비활성 또는 중복)
if (written > 0) {
    session->conpty->send_input({(const uint8_t*)buf, (uint32_t)written});
    return GW_OK;
}
return GW_MOUSE_NOT_REPORTED;  // 새 상수 = 2
```

`gw_scroll_viewport` 추가:
```cpp
GWAPI int gw_scroll_viewport(GwEngine engine, GwSessionId id, int32_t delta_rows);
// 구현: session->conpty->vt_core().scrollViewport(delta_rows)
```

#### 3.4.6 추가 필요 P/Invoke

```csharp
// NativeEngine.cs
[LibraryImport(Dll)]
internal static partial int gw_scroll_viewport(nint engine, uint id, int deltaRows);

// IEngineService.cs
int ScrollViewport(uint sessionId, int deltaRows);

// EngineService.cs
public int ScrollViewport(uint sessionId, int deltaRows)
    => NativeEngine.gw_scroll_viewport(_engine, sessionId, deltaRows);
```

#### 3.4.7 ScreenToClient P/Invoke

```csharp
[DllImport("user32.dll")]
private static extern bool ScreenToClient(nint hwnd, ref POINT point);

[StructLayout(LayoutKind.Sequential)]
private struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }
```

### 3.5 TerminalHostControl에 IEngineService 주입

```csharp
// TerminalHostControl.cs — 필드 추가
internal IEngineService? _engine;

// PaneContainerControl.cs — BuildElement에서 주입
var host = new TerminalHostControl { PaneId = paneId, SessionId = sessionId };
host._engine = Ioc.Default.GetService<IEngineService>();
```

---

## 4. Implementation Order

```
M-10a: 마우스 클릭 + 모션 (~1주)
  T-1: session_manager.h/cpp — SessionState에 mouse_encoder/event 추가
       - 생성 시 new + track_last_cell=true
       - 소멸 시 free
  T-2: ghostwin_engine.h/cpp — gw_session_write_mouse 구현
       - setopt_from_terminal + setopt(SIZE) + set_*/encode + send_input
       - extern "C" ghostty 헤더 래핑 (LNK2019 방지)
  T-3: NativeEngine.cs + EngineService.cs + IEngineService.cs
  T-4: TerminalHostControl.cs — WndProc 확장 + _engine 필드
  T-5: PaneContainerControl.cs — host._engine 주입
  T-6: 빌드 + vim :set mouse=a 검증 + 성능 확인

M-10b: 스크롤 (~3일)
  T-7: WM_MOUSEWHEEL 처리 (button 4/5)
  T-8: C++ 엔진 — 스크롤 누적 (비활성 모드 분기)
  T-9: 검증
```

---

## 5. Affected Files

| File | Change | M-10a | M-10b |
|------|--------|:-----:|:-----:|
| `src/engine-api/ghostwin_engine.h` | `gw_session_write_mouse` 선언 | O | — |
| `src/engine-api/ghostwin_engine.cpp` | 구현 + extern "C" ghostty include | O | O |
| `src/engine-api/session_manager.h` | SessionState에 encoder/event 멤버 | O | — |
| `src/engine-api/session_manager.cpp` | 생성/소멸 시 new/free | O | — |
| `src/GhostWin.Core/Interfaces/IEngineService.cs` | `WriteMouseEvent` | O | — |
| `src/GhostWin.Interop/NativeEngine.cs` | P/Invoke | O | — |
| `src/GhostWin.Interop/EngineService.cs` | 구현 | O | — |
| `src/GhostWin.App/Controls/TerminalHostControl.cs` | WndProc + _engine + 상수 | O | O |
| `src/GhostWin.App/Controls/PaneContainerControl.cs` | host._engine 주입 | O | — |

---

## 6. Test Plan

| ID | Case | Method | Sub-MS |
|----|------|--------|:------:|
| TC-1 | vim `:set mouse=a` + 좌클릭 커서 이동 | 수동 | M-10a |
| TC-2 | vim 비주얼 모드 마우스 드래그 | 수동 | M-10a |
| TC-5 | vim 마우스 스크롤 | 수동 | M-10b |
| TC-6 | 비활성 모드 scrollback 마우스 휠 | 수동 | M-10b |
| TC-7 | 다중 pane 마우스 독립 동작 | 수동 | M-10a |
| TC-8 | Shift+클릭 bypass | 수동 | M-10a |
| TC-P | 성능: 마우스 빠르게 움직여도 버벅임 없음 | 수동 | M-10a |

---

## 7. Decision Record

| ID | Decision | Rationale | 벤치마킹 근거 |
|----|----------|-----------|---------------|
| D-1 | per-session Encoder 캐시 (Option A) | Surface API 미포함, Encoder API만 가용 | cmux: Surface API, GhostWin: encoder only |
| D-2 | WndProc → P/Invoke 직접 호출 | 5/5 터미널 동기 처리 | WT: UI 동기, ghostty: 콜백 동기, cmux: 메인 동기 |
| D-3 | `track_last_cell = true` | 5/5 터미널 cell 중복 제거 | ghostty: `last_cell`, WT: `sameCoord`, Alacritty: `old_point != point` |
| D-4 | `setopt_from_terminal` 매 호출 | 터미널 mode/format 동적 변경 대응 | ghostty: `Options::fromTerminal`, WT: `IsTrackingMouseInput` |
| D-5 | 스크롤은 button 4/5로 전달 | ghostty scroll→mouseReport 패턴 | `Surface.zig:3518` `.four`/`.five` |
| D-6 | PaneClicked Dispatcher 유지 | UI 작업(focus 변경)은 UI 스레드 필요 | 마우스 VT 전달만 동기, UI는 비동기 허용 |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | 초안 |
| 1.0 | 2026-04-10 | 5개 터미널 벤치마킹 반영. per-session 캐시, 동기 P/Invoke, cell dedup, 스크롤 누적 |
