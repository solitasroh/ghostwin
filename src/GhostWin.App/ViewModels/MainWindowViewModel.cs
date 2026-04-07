using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Wpf.Ui.Appearance;

namespace GhostWin.App.ViewModels;

public partial class MainWindowViewModel : ObservableRecipient,
    IRecipient<WorkspaceCreatedMessage>,
    IRecipient<WorkspaceClosedMessage>,
    IRecipient<WorkspaceActivatedMessage>,
    IRecipient<SettingsChangedMessage>
{
    private readonly IWorkspaceService _workspaceService;
    private readonly ISettingsService _settingsService;

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    private WorkspaceItemViewModel? _selectedWorkspace;

    [ObservableProperty]
    private string _windowTitle = "GhostWin";

    [ObservableProperty]
    private int _sidebarWidth = 200;

    [ObservableProperty]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _showCwd = true;

    public MainWindowViewModel(
        IWorkspaceService workspaceService,
        ISettingsService settingsService)
    {
        _workspaceService = workspaceService;
        _settingsService = settingsService;
        IsActive = true;

        ApplySettings(_settingsService.Current);
    }

    [RelayCommand]
    private void NewWorkspace()
    {
        _workspaceService.CreateWorkspace();
    }

    [RelayCommand]
    private void CloseWorkspace(WorkspaceItemViewModel? workspace)
    {
        var target = workspace ?? SelectedWorkspace;
        if (target == null) return;
        _workspaceService.CloseWorkspace(target.WorkspaceId);
    }

    [RelayCommand]
    private void NextWorkspace()
    {
        if (Workspaces.Count == 0) return;
        var idx = Workspaces.IndexOf(SelectedWorkspace!);
        SelectedWorkspace = Workspaces[(idx + 1) % Workspaces.Count];
    }

    [RelayCommand]
    private void SplitVertical() =>
        _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical);

    [RelayCommand]
    private void SplitHorizontal() =>
        _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Horizontal);

    [RelayCommand]
    private void ClosePane() =>
        _workspaceService.ActivePaneLayout?.CloseFocused();

    partial void OnSelectedWorkspaceChanged(WorkspaceItemViewModel? value)
    {
        if (value == null) return;
        if (_workspaceService.ActiveWorkspaceId != value.WorkspaceId)
            _workspaceService.ActivateWorkspace(value.WorkspaceId);
    }

    public void Receive(WorkspaceCreatedMessage msg)
    {
        var workspace = _workspaceService.Workspaces
            .FirstOrDefault(w => w.Id == msg.Value);
        if (workspace == null) return;

        var vm = new WorkspaceItemViewModel(workspace);
        Workspaces.Add(vm);
        SelectedWorkspace = vm;
    }

    public void Receive(WorkspaceClosedMessage msg)
    {
        var vm = Workspaces.FirstOrDefault(w => w.WorkspaceId == msg.Value);
        if (vm == null) return;

        Workspaces.Remove(vm);
        vm.Dispose();

        if (Workspaces.Count == 0)
            Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    public void Receive(WorkspaceActivatedMessage msg)
    {
        // Sync sidebar selection if changed externally (e.g. via CloseWorkspace
        // promoting another workspace).
        var vm = Workspaces.FirstOrDefault(w => w.WorkspaceId == msg.Value);
        if (vm != null && SelectedWorkspace != vm)
            SelectedWorkspace = vm;
    }

    public void Receive(SettingsChangedMessage msg)
    {
        ApplySettings(msg.Value);
    }

    private void ApplySettings(AppSettings settings)
    {
        SidebarWidth = settings.Sidebar.Width;
        SidebarVisible = settings.Sidebar.Visible;
        ShowCwd = settings.Sidebar.ShowCwd;

        var theme = settings.Appearance switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme);
    }
}
