using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    IRecipient<WorkspaceReorderedMessage>,
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

        // P2 (2026-04-29): IsActive=true triggers ObservableRecipient.OnActivated
        // which already calls Messenger.RegisterAll(this) for every IRecipient<T>
        // implementation. An earlier defensive explicit RegisterAll(this) here
        // caused a duplicate-registration InvalidOperationException at startup
        // (commit 6bda85f, fixed by this revert). Leaving the diagnostic log
        // so the user-PC trace still shows the Messenger type and IsActive
        // value when verifying 4-5 sync.
        System.Diagnostics.Debug.WriteLine(
            $"[MainVM] ctor IsActive={IsActive} Messenger={Messenger.GetType().Name}");

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

    // M-16-B FR-09/10 (Day 5): toggling only sets IsNotificationPanelOpen.
    // MainWindow.xaml.cs subscribes to its PropertyChanged and animates the
    // NotificationPanelColumn.Width via GridLengthAnimationCustom (200ms
    // CubicEase EaseOut). NotificationPanelWidth is kept in sync by the
    // GridSplitter DragCompleted handler so the slider stays meaningful
    // when the panel is open.
    [RelayCommand]
    private void ToggleNotificationPanel()
    {
        IsNotificationPanelOpen = !IsNotificationPanelOpen;
        if (IsNotificationPanelOpen && NotificationPanelWidth <= 0)
            NotificationPanelWidth = 280;
        else if (!IsNotificationPanelOpen)
            NotificationPanelWidth = 0;
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

    // M-16-D D-07: notification ContextMenu commands.
    [RelayCommand]
    private void MarkNotificationRead(NotificationEntry? entry)
    {
        if (entry is null) return;
        _oscService.MarkAsRead(entry);
    }

    [RelayCommand]
    private void DismissNotification(NotificationEntry? entry)
    {
        if (entry is null) return;
        _oscService.DismissAttention(entry.SessionId);
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

    public void Receive(WorkspaceReorderedMessage msg)
    {
        // M-16-D D-08: WorkspaceService rearranged the underlying ordered list.
        // Mirror the move into the ObservableCollection so the bound ListBox
        // animates in place. We resolve the moved item by id, then call Move
        // (preserves the existing WorkspaceItemViewModel instance — no rebind,
        // selection survives).
        var vm = Workspaces.FirstOrDefault(w => w.WorkspaceId == msg.WorkspaceId);
        if (vm == null) return;
        int oldIdx = Workspaces.IndexOf(vm);
        if (oldIdx < 0) return;
        int newIdx = System.Math.Clamp(msg.NewIndex, 0, Workspaces.Count - 1);
        if (oldIdx == newIdx) return;
        Workspaces.Move(oldIdx, newIdx);
    }

    public void Receive(SettingsChangedMessage msg)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MainVM] Receive(SettingsChangedMessage) sidebarWidth={msg.Value.Sidebar.Width}");
        ApplySettings(msg.Value);
        SettingsPageVM?.LoadFromSettings(msg.Value);

        // M-16-A FR-06 / Plan R5: theme swap changed Application.Resources;
        // ask each WorkspaceItemViewModel to re-resolve its agent badge brush
        // so existing bindings reflect the new dictionary.
        foreach (var ws in Workspaces)
            ws.OnThemeChanged();
    }

    // M-16-B FR-07/08 (Day 4): suppress the partial OnSidebarWidthChanged
    // persistence path while reloading from disk. Without this guard, a
    // SettingsChangedMessage that only changed (say) Appearance would also
    // trigger a redundant settings.Save() because SidebarWidth setter fires
    // even when the value is unchanged on the WPF DependencyProperty path.
    private bool _suppressSettingsPersist;

    private void ApplySettings(AppSettings settings)
    {
        _suppressSettingsPersist = true;
        try
        {
            SidebarWidth = settings.Sidebar.Width;
            SidebarVisible = settings.Sidebar.Visible;
            ShowCwd = settings.Sidebar.ShowCwd;
        }
        finally
        {
            _suppressSettingsPersist = false;
        }

        var theme = settings.Appearance switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme);

        ApplyThemeColors(settings.Appearance == "light");
    }

    // M-16-B FR-06/07 (Day 4): persist Sidebar width when GridSplitter drags
    // change the value. The DragCompleted handler in MainWindow.xaml.cs writes
    // to SidebarWidth which triggers this partial. _suppressSettingsPersist
    // guards against re-entry from ApplySettings(). SettingsService.Save()
    // already self-suppresses its FileWatcher for 100ms (M-12 pattern), so the
    // SettingsPageVM's slider receives the next SettingsChangedMessage without
    // an infinite loop.
    partial void OnSidebarWidthChanged(int value)
    {
        if (_suppressSettingsPersist) return;
        _settingsService.Current.Sidebar.Width = value;
        _settingsService.Save();
    }

    /// <summary>
    /// M-16-A FR-07 (C10 fix): swap the Themes/Colors.*.xaml MergedDictionary
    /// instead of imperatively rewriting per-key brushes. Insert the new
    /// dictionary at index 0 first, then remove the old, so DynamicResource
    /// consumers never observe an empty resolution window.
    /// </summary>
    private static void ApplyThemeColors(bool isLight)
    {
        var app = Application.Current;
        if (app == null) return;

        var newDict = new ResourceDictionary
        {
            Source = new Uri(
                isLight
                    ? "/GhostWin.App;component/Themes/Colors.Light.xaml"
                    : "/GhostWin.App;component/Themes/Colors.Dark.xaml",
                UriKind.RelativeOrAbsolute),
        };

        var oldDicts = app.Resources.MergedDictionaries
            .Where(d => d.Source != null
                && d.Source.OriginalString.IndexOf("Themes/Colors.",
                    StringComparison.Ordinal) >= 0)
            .ToList();

        app.Resources.MergedDictionaries.Insert(0, newDict);
        foreach (var old in oldDicts)
            app.Resources.MergedDictionaries.Remove(old);
    }
}
