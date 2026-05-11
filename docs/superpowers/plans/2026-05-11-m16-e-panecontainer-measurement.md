# M-16-E PaneContainer Measurement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a measurement-first M-16-E harness for PaneContainer visual tree churn without rewriting the WPF layout structure prematurely.

**Architecture:** Keep `PaneContainerControl` as the owner of dynamic WPF/HwndHost composition. Add tests that lock down the existing focus-only fast path, and extend the out-of-process measurement runner with `pane-split-churn` and `workspace-switch-churn` scenarios that emit action timing and pane geometry artifacts.

**Tech Stack:** WPF, .NET 10, xUnit, FluentAssertions, FlaUI UIA3, PowerShell measurement scripts.

---

## File Map

| File | Responsibility |
|------|----------------|
| `tests/GhostWin.App.Tests/Controls/PaneContainerControlTests.cs` | Unit regression for focus-only fast path and structural rebuild behavior |
| `tests/GhostWin.Automation.Core.Tests/MeasurementDriverContractTests.cs` | Contract tests for new runner scenario and result fields |
| `tests/GhostWin.Automation.Core.Tests/AutomationRunnerScriptTests.cs` | Script contract tests for new measurement scenario |
| `tests/GhostWin.Automation.Runner/Program.cs` | Dispatch `pane-split-churn` and `workspace-switch-churn` |
| `tests/GhostWin.Automation.Runner/Contracts/DriverResult.cs` | Include optional artifact names and action count |
| `tests/GhostWin.Automation.Runner/Scenario/PaneSplitChurnScenario.cs` | Drive splits, capture timings and geometry |
| `tests/GhostWin.Automation.Runner/Scenario/WorkspaceSwitchChurnScenario.cs` | Drive workspace creation/switches, capture timings and geometry |
| `tests/GhostWin.Automation.Runner/Infrastructure/GhostWinController.cs` | Reuse split actions and expose a small settle helper |
| `scripts/test_automation.ps1` | Accept both churn scenarios as measurement scenarios |
| `scripts/measure_render_baseline.ps1` | Route both churn scenarios, launch app without redirected stdio inheritance, pass output directory to runner, summarize artifacts |
| `src/engine-api/ghostwin_engine.cpp` | Draw the visible active terminal border inside the focused DX surface |
| `src/GhostWin.Services/PaneLayoutService.cs` | Reassert native surface focus even when the same pane is selected again |
| `docs/04-report/features/m16-e-panecontainer-measurement.html` | Korean HTML implementation report |
| Obsidian M-16-E docs | Reflect current runner path and results |

## Tasks

### Task 1: RED Tests

- [x] Add tests that expect `pane-split-churn` to parse as a driver scenario.
- [x] Add tests that expect scripts to contain `pane-split-churn` and `workspace-switch-churn`.
- [x] Add PaneContainer tests for focus-only content stability.
- [x] Run the targeted tests and confirm the new scenario/script expectations fail before implementation.

### Task 2: Runner Scenario

- [x] Add `PaneSplitChurnScenario`.
- [x] Add `WorkspaceSwitchChurnScenario`.
- [x] Dispatch both scenarios from `Program.cs`.
- [x] Extend `DriverResult` with optional `ObservedActions` and `Artifacts`.
- [x] Capture `driver-events.csv`, `pane-geometry.json`, `workspace-events.csv`, and `workspace-geometry.json`.

### Task 3: Script Integration

- [x] Add `pane-split-churn` to `scripts/test_automation.ps1`.
- [x] Add `pane-split-churn` to `scripts/measure_render_baseline.ps1`.
- [x] Add `workspace-switch-churn` to both measurement scripts.
- [x] Pass `--artifact-dir` to the runner.
- [x] Include artifact names in `summary.txt`.

### Task 4: Documentation

- [x] Create the HTML implementation report.
- [x] Update HTML/report docs with both churn scenarios.
- [x] Update Obsidian `Milestones/m16-e-measurement.md`.
- [x] Update Obsidian index/roadmap/backlog status text if measurement is implemented.

### Task 5: Verification And Commit

- [x] Run App tests for PaneContainer.
- [x] Run Automation Core contract tests.
- [x] Run Debug build or targeted project builds.
- [x] Run the new measurement scenarios if Release app artifacts are available.
- [x] Commit with an English message.

### Task 6: Measurement Launch Fix

- [x] Reproduce blank terminal surface during measurement with redirected stdout.
- [x] Confirm native DLLs load and render samples exist, ruling out DLL reference failure.
- [x] Identify redirected stdio inheritance from `UseShellExecute=false` as the root cause.
- [x] Change `Start-GhostWinApp` to launch through `Start-Process` with temporary environment variables.
- [x] Add a script contract test that rejects `UseShellExecute = $false`.
- [x] Re-run `pane-split-churn` and confirm stdout no longer receives PowerShell prompt text.
- [x] Re-run `workspace-switch-churn` after the launch fix.

### Task 7: Visual Proof And Active Border Fix

- [x] Add live window capture before app shutdown for `pane-split-churn` and `workspace-switch-churn`.
- [x] Normalize relative `-OutputDir` to an absolute path before setting `GHOSTWIN_LOG_FILE`.
- [x] Convert UIA logical pane bounds to physical screenshot pixels with `GetDpiForWindow`.
- [x] Add text-pixel detection for each pane crop.
- [x] Add active-border 4-edge completeness detection.
- [x] Reproduce the restored-window active border clipping with the visual proof gate.
- [x] Confirm WPF-only 1 DIP border/frame does not fully solve `HwndHost` right/bottom clipping.
- [x] Draw the visible active border inside the focused DX surface.
- [x] Reassert focused pane through `PaneContainerControl.ApplyLayout`.
- [x] Reassert native `SurfaceFocus` when `PaneLayoutService.SetFocused` receives the already-focused pane.
- [x] Re-run both Release measurement scenarios and confirm `visual_valid=True`.
