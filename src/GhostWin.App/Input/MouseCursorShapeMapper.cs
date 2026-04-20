namespace GhostWin.App.Input;

public static class MouseCursorShapeMapper
{
    public static int MapToCursorId(int ghosttyShape) => ghosttyShape switch
    {
        0 => 32512, // DEFAULT -> IDC_ARROW
        1 => 32512, // CONTEXT_MENU -> fallback
        2 => 32651, // HELP -> IDC_HELP
        3 => 32649, // POINTER -> IDC_HAND
        4 => 32650, // PROGRESS -> IDC_APPSTARTING
        5 => 32514, // WAIT -> IDC_WAIT
        6 => 32512, // CELL -> Arrow
        7 => 32515, // CROSSHAIR -> IDC_CROSS
        8 => 32513, // TEXT -> IDC_IBEAM
        9 => 32513, // VERTICAL_TEXT -> IBeam fallback
        10 => 32649, // ALIAS -> Hand fallback
        11 => 32649, // COPY -> Hand fallback
        12 => 32649, // MOVE -> Hand fallback
        13 => 32648, // NO_DROP -> IDC_NO
        14 => 32648, // NOT_ALLOWED -> IDC_NO
        15 => 32649, // GRAB -> Hand fallback
        16 => 32649, // GRABBING -> Hand fallback
        17 => 32512, // ALL_SCROLL -> fallback
        18 => 32644, // COL_RESIZE -> IDC_SIZEWE
        19 => 32645, // ROW_RESIZE -> IDC_SIZENS
        20 => 32645, // N_RESIZE -> SIZENS fallback
        21 => 32644, // E_RESIZE -> SIZEWE fallback
        22 => 32645, // S_RESIZE -> SIZENS fallback
        23 => 32644, // W_RESIZE -> SIZEWE fallback
        24 => 32643, // NE_RESIZE -> IDC_SIZENESW
        25 => 32642, // NW_RESIZE -> IDC_SIZENWSE
        26 => 32642, // SE_RESIZE -> IDC_SIZENWSE
        27 => 32643, // SW_RESIZE -> IDC_SIZENESW
        28 => 32644, // EW_RESIZE -> IDC_SIZEWE
        29 => 32645, // NS_RESIZE -> IDC_SIZENS
        30 => 32643, // NESW_RESIZE -> IDC_SIZENESW
        31 => 32642, // NWSE_RESIZE -> IDC_SIZENWSE
        32 => 32512, // ZOOM_IN -> fallback
        33 => 32512, // ZOOM_OUT -> fallback
        _ => 32512,
    };
}
