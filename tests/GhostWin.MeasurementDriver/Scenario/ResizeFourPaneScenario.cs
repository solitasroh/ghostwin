using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;
using GhostWin.MeasurementDriver.Verification;

namespace GhostWin.MeasurementDriver.Scenario;

internal static class ResizeFourPaneScenario
{
    // Three split keystrokes produce a 4-pane layout:
    //   1 -> 2 (Alt+V)  ▸  2 -> 3 (Alt+H)  ▸  3 -> 4 (Alt+H on the second column)
    private const int ExpectedPaneCount = 4;

    public static DriverResult Execute(GhostWinController controller)
    {
        controller.BringToForeground();
        controller.SplitVertical();
        Thread.Sleep(300);
        controller.SplitHorizontal();
        Thread.Sleep(300);
        controller.SplitHorizontal();
        Thread.Sleep(500);

        var observed = CountTerminalHosts(controller.MainWindowHandle);
        return PaneCountVerifier.Evaluate(ExpectedPaneCount, observed);
    }

    // GhostWin attaches AutomationProperties.AutomationId="E2E_TerminalHost" to
    // every TerminalHostControl when PaneContainerControl creates it (see
    // src/GhostWin.App/Controls/PaneContainerControl.cs). Counting matches
    // gives the pane count without inspecting the WPF visual tree directly.
    private static int CountTerminalHosts(nint hwnd)
    {
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(hwnd).AsWindow();
        var hosts = window.FindAllDescendants(cf => cf.ByAutomationId("E2E_TerminalHost"));
        return hosts.Length;
    }
}
