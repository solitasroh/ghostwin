using System.Diagnostics;
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
    public uint? FocusedSessionId =>
        FocusedPaneId is { } id && _root?.FindLeafById(id) is { SessionId: var sid }
            ? sid
            : null;
    public int LeafCount => _leaves.Count;

    private uint AllocateId() => _nextPaneId++;

    public void Initialize(uint initialSessionId)
    {
        var paneId = AllocateId();
        _root = PaneNode.CreateLeaf(paneId, initialSessionId);
        // SurfaceId=0 is the placeholder sentinel. OnHostReady assigns the real
        // surface once the TerminalHostControl's child HWND becomes available.
        _leaves[paneId] = new PaneLeafState(paneId, initialSessionId, SurfaceId: 0);
        FocusedPaneId = paneId;
    }

    /// <summary>
    /// M-11 Session Restore — 스냅샷 PaneSnapshot 트리로부터 PaneNode 트리 재구성.
    /// 각 leaf 는 ResolveCwd 로 정규화된 CWD 로 새 세션을 발급받고 _leaves 상태를 채운다.
    /// Split 노드의 Ratio 는 [0.05, 0.95] 로 clamp (한쪽 pane 이 0 크기로 무너지지 않도록).
    /// </summary>
    public void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(rootSnap);
        ArgumentNullException.ThrowIfNull(sessions);
        if (_root != null)
            throw new InvalidOperationException(
                "InitializeFromTree must be called on a fresh PaneLayoutService (empty state)");

        _root = BuildNode(rootSnap, sessions);

        // 첫 번째 leaf 에 초기 포커스 (CreateWorkspace 동작과 일치 — 첫 세션이 active).
        var firstLeaf = _root.GetLeaves().FirstOrDefault();
        FocusedPaneId = firstLeaf?.Id;
    }

    /// <summary>
    /// PaneSnapshot → PaneNode 재귀 변환.
    /// - Leaf: CreateSession(cwd) 로 세션 발급 → PaneNode.CreateLeaf + _leaves 등록
    /// - Split: 좌/우 재귀 후 branch PaneNode 조립 (SessionId=null, SplitDirection 설정)
    /// </summary>
    private PaneNode BuildNode(PaneSnapshot snap, ISessionManager sessions)
    {
        switch (snap)
        {
            case PaneLeafSnapshot leafSnap:
            {
                var paneId = AllocateId();
                var cwd = SessionSnapshotMapper.ResolveCwd(leafSnap.Cwd);
                var sessionId = sessions.CreateSession(cwd);
                _leaves[paneId] = new PaneLeafState(paneId, sessionId, SurfaceId: 0);
                return PaneNode.CreateLeaf(paneId, sessionId);
            }
            case PaneSplitSnapshot splitSnap:
            {
                // 재귀 (깊이 우선 — 좌측 → 우측). 트리 크기는 MaxPanes(=8)로 상한.
                var left = BuildNode(splitSnap.Left, sessions);
                var right = BuildNode(splitSnap.Right, sessions);
                return new PaneNode
                {
                    Id = AllocateId(),
                    SessionId = null,                               // branch = null (PaneNode.cs:33 과 일치)
                    SplitDirection = splitSnap.Orientation,
                    Left = left,
                    Right = right,
                    Ratio = Math.Clamp(splitSnap.Ratio, 0.05, 0.95),
                };
            }
            default:
                throw new InvalidOperationException(
                    $"Unknown PaneSnapshot type: {snap.GetType().FullName}");
        }
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

        // Last pane: escalate to tab close. Clear all layout state so any
        // subsequent SplitFocused / FindLeaf cannot dereference a stale tree.
        // (No PaneLayoutChangedMessage emitted here — closing the last session
        // triggers Application.Shutdown via MainWindowViewModel.Receive.)
        if (_root.IsLeaf)
        {
            if (_leaves.TryGetValue(_root.Id, out var lastState))
            {
                if (lastState.SurfaceId != 0)
                    _engine.SurfaceDestroy(lastState.SurfaceId);
                _sessions.CloseSession(lastState.SessionId);
                _leaves.Remove(_root.Id);
            }
            _root = null;
            FocusedPaneId = null;
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

        // Remove from tree (sibling subtree is reparented intact — paneIds preserved)
        _root = _root.RemoveLeaf(focused);
        _leaves.Remove(focused.Id);
        if (_root == null) return;

        // Transfer focus
        FocusedPaneId = adjacentLeaf.Id;
        if (_leaves.TryGetValue(adjacentLeaf.Id, out var adjacentState) && adjacentState.SurfaceId != 0)
            _engine.SurfaceFocus(adjacentState.SurfaceId);

        if (adjacentLeaf.SessionId is { } adjacentSessionId)
            _sessions.ActivateSession(adjacentSessionId);

        _messenger.Send(new PaneFocusChangedMessage(adjacentLeaf.Id, adjacentState?.SessionId ?? 0));
        _messenger.Send(new PaneLayoutChangedMessage((IReadOnlyPaneNode)_root));
    }

    public void SetFocused(uint paneId)
    {
        if (_root == null) return;
        if (FocusedPaneId == paneId) return;

        var target = _root.FindLeafById(paneId);
        if (target?.SessionId == null) return;

        FocusedPaneId = paneId;

        if (_leaves.TryGetValue(paneId, out var state) && state.SurfaceId != 0)
            _engine.SurfaceFocus(state.SurfaceId);

        _sessions.ActivateSession(target.SessionId.Value);

        _messenger.Send(new PaneFocusChangedMessage(
            paneId, target.SessionId.Value));
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
        if (target.SessionId == null) return;

        FocusedPaneId = target.Id;

        if (_leaves.TryGetValue(target.Id, out var targetState) && targetState.SurfaceId != 0)
            _engine.SurfaceFocus(targetState.SurfaceId);

        _sessions.ActivateSession(target.SessionId.Value);

        _messenger.Send(new PaneFocusChangedMessage(
            target.Id, target.SessionId.Value));
    }

    public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
    {
        // #12 onhostready-enter — PaneLayoutService.OnHostReady 진입점.
        // H1 가설 검증: subscriber_count==0 일 때 이 진입점이 hit 되지 않으면 H1 confirmed.
        // hwnd=0 또는 paneId 가 _leaves 에 없으면 race condition 또는 stale event.
        // NOTE: RenderDiag 는 GhostWin.App 에 속하므로 GhostWin.Services 에서는
        //       직접 참조 불가. Trace.TraceInformation 으로 동일 정보를 남긴다.
        Trace.TraceInformation(
            $"[PaneLayoutService] OnHostReady: paneId={paneId} hwnd={hwnd} " +
            $"w={widthPx} h={heightPx} leaves_count={_leaves.Count}");

        // HC-2: silent return → Trace.TraceError 로 가시화 (D11 패턴 확장)
        if (!_leaves.TryGetValue(paneId, out var state))
        {
            Trace.TraceError(
                $"[PaneLayoutService] OnHostReady drop: paneId={paneId} not in _leaves " +
                $"(count={_leaves.Count}). Race condition or stale event?");
            return;
        }
        if (state.SurfaceId != 0) return; // Already created — silent OK (정상 경로)

        var leaf = FindLeaf(paneId);
        if (leaf?.SessionId == null)
        {
            // HC-2: 두 번째 silent return — leaf 가 없거나 SessionId 미설정
            Trace.TraceError(
                $"[PaneLayoutService] OnHostReady drop: leaf {paneId} has no SessionId. " +
                $"Tree corruption?");
            return;
        }

        var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);

        // #13 surfacecreate-return — SurfaceCreate 반환 직후.
        // surfaceId=0 이면 H2 (SurfaceCreate fail) 가설 evidence.
        // Trace.TraceInformation 으로 RenderDiag 와 동일 정보 병렬 기록.
        Trace.TraceInformation(
            $"[PaneLayoutService] SurfaceCreate return: paneId={paneId} surfaceId={surfaceId}");

        if (surfaceId == 0)
        {
            // SurfaceCreate failed. The pane will render nothing (active_surfaces
            // excludes this pane). There is no retry path — this is a terminal
            // failure for the pane. Log for diagnostics (Phase 5-E.5 P0-2 exposed
            // this silent-failure path, see bisect-mode-termination.design.md D11).
            Trace.TraceError(
                $"[PaneLayoutService] SurfaceCreate failed for pane {paneId} " +
                $"(session {leaf.SessionId.Value}, {widthPx}x{heightPx}). Pane will be blank.");
            return;
        }
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
