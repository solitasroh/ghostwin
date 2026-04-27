"""MQ-5: Close focused pane via Ctrl+Shift+W.

Action: Send Ctrl+Shift+W to close the currently-focused pane.
Expected: Pane count decreases by 1; the sibling pane fills the freed space.

Pre: MQ-2 succeeded (≥2 panes exist).
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

        # Single mode: ensure ≥2 panes (MQ-5 depends on MQ-2)
        if owns_app:
            logger.info("[MQ-5] single mode prelude: Alt+V to create 2 panes")
            safe_focus(hwnd)
            time.sleep(0.2)
            injector.send_keys(hwnd, "%v")
            time.sleep(0.6)
            ctx.pane_count = 2

        before_count = ctx.pane_count

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-5] sending Ctrl+Shift+W")
        injector.send_keys(hwnd, "^+w")
        time.sleep(0.6)

        path = artifact_dir / "after_pane_close.png"
        capturer.save(hwnd, path)
        ctx.pane_count = max(1, before_count - 1)

        return make_outcome(
            "MQ-5", artifact_dir, started,
            screenshots=[path],
            notes=f"hwnd=0x{hwnd:08X} pane_count={before_count}->{ctx.pane_count}",
        )
    except Exception as exc:
        return make_error_outcome("MQ-5", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-5"] = run
