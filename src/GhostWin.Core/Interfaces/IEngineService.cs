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
