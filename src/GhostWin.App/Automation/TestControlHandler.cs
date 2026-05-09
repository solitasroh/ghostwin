using System.Text;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Automation;

public sealed class TestControlHandler
{
    private readonly ISessionManager _sessions;
    private readonly IWorkspaceService _workspaces;
    private long _stateVersion;

    public TestControlHandler(ISessionManager sessions, IWorkspaceService workspaces)
    {
        _sessions = sessions;
        _workspaces = workspaces;
    }

    public TestControlResponse Handle(TestControlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            return TestControlResponse.Failure(_stateVersion, "command is required", request.RequestId);

        try
        {
            return request.Command.ToLowerInvariant() switch
            {
                "wait-for-ready" => GetState(request),
                "get-state" => GetState(request),
                "execute-command" => ExecuteCommand(request),
                "inject-osc" => InjectOsc(request),
                _ => TestControlResponse.Failure(
                    _stateVersion,
                    $"unsupported test-control command: {request.Command}",
                    request.RequestId)
            };
        }
        catch (Exception ex)
        {
            return TestControlResponse.Failure(_stateVersion, ex.Message, request.RequestId);
        }
    }

    private TestControlResponse GetState(TestControlRequest request)
        => TestControlResponse.Success(_stateVersion, Snapshot(), request.RequestId);

    private TestControlResponse ExecuteCommand(TestControlRequest request)
    {
        var commandName = request.Data?.CommandName;
        if (string.IsNullOrWhiteSpace(commandName))
            return TestControlResponse.Failure(_stateVersion, "command_name is required", request.RequestId);

        switch (commandName.ToLowerInvariant())
        {
            case "new-workspace":
                _workspaces.CreateWorkspace();
                break;
            case "split-vertical":
                if (!TryGetActivePaneLayout(request, out var verticalPaneLayout, out var verticalFailure))
                    return verticalFailure;
                verticalPaneLayout.SplitFocused(SplitOrientation.Vertical);
                break;
            case "split-horizontal":
                if (!TryGetActivePaneLayout(request, out var horizontalPaneLayout, out var horizontalFailure))
                    return horizontalFailure;
                horizontalPaneLayout.SplitFocused(SplitOrientation.Horizontal);
                break;
            case "close-pane":
                if (!TryGetActivePaneLayout(request, out var closePaneLayout, out var closeFailure))
                    return closeFailure;
                closePaneLayout.CloseFocused();
                break;
            default:
                return TestControlResponse.Failure(
                    _stateVersion,
                    $"unsupported execute-command: {commandName}",
                    request.RequestId);
        }

        _stateVersion++;
        return TestControlResponse.Success(_stateVersion, Snapshot(), request.RequestId);
    }

    private bool TryGetActivePaneLayout(
        TestControlRequest request,
        out IPaneLayoutService paneLayout,
        out TestControlResponse failure)
    {
        if (_workspaces.ActivePaneLayout is { } activePaneLayout)
        {
            paneLayout = activePaneLayout;
            failure = TestControlResponse.Success(_stateVersion);
            return true;
        }

        paneLayout = null!;
        failure = TestControlResponse.Failure(
            _stateVersion,
            "active pane layout is required",
            request.RequestId);
        return false;
    }

    private TestControlResponse InjectOsc(TestControlRequest request)
    {
        var osc = request.Data?.Osc;
        var message = request.Data?.Message;
        if (string.IsNullOrWhiteSpace(osc))
            return TestControlResponse.Failure(_stateVersion, "osc is required", request.RequestId);
        if (message == null)
            return TestControlResponse.Failure(_stateVersion, "message is required", request.RequestId);

        var sessionId = request.SessionId ?? _sessions.ActiveSessionId;
        if (sessionId is not { } targetSessionId)
            return TestControlResponse.Failure(_stateVersion, "target session is required", request.RequestId);

        var sequence = $"\x1b]{osc};{message}\x1b\\";
#pragma warning disable CS0618
        _sessions.TestOnlyInjectBytes(targetSessionId, Encoding.UTF8.GetBytes(sequence));
#pragma warning restore CS0618
        _stateVersion++;
        return TestControlResponse.Success(_stateVersion, Snapshot(), request.RequestId);
    }

    private TestControlState Snapshot()
    {
        var paneLayout = _workspaces.ActivePaneLayout;
        return new TestControlState(
            ActiveSessionId: _sessions.ActiveSessionId,
            ActiveWorkspaceId: _workspaces.ActiveWorkspaceId,
            FocusedPaneId: paneLayout?.FocusedPaneId,
            FocusedSessionId: paneLayout?.FocusedSessionId,
            SessionCount: _sessions.Sessions.Count,
            WorkspaceCount: _workspaces.Workspaces.Count,
            PaneCount: paneLayout?.LeafCount ?? 0);
    }
}
