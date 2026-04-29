using System.Runtime.InteropServices;

namespace GhostWin.Interop;

/// M-16-C Phase B1: scrollback geometry (matches GwScrollbackInfo in
/// ghostwin_engine.h). Layout sequential for direct interop.
[StructLayout(LayoutKind.Sequential)]
internal struct GwScrollbackInfo
{
    public uint TotalRows;
    public uint ViewportRows;
    public uint ScrollbackRows;
    public int  ViewportOffsetFromBottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GwCallbacks
{
    public nint Context;
    public nint OnCreated;
    public nint OnClosed;
    public nint OnActivated;
    public nint OnTitleChanged;
    public nint OnCwdChanged;
    public nint OnMouseShape;
    public nint OnChildExit;
    public nint OnRenderDone;
    public nint OnOscNotify;
}

internal static partial class NativeEngine
{
    private const string Dll = "ghostwin_engine";

    // Engine lifecycle
    [LibraryImport(Dll)]
    internal static partial nint gw_engine_create(in GwCallbacks callbacks);

    [LibraryImport(Dll)]
    internal static partial void gw_engine_destroy(nint engine);

    [LibraryImport(Dll)]
    internal static partial void gw_engine_detach_callbacks(nint engine);

    // Render
    [LibraryImport(Dll)]
    internal static partial int gw_render_init(nint engine, nint hwnd,
        uint widthPx, uint heightPx, float fontSizePt,
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamily,
        float dpiScale);

    [LibraryImport(Dll)]
    internal static partial int gw_render_resize(nint engine, uint widthPx, uint heightPx);

    [LibraryImport(Dll)]
    internal static partial int gw_update_cell_metrics(nint engine,
        float fontSizePt,
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamily,
        float dpiScale,
        float cellWidthScale,
        float cellHeightScale,
        float zoom);

    [LibraryImport(Dll)]
    internal static partial int gw_render_set_clear_color(nint engine, uint rgb);

    [LibraryImport(Dll)]
    internal static partial int gw_render_start(nint engine);

    [LibraryImport(Dll)]
    internal static partial void gw_render_stop(nint engine);

    // Session
    [LibraryImport(Dll)]
    internal static partial uint gw_session_create(nint engine,
        [MarshalAs(UnmanagedType.LPWStr)] string? shellPath,
        [MarshalAs(UnmanagedType.LPWStr)] string? initialDir,
        ushort cols, ushort rows);

    [LibraryImport(Dll)]
    internal static partial int gw_session_close(nint engine, uint id);

    [LibraryImport(Dll)]
    internal static partial void gw_session_activate(nint engine, uint id);

    [LibraryImport(Dll)]
    internal static partial int gw_session_write(nint engine, uint id,
        nint data, uint len);

    [LibraryImport(Dll)]
    internal static partial int gw_session_test_inject_vt(nint engine, uint id,
        nint data, uint len);

    [LibraryImport(Dll)]
    internal static partial int gw_session_write_mouse(nint engine, uint id,
        float xPx, float yPx, uint button, uint action, uint mods);

    [LibraryImport(Dll)]
    internal static partial int gw_session_resize(nint engine, uint id,
        ushort cols, ushort rows);

    [LibraryImport(Dll)]
    internal static partial int gw_scroll_viewport(nint engine, uint id, int deltaRows);

    [LibraryImport(Dll)]
    internal static partial int gw_session_get_scrollback_info(nint engine, uint id,
        out GwScrollbackInfo info);

    // TSF
    [LibraryImport(Dll)]
    internal static partial int gw_tsf_attach(nint engine, nint hiddenHwnd);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_focus(nint engine, uint sessionId);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_unfocus(nint engine);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_send_pending(nint engine);

    [LibraryImport(Dll)]
    internal static partial int gw_session_set_composition(nint engine, uint id,
        [MarshalAs(UnmanagedType.LPWStr)] string? text, uint len,
        uint caretOffset, int active);

    // Query
    [LibraryImport(Dll)]
    internal static partial uint gw_session_count(nint engine);

    [LibraryImport(Dll)]
    internal static partial uint gw_active_session_id(nint engine);

    [LibraryImport(Dll)]
    internal static partial void gw_poll_titles(nint engine);

    // Surface management (Phase 5-E pane split)
    [LibraryImport(Dll)]
    internal static partial uint gw_surface_create(nint engine, nint hwnd,
        uint sessionId, uint widthPx, uint heightPx);

    [LibraryImport(Dll)]
    internal static partial int gw_surface_destroy(nint engine, uint id);

    [LibraryImport(Dll)]
    internal static partial int gw_surface_resize(nint engine, uint id,
        uint widthPx, uint heightPx);

    [LibraryImport(Dll)]
    internal static partial int gw_surface_focus(nint engine, uint id);

    // Selection support (M-10c)
    [LibraryImport(Dll)]
    internal static partial int gw_session_set_selection(nint engine, uint id,
        int startRow, int startCol, int endRow, int endCol, int active);

    [LibraryImport(Dll)]
    internal static partial int gw_get_cell_size(nint engine,
        out uint cellWidth, out uint cellHeight);

    [LibraryImport(Dll)]
    internal static partial int gw_session_get_pixel_padding(nint engine, uint id,
        out uint padLeft, out uint padTop);

    [LibraryImport(Dll)]
    internal static partial int gw_session_get_cell_text(nint engine, uint id,
        int row, int col, nint buf, uint bufSize);

    [LibraryImport(Dll)]
    internal static partial int gw_session_get_selected_text(nint engine, uint id,
        int startRow, int startCol, int endRow, int endCol,
        nint buf, uint bufSize, out uint written);

    // Bracketed Paste mode query
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool gw_session_mode_get(nint engine, uint id, ushort modeValue);

    [LibraryImport(Dll)]
    internal static partial int gw_session_find_word_bounds(nint engine, uint id,
        int row, int col, out int outStart, out int outEnd);

    [LibraryImport(Dll)]
    internal static partial int gw_session_find_line_bounds(nint engine, uint id,
        int row, out int outStart, out int outEnd);
}
