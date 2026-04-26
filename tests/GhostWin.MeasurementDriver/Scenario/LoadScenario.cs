using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;

namespace GhostWin.MeasurementDriver.Scenario;

internal static class LoadScenario
{
    // The PowerShell launcher captures render-perf.csv + cpu.csv during the
    // configured DurationSec window; this scenario simply foregrounds the
    // window and types a fixed workload so output keeps streaming.
    public static DriverResult Execute(GhostWinController controller, string workload)
    {
        controller.BringToForeground();
        Keyboard.Type(workload);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Keyboard.Release(VirtualKeyShort.RETURN);
        // Brief settle so the shell starts producing output before the
        // capture window samples its first render-perf line.
        Thread.Sleep(500);
        return DriverResult.Success("load", "1pane");
    }
}
