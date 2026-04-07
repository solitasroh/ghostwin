"""GhostWin E2E test runner — CLI entry point.

Usage:
    python scripts/e2e/runner.py --scenario MQ-1 [--run-id YYYYMMDD_HHMMSS]
    python scripts/e2e/runner.py --all [--run-id YYYYMMDD_HHMMSS]
    python scripts/e2e/runner.py --all --verbose
    python scripts/e2e/runner.py --all --no-shutdown

Design references:
    docs/02-design/features/e2e-test-harness.design.md §2.4 D17-D20, §3.2, §6.2, §9 Step 5

IMPORTANT — Operator vs Evaluator separation (D19/D20):
    This script is the OPERATOR. It:
      - Launches the app
      - Executes UI actions
      - Captures screenshots
      - Records OK / ERROR / SKIPPED outcomes

    It does NOT judge pass/fail — that is the EVALUATOR's job (a separate
    Claude Code Task invocation that reads the screenshots and metadata.json).
    The summary.json produced here has no "passed"/"failed"/"match_rate" keys.
"""
from __future__ import annotations

# sys.path fixup MUST happen before any e2e_operator imports.
# This allows `python scripts/e2e/runner.py` from any CWD.
import sys
from pathlib import Path

_E2E_DIR = Path(__file__).resolve().parent          # .../scripts/e2e/
if str(_E2E_DIR) not in sys.path:
    sys.path.insert(0, str(_E2E_DIR))

# Force UTF-8 stdout/stderr — Korean Windows defaults to cp949 which cannot
# encode em-dash and other unicode characters used in our help text and logs.
# This must run before any print/argparse output. PS1 wrapper also sets
# PYTHONIOENCODING=utf-8 for safety; this is a belt-and-suspenders fallback
# for direct python invocation.
for _stream_name in ("stdout", "stderr"):
    _stream = getattr(sys, _stream_name, None)
    if _stream is not None and getattr(_stream, "encoding", "").lower() != "utf-8":
        try:
            _stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError):
            pass  # older Python or non-reconfigurable stream — accept default

# DPI awareness must be the FIRST executable line after path fixup (D10).
# Import is lazy-safe here because dpi.py has no side effects on import —
# the actual ctypes call is deferred to main() per spec.
try:
    import e2e_operator.dpi as _dpi_mod
    _DPI_AVAILABLE = True
except ImportError:
    _DPI_AVAILABLE = False

import argparse
import datetime
import json
import logging
import traceback


def _iso_now() -> str:
    return datetime.datetime.now(tz=datetime.timezone.utc).isoformat(timespec="seconds")


def _default_run_id() -> str:
    return datetime.datetime.now().strftime("%Y%m%d_%H%M%S")


def _resolve_exe_path() -> Path:
    """Resolve GhostWin.App.exe from repo root (relative to this file's location)."""
    repo_root = _E2E_DIR.parents[1]   # scripts/e2e/ -> scripts/ -> repo root
    return (
        repo_root
        / "src"
        / "GhostWin.App"
        / "bin"
        / "x64"
        / "Release"
        / "net10.0-windows"
        / "GhostWin.App.exe"
    )


def _build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="runner.py",
        description=(
            "GhostWin E2E Operator — captures screenshots for Evaluator.\n"
            "Does NOT judge pass/fail; that is the Evaluator's job."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument(
        "--scenario",
        metavar="MQ-N",
        help="Run a single scenario (e.g. MQ-1).",
    )
    mode.add_argument(
        "--all",
        action="store_true",
        help="Run MQ-1 through MQ-8 in canonical dependency order.",
    )

    parser.add_argument(
        "--run-id",
        metavar="YYYYMMDD_HHMMSS",
        default=None,
        help="Unique run identifier (default: current timestamp).",
    )
    parser.add_argument(
        "--artifacts-dir",
        metavar="PATH",
        default=None,
        help="Root artifacts directory (default: scripts/e2e/artifacts/).",
    )
    parser.add_argument(
        "--exe",
        metavar="PATH",
        default=None,
        help="Path to GhostWin.App.exe (default: resolved from repo root).",
    )
    parser.add_argument(
        "--no-shutdown",
        action="store_true",
        help="Debug: leave GhostWin running after the run for inspection.",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Enable DEBUG-level logging.",
    )

    return parser


def _write_metadata(outcome_dict: dict, scenario_dir: Path) -> None:
    """Write per-scenario metadata.json."""
    metadata_path = scenario_dir / "metadata.json"
    metadata_path.write_text(
        json.dumps(outcome_dict, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )
    logging.debug("Wrote metadata: %s", metadata_path)


def _run_scenario(
    name: str,
    artifact_dir: Path,
    capturer: object,
    ctx: object,
) -> dict:
    """Execute one scenario, returning its outcome dict.

    Wraps the scenario callable in a try/except so any exception becomes
    an ERROR outcome rather than aborting the runner.
    """
    # Lazy import so --help works even if scenarios package is incomplete.
    from e2e_operator.scenarios import get_scenario
    from e2e_operator.scenarios._base import ScenarioOutcome, ScenarioStatus

    scenario_dir = artifact_dir / name
    scenario_dir.mkdir(parents=True, exist_ok=True)

    started = _iso_now()
    logging.info("[%s] starting", name)

    try:
        fn = get_scenario(name)
        outcome = fn(scenario_dir, capturer, ctx)
    except KeyError as exc:
        # Scenario not yet registered (Steps 6/7 not done)
        logging.warning("[%s] not registered: %s", name, exc)
        outcome = ScenarioOutcome(
            scenario=name,
            status=ScenarioStatus.ERROR,
            artifact_dir=scenario_dir,
            error=f"Scenario not registered: {exc}",
            started_at=started,
            finished_at=_iso_now(),
        )
    except Exception:
        tb = traceback.format_exc()
        logging.error("[%s] operator raised:\n%s", name, tb)
        outcome = ScenarioOutcome(
            scenario=name,
            status=ScenarioStatus.ERROR,
            artifact_dir=scenario_dir,
            error=tb,
            started_at=started,
            finished_at=_iso_now(),
        )

    if not outcome.started_at:
        outcome.started_at = started
    if not outcome.finished_at:
        outcome.finished_at = _iso_now()

    outcome_dict = outcome.to_dict()
    _write_metadata(outcome_dict, scenario_dir)

    logging.info("[%s] status=%s", name, outcome_dict["status"])
    return outcome_dict


def main() -> int:
    # --- D10: DPI awareness MUST be first executable line of main() ---
    if _DPI_AVAILABLE:
        _dpi_mod.set_per_monitor_v2()
    else:
        # Sibling worker (wpf-architect) hasn't completed dpi.py yet.
        # Fall back to direct ctypes call so we're never unprotected.
        import ctypes
        ctypes.windll.user32.SetProcessDpiAwarenessContext(-4)

    parser = _build_arg_parser()
    args = parser.parse_args()

    # --- Logging setup ---
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )

    run_id: str = args.run_id or _default_run_id()
    exe_path: Path = Path(args.exe) if args.exe else _resolve_exe_path()

    default_artifacts = _E2E_DIR / "artifacts"
    artifacts_root: Path = Path(args.artifacts_dir) if args.artifacts_dir else default_artifacts
    run_dir = artifacts_root / run_id
    run_dir.mkdir(parents=True, exist_ok=True)

    logging.info("=== GhostWin E2E run %s ===", run_id)
    logging.info("exe     : %s", exe_path)
    logging.info("run_dir : %s", run_dir)

    # --- Lazy import of e2e_operator modules (allows --help without siblings) ---
    try:
        from e2e_operator.capture import get_capturer
        capturer = get_capturer()
        capturer_name: str = getattr(capturer, "name", str(capturer))
    except Exception as exc:
        logging.error(
            "capture: get_capturer() failed — sibling worker (code-analyzer) "
            "may not have completed capture/ yet: %r", exc
        )
        capturer = None
        capturer_name = "unavailable"

    # --- Determine target scenario list ---
    from e2e_operator.scenarios import all_scenarios_in_order, should_skip, ScenarioStatus
    from e2e_operator.scenarios._base import ScenarioContext, ScenarioOutcome

    if args.all:
        targets: list[str] = all_scenarios_in_order()
    else:
        targets = [args.scenario]

    # --- Scenario chain mode (D17): shared context across the chain ---
    ctx = ScenarioContext()

    # In --all mode, try to launch the app once before the chain starts.
    # Individual scenarios may also launch/shutdown if needed (single mode).
    if args.all and capturer is not None:
        try:
            from e2e_operator import app_lifecycle
            logging.info("Launching GhostWin.App.exe for chain run...")
            proc, hwnd = app_lifecycle.launch_app(exe_path)
            ctx.app = proc
            ctx.hwnd = hwnd
            logging.info("App ready: hwnd=0x%08X", hwnd)
        except ImportError:
            logging.warning(
                "app_lifecycle not available yet (wpf-architect sibling still running). "
                "Scenarios will launch their own app instances."
            )
        except Exception as exc:
            logging.error("Failed to pre-launch app: %r — scenarios will handle individually", exc)

    results: dict[str, dict] = {}
    skipped_list: list[dict] = []
    error_count = 0

    for name in targets:
        # --- Dependency skip check (D18) ---
        # Reconstruct ScenarioOutcome objects from result dicts for the skip check.
        outcomes_so_far: dict[str, ScenarioOutcome] = {}
        for prev_name, prev_dict in results.items():
            prev_status = ScenarioStatus(prev_dict["status"])
            outcomes_so_far[prev_name] = ScenarioOutcome(
                scenario=prev_name,
                status=prev_status,
                artifact_dir=Path(prev_dict["artifact_dir"]),
                error=prev_dict.get("error"),
            )

        skip, reason = should_skip(name, outcomes_so_far)
        if skip:
            logging.info("[%s] SKIPPED: %s", name, reason)
            skip_outcome = ScenarioOutcome(
                scenario=name,
                status=ScenarioStatus.SKIPPED,
                artifact_dir=run_dir / name,
                operator_notes=reason or "",
                started_at=_iso_now(),
                finished_at=_iso_now(),
            )
            (run_dir / name).mkdir(parents=True, exist_ok=True)
            outcome_dict = skip_outcome.to_dict()
            _write_metadata(outcome_dict, run_dir / name)
            results[name] = outcome_dict
            skipped_list.append({"scenario": name, "reason": reason})
            continue

        # --- Execute scenario ---
        outcome_dict = _run_scenario(name, run_dir, capturer, ctx)
        results[name] = outcome_dict
        if outcome_dict["status"] == "error":
            error_count += 1

    # --- Shutdown if we launched in chain mode ---
    if args.all and ctx.app is not None and not args.no_shutdown:
        try:
            from e2e_operator import app_lifecycle
            logging.info("Shutting down GhostWin.App.exe...")
            app_lifecycle.shutdown_app(ctx.app, ctx.hwnd or 0)
        except Exception as exc:
            logging.warning("Shutdown failed (non-fatal): %r", exc)

    # --- Write summary.json (§6.2) ---
    # NOTE: "passed"/"failed"/"match_rate" are EVALUATOR concerns.
    # Operator only knows OK / ERROR / SKIPPED.
    ok_count = sum(1 for r in results.values() if r["status"] == "ok")
    err_count = sum(1 for r in results.values() if r["status"] == "error")
    skip_count = sum(1 for r in results.values() if r["status"] == "skipped")
    total = len(results)

    summary = {
        "run_id": run_id,
        "feature": "bisect-mode-termination",
        "framework_version": "e2e-test-harness v0.1",
        "total": total,
        "operator_ok": ok_count,
        "operator_error": err_count,
        "skipped": skip_count,
        "capturer_used": capturer_name,
        "scenarios": list(results.values()),
        "skipped_list": skipped_list,
        "notes": (
            "Operator complete. Run Evaluator Task in Claude Code to judge pass/fail. "
            "This summary intentionally omits 'passed'/'failed'/'match_rate' — "
            "those are evaluator concerns, not operator concerns (Design D19/D20)."
        ),
    }

    summary_path = run_dir / "summary.json"
    summary_path.write_text(
        json.dumps(summary, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )
    logging.info("summary.json written: %s", summary_path)

    # --- Print user-facing next-step message ---
    print(f"\n=== E2E Run {run_id} complete ===")
    print(f"Artifacts: {run_dir}/")
    print(f"Operator outcomes: OK={ok_count} ERROR={err_count} SKIPPED={skip_count}")
    print()
    print("Next: in Claude Code, invoke Task tool with:")
    print(f"  Prompt: {_E2E_DIR / 'evaluator_prompt.md'} (will be added in Step 8)")
    print(f"  Artifact dir: {run_dir}/")

    # Exit 0 if all OK or skipped; exit 1 if any ERROR (SKIPPED does not affect exit code).
    return 1 if error_count > 0 else 0


if __name__ == "__main__":
    sys.exit(main())
