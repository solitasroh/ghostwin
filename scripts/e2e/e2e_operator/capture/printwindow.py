"""
PrintWindow(PW_RENDERFULLCONTENT) capture backend.

Second-tier fallback — PoC confirmed PASS (mean luma=30.56).
Uses win32ui/win32gui pywin32 wrappers (same pattern as PoC Test C).

Note: PW_RENDERFULLCONTENT (0x2) forces the window to re-render into the
provided HDC, bypassing the composited desktop. Works even if the DX11
HwndHost child renders off-screen, because the render is triggered on-demand.

References:
  scripts/e2e/capture_poc.py test_c_printwindow() — exactly reused
  docs/02-design/features/e2e-test-harness.design.md §2.1 D3
"""
from __future__ import annotations

import ctypes
import logging

from PIL import Image

from .base import CaptureError, WindowCapturer

logger = logging.getLogger(__name__)

PW_RENDERFULLCONTENT = 0x00000002


class PrintWindowCapturer(WindowCapturer):
    """Capture backend using Win32 PrintWindow with PW_RENDERFULLCONTENT flag.

    Promoted to second priority (before dxcam) because PoC shows it
    reliably captures the GhostWin DX11 HwndHost area (mean luma 30.56).

    Does NOT require the window to be foreground/uncovered, but the quality
    depends on the window being in a rendered state. Returns RGB Image.
    """

    name = "PrintWindow(PW_RENDERFULLCONTENT)"

    def self_test(self) -> None:
        """Smoke: user32 GetDesktopWindow + gdi32 CreateCompatibleDC.

        Also verifies pywin32 (win32gui/win32ui) is importable.
        """
        import win32gui  # noqa: F401
        import win32ui  # noqa: F401

        hwnd_desktop = ctypes.windll.user32.GetDesktopWindow()
        if not hwnd_desktop:
            raise CaptureError("GetDesktopWindow returned NULL")
        hdc = ctypes.windll.gdi32.CreateCompatibleDC(0)
        if not hdc:
            raise CaptureError("CreateCompatibleDC(0) returned NULL — GDI not functional")
        ctypes.windll.gdi32.DeleteDC(hdc)

    def capture(self, hwnd: int) -> Image.Image:
        """Capture the window using PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT).

        Args:
            hwnd: Win32 window handle.

        Returns:
            PIL Image (RGB mode — BGRX raw bytes converted).

        Raises:
            CaptureError: PrintWindow returned 0 or GDI object creation failed.
        """
        import win32con  # noqa: F401
        import win32gui
        import win32ui

        # Resolve window rect to determine bitmap dimensions
        rect = ctypes.wintypes.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
        width = rect.right - rect.left
        height = rect.bottom - rect.top

        if width <= 0 or height <= 0:
            raise CaptureError(
                f"hwnd=0x{hwnd:08X} has zero-size rect ({width}x{height})"
            )

        logger.debug(
            "PrintWindowCapturer: hwnd=0x%08X size=%dx%d", hwnd, width, height
        )

        hwnd_dc = None
        mfc_dc = None
        save_dc = None
        bmp = None

        try:
            hwnd_dc = win32gui.GetWindowDC(hwnd)
            mfc_dc = win32ui.CreateDCFromHandle(hwnd_dc)
            save_dc = mfc_dc.CreateCompatibleDC()
            bmp = win32ui.CreateBitmap()
            bmp.CreateCompatibleBitmap(mfc_dc, width, height)
            save_dc.SelectObject(bmp)

            result = ctypes.windll.user32.PrintWindow(
                hwnd, save_dc.GetSafeHdc(), PW_RENDERFULLCONTENT
            )
            if not result:
                raise CaptureError(
                    f"PrintWindow returned 0 for hwnd=0x{hwnd:08X} — "
                    "window may not support off-screen rendering"
                )

            bmp_info = bmp.GetInfo()
            bmp_str = bmp.GetBitmapBits(True)
            img = Image.frombuffer(
                "RGB",
                (bmp_info["bmWidth"], bmp_info["bmHeight"]),
                bmp_str,
                "raw",
                "BGRX",
                0,
                1,
            )
            # Return a copy so GDI resources can be freed immediately
            return img.copy()

        finally:
            # Always release GDI resources to avoid handle leaks
            try:
                if bmp is not None:
                    win32gui.DeleteObject(bmp.GetHandle())
            except Exception:
                pass
            try:
                if save_dc is not None:
                    save_dc.DeleteDC()
            except Exception:
                pass
            try:
                if mfc_dc is not None:
                    mfc_dc.DeleteDC()
            except Exception:
                pass
            try:
                if hwnd_dc is not None:
                    win32gui.ReleaseDC(hwnd, hwnd_dc)
            except Exception:
                pass
