"""MQ-4: Mouse click pane focus.

Action: Click in the upper-left region of the client area (which should be the
top-left pane after MQ-2/MQ-3 splits). The click coordinates are intentionally
in a region most likely to overlap any pane regardless of layout history.

Expected: The cyan focus border (#0091FF) moves to the clicked pane.

Pre: MQ-2 succeeded (at least 2 panes exist).

Note: The exact "which pane is focused after click" verdict is the Evaluator's
job — Operator only injects the click and captures the result.
"""
from __future__ import annotations

import logging
import time
from pathlib import Path

from . import SCENARIO_REGISTRY
from ._base import ScenarioContext, ScenarioOutcome
from ._helpers import (
    acquire_app, release_if_owned, safe_focus,
    make_outcome, make_error_outcome, now_iso,
)

logger = logging.getLogger(__name__)

# Click target in client coordinates of the GhostWin window normalized to 1280x800.
# Sidebar is roughly 175 px wide; pane area starts around x=180. We click well
# inside a likely pane (upper-left quadrant) at (400, 250).
CLICK_X = 400
CLICK_Y = 250


def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
    started = now_iso()
    artifact_dir.mkdir(parents=True, exist_ok=True)
    owns_app = False
    hwnd: int = 0

    try:
        proc, hwnd, owns_app = acquire_app(ctx, capturer)
        from e2e_operator import input as injector

        # Single mode: ensure 2 panes exist (MQ-4 depends on MQ-2)
        if owns_app:
            logger.info("[MQ-4] single mode prelude: Alt+V to create 2 panes")
            safe_focus(hwnd)
            time.sleep(0.2)
            injector.send_keys(hwnd, "%v")
            time.sleep(0.6)
            ctx.pane_count = 2

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-4] clicking at client (%d, %d)", CLICK_X, CLICK_Y)
        injector.click_at(hwnd, CLICK_X, CLICK_Y, button="left")
        time.sleep(0.4)

        path = artifact_dir / "after_mouse_focus.png"
        capturer.save(hwnd, path)

        return make_outcome(
            "MQ-4", artifact_dir, started,
            screenshots=[path],
            notes=f"hwnd=0x{hwnd:08X} click=({CLICK_X},{CLICK_Y}) pane_count={ctx.pane_count}",
        )
    except Exception as exc:
        return make_error_outcome("MQ-4", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-4"] = run
