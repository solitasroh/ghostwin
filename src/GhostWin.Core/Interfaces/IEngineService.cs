namespace GhostWin.Core.Interfaces;

public interface IEngineService : IDisposable
{
    bool IsInitialized { get; }

    void Initialize(GwCallbackContext callbackContext);

    /// <summary>
    /// Detach all native callbacks (set to NULL) to prevent re-entrant crashes
    /// during shutdown. Must be called before Shutdown/Dispose.
    /// After this call, on_child_exit and other callbacks become no-ops in C++.
    /// </summary>
    void DetachCallbacks();

    void Shutdown();

    int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily, float dpiScale = 1.0f);

    int RenderSetClearColor(uint rgb);
    void RenderStart();
    void RenderStop();

    uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows);
    int CloseSession(uint id);
    void ActivateSession(uint id);
    int WriteSession(uint id, ReadOnlySpan<byte> data);

    /// <summary>Forward mouse event to ConPTY via ghostty VT encoding.</summary>
    /// <param name="sessionId">Target session</param>
    /// <param name="xPx">Surface-space pixel X (child HWND lParam)</param>
    /// <param name="yPx">Surface-space pixel Y</param>
    /// <param name="button">0=none, 1=LEFT, 2=RIGHT, 3=MIDDLE, 4=WHEEL_UP, 5=WHEEL_DOWN</param>
    /// <param name="action">0=PRESS, 1=RELEASE, 2=MOTION</param>
    /// <param name="mods">Bitfield: 1=SHIFT, 2=CTRL, 4=ALT</param>
    int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                        uint button, uint action, uint mods);

    /// <summary>Scroll viewport (scrollback) by delta rows. Negative=up, positive=down.</summary>
    int ScrollViewport(uint sessionId, int deltaRows);

    /// <summary>
    /// Runtime cell metrics update — single entry point for DPI change
    /// (WM_DPICHANGED), font setting change, and zoom. Rebuilds the GlyphAtlas
    /// with the new metrics and broadcasts new cols/rows to every active
    /// surface+session. Must be called from the UI/cleanup thread.
    /// </summary>
    int UpdateCellMetrics(float fontSizePt, string fontFamily, float dpiScale,
                           float cellWidthScale, float cellHeightScale, float zoom);

    int TsfAttach(nint hiddenHwnd);
    int TsfFocus(uint sessionId);
    int TsfUnfocus();
    int TsfSendPending();

    void PollTitles();

    // Surface management (Phase 5-E pane split)
    uint SurfaceCreate(nint hwnd, uint sessionId, uint widthPx, uint heightPx);
    int SurfaceDestroy(uint id);
    int SurfaceResize(uint id, uint widthPx, uint heightPx);
    int SurfaceFocus(uint id);

    // ── Selection support (M-10c) ──

    /// <summary>
    /// Set selection range for DX11 render-time highlight overlay.
    /// The engine draws semi-transparent quads over selected cells each frame.
    /// </summary>
    void SetSelection(uint sessionId, int startRow, int startCol,
                      int endRow, int endCol, bool active);

    /// <summary>Get cell dimensions in pixels (for pixel-to-cell coordinate conversion).</summary>
    /// <param name="cellWidth">Output: cell width in pixels</param>
    /// <param name="cellHeight">Output: cell height in pixels</param>
    void GetCellSize(out uint cellWidth, out uint cellHeight);

    /// <summary>
    /// Read a single cell's text content at (row, col) for the given session.
    /// Returns the UTF-8 codepoint(s) as a string, or empty if blank/invalid.
    /// </summary>
    string GetCellText(uint sessionId, int row, int col);

    /// <summary>
    /// Read text from a rectangular selection range for the given session.
    /// Returns the selected text (newlines between rows).
    /// </summary>
    string GetSelectedText(uint sessionId, int startRow, int startCol,
                           int endRow, int endCol);

    /// <summary>Query DEC Private Mode state (e.g. 2004 for Bracketed Paste).</summary>
    bool GetMode(uint sessionId, ushort mode);

    /// <summary>Grid-native word boundary detection (handles CJK wide chars).</summary>
    (int startCol, int endCol) FindWordBounds(uint sessionId, int row, int col);

    /// <summary>Grid-native line boundary (full row).</summary>
    (int startCol, int endCol) FindLineBounds(uint sessionId, int row);
}

public class GwCallbackContext
{
    public Action<uint>? OnSessionCreated { get; set; }
    public Action<uint>? OnSessionClosed { get; set; }
    public Action<uint>? OnSessionActivated { get; set; }
    public Action<uint, string>? OnTitleChanged { get; set; }
    public Action<uint, string>? OnCwdChanged { get; set; }
    public Action<uint, uint>? OnChildExit { get; set; }
    public Action? OnRenderDone { get; set; }
    public Action<uint, string, string>? OnOscNotify { get; set; }
}
