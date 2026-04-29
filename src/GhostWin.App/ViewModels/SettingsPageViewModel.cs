using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private Timer? _saveDebounce;
    private bool _suppressSave;

    // ── Appearance ──
    [ObservableProperty] private string _appearance = "dark";
    [ObservableProperty] private bool _useMica = true;

    // ── Font ──
    [ObservableProperty] private string _fontFamily = "Cascadia Mono";
    [ObservableProperty] private double _fontSize = 14.0;
    [ObservableProperty] private double _cellWidthScale = 1.0;
    [ObservableProperty] private double _cellHeightScale = 1.0;

    // ── ScrollBar (M-16-C Phase B4) ──
    [ObservableProperty] private string _scrollbar = "system";
    public ObservableCollection<string> ScrollbarOptions { get; } =
        ["system", "always", "never"];

    // ── ContextMenu (M-16-D Phase C1) ──
    [ObservableProperty] private bool _forceContextMenu;

    // ── Sidebar ──
    [ObservableProperty] private bool _sidebarVisible = true;
    [ObservableProperty] private int _sidebarWidth = 200;
    [ObservableProperty] private bool _showCwd = true;
    [ObservableProperty] private bool _showGit = true;

    // ── Notifications ──
    [ObservableProperty] private bool _ringEnabled = true;
    [ObservableProperty] private bool _toastEnabled = true;
    [ObservableProperty] private bool _panelEnabled = true;
    [ObservableProperty] private bool _badgeEnabled = true;

    public ObservableCollection<string> AvailableFonts { get; } = [];
    public ObservableCollection<string> ThemeOptions { get; } = ["dark", "light"];

    public SettingsPageViewModel(ISettingsService settings)
    {
        _settings = settings;
        LoadFromSettings(settings.Current);
        LoadSystemFonts();
    }

    public void LoadFromSettings(AppSettings s)
    {
        _suppressSave = true;
        Appearance = s.Appearance;
        UseMica = s.Titlebar.UseMica;
        FontFamily = s.Terminal.Font.Family;
        FontSize = s.Terminal.Font.Size;
        CellWidthScale = s.Terminal.Font.CellWidthScale;
        CellHeightScale = s.Terminal.Font.CellHeightScale;
        Scrollbar = s.Terminal.Scrollbar;
        ForceContextMenu = s.Terminal.ForceContextMenu;
        SidebarVisible = s.Sidebar.Visible;
        SidebarWidth = s.Sidebar.Width;
        ShowCwd = s.Sidebar.ShowCwd;
        ShowGit = s.Sidebar.ShowGit;
        RingEnabled = s.Notifications.RingEnabled;
        ToastEnabled = s.Notifications.ToastEnabled;
        PanelEnabled = s.Notifications.PanelEnabled;
        BadgeEnabled = s.Notifications.BadgeEnabled;
        _suppressSave = false;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressSave)
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ =>
        {
            Application.Current?.Dispatcher.BeginInvoke(ApplyAndSave);
        }, null, 300, Timeout.Infinite);
    }

    private void ApplyAndSave()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[SettingsVM] ApplyAndSave sidebarWidth={SidebarWidth}");
        var s = _settings.Current;
        s.Appearance = Appearance;
        s.Titlebar.UseMica = UseMica;
        s.Terminal.Font.Family = FontFamily;
        s.Terminal.Font.Size = FontSize;
        s.Terminal.Font.CellWidthScale = CellWidthScale;
        s.Terminal.Font.CellHeightScale = CellHeightScale;
        s.Terminal.Scrollbar = Scrollbar;
        s.Terminal.ForceContextMenu = ForceContextMenu;
        s.Sidebar.Visible = SidebarVisible;
        s.Sidebar.Width = SidebarWidth;
        s.Sidebar.ShowCwd = ShowCwd;
        s.Sidebar.ShowGit = ShowGit;
        s.Notifications.RingEnabled = RingEnabled;
        s.Notifications.ToastEnabled = ToastEnabled;
        s.Notifications.PanelEnabled = PanelEnabled;
        s.Notifications.BadgeEnabled = BadgeEnabled;
        _settings.Save();

        // Save 후 직접 SettingsChangedMessage 발사.
        // _suppressWatcher가 FileWatcher를 막으므로, GUI 변경은
        // FileWatcher 경로가 아닌 이 직접 발사로 반영됨.
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(s));
    }

    [RelayCommand]
    private void OpenJson()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                _settings.SettingsFilePath) { UseShellExecute = true });
        }
        catch { }
    }

    private void LoadSystemFonts()
    {
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
            AvailableFonts.Add(family.Source);
    }
}
