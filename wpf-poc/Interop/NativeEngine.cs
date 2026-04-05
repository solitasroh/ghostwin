using System.Runtime.InteropServices;

namespace GhostWinPoC.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct GwCallbacks
{
    public nint Context;
    public nint OnCreated;
    public nint OnClosed;
    public nint OnActivated;
    public nint OnTitleChanged;
    public nint OnCwdChanged;
    public nint OnChildExit;
    public nint OnRenderDone;
}

internal static partial class NativeEngine
{
    private const string Dll = "ghostwin_engine";

    // Engine lifecycle
    [LibraryImport(Dll)]
    internal static partial nint gw_engine_create(in GwCallbacks callbacks);

    [LibraryImport(Dll)]
    internal static partial void gw_engine_destroy(nint engine);

    // Render
    [LibraryImport(Dll)]
    internal static partial int gw_render_init(nint engine, nint hwnd,
        uint widthPx, uint heightPx, float fontSizePt,
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamily);

    [LibraryImport(Dll)]
    internal static partial int gw_render_resize(nint engine, uint widthPx, uint heightPx);

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
    internal static partial int gw_session_resize(nint engine, uint id,
        ushort cols, ushort rows);

    // TSF
    [LibraryImport(Dll)]
    internal static partial int gw_tsf_attach(nint engine, nint hiddenHwnd);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_focus(nint engine, uint sessionId);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_unfocus(nint engine);

    // Query
    [LibraryImport(Dll)]
    internal static partial uint gw_session_count(nint engine);

    [LibraryImport(Dll)]
    internal static partial uint gw_active_session_id(nint engine);

    [LibraryImport(Dll)]
    internal static partial void gw_poll_titles(nint engine);
}
