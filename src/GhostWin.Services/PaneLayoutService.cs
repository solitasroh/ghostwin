using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class PaneLayoutService : IPaneLayoutService
{
    private readonly IEngineService _engine;
    private readonly ISessionManager _sessions;
    private readonly IMessenger _messenger;
    private readonly Dictionary<uint, PaneLeafState> _leaves = [];
    private PaneNode? _root;
    private uint _nextPaneId = 1;

    public const int MaxPanes = 8;

    public PaneLayoutService(
        IEngineService engine, ISessionManager sessions, IMessenger messenger)
    {
        _engine = engine;
        _sessions = sessions;
        _messenger = messenger;
    }

    public IReadOnlyPaneNode? Root => _root;
    public uint? FocusedPaneId { get; private set; }
    public int LeafCount => _leaves.Count;

    private uint AllocateId() => _nextPaneId++;

    public void Initialize(uint initialSessionId, uint initialSurfaceId)
    {
        var paneId = AllocateId();
        _root = PaneNode.CreateLeaf(paneId, initialSessionId);
        _leaves[paneId] = new PaneLeafState(paneId, initialSessionId, initialSurfaceId);
        FocusedPaneId = paneId;
    }

    public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction)
    {
        if (LeafCount >= MaxPanes) return null;

        var focused = FindLeaf(FocusedPaneId);
        if (focused == null) return null;

        var newSessionId = _sessions.CreateSession();
        var oldState = _leaves[focused.Id];
        var oldId = AllocateId();
        var newId = AllocateId();
        var (oldLeaf, newLeaf) = focused.Split(direction, newSessionId, oldId, newId);

        // Migrate existing leaf state to oldLeaf ID (host/surface preserved)
        _leaves.Remove(focused.Id);
        _leaves[oldLeaf.Id] = oldState with { PaneId = oldLeaf.Id };

        // Register newLeaf placeholder — SurfaceId=0 updated in OnHostReady
        _leaves[newLeaf.Id] = new PaneLeafState(newLeaf.Id, newSessionId, SurfaceId: 0);

        FocusedPaneId = newLeaf.Id;
        _messenger.Send(new PaneLayoutChangedMessage((IReadOnlyPaneNode)_root!));
        return (newSessionId, newLeaf.Id);
    }

    public void CloseFocused()
    {
        if (_root == null || FocusedPaneId == null) return;

        // Last pane: escalate to tab close
        if (_root.IsLeaf)
        {
            if (_leaves.TryGetValue(_root.Id, out var lastState))
            {
                if (lastState.SurfaceId != 0)
                    _engine.SurfaceDestroy(lastState.SurfaceId);
                _sessions.CloseSession(lastState.SessionId);
                _leaves.Remove(_root.Id);
            }
            return;
        }

        var focused = FindLeaf(FocusedPaneId);
        if (focused == null) return;

        if (!_leaves.TryGetValue(focused.Id, out var state)) return;

        // Find adjacent leaf for focus transfer
        var leaves = _root.GetLeaves().ToList();
        var idx = leaves.IndexOf(focused);
        var adjacentIdx = idx > 0 ? idx - 1 : Math.Min(idx + 1, leaves.Count - 1);
        var adjacentLeaf = leaves[adjacentIdx];

        // Destroy surface and close session
        if (state.SurfaceId != 0)
            _engine.SurfaceDestroy(state.SurfaceId);
        _sessions.CloseSession(state.SessionId);

        // Remove from tree
        _root.RemoveLeaf(focused);
        _leaves.Remove(focused.Id);

        // Transfer focus
        FocusedPaneId = adjacentLeaf.Id;
        if (_leaves.TryGetValue(adjacentLeaf.Id, out var adjacentState) && adjacentState.SurfaceId != 0)
            _engine.SurfaceFocus(adjacentState.SurfaceId);

        _messenger.Send(new PaneFocusChangedMessage(adjacentLeaf.Id, adjacentState?.SessionId ?? 0));
        _messenger.Send(new PaneLayoutChangedMessage((IReadOnlyPaneNode)_root));
    }

    public void MoveFocus(FocusDirection direction)
    {
        if (_root == null || FocusedPaneId == null) return;

        var leaves = _root.GetLeaves().ToList();
        var current = leaves.FirstOrDefault(l => l.Id == FocusedPaneId);
        if (current == null) return;

        var idx = leaves.IndexOf(current);
        int newIdx = direction switch
        {
            FocusDirection.Left or FocusDirection.Up => Math.Max(0, idx - 1),
            FocusDirection.Right or FocusDirection.Down => Math.Min(leaves.Count - 1, idx + 1),
            _ => idx,
        };

        if (newIdx == idx) return;

        var target = leaves[newIdx];
        FocusedPaneId = target.Id;

        if (_leaves.TryGetValue(target.Id, out var targetState) && targetState.SurfaceId != 0)
            _engine.SurfaceFocus(targetState.SurfaceId);

        _messenger.Send(new PaneFocusChangedMessage(
            target.Id, target.SessionId ?? 0));
    }

    public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
    {
        if (!_leaves.TryGetValue(paneId, out var state)) return;
        if (state.SurfaceId != 0) return; // Already created

        var leaf = FindLeaf(paneId);
        if (leaf?.SessionId == null) return;

        var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
        _leaves[paneId] = state with { SurfaceId = surfaceId };
    }

    public void OnPaneResized(uint paneId, uint widthPx, uint heightPx)
    {
        if (!_leaves.TryGetValue(paneId, out var state)) return;
        if (state.SurfaceId == 0) return;

        _engine.SurfaceResize(state.SurfaceId, widthPx, heightPx);
    }

    private PaneNode? FindLeaf(uint? paneId)
    {
        if (paneId == null || _root == null) return null;
        return _root.FindLeafById(paneId.Value);
    }
}
