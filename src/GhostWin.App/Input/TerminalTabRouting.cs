namespace GhostWin.App.Input;

public static class TerminalTabRouting
{
    public static bool ShouldLetWpfHandlePlainTab(
        bool hasFocusedTerminalHost,
        bool isPaneTreeFocused,
        bool isTerminalChildFocused,
        bool hasWpfChromeFocus)
    {
        if (isPaneTreeFocused || isTerminalChildFocused)
            return false;

        if (hasWpfChromeFocus)
            return true;

        return !hasFocusedTerminalHost;
    }
}
