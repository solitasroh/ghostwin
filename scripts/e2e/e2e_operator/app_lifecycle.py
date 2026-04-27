"""GhostWin application launch, readiness, and shutdown helpers.

Lifecycle flow:
    launch_app()  →  wait_until_ready(hwnd, capturer)  →  ... test ...  →  shutdown_app()

Or via context manager:
    with GhostWinApp() as app:
        wait_until_ready(app.hwnd, capturer)
        window.normalize(app.hwnd)
        ...  # scenarios

References:
    docs/02-design/features/e2e-test-harness.design.md §2.3 D16, §3.3 app_lifecycle.py
    scripts/e2e/capture_poc.py  — PID-based HWND discovery, WM_CLOSE shutdown pattern
    src/GhostWin.App/MainWindow.xaml.cs:174-192 OnClosing → Task.Run → Environment.Exit(0)
"""
import logging
import subprocess
import time
import ctypes
from ctypes import wintypes
from pathlib import Path
from typing import TYPE_CHECKING

from .window import wait_for_window

if TYPE_CHECKING:
    from e2e_operator.capture.base import WindowCapturer

logger = logging.getLogger(__name__)

# Default EXE path relative to repo root.  Resolved at call time so that
# the module can be imported from any working directory.
EXE_PATH = Path(
    "src/GhostWin.App/bin/x64/Release/net10.0-windows/GhostWin.App.exe"
)

WM_CLOSE = 0x0010  # MSDN: WM_CLOSE


# ---------------------------------------------------------------------------
# Launch
# ---------------------------------------------------------------------------

def launch_app(
    exe_path: Path | None = None,
    hwnd_timeout_s: float = 10.0,
) -> tuple[subprocess.Popen, int]:
    """Start GhostWin.App.exe and wait for its top-level HWND to appear.

    The exe is launched with cwd=exe.parent so it can locate ghostwin_engine.dll
    and other companion DLLs in the same directory.

    Args:
        exe_path:       Path to GhostWin.App.exe.  Defaults to EXE_PATH resolved
                        against the current working directory at call time.
        hwnd_timeout_s: Maximum seconds to wait for the top-level HWND.

    Returns:
        (proc, hwnd) where proc is the Popen handle and hwnd is the Win32 HWND.

    Raises:
        FileNotFoundError: if exe_path does not exist.
        TimeoutError:      if no visible HWND appears within hwnd_timeout_s.
                           The process is killed before raising.
    """
    exe = (exe_path or EXE_PATH).resolve()
    if not exe.exists():
        raise FileNotFoundError(f"GhostWin.App.exe not found: {exe}")

    logger.info("launch_app: starting %s", exe)
    proc = subprocess.Popen([str(exe)], cwd=str(exe.parent))
    logger.debug("launch_app: pid = %d", proc.pid)

    try:
        hwnd = wait_for_window(proc.pid, hwnd_timeout_s)
    except TimeoutError:
        logger.error("launch_app: HWND timeout — killing pid %d", proc.pid)
        proc.kill()
        proc.wait(timeout=3.0)
        raise TimeoutError(
            f"GhostWin window did not appear within {hwnd_timeout_s}s (pid {proc.pid})"
        )

    logger.info("launch_app: HWND 0x%08X ready (pid %d)", hwnd, proc.pid)
    return proc, hwnd


# ---------------------------------------------------------------------------
# Readiness (3-tier per Design D16)
# ---------------------------------------------------------------------------

def wait_until_ready(
    hwnd: int,
    capturer: "WindowCapturer",
    timeout_s: float = 5.0,
) -> None:
    """Wait until GhostWin renders at least one non-black frame (Tier B).

    Tier A (HWND visible) is already satisfied by launch_app().
    Tier B: capture a frame and check that the mean grayscale luminance exceeds
            0.05 * 255 = 12.75 (Design D16 threshold).  A consistently black
            frame suggests the bisect R2 HostReady race condition.
    Tier C (OCR prompt detection) is optional and not implemented here.

    Args:
        hwnd:      Top-level window handle returned by launch_app().
        capturer:  A WindowCapturer instance (e.g. WgcCapturer).
        timeout_s: Maximum seconds to wait for a non-black frame.

    Raises:
        TimeoutError: with a message hinting at bisect R2 if the frame remains
                      black after timeout_s seconds.

    References:
        Design D16 Tier B, Risk R2 (bisect HostReady race — CLOSED 2026-04-08)
    """
    import numpy as np

    LUMA_THRESHOLD = 12.75  # 0.05 * 255

    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            img = capturer.capture(hwnd)
            arr = np.asarray(img.convert("L"))
            mean_luma = float(arr.mean())
            if mean_luma > LUMA_THRESHOLD:
                logger.debug("wait_until_ready: Tier B passed (luma %.2f)", mean_luma)
                return
            logger.debug("wait_until_ready: frame too dark (luma %.2f), retrying", mean_luma)
        except Exception as exc:
            logger.warning("wait_until_ready: capture attempt failed: %r", exc)
        time.sleep(0.1)

    raise TimeoutError(
        f"GhostWin rendered only black frames for {timeout_s}s (HWND 0x{hwnd:08X}). "
        "bisect R2 (HostReady race) suspected — check OnHostReady diagnostic log."
    )


# ---------------------------------------------------------------------------
# Shutdown
# ---------------------------------------------------------------------------

def shutdown_app(
    proc: subprocess.Popen,
    hwnd: int,
    grace_s: float = 5.0,
) -> int:
    """Gracefully close GhostWin and return its exit code.

    Sends WM_CLOSE via PostMessageW (non-blocking) which triggers
    MainWindow.OnClosing → Task.Run → Environment.Exit(0).  Uses
    PostMessage (not SendMessage) to avoid blocking the caller's thread.

    Fallback: if the process does not exit within grace_s, terminate() is
    called; if still alive after 2 s, kill() is called.

    Args:
        proc:    The Popen object returned by launch_app().
        hwnd:    The window handle (used for WM_CLOSE).  May be invalid
                 by the time this is called — PostMessageW silently ignores
                 invalid HWNDs (Risk R10).
        grace_s: Seconds to wait for graceful exit before force-killing.

    Returns:
        The process exit code (0 for clean shutdown).

    References:
        Design D16 C6 — MainWindow.OnClosing calls Environment.Exit(0)
        Risk R10 — HWND invalidation on shutdown is safe with PostMessage
        MSDN: PostMessageW
              https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew
    """
    logger.info("shutdown_app: sending WM_CLOSE to HWND 0x%08X", hwnd)
    ctypes.windll.user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)

    try:
        exit_code = proc.wait(timeout=grace_s)
        logger.info("shutdown_app: exited cleanly (code %d)", exit_code)
        return exit_code
    except subprocess.TimeoutExpired:
        logger.warning("shutdown_app: grace period expired, terminating pid %d", proc.pid)
        proc.terminate()
        try:
            exit_code = proc.wait(timeout=2.0)
            logger.info("shutdown_app: terminated (code %d)", exit_code)
            return exit_code
        except subprocess.TimeoutExpired:
            logger.error("shutdown_app: terminate timed out, killing pid %d", proc.pid)
            proc.kill()
            return proc.wait()


# ---------------------------------------------------------------------------
# Context manager
# ---------------------------------------------------------------------------

class GhostWinApp:
    """Context manager that owns the GhostWin process lifecycle.

    Usage:
        with GhostWinApp() as app:
            wait_until_ready(app.hwnd, capturer)
            window.normalize(app.hwnd)
            window.focus(app.hwnd)
            # ... run scenarios ...
        # shutdown_app() is called automatically in __exit__

    Note: wait_until_ready() and window.normalize() are NOT called in __enter__
    because the capturer is owned by the caller, not by this class.  The caller
    must invoke them explicitly before running any input/capture operations.
    """

    def __init__(
        self,
        exe_path: Path | None = None,
        hwnd_timeout_s: float = 10.0,
    ) -> None:
        self._exe_path = exe_path
        self._hwnd_timeout_s = hwnd_timeout_s
        self._proc: subprocess.Popen | None = None
        self._hwnd: int | None = None

    def __enter__(self) -> "GhostWinApp":
        self._proc, self._hwnd = launch_app(self._exe_path, self._hwnd_timeout_s)
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if self._proc is not None and self._hwnd is not None:
            try:
                shutdown_app(self._proc, self._hwnd)
            except Exception as e:
                logger.error("GhostWinApp.__exit__: shutdown error: %r", e)

    @property
    def hwnd(self) -> int:
        if self._hwnd is None:
            raise RuntimeError("GhostWinApp: hwnd not available (not yet launched?)")
        return self._hwnd

    @property
    def proc(self) -> subprocess.Popen:
        if self._proc is None:
            raise RuntimeError("GhostWinApp: proc not available (not yet launched?)")
        return self._proc
