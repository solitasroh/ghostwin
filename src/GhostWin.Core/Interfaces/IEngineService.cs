namespace GhostWin.Core.Interfaces;

public interface IEngineService : IDisposable
{
    bool IsInitialized { get; }

    void Initialize();
    int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily);
    int RenderResize(uint widthPx, uint heightPx);
    void RenderStart();
    void RenderStop();

    uint SessionCreate(string? shellPath, string? initialDir, ushort cols, ushort rows);
    int SessionClose(uint id);
    void SessionActivate(uint id);
    int SessionWrite(uint id, ReadOnlySpan<byte> data);
    int SessionResize(uint id, ushort cols, ushort rows);

    int TsfAttach(nint hiddenHwnd);
    int TsfFocus(uint sessionId);
    int TsfUnfocus();
    int TsfSendPending();

    uint SessionCount();
    uint ActiveSessionId();
}
