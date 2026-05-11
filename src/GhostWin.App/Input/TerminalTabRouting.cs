namespace GhostWin.App.Input;

public static class TerminalTabRouting
{
    public static bool ShouldLetWpfHandlePlainTab(
        bool hasFocusedTerminalHost,
        bool isPaneTreeFocused,
        bool isTerminalChildFocused,
        bool isTerminalInputActive,
        bool hasWpfChromeFocus)
    {
        if (isPaneTreeFocused || isTerminalChildFocused || isTerminalInputActive)
            return false;

        if (hasWpfChromeFocus)
            return true;

        return !hasFocusedTerminalHost;
    }
}
