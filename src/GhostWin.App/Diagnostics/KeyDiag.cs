// KeyDiag — diagnostic key-input logger for e2e-ctrl-key-injection root cause analysis.
//
// Design reference: docs/02-design/features/e2e-ctrl-key-injection.design.md §4
//
// Activation (double-gated, NFR-01 Release leak prevention):
//   - All public methods carry [Conditional("DEBUG")] → Release builds compile out the
//     call site itself (zero cost in Release, no string formatting, no allocation).
//   - Even in Debug, file output requires GHOSTWIN_KEYDIAG environment variable to be
//     set (any non-empty value enables ENTRY level; "2" enables ENTRY+EXIT; "3" enables
//     ENTRY+BRANCH+EXIT). When the env var is unset, methods return after a cheap
//     early-out so smoke launches stay quiet.
//
// Output sink: %LocalAppData%\GhostWin\diagnostics\keyinput.log (D2)
//   - Append mode, line-per-event, ISO8601 UTC timestamp.
//   - Lazy directory creation, fail silently to Debug.WriteLine if path unavailable.
//   - Thread-safe via static lock; logging is on the WPF UI thread so contention is
//     not a concern in practice.
//
// Field spec (11 fields per §4.2):
//   timestamp | seq | evt | key | syskey | mods | osrc | foc | ws | pane | dispatch
// Plus H1 deep-dive fields appended to ENTRY events: isCtrlDown_kbd, isCtrlDown_win32.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.Diagnostics;

internal static class KeyDiag
{
    private static readonly object _lock = new();
    private static int _seq;
    private static string? _logPath;
    private static int _level = -1; // -1 = uninitialized, 0 = disabled, 1+ = enabled

    // GHOSTWIN_KEYDIAG levels:
    //   unset / "0" → disabled (early-out, no file IO)
    //   "1"         → ENTRY only
    //   "2"         → ENTRY + EXIT
    //   "3"         → ENTRY + BRANCH + EXIT (verbose)
    private const int LEVEL_OFF = 0;
    private const int LEVEL_ENTRY = 1;
    private const int LEVEL_EXIT = 2;
    private const int LEVEL_BRANCH = 3;

    private static int GetLevel()
    {
        if (_level >= 0) return _level;
        var raw = Environment.GetEnvironmentVariable("GHOSTWIN_KEYDIAG");
        if (string.IsNullOrEmpty(raw)) { _level = LEVEL_OFF; return _level; }
        _level = raw switch
        {
            "1" => LEVEL_ENTRY,
            "2" => LEVEL_EXIT,
            "3" => LEVEL_BRANCH,
            _ => LEVEL_ENTRY, // any other non-empty value enables ENTRY-only
        };
        return _level;
    }

    // R4 diagnosis: Conditional removed (was [Conditional("DEBUG")]).
    // NFR-01 leak prevention now relies solely on the GHOSTWIN_KEYDIAG env-var
    // gate via GetLevel() — when unset (Release default), GetLevel() returns
    // LEVEL_OFF and every public method returns immediately without IO.
    // Cost in Release: one Environment.GetEnvironmentVariable on first call
    // per process + one cached int compare per subsequent call. ~0 perf impact.
    public static void LogEntry(KeyEventArgs e, IWorkspaceService? workspace)
    {
        if (GetLevel() < LEVEL_ENTRY) return;
        try
        {
            var sb = new StringBuilder(256);
            AppendCommon(sb, "ENTRY", e, workspace, dispatch: "pending");
            // H1 primary deep-dive fields — modifier state cross-check
            bool isCtrlKbd = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool isCtrlWin32 = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            sb.Append(" isCtrlDown_kbd=").Append(isCtrlKbd ? "true" : "false");
            sb.Append(" isCtrlDown_win32=").Append(isCtrlWin32 ? "true" : "false");
            sb.Append(']');
            WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyDiag] LogEntry failed: {ex.Message}");
        }
    }

    // R4 diagnosis: Conditional removed (was [Conditional("DEBUG")]).
    // NFR-01 leak prevention now relies solely on the GHOSTWIN_KEYDIAG env-var
    // gate via GetLevel() — when unset (Release default), GetLevel() returns
    // LEVEL_OFF and every public method returns immediately without IO.
    // Cost in Release: one Environment.GetEnvironmentVariable on first call
    // per process + one cached int compare per subsequent call. ~0 perf impact.
    public static void LogBranch(string arm, KeyEventArgs e)
    {
        if (GetLevel() < LEVEL_BRANCH) return;
        try
        {
            var sb = new StringBuilder(160);
            AppendCommon(sb, "BRANCH", e, workspace: null, dispatch: arm);
            sb.Append(']');
            WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyDiag] LogBranch failed: {ex.Message}");
        }
    }

    // R4 diagnosis: Conditional removed (was [Conditional("DEBUG")]).
    // NFR-01 leak prevention now relies solely on the GHOSTWIN_KEYDIAG env-var
    // gate via GetLevel() — when unset (Release default), GetLevel() returns
    // LEVEL_OFF and every public method returns immediately without IO.
    // Cost in Release: one Environment.GetEnvironmentVariable on first call
    // per process + one cached int compare per subsequent call. ~0 perf impact.
    public static void LogExit(string outcome, KeyEventArgs e)
    {
        if (GetLevel() < LEVEL_EXIT) return;
        try
        {
            var sb = new StringBuilder(160);
            AppendCommon(sb, "EXIT", e, workspace: null, dispatch: outcome);
            sb.Append(" handled=").Append(e.Handled ? "true" : "false");
            sb.Append(']');
            WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyDiag] LogExit failed: {ex.Message}");
        }
    }

    // R4 diagnosis: Conditional removed (was [Conditional("DEBUG")]).
    // NFR-01 leak prevention now relies solely on the GHOSTWIN_KEYDIAG env-var
    // gate via GetLevel() — when unset (Release default), GetLevel() returns
    // LEVEL_OFF and every public method returns immediately without IO.
    // Cost in Release: one Environment.GetEnvironmentVariable on first call
    // per process + one cached int compare per subsequent call. ~0 perf impact.
    public static void LogKeyBindCommand(string commandName)
    {
        if (GetLevel() < LEVEL_ENTRY) return;
        try
        {
            var sb = new StringBuilder(128);
            int seq = Interlocked.Increment(ref _seq);
            sb.Append('[').Append(DateTime.UtcNow.ToString("O"))
              .Append("|#").Append(seq.ToString("D4"))
              .Append("|evt=KEYBIND command=").Append(commandName)
              .Append(" source=unknown")
              .Append(" mods=").Append(Keyboard.Modifiers.ToString())
              .Append(']');
            WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyDiag] LogKeyBindCommand failed: {ex.Message}");
        }
    }

    private static void AppendCommon(
        StringBuilder sb, string evt, KeyEventArgs e,
        IWorkspaceService? workspace, string dispatch)
    {
        int seq = Interlocked.Increment(ref _seq);
        sb.Append('[').Append(DateTime.UtcNow.ToString("O"))
          .Append("|#").Append(seq.ToString("D4"))
          .Append("|evt=").Append(evt)
          .Append(" key=").Append(e.Key.ToString())
          .Append(" syskey=").Append(e.SystemKey.ToString())
          .Append(" mods=").Append(Keyboard.Modifiers.ToString())
          .Append(" osrc=").Append(e.OriginalSource?.GetType().Name ?? "null")
          .Append(" foc=").Append(Keyboard.FocusedElement?.GetType().Name ?? "null");

        if (workspace != null)
        {
            sb.Append(" ws=").Append(workspace.ActiveWorkspaceId?.ToString() ?? "none")
              .Append(" pane=").Append(workspace.ActivePaneLayout?.FocusedPaneId?.ToString() ?? "none");
        }

        sb.Append(" dispatch=").Append(dispatch);
    }

    private static void WriteLine(string line)
    {
        // Always echo to debug stream — visible under DebugView/VS even without file.
        Debug.WriteLine(line);

        lock (_lock)
        {
            try
            {
                if (_logPath == null)
                {
                    var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var dir = Path.Combine(localApp, "GhostWin", "diagnostics");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "keyinput.log");
                }
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyDiag] file write failed: {ex.Message}");
            }
        }
    }

    // Win32 GetKeyState — H1 cross-check whether WPF KeyboardDevice agrees with raw VK state.
    private const int VK_CONTROL = 0x11;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
