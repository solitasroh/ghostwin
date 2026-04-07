"""MQ-7: Switch active workspace via sidebar click.

Action: Click on the first workspace entry in the left sidebar. With at least
2 workspaces (post-MQ-6), this should activate the original workspace.

Expected: The pane content area switches to show the clicked workspace's panes.

Pre: MQ-6 succeeded (≥2 workspaces exist).

Sidebar layout (visual estimate from MQ-1 PNG, normalized 1280x800 window):
    Sidebar header "GHOSTWIN":  ~y=85
    First workspace entry row:  ~x=80, y=150
    Second workspace entry row: ~x=80, y=190 (40 px row height approx)
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

# Click target for first workspace entry (client coordinates).
SIDEBAR_X = 80
FIRST_WORKSPACE_Y = 150


def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
    started = now_iso()
    artifact_dir.mkdir(parents=True, exist_ok=True)
    owns_app = False
    hwnd: int = 0

    try:
        proc, hwnd, owns_app = acquire_app(ctx, capturer)
        from e2e_operator import input as injector

        # Single mode: ensure ≥2 workspaces (MQ-7 depends on MQ-6)
        if owns_app:
            logger.info("[MQ-7] single mode prelude: Ctrl+T to create 2nd workspace")
            safe_focus(hwnd)
            time.sleep(0.2)
            injector.send_keys(hwnd, "^t")
            time.sleep(1.5)
            ctx.workspace_count = 2

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-7] clicking sidebar (%d, %d)", SIDEBAR_X, FIRST_WORKSPACE_Y)
        injector.click_at(hwnd, SIDEBAR_X, FIRST_WORKSPACE_Y, button="left")
        time.sleep(0.6)

        path = artifact_dir / "after_workspace_switch.png"
        capturer.save(hwnd, path)

        return make_outcome(
            "MQ-7", artifact_dir, started,
            screenshots=[path],
            notes=(
                f"hwnd=0x{hwnd:08X} click=({SIDEBAR_X},{FIRST_WORKSPACE_Y}) "
                f"workspace_count={ctx.workspace_count}"
            ),
        )
    except Exception as exc:
        return make_error_outcome("MQ-7", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-7"] = run
