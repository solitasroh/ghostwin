using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.ViewModels;

public sealed record TerminalPaneLayoutSnapshot(
    uint WorkspaceId,
    uint? FocusedPaneId,
    TerminalPaneNodeViewModel Root);

public sealed class TerminalPaneLayoutViewModel : ObservableObject,
    IRecipient<PaneLayoutChangedMessage>,
    IRecipient<PaneFocusChangedMessage>,
    IRecipient<WorkspaceClosedMessage>,
    IRecipient<WorkspaceActivatedMessage>
{
    private readonly IWorkspaceService _workspaces;
    private readonly IMessenger _messenger;
    private bool _isRegistered;
    private uint? _closedWorkspaceId;
    private TerminalPaneLayoutSnapshot? _current;
    private TerminalPaneNodeViewModel? _root;

    public TerminalPaneLayoutViewModel(
        IWorkspaceService workspaces,
        IMessenger? messenger = null)
    {
        _workspaces = workspaces;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public TerminalPaneNodeViewModel? Root
    {
        get => _root;
        private set => SetProperty(ref _root, value);
    }

    public TerminalPaneLayoutSnapshot? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public uint? ClosedWorkspaceId
    {
        get => _closedWorkspaceId;
        private set => SetProperty(ref _closedWorkspaceId, value);
    }

    public void EnsureRegistered()
    {
        if (_isRegistered) return;
        _messenger.RegisterAll(this);
        _isRegistered = true;
    }

    public void RebuildFromActiveLayout()
    {
        RebuildFromActiveLayout(focusedPaneIdOverride: null);
    }

    public void Receive(PaneLayoutChangedMessage message)
    {
        RebuildFromActiveLayout();
    }

    public void Receive(PaneFocusChangedMessage message)
    {
        RebuildFromActiveLayout();
    }

    public void Receive(WorkspaceClosedMessage message)
    {
        PublishClosedWorkspaceId(message.Value);
        RebuildFromActiveLayout();
    }

    public void Receive(WorkspaceActivatedMessage message)
    {
        RebuildFromActiveLayout();
    }

    private void RebuildFromActiveLayout(uint? focusedPaneIdOverride)
    {
        var activeLayout = _workspaces.ActivePaneLayout;
        var root = activeLayout?.Root;
        var activeWorkspaceId = _workspaces.ActiveWorkspaceId;
        if (root == null || activeWorkspaceId == null)
        {
            Root = null;
            Current = null;
            return;
        }

        var focusedPaneId = focusedPaneIdOverride ?? activeLayout?.FocusedPaneId;
        var projected = TerminalPaneNodeViewModel.FromReadOnlyNode(root, focusedPaneId);
        Root = projected;
        Current = new TerminalPaneLayoutSnapshot(activeWorkspaceId.Value, focusedPaneId, projected);
    }

    private void PublishClosedWorkspaceId(uint workspaceId)
    {
        if (ClosedWorkspaceId == workspaceId)
            ClosedWorkspaceId = null;
        ClosedWorkspaceId = workspaceId;
    }
}
