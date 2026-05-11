# M-16-E PaneContainer Measurement Design

## Summary

M-16-E does not rewrite `PaneContainerControl` first. It first proves whether the current WPF visual tree rebuild behavior is actually costly or visually unstable.

The recommended scope is measurement-first: add deterministic pane split and workspace switch churn scenarios, record per-action timings and pane geometry, and add unit regression tests that prove focus-only layout updates do not rebuild the whole visual tree.

## Current Flow

```mermaid
flowchart TD
    VM["TerminalPaneLayoutViewModel"] --> Snap["TerminalPaneLayoutSnapshot"]
    Snap --> PC["PaneContainerControl.Layout"]
    PC --> Key{"shape key changed?"}
    Key -- "no" --> Focus["UpdateFocusVisuals only"]
    Key -- "yes" --> Build["BuildGrid / BuildElement"]
    Build --> Host["TerminalHostControl reuse/create"]
    Host --> HWND["child HWND + RenderSurface"]
```

`PaneContainerControl` already has a shape-key fast path. Focus changes are intentionally excluded from the shape key, so a focus-only update should keep `Content` and host instances stable.

## Problem

The old M-16-E stub assumed `BuildElement` might rebuild too often. After the MVVM boundary refactor and hardening work, that assumption is stale for focus-only changes, but split and workspace switch paths still deserve measurement.

The risk is not a confirmed user-visible bug. The risk is hidden cost: a split or workspace switch could cause a layout spike, geometry jump, or child HWND timing artifact.

## Design

| Area | Decision |
|------|----------|
| Primary strategy | Measure first, refactor only if evidence crosses the threshold |
| Scope | `pane-split-churn` and `workspace-switch-churn` scenarios plus focus fast-path unit tests |
| Artifact | `driver-events.csv`, `pane-geometry.json`, `workspace-events.csv`, `workspace-geometry.json`, existing `render-perf.csv`, `cpu.csv` |
| Entry point | Existing `scripts/test_automation.ps1 -Suite Measurement` |
| App launch contract | Measurement must not let `GhostWin.App` inherit redirected stdout/stderr handles |
| Threshold | Keep current structure if frame-drop ratio is below 5%; consider diff update if 5% or higher |

## Implementation Shape

1. Extend the automation runner with `pane-split-churn` and `workspace-switch-churn` scenarios.
2. Drive `Alt+V`, `Alt+H`, `Alt+H` and record timing around each split action.
3. Drive workspace creation and `Ctrl+Tab` switching, then record timing around each transition.
4. Capture UIA host count and bounding rectangles after each action.
5. Emit sidecar artifacts in the measurement output directory.
6. Add unit tests proving focus-only layout changes keep `PaneContainerControl.Content` stable.
7. Update M-16-E docs to use the current `tests/GhostWin.Automation.Runner` path.

## Measurement Launch Finding

During 2026-05-11 validation, `pane-split-churn` reproduced a blank terminal surface while PowerShell prompt text appeared in the measurement stdout log. The native DLLs were loaded and `render-perf` samples existed, so the issue was not a DLL reference failure.

Root cause: `measure_render_baseline.ps1` launched `GhostWin.App` with `ProcessStartInfo.UseShellExecute=false` while the measurement script's stdout was redirected. The child shell inherited redirected stdio through the app process, so shell output escaped to the measurement log instead of the ConPTY surface.

The launch contract is now: set `GHOSTWIN_RENDER_PERF` and `GHOSTWIN_LOG_FILE` only around `Start-Process`, and launch the app without inheriting redirected stdio handles.

## Non-Goals

- Do not introduce diff-based visual tree patching in this pass.
- Do not change native engine rendering.
- Do not make screenshot/pixel comparison a Daily gate.
- Do not depend on the removed `tests/GhostWin.MeasurementDriver` project.

## Validation

```powershell
dotnet test tests\GhostWin.App.Tests\GhostWin.App.Tests.csproj -c Debug --filter PaneContainerControlTests
dotnet test tests\GhostWin.Automation.Core.Tests\GhostWin.Automation.Core.Tests.csproj -c Debug --filter "MeasurementDriverContractTests|AutomationRunnerScriptTests"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\test_automation.ps1 -Suite Measurement -Configuration Release -MeasurementScenario pane-split-churn -DurationSec 10 -ResetSession
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\test_automation.ps1 -Suite Measurement -Configuration Release -MeasurementScenario workspace-switch-churn -DurationSec 10 -ResetSession
```

Latest validation after the launch fix:

- `pane-split-churn`: `DurationSec=20`, `ObservedPanes=4`, `ObservedActions=3`, `sample_count=19`, `total_us p95=38800.5`, prompt text visible in all panes.
- `workspace-switch-churn`: `DurationSec=10`, `ObservedPanes=2`, `ObservedActions=7`, `sample_count=23`, `total_us p95=44517.4`.

## Decision Rule

If both churn scenarios show acceptable frame timing and stable geometry, M-16-E closes as “measured, no structural rewrite.” If either shows frame-drop ratio at or above 5%, the next cycle can justify a targeted diff-based update.
