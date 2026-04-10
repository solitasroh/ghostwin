namespace GhostWin.Core.Interfaces;

public interface IEngineService : IDisposable
{
    bool IsInitialized { get; }
    uint SessionCount { get; }
    uint ActiveSessionId { get; }

    void Initialize(GwCallbackContext callbackContext);
    void Shutdown();

    int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily);

    /// <remarks>
    /// Deprecated in Phase 5-E.5 (2026-04-07, feature: bisect-mode-termination).
    /// The engine no longer uses a window-level swapchain — per-pane resizes go
    /// through <see cref="SurfaceResize"/>. This method remains as an ABI-
    /// compatible no-op for external callers.
    /// </remarks>
    int RenderResize(uint widthPx, uint heightPx);
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

    int ResizeSession(uint id, ushort cols, ushort rows);

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
}
