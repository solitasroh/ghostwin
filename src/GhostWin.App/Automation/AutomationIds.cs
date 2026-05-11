using System.Text;

namespace GhostWin.App.Automation;

public static class AutomationIds
{
    public const string LegacyTerminalHost = "E2E_TerminalHost";
    public const string SplitVertical = "E2E_SplitVertical";
    public const string SplitHorizontal = "E2E_SplitHorizontal";
    public const string ClosePane = "E2E_ClosePane";
    public const string NewWorkspace = "E2E_NewWorkspace";
    public const string NextWorkspace = "E2E_NextWorkspace";
    public const string OpenCommandPalette = "E2E_OpenCommandPalette";
    public const string SettingsPage = "E2E_SettingsPage";
    public const string CommandPalette = "E2E_CommandPalette";
    public const string CommandPaletteSearch = "E2E_PaletteSearch";
    public const string CommandPaletteResults = "E2E_CommandPaletteResults";
    public const string MouseCursorShape = "E2E_MouseCursorShape";
    public const string MouseCursorId = "E2E_MouseCursorId";
    public const string MouseCursorSession = "E2E_MouseCursorSession";
    public const string MouseCursorVersion = "E2E_MouseCursorVersion";
    public const string MouseCursorUpdatedAt = "E2E_MouseCursorUpdatedAt";
    public const string NotificationPanel = "E2E_NotificationPanel";
    public const string NotificationList = "E2E_NotificationList";
    public const string MarkAllRead = "E2E_MarkAllRead";
    public const string RenameTextBox = "E2E_RenameTextBox";
    public const string ThemeCombo = "E2E_ThemeCombo";
    public const string FontFamily = "E2E_FontFamily";
    public const string FontSize = "E2E_FontSize";
    public const string ScrollbarPolicy = "E2E_ScrollbarPolicy";
    public const string ForceContextMenu = "E2E_ForceContextMenu";
    public const string SettingsBack = "E2E_SettingsBack";
    public const string UseMica = "E2E_UseMica";
    public const string CellWidthScale = "E2E_CellWidthScale";
    public const string CellHeightScale = "E2E_CellHeightScale";
    public const string SidebarVisible = "E2E_SidebarVisible";
    public const string SidebarWidth = "E2E_SidebarWidth";
    public const string ShowCwd = "E2E_ShowCwd";
    public const string ShowGit = "E2E_ShowGit";
    public const string NotificationRingEnabled = "E2E_NotificationRingEnabled";
    public const string ToastEnabled = "E2E_ToastEnabled";
    public const string NotificationPanelEnabled = "E2E_NotificationPanelEnabled";
    public const string AgentBadgeEnabled = "E2E_AgentBadgeEnabled";
    public const string OpenSettingsJson = "E2E_OpenSettingsJson";
    public const string ContextWorkspaceRename = "E2E_Context_Workspace_Rename";
    public const string ContextWorkspaceEditDescription = "E2E_Context_Workspace_EditDescription";
    public const string ContextWorkspacePin = "E2E_Context_Workspace_Pin";
    public const string ContextWorkspaceMoveUp = "E2E_Context_Workspace_MoveUp";
    public const string ContextWorkspaceMoveDown = "E2E_Context_Workspace_MoveDown";
    public const string ContextWorkspaceMarkAllRead = "E2E_Context_Workspace_MarkAllRead";
    public const string ContextWorkspaceClose = "E2E_Context_Workspace_Close";
    public const string ContextPaneSplitVertical = "E2E_Context_Pane_SplitVertical";
    public const string ContextPaneSplitHorizontal = "E2E_Context_Pane_SplitHorizontal";
    public const string ContextPaneClose = "E2E_Context_Pane_Close";
    public const string ContextPaneZoom = "E2E_Context_Pane_Zoom";
    public const string ContextNotificationMarkRead = "E2E_Context_Notification_MarkRead";
    public const string ContextNotificationDismiss = "E2E_Context_Notification_Dismiss";
    public const string ContextNotificationJumpToSource = "E2E_Context_Notification_JumpToSource";
    public const string ContextTerminalCopy = "E2E_Context_Terminal_Copy";
    public const string ContextTerminalPaste = "E2E_Context_Terminal_Paste";
    public const string ContextTerminalSelectAll = "E2E_Context_Terminal_SelectAll";
    public const string ContextTerminalClearScrollback = "E2E_Context_Terminal_ClearScrollback";
    public const string ContextTerminalOpenVsCode = "E2E_Context_Terminal_OpenVsCode";
    public const string ContextTerminalOpenCursor = "E2E_Context_Terminal_OpenCursor";
    public const string ContextTerminalOpenExplorer = "E2E_Context_Terminal_OpenExplorer";

    public static IReadOnlyList<string> StaticIds { get; } =
    [
        LegacyTerminalHost,
        SplitVertical,
        SplitHorizontal,
        ClosePane,
        NewWorkspace,
        NextWorkspace,
        OpenCommandPalette,
        SettingsPage,
        CommandPalette,
        CommandPaletteSearch,
        CommandPaletteResults,
        MouseCursorShape,
        MouseCursorId,
        MouseCursorSession,
        MouseCursorVersion,
        MouseCursorUpdatedAt,
        NotificationPanel,
        NotificationList,
        MarkAllRead,
        RenameTextBox,
        ThemeCombo,
        FontFamily,
        FontSize,
        ScrollbarPolicy,
        ForceContextMenu,
        SettingsBack,
        UseMica,
        CellWidthScale,
        CellHeightScale,
        SidebarVisible,
        SidebarWidth,
        ShowCwd,
        ShowGit,
        NotificationRingEnabled,
        ToastEnabled,
        NotificationPanelEnabled,
        AgentBadgeEnabled,
        OpenSettingsJson,
        ContextWorkspaceRename,
        ContextWorkspaceEditDescription,
        ContextWorkspacePin,
        ContextWorkspaceMoveUp,
        ContextWorkspaceMoveDown,
        ContextWorkspaceMarkAllRead,
        ContextWorkspaceClose,
        ContextPaneSplitVertical,
        ContextPaneSplitHorizontal,
        ContextPaneClose,
        ContextPaneZoom,
        ContextNotificationMarkRead,
        ContextNotificationDismiss,
        ContextNotificationJumpToSource,
        ContextTerminalCopy,
        ContextTerminalPaste,
        ContextTerminalSelectAll,
        ContextTerminalClearScrollback,
        ContextTerminalOpenVsCode,
        ContextTerminalOpenCursor,
        ContextTerminalOpenExplorer
    ];

    public static string TerminalHost(uint paneId) => $"E2E_TerminalHost_{paneId}";

    public static string WorkspaceItem(uint workspaceId) => $"E2E_WorkspaceItem_{workspaceId}";

    public static string NotificationItem(uint notificationId) => $"E2E_NotificationItem_{notificationId}";

    public static string NotificationRing(uint workspaceId) => $"E2E_NotificationRing_{workspaceId}";

    public static string CommandPaletteItem(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return $"E2E_CommandPaletteItem_{ToToken(actionId)}";
    }

    private static string ToToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }
}
