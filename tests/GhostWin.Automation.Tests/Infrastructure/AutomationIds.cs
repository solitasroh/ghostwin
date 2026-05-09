namespace GhostWin.Automation.Tests.Infrastructure;

internal static class AutomationIds
{
    public const string SplitVertical = "E2E_SplitVertical";
    public const string SplitHorizontal = "E2E_SplitHorizontal";
    public const string ClosePane = "E2E_ClosePane";
    public const string NewWorkspace = "E2E_NewWorkspace";
    public const string OpenCommandPalette = "E2E_OpenCommandPalette";
    public const string SettingsPage = "E2E_SettingsPage";
    public const string CommandPalette = "E2E_CommandPalette";
    public const string CommandPaletteResults = "E2E_CommandPaletteResults";
    public const string MouseCursorShape = "E2E_MouseCursorShape";
    public const string MouseCursorId = "E2E_MouseCursorId";
    public const string MouseCursorSession = "E2E_MouseCursorSession";
    public const string MouseCursorVersion = "E2E_MouseCursorVersion";
    public const string MouseCursorUpdatedAt = "E2E_MouseCursorUpdatedAt";
    public const string NotificationPanel = "E2E_NotificationPanel";
    public const string NotificationList = "E2E_NotificationList";
    public const string MarkAllRead = "E2E_MarkAllRead";
    public const string ThemeCombo = "E2E_ThemeCombo";
    public const string FontFamily = "E2E_FontFamily";
    public const string FontSize = "E2E_FontSize";
    public const string ForceContextMenu = "E2E_ForceContextMenu";
    public const string SidebarVisible = "E2E_SidebarVisible";
    public const string SidebarWidth = "E2E_SidebarWidth";
    public const string ShowCwd = "E2E_ShowCwd";

    public static string TerminalHost(uint paneId) => $"E2E_TerminalHost_{paneId}";

    public static string WorkspaceItem(uint workspaceId) => $"E2E_WorkspaceItem_{workspaceId}";

    public static string NotificationItem(uint notificationId) => $"E2E_NotificationItem_{notificationId}";

    public static string CommandPaletteItem(string actionId)
        => $"E2E_CommandPaletteItem_{ToToken(actionId)}";

    private static string ToToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    }
}
