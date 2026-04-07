"""MQ-2: Vertical pane split via Alt+V.

Action: Send Alt+V to the focused window.
Expected: 2 panes appear with a vertical split line; both panes show prompts.

Pre: MQ-1 succeeded (1 pane visible). In single mode, scenario launches its
own GhostWin instance.
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


def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
    started = now_iso()
    artifact_dir.mkdir(parents=True, exist_ok=True)
    owns_app = False
    hwnd: int = 0

    try:
        proc, hwnd, owns_app = acquire_app(ctx, capturer)
        from e2e_operator import input as injector

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-2] sending Alt+V")
        injector.send_keys(hwnd, "%v")
        time.sleep(0.6)  # split animation + WPF measure/arrange

        path = artifact_dir / "after_split_vertical.png"
        capturer.save(hwnd, path)
        ctx.pane_count = 2

        return make_outcome(
            "MQ-2", artifact_dir, started,
            screenshots=[path],
            notes=f"hwnd=0x{hwnd:08X} pane_count={ctx.pane_count}",
        )
    except Exception as exc:
        return make_error_outcome("MQ-2", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-2"] = run
