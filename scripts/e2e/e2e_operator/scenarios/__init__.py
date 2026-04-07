"""Scenario registry for GhostWin E2E test harness.

SCENARIO_REGISTRY is populated by Step 6/7 MQ-1~8 implementation files.
This file intentionally ships empty so runner.py and sibling workers can
import from here before MQ scenarios exist.

Usage (Step 6 implementers):
    # In mq1_initial_render.py:
    from e2e_operator.scenarios import SCENARIO_REGISTRY
    from e2e_operator.scenarios._base import ScenarioOutcome, ScenarioStatus, ScenarioContext
    from pathlib import Path

    def run(artifact_dir: Path, capturer, ctx: ScenarioContext) -> ScenarioOutcome:
        ...

    SCENARIO_REGISTRY["MQ-1"] = run

    # Then in this __init__.py, add:
    #   from . import mq1_initial_render  # noqa: F401
"""
from __future__ import annotations

from ._base import ScenarioOutcome, ScenarioStatus, ScenarioContext, ScenarioFn
from ._skip import DEPENDENCY_PARENTS, CANONICAL_ORDER, should_skip


# Registry populated by Steps 6 and 7 (MQ-1 through MQ-8 implementation files).
# Key: scenario ID string (e.g. "MQ-1")
# Value: ScenarioFn — callable(artifact_dir, capturer, ctx) -> ScenarioOutcome
SCENARIO_REGISTRY: dict[str, ScenarioFn] = {}


# Step 6/7: MQ-1 through MQ-8 implementations register themselves on import.
from . import mq1_initial_render  # noqa: F401
from . import mq2_split_vertical  # noqa: F401
from . import mq3_split_horizontal  # noqa: F401
from . import mq4_mouse_focus  # noqa: F401
from . import mq5_pane_close  # noqa: F401
from . import mq6_new_workspace  # noqa: F401
from . import mq7_workspace_switch  # noqa: F401
from . import mq8_window_resize  # noqa: F401


def list_scenarios() -> list[str]:
    """Return sorted list of currently-registered scenario IDs.

    Will be empty until Step 6 MQ-1 is implemented and registered.
    """
    return sorted(SCENARIO_REGISTRY.keys())


def get_scenario(name: str) -> ScenarioFn:
    """Retrieve a scenario callable by ID.

    Raises:
        KeyError: with a helpful message listing available scenarios.
    """
    if name not in SCENARIO_REGISTRY:
        available = list_scenarios()
        if available:
            hint = f"Available: {', '.join(available)}"
        else:
            hint = "Registry is empty — Step 6 MQ-1 implementation not yet registered."
        raise KeyError(f"Unknown scenario {name!r}. {hint}")
    return SCENARIO_REGISTRY[name]


def all_scenarios_in_order() -> list[str]:
    """Return the canonical MQ-1..MQ-8 execution order.

    This is the correct sequence for --all mode regardless of which scenarios
    are currently registered. The runner uses this to determine execution order
    AND to emit NOT_RUN outcomes for unregistered entries.
    """
    return list(CANONICAL_ORDER)


__all__ = [
    "SCENARIO_REGISTRY",
    "ScenarioOutcome",
    "ScenarioStatus",
    "ScenarioContext",
    "ScenarioFn",
    "DEPENDENCY_PARENTS",
    "CANONICAL_ORDER",
    "should_skip",
    "list_scenarios",
    "get_scenario",
    "all_scenarios_in_order",
]
