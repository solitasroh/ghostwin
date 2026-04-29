namespace GhostWin.Core.Models;

public sealed class AppSettings
{
    public string Appearance { get; set; } = "dark";
    public SidebarSettings Sidebar { get; set; } = new();
    public TitlebarSettings Titlebar { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public Dictionary<string, string> Keybindings { get; set; } = new();
}

/// <summary>
/// Terminal-related settings (font, cell metrics).
/// M-12 Settings UI binds to these fields; the engine pipeline uses
/// <c>IEngineService.UpdateCellMetrics</c> to apply changes at runtime.
/// </summary>
public sealed class TerminalSettings
{
    public FontSettings Font { get; set; } = new();
    /// <summary>
    /// M-16-C Phase B4: per-pane ScrollBar visibility policy.
    /// "system" — visible only when there is scrollback above the viewport (default).
    /// "always" — always visible (matches VS Code "vertical": "auto" + persistent track).
    /// "never"  — hidden; users rely on wheel/keyboard for scrollback.
    /// </summary>
    public string Scrollbar { get; set; } = "system";
}

public sealed class FontSettings
{
    public double Size { get; set; } = 14.0;
    public string Family { get; set; } = "Cascadia Mono";
    public double CellWidthScale { get; set; } = 1.0;
    public double CellHeightScale { get; set; } = 1.0;
}

public sealed class SidebarSettings
{
    public bool Visible { get; set; } = true;
    public int Width { get; set; } = 200;
    public bool ShowCwd { get; set; } = true;
    public bool ShowGit { get; set; } = true;
    public bool HideAllDetails { get; set; }
}

public sealed class TitlebarSettings
{
    public bool ShowSessionInfo { get; set; } = true;
    public bool UseMica { get; set; } = true;
}

public sealed class NotificationSettings
{
    public bool RingEnabled { get; set; } = true;
    public bool ToastEnabled { get; set; } = true;
    public bool PanelEnabled { get; set; } = true;
    public bool BadgeEnabled { get; set; } = true;
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1024;
    public double Height { get; set; } = 768;
    public double Top { get; set; } = double.NaN;
    public double Left { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
}
