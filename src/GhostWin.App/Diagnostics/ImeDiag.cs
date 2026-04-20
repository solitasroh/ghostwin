// ImeDiag — diagnostic logger for M-13 IME composition pipeline (Backspace stale resurrect investigation).
//
// Activation: GHOSTWIN_IMEDIAG environment variable.
//   unset / "0" → disabled (early-out, zero IO)
//   "1"         → enabled (Debug.WriteLine + file)
//
// Output sinks (KeyDiag pattern — WPF GUI process has no usable stderr handle):
//   1. Debug.WriteLine → VS Output > Debug
//   2. File: %LocalAppData%\GhostWin\diagnostics\imediag.log (append, ISO8601 UTC)
//
// Format: [ISO8601 UTC | #seq] event | detail
//
// Removal plan: After M-13 Backspace race is fully diagnosed and fixed,
// delete this file + the GHOSTWIN_IMEDIAG entry in launchSettings.json.

using System.Diagnostics;
using System.IO;
using System.Text;

namespace GhostWin.App.Diagnostics;

internal static class ImeDiag
{
    private static int _level = -1;
    private static int _seq;
    private static readonly object _lock = new();
    private static string? _logPath;

    private const int LEVEL_OFF = 0;
    private const int LEVEL_ON  = 1;

    private static int GetLevel()
    {
        if (_level >= 0) return _level;
        var raw = Environment.GetEnvironmentVariable("GHOSTWIN_IMEDIAG");
        _level = string.IsNullOrEmpty(raw) || raw == "0" ? LEVEL_OFF : LEVEL_ON;
        return _level;
    }

    public static void Log(string evt, string? detail = null)
    {
        if (GetLevel() < LEVEL_ON) return;
        try
        {
            int seq = Interlocked.Increment(ref _seq);
            var sb = new StringBuilder(200);
            sb.Append('[').Append(DateTime.UtcNow.ToString("O"))
              .Append("|#").Append(seq.ToString("D4"))
              .Append("] ").Append(evt);
            if (!string.IsNullOrEmpty(detail))
                sb.Append(" | ").Append(detail);
            var line = sb.ToString();

            // Sink 1 — Debug.WriteLine (VS Output > Debug)
            Debug.WriteLine("[IME-DIAG] " + line);

            // Sink 2 — File (KeyDiag pattern, WPF stderr is unreachable)
            lock (_lock)
            {
                try
                {
                    if (_logPath == null)
                    {
                        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var dir = Path.Combine(localApp, "GhostWin", "diagnostics");
                        Directory.CreateDirectory(dir);
                        _logPath = Path.Combine(dir, "imediag.log");

                        // First call of this process — write a session-start marker so
                        // inspect tooling can isolate the current run from prior data.
                        var pid = Environment.ProcessId;
                        var marker = $"=== SESSION START pid={pid} utc={DateTime.UtcNow:O} ===";
                        File.AppendAllText(_logPath, Environment.NewLine + marker + Environment.NewLine);
                    }
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
                catch (Exception fileEx)
                {
                    Debug.WriteLine($"[IME-DIAG] file write failed: {fileEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IME-DIAG] log failed: {ex.Message}");
        }
    }

    /// <summary>Force the session-start marker to be written immediately on app boot,
    /// even before any other Log() call. Lets us tell "diagnostics didn't run" apart
    /// from "diagnostics ran but caught no events".</summary>
    public static void EnsureSessionMarker()
    {
        Log("BOOT", $"GHOSTWIN_IMEDIAG={Environment.GetEnvironmentVariable("GHOSTWIN_IMEDIAG") ?? "<unset>"}");
    }

    /// <summary>Render a wstring as printable ASCII + \uXXXX escapes for safe logging.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "''";
        var sb = new StringBuilder(text.Length + 4);
        sb.Append('\'');
        foreach (var ch in text)
        {
            if (ch >= 0x20 && ch < 0x7F)
                sb.Append(ch);
            else
                sb.Append("\\u").Append(((int)ch).ToString("X4"));
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
