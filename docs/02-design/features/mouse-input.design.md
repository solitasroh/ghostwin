# mouse-input Design Document

> **Summary**: 터미널 마우스 클릭/스크롤/선택 — WndProc → C++ Engine (ghostty mouse_encode) → ConPTY
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft
> **Planning Doc**: [mouse-input.plan.md](../../01-plan/features/mouse-input.plan.md)
> **PRD**: [mouse-input.prd.md](../../00-pm/mouse-input.prd.md)

---

## 0. Constraints & Locks

| ID | Constraint | Rationale |
|----|-----------|-----------|
| C-1 | ghostty mouse C API (`ghostty_mouse_event_*`, `ghostty_mouse_encoder_*`) 사용 필수 | 5 포맷 + 4 모드 인코딩 정확도 보장. 자체 구현 금지 |
| C-2 | WndProc(Win32 message) 방식 유지 | HwndHost 기반 → WPF 라우팅 이벤트 불가. 기존 패턴 확장 |
| C-3 | `gw_session_write` 패턴 준수 | GW_TRY/GW_CATCH_INT, session_mgr→get, conpty→send_input |
| C-4 | per-pane 독립 처리 | child HWND가 독립 WndProc → lParam 좌표는 pane-local |

---

## 1. Overview

### 1.1 Design Goals

1. VT 마우스 인코딩: ghostty C API로 정확한 VT 시퀀스 생성 (5 포맷 × 4 모드)
2. 최소 레이어: WndProc → 1개 C API 호출 → ConPTY (중간 계층 없음)
3. 기존 코드 영향 최소화: `TerminalHostControl.WndProc` 확장, 새 파일 없음

### 1.2 Design Principles

- ghostty upstream 예제(`c-vt-encode-mouse`) 패턴 정확히 따르기
- `gw_session_write` 와 동일한 에러 처리/세션 조회 패턴
- DefWindowProc 전달 유지 (OS 기본 동작 보존)

---

## 2. Architecture

### 2.1 데이터 흐름 (M-10a: 클릭 + 모션)

```
[TerminalHostControl child HWND]
  ↓ WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_MOUSEWHEEL
[WndProc: 좌표(lParam) + 버튼(msg) + modifier(wParam) 추출]
  ↓ Dispatcher.BeginInvoke
[MouseInputRequested 이벤트 → PaneContainerControl]
  ↓
[IEngineService.WriteMouseEvent(sessionId, x, y, button, action, mods)]
  ↓ P/Invoke
[gw_session_write_mouse → ghostty mouse_event + mouse_encode]
  ↓ VT 시퀀스 바이트
[conpty→send_input → 자식 프로세스 stdin]
```

### 2.2 계층별 변경 요약

| Layer | File | 변경 내용 |
|-------|------|-----------|
| **C++ Engine** | `ghostwin_engine.h` | `gw_session_write_mouse` 선언 |
| **C++ Engine** | `ghostwin_engine.cpp` | 구현: mouse_event → mouse_encode → send_input |
| **C# Interop** | `NativeEngine.cs` | P/Invoke 1개 |
| **C# Interop** | `EngineService.cs` | `WriteMouseEvent` 구현 |
| **C# Core** | `IEngineService.cs` | 인터페이스 메서드 1개 |
| **WPF** | `TerminalHostControl.cs` | WndProc 마우스 메시지 캡처 확장 + 이벤트 |
| **WPF** | `PaneContainerControl.cs` | MouseInputRequested 구독 → engine 호출 |

---

## 3. Detailed Design

### 3.1 C++ Engine API

#### ghostwin_engine.h 추가

```c
// ── Mouse input ──

/// Forward mouse event to the session's ConPTY via ghostty VT encoding.
/// Coordinates are surface-space pixels (child HWND client area).
/// button: 0=none(motion), 1=LEFT, 2=RIGHT, 3=MIDDLE
/// action: 0=PRESS, 1=RELEASE, 2=MOTION
/// mods: bitfield (1=SHIFT, 2=CTRL, 4=ALT, 8=SUPER)
GWAPI int gw_session_write_mouse(GwEngine engine, GwSessionId id,
                                  float x_px, float y_px,
                                  uint32_t button, uint32_t action,
                                  uint32_t mods);
```

#### ghostwin_engine.cpp 구현

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

        // 1. Encoder 생성 + 터미널 상태 로드
        GhosttyMouseEncoder encoder = nullptr;
        ghostty_mouse_encoder_new(nullptr, &encoder);
        ghostty_mouse_encoder_setopt_from_terminal(
            encoder, (GhosttyTerminal)vt->raw_terminal());

        // 2. Size 설정 (cell 변환에 필요)
        auto& surf = /* surface lookup by session_id */;
        GhosttyMouseEncoderSize sz{};
        sz.size = sizeof(sz);
        sz.screen_width = surf.width_px;
        sz.screen_height = surf.height_px;
        sz.cell_width = vt->cell_width();
        sz.cell_height = vt->cell_height();
        ghostty_mouse_encoder_setopt(encoder,
            GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &sz);

        // 3. Event 생성 + 설정
        GhosttyMouseEvent event = nullptr;
        ghostty_mouse_event_new(nullptr, &event);
        ghostty_mouse_event_set_action(event, (GhosttyMouseAction)action);
        if (button > 0)
            ghostty_mouse_event_set_button(event, (GhosttyMouseButton)button);
        else
            ghostty_mouse_event_clear_button(event);
        ghostty_mouse_event_set_position(event, {x_px, y_px});
        ghostty_mouse_event_set_mods(event, (GhosttyMods)mods);

        // 4. Encode + send
        char buf[128];
        size_t written = 0;
        ghostty_mouse_encoder_encode(encoder, event,
                                      buf, sizeof(buf), &written);
        if (written > 0)
            session->conpty->send_input({(const uint8_t*)buf, (uint32_t)written});

        // 5. Cleanup
        ghostty_mouse_event_free(event);
        ghostty_mouse_encoder_free(encoder);
        return GW_OK;
    GW_CATCH_INT
}
```

**성능 고려**: Encoder/Event를 매 호출마다 생성/파괴. Motion 폭주 시 per-session 캐시로 최적화 가능하지만, 1차에서는 단순 구현 우선.

### 3.2 C# Interop

#### IEngineService.cs 추가

```csharp
int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                    uint button, uint action, uint mods);
```

#### NativeEngine.cs 추가

```csharp
[LibraryImport(Dll)]
internal static partial int gw_session_write_mouse(
    nint engine, uint id,
    float xPx, float yPx,
    uint button, uint action, uint mods);
```

#### EngineService.cs 추가

```csharp
public int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                           uint button, uint action, uint mods)
    => NativeEngine.gw_session_write_mouse(
        _engine, sessionId, xPx, yPx, button, action, mods);
```

### 3.3 WPF: TerminalHostControl.WndProc 확장

#### 추가 상수

```csharp
const uint WM_MOUSEMOVE    = 0x0200;
const uint WM_LBUTTONUP    = 0x0202;
const uint WM_RBUTTONUP    = 0x0205;
const uint WM_MBUTTONUP    = 0x0208;
const uint WM_MOUSEWHEEL   = 0x020A;

const uint MK_SHIFT   = 0x0004;
const uint MK_CONTROL = 0x0008;
```

#### 이벤트 추가

```csharp
public record MouseInputEventArgs(uint SessionId, float X, float Y,
                                   uint Button, uint Action, uint Mods);
public event EventHandler<MouseInputEventArgs>? MouseInputRequested;
```

#### WndProc 확장 로직

```csharp
// 기존 PaneClicked 유지 (DOWN 메시지)
// 新: 모든 마우스 메시지를 MouseInputRequested로 전달

if (IsMouseMessage(msg))
{
    short x = (short)(lParam & 0xFFFF);
    short y = (short)((lParam >> 16) & 0xFFFF);
    uint button = ButtonFromMsg(msg);   // 0=none, 1=L, 2=R, 3=M
    uint action = ActionFromMsg(msg);   // 0=PRESS, 1=RELEASE, 2=MOTION
    uint mods = ModsFromWParam(wParam); // Shift|Ctrl|Alt

    host.Dispatcher.BeginInvoke(() =>
    {
        live.MouseInputRequested?.Invoke(live,
            new MouseInputEventArgs(live.SessionId, x, y, button, action, mods));
    });
}
```

#### Modifier 추출 (wParam)

```csharp
static uint ModsFromWParam(nint wParam)
{
    uint w = (uint)wParam;
    uint mods = 0;
    if ((w & MK_SHIFT) != 0) mods |= 1;   // SHIFT
    if ((w & MK_CONTROL) != 0) mods |= 2;  // CTRL
    // Alt: wParam에 없으므로 GetKeyState 사용
    if ((GetKeyState(VK_MENU) & 0x8000) != 0) mods |= 4; // ALT
    return mods;
}
```

### 3.4 WPF: PaneContainerControl 마우스 구독

`BuildElement`에서 TerminalHostControl 생성 시 이벤트 구독:

```csharp
host.MouseInputRequested += (_, e) =>
{
    if (_workspaces?.ActivePaneLayout is { } layout)
    {
        var engine = Ioc.Default.GetService<IEngineService>();
        engine?.WriteMouseEvent(e.SessionId, e.X, e.Y,
                                e.Button, e.Action, e.Mods);
    }
};
```

### 3.5 WM_MOUSEWHEEL 처리 (M-10b)

```csharp
if (msg == WM_MOUSEWHEEL)
{
    short delta = (short)((wParam >> 16) & 0xFFFF); // HIWORD
    int lines = delta / 120 * 3; // Windows 기본 3줄

    // 마우스 모드 활성 → VT 인코딩 (button 4=up, 5=down)
    // 마우스 모드 비활성 → scrollback viewport 이동
    // → 엔진에서 모드 판별 후 분기
}
```

스크롤은 `gw_session_write_mouse`에서 `button=4(up)/5(down)`, `action=PRESS`로 전달. 엔진 측에서 마우스 모드 비활성이면 VT 인코딩 대신 scrollback viewport 이동.

---

## 4. Implementation Order

### M-10a: 마우스 클릭 + 모션 (~1주)

```
T-1: ghostty mouse C API가 libvt 빌드에 포함되는지 검증
     → zig build 후 nm/dumpbin으로 심볼 확인

T-2: ghostwin_engine.h/cpp — gw_session_write_mouse 구현
     → ghostty example 패턴 따르기
     → VtCore에서 cell_width/height 조회 방법 확인

T-3: NativeEngine.cs + EngineService.cs + IEngineService.cs
     → P/Invoke + 인터페이스 + 구현

T-4: TerminalHostControl.WndProc 확장
     → 마우스 메시지 캡처 + MouseInputRequested 이벤트

T-5: PaneContainerControl — 이벤트 구독 + engine 호출

T-6: 빌드 + vim :set mouse=a 검증
```

### M-10b: 스크롤 (~3일)

```
T-7: WM_MOUSEWHEEL 처리 (button 4/5 인코딩)
T-8: gw_scroll_viewport API (비활성 모드 scrollback)
T-9: vim/scrollback 검증
```

### M-10c: 텍스트 선택 (~1주, 별도 Design 검토)

```
T-10: Shift bypass 판별 로직
T-11: Selection 상태 관리 (word/line/block)
T-12: Selection 시각화
```

### M-10d: 통합 검증 (~3일)

```
T-13: 다중 pane 라우팅 검증
T-14: DPI 변경 검증
T-15: vim/tmux/htop/nano smoke test
```

---

## 5. Affected Files

| File | Change Type | M-10a | M-10b | M-10c |
|------|:-----------:|:-----:|:-----:|:-----:|
| `src/engine-api/ghostwin_engine.h` | Modify | O | O | — |
| `src/engine-api/ghostwin_engine.cpp` | Modify | O | O | — |
| `src/vt-core/vt_core.h` | Modify (cell_width/height getter) | O | — | — |
| `src/GhostWin.Core/Interfaces/IEngineService.cs` | Modify | O | O | — |
| `src/GhostWin.Interop/NativeEngine.cs` | Modify | O | O | — |
| `src/GhostWin.Interop/EngineService.cs` | Modify | O | O | — |
| `src/GhostWin.App/Controls/TerminalHostControl.cs` | Modify | O | O | O |
| `src/GhostWin.App/Controls/PaneContainerControl.cs` | Modify | O | — | O |

---

## 6. Test Plan

| ID | Case | Method | Sub-MS |
|----|------|--------|:------:|
| TC-1 | vim `:set mouse=a` + 좌클릭 커서 이동 | 수동 | M-10a |
| TC-2 | vim 비주얼 모드 마우스 드래그 선택 | 수동 | M-10a |
| TC-3 | tmux 마우스 pane 클릭 전환 | 수동 | M-10a |
| TC-4 | htop 프로세스 마우스 클릭 | 수동 | M-10a |
| TC-5 | vim 마우스 스크롤 (active mode) | 수동 | M-10b |
| TC-6 | 비활성 모드 scrollback 마우스 휠 | 수동 | M-10b |
| TC-7 | 다중 pane에서 각 pane 마우스 독립 동작 | 수동 | M-10d |
| TC-8 | Shift+클릭 bypass (선택 모드 진입) | 수동 | M-10c |
| TC-9 | E2E MQ-4 (mouse focus) regression | E2E | M-10d |

---

## 7. Risk Assessment

| Risk | Severity | Mitigation | 확인 시점 |
|------|:--------:|------------|:---------:|
| ghostty mouse API가 libvt 빌드 미포함 | **HIGH** | T-1에서 nm/dumpbin 심볼 검증. 미포함 시 zig build 옵션 조사 | T-1 |
| VtCore에 cell_width/height getter 없음 | MEDIUM | `raw_terminal()` 로 ghostty terminal 직접 쿼리 또는 getter 추가 | T-2 |
| Encoder 매 호출 생성/파괴 성능 | LOW | 1차 단순 구현. Motion 폭주 시 per-session 캐시로 최적화 | M-10d |
| WM_MOUSEWHEEL은 child HWND가 아닌 포커스 윈도우에 전달 | MEDIUM | child HWND에서 SetCapture 또는 parent에서 forwarding | T-7 |

---

## 8. Decision Record

| ID | Decision | Rationale |
|----|----------|-----------|
| D-1 | 단일 API `gw_session_write_mouse` | button/action/mods를 파라미터로 전달. msg 포워딩(PRD 초안)보다 플랫폼 독립적 |
| D-2 | Encoder를 C++ 엔진에서 관리 | terminal state(mode/format)에 직접 접근 필요. C# 측에서는 불가 |
| D-3 | 좌표는 surface-space pixel 그대로 전달 | ghostty mouse_encode가 pixel→cell 변환 수행. DPI 변환 불필요 (child HWND가 이미 pixel 단위) |
| D-4 | M-10c (텍스트 선택)은 별도 상세 Design 검토 | Selection 시각화 + render buffer 읽기 경로 미정. M-10a/b 완료 후 설계 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft (M-10a/b 중심) | Claude + 노수장 |
