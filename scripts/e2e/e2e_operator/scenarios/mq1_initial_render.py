"""MQ-1: Initial render canary.

Goal: After app launch, verify the first workspace's first pane renders the
PowerShell prompt. This is the canary scenario for the entire E2E pipeline —
if MQ-1 cannot capture a non-black DX11 frame, none of the downstream
scenarios will work either.

Pass criteria (Evaluator side, not enforced here):
- Terminal area is one (no split yet)
- PowerShell prompt pattern visible: e.g. "PS C:\\... > " (Korean path OK)
- DX11 client area is non-black

Operator side (this file):
- Either uses ctx.app/ctx.hwnd from chain mode, or launches its own app for
  single-scenario mode
- Calls wait_until_ready() to enforce Tier B (non-black frame)
- Calls window.normalize() to fix window geometry for reproducible captures
- Captures one screenshot: 01_initial_render.png
- Returns OK if all steps complete; ERROR on any exception
- Cleans up its own process if it launched one (single mode)

Design refs:
    docs/02-design/features/e2e-test-harness.design.md §4.1 MQ-1
    Risks: R5 (HostReady race shows as black → bisect_r2_suspected),
           R11 (user mouse interference)
"""
from __future__ import annotations

import logging
import time
from pathlib import Path

from . import SCENARIO_REGISTRY
from ._base import ScenarioContext, ScenarioOutcome, ScenarioStatus

logger = logging.getLogger(__name__)


def _now_iso() -> str:
    import datetime
    return datetime.datetime.now(tz=datetime.timezone.utc).isoformat(timespec="seconds")


def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
    """Execute MQ-1: capture the initial render.

    Args:
        artifact_dir: Per-scenario directory (already created by runner)
        capturer:     Capture backend (windows-capture WGC primary)
        ctx:          Shared chain context. ctx.app/ctx.hwnd may be set by runner
                      in --all chain mode, or None in --scenario MQ-1 single mode.

    Returns:
        ScenarioOutcome with status OK on success, ERROR on any exception.
    """
    started = _now_iso()
    artifact_dir.mkdir(parents=True, exist_ok=True)

    # Lazy imports to avoid heavy module loading at registry time
    from e2e_operator import app_lifecycle, window

    owns_app = False  # whether this scenario must clean up the app on exit
    proc = None
    hwnd: int = 0

    try:
        # ----- Acquire app -----
        if ctx.app is not None and ctx.hwnd:
            # Chain mode: runner already launched
            proc = ctx.app
            hwnd = ctx.hwnd
            logger.info("[MQ-1] using chain app: hwnd=0x%08X", hwnd)
        else:
            # Single mode: launch our own
            logger.info("[MQ-1] launching app (single mode)")
            proc, hwnd = app_lifecycle.launch_app()
            owns_app = True
            ctx.app = proc
            ctx.hwnd = hwnd

        # ----- Tier B readiness -----
        # Brief settle time so the WPF Loaded handler / OnSourceInitialized completes
        # before we start polling. Without this, RestoreWindowBounds may fire after
        # our normalize() call (Risk R6).
        time.sleep(0.5)

        logger.info("[MQ-1] waiting for non-black frame (Tier B)")
        app_lifecycle.wait_until_ready(hwnd, capturer, timeout_s=8.0)

        # ----- Normalize window geometry -----
        # Must run AFTER OnSourceInitialized completes (after wait_until_ready)
        # to avoid RestoreWindowBounds overwriting our SetWindowPos.
        logger.info("[MQ-1] normalizing window to (100, 100, 1280, 800)")
        window.normalize(hwnd, x=100, y=100, width=1280, height=800)

        # Give the renderer a moment to redraw at the new size + let the
        # PowerShell prompt fully appear (PS startup banner takes ~1s)
        time.sleep(1.5)

        # ----- Bring to foreground (Alt-tap trick) -----
        try:
            window.focus(hwnd)
        except Exception as exc:
            # Non-fatal: capture may still work even if foreground fails
            logger.warning("[MQ-1] focus warning (non-fatal): %r", exc)

        # ----- Capture -----
        snapshot_path = artifact_dir / "01_initial_render.png"
        logger.info("[MQ-1] capturing %s", snapshot_path)
        capturer.save(hwnd, snapshot_path)

        if not snapshot_path.exists():
            raise RuntimeError(f"capturer.save() did not produce {snapshot_path}")

        # ----- Build outcome -----
        rect = window.get_window_rect(hwnd) if hasattr(window, "get_window_rect") else None
        notes = f"hwnd=0x{hwnd:08X}"
        if rect is not None:
            notes += f" rect={rect}"

        outcome = ScenarioOutcome(
            scenario="MQ-1",
            status=ScenarioStatus.OK,
            artifact_dir=artifact_dir,
            screenshots=[snapshot_path],
            operator_notes=notes,
            started_at=started,
            finished_at=_now_iso(),
        )
        logger.info("[MQ-1] OK")
        return outcome

    except Exception as exc:
        import traceback
        tb = traceback.format_exc()
        logger.error("[MQ-1] ERROR: %r\n%s", exc, tb)
        return ScenarioOutcome(
            scenario="MQ-1",
            status=ScenarioStatus.ERROR,
            artifact_dir=artifact_dir,
            error=tb,
            operator_notes=f"hwnd=0x{hwnd:08X}" if hwnd else "no hwnd",
            started_at=started,
            finished_at=_now_iso(),
        )

    finally:
        # Clean up only if we launched our own app (single mode)
        if owns_app and proc is not None:
            try:
                logger.info("[MQ-1] shutting down owned app")
                app_lifecycle.shutdown_app(proc, hwnd)
                ctx.app = None
                ctx.hwnd = None
            except Exception as exc:
                logger.warning("[MQ-1] shutdown warning (non-fatal): %r", exc)


# Register with the global registry
SCENARIO_REGISTRY["MQ-1"] = run
