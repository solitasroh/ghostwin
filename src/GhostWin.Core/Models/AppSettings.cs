namespace GhostWin.Core.Models;

public class AppSettings
{
    public AppAppearance App { get; set; } = new();
    public SidebarSettings Sidebar { get; set; } = new();
    public Dictionary<string, string> Keybindings { get; set; } = new();
}

public class AppAppearance
{
    public string Appearance { get; set; } = "dark";
}

public class SidebarSettings
{
    public bool Visible { get; set; } = true;
    public int Width { get; set; } = 250;
    public bool ShowCwd { get; set; } = true;
    public bool ShowGit { get; set; } = true;
    public bool HideAllDetails { get; set; }
}
