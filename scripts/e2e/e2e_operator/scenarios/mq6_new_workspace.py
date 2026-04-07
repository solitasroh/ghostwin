"""MQ-6: Create new workspace via Ctrl+T.

Action: Send Ctrl+T.
Expected: A new workspace entry appears in the sidebar; the active workspace
switches to the new one which contains a fresh single pane.

Pre: MQ-1 succeeded (app running with at least the initial workspace).
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

        before_workspaces = ctx.workspace_count

        safe_focus(hwnd)
        time.sleep(0.2)

        logger.info("[MQ-6] sending Ctrl+T")
        injector.send_keys(hwnd, "^t")
        # New workspace creation involves new ConPTY session + sidebar update;
        # allow extra settle time for shell prompt to render in the new pane.
        time.sleep(1.5)

        path = artifact_dir / "after_new_workspace.png"
        capturer.save(hwnd, path)
        ctx.workspace_count = before_workspaces + 1

        return make_outcome(
            "MQ-6", artifact_dir, started,
            screenshots=[path],
            notes=f"hwnd=0x{hwnd:08X} workspace_count={before_workspaces}->{ctx.workspace_count}",
        )
    except Exception as exc:
        return make_error_outcome("MQ-6", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-6"] = run
