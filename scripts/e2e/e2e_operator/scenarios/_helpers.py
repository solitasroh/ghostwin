"""Shared helpers for MQ-2 through MQ-8 scenarios.

These reduce per-scenario boilerplate while keeping each scenario file focused
on its action sequence + capture pattern.

Design refs:
    docs/02-design/features/e2e-test-harness.design.md §2.4 D17 (chain mode)
"""
from __future__ import annotations

import datetime
import logging
import time
from pathlib import Path

from ._base import ScenarioContext, ScenarioOutcome, ScenarioStatus

logger = logging.getLogger(__name__)


def now_iso() -> str:
    """Return ISO8601 UTC timestamp."""
    return datetime.datetime.now(tz=datetime.timezone.utc).isoformat(timespec="seconds")


def acquire_app(ctx: ScenarioContext, capturer) -> tuple[object, int, bool]:
    """Return (proc, hwnd, owns_app).

    In --all chain mode, ctx.app/ctx.hwnd are pre-populated by an earlier
    scenario (typically MQ-1). Returns owns_app=False.

    In --scenario MQ-N single mode, ctx is empty on entry; this function
    launches a fresh GhostWin, waits for readiness, normalizes geometry, and
    returns owns_app=True so the caller knows to shutdown in finally.
    """
    if ctx.app is not None and ctx.hwnd:
        return ctx.app, ctx.hwnd, False

    # Lazy import to avoid heavy module loading at registry time
    from e2e_operator import app_lifecycle, window

    logger.info("acquire_app: launching new instance (single mode)")
    proc, hwnd = app_lifecycle.launch_app()
    ctx.app = proc
    ctx.hwnd = hwnd

    time.sleep(0.5)
    app_lifecycle.wait_until_ready(hwnd, capturer, timeout_s=8.0)
    window.normalize(hwnd, x=100, y=100, width=1280, height=800)
    time.sleep(1.0)
    return proc, hwnd, True


def release_if_owned(ctx: ScenarioContext, owns_app: bool) -> None:
    """Shutdown the app if this scenario launched it (single mode)."""
    if not owns_app:
        return
    if ctx.app is None:
        return
    from e2e_operator import app_lifecycle
    try:
        app_lifecycle.shutdown_app(ctx.app, ctx.hwnd or 0)
    except Exception as exc:
        logger.warning("release_if_owned: shutdown error: %r", exc)
    ctx.app = None
    ctx.hwnd = None


def safe_focus(hwnd: int) -> None:
    """Focus the window, swallowing any exception (non-fatal)."""
    from e2e_operator import window
    try:
        window.focus(hwnd)
    except Exception as exc:
        logger.warning("safe_focus: %r (continuing)", exc)


def make_outcome(
    scenario: str,
    artifact_dir: Path,
    started: str,
    *,
    status: ScenarioStatus = ScenarioStatus.OK,
    screenshots: list[Path] | None = None,
    notes: str = "",
    error: str | None = None,
) -> ScenarioOutcome:
    """Build a ScenarioOutcome with consistent timestamp handling."""
    return ScenarioOutcome(
        scenario=scenario,
        status=status,
        artifact_dir=artifact_dir,
        screenshots=screenshots or [],
        operator_notes=notes,
        error=error,
        started_at=started,
        finished_at=now_iso(),
    )


def make_error_outcome(
    scenario: str,
    artifact_dir: Path,
    started: str,
    exc: BaseException,
    *,
    notes: str = "",
) -> ScenarioOutcome:
    """Build an ERROR outcome from an exception, capturing the traceback."""
    import traceback
    tb = traceback.format_exc()
    logger.error("[%s] ERROR: %r\n%s", scenario, exc, tb)
    return ScenarioOutcome(
        scenario=scenario,
        status=ScenarioStatus.ERROR,
        artifact_dir=artifact_dir,
        error=tb,
        operator_notes=notes,
        started_at=started,
        finished_at=now_iso(),
    )
