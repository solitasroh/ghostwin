"""
BC-01 — repro-script-fix: AMSI-safe window capture CLI.

Purpose
-------
Provides a standalone CLI wrapper around the existing e2e PrintWindow capturer
so that PowerShell scripts (scripts/repro_first_pane.ps1) can capture the
GhostWin window without triggering the Windows Defender AMSI signature that
blocked inline C# PInvoke screen-capture in PowerShell.

Reuses the already-validated PrintWindowCapturer from
scripts/e2e/e2e_operator/capture/printwindow.py (pywin32-based, PoC mean
luma 30.56 on DX11 HwndHost child).

Usage
-----
    python scripts/capture_window.py --process GhostWin.App --out path/to/out.png
    python scripts/capture_window.py --hwnd 0x123456 --out path/to/out.png

Exit codes
----------
    0 — PNG written successfully
    1 — process/hwnd not found
    2 — capture failed (propagated CaptureError)

Design notes
------------
- Uses the e2e venv (pywin32, PIL) — same interpreter invoked via
  scripts/test_e2e.ps1. No new dependencies.
- Finds the main window HWND by process name via win32gui EnumWindows.
- PrintWindow(PW_RENDERFULLCONTENT) works even when the window is covered or
  off-screen (unlike CopyFromScreen + AutomationElement combination).
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Make the e2e operator package importable
_E2E_ROOT = Path(__file__).resolve().parent / "e2e"
if str(_E2E_ROOT) not in sys.path:
    sys.path.insert(0, str(_E2E_ROOT))

from e2e_operator.capture.printwindow import PrintWindowCapturer  # noqa: E402
from e2e_operator.capture.base import CaptureError  # noqa: E402


_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def _get_process_image_basename(pid: int) -> str | None:
    """Return lowercase exe basename for pid, or None on failure.

    Uses QueryFullProcessImageNameW (Win7+) — pywin32/ctypes only, no psutil.
    """
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.windll.kernel32
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL

    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buf))
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return None
        path = buf.value
    finally:
        kernel32.CloseHandle(handle)

    base = path.rsplit("\\", 1)[-1].lower()
    if base.endswith(".exe"):
        base = base[:-4]
    return base


def find_main_hwnd_by_process(process_name: str) -> int | None:
    """Return the first visible top-level HWND whose process image matches.

    process_name may omit the .exe suffix. Case-insensitive match.
    """
    import win32gui
    import win32process

    target = process_name.lower()
    if target.endswith(".exe"):
        target = target[:-4]

    found: list[int] = []

    def _cb(hwnd: int, _: object) -> bool:
        if not win32gui.IsWindowVisible(hwnd):
            return True
        # Skip tool/owned windows — we want the main app window
        if not win32gui.GetWindowText(hwnd):
            return True
        try:
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid <= 0:
                return True
            base = _get_process_image_basename(pid)
            if base == target:
                found.append(hwnd)
        except Exception:
            pass
        return True

    win32gui.EnumWindows(_cb, None)
    return found[0] if found else None


def main() -> int:
    parser = argparse.ArgumentParser(description="AMSI-safe window capture via PrintWindow")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--process", help="Process image name (e.g. GhostWin.App)")
    group.add_argument("--hwnd", help="Window handle (hex 0x... or decimal)")
    parser.add_argument("--out", required=True, help="Output PNG path")
    args = parser.parse_args()

    if args.hwnd:
        hwnd_str = args.hwnd.lower()
        hwnd = int(hwnd_str, 16) if hwnd_str.startswith("0x") else int(hwnd_str)
    else:
        hwnd = find_main_hwnd_by_process(args.process)
        if hwnd is None:
            print(f"ERROR: no visible window for process {args.process!r}", file=sys.stderr)
            return 1

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    capturer = PrintWindowCapturer()
    try:
        img = capturer.capture(hwnd)
    except CaptureError as e:
        print(f"ERROR: PrintWindow capture failed: {e}", file=sys.stderr)
        return 2

    img.save(out_path, format="PNG")
    print(f"OK: captured hwnd=0x{hwnd:08X} -> {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
