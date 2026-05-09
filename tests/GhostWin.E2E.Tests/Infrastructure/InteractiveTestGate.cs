namespace GhostWin.E2E.Tests.Infrastructure;

internal static class InteractiveTestGate
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("GHOSTWIN_INTERACTIVE_AUTOMATION") == "1";
}
