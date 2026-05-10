using GhostWin.Core.Interfaces;

namespace GhostWin.App.Tests.Fakes;

internal class FakeEngineService : IEngineService
{
    private uint _nextSessionId;

    public bool IsInitialized { get; set; } = true;
    public int MouseEventResult { get; set; }
    public Dictionary<uint, ScrollbackInfo?> ScrollbackBySession { get; } = [];
    public List<(uint SessionId, int DeltaRows)> ScrollViewportCalls { get; } = [];
    public List<(uint SessionId, ReadOnlyMemory<byte> Payload)> WriteSessionCalls { get; } = [];
    public List<(uint SessionId, float XPx, float YPx, uint Button, uint Action, uint Mods)> MouseEventCalls { get; } = [];

    public void Initialize(GwCallbackContext callbackContext) { }
    public void DetachCallbacks() { }
    public void Shutdown() { }
    public void Dispose() { }
    public int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily, float dpiScale = 1.0f) => 0;
    public int RenderSetClearColor(uint rgb) => 0;
    public int SetTerminalColors(uint bgRgb, uint fgRgb, uint cursorRgb, uint[]? palette16) => 0;
    public void RenderStart() { }
    public void RenderStop() { }
    public uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows) => ++_nextSessionId;
    public int CloseSession(uint id) => 0;
    public void ActivateSession(uint id) { }

    public int WriteSession(uint id, ReadOnlySpan<byte> data)
    {
        WriteSessionCalls.Add((id, data.ToArray()));
        return 0;
    }

    public int TestOnlyInjectVt(uint id, ReadOnlySpan<byte> data) => 0;

    public int WriteMouseEvent(uint sessionId, float xPx, float yPx, uint button, uint action, uint mods)
    {
        MouseEventCalls.Add((sessionId, xPx, yPx, button, action, mods));
        return MouseEventResult;
    }

    public int ScrollViewport(uint sessionId, int deltaRows)
    {
        ScrollViewportCalls.Add((sessionId, deltaRows));
        return 0;
    }

    public virtual ScrollbackInfo? GetScrollbackInfo(uint sessionId) =>
        ScrollbackBySession.TryGetValue(sessionId, out var info) ? info : null;

    public int UpdateCellMetrics(float fontSizePt, string fontFamily, float dpiScale, float cellWidthScale, float cellHeightScale, float zoom) => 0;
    public int TsfAttach(nint hiddenHwnd) => 0;
    public int TsfFocus(uint sessionId) => 0;
    public int TsfUnfocus() => 0;
    public int TsfSendPending() => 0;
    public int SetComposition(uint sessionId, string? text, int caretOffset, bool active) => 0;
    public void PollTitles() { }
    public uint SurfaceCreate(nint hwnd, uint sessionId, uint widthPx, uint heightPx) => 1;
    public int SurfaceDestroy(uint id) => 0;
    public int SurfaceResize(uint id, uint widthPx, uint heightPx) => 0;
    public int SurfaceFocus(uint id) => 0;
    public void SetSelection(uint sessionId, int startRow, int startCol, int endRow, int endCol, bool active) { }

    public void GetCellSize(out uint cellWidth, out uint cellHeight)
    {
        cellWidth = 10;
        cellHeight = 20;
    }

    public void GetPixelPadding(uint sessionId, out uint padLeft, out uint padTop)
    {
        padLeft = 0;
        padTop = 0;
    }

    public string GetCellText(uint sessionId, int row, int col) => string.Empty;
    public string GetSelectedText(uint sessionId, int startRow, int startCol, int endRow, int endCol) => string.Empty;
    public bool GetMode(uint sessionId, ushort mode) => false;
    public (int startCol, int endCol) FindWordBounds(uint sessionId, int row, int col) => (0, 0);
    public (int startCol, int endCol) FindLineBounds(uint sessionId, int row) => (0, 0);
}
