namespace GhostWin.Core.Interfaces;

public interface IEngineService : IDisposable
{
    bool IsInitialized { get; }
    uint SessionCount { get; }
    uint ActiveSessionId { get; }

    void Initialize(GwCallbackContext callbackContext);
    void Shutdown();

    int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily);
    int RenderResize(uint widthPx, uint heightPx);
    int RenderSetClearColor(uint rgb);
    void RenderStart();
    void RenderStop();

    uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows);
    int CloseSession(uint id);
    void ActivateSession(uint id);
    int WriteSession(uint id, ReadOnlySpan<byte> data);
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
