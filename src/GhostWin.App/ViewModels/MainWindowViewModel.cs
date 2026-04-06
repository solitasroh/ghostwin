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
    IRecipient<SessionCreatedMessage>,
    IRecipient<SessionClosedMessage>,
    IRecipient<SessionTitleChangedMessage>,
    IRecipient<SessionCwdChangedMessage>,
    IRecipient<SettingsChangedMessage>
{
    private readonly ISessionManager _sessionManager;
    private readonly ISettingsService _settingsService;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private TerminalTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _windowTitle = "GhostWin";

    [ObservableProperty]
    private int _sidebarWidth = 200;

    [ObservableProperty]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _showCwd = true;

    public MainWindowViewModel(ISessionManager sessionManager, ISettingsService settingsService)
    {
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        IsActive = true;

        ApplySettings(_settingsService.Current);
    }

    [RelayCommand]
    private void NewTab()
    {
        _sessionManager.CreateSession();
    }

    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab == null) return;
        _sessionManager.CloseSession(tab.SessionId);
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count == 0) return;
        var idx = Tabs.IndexOf(SelectedTab!);
        SelectedTab = Tabs[(idx + 1) % Tabs.Count];
        _sessionManager.ActivateSession(SelectedTab.SessionId);
    }

    // Pane split commands (Phase 5-E) — actual logic in MainWindow.xaml.cs
    [RelayCommand]
    private void SplitVertical() => SplitRequested?.Invoke(SplitOrientation.Vertical);

    [RelayCommand]
    private void SplitHorizontal() => SplitRequested?.Invoke(SplitOrientation.Horizontal);

    [RelayCommand]
    private void ClosePane() => ClosePaneRequested?.Invoke();

    public event Action<SplitOrientation>? SplitRequested;
    public event Action? ClosePaneRequested;

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        if (value != null && _sessionManager.ActiveSessionId != value.SessionId)
            _sessionManager.ActivateSession(value.SessionId);
    }

    public void Receive(SessionCreatedMessage msg)
    {
        var session = _sessionManager.Sessions
            .FirstOrDefault(s => s.Id == msg.Value);
        if (session == null) return;

        var tab = new TerminalTabViewModel(session);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void Receive(SessionClosedMessage msg)
    {
        var tab = Tabs.FirstOrDefault(t => t.SessionId == msg.Value);
        if (tab == null) return;
        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count == 0)
            Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        else
            SelectedTab = Tabs[^1];
    }

    public void Receive(SessionTitleChangedMessage msg)
    {
        if (SelectedTab?.SessionId == msg.Value.Id)
            WindowTitle = $"GhostWin — {msg.Value.Title}";
    }

    public void Receive(SessionCwdChangedMessage msg)
    {
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
