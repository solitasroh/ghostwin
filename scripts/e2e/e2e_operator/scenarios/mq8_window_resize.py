"""MQ-8: Window resize verification (Top Risk 4 — bisect resize propagation).

Action: Resize the window from 1280x800 to 1600x1000 via SetWindowPos. This
exercises the entire resize pipeline: WPF Grid measure/arrange → PaneContainer
host migration → engine swap chain release → DX11 resize → ConPTY pty resize.

Expected: All pane content scales/reflows to fit the new window size; glyphs
remain crisp; no black artifacts; PowerShell prompts re-render correctly.

Pre: MQ-1 succeeded (app running, any pane layout).

This is the "bisect Top Risk 4" scenario — the entire reason e2e-test-harness
exists (per Plan §1.2). Capture before+after for evaluator comparison.
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

# Larger size used for the resize check.
LARGE_W = 1600
LARGE_H = 1000


def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
    started = now_iso()
    artifact_dir.mkdir(parents=True, exist_ok=True)
    owns_app = False
    hwnd: int = 0

    try:
        proc, hwnd, owns_app = acquire_app(ctx, capturer)
        from e2e_operator import window

        safe_focus(hwnd)
        time.sleep(0.2)

        # Capture the "before" state (current geometry) for evaluator diff
        before_path = artifact_dir / "01_before_resize.png"
        capturer.save(hwnd, before_path)
        before_rect = window.get_window_rect(hwnd)

        logger.info("[MQ-8] resizing to %dx%d (was %s)", LARGE_W, LARGE_H, before_rect)
        window.normalize(hwnd, x=100, y=100, width=LARGE_W, height=LARGE_H)
        # Resize propagation: WPF arrange + engine swap chain release/recreate
        # + ConPTY resize. 800 ms is generous; bisect P0-2 added warm-up guard.
        time.sleep(1.0)

        after_path = artifact_dir / "02_after_resize.png"
        capturer.save(hwnd, after_path)
        after_rect = window.get_window_rect(hwnd)

        # Restore baseline for any subsequent scenarios (none in MQ-8 chain pos,
        # but defensive for re-runs)
        window.normalize(hwnd, x=100, y=100, width=1280, height=800)
        time.sleep(0.5)

        return make_outcome(
            "MQ-8", artifact_dir, started,
            screenshots=[before_path, after_path],
            notes=(
                f"hwnd=0x{hwnd:08X} "
                f"before_rect={before_rect} after_rect={after_rect} "
                f"target={LARGE_W}x{LARGE_H}"
            ),
        )
    except Exception as exc:
        return make_error_outcome("MQ-8", artifact_dir, started, exc,
                                  notes=f"hwnd=0x{hwnd:08X}")
    finally:
        release_if_owned(ctx, owns_app)


SCENARIO_REGISTRY["MQ-8"] = run
