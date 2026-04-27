// e2e-headless-input T-5 (2026-04-09) — FlaUI cross-validation tool.
//
// Purpose: empirically confirm whether FlaUI's SendInput path drives GhostWin's
// Alt+V / Ctrl+T / Ctrl+Shift+W chords when our Python ctypes SendInput path
// does not. Used to narrow Design §3.1.2 scenario selection from a black-box
// vantage point independent of scripts/e2e/e2e_operator/input.py.
//
// DO NOT run from a Claude Code bash session — the whole point of this tool
// is that it expects to be invoked from an interactive user session where the
// GhostWin window can be brought to foreground (or at least receive focus)
// manually by the user during the 3-second grace window.
//
// Usage (user hardware only):
//   1. Build:   dotnet build tests/e2e-flaui-cross-validation -c Release
//   2. Start GhostWin.App:
//        scripts\build_wpf.ps1 -Config Release  # if not already built
//        src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe
//   3. Run FlaUI PoC:
//        dotnet run --project tests/e2e-flaui-cross-validation -c Release
//      or the raw exe at tests\e2e-flaui-cross-validation\bin\Release\net10.0-windows\GhostWin.FlaUi.CrossValidation.exe
//   4. When the tool prints "3 seconds — click GhostWin window now", click
//      the GhostWin main window to give it focus. The tool will then inject
//      the chord sequence via FlaUI.Core.Input.Keyboard.TypeSimultaneously.
//   5. Observe the GhostWin window + optionally capture screenshots via
//      PrintWindow. Log expected vs observed behaviour in the Do-phase
//      evidence sheet.
//
// References:
//   docs/02-design/features/e2e-headless-input.design.md §2.3, §3.1.1 T-5
//   docs/01-plan/features/e2e-headless-input.plan.md v0.2 §5.2 G
//   FlaUI Keyboard.cs source:
//     https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.Core/Input/Keyboard.cs

using System;
using System.Diagnostics;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI; // VirtualKeyShort

namespace GhostWin.FlaUi.CrossValidation;

internal static class Program
{
    private const string TargetProcessName = "GhostWin.App";
    private const int FocusGraceMs = 3_000;
    private const int ChordSettleMs = 600;

    private static int Main(string[] args)
    {
        Console.WriteLine("=== GhostWin FlaUI Cross-Validation (e2e-headless-input T-5) ===");
        Console.WriteLine();

        // 1. Find the target process.
        var procs = Process.GetProcessesByName(TargetProcessName);
        if (procs.Length == 0)
        {
            Console.Error.WriteLine(
                $"ERROR: no process named '{TargetProcessName}' is running. " +
                "Start GhostWin.App from the built output before launching this tool.");
            return 2;
        }
        var target = procs[0];
        Console.WriteLine($"Found {TargetProcessName} pid={target.Id}, " +
                          $"MainWindowHandle=0x{target.MainWindowHandle.ToInt64():X8}");

        // 2. Attach via FlaUI. FlaUI.Core.Application.Attach(int) wraps the
        //    process and exposes MainWindow enumeration. No SendInput yet.
        Application app;
        try
        {
            app = Application.Attach(target.Id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: FlaUI Application.Attach failed: {ex.Message}");
            return 3;
        }
        Console.WriteLine($"FlaUI attached. ProcessName={app.Name}, " +
                          $"ProcessId={app.ProcessId}, Is64Bit={app.IsStoreApp}");

        // 3. Give the user time to click the GhostWin window so it has focus.
        //    FlaUI does not forcibly steal foreground — consistent with the
        //    non-goals of this RCA tool.
        Console.WriteLine();
        Console.WriteLine($"{FocusGraceMs / 1000} seconds — click the GhostWin window NOW " +
                          "so it has keyboard focus, then wait...");
        Thread.Sleep(FocusGraceMs);

        // 4. Inject Alt+V (vertical split). Alt is Key.System in WPF, so this
        //    exercises the WM_SYSKEYDOWN path (the one that historically worked
        //    in attempts #1-#3).
        Console.WriteLine();
        Console.WriteLine("Step 1/3 — injecting Alt+V (expect: vertical split)");
        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_V);
        Thread.Sleep(ChordSettleMs);

        // 5. Inject Ctrl+T (new workspace). Ctrl is NOT Key.System, so this is
        //    the chord that historically failed in attempts #1-#3. If FlaUI
        //    succeeds here where our Python SendInput does not, the delta is
        //    localized to e2e_operator/input.py. If both fail, H-RCA4 (child
        //    HWND WM_KEYDOWN consumption) is the dominant suspect and the
        //    T-Main fix is validated empirically from an orthogonal path.
        Console.WriteLine("Step 2/3 — injecting Ctrl+T (expect: new workspace entry in sidebar)");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        Thread.Sleep(ChordSettleMs);

        // 6. Inject Ctrl+Shift+W (close focused pane). Triple-modifier chord —
        //    stresses the ordering in Keyboard.TypeSimultaneously (press in
        //    order, release in reverse). This is the key that all three
        //    earlier attempts also failed on.
        Console.WriteLine("Step 3/3 — injecting Ctrl+Shift+W (expect: active pane close)");
        Keyboard.TypeSimultaneously(
            VirtualKeyShort.CONTROL,
            VirtualKeyShort.SHIFT,
            VirtualKeyShort.KEY_W);
        Thread.Sleep(ChordSettleMs);

        Console.WriteLine();
        Console.WriteLine("DONE. Manually verify each chord via:");
        Console.WriteLine("  - GhostWin window visual state (pane count, sidebar entries)");
        Console.WriteLine("  - %LocalAppData%\\GhostWin\\diagnostics\\keyinput.log " +
                          "(set GHOSTWIN_KEYDIAG=3 before starting GhostWin.App)");
        Console.WriteLine("  - optional: PrintWindow screenshot via scripts/e2e/e2e_operator");
        Console.WriteLine();
        Console.WriteLine(
            "Record observations in the Do-phase evidence sheet under " +
            "docs/04-report/e2e-headless-input.report.md §T-5.");
        return 0;
    }
}
