using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.Diagnostics;
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
    private readonly IOscNotificationService _oscService;

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

    [ObservableProperty]
    private bool _isNotificationPanelOpen;

    [ObservableProperty]
    private int _notificationPanelWidth;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private SettingsPageViewModel? _settingsPageVM;

    public ObservableCollection<NotificationEntry> Notifications => _oscService.Notifications;
    public int UnreadCount => _oscService.UnreadCount;
    public bool HasUnread => _oscService.UnreadCount > 0;
    public bool HasNotifications => _oscService.Notifications.Count > 0;

    public MainWindowViewModel(
        IWorkspaceService workspaceService,
        ISettingsService settingsService,
        IOscNotificationService oscService)
    {
        _workspaceService = workspaceService;
        _settingsService = settingsService;
        _oscService = oscService;
        IsActive = true;

        if (oscService is INotifyPropertyChanged npc)
            npc.PropertyChanged += OnOscServicePropertyChanged;
        _oscService.Notifications.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasNotifications));

        ApplySettings(_settingsService.Current);
    }

    private void OnOscServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IOscNotificationService.UnreadCount))
        {
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(HasUnread));
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsPageVM == null)
            SettingsPageVM = new SettingsPageViewModel(_settingsService);
        else
            SettingsPageVM.LoadFromSettings(_settingsService.Current);
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        // 포커스 복원: MainWindow.xaml.cs의 PropertyChanged 구독에서 처리
    }

    [RelayCommand]
    private void ToggleNotificationPanel()
    {
        IsNotificationPanelOpen = !IsNotificationPanelOpen;
        NotificationPanelWidth = IsNotificationPanelOpen ? 280 : 0;
    }

    [RelayCommand]
    private void NotificationClick(NotificationEntry? entry)
    {
        if (entry is null) return;
        _oscService.MarkAsRead(entry);
        var ws = _workspaceService.FindWorkspaceBySessionId(entry.SessionId);
        if (ws != null)
            _workspaceService.ActivateWorkspace(ws.Id);
        _oscService.DismissAttention(entry.SessionId);
    }

    [RelayCommand]
    private void JumpToUnread()
    {
        var entry = _oscService.GetMostRecentUnread();
        if (entry == null) return;
        NotificationClick(entry);
    }

    [RelayCommand]
    private void MarkAllRead()
    {
        _oscService.MarkAllAsRead();
    }

    [RelayCommand]
    private void NewWorkspace()
    {
        KeyDiag.LogKeyBindCommand("NewWorkspace");
        _workspaceService.CreateWorkspace();
    }

    [RelayCommand]
    private void CloseWorkspace(WorkspaceItemViewModel? workspace)
    {
        KeyDiag.LogKeyBindCommand("CloseWorkspace");
        var target = workspace ?? SelectedWorkspace;
        if (target == null) return;
        _workspaceService.CloseWorkspace(target.WorkspaceId);
    }

    [RelayCommand]
    private void NextWorkspace()
    {
        KeyDiag.LogKeyBindCommand("NextWorkspace");
        if (Workspaces.Count == 0) return;
        var idx = Workspaces.IndexOf(SelectedWorkspace!);
        SelectedWorkspace = Workspaces[(idx + 1) % Workspaces.Count];
    }

    [RelayCommand]
    private void SplitVertical()
    {
        KeyDiag.LogKeyBindCommand("SplitVertical");
        _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical);
    }

    [RelayCommand]
    private void SplitHorizontal()
    {
        KeyDiag.LogKeyBindCommand("SplitHorizontal");
        _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Horizontal);
    }

    [RelayCommand]
    private void ClosePane()
    {
        KeyDiag.LogKeyBindCommand("ClosePane");
        _workspaceService.ActivePaneLayout?.CloseFocused();
    }

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
            Application.Current.Dispatcher.BeginInvoke(() =>
                Application.Current.MainWindow?.Close());
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
        SettingsPageVM?.LoadFromSettings(msg.Value);
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

        // M-12: Apply theme colors to MainWindow resources
        ApplyThemeColors(settings.Appearance == "light");
    }

    private static void ApplyThemeColors(bool isLight)
    {
        var window = Application.Current?.MainWindow;
        if (window == null) return;

        if (isLight)
        {
            window.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5));
            SetBrush(window, "TitleBarBg", 0xF0, 0xF0, 0xF0);
            SetBrush(window, "SidebarBg", 0xE8, 0xE8, 0xE8);
            SetBrush(window, "SidebarHover", 0x00, 0x00, 0x00);  // black with opacity
            SetBrush(window, "SidebarSelected", 0x00, 0x00, 0x00);
            SetBrush(window, "PrimaryText", 0x1C, 0x1C, 0x1E);
            SetBrush(window, "SecondaryText", 0x63, 0x63, 0x66);
            SetBrush(window, "TertiaryText", 0x8E, 0x8E, 0x93);
            SetBrush(window, "DividerColor", 0xD1, 0xD1, 0xD6);
            SetBrush(window, "TerminalBg", 0xFB, 0xFB, 0xFB);
            SetBrush(window, "ButtonHover", 0xDC, 0xDC, 0xE0);
            // Settings page resources
            SetBrush(window, "ApplicationBackgroundBrush", 0xF5, 0xF5, 0xF5);
            SetBrush(window, "CardBackgroundBrush", 0xE8, 0xE8, 0xE8);
            SetBrush(window, "PrimaryTextBrush", 0x1C, 0x1C, 0x1E);
            SetBrush(window, "SecondaryTextBrush", 0x63, 0x63, 0x66);
        }
        else
        {
            window.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0A, 0x0A, 0x0A));
            SetBrush(window, "TitleBarBg", 0x0A, 0x0A, 0x0A);
            SetBrush(window, "SidebarBg", 0x14, 0x14, 0x14);
            SetBrush(window, "PrimaryText", 0xFF, 0xFF, 0xFF);
            SetBrush(window, "SecondaryText", 0x8E, 0x8E, 0x93);
            SetBrush(window, "TertiaryText", 0x63, 0x63, 0x66);
            SetBrush(window, "DividerColor", 0x3A, 0x3A, 0x3C);
            SetBrush(window, "TerminalBg", 0x1E, 0x1E, 0x2E);
            SetBrush(window, "ButtonHover", 0x3E, 0x3E, 0x42);
            SetBrush(window, "ApplicationBackgroundBrush", 0x1A, 0x1A, 0x1A);
            SetBrush(window, "CardBackgroundBrush", 0x2C, 0x2C, 0x2E);
            SetBrush(window, "PrimaryTextBrush", 0xFF, 0xFF, 0xFF);
            SetBrush(window, "SecondaryTextBrush", 0x8E, 0x8E, 0x93);
        }
    }

    private static void SetBrush(Window window, string key, byte r, byte g, byte b)
    {
        window.Resources[key] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
    }
}
