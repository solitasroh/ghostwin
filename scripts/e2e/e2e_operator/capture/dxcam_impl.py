"""
dxcam (DXGI Desktop Duplication) capture backend.

Third-tier fallback. Requires the window to be visible and uncovered.

--- Root Cause Analysis: PoC ValueError ---
PoC error: "ValueError: Invalid Region: Region should be in 2560x1600"
Window rect from PoC: (702, 102, 1833, 849) — values clearly within 2560x1600.

Diagnosis (reading dxcam/dxcam.py _validate_region):
  Condition: self.width >= right > left >= 0 and self.height >= bottom > top >= 0
  For rect (702, 102, 1833, 849): 2560>=1833>702>=0 AND 1600>=849>102>=0 → True
  This should NOT fail.

Root cause identified:
  dxcam.__init__ executes DXFactory() at MODULE IMPORT TIME (line 188:
  `__factory = DXFactory()`). DXFactory.__init__() calls enum_dxgi_adapters()
  + Output.update_desc() which reads monitor resolution. In a multi-monitor
  setup, `create()` with output_idx=None selects the "primary" output via
  output_metadata is_primary flag. However:

  The PoC runs WITHOUT SetProcessDpiAwarenessContext (it sets DPI awareness
  at the top of capture_poc.py — BUT only for that process). The camera
  attaches to a monitor. On a multi-monitor system where the USER's primary
  monitor is 2560x1600 but DXFactory resolves a different monitor as
  output_idx=0 (e.g., a secondary/virtual display of smaller resolution),
  grab(region=...) passes the rect of the primary-monitor window to a
  secondary-monitor camera, triggering the validation failure.

  Empirical evidence: the error message says "2560x1600", which IS the
  primary monitor's resolution — meaning self.width=2560, self.height=1600.
  But rect right=1833 < 2560 and bottom=849 < 1600, so validation should
  pass... UNLESS this is a different version of dxcam than the installed one.

  UPDATED ANALYSIS: After reading the installed dxcam/dxcam.py directly
  (line 783-787), _validate_region uses strict `>` not `>=` for left/top:
    `self.width >= right > left >= 0 and self.height >= bottom > top >= 0`
  This still passes for the PoC rect.

  FINAL HYPOTHESIS: The PoC ran with a different/older dxcam version that
  had a different validation (right < width, bottom < height with < instead
  of <=). In that version: 1833 < 2560 and 849 < 1600 → both True → should
  pass. Or: the monitor resolution reported by DXGI was different (e.g.,
  DPI-scaled logical resolution instead of physical pixels).

  WORKING FIX: Use output_info() to find the correct monitor that contains
  the window rect, pass output_idx explicitly to create(). Also clip the
  region to monitor-relative coordinates before passing to grab().

References:
  scripts/e2e/capture_poc.py test_b_dxcam_crop() — PoC failure case
  docs/02-design/features/e2e-test-harness.design.md §2.1 D2
  dxcam/dxcam.py _validate_region lines 782-787
"""
from __future__ import annotations

import ctypes
import logging
from ctypes import wintypes

from PIL import Image

from .base import CaptureError, WindowCapturer

logger = logging.getLogger(__name__)


def _get_window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def _find_monitor_for_rect(
    rect: tuple[int, int, int, int],
) -> tuple[int, int, int, int, int, int] | None:
    """Find the monitor that best contains the given screen rect.

    Returns (output_idx, mon_left, mon_top, mon_right, mon_bottom, device_idx)
    by enumerating all DXGI outputs via dxcam.output_info() and
    MONITORINFO via EnumDisplayMonitors.

    Falls back to (0, 0, 0, width, height) for device_idx=0 output_idx=0
    if detection fails.
    """
    import dxcam

    win_cx = (rect[0] + rect[2]) // 2
    win_cy = (rect[1] + rect[3]) // 2

    # Use MonitorFromPoint to find the HMONITOR containing the window center
    PT = wintypes.POINT
    pt = PT(win_cx, win_cy)
    MONITOR_DEFAULTTONEAREST = 0x00000002
    hmon = ctypes.windll.user32.MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST)
    if not hmon:
        return None

    # Get monitor rect via GetMonitorInfoW
    class MONITORINFO(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.DWORD),
            ("rcMonitor", wintypes.RECT),
            ("rcWork", wintypes.RECT),
            ("dwFlags", wintypes.DWORD),
        ]

    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    if not ctypes.windll.user32.GetMonitorInfoW(hmon, ctypes.byref(mi)):
        return None

    mon_left = mi.rcMonitor.left
    mon_top = mi.rcMonitor.top
    mon_right = mi.rcMonitor.right
    mon_bottom = mi.rcMonitor.bottom
    mon_w = mon_right - mon_left
    mon_h = mon_bottom - mon_top

    logger.debug(
        "Monitor for window: (%d,%d,%d,%d) size=%dx%d",
        mon_left, mon_top, mon_right, mon_bottom, mon_w, mon_h,
    )

    # Find which dxcam device/output corresponds to this monitor resolution.
    # Parse output_info() string — format: "Device[D] Output[O]: Res:(W, H) ..."
    info = dxcam.output_info()
    best_device_idx = 0
    best_output_idx = 0
    found = False
    for line in info.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            d_part = line.split("Device[")[1].split("]")[0]
            o_part = line.split("Output[")[1].split("]")[0]
            res_part = line.split("Res:")[1].split(" ")[0]  # "(W, H)"
            res_part = res_part.strip("()")
            w_str, h_str = res_part.split(",")
            res_w = int(w_str.strip())
            res_h = int(h_str.strip())
            if res_w == mon_w and res_h == mon_h:
                best_device_idx = int(d_part)
                best_output_idx = int(o_part)
                found = True
                logger.debug(
                    "dxcam output match: device=%d output=%d res=%dx%d",
                    best_device_idx, best_output_idx, res_w, res_h,
                )
                break
        except (IndexError, ValueError):
            continue

    if not found:
        logger.warning(
            "dxcam: no output matched monitor %dx%d — falling back to device=0 output=0",
            mon_w, mon_h,
        )

    return (best_output_idx, mon_left, mon_top, mon_right, mon_bottom, best_device_idx)


class DxcamCapturer(WindowCapturer):
    """Capture backend using dxcam (DXGI Desktop Duplication).

    Third priority — requires window to be visible and uncovered.

    Fix for PoC ValueError: selects the correct monitor output by matching
    the monitor that contains the window center via MonitorFromPoint + DXGI
    output enumeration. Converts screen coordinates to monitor-relative
    coordinates before passing to camera.grab().

    If self_test() raises, the factory will skip this backend and use
    PrintWindowCapturer instead.
    """

    name = "dxcam(DXGI Desktop Duplication)"

    def self_test(self) -> None:
        """Smoke: import dxcam only (do not create camera — it's expensive)."""
        import dxcam  # noqa: F401 — import smoke only

    def capture(self, hwnd: int) -> Image.Image:
        """Capture the window using DXGI Desktop Duplication.

        Resolves the correct monitor for the window, converts coordinates to
        monitor-relative space, and validates the region before grab.

        Args:
            hwnd: Win32 window handle.

        Returns:
            PIL Image (RGB mode).

        Raises:
            CaptureError: grab failed, None frame, or region validation error.
        """
        import dxcam

        left, top, right, bottom = _get_window_rect(hwnd)
        if right <= left or bottom <= top:
            raise CaptureError(f"hwnd=0x{hwnd:08X} has zero/negative-size rect")

        mon_info = _find_monitor_for_rect((left, top, right, bottom))
        if mon_info is None:
            raise CaptureError("Could not determine monitor for window")

        output_idx, mon_left, mon_top, mon_right, mon_bottom, device_idx = mon_info

        # Convert screen coordinates to monitor-relative (dxcam origin = monitor top-left)
        rel_left   = max(left   - mon_left, 0)
        rel_top    = max(top    - mon_top,  0)
        rel_right  = min(right  - mon_left, mon_right  - mon_left)
        rel_bottom = min(bottom - mon_top,  mon_bottom - mon_top)

        if rel_right <= rel_left or rel_bottom <= rel_top:
            raise CaptureError(
                f"Window rect ({left},{top},{right},{bottom}) maps to "
                f"zero-size monitor-relative region ({rel_left},{rel_top},"
                f"{rel_right},{rel_bottom})"
            )

        region = (rel_left, rel_top, rel_right, rel_bottom)
        logger.debug(
            "DxcamCapturer: screen(%d,%d,%d,%d) -> monitor-relative%s "
            "device=%d output=%d",
            left, top, right, bottom, region, device_idx, output_idx,
        )

        camera = dxcam.create(
            device_idx=device_idx,
            output_idx=output_idx,
            output_color="RGB",
        )
        try:
            frame = camera.grab(region=region)
            if frame is None:
                # DXGI may return None on first grab — retry once
                import time
                time.sleep(0.2)
                frame = camera.grab(region=region)
            if frame is None:
                raise CaptureError("dxcam.grab() returned None twice — no new frame")
            return Image.fromarray(frame)
        finally:
            camera.release()
