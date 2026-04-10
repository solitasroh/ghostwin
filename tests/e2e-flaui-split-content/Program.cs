// split-content-loss-v2 smoke runner (2026-04-09).
//
// Strategy: instead of SendInput (which fails with ERROR_ACCESS_DENIED
// in Windows disconnected sessions where there is no active input
// desktop), we drive GhostWin.App through UI Automation (UIA). The WPF
// XAML exposes invisible 0x0 Buttons with AutomationId="E2E_*" that are
// bound to the same RelayCommands as the Alt+V/H KeyBindings, so the
// FlaUI UIA3 client can find them and call InvokePattern.Invoke() —
// this path is pure COM and does NOT need an input desktop.
//
// Steps:
//   1. Launch GhostWin.App as a child process.
//   2. Wait for the main window and PowerShell prompt to render.
//   3. PrintWindow-capture screenshot before split → before-split.png.
//   4. Find AutomationId "E2E_SplitVertical" via UIA and Invoke.
//   5. Wait for WPF Grid layout to settle.
//   6. PrintWindow-capture screenshot after split → after-split.png.
//   7. Close GhostWin.App cleanly.
//
// The caller (Claude Code) reads the two PNGs via the Read tool and
// visually confirms that the PowerShell prompt (bottom-line $ marker)
// is still visible in the LEFT pane after split. If the left pane is
// blank in after-split.png, the capacity-backed RenderFrame fix is
// incomplete.
//
// Note: we do NOT type marker text into PowerShell. Typing would
// require either (a) Keyboard.Type which also fails in disconnected
// session, or (b) writing to the ConPTY stdin pipe via an IPC hook
// that does not exist. The PowerShell prompt itself serves as the
// pre-existing content marker.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace GhostWin.FlaUi.SplitContentSmoke;

internal static class Program
{
    private const int ShellBootMs = 8000;
    private const int SplitSettleMs = 2000;
    private const string SplitVerticalAutomationId = "E2E_SplitVertical";

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private static int Main(string[] args)
    {
        Console.WriteLine("=== GhostWin split-content-loss-v2 smoke (FlaUI) ===");

        // Locate GhostWin.App.exe. Default path matches scripts/build_wpf.ps1 output.
        string exePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "GhostWin.App", "bin", "x64", "Release", "net10.0-windows",
            "GhostWin.App.exe"));

        // Subcommand router.
        if (args.Length > 0 && args[0] == "--uia-probe")
        {
            return UiaProbe.Run(exePath);
        }
        if (args.Length > 0 && args[0] != "--uia-probe" && File.Exists(args[0]))
        {
            exePath = args[0];
        }

        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"ERROR: GhostWin.App.exe not found at {exePath}");
            Console.Error.WriteLine("Build first: scripts\\build_wpf.ps1 -Config Release");
            return 2;
        }
        Console.WriteLine($"Using exe: {exePath}");

        // Artifact directory at repo root.
        string artifactDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "artifacts", "split-content-loss-v2"));
        Directory.CreateDirectory(artifactDir);
        Console.WriteLine($"Artifacts: {artifactDir}");

        // 1. Launch the app.
        Application app;
        try
        {
            app = Application.Launch(exePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: failed to launch GhostWin.App: {ex.Message}");
            return 3;
        }
        Console.WriteLine($"Launched pid={app.ProcessId}");

        int exitCode = 0;
        try
        {
            using var automation = new UIA3Automation();
            var mainWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(8));
            if (mainWindow is null)
            {
                Console.Error.WriteLine("ERROR: GhostWin main window not found.");
                return 4;
            }
            IntPtr hwnd = mainWindow.Properties.NativeWindowHandle.Value;
            Console.WriteLine($"Main window handle=0x{hwnd.ToInt64():X}");

            // 2. Give the shell (PowerShell) time to spawn and render its prompt.
            Console.WriteLine($"Waiting {ShellBootMs}ms for PowerShell to boot...");
            Thread.Sleep(ShellBootMs);

            // 3. Screenshot before split — captures the single initial pane
            //    with its PowerShell prompt. This is our baseline content.
            string beforePath = Path.Combine(artifactDir, "before-split.png");
            CaptureWindow(hwnd, beforePath);
            Console.WriteLine($"[pre-split] Saved: {beforePath}");

            // 4. Locate the invisible E2E_SplitVertical button via UIA and
            //    invoke it. This triggers the same SplitVerticalCommand that
            //    Alt+V binds to, without going anywhere near SendInput.
            var btn = mainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId(SplitVerticalAutomationId));
            if (btn is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: UIA element with AutomationId '{SplitVerticalAutomationId}' " +
                    "not found. Did the WPF XAML E2E hooks get stripped?");
                return 6;
            }
            Console.WriteLine($"[UIA] found {SplitVerticalAutomationId}: type={btn.ControlType}");

            var invoker = btn.Patterns.Invoke.PatternOrDefault;
            if (invoker is null)
            {
                Console.Error.WriteLine(
                    "ERROR: the E2E_SplitVertical element does not expose InvokePattern.");
                return 7;
            }

            Console.WriteLine("[UIA] Invoking SplitVerticalCommand via InvokePattern...");
            invoker.Invoke();

            // 5. Wait for WPF Grid layout to fire the shrink-then-grow chain
            //    that this cycle is fixing.
            Thread.Sleep(SplitSettleMs);

            // 6. Screenshot after split. For the fix to be verified, the
            //    LEFT half of this image must still show the PowerShell
            //    prompt. If the left half is blank, the capacity-backed
            //    RenderFrame fix is incomplete.
            string afterPath = Path.Combine(artifactDir, "after-split.png");
            CaptureWindow(hwnd, afterPath);
            Console.WriteLine($"[post-split] Saved: {afterPath}");

            Console.WriteLine();
            Console.WriteLine("SMOKE COMPLETE.");
            Console.WriteLine("  before: " + beforePath);
            Console.WriteLine("  after:  " + afterPath);
            Console.WriteLine("Verify visually: the left half of after-split.png");
            Console.WriteLine("must still contain the same PowerShell prompt as");
            Console.WriteLine("before-split.png. A blank left pane = fix incomplete.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            exitCode = 5;
        }
        finally
        {
            // 11. Close the app.
            try
            {
                app.Close();
                if (!app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(2)))
                {
                    // Close didn't finish cleanly — kill.
                    try { Process.GetProcessById(app.ProcessId).Kill(); } catch { }
                }
            }
            catch { }
        }

        return exitCode;
    }

    private static void CaptureWindow(IntPtr hwnd, string outPath)
    {
        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("GetWindowRect failed");
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Invalid window size {w}x{h}");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT ensures DirectComposition / HwndHost
                // children render into the DC. Without this flag the
                // terminal HwndHost region is blank.
                bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                if (!ok)
                    throw new InvalidOperationException("PrintWindow failed");
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        bmp.Save(outPath, ImageFormat.Png);
    }
}
