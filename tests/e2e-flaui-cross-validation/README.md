# e2e-headless-input T-5 — FlaUI Cross-Validation PoC

**Status**: Scaffold only (Do-phase 2026-04-09). Execution deferred to user
interactive session; the cto-lead builds this project but does NOT run it.

## Purpose

Cross-validate the Root Cause Analysis outcome from
`docs/02-design/features/e2e-headless-input.design.md` §2.3 (RCA-C) by driving
the same GhostWin.App binary via FlaUI (`FlaUI.UIA3` v5.0.0) instead of the
Python `ctypes SendInput` path in `scripts/e2e/e2e_operator/input.py`.

Because FlaUI's `Keyboard.TypeSimultaneously` also calls `User32.SendInput`
under the hood (verified from FlaUI source in Design §2.3.2), an asymmetric
pass/fail between the two tools would localise the fault to our Python layer
or to WPF focus scope rather than to the OS injection API itself.

## Build (safe for Claude bash)

```powershell
dotnet build tests/e2e-flaui-cross-validation -c Release
```

## Run (user interactive session only)

DO NOT run this from a bash session. Run from a real desktop session so the
GhostWin window can receive keyboard focus.

1. Build GhostWin.App:
   ```powershell
   scripts\build_wpf.ps1 -Config Release
   ```
2. Start GhostWin.App (optionally with KeyDiag enabled so the injected chords
   are logged to `%LocalAppData%\GhostWin\diagnostics\keyinput.log`):
   ```powershell
   $env:GHOSTWIN_KEYDIAG = "3"
   src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe
   ```
3. In a second terminal, run this PoC:
   ```powershell
   dotnet run --project tests/e2e-flaui-cross-validation -c Release
   ```
4. When the tool prints `3 seconds — click the GhostWin window NOW`, click
   the GhostWin main window to give it focus.
5. Observe:
   - `Alt+V` → expected: vertical split
   - `Ctrl+T` → expected: new workspace entry in sidebar
   - `Ctrl+Shift+W` → expected: active pane close
6. Review `keyinput.log` to see whether FlaUI-injected events land in the
   WPF `PreviewKeyDown` dispatcher and whether `IsCtrlDown_kbd` /
   `IsCtrlDown_win32` agree.

## Interpretation

See Design §2.3.3 for the three-way branch table. Short version:

| FlaUI result                                   | Implication                                                                                      |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| all three chords work                          | Delta is `e2e_operator/input.py` only. T-Main Ctrl-branch fix is likely sufficient                |
| Alt+V works, Ctrl+T / Ctrl+Shift+W fail        | Confirms H-RCA4 (child HWND WM_KEYDOWN consumption). T-Main bubble handler + helpers are required |
| all three fail                                 | Escalate — pass raw evidence to user, potentially R-RCA trigger                                   |

## Related

- `docs/01-plan/features/e2e-headless-input.plan.md` v0.2 §5.2 G, §10 Milestone 2a
- `docs/02-design/features/e2e-headless-input.design.md` v0.1 §2.3, §3.1.1 T-5
- `src/GhostWin.App/MainWindow.xaml.cs` — T-Main fix (bubble handler +
  `IsCtrlDown/IsShiftDown/IsAltDown` helpers)
- `src/GhostWin.App/Diagnostics/KeyDiag.cs` — 11-field diagnostic logger,
  activated via `GHOSTWIN_KEYDIAG` env var
