using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.ViewModels;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Automation;

public sealed class TestControlHandler
{
    private readonly ISessionManager _sessions;
    private readonly IWorkspaceService _workspaces;
    private readonly ISettingsService? _settings;
    private readonly IOscNotificationService? _notifications;
    private readonly MainWindowViewModel? _mainWindow;
    private long _stateVersion;

    public TestControlHandler(
        ISessionManager sessions,
        IWorkspaceService workspaces,
        ISettingsService? settings = null,
        IOscNotificationService? notifications = null,
        MainWindowViewModel? mainWindow = null)
    {
        _sessions = sessions;
        _workspaces = workspaces;
        _settings = settings;
        _notifications = notifications;
        _mainWindow = mainWindow;
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
                "inject-notification" => InjectNotification(request),
                "set-settings" => SetSettings(request),
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
            case "open-settings":
                if (!TryGetMainWindow(request, out var openVm, out var openFailure))
                    return openFailure;
                openVm.OpenSettingsCommand.Execute(null);
                break;
            case "close-settings":
                if (!TryGetMainWindow(request, out var closeVm, out var closeSettingsFailure))
                    return closeSettingsFailure;
                closeVm.CloseSettingsCommand.Execute(null);
                break;
            case "toggle-notification-panel":
                if (!TryGetMainWindow(request, out var panelVm, out var panelFailure))
                    return panelFailure;
                panelVm.ToggleNotificationPanelCommand.Execute(null);
                break;
            case "mark-all-read":
                if (!TryGetNotifications(request, out var notifications, out var notificationsFailure))
                    return notificationsFailure;
                notifications.MarkAllAsRead();
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

    private TestControlResponse SetSettings(TestControlRequest request)
    {
        if (_settings is null)
        {
            return TestControlResponse.Failure(
                _stateVersion,
                "settings service is required",
                request.RequestId);
        }

        var settingName = request.Data?.SettingName;
        var value = request.Data?.Value;
        if (string.IsNullOrWhiteSpace(settingName))
            return TestControlResponse.Failure(_stateVersion, "setting_name is required", request.RequestId);
        if (value is null)
            return TestControlResponse.Failure(_stateVersion, "value is required", request.RequestId);

        var settings = _settings.Current;
        switch (settingName.ToLowerInvariant())
        {
            case "appearance":
                settings.Appearance = value;
                break;
            case "sidebar-visible":
                settings.Sidebar.Visible = ParseBool(value, settingName);
                break;
            case "sidebar-width":
                settings.Sidebar.Width = int.Parse(value);
                break;
            case "show-cwd":
                settings.Sidebar.ShowCwd = ParseBool(value, settingName);
                break;
            case "force-context-menu":
                settings.Terminal.ForceContextMenu = ParseBool(value, settingName);
                break;
            default:
                return TestControlResponse.Failure(
                    _stateVersion,
                    $"unsupported setting: {settingName}",
                    request.RequestId);
        }

        _settings.Save();
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(settings));
        _stateVersion++;
        return TestControlResponse.Success(_stateVersion, Snapshot(), request.RequestId);
    }

    private static bool ParseBool(string value, string settingName)
    {
        if (bool.TryParse(value, out var result))
            return result;

        throw new ArgumentException($"{settingName} value must be true or false");
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

    private bool TryGetMainWindow(
        TestControlRequest request,
        out MainWindowViewModel mainWindow,
        out TestControlResponse failure)
    {
        if (_mainWindow is { } vm)
        {
            mainWindow = vm;
            failure = TestControlResponse.Success(_stateVersion);
            return true;
        }

        mainWindow = null!;
        failure = TestControlResponse.Failure(
            _stateVersion,
            "main window view model is required",
            request.RequestId);
        return false;
    }

    private bool TryGetNotifications(
        TestControlRequest request,
        out IOscNotificationService notifications,
        out TestControlResponse failure)
    {
        if (_notifications is { } service)
        {
            notifications = service;
            failure = TestControlResponse.Success(_stateVersion);
            return true;
        }

        notifications = null!;
        failure = TestControlResponse.Failure(
            _stateVersion,
            "notification service is required",
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

    private TestControlResponse InjectNotification(TestControlRequest request)
    {
        if (!TryGetNotifications(request, out var notifications, out var failure))
            return failure;

        var message = request.Data?.Message;
        if (message == null)
            return TestControlResponse.Failure(_stateVersion, "message is required", request.RequestId);

        var sessionId = request.SessionId ?? _sessions.ActiveSessionId;
        if (sessionId is not { } targetSessionId)
            return TestControlResponse.Failure(_stateVersion, "target session is required", request.RequestId);

        notifications.HandleOscEvent(targetSessionId, request.Data?.Osc ?? "GhostWin", message);
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
            PaneCount: paneLayout?.LeafCount ?? 0,
            IsSettingsOpen: _mainWindow?.IsSettingsOpen ?? false,
            IsNotificationPanelOpen: _mainWindow?.IsNotificationPanelOpen ?? false,
            NotificationCount: _notifications?.Notifications.Count ?? 0,
            UnreadNotificationCount: _notifications?.UnreadCount ?? 0,
            Appearance: _settings?.Current.Appearance ?? "dark",
            SidebarVisible: _settings?.Current.Sidebar.Visible ?? true,
            SidebarWidth: _settings?.Current.Sidebar.Width ?? 200,
            ShowCwd: _settings?.Current.Sidebar.ShowCwd ?? true,
            ForceContextMenu: _settings?.Current.Terminal.ForceContextMenu ?? false);
    }
}
