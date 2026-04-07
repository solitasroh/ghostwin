"""MQ-3: Horizontal pane split via Alt+H.

Action: Send Alt+H to split the currently-focused pane horizontally.
Expected: 3 panes total; the focused pane is now split into top/bottom halves.

Pre: MQ-2 succeeded (2 panes exist).
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

        # Single mode: also need MQ-2 setup since MQ-3 depends on 2 panes
        if owns_app:
            logger.info("[MQ-3] single mode prelude: Alt+V to create 2 panes")
            safe_focus(hwnd)
            time.sleep(0.2)
            injector.send_keys(hwnd, "%v")
            time.sleep(0.6)
            ctx.pane_count = 2

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-3] sending Alt+H")
        injector.send_keys(hwnd, "%h")
        time.sleep(0.6)

        path = artifact_dir / "after_split_horizontal.png"
        capturer.save(hwnd, path)
        ctx.pane_count = 3

        return make_outcome(
            "MQ-3", artifact_dir, started,
            screenshots=[path],
            notes=f"hwnd=0x{hwnd:08X} pane_count={ctx.pane_count}",
        )
    except Exception as exc:
        return make_error_outcome("MQ-3", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-3"] = run
