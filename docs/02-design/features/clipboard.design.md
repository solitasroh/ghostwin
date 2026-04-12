# Clipboard Design -- GhostWin M-10.5

> **Summary**: Ctrl+C/V 복사/붙여넣기 + C0/C1 보안 필터링 + Bracketed Paste + OSC 52 + 멀티라인/대용량 경고를 3 Phase로 구현한다.
>
> **Project**: GhostWin Terminal
> **Date**: 2026-04-11
> **Status**: Draft
> **PRD**: `docs/00-pm/clipboard.prd.md` (v1.1)
> **Plan**: `docs/01-plan/features/clipboard.plan.md`
> **Research**: `docs/00-research/research-clipboard-copy-paste.md`

---

## Executive Summary

| Perspective         | Content |
|---------------------|---------|
| **Problem**         | GhostWin은 M-10에서 텍스트 선택/DX11 하이라이트를 완성했지만 클립보드 코드가 전혀 없다. Ctrl+C는 항상 SIGINT(0x03), Ctrl+V는 무동작이므로 일상 사용이 불가능하다. |
| **Solution**        | `IClipboardService` 서비스를 `GhostWin.Services`에 신규 추가하고, `TerminalInputService`의 기존 TODO 확장점에 Ctrl+C/V 분기를 삽입한다. 보안 필터/Bracketed Paste/줄바꿈 정규화는 서비스 내부 파이프라인으로 캡슐화한다. OSC 52는 ConPTY 출력 스트림에서 직접 파싱한다 (WT 패턴). |
| **아키텍처 영향**   | 신규 서비스 1개 (`ClipboardService`), 인터페이스 1개 (`IClipboardService`), 모델 1개 (`ClipboardSettings`), C API 1개 (`gw_session_mode_get`). 기존 코드 수정은 `TerminalInputService` 키 분기 확장 + `IEngineService.GetMode()` 1메서드 추가. |
| **핵심 가치**       | WT 수준 보안 필터 + CJK 정밀 복사 + WPF 네이티브 안정성. "데모용 터미널"에서 "일상 도구"로 전환하는 gate 조건 충족. |

---

## 1. 아키텍처 결정

### 1.1 ClipboardService 위치: `GhostWin.Services`

| 후보 | 장점 | 단점 | 결정 |
|-------|------|------|:----:|
| `GhostWin.App/Services` | WPF `Clipboard` API 직접 접근 | `System.Windows.Clipboard`는 STA 필수 -> App 레이어 종속 | X |
| `GhostWin.Services` | Core/Interop만 의존. 테스트 용이 | WPF Clipboard 접근에 추상화 필요 | **채택** |

**결정 근거**: `ClipboardService`의 핵심 로직(필터링, 줄바꿈 정규화, bracket 감싸기)은 플랫폼 무관하다. OS 클립보드 접근만 `ISystemClipboard` 인터페이스로 추상화하면 `GhostWin.Services`에 배치 가능하다. 이렇게 하면 단위 테스트에서 클립보드를 모킹할 수 있다.

```
GhostWin.Core
  IClipboardService      -- Copy/Paste 인터페이스
  ISystemClipboard       -- OS 클립보드 추상화 (GetText/SetText)
  ClipboardSettings      -- 설정 모델

GhostWin.Services
  ClipboardService       -- 필터/정규화/bracket 로직 (핵심)

GhostWin.App
  WpfSystemClipboard     -- ISystemClipboard 구현 (Dispatcher 마셜링)
  TerminalInputService   -- Ctrl+C/V 분기 (기존 TODO 위치)
```

### 1.2 WPF Clipboard API vs Win32 직접 호출

| 방식 | 성능 | 복잡도 | 안정성 |
|------|:----:|:------:|:------:|
| `System.Windows.Clipboard` (OLE) | 보통 (ms 단위) | 낮음 | 높음 |
| Win32 `OpenClipboard`/`SetClipboardData` | 빠름 (us 단위) | 높음 | 중간 (P/Invoke 관리 필요) |

**결정**: WPF `System.Windows.Clipboard` 채택. 터미널 클립보드 조작은 사용자 입력 기반(초당 수 회 미만)이므로 성능 병목이 아니다. WT 주석(`TerminalPage.cpp:2953`)에서도 "Win32 API가 300~1000x 빠르다"고 기록하지만, WPF 앱에서는 OLE 방식이 자연스럽다.

### 1.3 Paste 경로: `ConPty::send_input()` 직접 전송 (WT 패턴)

Plan 단계에서 확정된 경로 B를 채택한다.

| 경로 | 설명 | 결정 |
|------|------|:----:|
| A: `ghostty_surface_text()` | Ghostty 코어가 bracket 자동 처리 | X (GhostWin에 surface 매핑 없음) |
| **B: `IEngineService.WriteSession()`** | 기존 인프라 활용. C# 레이어에서 필터/정규화/bracket 완전 제어 | **채택** |

**근거**: `IEngineService.WriteSession()` -> `gw_session_write()` -> `ConPty::send_input()` 경로가 이미 완비되어 있다. Bracketed paste bracket 감싸기만 직접 구현하면 된다. WT `ControlCore::PasteText`와 동일한 접근법이다.

### 1.4 OSC 52 구현 전략: ConPTY 출력 스트림 직접 파싱

Plan 단계에서 3가지 전략을 제시했다. Ghostty 소스 분석 결과:

- **Ghostty의 OSC 52 콜백은 `ghostty_runtime_config_s` (surface/runtime 수준)에서만 제공된다.**
- GhostWin은 Ghostty를 VT 코어(`ghostty_terminal_*`)로만 사용하며, runtime/surface 레이어를 사용하지 않는다.
- VT terminal 수준의 effects 콜백(`ghostty_terminal_set`)에는 OSC 52 항목이 없다 (title/bell/enquiry 등만 지원).
- 따라서 Ghostty 콜백 경로로는 OSC 52를 수신할 수 없다.

| 전략 | 실현 가능성 | 결정 |
|------|:-----------:|:----:|
| A: vt_bridge에 OSC 콜백 추가 | 낮음 (Ghostty terminal API에 OSC 52 콜백 없음) | X |
| **B: ConPTY 출력 스트림에서 OSC 52 직접 파싱** | 높음 (WT와 동일 방식) | **Phase 1에서 채택** |
| C: Ghostty runtime config 활성화 | 높음 (대규모 리팩토링 필요) | X (ROI 낮음) |

**전략 B 세부 설계**: ConPTY I/O 스레드(`conpty_session.cpp:159`)에서 VtCore에 데이터를 feed하기 전에, 간단한 상태 머신으로 `\x1b]52;` 패턴을 탐지한다. OSC 52 시퀀스가 감지되면 콜백으로 WPF 레이어에 전달한다. Ghostty VtCore에는 OSC 52가 그대로 전달되어도 무해하다 (runtime 콜백이 없으면 무시됨).

---

## 2. 상세 설계

### 2.1 ISystemClipboard (OS 클립보드 추상화)

```csharp
// src/GhostWin.Core/Interfaces/ISystemClipboard.cs
namespace GhostWin.Core.Interfaces;

/// <summary>
/// OS 클립보드 접근 추상화. STA 스레드 마셜링은 구현체 책임.
/// </summary>
public interface ISystemClipboard
{
    /// <summary>클립보드에서 텍스트를 읽는다. 실패 시 null.</summary>
    string? GetText();

    /// <summary>클립보드에 텍스트를 쓴다. OLE 점유 시 재시도.</summary>
    /// <returns>성공 여부</returns>
    bool SetText(string text);
}
```

### 2.2 WpfSystemClipboard (App 레이어 구현)

```csharp
// src/GhostWin.App/Services/WpfSystemClipboard.cs
namespace GhostWin.App.Services;

/// <summary>
/// WPF System.Windows.Clipboard 래퍼.
/// Dispatcher.Invoke로 STA 마셜링 + OLE 재시도 (NFR-03).
/// </summary>
public sealed class WpfSystemClipboard : ISystemClipboard
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 50;

    public string? GetText()
    {
        return ExecuteWithRetry(() =>
        {
            if (System.Windows.Clipboard.ContainsText())
                return System.Windows.Clipboard.GetText();
            return null;
        });
    }

    public bool SetText(string text)
    {
        return ExecuteWithRetry(() =>
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }) ?? false;
    }

    private static T? ExecuteWithRetry<T>(Func<T> action)
    {
        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                return action();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // CLIPBRD_E_CANT_OPEN -- 다른 앱이 클립보드 점유 중
                if (i < MaxRetries - 1)
                    Thread.Sleep(RetryDelayMs);
            }
        }
        return default;
    }
}
```

**설계 결정**: `Dispatcher.Invoke` 대신 동기 호출을 사용한다. `TerminalInputService.HandleKeyDown`은 이미 UI 스레드(WPF Dispatcher)에서 호출되므로 별도 마셜링이 불필요하다.

### 2.3 IClipboardService (Core 인터페이스)

```csharp
// src/GhostWin.Core/Interfaces/IClipboardService.cs
namespace GhostWin.Core.Interfaces;

/// <summary>
/// 터미널 클립보드 서비스. Copy/Paste + 보안 필터링 + Bracketed Paste.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// active pane의 선택 텍스트를 클립보드에 복사하고 선택을 해제한다.
    /// </summary>
    /// <returns>복사 성공 여부 (선택 없으면 false)</returns>
    bool Copy(uint sessionId);

    /// <summary>
    /// 클립보드 텍스트를 필터링/정규화하여 세션에 전달한다.
    /// Phase 2 경고 다이얼로그는 Func 콜백으로 외부 주입.
    /// </summary>
    /// <returns>paste 성공 여부 (취소/빈 클립보드면 false)</returns>
    bool Paste(uint sessionId);

    /// <summary>
    /// OSC 52 Write: 원격 앱이 요청한 클립보드 쓰기.
    /// </summary>
    void Osc52Write(uint sessionId, string text);

    /// <summary>
    /// OSC 52 Read: 원격 앱이 요청한 클립보드 읽기.
    /// 보안 정책에 따라 deny/ask/allow.
    /// </summary>
    string? Osc52Read(uint sessionId);
}
```

### 2.4 ClipboardService (핵심 서비스)

```csharp
// src/GhostWin.Services/ClipboardService.cs
namespace GhostWin.Services;

public sealed class ClipboardService : IClipboardService
{
    private readonly ISystemClipboard _clipboard;
    private readonly IEngineService _engine;
    private readonly ISettingsService _settings;
    private readonly Func<uint, ISelectionService?> _selectionAccessor;
    private readonly Func<uint, bool> _isFocusedSession;

    // Phase 2: 경고 다이얼로그 콜백 (App 레이어에서 DI 주입)
    // true = 사용자가 확인, false = 취소
    public Func<string, int, bool>? OnMultiLineWarning { get; set; }
    public Func<string, int, bool>? OnLargePasteWarning { get; set; }
    // ...constructor...
}
```

#### 2.4.1 Copy 흐름

```
Copy(sessionId)
  |
  +-- selectionAccessor(sessionId) -> ISelectionService
  |     -> HasSelection == false -> return false
  |
  +-- service.GetSelectedText(sessionId) -> string?
  |     -> null or empty -> return false
  |
  +-- _clipboard.SetText(text) -> OLE 재시도 (3회, 50ms)
  |
  +-- service.ClearSelection()
  |
  +-- _engine.SetSelection(sessionId, 0, 0, 0, 0, false)  // DX11 하이라이트 해제
  |
  +-- return true
```

#### 2.4.2 Paste 흐름 (파이프라인)

```
Paste(sessionId)
  |
  +-- (1) _clipboard.GetText() -> string?
  |        -> null or empty -> return false
  |
  +-- (2) Phase 2: 대용량 체크 (>= settings.LargePasteThreshold)
  |        -> OnLargePasteWarning 콜백 -> 취소 시 return false
  |
  +-- (3) Phase 2: 멀티라인 체크 (줄바꿈 2개 이상)
  |        -> OnMultiLineWarning 콜백 -> 취소 시 return false
  |
  +-- (4) Phase 2: TrimPaste (단일 라인 + !bracketed + 설정 활성)
  |        -> 후행 공백/탭/개행 제거 (앞쪽 공백 유지)
  |
  +-- (5) FilterForPaste(text)
  |        -> C0/C1 제어 코드 제거 (HT/LF/CR 허용)
  |
  +-- (6) mode_get(2004) 체크
  |        |
  |        +-- bracketed=true:  "\x1b[200~" + text + "\x1b[201~"
  |        +-- bracketed=false: NormalizeNewlines(text)
  |
  +-- (7) _engine.WriteSession(sessionId, Encoding.UTF8.GetBytes(result))
  |
  +-- (8) _engine.ScrollViewport(sessionId, int.MaxValue)
  |
  +-- return true
```

#### 2.4.3 FilterForPaste (C0/C1 보안 필터링)

WT `ControlCore::_filterStringForPaste` 동일 규칙:

```csharp
internal static string FilterForPaste(string text)
{
    // 중첩된 bracket 마커 제거 (de-fang)
    // ESC[200~ / ESC[201~ 패턴이 텍스트에 포함되면 제거
    text = text.Replace("\x1b[200~", "").Replace("\x1b[201~", "");

    var sb = new StringBuilder(text.Length);
    foreach (char c in text)
    {
        if (c < 0x20) // C0 제어 코드 범위
        {
            // 허용: HT(0x09), LF(0x0A), CR(0x0D)
            if (c == '\t' || c == '\n' || c == '\r')
                sb.Append(c);
            // 나머지 C0 (0x00~0x08, 0x0B~0x0C, 0x0E~0x1F) 제거
        }
        else if (c >= 0x80 && c <= 0x9F)
        {
            // C1 제어 코드 (0x80~0x9F) 제거
        }
        else
        {
            sb.Append(c);
        }
    }
    return sb.ToString();
}
```

**처리 순서 보장**: (1) de-fang -> (2) C0/C1 필터 -> (3) bracket 감싸기. 필터링이 bracket 마커를 훼손할 수 없다. WT와 동일 순서이다.

#### 2.4.4 NormalizeNewlines (줄바꿈 정규화)

```csharp
internal static string NormalizeNewlines(string text)
{
    // Windows 클립보드 CRLF -> CR, 단독 LF -> CR
    // 터미널 프로토콜에서 Enter = CR(0x0D)
    return text.Replace("\r\n", "\r").Replace("\n", "\r");
}
```

**적용 조건**: Bracketed paste mode가 **비활성**일 때만 적용. Bracketed mode에서는 원본 유지. Alacritty, WT 공통 패턴이다.

#### 2.4.5 TrimPaste

```csharp
internal static string TrimPaste(string text)
{
    // 단일 라인만 적용 (줄바꿈 포함 시 비활성)
    if (text.Contains('\n') || text.Contains('\r'))
        return text;
    return text.TrimEnd();
}
```

**비활성 조건**: (1) 멀티라인 (2) Bracketed paste mode 활성. WT `TrimPaste` 동작과 동일.

### 2.5 Bracketed Paste Mode (DECSET 2004)

#### 2.5.1 C API 추가: `gw_session_mode_get`

기존 호출 경로가 이미 C++/C 레이어에 완비되어 있다:

```
VtCore::mode_get(uint16_t)           -- vt_core.cpp:180
  -> vt_bridge_mode_get(terminal, mode_value, &out)  -- vt_bridge.c:363
    -> ghostty_mode_new(mode_value, false)            -- DEC Private Mode
    -> ghostty_terminal_mode_get(terminal, mode, &out)
```

engine-api에 export 함수를 추가한다:

```c
// src/engine-api/ghostwin_engine.h 추가
GWAPI int gw_session_mode_get(GwEngine engine, GwSessionId id,
                              uint16_t mode_value, int* out_value);
```

```c
// src/engine-api/ghostwin_engine.cpp 추가
GWAPI int gw_session_mode_get(GwEngine engine, GwSessionId id,
                              uint16_t mode_value, int* out_value) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !out_value) return GW_ERR_INVALID;

        auto* session = eng->session_mgr->get(id);
        if (!session || !session->conpty) return GW_ERR_NOT_FOUND;

        // ConPty mutex 로 보호: I/O thread 의 write() 와 경합 방지
        std::lock_guard lock(session->conpty->vt_mutex());
        bool val = session->conpty->vt_core().mode_get(mode_value);
        *out_value = val ? 1 : 0;
        return GW_OK;
    GW_CATCH_INT
}
```

**스레드 안전성**: `mode_get`은 VT 상태를 읽는 작업이므로, I/O 스레드의 `vt_bridge_write`와 경합할 수 있다. ConPty의 `vt_mutex`로 보호한다. 기존 `gw_session_get_selected_text`와 동일한 패턴이다.

#### 2.5.2 P/Invoke + IEngineService 확장

```csharp
// src/GhostWin.Interop/NativeEngine.cs 추가
[LibraryImport(Dll)]
internal static partial int gw_session_mode_get(nint engine, uint id,
    ushort modeValue, out int outValue);
```

```csharp
// src/GhostWin.Core/Interfaces/IEngineService.cs 추가
/// <summary>
/// VT terminal mode 상태 조회 (DECSET/DECRST).
/// </summary>
/// <param name="sessionId">세션 ID</param>
/// <param name="modeValue">DEC Private Mode 번호 (예: 2004 = Bracketed Paste)</param>
/// <returns>해당 모드가 활성화되어 있으면 true</returns>
bool GetMode(uint sessionId, ushort modeValue);
```

```csharp
// src/GhostWin.Interop/EngineService.cs 추가
public bool GetMode(uint sessionId, ushort modeValue)
{
    if (_engine == IntPtr.Zero) return false;
    int result = NativeEngine.gw_session_mode_get(_engine, sessionId, modeValue, out int val);
    return result == 0 && val != 0;
}
```

#### 2.5.3 사용 위치

`ClipboardService.Paste()` 파이프라인의 step (6):

```csharp
bool bracketed = _engine.GetMode(sessionId, 2004); // VT_MODE_BRACKETED_PASTE

if (bracketed)
{
    text = "\x1b[200~" + text + "\x1b[201~";
    // bracketed mode 에서는 줄바꿈 정규화 적용 안 함
}
else
{
    text = NormalizeNewlines(text);
}
```

### 2.6 TerminalInputService 확장

현재 `TerminalInputService.cs`의 TODO 위치에 클립보드 분기를 추가한다.

#### 2.6.1 수정 범위

```csharp
// src/GhostWin.App/Services/TerminalInputService.cs

public sealed class TerminalInputService : ITerminalInputService
{
    private readonly IEngineService _engine;
    private readonly ISessionManager _sessionManager;
    private readonly IWorkspaceService _workspaceService;
    private readonly IClipboardService _clipboard;                   // +++ 추가
    private readonly Func<uint, ISelectionService?> _selectionAccessor; // +++ 추가

    // ... constructor에 IClipboardService 주입 ...
```

#### 2.6.2 단축키 테이블 확장

`BuildShortcutTable()`에 클립보드 단축키를 추가한다:

```csharp
private AppShortcut[] BuildShortcutTable() =>
[
    // ... 기존 단축키 ...

    // Clipboard: Ctrl+Shift+C/V (명시적 복사/붙여넣기)
    new(Key.C, true, true, false, CopyToClipboard),
    new(Key.V, true, true, false, PasteFromClipboard),

    // Clipboard: Shift+Insert (전통적 paste)
    new(Key.Insert, false, true, false, PasteFromClipboard),
];
```

#### 2.6.3 Ctrl+C 이중 역할 (VT 분기 수정)

현재 `TryWriteVtSequence`의 Ctrl+C 분기를 수정한다:

```csharp
// 기존 (line 108):
// else if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
// {
//     // TODO M-10.5: if (HasSelection) Clipboard.SetText(...)
//     data = "\x03"u8.ToArray();
// }

// 수정 후:
else if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
{
    if (_clipboard.Copy(activeId))
        return true;  // 선택 있으면 복사 후 handled
    data = "\x03"u8.ToArray();  // 비선택 -> SIGINT
}
else if (key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
{
    _clipboard.Paste(activeId);
    return true;
}
```

**설계 결정**: Ctrl+C/V는 `TryWriteVtSequence` 안에서 직접 처리한다 (단축키 테이블이 아님). 이유: Ctrl+C는 VT 시퀀스(`\x03`)와 클립보드 복사 사이의 조건 분기가 필요하므로, VT 변환 로직과 같은 위치에 있어야 의도가 명확하다. Ctrl+Shift+C/V와 Shift+Insert는 항상 동일 동작이므로 단축키 테이블에 등록한다.

#### 2.6.4 Copy/Paste 헬퍼

```csharp
private void CopyToClipboard()
{
    if (_sessionManager.ActiveSessionId is { } id)
        _clipboard.Copy(id);
}

private void PasteFromClipboard()
{
    if (_sessionManager.ActiveSessionId is { } id)
        _clipboard.Paste(id);
}
```

### 2.7 Selection 접근 경로

`ClipboardService.Copy()`가 active pane의 `ISelectionService`에 접근해야 한다. 현재 `ISelectionService`는 per-host 인스턴스이며 `PaneHostManager._activeHosts`에서 `TerminalHostControl._selectionService`로 접근 가능하다.

**접근 전략**: DI 시점에 `Func<uint, ISelectionService?>` accessor를 주입한다.

```csharp
// App.xaml.cs DI 등록
services.AddSingleton<IClipboardService>(sp =>
{
    var hostManager = sp.GetRequiredService<PaneHostManager>();
    return new ClipboardService(
        sp.GetRequiredService<ISystemClipboard>(),
        sp.GetRequiredService<IEngineService>(),
        sp.GetRequiredService<ISettingsService>(),
        selectionAccessor: sessionId =>
        {
            // PaneHostManager에서 sessionId로 host 탐색 -> selectionService 반환
            foreach (var host in hostManager.ActiveHosts.Values)
                if (host.SessionId == sessionId)
                    return host._selectionService;
            return null;
        },
        isFocusedSession: sessionId =>
            sp.GetRequiredService<ISessionManager>().ActiveSessionId == sessionId
    );
});
```

**PaneHostManager 수정**: `_selectionService` 접근을 위해 `TerminalHostControl._selectionService`를 `internal`에서 `public`으로 변경하거나, `PaneHostManager`에 `GetSelectionService(uint sessionId)` 메서드를 추가한다.

**권장**: `PaneHostManager`에 메서드를 추가하여 캡슐화를 유지한다:

```csharp
// src/GhostWin.App/Services/PaneHostManager.cs 추가
public ISelectionService? GetSelectionService(uint sessionId)
{
    foreach (var host in _activeHosts.Values)
        if (host.SessionId == sessionId)
            return host._selectionService;
    return null;
}
```

### 2.8 OSC 52 설계

#### 2.8.1 ConPTY 출력 스트림 파싱

`ConPtySession::Impl::io_thread_func`(conpty_session.cpp:159)에서 VtCore에 데이터를 feed하기 전에 OSC 52 패턴을 탐지한다.

OSC 52 형식:
```
Write: ESC ] 52 ; <selection> ; <base64-data> BEL
       ESC ] 52 ; <selection> ; <base64-data> ST
Read:  ESC ] 52 ; <selection> ; ? BEL
```

여기서 `<selection>`은 `c` (clipboard), `p` (primary), `s` (secondary) 등이다. Windows에서는 `c`만 의미 있다.

**상태 머신 설계**:

```
IDLE -> ESC 감지 -> SAW_ESC
SAW_ESC -> ']' -> SAW_OSC_START
SAW_OSC_START -> '5' -> SAW_5
SAW_5 -> '2' -> SAW_52
SAW_52 -> ';' -> ACCUMULATING
ACCUMULATING -> BEL(0x07) 또는 ST(ESC '\') -> COMPLETE
그 외 전이 -> IDLE 복귀
```

**구현 위치**: `src/conpty/osc52_detector.h` + `osc52_detector.cpp`

```cpp
// src/conpty/osc52_detector.h
#pragma once
#include <string>
#include <functional>
#include <cstdint>

/// ConPTY 출력 스트림에서 OSC 52 시퀀스를 탐지하는 간단한 상태 머신.
/// 매 바이트를 feed하여 OSC 52 완성 시 콜백을 호출한다.
/// VtCore에는 데이터가 그대로 전달된다 (소비하지 않음).
class Osc52Detector {
public:
    /// selection: "c"/"p" 등, data: base64 인코딩된 텍스트 또는 "?"(읽기 요청)
    using Callback = std::function<void(const std::string& selection,
                                        const std::string& data)>;

    explicit Osc52Detector(Callback cb);

    /// 바이트 스트림을 feed. 내부 상태 머신으로 OSC 52를 탐지한다.
    /// 감지 시 콜백 호출 (I/O 스레드에서 호출됨).
    void feed(const uint8_t* data, size_t len);

    /// 상태 초기화.
    void reset();

private:
    enum class State : uint8_t {
        Idle, SawEsc, SawBracket, Saw5, Saw52,
        SawSemicolon, AccumSelection, AccumData, SawEscInData
    };

    Callback callback_;
    State state_ = State::Idle;
    std::string selection_;
    std::string data_;
};
```

#### 2.8.2 I/O 스레드 통합

```cpp
// conpty_session.cpp 수정 (io_thread_func 내부)
// 기존 코드:
//   impl->vt_core->write({buf.get(), bytes_read});
// 수정 후:
    impl->osc52_detector.feed(buf.get(), bytes_read);  // +++ 탐지만 (소비 안 함)
    impl->vt_core->write({buf.get(), bytes_read});      // 기존대로 VtCore에 전달
```

**핵심**: Osc52Detector는 데이터를 소비하지 않는다. VtCore에는 원본 데이터가 그대로 전달된다. Ghostty VT 파서가 OSC 52를 만나면 runtime 콜백이 없어 무시하므로 부작용이 없다.

#### 2.8.3 콜백 전달 경로

```
I/O thread: Osc52Detector.feed()
  -> Callback(selection, data)
    -> ConPtySession.on_osc52 콜백 호출
      -> SessionManager.on_osc52 전파
        -> Engine.on_osc52 -> C API 콜백
          -> GwCallbackContext.OnOsc52 (WPF Dispatcher로 마셜링)
            -> ClipboardService.Osc52Write() 또는 Osc52Read()
```

#### 2.8.4 GwCallbackContext 확장

```csharp
// src/GhostWin.Core/Interfaces/IEngineService.cs 수정
public class GwCallbackContext
{
    // ... 기존 콜백 ...
    public Action<uint, string>? OnOsc52Write { get; set; }    // +++ sessionId, base64Text
    public Action<uint>? OnOsc52Read { get; set; }             // +++ sessionId
}
```

#### 2.8.5 OSC 52 Write 보안

```csharp
public void Osc52Write(uint sessionId, string text)
{
    // 보안: focused session만 허용
    if (!_isFocusedSession(sessionId)) return;

    // 설정 체크
    var settings = _settings.Current.Clipboard;
    if (!settings.Osc52Write) return;

    // base64 디코드 -> 클립보드 쓰기
    try
    {
        var decoded = Convert.FromBase64String(text);
        var str = Encoding.UTF8.GetString(decoded);
        _clipboard.SetText(str);
    }
    catch (FormatException)
    {
        // 잘못된 base64 -- 무시
    }
}
```

#### 2.8.6 OSC 52 Read 보안

```csharp
public string? Osc52Read(uint sessionId)
{
    if (!_isFocusedSession(sessionId)) return null;

    var settings = _settings.Current.Clipboard;
    switch (settings.Osc52Read)
    {
        case "deny":
            return null;
        case "allow":
            break;
        case "ask":
            // Phase 2+에서 확인 다이얼로그 추가
            // 현재는 deny로 fallback
            return null;
        default:
            return null;
    }

    var text = _clipboard.GetText();
    if (string.IsNullOrEmpty(text)) return null;

    return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
```

**OSC 52 Read 응답**: 읽은 텍스트를 base64 인코딩하여 `\x1b]52;c;<base64>\x07` 형태로 ConPTY 입력에 써야 한다. 이는 `IEngineService.WriteSession()`으로 전달한다.

### 2.9 ClipboardSettings (설정 모델)

```csharp
// src/GhostWin.Core/Models/ClipboardSettings.cs
namespace GhostWin.Core.Models;

public sealed class ClipboardSettings
{
    /// <summary>마우스 선택 완료 시 자동 클립보드 복사 (Phase 3).</summary>
    public bool CopyOnSelect { get; set; } = false;

    /// <summary>단일 라인 paste 시 후행 공백 트림 (Phase 2).</summary>
    public bool TrimPaste { get; set; } = true;

    /// <summary>멀티라인 paste 경고 다이얼로그 표시 (Phase 2).</summary>
    public bool WarnOnMultiLinePaste { get; set; } = true;

    /// <summary>대용량 paste 경고 다이얼로그 표시 (Phase 2).</summary>
    public bool WarnOnLargePaste { get; set; } = true;

    /// <summary>대용량 경고 임계값 (바이트). 기본 5KiB (Phase 2).</summary>
    public int LargePasteThreshold { get; set; } = 5120;

    /// <summary>OSC 52 Write 허용 (Phase 1).</summary>
    public bool Osc52Write { get; set; } = true;

    /// <summary>OSC 52 Read 정책: "deny" | "ask" | "allow" (Phase 1).</summary>
    public string Osc52Read { get; set; } = "deny";
}
```

```csharp
// src/GhostWin.Core/Models/AppSettings.cs 수정
public sealed class AppSettings
{
    // ... 기존 ...
    public ClipboardSettings Clipboard { get; set; } = new();  // +++ 추가
}
```

### 2.10 멀티라인/대용량 경고 다이얼로그 (Phase 2)

#### 2.10.1 PasteWarningDialog

```xml
<!-- src/GhostWin.App/Dialogs/PasteWarningDialog.xaml -->
<Window x:Class="GhostWin.App.Dialogs.PasteWarningDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="Paste Warning" SizeToContent="WidthAndHeight"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="20">
        <TextBlock x:Name="WarningMessage" TextWrapping="Wrap" MaxWidth="400"/>
        <TextBox x:Name="Preview" IsReadOnly="True" MaxHeight="100"
                 Margin="0,10" FontFamily="Consolas" FontSize="12"
                 VerticalScrollBarVisibility="Auto"/>
        <CheckBox x:Name="DontAskAgain" Content="다시 묻지 않기" Margin="0,10"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="붙여넣기" IsDefault="True" Click="OnPaste"
                    Padding="20,5" Margin="0,0,10,0"/>
            <Button Content="취소" IsCancel="True" Padding="20,5"/>
        </StackPanel>
    </StackPanel>
</Window>
```

#### 2.10.2 다이얼로그 연동

`ClipboardService.Paste()` 파이프라인에서 경고 조건 체크 후 콜백을 호출한다. 콜백은 App 레이어에서 주입하며, UI 스레드에서 `PasteWarningDialog.ShowDialog()`를 호출한다.

```csharp
// Phase 2 파이프라인 삽입 위치 (ClipboardService.Paste 내부)

var settings = _settings.Current.Clipboard;
int byteSize = Encoding.UTF8.GetByteCount(text);

// 대용량 경고
if (settings.WarnOnLargePaste && byteSize >= settings.LargePasteThreshold)
{
    if (OnLargePasteWarning != null && !OnLargePasteWarning(text, byteSize))
        return false;  // 사용자 취소
}

// 멀티라인 경고
int lineCount = text.Count(c => c == '\n') + 1;
if (settings.WarnOnMultiLinePaste && lineCount >= 2)
{
    if (OnMultiLineWarning != null && !OnMultiLineWarning(text, lineCount))
        return false;  // 사용자 취소
}
```

**"다시 묻지 않기" 처리**: 다이얼로그에서 체크박스 선택 시 `ISettingsService`를 통해 `settings.json`에 반영한다. 핫 리로드로 즉시 적용된다.

### 2.11 우클릭 컨텍스트 메뉴 (Phase 3)

#### 2.11.1 Airspace 문제

`TerminalHostControl`은 `HwndHost`이므로 WPF 컨텐츠가 위에 그려지지 않는다 (Airspace 제약). 일반 `ContextMenu`는 가려진다.

**해결 방안**: 별도 `Popup` Window를 생성한다. M-12 Command Palette과 동일한 접근법이다.

```csharp
// src/GhostWin.App/Controls/TerminalContextMenu.cs
namespace GhostWin.App.Controls;

/// <summary>
/// HwndHost Airspace 우회를 위한 Popup 기반 컨텍스트 메뉴.
/// 별도 Win32 창으로 렌더링되므로 HwndHost 위에 표시 가능.
/// </summary>
public sealed class TerminalContextMenu
{
    private readonly IClipboardService _clipboard;
    private readonly Func<uint> _activeSessionAccessor;
    private readonly Func<bool> _hasSelectionAccessor;

    public void Show(Point screenPosition)
    {
        // WPF Popup (AllowsTransparency + PlacementTarget=null -> 별도 Window)
        var popup = new Popup
        {
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = screenPosition.X,
            VerticalOffset = screenPosition.Y,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = BuildMenu(),
        };
        popup.IsOpen = true;
    }

    private StackPanel BuildMenu()
    {
        bool hasSelection = _hasSelectionAccessor();
        // [복사] [붙여넣기] [모두 선택] 메뉴 구성
        // hasSelection == false면 [복사] 비활성
        // ...
    }
}
```

#### 2.11.2 우클릭 이벤트 연결

`TerminalHostControl.WndProc`에서 `WM_RBUTTONUP` 수신 시 `TerminalContextMenu.Show()`를 Dispatcher를 통해 호출한다.

### 2.12 Copy-on-Select (Phase 3)

`TerminalHostControl.SelectionChanged` 이벤트는 현재 발행 중이나 소비자가 없다 (M-10c에서 준비). Copy-on-Select 활성화 시 이 이벤트를 구독한다.

```csharp
// PaneHostManager.EnsureServices 또는 별도 구독 위치
host.SelectionChanged += (sender, args) =>
{
    if (!settings.Clipboard.CopyOnSelect) return;
    if (args.Range == null) return;
    // 마우스 업 (선택 완료) 시에만 복사
    // OnMouseUp -> SelectionChanged 발생 시점
    _clipboard.Copy(host.SessionId);
};
```

---

## 3. 데이터 흐름 다이어그램

### 3.1 Copy 흐름

```
사용자 Ctrl+C (선택 있음) / Ctrl+Shift+C
    |
    v
TerminalInputService.TryWriteVtSequence (Ctrl+C 분기)
  또는 AppShortcut 테이블 (Ctrl+Shift+C)
    |
    v
ClipboardService.Copy(sessionId)
    |
    +-- PaneHostManager.GetSelectionService(sessionId)
    |     -> ISelectionService.HasSelection? --[no]--> return false
    |
    +-- ISelectionService.GetSelectedText(sessionId)
    |     -> IEngineService.GetSelectedText (P/Invoke)
    |        -> gw_session_get_selected_text (C API)
    |           -> TerminalRenderState.frame().row() (UTF-8)
    |
    +-- ISystemClipboard.SetText(text)
    |     -> System.Windows.Clipboard.SetText (OLE, 3회 재시도)
    |
    +-- ISelectionService.ClearSelection()
    |
    +-- IEngineService.SetSelection(active=false)
    |     -> DX11 하이라이트 해제
    |
    v
return true (handled)
```

### 3.2 Paste 흐름

```
사용자 Ctrl+V / Ctrl+Shift+V / Shift+Insert
    |
    v
TerminalInputService
    |
    v
ClipboardService.Paste(sessionId)
    |
    +-- ISystemClipboard.GetText() ----[null]--> return false
    |
    +-- [Phase 2] 대용량 체크 (>= 5KiB?)
    |     -> OnLargePasteWarning 콜백 --[취소]--> return false
    |
    +-- [Phase 2] 멀티라인 체크 (>= 2줄?)
    |     -> OnMultiLineWarning 콜백 --[취소]--> return false
    |
    +-- [Phase 2] TrimPaste (단일 라인 + 설정 활성?)
    |     -> text.TrimEnd()
    |
    +-- FilterForPaste(text)
    |     (1) bracket 마커 de-fang
    |     (2) C0 제거 (HT/LF/CR 허용)
    |     (3) C1 제거 (0x80~0x9F)
    |
    +-- IEngineService.GetMode(sessionId, 2004)
    |     -> gw_session_mode_get (P/Invoke)
    |        |
    |        +--[true]  "\x1b[200~" + text + "\x1b[201~"
    |        +--[false] NormalizeNewlines(text)
    |                   \r\n -> \r, \n -> \r
    |
    +-- IEngineService.WriteSession(sessionId, UTF8 bytes)
    |     -> gw_session_write -> ConPty::send_input
    |
    +-- IEngineService.ScrollViewport(sessionId, MaxValue)
    |
    v
return true
```

### 3.3 OSC 52 Write 흐름

```
원격 앱 (vim/tmux) -> OSC 52 Write 시퀀스 전송
    |
    v
ConPTY output -> I/O thread ReadFile
    |
    v
Osc52Detector.feed(data)
    -> 상태 머신 탐지: ESC ] 52 ; c ; <base64> BEL
    |
    v
ConPtySession.on_osc52 콜백 (I/O thread)
    |
    v
SessionManager -> Engine -> GwCallbackContext.OnOsc52Write
    |
    v
WPF Dispatcher.BeginInvoke
    |
    v
ClipboardService.Osc52Write(sessionId, base64Text)
    +-- isFocusedSession? --[no]--> 무시
    +-- settings.Osc52Write? --[false]--> 무시
    +-- base64 디코드 -> ISystemClipboard.SetText
```

### 3.4 OSC 52 Read 흐름

```
원격 앱 -> ESC ] 52 ; c ; ? BEL
    |
    v
Osc52Detector.feed -> 콜백: selection="c", data="?"
    |
    v
GwCallbackContext.OnOsc52Read(sessionId)
    |
    v
ClipboardService.Osc52Read(sessionId)
    +-- isFocusedSession? --[no]--> null
    +-- settings.Osc52Read == "deny"? --> null
    +-- settings.Osc52Read == "allow"?
    |     -> ISystemClipboard.GetText -> base64 인코딩
    |
    v
응답: IEngineService.WriteSession(sessionId, "\x1b]52;c;<base64>\x07")
```

---

## 4. 파일 변경 목록 + 구현 순서

### 4.1 신규 파일

| # | 파일 | Phase | 설명 | 예상 줄 수 |
|:-:|------|:-----:|------|:----------:|
| 1 | `src/GhostWin.Core/Interfaces/IClipboardService.cs` | 1 | 클립보드 서비스 인터페이스 | ~25 |
| 2 | `src/GhostWin.Core/Interfaces/ISystemClipboard.cs` | 1 | OS 클립보드 추상화 | ~15 |
| 3 | `src/GhostWin.Core/Models/ClipboardSettings.cs` | 1 | 설정 모델 (Phase 2/3 키 포함) | ~25 |
| 4 | `src/GhostWin.Services/ClipboardService.cs` | 1 | Copy/Paste/Filter/Normalize/OSC52 | ~180 |
| 5 | `src/GhostWin.App/Services/WpfSystemClipboard.cs` | 1 | WPF Clipboard 래퍼 + OLE 재시도 | ~50 |
| 6 | `src/conpty/osc52_detector.h` | 1 | OSC 52 상태 머신 헤더 | ~40 |
| 7 | `src/conpty/osc52_detector.cpp` | 1 | OSC 52 상태 머신 구현 | ~100 |
| 8 | `src/GhostWin.App/Dialogs/PasteWarningDialog.xaml` | 2 | 경고 다이얼로그 XAML | ~25 |
| 9 | `src/GhostWin.App/Dialogs/PasteWarningDialog.xaml.cs` | 2 | 다이얼로그 코드비하인드 | ~40 |
| 10 | `src/GhostWin.App/Controls/TerminalContextMenu.cs` | 3 | 우클릭 Popup 메뉴 | ~80 |

### 4.2 수정 파일

| # | 파일 | Phase | 수정 내용 | 예상 diff |
|:-:|------|:-----:|-----------|:---------:|
| 1 | `src/engine-api/ghostwin_engine.h` | 1 | `gw_session_mode_get` 선언 추가 | +3줄 |
| 2 | `src/engine-api/ghostwin_engine.cpp` | 1 | `gw_session_mode_get` 구현 + OSC 52 콜백 구조체 | +20줄 |
| 3 | `src/GhostWin.Interop/NativeEngine.cs` | 1 | `gw_session_mode_get` P/Invoke | +4줄 |
| 4 | `src/GhostWin.Interop/EngineService.cs` | 1 | `GetMode()` 구현 | +8줄 |
| 5 | `src/GhostWin.Core/Interfaces/IEngineService.cs` | 1 | `GetMode()` + 콜백 확장 | +10줄 |
| 6 | `src/GhostWin.App/Services/TerminalInputService.cs` | 1 | Ctrl+C/V 분기 + 단축키 추가 | +30줄 |
| 7 | `src/GhostWin.App/Services/PaneHostManager.cs` | 1 | `GetSelectionService()` 추가 | +8줄 |
| 8 | `src/GhostWin.Core/Models/AppSettings.cs` | 1 | `Clipboard` 프로퍼티 추가 | +1줄 |
| 9 | `src/GhostWin.App/App.xaml.cs` | 1 | DI 등록 (`IClipboardService`, `ISystemClipboard`) | +15줄 |
| 10 | `src/conpty/conpty_session.h` | 1 | Osc52Detector 멤버 + 콜백 타입 | +5줄 |
| 11 | `src/conpty/conpty_session.cpp` | 1 | I/O 스레드에 feed 호출 + 콜백 전파 | +10줄 |
| 12 | `src/engine-api/CMakeLists.txt` (또는 해당 빌드) | 1 | `osc52_detector.cpp` 추가 | +1줄 |

### 4.3 구현 순서

```
Phase 1 -- 필수 복사/붙여넣기 (Day 1~3)
============================================

Step 1: mode_get P/Invoke 경로 확보
  [C++] ghostwin_engine.h/cpp: gw_session_mode_get 추가
  [C#]  NativeEngine.cs: P/Invoke 선언
  [C#]  IEngineService.cs: GetMode() 인터페이스
  [C#]  EngineService.cs: GetMode() 구현
  검증: GetMode(sessionId, 2004) 호출 성공

Step 2: ClipboardService 핵심 구현
  [C#]  ISystemClipboard.cs: 인터페이스
  [C#]  IClipboardService.cs: 인터페이스
  [C#]  ClipboardSettings.cs: 설정 모델
  [C#]  ClipboardService.cs: Copy/Paste/Filter/Normalize
  [C#]  WpfSystemClipboard.cs: WPF 구현
  [C#]  AppSettings.cs: Clipboard 프로퍼티 추가
  검증: 단위 로직 (필터/정규화) 정상 동작

Step 3: TerminalInputService 확장
  [C#]  TerminalInputService.cs: Ctrl+C 이중 역할 + Ctrl+V + 단축키
  [C#]  PaneHostManager.cs: GetSelectionService 추가
  [C#]  App.xaml.cs: DI 등록
  검증: Ctrl+C/V, Ctrl+Shift+C/V, Shift+Insert smoke 테스트

Step 4: OSC 52 Write/Read
  [C++] osc52_detector.h/cpp: 상태 머신
  [C++] conpty_session.h/cpp: 탐지기 통합 + 콜백 전파
  [C++] ghostwin_engine.cpp: 콜백 구조체 확장
  [C#]  GwCallbackContext: OnOsc52Write/Read 추가
  [C#]  ClipboardService: Osc52Write/Osc52Read 구현
  검증: SSH에서 printf '\x1b]52;c;...\x07' -> 클립보드 확인

Phase 2 -- 안전성 (Day 4)
============================================

Step 5: 경고 다이얼로그
  [XAML] PasteWarningDialog.xaml + .cs
  [C#]  ClipboardService: 콜백 연동
  검증: 3줄/6KiB 텍스트 paste 시 경고

Step 6: TrimPaste + 설정 연동
  [C#]  ClipboardService: TrimPaste 로직
  [C#]  설정 핫 리로드 연동
  검증: 단일 라인 후행 공백 제거

Phase 3 -- 편의/확장 (Day 5~6)
============================================

Step 7: 우클릭 컨텍스트 메뉴
  [C#]  TerminalContextMenu.cs
  검증: 우클릭 시 메뉴 표시

Step 8: Copy-on-Select + 설정 JSON
  [C#]  SelectionChanged 이벤트 구독
  검증: 설정 true -> 드래그 완료 시 자동 복사
```

---

## 5. 위험 요소 + 완화 방안

| # | 위험 | 영향 | 확률 | 완화 전략 | 검증 시점 |
|:-:|------|:----:|:----:|-----------|-----------|
| R1 | `mode_get` P/Invoke 래핑 실패 | 높음 | 낮음 | C++ 래퍼 이미 존재 (`VtCore::mode_get`). export 함수 추가만 필요. Step 1에서 즉시 검증 | Step 1 |
| R2 | WPF `Clipboard` OLE 점유 경합 | 중간 | 중간 | `WpfSystemClipboard`에 3회 재시도 (50ms 간격). 실패 시 무시 (크래시 없음) | Step 2 |
| R3 | Bracketed paste에서 필터가 bracket 마커 훼손 | 높음 | 낮음 | (1) de-fang -> (2) C0/C1 필터 -> (3) bracket 감싸기 순서 보장 (WT 동일) | Step 3 |
| R4 | OSC 52 상태 머신의 부분 시퀀스 false positive | 중간 | 낮음 | 완전한 시퀀스 (`ESC]52;` + `;` + BEL/ST)만 매칭. 부분 매칭은 IDLE 복귀 | Step 4 |
| R5 | OSC 52 탐지가 I/O 스레드 성능 저하 | 낮음 | 낮음 | 상태 머신은 바이트당 단일 비교 연산. O(n) 추가 비용은 ReadFile I/O 대비 무시 가능 | Step 4 |
| R6 | HwndHost Airspace로 ContextMenu 가림 | 중간 | 중간 | Popup Window 방식 우회. M-12 Command Palette과 동일 해결 | Step 7 |
| R7 | 대용량 paste (1MB+) 시 UI 프리즈 | 중간 | 낮음 | Phase 2 대용량 경고로 선제 차단. 필터링 자체는 `StringBuilder` 기반으로 빠름 | Step 5 |
| R8 | `ConPtySession` vt_mutex 경합으로 `GetMode` 지연 | 낮음 | 낮음 | `mode_get`은 단순 bool 조회 (~us). I/O 스레드 write와 경합 시에도 대기 시간 극미 | Step 1 |

---

## 6. 수락 기준 (FR별)

### Phase 1

| FR | 시나리오 | 기대 결과 | 검증 방법 |
|----|----------|-----------|-----------|
| FR-01 | 텍스트 선택 후 Ctrl+C | 클립보드에 텍스트 저장 | 메모장에 Ctrl+V로 확인 |
| FR-01 | 비선택 상태에서 Ctrl+C | `^C` 표시 (SIGINT) | bash/PowerShell에서 확인 |
| FR-02 | ESC 시퀀스 포함 텍스트 paste | 제어 코드 제거, 무해화 | 악성 payload 수동 구성 후 paste |
| FR-03 | vim insert mode에서 Ctrl+V | auto-indent 미작동 | `set paste` 없이 코드 paste |
| FR-04 | 메모장 멀티라인 복사 후 bash paste | 줄바꿈 정상 처리 | 각 줄이 순차 실행됨 |
| FR-05 | Ctrl+Shift+C (선택 없음) | 아무 일 없음 | 관찰 |
| FR-05 | Ctrl+Shift+V | paste 동작 | 메모장에서 복사한 텍스트 확인 |
| FR-06 | Shift+Insert | paste 동작 | Ctrl+V와 동일 결과 |
| FR-07 | 복사 후 DX11 하이라이트 | 즉시 사라짐 | 시각 확인 |
| FR-14 | SSH에서 `printf '\x1b]52;c;...\x07'` | 클립보드에 텍스트 저장 | 디코딩 결과 확인 |
| CJK | 한글/일본어 텍스트 복사-paste 왕복 | 글자 깨짐 없음 | CJK 텍스트 왕복 검증 |
| OLE | 다른 앱 클립보드 점유 중 Ctrl+C | 재시도 후 복사 또는 무시 (크래시 없음) | 의도적 점유 후 테스트 |

### Phase 2

| FR | 시나리오 | 기대 결과 |
|----|----------|-----------|
| FR-08 | 3줄 텍스트 paste | 경고 다이얼로그, [붙여넣기] 클릭 시 정상 paste |
| FR-08 | "다시 묻지 않기" 체크 | 이후 경고 미표시 |
| FR-09 | 6KiB 텍스트 paste | "약 6KB" 크기 경고 표시 |
| FR-10 | `" ls -la \n"` paste | `" ls -la"` 결과 (앞 공백 유지, 뒤 공백/개행 제거) |

### Phase 3

| FR | 시나리오 | 기대 결과 |
|----|----------|-----------|
| FR-11 | 터미널 우클릭 (선택 있음) | [복사] [붙여넣기] [모두 선택] 메뉴 |
| FR-11 | 터미널 우클릭 (비선택) | [복사] 비활성 (회색) |
| FR-12 | `copyOnSelect: true` + 드래그 | 드래그 완료 시 자동 복사 |
| FR-13 | `settings.json` 클립보드 키 변경 | 핫 리로드로 즉시 반영 |

---

## 참조 문서

| 문서 | 경로 |
|------|------|
| Clipboard PRD v1.1 | `docs/00-pm/clipboard.prd.md` |
| Clipboard Plan | `docs/01-plan/features/clipboard.plan.md` |
| 클립보드 기술 리서치 | `docs/00-research/research-clipboard-copy-paste.md` |
| WPF Migration Design | `docs/02-design/features/wpf-migration.design.md` |
| SelectionService Design | `docs/02-design/features/selection-service.design.md` |
| M-10 마우스 입력 아카이브 | `docs/archive/2026-04/mouse-input/` |
| Settings System (M-4) 아카이브 | `docs/archive/2026-04/settings-system/` |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-11 | Initial Design -- PRD v1.1 + Plan + 리서치 + 현재 코드 분석 기반 |
