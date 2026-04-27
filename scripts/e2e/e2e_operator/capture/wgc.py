"""
WGC (Windows Graphics Capture) backend via windows-capture 1.5.0.

Uses the window_name API (title-based lookup) — NOT hwnd direct.
The PoC confirmed this captures GhostWin's DX11 HwndHost area correctly:
  mean_luma=30.47, dim=(1697, 1121), GhostWin title="GhostWin".

API contract (2026-04-08 PoC empirical):
  WindowsCapture(window_name=<str>, cursor_capture=False, draw_border=False)
  Frame.frame_buffer -> (H, W, 4) numpy BGRA

References:
  scripts/e2e/capture_poc.py test_a_wgc_window_name()
  docs/02-design/features/e2e-test-harness.design.md §2.1 D1
"""
from __future__ import annotations

import ctypes
import logging
import queue

from PIL import Image

from .base import CaptureError, WindowCapturer, WindowNotFoundError

logger = logging.getLogger(__name__)

_MAX_TITLE_LEN = 256
_FRAME_TIMEOUT_S = 3.0


def _hwnd_to_title(hwnd: int) -> str:
    """Resolve hwnd to window title via GetWindowTextW.

    Returns:
        The window title string.

    Raises:
        WindowNotFoundError: Title is empty (window destroyed or no title).
    """
    buf = ctypes.create_unicode_buffer(_MAX_TITLE_LEN)
    ctypes.windll.user32.GetWindowTextW(hwnd, buf, _MAX_TITLE_LEN)
    title = buf.value
    if not title:
        raise WindowNotFoundError(
            f"hwnd=0x{hwnd:08X} has empty title — window may be destroyed"
        )
    return title


class WgcCapturer(WindowCapturer):
    """Capture backend using Windows Graphics Capture via windows-capture 1.5.0.

    Primary backend — proven by PoC (mean luma 30.47, DX11 HwndHost visible).
    Uses window_name parameter so DWM composition tree is captured correctly,
    including minimized/partially-covered windows.
    """

    name = "windows-capture(WGC,window_name)"

    def self_test(self) -> None:
        """Smoke: import windows_capture + user32 GetDesktopWindow call."""
        import windows_capture  # noqa: F401 — import smoke

        hwnd_desktop = ctypes.windll.user32.GetDesktopWindow()
        if not hwnd_desktop:
            raise CaptureError("GetDesktopWindow returned NULL — user32 not functional")

    def capture(self, hwnd: int) -> Image.Image:
        """Capture window via WGC title-based lookup.

        Args:
            hwnd: Win32 window handle. Resolved to title internally.

        Returns:
            PIL Image (RGBA).

        Raises:
            WindowNotFoundError: hwnd has empty title.
            CaptureError: WGC frame timeout or closed without delivering a frame.
        """
        from windows_capture import Frame, InternalCaptureControl, WindowsCapture

        title = _hwnd_to_title(hwnd)
        logger.debug("WgcCapturer: capturing window title=%r hwnd=0x%08X", title, hwnd)

        result_q: queue.Queue = queue.Queue(maxsize=1)

        cap = WindowsCapture(
            cursor_capture=False,
            draw_border=False,
            window_name=title,
        )

        @cap.event
        def on_frame_arrived(frame: Frame, ctl: InternalCaptureControl) -> None:
            try:
                arr = frame.frame_buffer  # (H, W, 4) BGRA numpy array
                # BGRA -> RGBA channel reorder (copy required to own the data)
                rgba = arr[..., [2, 1, 0, 3]].copy()
                img = Image.fromarray(rgba, mode="RGBA")
                if result_q.empty():
                    result_q.put(img)
            finally:
                ctl.stop()

        @cap.event
        def on_closed() -> None:
            # Fired after ctl.stop() completes; if queue still empty something failed.
            if result_q.empty():
                result_q.put(CaptureError("WGC session closed without delivering a frame"))

        cap.start_free_threaded()
        try:
            obj = result_q.get(timeout=_FRAME_TIMEOUT_S)
        except queue.Empty:
            raise CaptureError(
                f"WGC frame timeout after {_FRAME_TIMEOUT_S}s — "
                f"window={title!r} may not be visible"
            )
        if isinstance(obj, Exception):
            raise obj
        return obj
