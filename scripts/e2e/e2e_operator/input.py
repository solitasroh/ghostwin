"""Keyboard and mouse input injection for GhostWin E2E scenarios.

Key injection design (Design D13-D14):
    pywinauto type_keys() uses SendInput + scan codes, which is more reliable
    than the deprecated keybd_event API used by pyautogui.

    GhostWin's MainWindow.OnTerminalKeyDown handles keys at Window level via
    PreviewKeyDown, so HwndHost child focus is NOT required.  Window.focus()
    (from window.py) is sufficient before calling send_keys().

Key mapping (Design D14) — pywinauto notation:
    Key combination         pywinauto string    WPF handler
    -----------------       ----------------    -----------------------
    Alt+V                   '%v'                SplitFocused(Vertical)
    Alt+H                   '%h'                SplitFocused(Horizontal)
    Ctrl+T                  '^t'                CreateWorkspace()
    Ctrl+W                  '^w'                CloseWorkspace()
    Ctrl+Shift+W            '^+w'               CloseFocused() pane
    Alt+Left                '%{LEFT}'           MoveFocus(Left)
    Alt+Right               '%{RIGHT}'          MoveFocus(Right)
    Alt+Up                  '%{UP}'             MoveFocus(Up)
    Alt+Down                '%{DOWN}'           MoveFocus(Down)

Source verified: MainWindow.xaml.cs:217-303 (2026-04-08)

References:
    docs/02-design/features/e2e-test-harness.design.md §2.3 D13/D14, §10 R4/R11
    src/GhostWin.App/MainWindow.xaml.cs:212-329 OnTerminalKeyDown
    pywinauto docs: https://pywinauto.readthedocs.io/en/latest/code/pywinauto.keyboard.html
"""
import ctypes
import logging
import time
from ctypes import wintypes
from typing import Literal

from pywinauto import Application

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Key mapping reference (documentation only — not used at runtime)
# ---------------------------------------------------------------------------

KEY_MAP: dict[str, str] = {
    "alt+v":        "%v",
    "alt+h":        "%h",
    "ctrl+t":       "^t",
    "ctrl+w":       "^w",
    "ctrl+shift+w": "^+w",
    "alt+left":     "%{LEFT}",
    "alt+right":    "%{RIGHT}",
    "alt+up":       "%{UP}",
    "alt+down":     "%{DOWN}",
}


# ---------------------------------------------------------------------------
# Keyboard injection
# ---------------------------------------------------------------------------

def _make_keybd_input(vk: int, key_up: bool) -> "_INPUT":
    """Build a single keyboard INPUT struct for SendInput."""
    inp = _INPUT()
    inp.type = _INPUT_KEYBOARD
    inp.ki.wVk = vk
    inp.ki.wScan = 0
    flags = 0
    if key_up:
        flags |= _KEYEVENTF_KEYUP
    if vk in _EXTENDED_VKS:
        flags |= _KEYEVENTF_EXTENDEDKEY
    inp.ki.dwFlags = flags
    inp.ki.time = 0
    inp.ki.dwExtraInfo = None
    return inp


def send_keys(hwnd: int, keys: str, pause: float = 0.05) -> None:
    """Inject *keys* into the foreground window via ctypes SendInput batch.

    All key down events (in order) followed by all key up events (reverse order)
    are submitted as a SINGLE SendInput call. This is atomic from the OS
    perspective — no other process can interleave between modifier-down and
    key-down, which is the root cause that broke the previous pywinauto-based
    implementations.

    Caller MUST have already called window.focus(hwnd) before this function so
    that GhostWin is the foreground window. The hwnd argument is kept for API
    compatibility but is unused at this layer (SendInput targets foreground).

    History (R4 fix, 2026-04-08):
        Attempt 1: Application(backend='uia').connect + window.type_keys —
                   Alt+V/H worked but Ctrl+T / Ctrl+Shift+W silently failed.
        Attempt 2: pywinauto.keyboard.send_keys (standalone) — same failure
                   pattern as attempt 1.
        Attempt 3 (current): direct ctypes SendInput batch with pre-baked VK
                   sequences. Atomic submission eliminates any race between
                   modifier and key events.

    Args:
        hwnd:  Top-level GhostWin window handle (unused, API compat only).
        keys:  pywinauto key string. Must be a key in _KEY_VK_SEQ — see the
               module-level table for the full mapping. Free-form text input
               is NOT supported (we only need fixed scenario chords).
        pause: Seconds to wait after the SendInput call returns. Lets WPF
               OnTerminalKeyDown finish dispatching before the next call.

    Raises:
        ValueError: if `keys` is not in _KEY_VK_SEQ.
        OSError:    if SendInput injects fewer events than requested.

    References:
        Design D13/D14, §10 R4 mitigation
        MSDN: SendInput
              https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
    """
    seq = _KEY_VK_SEQ.get(keys)
    if seq is None:
        raise ValueError(f"send_keys: unsupported key string {keys!r}; "
                         f"add it to _KEY_VK_SEQ in input.py")

    user32 = ctypes.windll.user32
    logger.debug("send_keys: hwnd=0x%08X keys=%r vks=%s", hwnd, keys, seq)

    events: list[_INPUT] = []
    # Press in declared order (modifiers first)
    for vk in seq:
        events.append(_make_keybd_input(vk, key_up=False))
    # Release in reverse order (key first, then modifiers)
    for vk in reversed(seq):
        events.append(_make_keybd_input(vk, key_up=True))

    array_type = _INPUT * len(events)
    inputs = array_type(*events)
    sent = user32.SendInput(len(events), inputs, ctypes.sizeof(_INPUT))
    if sent != len(events):
        err = ctypes.get_last_error()
        logger.warning(
            "send_keys: SendInput injected %d/%d events (WinError %d); "
            "falling back to PostMessage — works without foreground but less accurate",
            sent, len(events), err,
        )
        _post_message_chord(hwnd, seq)

    if pause > 0:
        time.sleep(pause)


# WM_*KEYDOWN/UP constants
_WM_KEYDOWN    = 0x0100
_WM_KEYUP      = 0x0101
_WM_SYSKEYDOWN = 0x0104
_WM_SYSKEYUP   = 0x0105


def _post_message_chord(hwnd: int, seq: list[int]) -> None:
    """Fallback key injection via PostMessage to a specific HWND.

    Works without foreground/visibility requirements (unlike SendInput).
    Used when SendInput fails with WinError 0 in non-interactive sessions
    (e.g. Claude Code bash) where the target window cannot become
    foreground.

    Caveats vs SendInput:
      - Does NOT update GetAsyncKeyState / global keyboard state.
      - WPF InputManager still processes these because HwndSource's WndProc
        dispatches WM_KEYDOWN/WM_SYSKEYDOWN through the input system,
        which updates Keyboard.Modifiers for PreviewKeyDown handlers.
      - Context bit (Alt-held indicator) is set manually on SYS* messages.

    Args:
        hwnd: Target window (top-level MainWindow).
        seq:  Virtual-key codes in press order (modifiers first, key last).
    """
    user32 = ctypes.windll.user32

    uses_alt = _VK_MENU in seq
    keydown_msg = _WM_SYSKEYDOWN if uses_alt else _WM_KEYDOWN
    keyup_msg   = _WM_SYSKEYUP   if uses_alt else _WM_KEYUP

    # lParam for WM_KEYDOWN / WM_SYSKEYDOWN:
    #   bits 0-15  : repeat count (1)
    #   bits 16-23 : scan code (unused here, 0)
    #   bit 24     : extended key flag
    #   bits 25-28 : reserved
    #   bit 29     : context code (Alt held) — set for SYSKEYDOWN of chord keys
    #   bit 30     : previous key state (0 = up)
    #   bit 31     : transition state (0 = down)
    #
    # For WM_*KEYUP, set bit 30 (previous=down) and bit 31 (transition=up).

    def _lparam_down(vk: int, alt_context: bool) -> int:
        val = 1  # repeat count
        if vk in _EXTENDED_VKS:
            val |= (1 << 24)
        if alt_context and vk != _VK_MENU:
            val |= (1 << 29)  # Alt held context
        return val

    def _lparam_up(vk: int, alt_context: bool) -> int:
        val = 1
        if vk in _EXTENDED_VKS:
            val |= (1 << 24)
        if alt_context and vk != _VK_MENU:
            val |= (1 << 29)
        val |= (1 << 30)  # previous down
        val |= (1 << 31)  # transition up
        return val

    # Press in declared order (modifiers first)
    for vk in seq:
        ok = user32.PostMessageW(hwnd, keydown_msg, vk, _lparam_down(vk, uses_alt))
        if not ok:
            err = ctypes.get_last_error()
            raise OSError(f"PostMessage WM_*KEYDOWN failed for vk=0x{vk:02X}: WinError {err}")
        time.sleep(0.01)

    # Release in reverse order
    for vk in reversed(seq):
        ok = user32.PostMessageW(hwnd, keyup_msg, vk, _lparam_up(vk, uses_alt))
        if not ok:
            err = ctypes.get_last_error()
            raise OSError(f"PostMessage WM_*KEYUP failed for vk=0x{vk:02X}: WinError {err}")
        time.sleep(0.01)


# ---------------------------------------------------------------------------
# Mouse click injection
# ---------------------------------------------------------------------------

# Win32 MOUSEINPUT constants
# MSDN: https://docs.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
_MOUSEEVENTF_MOVE        = 0x0001
_MOUSEEVENTF_LEFTDOWN    = 0x0002
_MOUSEEVENTF_LEFTUP      = 0x0004
_MOUSEEVENTF_RIGHTDOWN   = 0x0008
_MOUSEEVENTF_RIGHTUP     = 0x0010
_MOUSEEVENTF_ABSOLUTE    = 0x8000

# SendInput INPUT type
_INPUT_MOUSE    = 0
_INPUT_KEYBOARD = 1

# KEYBDINPUT dwFlags
_KEYEVENTF_EXTENDEDKEY = 0x0001
_KEYEVENTF_KEYUP       = 0x0002
_KEYEVENTF_UNICODE     = 0x0004
_KEYEVENTF_SCANCODE    = 0x0008

# Virtual key codes (subset used by GhostWin scenarios)
# https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes
_VK_SHIFT     = 0x10
_VK_CONTROL   = 0x11
_VK_MENU      = 0x12  # Alt
_VK_LEFT      = 0x25
_VK_UP        = 0x26
_VK_RIGHT     = 0x27
_VK_DOWN      = 0x28
_VK_H         = 0x48
_VK_T         = 0x54
_VK_V         = 0x56
_VK_W         = 0x57

# pywinauto-style key string → ordered list of virtual-key codes (modifiers first)
# Each entry: list of VKs that compose the chord. Press order = list order;
# release order = reverse. This produces a single SendInput batch atomically.
_KEY_VK_SEQ: dict[str, list[int]] = {
    "%v":      [_VK_MENU,    _VK_V],         # Alt+V
    "%h":      [_VK_MENU,    _VK_H],         # Alt+H
    "^t":      [_VK_CONTROL, _VK_T],         # Ctrl+T
    "^w":      [_VK_CONTROL, _VK_W],         # Ctrl+W
    "^+w":     [_VK_CONTROL, _VK_SHIFT, _VK_W],   # Ctrl+Shift+W
    "%{LEFT}":  [_VK_MENU, _VK_LEFT],
    "%{RIGHT}": [_VK_MENU, _VK_RIGHT],
    "%{UP}":    [_VK_MENU, _VK_UP],
    "%{DOWN}":  [_VK_MENU, _VK_DOWN],
}

# VKs that need KEYEVENTF_EXTENDEDKEY (arrows, ins, del, etc.)
_EXTENDED_VKS = {_VK_LEFT, _VK_UP, _VK_RIGHT, _VK_DOWN}

# Screen dimensions for MOUSEEVENTF_ABSOLUTE coordinate normalisation.
# ABSOLUTE coords are in [0, 65535] mapped to full virtual screen.
_SM_CXVIRTUALSCREEN = 78
_SM_CYVIRTUALSCREEN = 79
_SM_XVIRTUALSCREEN  = 76
_SM_YVIRTUALSCREEN  = 77


class _MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx",          ctypes.c_long),
        ("dy",          ctypes.c_long),
        ("mouseData",   ctypes.c_ulong),
        ("dwFlags",     ctypes.c_ulong),
        ("time",        ctypes.c_ulong),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk",         ctypes.c_ushort),
        ("wScan",       ctypes.c_ushort),
        ("dwFlags",     ctypes.c_ulong),
        ("time",        ctypes.c_ulong),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", _KEYBDINPUT), ("mi", _MOUSEINPUT)]


class _INPUT(ctypes.Structure):
    # _anonymous_ MUST be declared before _fields_ so the union fields
    # (mi.dx, mi.dy, ...) become directly accessible from the parent
    # struct via inp.mi.dx instead of inp._input.mi.dx.
    # See: https://docs.python.org/3/library/ctypes.html#ctypes.Structure._anonymous_
    _anonymous_ = ("_input",)
    _fields_ = [("type", ctypes.c_ulong), ("_input", _INPUT_UNION)]


def click_at(
    hwnd: int,
    client_x: int,
    client_y: int,
    button: Literal["left", "right"] = "left",
) -> None:
    """Click at (client_x, client_y) relative to the client area of *hwnd*.

    Converts client coordinates to screen coordinates via ClientToScreen, then
    injects a mouse move + button down + button up sequence via SendInput.

    This function is used by MQ-4 (mouse focus scenario).

    Args:
        hwnd:     Target window handle (used only for coordinate conversion).
        client_x: X coordinate in the window's client space.
        client_y: Y coordinate in the window's client space.
        button:   'left' (default) or 'right'.

    Raises:
        NotImplementedError: for buttons other than 'left' or 'right'.
        OSError:             if SendInput fails (WinError from GetLastError).

    References:
        MSDN: SendInput
              https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
        MSDN: ClientToScreen
              https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen
    """
    if button not in ("left", "right"):
        raise NotImplementedError(f"click_at: unsupported button {button!r}")

    user32 = ctypes.windll.user32

    # Convert client → screen coordinates
    pt = wintypes.POINT(client_x, client_y)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    screen_x, screen_y = pt.x, pt.y
    logger.debug(
        "click_at: client (%d,%d) → screen (%d,%d) button=%s",
        client_x, client_y, screen_x, screen_y, button,
    )

    # Normalise to [0, 65535] for MOUSEEVENTF_ABSOLUTE
    # Virtual screen origin and size handle multi-monitor setups
    vscreen_left   = user32.GetSystemMetrics(_SM_XVIRTUALSCREEN)
    vscreen_top    = user32.GetSystemMetrics(_SM_YVIRTUALSCREEN)
    vscreen_width  = user32.GetSystemMetrics(_SM_CXVIRTUALSCREEN)
    vscreen_height = user32.GetSystemMetrics(_SM_CYVIRTUALSCREEN)
    abs_x = int((screen_x - vscreen_left) * 65535 / vscreen_width)
    abs_y = int((screen_y - vscreen_top)  * 65535 / vscreen_height)

    if button == "left":
        down_flag = _MOUSEEVENTF_LEFTDOWN
        up_flag   = _MOUSEEVENTF_LEFTUP
    else:
        down_flag = _MOUSEEVENTF_RIGHTDOWN
        up_flag   = _MOUSEEVENTF_RIGHTUP

    def _make_input(flags: int, dx: int = abs_x, dy: int = abs_y) -> _INPUT:
        inp = _INPUT()
        inp.type = _INPUT_MOUSE
        inp.mi.dx = dx
        inp.mi.dy = dy
        inp.mi.mouseData = 0
        inp.mi.dwFlags = flags
        inp.mi.time = 0
        inp.mi.dwExtraInfo = None
        return inp

    # Move to target, then press and release
    events = [
        _make_input(_MOUSEEVENTF_MOVE | _MOUSEEVENTF_ABSOLUTE),
        _make_input(down_flag | _MOUSEEVENTF_ABSOLUTE),
        _make_input(up_flag   | _MOUSEEVENTF_ABSOLUTE),
    ]
    array_type = _INPUT * len(events)
    inputs = array_type(*events)
    sent = user32.SendInput(len(events), inputs, ctypes.sizeof(_INPUT))
    if sent != len(events):
        err = ctypes.get_last_error()
        raise OSError(f"SendInput: only {sent}/{len(events)} events injected: WinError {err}")

    time.sleep(0.05)  # brief settle for WPF hit-test processing
