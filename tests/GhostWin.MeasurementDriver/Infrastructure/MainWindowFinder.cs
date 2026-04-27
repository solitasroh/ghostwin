using System.Diagnostics;

namespace GhostWin.MeasurementDriver.Infrastructure;

internal static class MainWindowFinder
{
    public static nint WaitForMainWindow(int pid, TimeSpan timeout)
    {
        var start = Stopwatch.StartNew();
        while (start.Elapsed < timeout)
        {
            var process = Process.GetProcessById(pid);
            if (process.MainWindowHandle != nint.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(100);
        }

        return nint.Zero;
    }
}
