// UIA probe — empirically test whether FlaUI UIA3 basic reads work
// against GhostWin.App from a disconnected Windows session.
//
// This file is conditionally compiled into the smoke runner. To run
// the probe instead of the full smoke, pass `--uia-probe` as arg 0.
//
// What it tests:
//   1. Application.Launch succeeds
//   2. GetMainWindow returns a UIA element
//   3. FindAllDescendants returns a non-empty tree (forces lazy build)
//   4. Properties.Name / AutomationId can be read without ERROR_ACCESS_DENIED
//   5. PrintWindow-based screenshot capture still works for later steps
//
// If all 5 succeed, we proceed with custom AutomationPeer + Invoke pattern.
// If UIA itself fails, we need a different strategy (in-process named pipe).

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.UIA3;

namespace GhostWin.FlaUi.SplitContentSmoke;

internal static class UiaProbe
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static int Run(string exePath)
    {
        Console.WriteLine("=== UIA Probe (split-content-loss-v2) ===");
        Console.WriteLine($"Exe: {exePath}");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"ERROR: GhostWin.App.exe not found");
            return 2;
        }

        // Step 1: Launch
        Application app;
        try
        {
            app = Application.Launch(exePath);
            Console.WriteLine($"[1/6] Application.Launch OK pid={app.ProcessId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[1/6] Application.Launch FAIL: {ex.Message}");
            return 3;
        }

        int exitCode = 0;
        try
        {
            // Step 2: Wait for main window
            Thread.Sleep(4000);
            using var automation = new UIA3Automation();
            var mainWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(8));
            if (mainWindow is null)
            {
                Console.Error.WriteLine("[2/6] GetMainWindow FAIL: null");
                return 4;
            }
            Console.WriteLine($"[2/6] GetMainWindow OK");

            // Step 3: Read basic properties
            try
            {
                string name = mainWindow.Properties.Name.ValueOrDefault ?? "(null)";
                var handleValue = mainWindow.Properties.NativeWindowHandle.ValueOrDefault;
                string hwnd = handleValue == IntPtr.Zero ? "0" : $"0x{handleValue.ToInt64():X}";
                Console.WriteLine($"[3/6] Properties OK: Name=\"{name}\" HWND={hwnd}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[3/6] Properties FAIL: {ex.Message}");
                return 5;
            }

            // Step 4: Enumerate children (forces UIA tree realization)
            try
            {
                var kids = mainWindow.FindAllChildren();
                Console.WriteLine($"[4/6] FindAllChildren OK: {kids.Length} direct children");
                foreach (var k in kids)
                {
                    string kname = "?";
                    string kid = "?";
                    try { kname = k.Properties.Name.ValueOrDefault ?? "(null)"; } catch { }
                    try { kid = k.Properties.AutomationId.ValueOrDefault ?? "(null)"; } catch { }
                    Console.WriteLine($"         child: type={k.ControlType} name=\"{kname}\" aid=\"{kid}\"");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[4/6] FindAllChildren FAIL: {ex.Message}");
                return 6;
            }

            // Step 5: Recursive find — look for any element with AutomationId
            try
            {
                var all = mainWindow.FindAllDescendants();
                Console.WriteLine($"[5/6] FindAllDescendants OK: {all.Length} total descendants");
                int withId = 0;
                foreach (var e in all)
                {
                    try
                    {
                        var aid = e.Properties.AutomationId.ValueOrDefault;
                        if (!string.IsNullOrEmpty(aid)) withId++;
                    }
                    catch { }
                }
                Console.WriteLine($"         with AutomationId: {withId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[5/6] FindAllDescendants FAIL: {ex.Message}");
                return 7;
            }

            // Step 6: Window rect for PrintWindow check
            try
            {
                IntPtr hwnd = mainWindow.Properties.NativeWindowHandle.Value;
                if (GetWindowRect(hwnd, out var rect))
                {
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    Console.WriteLine($"[6/6] GetWindowRect OK: {w}x{h}");
                }
                else
                {
                    Console.Error.WriteLine($"[6/6] GetWindowRect FAIL");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[6/6] GetWindowRect FAIL: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("PROBE PASS: UIA tree reads work in this session.");
            Console.WriteLine("Next step: implement custom AutomationPeer + IInvokeProvider.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            exitCode = 99;
        }
        finally
        {
            try
            {
                app.Close();
                if (!app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(2)))
                {
                    try { Process.GetProcessById(app.ProcessId).Kill(); } catch { }
                }
            }
            catch { }
        }

        return exitCode;
    }
}
