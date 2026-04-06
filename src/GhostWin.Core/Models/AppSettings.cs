namespace GhostWin.Core.Models;

public sealed class AppSettings
{
    public string Appearance { get; set; } = "dark";
    public SidebarSettings Sidebar { get; set; } = new();
    public TitlebarSettings Titlebar { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
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
    public bool ShowSessionInfo { get; set; } = true;
    public bool UseMica { get; set; } = true;
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1024;
    public double Height { get; set; } = 768;
    public double Top { get; set; } = double.NaN;
    public double Left { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
}
