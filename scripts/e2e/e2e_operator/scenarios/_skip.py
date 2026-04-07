"""Dependency cascade skip logic per Design D18.

DEPENDENCY_PARENTS maps each MQ-N to its required parents. If any parent
outcome is ERROR or SKIPPED the dependent scenario is also SKIPPED, and the
reason is propagated into summary.skipped_list[].

Dependency graph from §4.2:

    MQ-1 (root)
    ├── MQ-2
    │   ├── MQ-3
    │   ├── MQ-4  (independent once 2 panes exist)
    │   └── MQ-5
    ├── MQ-6
    │   └── MQ-7
    └── MQ-8  (needs only MQ-1)

Fail/skip cascade rules:
  MQ-1 fails → MQ-2..8 all skip
  MQ-2 fails → MQ-3, MQ-4, MQ-5 skip
  MQ-6 fails → MQ-7 skip
"""
from __future__ import annotations

from typing import TYPE_CHECKING

from ._base import ScenarioStatus

if TYPE_CHECKING:
    from ._base import ScenarioOutcome


# Maps scenario ID → list of parent IDs that must have succeeded.
DEPENDENCY_PARENTS: dict[str, list[str]] = {
    "MQ-1": [],
    "MQ-2": ["MQ-1"],
    "MQ-3": ["MQ-2"],
    "MQ-4": ["MQ-2"],
    "MQ-5": ["MQ-2"],
    "MQ-6": ["MQ-1"],
    "MQ-7": ["MQ-6"],
    "MQ-8": ["MQ-1"],
}

# Canonical execution order — always this sequence regardless of which are registered.
CANONICAL_ORDER: list[str] = ["MQ-1", "MQ-2", "MQ-3", "MQ-4", "MQ-5", "MQ-6", "MQ-7", "MQ-8"]


def should_skip(
    scenario: str,
    results: dict[str, "ScenarioOutcome"],
) -> tuple[bool, str | None]:
    """Check whether `scenario` should be skipped due to a failed parent.

    Args:
        scenario: Scenario ID to check (e.g. "MQ-3").
        results: Dict of already-completed ScenarioOutcome values keyed by scenario ID.
                 Parents not yet present in results are treated as "not yet run" and
                 do NOT trigger a skip — the caller must ensure topological order.

    Returns:
        (True, reason_str) if the scenario should be skipped.
        (False, None)      if it is safe to run.
    """
    parents = DEPENDENCY_PARENTS.get(scenario, [])
    for parent in parents:
        outcome = results.get(parent)
        if outcome is None:
            # Parent not yet in results — caller controls ordering, skip check.
            continue
        if outcome.status in (ScenarioStatus.ERROR, ScenarioStatus.SKIPPED):
            reason = f"parent {parent} status={outcome.status.value}"
            return True, reason
    return False, None
