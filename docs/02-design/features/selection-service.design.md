# SelectionService Design Document

> **Summary**: TerminalHostControl 의 Selection 로직 259줄을 ISelectionService 로 분리
>
> **Project**: GhostWin Terminal
> **Author**: rkit-wpf-architect
> **Date**: 2026-04-11
> **Status**: Draft
> **Scope**: P1 리팩토링 (M-10.5 복사/붙여넣기 선행 작업)

---

## 0. 현상 분석

### 0.1 현재 코드 위치와 줄 수

| 위치 (TerminalHostControl.cs) | 줄 범위 | 줄 수 | 역할 |
|------|---------|:----:|------|
| Selection 상태 필드 | L42-54 | 13 | `_selection`, `_clickCount`, `_anchorRow` 등 |
| PixelToCell | L256-265 | 10 | 픽셀→셀 좌표 변환 |
| UpdateClickCount | L271-294 | 24 | 더블/트리플 클릭 판별 |
| FindWordBounds | L302-349 | 48 | 단어 경계 탐색 (CJK wide char 처리) |
| IsWordChar | L355-375 | 21 | Unicode 카테고리 기반 단어 문자 판별 |
| FindLineBounds | L381-395 | 15 | 줄 전체 선택 범위 |
| HandleSelection | L397-508 | 112 | click/drag/word/line 통합 God Method |
| NotifySelectionChanged | L510-537 | 28 | DX11 SetSelection + WPF 이벤트 |
| **합계** | | **271** | |

### 0.2 문제점 5가지

1. **HandleSelection 112줄 God Method** — click/drag/word/line 4가지 모드가 하나의 static 메서드에 혼재
2. **터미널 텍스트 처리가 UI Control 안에 존재** — `FindWordBounds`, `IsWordChar` 는 도메인 로직이나 View 계층(`HwndHost`)에 위치
3. **PixelToCell 좌표 변환이 View 계층에 존재** — 셀 크기는 engine 이 알고 있으므로 서비스에서 처리해야 함
4. **`internal _engine` 필드 직접 접근** — DI 가 아닌 `Ioc.Default.GetService` 로 주입 후 `internal` 필드로 노출
5. **Selection 상태 외부 접근 불가** — M-10.5 클립보드 복사 시 `GetSelectedText` 를 호출하려면 현재 selection 범위를 외부에서 가져올 방법이 없음

---

## 1. 설계 목표

| # | 목표 | 측정 |
|:-:|------|------|
| G1 | TerminalHostControl 에서 Selection 로직 100% 분리 | 271줄 → 0줄 (위임 호출만 잔류) |
| G2 | HandleSelection God Method 해소 | 112줄 단일 메서드 → 3개 이하 메서드 (각 40줄 이하) |
| G3 | M-10.5 클립보드 확장점 확보 | `ISelectionService.GetSelectedText()` 외부 호출 가능 |
| G4 | WndProc 스레드 안전성 유지 | 동기 P/Invoke 경로 보존 (Dispatcher 경유 없음) |
| G5 | 테스트 가능성 확보 | engine mock 으로 Selection 로직 단위 테스트 가능 |

### 설계 원칙

- **Single Responsibility**: Selection 상태 관리 = `SelectionService`, 좌표 변환 = 유틸리티, UI 이벤트 수신 = `TerminalHostControl`
- **Depend on Abstractions**: `ISelectionService` 인터페이스를 통해 DI 주입
- **WT 패턴 준수**: deferred drag, click count, SetCapture/ReleaseCapture 흐름 유지

---

## 2. 아키텍처

### 2.1 계층 배치

```
GhostWin.Core (Domain)
├── Interfaces/ISelectionService.cs    ← 인터페이스 정의
├── Models/SelectionState.cs           ← 기존 유지 (변경 없음)
└── Models/CellCoord.cs                ← 기존 유지

GhostWin.Services (Application)
└── SelectionService.cs                ← 구현

GhostWin.App (Presentation)
└── Controls/TerminalHostControl.cs    ← WndProc → 서비스 위임만
```

### 2.2 의존성 흐름

```
TerminalHostControl ──uses──▶ ISelectionService
                                    │
                                    ▼
                              IEngineService (DI 주입)
```

### 2.3 소유권 모델

| 객체 | 소유자 | 생명주기 |
|------|--------|----------|
| `SelectionState` | `SelectionService` (per-instance) | Host 생성~파괴 |
| `SelectionService` 인스턴스 | `TerminalHostControl` (1:1) | Host 생성~파괴 |
| `IEngineService` | DI 컨테이너 (Singleton) | App 전체 |

> **생명주기 불일치 해결**: `SelectionService` 는 Singleton 이 **아니라** per-host Transient 팩토리로 생성.
> `TerminalHostControl` 은 `HwndHost` 이므로 pane split/close 마다 생성/파괴됨.
> Singleton 으로 만들면 여러 pane 의 selection 상태가 충돌.
> 팩토리 패턴: `Func<uint, ISelectionService>` 를 DI 에 등록, sessionId 로 인스턴스 생성.

---

## 3. 인터페이스 정의

### 3.1 ISelectionService (GhostWin.Core)

```csharp
namespace GhostWin.Core.Interfaces;

/// <summary>
/// Per-pane selection state machine.
/// All methods are synchronous (WndProc thread 에서 직접 호출).
/// </summary>
public interface ISelectionService
{
    /// <summary>현재 Selection 활성 여부.</summary>
    bool HasSelection { get; }

    /// <summary>정규화된 현재 선택 범위 (없으면 null).</summary>
    SelectionRange? CurrentRange { get; }

    /// <summary>
    /// 마우스 버튼 down 처리.
    /// click count 판별 + anchor 설정 + word/line 즉시 선택.
    /// </summary>
    /// <returns>SetCapture 필요 여부.</returns>
    bool OnMouseDown(short xPx, short yPx, uint sessionId);

    /// <summary>
    /// 마우스 이동 (드래그) 처리.
    /// deferred drag 시작 + selection 확장.
    /// </summary>
    void OnMouseMove(short xPx, short yPx, uint sessionId);

    /// <summary>
    /// 마우스 버튼 up 처리.
    /// 빈 selection (start==end) 이면 자동 클리어.
    /// </summary>
    /// <returns>ReleaseCapture 필요 여부.</returns>
    bool OnMouseUp(short xPx, short yPx, uint sessionId);

    /// <summary>Selection 해제.</summary>
    void ClearSelection();

    /// <summary>
    /// 현재 selection 범위의 텍스트를 engine 에서 읽어 반환.
    /// M-10.5 클립보드용.
    /// </summary>
    string GetSelectedText(uint sessionId);
}
```

### 3.2 이벤트 콜백 (서비스→호스트)

서비스가 selection 변경을 알릴 때 engine P/Invoke (DX11 overlay) + WPF 이벤트 를 모두 호출해야 합니다.
서비스는 `System.Windows` 를 참조할 수 없으므로 콜백으로 분리합니다.

```csharp
/// <summary>
/// Selection 범위가 변경될 때 호출되는 콜백.
/// TerminalHostControl 이 등록하여 DX11 SetSelection + WPF 이벤트 발행.
/// </summary>
public delegate void SelectionChangedCallback(SelectionRange? range);
```

이 콜백은 `SelectionService` 생성자에서 주입받습니다.

---

## 4. 구현 클래스 골격

### 4.1 SelectionService (GhostWin.Services)

```csharp
namespace GhostWin.Services;

using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

/// <summary>
/// Per-pane selection state machine. WndProc 스레드에서 동기 호출.
/// 외부 의존: IEngineService (셀 크기, 단어/줄 경계, 텍스트 읽기).
/// </summary>
public sealed class SelectionService : ISelectionService
{
    private readonly IEngineService _engine;
    private readonly SelectionChangedCallback? _onChanged;

    // ── 상태 ──
    private readonly SelectionState _state = new();
    private int _clickCount;
    private long _lastClickTicks;
    private short _lastClickX, _lastClickY;
    private int _anchorRow, _anchorCol;
    private short _anchorPxX, _anchorPxY;
    private bool _dragStarted;

    private const int ClickDistanceThreshold = 4; // px

    public SelectionService(IEngineService engine,
                            SelectionChangedCallback? onChanged = null)
    {
        _engine = engine;
        _onChanged = onChanged;
    }

    // ── ISelectionService ──

    public bool HasSelection => _state.IsActive;
    public SelectionRange? CurrentRange => _state.CurrentRange;

    public bool OnMouseDown(short xPx, short yPx, uint sessionId)
    {
        if (!PixelToCell(xPx, yPx, out int row, out int col))
            return false;

        int clickCount = UpdateClickCount(xPx, yPx);

        switch (clickCount)
        {
            case 1:
                HandleSingleClick(row, col, xPx, yPx);
                break;
            case 2:
                HandleWordClick(row, col, sessionId);
                break;
            case 3:
                HandleLineClick(row, sessionId);
                break;
        }

        NotifyChanged();
        return true; // caller: SetCapture
    }

    public void OnMouseMove(short xPx, short yPx, uint sessionId)
    {
        if (!PixelToCell(xPx, yPx, out int row, out int col))
            return;

        if (!_dragStarted)
        {
            if (!ShouldStartDrag(xPx, yPx)) return;
            _state.Start(_anchorRow, _anchorCol, SelectionMode.Cell);
            _dragStarted = true;
        }

        if (!_state.IsActive) return;

        ExtendByMode(row, col, sessionId);
        NotifyChanged();
    }

    public bool OnMouseUp(short xPx, short yPx, uint sessionId)
    {
        if (!_state.IsActive) return true;

        // start==end (클릭만, 드래그 없음) → 클리어
        if (_state.CurrentRange is { } r &&
            r.Start.Row == r.End.Row &&
            r.Start.Col == r.End.Col)
        {
            _state.Clear();
            NotifyChanged();
        }

        return true; // caller: ReleaseCapture
    }

    public void ClearSelection()
    {
        _state.Clear();
        NotifyChanged();
    }

    public string GetSelectedText(uint sessionId)
    {
        if (_state.CurrentRange is not { } range || !range.IsValid)
            return string.Empty;

        return _engine.GetSelectedText(sessionId,
            range.Start.Row, range.Start.Col,
            range.End.Row, range.End.Col);
    }

    // ── private: click handlers (각 10줄 이하) ──

    private void HandleSingleClick(int row, int col, short xPx, short yPx)
    {
        _state.Clear();
        _anchorRow = row;
        _anchorCol = col;
        _anchorPxX = xPx;
        _anchorPxY = yPx;
        _dragStarted = false;
    }

    private void HandleWordClick(int row, int col, uint sessionId)
    {
        var (wStart, wEnd) = _engine.FindWordBounds(sessionId, row, col);
        _state.Clear();
        _state.Start(row, wStart, SelectionMode.Word);
        _state.Extend(row, wEnd);
        _dragStarted = true;
    }

    private void HandleLineClick(int row, uint sessionId)
    {
        var (lStart, lEnd) = _engine.FindLineBounds(sessionId, row);
        _state.Clear();
        _state.Start(row, lStart, SelectionMode.Line);
        _state.Extend(row, lEnd);
        _dragStarted = true;
    }

    // ── private: drag helpers ──

    private bool ShouldStartDrag(short xPx, short yPx)
    {
        int dx = Math.Abs(xPx - _anchorPxX);
        int dy = Math.Abs(yPx - _anchorPxY);
        _engine.GetCellSize(out uint cw, out uint _);
        int threshold = Math.Max((int)cw / 4, 2);
        return dx >= threshold || dy >= threshold;
    }

    private void ExtendByMode(int row, int col, uint sessionId)
    {
        switch (_state.Mode)
        {
            case SelectionMode.Cell:
                _state.Extend(row, col);
                break;
            case SelectionMode.Word:
                var (_, wEnd) = _engine.FindWordBounds(sessionId, row, col);
                _state.Extend(row, wEnd);
                break;
            case SelectionMode.Line:
                var (_, lEnd) = _engine.FindLineBounds(sessionId, row);
                _state.Extend(row, lEnd);
                break;
        }
    }

    // ── private: coordinate conversion ──

    private bool PixelToCell(short xPx, short yPx, out int row, out int col)
    {
        _engine.GetCellSize(out uint cw, out uint ch);
        if (cw == 0 || ch == 0) { row = 0; col = 0; return false; }
        col = Math.Max(0, xPx / (int)cw);
        row = Math.Max(0, yPx / (int)ch);
        return true;
    }

    // ── private: click count state machine ──

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    private int UpdateClickCount(short x, short y)
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastClickTicks;
        int threshold = GetDoubleClickTime();

        int dx = Math.Abs(x - _lastClickX);
        int dy = Math.Abs(y - _lastClickY);

        _clickCount = (elapsed <= threshold &&
                       dx <= ClickDistanceThreshold &&
                       dy <= ClickDistanceThreshold)
            ? (_clickCount % 3) + 1
            : 1;

        _lastClickTicks = now;
        _lastClickX = x;
        _lastClickY = y;
        return _clickCount;
    }

    // ── private: notification ──

    private void NotifyChanged()
    {
        var range = _state.CurrentRange;

        // DX11 overlay (동기, WndProc 스레드)
        if (range is { } r && r.IsValid)
            _engine.SetSelection(0, r.Start.Row, r.Start.Col,
                r.End.Row, r.End.Col, true);
        else
            _engine.SetSelection(0, 0, 0, 0, 0, false);

        _onChanged?.Invoke(range);
    }
}
```

**참고**: `NotifyChanged` 에서 `SetSelection` 의 sessionId 인자는 `0` 으로 표기했지만, 실제 구현에서는 호출자가 sessionId 를 전달해야 합니다. 이 부분은 아래 §4.2 에서 해결합니다.

### 4.2 sessionId 전달 전략

`SelectionService` 는 per-pane 인스턴스이므로, 생성 시 sessionId 를 받아 필드로 보관할 수 있습니다.
그러나 session migration (pane split 시 sessionId 변경) 이 있으므로, 매 호출마다 caller 가 전달하는 현재 설계가 더 안전합니다.

`NotifyChanged` 에서 sessionId 가 필요한데 `OnMouseDown/Move/Up` 이 이미 sessionId 를 받으므로, 마지막 호출의 sessionId 를 필드에 캐싱합니다.

```csharp
private uint _lastSessionId;

public bool OnMouseDown(short xPx, short yPx, uint sessionId)
{
    _lastSessionId = sessionId;
    // ... 나머지 동일
}

private void NotifyChanged()
{
    // ... _lastSessionId 사용
    _engine.SetSelection(_lastSessionId, ...);
}
```

---

## 5. TerminalHostControl 수정 후 모습

### 5.1 필드 변경

```csharp
// 제거할 필드 (13줄)
// internal readonly SelectionState _selection = new();     ← 삭제
// private int _clickCount;                                  ← 삭제
// private long _lastClickTicks;                             ← 삭제
// private short _lastClickX, _lastClickY;                   ← 삭제
// private int _anchorRow, _anchorCol;                       ← 삭제
// private short _anchorX, _anchorY;                         ← 삭제
// private bool _dragStarted;                                ← 삭제

// 추가할 필드 (1줄)
internal ISelectionService? _selectionService;
```

### 5.2 WndProc HandleSelection 호출부 변경

**Before** (L210-213):
```csharp
if (result == GW_MOUSE_NOT_REPORTED || shiftHeld)
{
    HandleSelection(host, msg, x, y, button, action, mods);
}
```

**After**:
```csharp
if (result == GW_MOUSE_NOT_REPORTED || shiftHeld)
{
    if (host._selectionService == null) return DefWindowProc(hwnd, msg, wParam, lParam);

    if (msg == WM_LBUTTONDOWN)
    {
        if (host._selectionService.OnMouseDown(x, y, host.SessionId))
            SetCapture(host._childHwnd);
    }
    else if (msg == WM_MOUSEMOVE && (GetKeyState(VK_LBUTTON) & 0x8000) != 0)
    {
        host._selectionService.OnMouseMove(x, y, host.SessionId);
    }
    else if (msg == WM_LBUTTONUP)
    {
        if (host._selectionService.OnMouseUp(x, y, host.SessionId))
            ReleaseCapture();
    }
}
```

이것이 TerminalHostControl 에 남는 selection 관련 코드의 **전부**입니다 (약 15줄).

### 5.3 제거되는 메서드 (6개, 238줄)

| 메서드 | 줄 수 | 이동 위치 |
|--------|:----:|-----------|
| `PixelToCell` | 10 | `SelectionService.PixelToCell` |
| `UpdateClickCount` | 24 | `SelectionService.UpdateClickCount` |
| `FindWordBounds` | 48 | 제거 (이미 `IEngineService.FindWordBounds` 에 위임) |
| `IsWordChar` | 21 | 제거 (이미 engine 에 위임) |
| `FindLineBounds` | 15 | 제거 (이미 `IEngineService.FindLineBounds` 에 위임) |
| `HandleSelection` | 112 | `SelectionService.OnMouseDown/Move/Up` 으로 분해 |
| `NotifySelectionChanged` | 28 | `SelectionService.NotifyChanged` + callback |

### 5.4 NotifySelectionChanged 이벤트 흐름 변경

```
[Before]
WndProc → HandleSelection → NotifySelectionChanged
                               ├─ engine.SetSelection (동기)
                               └─ Dispatcher.BeginInvoke → SelectionChanged 이벤트

[After]
WndProc → selectionService.OnMouseDown/Move/Up
              ├─ engine.SetSelection (동기, 서비스 내부)
              └─ _onChanged callback
                    └─ Dispatcher.BeginInvoke → SelectionChanged 이벤트
```

callback 등록은 `PaneContainerControl.BuildElement` 에서 host 생성 직후:

```csharp
host._selectionService = new SelectionService(engine, range =>
{
    var paneId = host.PaneId;
    var hwnd = host._childHwnd;
    host.Dispatcher.BeginInvoke(() =>
    {
        if (_hostsByHwnd.TryGetValue(hwnd, out var live) &&
            live._childHwnd != IntPtr.Zero)
        {
            live.SelectionChanged?.Invoke(live,
                new SelectionChangedEventArgs(paneId, range));
        }
    });
});
```

---

## 6. DI 등록

### 6.1 팩토리 등록 (App.xaml.cs)

`SelectionService` 는 per-host 인스턴스이므로 Singleton 등록이 아닙니다.
`PaneContainerControl.BuildElement` 에서 직접 `new SelectionService(engine, callback)` 로 생성합니다.

DI 에 등록할 것은 없습니다. `ISelectionService` 인터페이스는 외부 접근용 (M-10.5 클립보드) 으로만 사용되며,
외부에서 접근할 때는 `TerminalHostControl._selectionService` 프로퍼티를 통해 가져옵니다.

### 6.2 M-10.5 클립보드 접근 경로

```
MainWindow (Ctrl+C keybinding)
  → WorkspaceService.GetFocusedPaneId()
  → PaneContainerControl.GetHostForPane(paneId)
  → host._selectionService.GetSelectedText(sessionId)
  → Clipboard.SetText(text)
```

이를 위해 `PaneContainerControl` 에 public 메서드 추가:

```csharp
public string? GetSelectedTextForFocusedPane()
{
    if (_focusedPaneId is not { } paneId) return null;
    if (!_hostControls.TryGetValue(paneId, out var host)) return null;
    if (host._selectionService is not { HasSelection: true } svc) return null;
    return svc.GetSelectedText(host.SessionId);
}
```

---

## 7. 파일 변경 목록

| # | 파일 | 변경 유형 | 줄 수 변경 (예상) |
|:-:|------|-----------|:-:|
| 1 | `src/GhostWin.Core/Interfaces/ISelectionService.cs` | **신규** | +35 |
| 2 | `src/GhostWin.Services/SelectionService.cs` | **신규** | +160 |
| 3 | `src/GhostWin.App/Controls/TerminalHostControl.cs` | 수정 | -250, +20 (순 -230) |
| 4 | `src/GhostWin.App/Controls/PaneContainerControl.cs` | 수정 | +15 (callback 등록 + GetSelectedText) |
| 5 | `src/GhostWin.Core/Models/SelectionState.cs` | 변경 없음 | 0 |
| | **합계** | | +230, -250 (순 -20) |

**줄 수 분포 결과**:
- `TerminalHostControl.cs`: 647줄 → ~417줄 (35% 감소)
- `SelectionService.cs`: ~160줄 (40줄 제한 내 메서드 7개)
- `ISelectionService.cs`: ~35줄

---

## 8. 위험 요소 + 완화 방안

### R1: WndProc 스레드 안전성

| 위험 | 영향 | 완화 |
|------|------|------|
| `SelectionService` 메서드가 WndProc 외 스레드에서 호출되면 `SelectionState` 경합 | 렌더링 깨짐, 크래시 | `SelectionService` 는 `sealed class` + 문서로 "WndProc 스레드에서만 호출" 명시. 현재도 동일 제약이므로 기존 동작 유지 |

### R2: HwndHost 생명주기 불일치

| 위험 | 영향 | 완화 |
|------|------|------|
| pane close 시 `TerminalHostControl.Dispose` 호출되지만 `SelectionService` 는 GC 까지 살아있음 | `IEngineService` 호출 시 이미 파괴된 session 참조 | `SelectionService` 에 `engine == null` guard 유지. `TerminalHostControl.DestroyWindowCore` 에서 `_selectionService = null` 설정 |

### R3: FindWordBounds/IsWordChar 중복 제거

| 위험 | 영향 | 완화 |
|------|------|------|
| `TerminalHostControl` 의 `FindWordBounds`/`IsWordChar` 는 `IEngineService.FindWordBounds` 와 중복 | M-10c 에서 이미 engine 에 grid-native API 를 추가했으나 C# 쪽에도 fallback 로 남아있음 | 서비스 전환 시 C# 쪽 fallback 완전 제거. engine API 만 사용 |

### R4: GetDoubleClickTime P/Invoke

| 위험 | 영향 | 완화 |
|------|------|------|
| `GetDoubleClickTime` P/Invoke 가 `SelectionService` (GhostWin.Services) 에 직접 존재 | Services 프로젝트에 `user32.dll` P/Invoke 의존성 추가 | 이미 `Environment.TickCount64` 도 사용 중. `GetDoubleClickTime` 단일 P/Invoke 는 허용 범위. 또는 생성자에서 값을 주입받는 방식으로 격리 가능 |

### R5: Regression — 기존 selection 동작 변경

| 위험 | 영향 | 완화 |
|------|------|------|
| 리팩토링 과정에서 click count / drag threshold / word boundary 동작 미세 변경 | 사용자 체감 selection 동작 이상 | 현재 코드를 1:1 이식 (로직 변경 없음). smoke test: single click → drag select → double click word → triple click line |

---

## 9. 구현 순서

1. `ISelectionService` 인터페이스 작성 (GhostWin.Core)
2. `SelectionService` 구현 (GhostWin.Services) — 현재 코드 1:1 이식
3. `TerminalHostControl` 에서 selection 필드/메서드 제거 + 서비스 위임 코드 추가
4. `PaneContainerControl.BuildElement` 에서 서비스 인스턴스 생성 + callback 등록
5. 빌드 + smoke test (single/double/triple click, drag, shift+click)
6. `PaneContainerControl.GetSelectedTextForFocusedPane` 추가 (M-10.5 준비)

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-11 | Initial draft | rkit-wpf-architect |
