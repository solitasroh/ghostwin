"""
Capture backend factory for GhostWin E2E test harness.

Usage:
    from e2e_operator.capture import get_capturer
    capturer = get_capturer()
    capturer.save(hwnd, Path("artifacts/shot.png"))

Factory order (CHANGED from Design §2.1 D5 original):
  Design D5 original: wgc → dxcam → printwindow
  Implemented order:  wgc → printwindow → dxcam

Reason (2026-04-08 PoC empirical evidence):
  - WGC:          PASS, mean luma=30.47 (best: DWM composition, captures DX11 HwndHost)
  - PrintWindow:  PASS, mean luma=30.56 (reliable, no visibility requirement)
  - dxcam:        FAIL, ValueError — requires correct monitor selection fix
  PrintWindow is promoted to secondary (before dxcam) because it was empirically
  proven in the PoC while dxcam needs a coordinate-fix that has not yet been
  re-validated live. If dxcam fix works it will still be available as tertiary.

  Reference: scripts/e2e/poc_artifacts/poc_summary.json (2026-04-08)

Environment override:
  GHOSTWIN_E2E_CAPTURER=wgc|printwindow|dxcam  — force a specific backend

Re-exports: WindowCapturer, CaptureError, WindowNotFoundError
"""
from __future__ import annotations

import importlib
import logging
import os
from typing import Optional

from .base import CaptureError, WindowCapturer, WindowNotFoundError

logger = logging.getLogger(__name__)

_CACHED: Optional[WindowCapturer] = None

# (env key, class name, relative module)
_CANDIDATES: list[tuple[str, str, str]] = [
    ("wgc",          "WgcCapturer",          ".wgc"),
    ("printwindow",  "PrintWindowCapturer",   ".printwindow"),
    ("dxcam",        "DxcamCapturer",         ".dxcam_impl"),
]


def get_capturer() -> WindowCapturer:
    """Return the best available capture backend, cached after first call.

    Probe order: wgc → printwindow → dxcam (unless overridden by env var).
    Each backend's self_test() is called; the first to pass is cached and
    returned.  Subsequent calls return the cached instance.

    Returns:
        Concrete WindowCapturer instance.

    Raises:
        CaptureError: All backends failed self_test().
    """
    global _CACHED
    if _CACHED is not None:
        return _CACHED

    forced = os.environ.get("GHOSTWIN_E2E_CAPTURER", "").strip().lower()
    candidates = _CANDIDATES
    if forced:
        candidates = [c for c in _CANDIDATES if c[0] == forced]
        if not candidates:
            raise CaptureError(
                f"GHOSTWIN_E2E_CAPTURER={forced!r} does not match any known backend "
                f"(valid: wgc, printwindow, dxcam)"
            )

    errors: list[tuple[str, str]] = []
    pkg = __name__.rsplit(".", 1)[0]  # "e2e_operator.capture" → "e2e_operator.capture"
    # __name__ is "e2e_operator.capture" so relative import base is correct.

    for env_key, cls_name, rel_mod in candidates:
        try:
            mod = importlib.import_module(rel_mod, package=__name__)
            cls = getattr(mod, cls_name)
            instance: WindowCapturer = cls()
            instance.self_test()
            logger.info("capture factory: selected backend %r", instance.name)
            _CACHED = instance
            return instance
        except Exception as exc:
            msg = f"{type(exc).__name__}: {exc}"
            logger.warning("capture factory: backend %r unavailable — %s", env_key, msg)
            errors.append((env_key, msg))

    raise CaptureError(f"no capture backend available; all failed self_test: {errors}")


def reset_capturer() -> None:
    """Clear the module-level cache (for testing purposes)."""
    global _CACHED
    _CACHED = None


__all__ = [
    "get_capturer",
    "reset_capturer",
    "WindowCapturer",
    "CaptureError",
    "WindowNotFoundError",
]
