namespace GhostWin.Core.Models;

public sealed class AppSettings
{
    public string Appearance { get; set; } = "dark";
    public SidebarSettings Sidebar { get; set; } = new();
    public TitlebarSettings Titlebar { get; set; } = new();
    public Dictionary<string, string> Keybindings { get; set; } = new();
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
    public bool ShowSessionTitle { get; set; } = true;
}
