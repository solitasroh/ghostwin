namespace GhostWin.App.Input;

public static class MouseCursorOracleFormatter
{
    public static string FormatShape(int shape)
        => $"shape={shape} ({GetShapeName(shape)})";

    public static string FormatCursorId(int cursorId)
        => $"cursorId={cursorId} ({GetCursorName(cursorId)})";

    public static string FormatSessionId(uint sessionId)
        => $"sessionId={sessionId}";

    private static string GetShapeName(int shape) => shape switch
    {
        0 => "DEFAULT",
        1 => "CONTEXT_MENU",
        2 => "HELP",
        3 => "POINTER",
        4 => "PROGRESS",
        5 => "WAIT",
        6 => "CELL",
        7 => "CROSSHAIR",
        8 => "TEXT",
        9 => "VERTICAL_TEXT",
        10 => "ALIAS",
        11 => "COPY",
        12 => "MOVE",
        13 => "NO_DROP",
        14 => "NOT_ALLOWED",
        15 => "GRAB",
        16 => "GRABBING",
        17 => "ALL_SCROLL",
        18 => "COL_RESIZE",
        19 => "ROW_RESIZE",
        20 => "N_RESIZE",
        21 => "E_RESIZE",
        22 => "S_RESIZE",
        23 => "W_RESIZE",
        24 => "NE_RESIZE",
        25 => "NW_RESIZE",
        26 => "SE_RESIZE",
        27 => "SW_RESIZE",
        28 => "EW_RESIZE",
        29 => "NS_RESIZE",
        30 => "NESW_RESIZE",
        31 => "NWSE_RESIZE",
        32 => "ZOOM_IN",
        33 => "ZOOM_OUT",
        _ => "UNKNOWN",
    };

    private static string GetCursorName(int cursorId) => cursorId switch
    {
        32512 => "IDC_ARROW",
        32513 => "IDC_IBEAM",
        32514 => "IDC_WAIT",
        32515 => "IDC_CROSS",
        32642 => "IDC_SIZENWSE",
        32643 => "IDC_SIZENESW",
        32644 => "IDC_SIZEWE",
        32645 => "IDC_SIZENS",
        32648 => "IDC_NO",
        32649 => "IDC_HAND",
        32650 => "IDC_APPSTARTING",
        32651 => "IDC_HELP",
        _ => "UNKNOWN",
    };
}
