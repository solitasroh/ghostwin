using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace GhostWin.Automation.Runner.Measurement.Infrastructure;

internal sealed class GhostWinController
{
    public nint MainWindowHandle { get; }

    public GhostWinController(nint mainWindowHandle)
    {
        MainWindowHandle = mainWindowHandle;
    }

    public void BringToForeground()
    {
        Win32.ShowWindow(MainWindowHandle, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(MainWindowHandle);
        // Give the focus change time to settle before the next UIA invoke.
        Thread.Sleep(250);
    }

    public void SplitVertical()
        => InvokeAutomationButton("E2E_SplitVertical");

    public void SplitHorizontal()
        => InvokeAutomationButton("E2E_SplitHorizontal");

    public void NewWorkspace()
        => InvokeAutomationButton("E2E_NewWorkspace");

    public void NextWorkspace()
        => InvokeAutomationButton("E2E_NextWorkspace");

    public static void Settle(int milliseconds = 300)
        => Thread.Sleep(milliseconds);

    private void InvokeAutomationButton(string automationId)
    {
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(MainWindowHandle);
        var button = WaitForAutomationButton(window, automationId);
        button.Patterns.Invoke.Pattern.Invoke();
    }

    private static AutomationElement WaitForAutomationButton(
        AutomationElement window,
        string automationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var element = window.FindFirstDescendant(
                cf => cf.ByAutomationId(automationId));
            if (element != null)
                return element;

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            $"Automation hook not found: {automationId}");
    }
}
