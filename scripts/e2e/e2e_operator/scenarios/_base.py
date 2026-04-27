"""Scenario base contract.

A scenario is a callable (function or class.run) that:
1. Takes (artifact_dir: Path, capturer: WindowCapturer, ctx: ScenarioContext) — never globals
2. May call sibling helpers (launch_app, send_keys, etc.)
3. Captures one or more screenshots into artifact_dir
4. Writes a metadata.json describing what was captured
5. Returns a ScenarioOutcome (NOT pass/fail — Operator does NOT judge; Evaluator does)

Design reference: docs/02-design/features/e2e-test-harness.design.md §2.4 D17-D20
"""
from __future__ import annotations

from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Protocol
import enum


class ScenarioStatus(enum.Enum):
    OK = "ok"               # operator finished without throwing; screenshots saved
    SKIPPED = "skipped"     # dependency failed — see _skip.should_skip
    ERROR = "error"         # operator raised — distinguish from evaluator FAIL
    NOT_RUN = "not_run"     # not in target set for this run


@dataclass
class ScenarioOutcome:
    """Result produced by the Operator for one scenario.

    Crucially, this does NOT contain pass/fail — that is the Evaluator's job (D19/D20).
    The Operator only reports whether it could *execute* the scenario successfully
    (screenshots saved) or not (error / skip).
    """
    scenario: str                       # "MQ-1"
    status: ScenarioStatus
    artifact_dir: Path
    screenshots: list[Path] = field(default_factory=list)
    operator_notes: str = ""            # freeform notes (e.g. timing, window rect)
    error: str | None = None            # populated when status == ERROR
    started_at: str = ""                # ISO8601 timestamp
    finished_at: str = ""               # ISO8601 timestamp

    def to_dict(self) -> dict:
        """Serialize to JSON-compatible dict. Called when writing metadata.json."""
        d = asdict(self)
        d["status"] = self.status.value
        d["artifact_dir"] = str(self.artifact_dir)
        d["screenshots"] = [str(p) for p in self.screenshots]
        return d


class ScenarioFn(Protocol):
    """Callable signature that every registered scenario must satisfy."""
    def __call__(
        self,
        artifact_dir: Path,
        capturer: object,
        ctx: "ScenarioContext",
    ) -> ScenarioOutcome: ...


@dataclass
class ScenarioContext:
    """Shared mutable state threaded through a scenario chain run (D17).

    In --all mode the runner launches one GhostWinApp instance and passes this
    context between MQ-1 through MQ-8 so each scenario builds on the prior state.
    In single-scenario mode the context still exists but app is None on entry and
    the scenario is responsible for launching / shutting down.

    Typed loosely (object | None) to avoid circular imports with app_lifecycle.
    """
    app: object | None = None           # subprocess.Popen (or GhostWinApp wrapper)
    hwnd: int | None = None             # top-level HWND
    workspace_count: int = 1
    pane_count: int = 1
