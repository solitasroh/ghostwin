# R4 Pass 1 Analysis — Gate G1 Reached

**Date**: 2026-04-08
**Phase**: e2e-ctrl-key-injection Do — Steps 1-4 complete
**Evidence files** (this directory):
- `baseline_hardware.log` — 14 entries (5 hardware key chords by user)
- `sendinput_alt_v.log` — 2 entries (e2e MQ-2 Alt+V via SendInput batch)
- `sendinput_ctrl_t.log` — 1 entry (e2e MQ-6 Ctrl+T via SendInput batch)

## Critical comparison

| Source | Key chord | Entries | First entry summary |
|---|---|:---:|---|
| Hardware | Ctrl+T | 2 (#0008-0009) | `key=LeftCtrl mods=Control isCtrlDown_kbd=true isCtrlDown_win32=true` |
| Hardware | Alt+V | 2 (#0001-0003 area) | `key=System syskey=V mods=Alt` |
| SendInput | Alt+V | 2 (#0001-0002) | `key=System syskey=V mods=Alt` ✅ matches hardware |
| **SendInput** | **Ctrl+T** | **1 (#0001 only)** | **`key=System syskey=LeftAlt mods=Alt isCtrlDown_kbd=false isCtrlDown_win32=false`** ⚠️ |

## Hypothesis falsification

| H | Plan | Design | Pass 1 | Status |
|---|:---:|:---:|:---:|---|
| H1 Modifier state race | 30% | 32% | 20% | Weakened — symptom is stuck modifier, not race |
| **H2 HwndHost Ctrl absorption** | 35% | 40% | **5%** | **FALSIFIED** — SendInput Ctrl+T DOES reach PreviewKeyDown (1 entry exists) |
| H3 dual dispatch conflict | 20% | 15% | 5% | Weakened — only 1 entry, dual dispatch not implicated |
| H4 P0-2 regression | 10% | 8% | 5% | Weakened — KeyDiag fires reliably for hardware on same build |
| H5 UIPI mismatch | 5% | 5% | 0% | **FALSIFIED** — entry exists, UIPI does not block |
| **H6 (NEW) window.focus() Alt-tap stuck** | — | — | **65%** | **NEW top suspect** |

## H6 — `window.focus()` Alt-tap leaves Alt key stuck

**Code site**: `scripts/e2e/e2e_operator/window.py:147-186`

```python
def focus(hwnd: int, retries: int = 3) -> None:
    _user32.keybd_event(VK_MENU, 0, 0, 0)                   # Alt key down
    _user32.SetForegroundWindow(hwnd)
    _user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)     # Alt key up
    _user32.BringWindowToTop(hwnd)
    _user32.SetActiveWindow(hwnd)
    ...
```

**Empirical signature in `sendinput_ctrl_t.log`**:
```
[#0001|evt=ENTRY key=System syskey=LeftAlt mods=Alt
       osrc=MainWindow foc=MainWindow ws=1 pane=1
       isCtrlDown_kbd=false isCtrlDown_win32=false]
```

**Decoding**:
- `key=System` + `syskey=LeftAlt` → WPF received WM_SYSKEYDOWN with VK=LeftAlt. This is the **Alt-down event** of the focus()'s Alt-tap.
- `mods=Alt` → `Keyboard.Modifiers` is Alt. Alt is currently held down.
- `isCtrlDown_kbd=false` and `isCtrlDown_win32=false` → Ctrl is NOT down. Both WPF KeyboardDevice and raw Win32 GetKeyState agree.
- Only ONE entry — the subsequent Ctrl+T SendInput sequence (Ctrl down, T down, T up, Ctrl up) produced **zero additional entries**. This means after the Alt-down event, OnTerminalKeyDown was never called again for the rest of the sequence.

**Why does send_keys('^t') produce ZERO additional events?**

Hypothesis (high confidence): Alt is held down due to keybd_event's KEYEVENTF_KEYUP not actually releasing it. The most likely reason is **ctypes prototype mismatch**:

```python
_user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)
```

Without explicit `argtypes`, ctypes assumes `c_int` (4 bytes) for every argument. The Win32 signature is:

```c
void keybd_event(BYTE bVk, BYTE bScan, DWORD dwFlags, ULONG_PTR dwExtraInfo);
```

On x64 Windows, `ULONG_PTR` is 8 bytes. ctypes is sending 4-byte 0 for `dwExtraInfo`, which causes a stack-frame misalignment that may scramble `dwFlags` (KEYEVENTF_KEYUP=0x0002). If `dwFlags` is corrupted to 0, the function interprets the call as another **Alt key DOWN**, not UP. The Alt key never releases. Subsequent SendInput keys are interpreted by Windows as Alt+chord, and the WPF SystemKey path swallows them at a different stage.

Note: this is a **strong candidate** but still a hypothesis — confirmation requires either (a) fixing the ctypes prototype and re-running, or (b) instrumenting the actual win32 GetKeyState immediately after focus() returns to see whether Alt is in the down state.

## Recommended next-step fix branch (Design §6 Fix Decision Tree)

Most promising fix order:

1. **Fix A — defensive modifier release in send_keys prologue** (lowest risk, ~10 LOC):
   - At the top of `input.py::send_keys()`, before injecting the requested chord, send a SendInput batch that releases LeftAlt, RightAlt, LeftCtrl, RightCtrl, LeftShift, RightShift. If they're not down, the OS treats it as a no-op.
   - This neutralizes any stuck modifier from `window.focus()` regardless of root cause.

2. **Fix B — strict ctypes prototypes for keybd_event** (~5 LOC, addresses root cause):
   - Add `_user32.keybd_event.argtypes = [BYTE, BYTE, DWORD, c_void_p]` and `_user32.keybd_event.restype = None` at module load.
   - Forces ctypes to marshal `dwExtraInfo` as a pointer-sized value.

3. **Fix C — replace keybd_event with SendInput in window.focus()** (~30 LOC):
   - keybd_event is officially deprecated. Use SendInput batch (Alt down + Alt up) for the focus() Alt-tap, with a single atomic call.

**Recommendation**: Apply Fix A and Fix B together. They are orthogonal and additive — Fix B addresses the immediate root cause if the prototype theory is correct, while Fix A protects against any future regression where a modifier could leak from any source. Combined LOC ~15.

## Gate G1 verdict

- **G1 PASS**: root cause hypothesis (H6) has strong empirical support
- **Next action**: implement Fix A + Fix B in `input.py`/`window.py`, rebuild, re-run `diag_e2e_mq6.ps1`, expect log entry like `key=T mods=Control isCtrlDown=true`
- **Awaiting user approval**: Step 5 (apply fixes) per design §8 G2 gate (≤30 LOC, no council needed since fix scope is small and low-risk)
