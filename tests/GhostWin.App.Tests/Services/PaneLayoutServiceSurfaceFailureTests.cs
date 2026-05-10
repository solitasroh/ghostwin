using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Services;
using Xunit;

namespace GhostWin.App.Tests.Services;

public class PaneLayoutServiceSurfaceFailureTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void AttachHostSurface_WhenSurfaceCreateReturnsZero_RecordsFailureStateAndPublishesLayout()
    {
        var engine = new RecordingEngineService();
        engine.SurfaceCreateResults.Enqueue(0);
        var messenger = new WeakReferenceMessenger();
        var recipient = new LayoutChangedRecipient();
        messenger.Register<PaneLayoutChangedMessage>(recipient, (_, _) => recipient.Count++);
        var service = new PaneLayoutService(engine, new FakeSessionManager(), messenger);
        service.Initialize(initialSessionId: 10);

        service.AttachHostSurface(paneId: 1, hwnd: 123, widthPx: 640, heightPx: 480);

        var provider = service as IPaneSurfaceStateProvider;
        provider.Should().NotBeNull();
        var state = provider!.GetPaneSurfaceState(1);
        state.Status.Should().Be(TerminalPaneSurfaceStatus.Failed);
        state.SurfaceId.Should().Be(0);
        state.LastHwnd.Should().Be((nint)123);
        state.LastWidthPx.Should().Be(640);
        state.LastHeightPx.Should().Be(480);
        state.Failure.Should().NotBeNull();
        state.Failure!.Attempt.Should().Be(1);
        state.Failure.Reason.Should().Be("SurfaceCreate returned 0");
        recipient.Count.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResizeHostSurface_WhenSurfaceCreateFailed_CachesLatestRetrySizeWithoutCallingEngineResize()
    {
        var engine = new RecordingEngineService();
        engine.SurfaceCreateResults.Enqueue(0);
        var service = new PaneLayoutService(engine, new FakeSessionManager(), new WeakReferenceMessenger());
        service.Initialize(initialSessionId: 10);
        service.AttachHostSurface(paneId: 1, hwnd: 123, widthPx: 640, heightPx: 480);

        service.ResizeHostSurface(paneId: 1, widthPx: 800, heightPx: 600);

        var state = ((IPaneSurfaceStateProvider)service).GetPaneSurfaceState(1);
        state.Status.Should().Be(TerminalPaneSurfaceStatus.Failed);
        state.LastWidthPx.Should().Be(800);
        state.LastHeightPx.Should().Be(600);
        engine.SurfaceResizeCalls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RetryHostSurface_WhenFocusedPaneFailed_UsesCachedHostFactsAndRefocusesNewSurface()
    {
        var engine = new RecordingEngineService();
        engine.SurfaceCreateResults.Enqueue(0);
        engine.SurfaceCreateResults.Enqueue(77);
        var messenger = new WeakReferenceMessenger();
        var recipient = new LayoutChangedRecipient();
        messenger.Register<PaneLayoutChangedMessage>(recipient, (_, _) => recipient.Count++);
        var service = new PaneLayoutService(engine, new FakeSessionManager(), messenger);
        service.Initialize(initialSessionId: 10);
        service.AttachHostSurface(paneId: 1, hwnd: 123, widthPx: 640, heightPx: 480);
        service.ResizeHostSurface(paneId: 1, widthPx: 800, heightPx: 600);

        var retried = service.RetryHostSurface(paneId: 1);

        retried.Should().BeTrue();
        engine.SurfaceCreateCalls.Should().Equal(
            ((nint)123, 10u, 640u, 480u),
            ((nint)123, 10u, 800u, 600u));
        engine.SurfaceFocusCalls.Should().ContainSingle().Which.Should().Be(77u);
        var state = ((IPaneSurfaceStateProvider)service).GetPaneSurfaceState(1);
        state.Status.Should().Be(TerminalPaneSurfaceStatus.Attached);
        state.SurfaceId.Should().Be(77u);
        state.Failure.Should().BeNull();
        recipient.Count.Should().Be(2);
    }

    private sealed class LayoutChangedRecipient
    {
        public int Count { get; set; }
    }

    private sealed class FakeSessionManager : ISessionManager
    {
        public IReadOnlyList<SessionInfo> Sessions => [];
        public uint? ActiveSessionId { get; private set; }
        public uint CreateSession(ushort cols = 80, ushort rows = 24) => 11;
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => 11;
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) => ActiveSessionId = id;
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }
        public void TestOnlyInjectBytes(uint sessionId, byte[] data) { }
    }

    private sealed class RecordingEngineService : IEngineService
    {
        public Queue<uint> SurfaceCreateResults { get; } = new();
        public List<(nint Hwnd, uint SessionId, uint WidthPx, uint HeightPx)> SurfaceCreateCalls { get; } = [];
        public List<(uint SurfaceId, uint WidthPx, uint HeightPx)> SurfaceResizeCalls { get; } = [];
        public List<uint> SurfaceFocusCalls { get; } = [];
        public bool IsInitialized => true;
        public void Initialize(GwCallbackContext callbackContext) { }
        public void DetachCallbacks() { }
        public void Shutdown() { }
        public void Dispose() { }
        public int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily, float dpiScale = 1) => 0;
        public int RenderSetClearColor(uint rgb) => 0;
        public int SetTerminalColors(uint bgRgb, uint fgRgb, uint cursorRgb, uint[]? palette16) => 0;
        public void RenderStart() { }
        public void RenderStop() { }
        public uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows) => 0;
        public int CloseSession(uint id) => 0;
        public void ActivateSession(uint id) { }
        public int WriteSession(uint id, ReadOnlySpan<byte> data) => 0;
        public int TestOnlyInjectVt(uint id, ReadOnlySpan<byte> data) => 0;
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

        public uint SurfaceCreate(nint hwnd, uint sessionId, uint widthPx, uint heightPx)
        {
            SurfaceCreateCalls.Add((hwnd, sessionId, widthPx, heightPx));
            return SurfaceCreateResults.Count == 0 ? 1 : SurfaceCreateResults.Dequeue();
        }

        public int SurfaceDestroy(uint id) => 0;

        public int SurfaceResize(uint id, uint widthPx, uint heightPx)
        {
            SurfaceResizeCalls.Add((id, widthPx, heightPx));
            return 0;
        }

        public int SurfaceFocus(uint id)
        {
            SurfaceFocusCalls.Add(id);
            return 0;
        }

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
}
