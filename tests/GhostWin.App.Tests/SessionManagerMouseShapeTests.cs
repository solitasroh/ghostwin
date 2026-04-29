using FluentAssertions;
using GhostWin.Core.Interfaces;
using GhostWin.Services;
using Xunit;

namespace GhostWin.App.Tests;

public class SessionManagerMouseShapeTests
{
    [Fact]
    public void UpdateMouseCursorShape_UpdatesOnlyTargetSession()
    {
        var manager = new SessionManager(new FakeEngineService());
        var first = manager.CreateSession();
        var second = manager.CreateSession();

        manager.UpdateMouseCursorShape(first, 8);

        manager.Sessions.Should().ContainSingle(s => s.Id == first && s.MouseCursorShape == 8);
        manager.Sessions.Should().ContainSingle(s => s.Id == second && s.MouseCursorShape == 0);
    }

    [Fact]
    public void TestOnlyInjectBytes_UsesDirectVtInjectionPath()
    {
        var engine = new FakeEngineService();
        var manager = new SessionManager(engine);
        var sessionId = manager.CreateSession();

#pragma warning disable CS0618
        manager.TestOnlyInjectBytes(sessionId, [0x1b, (byte)']', (byte)'2', (byte)'2']);
#pragma warning restore CS0618

        engine.LastDirectVtInjectSessionId.Should().Be(sessionId);
        engine.LastDirectVtInjectPayload.Should().Equal([0x1b, (byte)']', (byte)'2', (byte)'2']);
    }

    private sealed class FakeEngineService : IEngineService
    {
        public bool IsInitialized => false;
        public uint? LastDirectVtInjectSessionId { get; private set; }
        public byte[]? LastDirectVtInjectPayload { get; private set; }
        public void Initialize(GwCallbackContext callbackContext) { }
        public void DetachCallbacks() { }
        public void Shutdown() { }
        public void Dispose() { }
        public int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily, float dpiScale = 1.0f) => 0;
        public int RenderSetClearColor(uint rgb) => 0;
        public void RenderStart() { }
        public void RenderStop() { }
        public uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows) => ++_nextId;
        public int CloseSession(uint id) => 0;
        public void ActivateSession(uint id) { }
        public int WriteSession(uint id, ReadOnlySpan<byte> data) => 0;
        public int TestOnlyInjectVt(uint id, ReadOnlySpan<byte> data)
        {
            LastDirectVtInjectSessionId = id;
            LastDirectVtInjectPayload = data.ToArray();
            return 0;
        }
        public int WriteMouseEvent(uint sessionId, float xPx, float yPx, uint button, uint action, uint mods) => 0;
        public int ScrollViewport(uint sessionId, int deltaRows) => 0;
        public ScrollbackInfo? GetScrollbackInfo(uint sessionId) => null;
        public int UpdateCellMetrics(float fontSizePt, string fontFamily, float dpiScale, float cellWidthScale, float cellHeightScale, float zoom) => 0;
        public int TsfAttach(nint hiddenHwnd) => 0;
        public int TsfFocus(uint sessionId) => 0;
        public int TsfUnfocus() => 0;
        public int TsfSendPending() => 0;
        public int SetComposition(uint sessionId, string? text, int caretOffset, bool active) => 0;
        public void PollTitles() { }
        public uint SurfaceCreate(nint hwnd, uint sessionId, uint widthPx, uint heightPx) => 0;
        public int SurfaceDestroy(uint id) => 0;
        public int SurfaceResize(uint id, uint widthPx, uint heightPx) => 0;
        public int SurfaceFocus(uint id) => 0;
        public void SetSelection(uint sessionId, int startRow, int startCol, int endRow, int endCol, bool active) { }
        public void GetCellSize(out uint cellWidth, out uint cellHeight)
        {
            cellWidth = 0;
            cellHeight = 0;
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

        private uint _nextId;
    }
}
