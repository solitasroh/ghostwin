namespace GhostWin.Core.Models;

public class SessionInfo
{
    public uint Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public string Cwd { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
