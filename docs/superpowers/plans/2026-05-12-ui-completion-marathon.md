# UI Completion Marathon Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the next five approved GhostWin follow-up tracks in order: MainWindow accessibility, spacing tokens, M-16-G P3 cleanup, M-16-E repeat measurement, and M-15 Stage B comparison.

**Architecture:** Keep changes small and evidence-based. UI behavior changes get unit or contract tests first; measurement/comparison work produces repeatable scripts or artifacts plus documentation.

**Tech Stack:** WPF/.NET 10, xUnit, FlaUI/UIA automation, PowerShell measurement scripts, Obsidian project notes.

---

### Task 1: MainWindow Accessibility Mini

**Files:**
- Modify: `src/GhostWin.App/MainWindow.xaml`
- Modify: `tests/GhostWin.Automation.Core.Tests/AutomationRunnerScriptTests.cs`
- Update docs in `C:\Users\Solit\obsidian\note\Projects\GhostWin\Backlog\tech-debt.md`

- [ ] Add failing contract tests for MainWindow chrome `TabIndex`, accessible names/help text, and focus visual inheritance.
- [ ] Implement the minimum XAML changes.
- [ ] Run focused tests, full `GhostWin.App.Tests`, and `GhostWin.Automation.Core.Tests`.
- [ ] Commit as `fix: improve main window accessibility`.

### Task 2: Spacing And Size Tokens

**Files:**
- Modify: `src/GhostWin.App/Themes/Spacing.xaml`
- Modify: XAML files with repeated Width/Height/FontSize constants
- Modify: `tests/GhostWin.Automation.Core.Tests/AutomationRunnerScriptTests.cs`

- [ ] Add contract tests for newly introduced size/font resources.
- [ ] Move repeated MainWindow/CommandPalette dimensions into resources without changing layout.
- [ ] Run XAML contract tests and app tests.
- [ ] Commit as `refactor: add ui size tokens`.

### Task 3: M-16-G P3 Cleanup

**Files:**
- Review: `docs/00-research/2026-05-08-ui-completeness-audit.md`
- Modify files selected by the remaining P3 items.

- [ ] Reconfirm unresolved P3 items against current code.
- [ ] Implement only small, low-risk items that are still real.
- [ ] Add tests/contracts for each item.
- [ ] Commit as one or more focused commits.

### Task 4: M-16-E Repeat Measurement

**Files:**
- Use: `scripts/test_automation.ps1`
- Use: `scripts/measure_render_repeats.ps1`
- Update: `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\m16-e-measurement.md`

- [ ] Run repeat measurement for pane split churn and workspace switch churn.
- [ ] Save artifact paths and summary metrics.
- [ ] Update milestone notes with measured values and any gaps.
- [ ] Commit docs/scripts only if files changed.

### Task 5: M-15 Stage B Competitor Comparison

**Files:**
- Review: `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\m15-render-baseline-comparison.md`
- Use existing measurement scripts where possible.

- [ ] Check availability of Windows Terminal, WezTerm, and Alacritty locally.
- [ ] Run comparable baseline measurements where installed.
- [ ] Document unavailable tools explicitly instead of inventing numbers.
- [ ] Commit the comparison report.
