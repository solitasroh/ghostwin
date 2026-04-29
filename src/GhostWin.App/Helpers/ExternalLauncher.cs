using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace GhostWin.App.Helpers;

/// <summary>
/// M-16-D D-05: thin wrapper over Process.Start for "Open in VS Code /
/// Cursor / Explorer" context menu commands. PATH probe results are
/// cached for the session — `IsAvailable` calls `where.exe` once per
/// executable name and reuses the result.
/// </summary>
public static class ExternalLauncher
{
    private static readonly ConcurrentDictionary<string, bool> _availability = new();

    public static bool IsAvailable(string executable)
    {
        return _availability.GetOrAdd(executable, name =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = name,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }

    public static bool TryOpenInVsCode(string cwd) => TryOpenInEditor("code", cwd);
    public static bool TryOpenInCursor(string cwd) => TryOpenInEditor("cursor", cwd);

    public static bool TryOpenInExplorer(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{cwd}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryOpenInEditor(string executable, string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        if (!IsAvailable(executable)) return false;
        try
        {
            // VS Code / Cursor accept a directory path argument and reuse the
            // same window if one is already open in that folder.
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"\"{cwd}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
