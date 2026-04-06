using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.ViewModels;

public partial class MainWindowViewModel : ObservableRecipient,
    IRecipient<SessionCreatedMessage>,
    IRecipient<SessionClosedMessage>,
    IRecipient<SessionTitleChangedMessage>,
    IRecipient<SessionCwdChangedMessage>
{
    private readonly ISessionManager _sessionManager;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private TerminalTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _windowTitle = "GhostWin";

    public MainWindowViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        IsActive = true;
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

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        if (value != null)
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
            Application.Current.Shutdown();
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
        // SessionInfo.Cwd는 SessionManager에서 이미 업데이트됨
        // ObservableObject 바인딩으로 사이드바 자동 갱신
    }
}
