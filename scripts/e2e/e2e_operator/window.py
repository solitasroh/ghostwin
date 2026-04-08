"""Win32 window discovery, focus, geometry, and child-HWND helpers.

All coordinate-sensitive APIs (GetWindowRect, SetWindowPos) require the
calling process to be PerMonitorV2 DPI-aware.  Call dpi.set_per_monitor_v2()
before using any function in this module.

References:
    docs/02-design/features/e2e-test-harness.design.md §2.3 D11-D15
    scripts/e2e/capture_poc.py  — empirically verified find_top_level_hwnd_by_pid,
                                   force_foreground patterns (2026-04-08)
    poc_artifacts/poc_summary.json:
        GhostWin native child class = 'GhostWinTermChild' (confirmed 2026-04-08)
"""
import ctypes
import logging
import time
from ctypes import wintypes
from typing import Optional

logger = logging.getLogger(__name__)

# Win32 constants
VK_MENU = 0x12            # Virtual-key code for the Alt key
KEYEVENTF_KEYUP = 0x0002  # SendInput / keybd_event flag: key release
GW_OWNER = 4              # GetWindow nCmd: get owner window

# SetWindowPos flags
HWND_TOP = 0              # Place window at top of Z-order
SWP_NOZORDER = 0x0004     # Retain current Z-order
SWP_FRAMECHANGED = 0x0020 # Forces a WM_NCCALCSIZE message (refreshes frame)

# SendInput INPUT.type discriminant
_INPUT_KEYBOARD = 1

# ctypes callback type for EnumWindows / EnumChildWindows
_WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

_user32 = ctypes.windll.user32


# ---------------------------------------------------------------------------
# Self-contained SendInput Alt-tap (Exp-D1, 2026-04-08)
# ---------------------------------------------------------------------------
#
# Replaces the deprecated keybd_event(VK_MENU, ...) call pattern in focus().
#
# Empirical evidence (Exp-C, scripts/diag_exp_c_raw_sendinput.ps1):
#   When focus() is bypassed entirely, raw PowerShell SendInput Ctrl+T reaches
#   GhostWin's PreviewKeyDown handler with byte-for-byte the same KeyDiag log
#   pattern as a hardware key press. The contaminator is somewhere inside
#   focus() — Exp-D1 isolates whether it is the keybd_event API itself.
#
# We deliberately keep this self-contained (separate _INPUT struct from
# input.py) so window.py has zero coupling to the e2e injector module — focus
# is a pre-injection responsibility and must not pull pywinauto in transitively.

class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk",         ctypes.c_ushort),
        ("wScan",       ctypes.c_ushort),
        ("dwFlags",     ctypes.c_ulong),
        ("time",        ctypes.c_ulong),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _MOUSEINPUT(ctypes.Structure):
    """Sized union sibling — keyboard events do not use it but the OS expects
    INPUT to be the largest of (mouse, keyboard, hardware) bytes."""
    _fields_ = [
        ("dx",          ctypes.c_long),
        ("dy",          ctypes.c_long),
        ("mouseData",   ctypes.c_ulong),
        ("dwFlags",     ctypes.c_ulong),
        ("time",        ctypes.c_ulong),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", _KEYBDINPUT), ("mi", _MOUSEINPUT)]


class _INPUT(ctypes.Structure):
    _anonymous_ = ("_input",)
    _fields_ = [("type", ctypes.c_ulong), ("_input", _INPUT_UNION)]


def _make_alt_event(key_up: bool) -> "_INPUT":
    """Build a single SendInput keyboard event for VK_MENU (Alt)."""
    inp = _INPUT()
    inp.type = _INPUT_KEYBOARD
    inp.ki.wVk = VK_MENU
    inp.ki.wScan = 0
    inp.ki.dwFlags = KEYEVENTF_KEYUP if key_up else 0
    inp.ki.time = 0
    inp.ki.dwExtraInfo = None
    return inp


def _send_alt(key_up: bool) -> None:
    """Inject a single Alt down or Alt up via SendInput.

    Replaces keybd_event(VK_MENU, 0, flags, 0) — keybd_event is officially
    deprecated and Exp-D1 tests whether it is the contaminator that prevents
    subsequent SendInput Ctrl-chords from reaching WPF's PreviewKeyDown.
    """
    event = _make_alt_event(key_up)
    array_type = _INPUT * 1
    inputs = array_type(event)
    sent = _user32.SendInput(1, inputs, ctypes.sizeof(_INPUT))
    if sent != 1:
        err = ctypes.get_last_error()
        logger.warning("_send_alt(key_up=%s): SendInput sent %d/1 (WinError %d)",
                       key_up, sent, err)


# ---------------------------------------------------------------------------
# Discovery
# ---------------------------------------------------------------------------

def find_top_level_hwnd_by_pid(pid: int) -> int | None:
    """Return the first visible top-level HWND owned by *pid*, or None.

    Implementation: EnumWindows + GetWindowThreadProcessId match.  Only
    windows that pass IsWindowVisible AND have no owner (GW_OWNER == 0)
    are considered top-level application windows.

    This is the primary discovery method (Design D11).  Title-based lookup is
    intentionally avoided because GhostWin may acquire variable titles once
    workspace-title mirroring is implemented.

    Adapted verbatim from scripts/e2e/capture_poc.py (empirically verified
    2026-04-08).

    MSDN: EnumWindows
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows
    """
    found: list[int] = []

    def _callback(hwnd: int, _lparam: int) -> bool:
        owner_pid = wintypes.DWORD(0)
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner_pid))
        if owner_pid.value == pid and _user32.IsWindowVisible(hwnd):
            # Exclude owned popup windows (dialogs, tooltips).
            if _user32.GetWindow(hwnd, GW_OWNER) == 0:
                found.append(hwnd)
                return False  # stop enumeration
        return True

    _user32.EnumWindows(_WNDENUMPROC(_callback), 0)
    return found[0] if found else None


def wait_for_window(pid: int, timeout_s: float = 10.0) -> int:
    """Poll find_top_level_hwnd_by_pid every 50 ms until a HWND is found.

    Returns:
        int: the HWND value (always > 0)

    Raises:
        TimeoutError: if no visible HWND is found within *timeout_s* seconds.
    """
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        hwnd = find_top_level_hwnd_by_pid(pid)
        if hwnd:
            logger.debug("wait_for_window: found HWND 0x%08X for pid %d", hwnd, pid)
            return hwnd
        time.sleep(0.05)
    raise TimeoutError(
        f"No visible top-level HWND for pid {pid} within {timeout_s}s"
    )


# ---------------------------------------------------------------------------
# Title / geometry
# ---------------------------------------------------------------------------

def get_window_title(hwnd: int) -> str:
    """Return the window title via GetWindowTextW (max 256 wide chars).

    MSDN: GetWindowTextW
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowtextw
    """
    buf = ctypes.create_unicode_buffer(256)
    _user32.GetWindowTextW(hwnd, buf, 256)
    return buf.value


def get_window_rect(hwnd: int) -> tuple[int, int, int, int]:
    """Return (left, top, right, bottom) in screen coordinates.

    Requires PerMonitorV2 DPI awareness — call dpi.set_per_monitor_v2() first.

    MSDN: GetWindowRect
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect
    """
    rect = wintypes.RECT()
    _user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def get_client_rect(hwnd: int) -> tuple[int, int, int, int]:
    """Return the client area in screen coordinates as (left, top, width, height).

    Internally: GetClientRect (relative to client origin) + ClientToScreen
    (maps origin to screen).

    MSDN: ClientToScreen
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen
    """
    rect = wintypes.RECT()
    _user32.GetClientRect(hwnd, ctypes.byref(rect))
    # rect.left/top are 0 from GetClientRect; ClientToScreen maps the origin.
    pt = wintypes.POINT(rect.left, rect.top)
    _user32.ClientToScreen(hwnd, ctypes.byref(pt))
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    return pt.x, pt.y, width, height


# ---------------------------------------------------------------------------
# Focus
# ---------------------------------------------------------------------------

def focus(hwnd: int, retries: int = 3) -> None:
    """Bring *hwnd* to the foreground, bypassing Windows 11 focus-stealing prevention.

    Uses the "Alt-tap trick": briefly injecting a VK_MENU (Alt) press/release
    causes Windows to consider the calling thread as having received input, which
    satisfies the foreground lock requirement.

    Implementation matches Design D12 exactly.
    Adapted from scripts/e2e/capture_poc.py::force_foreground (2026-04-08).

    Args:
        hwnd:    Target window handle.
        retries: Number of GetForegroundWindow verification attempts after the
                 initial Alt-tap sequence.  Each attempt waits 100 ms.

    Raises:
        RuntimeError: if the window does not become foreground within *retries*
                      attempts.

    References:
        MSDN: SetForegroundWindow
              https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
        Design D12 — Alt-tap trick for Win11 focus stealing prevention
        Risk R12 — fallback: AttachThreadInput (not yet implemented; add if needed)
        Exp-D1 (2026-04-08): Alt-tap migrated from deprecated keybd_event to
            SendInput to test whether keybd_event is the e2e R4 contaminator
            — FALSIFIED, the SendInput Alt-tap reproduces the same single-entry
            failure pattern.
        Exp-D2a (2026-04-08): Alt-tap REMOVED entirely. New top hypothesis (H9)
            is that the Alt single-tap activates WPF Window's System Menu
            accelerator, which then swallows every subsequent SendInput chord.
            launch_app() launches GhostWin via subprocess.Popen so the spawned
            process is already foreground; SetForegroundWindow + retry loop is
            sufficient to reaffirm the foreground state without injecting Alt.
    """
    _user32.SetForegroundWindow(hwnd)
    _user32.BringWindowToTop(hwnd)
    _user32.SetActiveWindow(hwnd)
    time.sleep(0.05)  # brief settle before polling
    for attempt in range(retries):
        if _user32.GetForegroundWindow() == hwnd:
            logger.debug("focus: HWND 0x%08X foreground confirmed (attempt %d)", hwnd, attempt)
            return
        time.sleep(0.1)
    raise RuntimeError(
        f"Failed to bring HWND 0x{hwnd:08X} to foreground after {retries} retries. "
        "Consider AttachThreadInput fallback (Risk R12)."
    )


# ---------------------------------------------------------------------------
# Geometry normalization
# ---------------------------------------------------------------------------

def normalize(
    hwnd: int,
    x: int = 100,
    y: int = 100,
    width: int = 1280,
    height: int = 800,
) -> None:
    """Move and resize *hwnd* to a known baseline geometry via SetWindowPos.

    Design D15: fixed (100, 100, 1280, 800) for monitor-independence and
    PerMonitorV2 virtualization avoidance.

    IMPORTANT: call this AFTER the window is fully visible.  MainWindow.xaml
    OnSourceInitialized runs RestoreWindowBounds which restores persisted size/
    position.  If normalize() runs before that completes, RestoreWindowBounds
    will overwrite the normalized geometry.  Caller must ensure the window is
    visible (Tier A readiness) before calling this function.

    References:
        Design D15, Risk R6
        MainWindow.xaml lines 36-61 RestoreWindowBounds in OnSourceInitialized
        MSDN: SetWindowPos
              https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
    """
    _user32.SetWindowPos(
        hwnd,
        HWND_TOP,
        x,
        y,
        width,
        height,
        SWP_NOZORDER | SWP_FRAMECHANGED,
    )
    logger.debug(
        "normalize: HWND 0x%08X positioned to (%d,%d) %dx%d", hwnd, x, y, width, height
    )


# ---------------------------------------------------------------------------
# Child HWND discovery
# ---------------------------------------------------------------------------

def find_native_child(hwnd: int, child_class_name: str) -> int | None:
    """Return the first descendant HWND whose Win32 class name matches *child_class_name*.

    Used by the capture layer when WGC needs to fall back to a child HWND.
    Per poc_summary.json (2026-04-08), GhostWin's native child class is
    'GhostWinTermChild'.

    Args:
        hwnd:             Parent window handle.
        child_class_name: Exact Win32 class name to match (case-sensitive).

    Returns:
        HWND of the first matching child, or None if not found.

    MSDN: EnumChildWindows
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumchildwindows
    """
    found: list[int] = []

    def _callback(child_hwnd: int, _lparam: int) -> bool:
        buf = ctypes.create_unicode_buffer(256)
        _user32.GetClassNameW(child_hwnd, buf, 256)
        if buf.value == child_class_name:
            found.append(child_hwnd)
            return False  # stop on first match
        return True

    _user32.EnumChildWindows(hwnd, _WNDENUMPROC(_callback), 0)
    return found[0] if found else None
