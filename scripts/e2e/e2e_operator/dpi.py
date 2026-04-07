"""PerMonitorV2 DPI awareness bootstrap.

Must be called as the FIRST executable line of any process that interacts with
GhostWin (app.manifest declares PerMonitorV2). Without this, GetWindowRect returns
DPI-virtualized coordinates and capture region calculations are wrong.

References:
    docs/02-design/features/e2e-test-harness.design.md §2.2 D10, §10 R3
    src/GhostWin.App/app.manifest: dpiAwareness = PerMonitorV2 (confirmed 2026-04-08)
    MSDN: SetProcessDpiAwarenessContext
          https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext
"""
import ctypes

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4 (as a handle value)
# Ref: https://docs.microsoft.com/en-us/windows/win32/hidpi/dpi-awareness-context
_PROCESS_PER_MONITOR_DPI_AWARE_V2 = -4


def set_per_monitor_v2() -> None:
    """Bootstrap PerMonitorV2 DPI awareness for this process.

    Idempotent: safe to call multiple times. If the process is already at
    PerMonitorV2, GetLastError returns ERROR_ACCESS_DENIED (5) — that is
    treated as success here.

    Raises:
        OSError: if SetProcessDpiAwarenessContext fails for any reason other
                 than idempotent re-set. Per behavior.md: do NOT silently swallow.

    MSDN note:
        SetProcessDpiAwarenessContext supersedes SetProcessDPIAware and
        SetProcessDpiAwareness. Call as early as possible — before any HWNDs
        are created or any DPI-sensitive APIs are invoked.
    """
    user32 = ctypes.windll.user32
    # SetProcessDpiAwarenessContext returns BOOL (non-zero on success).
    # The handle value -4 corresponds to DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    ok = user32.SetProcessDpiAwarenessContext(_PROCESS_PER_MONITOR_DPI_AWARE_V2)
    if not ok:
        err = ctypes.get_last_error()
        # ERROR_ACCESS_DENIED (5) means DPI awareness is already set — idempotent OK.
        if err not in (0, 5):
            raise OSError(
                f"SetProcessDpiAwarenessContext failed: WinError {err}. "
                "Ensure no HWND has been created before this call."
            )
